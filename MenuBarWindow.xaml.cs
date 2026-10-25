using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using static Mindows.Native;

namespace Mindows;

public partial class MenuBarWindow : Window
{
    const double BarHeight = 28;
    static readonly CultureInfo Korean = new("ko-KR");

    readonly WinEventProc foregroundHook; // held so the GC keeps the native callback alive
    IntPtr foreground;
    AppInfo? foregroundApp;

    public MenuBarWindow()
    {
        InitializeComponent();
        _ = new AppBar(this, AppBar.Top, BarHeight, BarHeight);

        foregroundHook = (_, _, h, idObject, _, _, _) => { if (idObject == 0) OnForeground(h); };
        SetWinEventHook(3, 3, IntPtr.Zero, foregroundHook, 0, 0, 0); // EVENT_SYSTEM_FOREGROUND, out of context
        OnForeground(GetForegroundWindow());

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => Tick();
        timer.Start();
        Tick();
    }

    void OnForeground(IntPtr h)
    {
        // Shell flyouts, dialogs and our own bars keep the previous app, as on macOS.
        if (!WindowTracker.IsAppWindow(h) && ClassName(h) is not ("Progman" or "WorkerW")) return;
        if (WindowTracker.Resolve(h) is not { } app) return;
        (foreground, foregroundApp) = (h, app);
        AppName.Text = app.Name;
    }

    void Tick()
    {
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
            Menus.Item("Windows 작업 표시줄 보기", ToggleTaskbar, isChecked: Taskbar.Peeking),
            Menus.Item("Mindows 종료", () => Application.Current.Shutdown()),
        });
    }

    /// <summary>Gives temporary access to the system tray, which Mindows does not replace.</summary>
    static void ToggleTaskbar()
    {
        var dock = Application.Current.Windows.OfType<DockWindow>().First();
        if (Taskbar.Peeking)
        {
            Taskbar.Hide();
            dock.Visibility = Visibility.Visible;
        }
        else
        {
            Taskbar.Peek();
            dock.Visibility = Visibility.Hidden;
        }
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
    void Search_Click(object sender, RoutedEventArgs e) => Sys.WinKey('S');
    void Clock_Click(object sender, RoutedEventArgs e) => Sys.WinKey('N');
}
