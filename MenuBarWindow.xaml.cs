using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using ManagedShell.Common.Helpers;
using ManagedShell.Interop;
using ManagedShell.WindowsTray;
using static Mindows.Native;

namespace Mindows;

public partial class MenuBarWindow : Window
{
    const double BarHeight = 28;
    static readonly CultureInfo Korean = new("ko-KR");

    readonly WinEventProc foregroundHook; // held so the GC keeps the native callback alive
    IntPtr foreground;
    AppInfo? foregroundApp;

    readonly Action openSettings;
    readonly Config config;
    readonly DockWindow dock;
    readonly NotificationArea? tray;
    NotifyIcon? movingIcon; // tray icon being Ctrl+dragged to a new spot

    // Quick Settings and Search open over Explorer's hidden taskbar at the bottom; while one is open, keep Quick Settings
    // under the menu bar and Search, opened from the dock, just above the dock.
    const double FlyoutGap = 6;
    readonly WinEventProc shownHook;
    readonly DispatcherTimer flyoutTimer = new();
    readonly Dictionary<IntPtr, int> flyoutEdges = new(); // last panel edge found in each flyout window: Search's bottom, else the top
    IntPtr flyout;
    int? parkedY;  // the shell's own y for a flyout parked above the screen until its panel is found
    long parkedAt;
    IntPtr search; // Search's window, kept above the dock while closed

    static MenuBarWindow()
    {
        var order = new[] { "new", "a", "hidden", "B" };
        Debug.Assert(string.Join(",", MoveBefore(order, "b", "a")) == "new,b,a,hidden"
            && string.Join(",", MoveBefore(order, "a", null)) == "new,hidden,B,a",
            "a moved tray icon must land before its new neighbor without moving icons that aren't shown");
    }

    public MenuBarWindow(NotificationArea? tray, Config config, DockWindow dock, Action openSettings)
    {
        (this.tray, this.config, this.dock, this.openSettings) = (tray, config, dock, openSettings);
        InitializeComponent();
        _ = new AppBar(this, AppBar.Top, () => BarHeight, () => BarHeight);
        if (tray != null) TrayIcons.ItemsSource = TrayView(tray);

        foregroundHook = (_, _, h, idObject, _, _, _) => { if (idObject == 0) OnForeground(h); };
        SetWinEventHook(3, 3, IntPtr.Zero, foregroundHook, 0, 0, 0); // EVENT_SYSTEM_FOREGROUND, out of context
        OnForeground(GetForegroundWindow());
        // Catch flyouts as they appear, before they take the foreground, so they don't show at the bottom first.
        shownHook = (_, _, h, idObject, _, _, _) => { if (idObject == 0) WatchFlyout(h); };
        SetWinEventHook(0x8002, 0x8002, IntPtr.Zero, shownHook, 0, 0, 0); // EVENT_OBJECT_SHOW: Quick Settings
        SetWinEventHook(0x8018, 0x8018, IntPtr.Zero, shownHook, 0, 0, 0); // EVENT_OBJECT_UNCLOAKED: Search
        flyoutTimer.Tick += (_, _) => PlaceFlyout();

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => Tick();
        timer.Start();
        Tick();
    }

    void OnForeground(IntPtr h)
    {
        WatchFlyout(h);
        // Shell flyouts, dialogs and our own bars keep the previous app, as on macOS.
        if (!WindowTracker.IsAppWindow(h) && ClassName(h) is not ("Progman" or "WorkerW")) return;
        if (WindowTracker.Resolve(h) is not { } app) return;
        (foreground, foregroundApp) = (h, app);
        AppName.Text = app.Name;
    }

    internal static bool IsSearch(IntPtr h) => ClassName(h) == "Windows.UI.Core.CoreWindow" && GetWindowThreadProcessId(h, out var pid) != 0
        && string.Equals(Path.GetFileName(QueryProcess(pid).exe), "SearchHost.exe", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Moves the closed Search window above the dock, so it opens there: it keeps its spot between openings, but
    /// the shell puts it back over the taskbar (e.g. when the work area changes), and its first frame shows before any event.
    /// Quick Settings is placed afresh at every opening, so only Search can be readied this way.
    /// </summary>
    void ReadySearch()
    {
        if (!IsVisible || !dock.IsVisible) return; // the bars aren't in place yet
        if (!IsWindow(search)) search = AllTopLevelWindows().FirstOrDefault(IsSearch);
        if (search == IntPtr.Zero || search == flyout || IsShown(search)) return;
        if (!flyoutEdges.TryGetValue(search, out var edge))
        {
            if (PanelEdge(search) is not { } found) return; // a cloaked window still captures its last frame
            flyoutEdges[search] = edge = found;
        }
        MoveFlyout(search, edge);
    }

    void WatchFlyout(IntPtr h)
    {
        if (h == flyout) return;
        // Notification Center already starts under the menu bar, and its calendar can't be moved on its own.
        if (IsSearch(h)) search = h;
        else if (ClassName(h) != "ControlCenterWindow") return; // Quick Settings
        flyout = h;
        // The shell reuses the window, so last time's panel edge moves it at once; capturing takes a few frames.
        if (flyoutEdges.TryGetValue(h, out var edge)) MoveFlyout(h, edge);
        else
        {
            // First sight: park it above the screen until its panel is found, instead of showing it at the bottom first.
            GetWindowRect(h, out var r);
            (parkedY, parkedAt) = (r.Top, Environment.TickCount64);
            SetWindowPos(h, IntPtr.Zero, r.Left, r.Top - r.Bottom, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        }
        flyoutTimer.Start();
        PlaceFlyout();
    }

    // Closed, Quick Settings is hidden and Search is cloaked.
    internal static bool IsShown(IntPtr h) => IsWindowVisible(h) && DwmGetWindowAttribute(h, 14, out var cloaked, sizeof(int)) == 0 && cloaked == 0; // DWMWA_CLOAKED

    /// <summary>
    /// Moves the flyout so its panel sits just under the menu bar, or Search's just above the dock, keeping its x.
    /// Quick Settings grows upward (e.g. the Wi-Fi list), so this runs until the flyout closes.
    /// </summary>
    void PlaceFlyout()
    {
        if (!IsShown(flyout))
        {
            Unpark(); // closed before its panel was found; it must not reopen out of sight
            flyoutTimer.Stop();
            flyout = IntPtr.Zero;
            return;
        }
        // Until the panel is drawn, retry every frame.
        var edge = PanelEdge(flyout);
        flyoutTimer.Interval = TimeSpan.FromMilliseconds(edge == null ? 16 : 100);
        if (edge == null)
        {
            // Never leave it out of sight: if the capture keeps failing, give the shell's own spot back.
            if (Environment.TickCount64 - parkedAt > 500) Unpark();
            return;
        }
        parkedY = null;
        flyoutEdges[flyout] = edge.Value;
        MoveFlyout(flyout, edge.Value);
    }

    void Unpark()
    {
        if (parkedY is not { } y) return;
        GetWindowRect(flyout, out var r);
        SetWindowPos(flyout, IntPtr.Zero, r.Left, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        parkedY = null;
    }

    // Search lines up its bottom with the dock, the others their top with the menu bar.
    int? PanelEdge(IntPtr h) => ContentEdge(h, bottom: h == search);

    /// <summary>Moves a flyout so its panel edge, <paramref name="edge"/> pixels into the window, sits by the menu bar or the dock.</summary>
    void MoveFlyout(IntPtr h, int edge)
    {
        if (h == search && !dock.IsVisible) return;
        GetWindowRect(h, out var r);
        GetWindowRect(new WindowInteropHelper(this).Handle, out var bar);
        var gap = (int)Math.Round(FlyoutGap * PrimaryMonitor().scale);
        var y = h == search ? dock.ShelfTop - gap - edge : bar.Bottom + gap - edge;
        if (y != r.Top) SetWindowPos(h, IntPtr.Zero, r.Left, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    void Tick()
    {
        ReadySearch();
        // A UWP frame gets its app child only after it comes to the front, so resolve again.
        OnForeground(GetForegroundWindow());
        Clock.Text = DateTime.Now.ToString("M월 d일 (ddd) tt h:mm", Korean);
        GetSystemPowerStatus(out var power);
        var hasBattery = power.BatteryFlag is not (128 or 255) && power.BatteryLifePercent <= 100;
        BatteryButton.Visibility = hasBattery ? Visibility.Visible : Visibility.Collapsed;
        if (hasBattery)
        {
            BatteryText.Text = $"{power.BatteryLifePercent}%";
            BatteryFill.Width = 17 * power.BatteryLifePercent / 100.0;
            BatteryFill.Background = power.ACLineStatus == 1 ? Brushes.LimeGreen
                : power.BatteryLifePercent <= 20 ? Brushes.OrangeRed : (Brush)FindResource("BarFg");
        }
        Taskbar.Recheck();
    }

    bool HasAppWindow => foregroundApp != null && WindowTracker.IsAppWindow(foreground);

    IEnumerable<IntPtr> ForegroundAppWindows() =>
        WindowTracker.List().Where(w => w.App.Key == foregroundApp?.Key).Select(w => w.Hwnd);

    void Logo_Click(object sender, RoutedEventArgs e)
    {
        Menus.Show(LogoButton, PlacementMode.Bottom, new Control?[]
        {
            Menus.Item("이 PC에 관하여", () => Sys.Launch("ms-settings:about")),
            null,
            Menus.Item("시스템 설정...", () => Sys.Launch("ms-settings:")),
            Menus.Item("작업 관리자", () => Sys.Launch("taskmgr.exe")),
            null,
            Menus.Item("잠자기", () => SetSuspendState(false, false, false)),
            Menus.Item("재시동...", () => { if (Sys.Confirm("지금 재시동하시겠습니까?")) Sys.Run("shutdown", "/r /t 0"); }),
            Menus.Item("시스템 종료...", () => { if (Sys.Confirm("지금 시스템을 종료하시겠습니까?")) Sys.Run("shutdown", "/s /t 0"); }),
            null,
            Menus.Item("화면 잠금", () => LockWorkStation()),
            Menus.Item("로그아웃...", () => { if (Sys.Confirm("로그아웃하시겠습니까?")) ExitWindowsEx(0, 0); }),
            null,
            Menus.Item("Mindows 설정...", openSettings),
            Menus.Item("Mindows 종료",() => Application.Current.Shutdown()),
        });
    }

    /// <summary>Visible tray icons, app icons first and the pinned system icons (network, volume) next to the clock.</summary>
    static ListCollectionView TrayView(NotificationArea tray)
    {
        var power = new Guid(NotificationArea.POWER_GUID); // the menu bar draws its own battery
        var view = new ListCollectionView(tray.TrayIcons)
        {
            Filter = o => o is NotifyIcon { IsHidden: false } icon && icon.GUID != power,
            IsLiveFiltering = true,
            IsLiveSorting = true,
        };
        view.LiveFilteringProperties.Add(nameof(NotifyIcon.IsHidden));
        view.SortDescriptions.Add(new(nameof(NotifyIcon.IsPinned), ListSortDirection.Ascending));
        view.SortDescriptions.Add(new(nameof(NotifyIcon.PinOrder), ListSortDirection.Ascending));
        view.LiveSortingProperties.Add(nameof(NotifyIcon.IsPinned));
        view.LiveSortingProperties.Add(nameof(NotifyIcon.PinOrder));
        return view;
    }

    static NotifyIcon TrayIcon(object sender) => (NotifyIcon)((FrameworkElement)sender).DataContext;

    // The bar never takes focus, so WPF's own modifier state can be stale.
    static bool CtrlDown => (GetAsyncKeyState(0x11) & 0x8000) != 0; // VK_CONTROL

    void Tray_MouseDown(object sender, MouseButtonEventArgs e)
    {
        // As Cmd+drag on macOS: Ctrl+drag moves the icon instead of clicking it.
        if (tray != null && e.ChangedButton == MouseButton.Left && CtrlDown)
        {
            movingIcon = TrayIcon(sender);
            TrayIcons.CaptureMouse(); // not the icon's own element, which re-sorting can take out of the tree
            e.Handled = true;
            return;
        }
        TrayIcon(sender).IconMouseDown(e.ChangedButton, MouseHelper.GetCursorPositionParam(), GetDoubleClickTime());
        e.Handled = true;
    }

    void Tray_MouseUp(object sender, MouseButtonEventArgs e)
    {
        TrayIcon(sender).IconMouseUp(e.ChangedButton, MouseHelper.GetCursorPositionParam(), GetDoubleClickTime());
        e.Handled = true;
    }

    void Tray_MouseEnter(object sender, MouseEventArgs e)
    {
        if (movingIcon != null) return;
        var element = (FrameworkElement)sender;
        var icon = TrayIcon(sender);
        // Apps place their flyouts next to this rect (Shell_NotifyIconGetRect); physical pixels.
        var topLeft = element.PointToScreen(new Point(0, 0));
        var bottomRight = element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight));
        icon.Placement = new NativeMethods.Rect
        {
            Left = (int)topLeft.X, Top = (int)topLeft.Y, Right = (int)bottomRight.X, Bottom = (int)bottomRight.Y,
        };
        // Version 4 icons draw their own tooltip; older ones expect the host to show the tip text.
        element.ToolTip = icon.Version >= 4 ? null : icon.Title;
        icon.IconMouseEnter(MouseHelper.GetCursorPositionParam());
    }

    void Tray_MouseLeave(object sender, MouseEventArgs e) => TrayIcon(sender).IconMouseLeave(MouseHelper.GetCursorPositionParam());
    void Tray_MouseMove(object sender, MouseEventArgs e)
    {
        if (movingIcon == null) TrayIcon(sender).IconMouseMove(MouseHelper.GetCursorPositionParam());
    }

    /// <summary>Moves the dragged icon before the first other icon right of the pointer; all shown icons become pinned in that order.</summary>
    void TrayIcons_MouseMove(object sender, MouseEventArgs e)
    {
        if (movingIcon is not { } moving) return;
        var x = e.GetPosition(TrayIcons).X;
        var icons = TrayIcons.Items.Cast<NotifyIcon>().ToList();
        var next = icons.FirstOrDefault(i => i != moving && TrayIcons.ItemContainerGenerator.ContainerFromItem(i) is FrameworkElement c
            && c.TranslatePoint(new Point(c.ActualWidth / 2, 0), TrayIcons).X > x);
        if (next == icons.ElementAtOrDefault(icons.IndexOf(moving) + 1)) return;
        // Unpinned icons are shown left of the pinned ones. A saved identifier can hold an old title, so take the icon's current one.
        var order = icons.Where(i => !i.IsPinned).Select(i => i.Identifier)
            .Concat(tray!.PinnedNotifyIcons.Select(p => icons.FirstOrDefault(i => i.IsEqualByIdentifier(p))?.Identifier ?? p));
        tray.SetPinnedIcons(MoveBefore(order, moving.Identifier, next?.Identifier));
    }

    void TrayIcons_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (movingIcon != null) TrayIcons.ReleaseMouseCapture(); // ends the move in LostMouseCapture
    }

    void TrayIcons_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (movingIcon == null) return;
        movingIcon = null;
        config.TrayOrder = tray!.PinnedNotifyIcons;
        config.Save();
    }

    /// <summary>The order with id moved to just before next, or to the end; the other entries keep their order.</summary>
    static string[] MoveBefore(IEnumerable<string> order, string id, string? next)
    {
        var list = order.Where(x => !x.Equals(id, StringComparison.OrdinalIgnoreCase)).ToList();
        var at = next == null ? -1 : list.FindIndex(x => x.Equals(next, StringComparison.OrdinalIgnoreCase));
        list.Insert(at < 0 ? list.Count : at, id);
        return list.ToArray();
    }

    void App_Click(object sender, RoutedEventArgs e)
    {
        var name = foregroundApp?.Name ?? "";
        var ok = HasAppWindow;
        Menus.Show(AppButton, PlacementMode.Bottom, new Control?[]
        {
            Menus.Item($"{name} 가리기", () => { foreach (var h in ForegroundAppWindows()) ShowWindow(h, SW_MINIMIZE); }, ok),
            Menus.Item("기타 가리기", () =>
            {
                foreach (var w in WindowTracker.List().Where(w => w.App.Key != foregroundApp?.Key)) ShowWindow(w.Hwnd, SW_MINIMIZE);
            }, ok),
            null,
            Menus.Item($"{name} 종료", () => { foreach (var h in ForegroundAppWindows()) Sys.Close(h); }, ok),
        });
    }

    void Window_Click(object sender, RoutedEventArgs e)
    {
        var h = foreground;
        var ok = HasAppWindow;
        var items = new List<Control?>
        {
            Menus.Item("최소화", () => ShowWindow(h, SW_MINIMIZE), ok),
            Menus.Item("확대/축소", () => ShowWindow(h, IsZoomed(h) ? SW_RESTORE : SW_MAXIMIZE), ok),
            Menus.Item("닫기", () => Sys.Close(h), ok),
        };
        if (ok)
        {
            items.Add(null);
            foreach (var w in ForegroundAppWindows())
                items.Add(Menus.Item(WindowText(w), () => Sys.Focus(w), isChecked: w == h));
        }
        Menus.Show(WindowButton, PlacementMode.Bottom, items);
    }

    void Battery_Click(object sender, RoutedEventArgs e) => Sys.Launch("ms-settings:batterysaver");
    void Control_Click(object sender, RoutedEventArgs e) => Sys.WinKey('A');
    void Clock_Click(object sender, RoutedEventArgs e) => Sys.WinKey('N');
}
