using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    readonly ObservableCollection<NotifyIcon> shownIcons = new(); // the tray icons in the bar
    List<NotifyIcon> overflowIcons = new(); // the ones beyond Config.MaxTrayIcons, in the overflow menu
    ListCollectionView? trayView;

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
        if (tray != null)
        {
            trayView = TrayView(tray, config);
            ((INotifyCollectionChanged)trayView).CollectionChanged += (_, _) => UpdateTray();
            TrayIcons.ItemsSource = shownIcons;
            UpdateTray();
        }

        foregroundHook = (_, _, h, idObject, _, _, _) => { if (idObject == 0) OnForeground(h); };
        SetWinEventHook(3, 3, IntPtr.Zero, foregroundHook, 0, 0, 0); // EVENT_SYSTEM_FOREGROUND, out of context
        OnForeground(GetForegroundWindow());
        // Catch flyouts as they appear, before they take the foreground, so they don't show at the bottom first.
        shownHook = (_, _, h, idObject, _, _, _) => { if (idObject == 0) WatchFlyout(h); };
        SetWinEventHook(0x8002, 0x8002, IntPtr.Zero, shownHook, 0, 0, 0); // EVENT_OBJECT_SHOW: Quick Settings
        SetWinEventHook(0x8018, 0x8018, IntPtr.Zero, shownHook, 0, 0, 0); // EVENT_OBJECT_UNCLOAKED: Search
        flyoutTimer.Tick += (_, _) => PlaceFlyout();
        NetworkChange.NetworkAddressChanged += (_, _) => networkChanged = true;

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) =>
        {
            Tick();
            // Re-aim just past the next second boundary; a fixed 1s interval drifts and the clock skips a second.
            timer.Interval = TimeSpan.FromMilliseconds(1020 - DateTime.Now.Millisecond);
        };
        timer.Start();
        Tick();
        // The Hangul key changes the mode without any event we can watch, so poll it quickly.
        var imeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        imeTimer.Tick += (_, _) => UpdateIme();
        imeTimer.Start();
    }

    /// <summary>The taskbar's input indicator for the Korean IME in the foreground app: 한 in Hangul mode, A otherwise.</summary>
    // shortcut: reads the mode through IMM, so a TSF-only app may always show A; other languages show nothing.
    void UpdateIme()
    {
        var h = GetForegroundWindow();
        var korean = ((long)GetKeyboardLayout(GetWindowThreadProcessId(h, out _)) & 0xFFFF) == 0x412;
        ImeButton.Visibility = korean ? Visibility.Visible : Visibility.Collapsed;
        if (!korean) return;
        // WM_IME_CONTROL with IMC_GETCONVERSIONMODE; IME_CMODE_NATIVE (1) is Hangul. SMTO_ABORTIFHUNG.
        // A busy app holds up this thread, and the dock's with it, for the whole timeout, 5 times a second; so keep it short,
        // and keep the last letter when the app doesn't answer in time.
        var ime = ImmGetDefaultIMEWnd(h);
        var mode = IntPtr.Zero;
        if (ime != IntPtr.Zero && SendMessageTimeout(ime, 0x283, 1, IntPtr.Zero, 2, 20, out mode) == IntPtr.Zero) return;
        var text = ((long)mode & 1) != 0 ? "한" : "A";
        if (text == imeText) return;
        imeText = text;
        var typeface = new Typeface((FontFamily)FindResource("UiFont"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
        // Sized and placed to match the text in the macOS badge: centered, and 0.7 below the middle.
        var glyphs = new FormattedText(text, Korean, FlowDirection.LeftToRight, typeface, text == "A" ? 12.5 : 12, Brushes.Black,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        double width = ImeBadge.Width, height = ImeBadge.Height;
        var at = new Point((width - glyphs.WidthIncludingTrailingWhitespace) / 2, (height - glyphs.Height) / 2 + 0.7);
        ImeBadge.Data = new CombinedGeometry(GeometryCombineMode.Exclude,
            new RectangleGeometry(new Rect(0, 0, width, height), 4.2, 4.2), glyphs.BuildGeometry(at));
    }

    string? imeText; // the letter cut out of the IME badge

    /// <summary>
    /// Explorer draws its network icon itself and never hands it to a tray host, so draw it here: ‹···› while a wired
    /// connection is up, else Wi-Fi with the arcs beyond its signal strength dimmed, all of them when disconnected.
    /// </summary>
    void UpdateNetwork()
    {
        if (networkChanged)
        {
            networkChanged = false; // cleared first, so a change during the read is read again next tick
            // A gateway leaves out virtual adapters (WAN miniports, Hyper-V switches, tunnels).
            wired = NetworkInterface.GetAllNetworkInterfaces().Any(n => n.OperationalStatus == OperationalStatus.Up
                && n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet
                && n.GetIPProperties().GatewayAddresses.Count > 0);
            Ethernet.Visibility = wired ? Visibility.Visible : Visibility.Collapsed;
            Wifi.Visibility = wired ? Visibility.Collapsed : Visibility.Visible;
            wifiReadAt = 0;
        }
        if (wired || Environment.TickCount64 - wifiReadAt < 5000) return;
        wifiReadAt = Environment.TickCount64;
        var bars = WifiSignal() is not { } quality ? 0 : quality >= 67 ? 3 : quality >= 34 ? 2 : 1;
        WifiBar1.Opacity = bars >= 1 ? 1 : 0.3;
        WifiBar2.Opacity = bars >= 2 ? 1 : 0.3;
        WifiBar3.Opacity = bars >= 3 ? 1 : 0.3;
    }

    // Listing the adapters can block this thread for ~20 ms, so it is read only after an address change, not every tick.
    volatile bool networkChanged = true; // set on a pool thread by NetworkChange
    bool wired;
    long wifiReadAt; // the signal strength changes without an address change, so it is polled, every 5 s

    void Network_Click(object sender, RoutedEventArgs e) => Sys.Launch("ms-availablenetworks:");

    sealed record VolumeLevel(string Text, double Wave1, double Wave2, double Wave3);

    public static readonly DependencyProperty VolumeProperty = DependencyProperty.Register(nameof(Volume), typeof(object), typeof(MenuBarWindow));
    /// <summary>What the volume icon in the tray template shows, bound from there.</summary>
    public object? Volume { get => GetValue(VolumeProperty); set => SetValue(VolumeProperty, value); }

    /// <summary>The volume next to the speaker, with the waves beyond it dimmed, all of them when muted.</summary>
    void UpdateVolume()
    {
        var volume = Native.Volume();
        var waves = volume is not { muted: false, volume: > 0 and var v } ? 0 : v >= 67 ? 3 : v >= 34 ? 2 : 1;
        Volume = new VolumeLevel(volume is { } level ? $"{level.volume}%" : "", waves >= 1 ? 1 : 0.3, waves >= 2 ? 1 : 0.3, waves >= 3 ? 1 : 0.3);
    }

    int wheelRest; // a touchpad scrolls in fractions of a notch

    // Over the volume icon, each wheel notch moves the volume 2%.
    void Tray_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (TrayIcon(sender)?.GUID != new Guid(NotificationArea.VOLUME_GUID)) return;
        wheelRest += e.Delta;
        StepVolume(wheelRest / 60);
        wheelRest %= 60;
        UpdateVolume();
        e.Handled = true;
    }

    // The bar never takes focus, so the Hangul key reaches the foreground app, as on the taskbar.
    void Ime_Click(object sender, RoutedEventArgs e)
    {
        const byte Hangul = 0x15;
        keybd_event(Hangul, 0, 0, UIntPtr.Zero);
        keybd_event(Hangul, 0, 2, UIntPtr.Zero); // KEYEVENTF_KEYUP
        UpdateIme();
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

    /// <summary>Where UsageBar, a separate app, publishes AI usage for the menu bar; its README has the format.</summary>
    static readonly string UsagePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UsageBar", "status.json");
    JsonNode? usage;
    DateTime usageWrite;

    sealed record UsageItem(int Index, Geometry? Logo, string Text, string Tip, double Opacity);

    /// <summary>An item per provider in UsageBar's file: its mark and the percentage left, dimmed when its last refresh failed.</summary>
    void UpdateUsage()
    {
        var write = File.GetLastWriteTimeUtc(UsagePath); // 1601 when there is none
        // UsageBar rewrites it every 5 minutes and deletes it on exit; an older one was left behind by a crash.
        if (DateTime.UtcNow - write > TimeSpan.FromMinutes(15)) write = default;
        if (write == usageWrite) return;
        List<UsageItem>? items = null;
        try
        {
            usage = write == default ? null : JsonNode.Parse(File.ReadAllText(UsagePath));
            items = usage?["providers"]?.AsArray().Select((p, i) => p == null ? null : new UsageItem(i, Logo((string?)p["logo"]),
                    p["remainingPercent"] is { } left ? $"{(double)left:0}%" : "–",
                    $"{p["name"]}: {(string?)p["error"] ?? (p["remainingPercent"] is { } l ? $"{(double)l:0}% 남음" : "사용량 없음")}",
                    p["error"] == null ? 1 : 0.5))
                .OfType<UsageItem>().ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; } // being replaced or deleted; read it on the next tick
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException) { usage = null; } // not UsageBar's format
        usageWrite = write;
        UsageItems.ItemsSource = items;
    }

    static Geometry? Logo(string? data)
    {
        if (data == null) return null;
        var logo = Geometry.Parse(data);
        logo.Freeze();
        return logo;
    }

    void Usage_Click(object sender, RoutedEventArgs e)
    {
        if (usage?["pid"] is not JsonValue pidValue || !pidValue.TryGetValue<uint>(out var pid)
            || usage["hwnd"] is not JsonValue hwndValue || !hwndValue.TryGetValue<long>(out var hwnd)) return;
        var item = (FrameworkElement)sender;
        var at = item.PointToScreen(new Point(0, item.ActualHeight)); // the popup's top left, in pixels
        // The bar never takes focus, so let UsageBar bring its popup to the front.
        AllowSetForegroundWindow(pid);
        PostMessage((IntPtr)hwnd, RegisterWindowMessage("UsageBar.ShowPopup"), ((UsageItem)item.DataContext).Index,
            (IntPtr)(((int)at.Y << 16) | ((int)at.X & 0xFFFF)));
    }

    void Tick()
    {
        ReadySearch();
        UpdateNetwork();
        UpdateVolume();
        UpdateUsage();
        // A UWP frame gets its app child only after it comes to the front, so resolve again.
        OnForeground(GetForegroundWindow());
        Clock.Text = DateTime.Now.ToString("M월 d일 (ddd) tt h:mm:ss", Korean);
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

    /// <summary>Tray icons the menu bar can show; the power icon is left out because the menu bar draws its own battery, and UsageBar's because it shows its usage as items.</summary>
    internal static bool Listed(NotifyIcon icon) =>
        !icon.IsHidden && icon.GUID != new Guid(NotificationArea.POWER_GUID)
        && !string.Equals(Path.GetFileName(icon.Path), "UsageBar.exe", StringComparison.OrdinalIgnoreCase);

    internal static string IconName(NotifyIcon icon) =>
        string.IsNullOrEmpty(icon.Title) ? Path.GetFileNameWithoutExtension(icon.Path) : icon.Title;

    /// <summary>Called after the settings change which tray icons are shown.</summary>
    public void ApplyTray() => trayView?.Refresh(); // raises CollectionChanged, which updates the bar

    /// <summary>The leftmost icons beyond the maximum move to the overflow menu; the pinned ones next to the clock stay.</summary>
    void UpdateTray()
    {
        var all = trayView!.Cast<NotifyIcon>().ToList();
        var overflow = config.MaxTrayIcons > 0 ? Math.Max(all.Count - config.MaxTrayIcons, 0) : 0;
        overflowIcons = all.Take(overflow).ToList();
        var shown = all.Skip(overflow).ToList();
        // Edit in place rather than rebuild: a Ctrl+drag re-sorts on every move and needs the dragged icon's container to survive.
        for (var i = shownIcons.Count - 1; i >= 0; i--)
            if (!shown.Contains(shownIcons[i])) shownIcons.RemoveAt(i);
        for (var i = 0; i < shown.Count; i++)
        {
            var at = shownIcons.IndexOf(shown[i]);
            if (at < 0) shownIcons.Insert(i, shown[i]);
            else if (at != i) shownIcons.Move(at, i);
        }
        TrayOverflow.Visibility = overflow > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // shortcut: an overflow item forwards only a left click; its context menu needs the icon in the bar, when it is worth wiring.
    void TrayOverflow_Click(object sender, RoutedEventArgs e) =>
        Menus.Show(TrayOverflow, PlacementMode.Bottom, overflowIcons.Select(icon =>
        {
            var item = Menus.Item(IconName(icon), () =>
            {
                icon.Placement = ScreenRect(TrayOverflow);
                AllowSetForegroundWindow(uint.MaxValue);
                var cursor = MouseHelper.GetCursorPositionParam();
                icon.IconMouseDown(MouseButton.Left, cursor, GetDoubleClickTime());
                icon.IconMouseUp(MouseButton.Left, cursor, GetDoubleClickTime());
            });
            // The menu template draws only Header, so the icon goes in it.
            var image = new Image { Source = icon.Icon, Width = 16, Height = 16, Margin = new Thickness(0, 0, 8, 0) };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            item.Header = new StackPanel { Orientation = Orientation.Horizontal, Children = { image, new TextBlock { Text = IconName(icon) } } };
            return item;
        }));

    /// <summary>The element's bounds in physical pixels, where apps place their flyouts (Shell_NotifyIconGetRect).</summary>
    static NativeMethods.Rect ScreenRect(FrameworkElement element)
    {
        var topLeft = element.PointToScreen(new Point(0, 0));
        var bottomRight = element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight));
        return new NativeMethods.Rect { Left = (int)topLeft.X, Top = (int)topLeft.Y, Right = (int)bottomRight.X, Bottom = (int)bottomRight.Y };
    }

    /// <summary>Shown tray icons, app icons first and the pinned system icons (network, volume) next to the clock.</summary>
    static ListCollectionView TrayView(NotificationArea tray, Config config)
    {
        var view = new ListCollectionView(tray.TrayIcons)
        {
            Filter = o => o is NotifyIcon icon && Listed(icon) && !config.HiddenTrayIcons.Any(icon.IsEqualByIdentifier),
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

    // A container the live filter/sort removed keeps raising mouse events with DataContext {DisconnectedItem}.
    static NotifyIcon? TrayIcon(object sender) => ((FrameworkElement)sender).DataContext as NotifyIcon;

    // The bar never takes focus, so WPF's own modifier state can be stale.
    static bool CtrlDown => (GetAsyncKeyState(0x11) & 0x8000) != 0; // VK_CONTROL

    void Tray_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (TrayIcon(sender) is not { } icon) return;
        // As Cmd+drag on macOS: Ctrl+drag moves the icon instead of clicking it.
        if (tray != null && e.ChangedButton == MouseButton.Left && CtrlDown)
        {
            movingIcon = icon;
            TrayIcons.CaptureMouse(); // not the icon's own element, which re-sorting can take out of the tree
            e.Handled = true;
            return;
        }
        AllowSetForegroundWindow(uint.MaxValue); // ASFW_ANY: the app's window or menu would otherwise stay behind
        icon.IconMouseDown(e.ChangedButton, MouseHelper.GetCursorPositionParam(), GetDoubleClickTime());
        e.Handled = true;
    }

    void Tray_MouseUp(object sender, MouseButtonEventArgs e)
    {
        AllowSetForegroundWindow(uint.MaxValue);
        TrayIcon(sender)?.IconMouseUp(e.ChangedButton, MouseHelper.GetCursorPositionParam(), GetDoubleClickTime());
        e.Handled = true;
    }

    void Tray_MouseEnter(object sender, MouseEventArgs e)
    {
        if (movingIcon != null) return;
        var element = (FrameworkElement)sender;
        if (TrayIcon(sender) is not { } icon) return;
        icon.Placement = ScreenRect(element);
        // Version 4 icons draw their own tooltip; older ones expect the host to show the tip text.
        element.ToolTip = icon.Version >= 4 ? null : icon.Title;
        icon.IconMouseEnter(MouseHelper.GetCursorPositionParam());
    }

    void Tray_MouseLeave(object sender, MouseEventArgs e) => TrayIcon(sender)?.IconMouseLeave(MouseHelper.GetCursorPositionParam());
    void Tray_MouseMove(object sender, MouseEventArgs e)
    {
        if (movingIcon == null) TrayIcon(sender)?.IconMouseMove(MouseHelper.GetCursorPositionParam());
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
                items.Add(Menus.Item(WindowText(w) is { Length: > 0 } t ? t : foregroundApp!.Name, () => Sys.Focus(w), isChecked: w == h));
        }
        Menus.Show(WindowButton, PlacementMode.Bottom, items);
    }

    void Battery_Click(object sender, RoutedEventArgs e) => Sys.Launch("ms-settings:batterysaver");
    void Control_Click(object sender, RoutedEventArgs e) => Sys.WinKey('A');
    void Clock_Click(object sender, RoutedEventArgs e) => Sys.WinKey('N');
}
