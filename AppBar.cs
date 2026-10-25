using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using static Mindows.Native;

namespace Mindows;

/// <summary>
/// Registers a window as a shell app bar on the primary monitor so maximized windows stop at it.
/// The window may be taller than the reserved strip (the dock needs room to magnify).
/// </summary>
sealed class AppBar
{
    public const uint Top = 1, Bottom = 3;
    static readonly uint CallbackMsg = RegisterWindowMessage("MindowsAppBar");
    static readonly uint TaskbarCreated = RegisterWindowMessage("TaskbarCreated");
    static readonly List<AppBar> all = new();

    readonly Window window;
    readonly uint edge;
    readonly double reserve, height;
    IntPtr hwnd;

    public AppBar(Window window, uint edge, double reserve, double height)
    {
        (this.window, this.edge, this.reserve, this.height) = (window, edge, reserve, height);
        all.Add(this);
        window.SourceInitialized += (_, _) =>
        {
            hwnd = new WindowInteropHelper(window).Handle;
            // Tool window: no Alt+Tab entry. No-activate: clicking a bar keeps the user's app in the foreground.
            var ex = (long)GetWindowLongPtr(hwnd, GWL_EXSTYLE);
            SetWindowLongPtr(hwnd, GWL_EXSTYLE, (IntPtr)(ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE));
            HwndSource.FromHwnd(hwnd).AddHook(Hook);
            Register();
        };
        window.Closed += (_, _) => Remove();
    }

    public static void RemoveAll()
    {
        foreach (var bar in all) bar.Remove();
    }

    APPBARDATA Data() => new() { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = hwnd, uCallbackMessage = CallbackMsg, uEdge = edge };

    void Register()
    {
        var d = Data();
        SHAppBarMessage(ABM_NEW, ref d);
        Position();
    }

    void Remove()
    {
        if (hwnd == IntPtr.Zero) return;
        var d = Data();
        SHAppBarMessage(ABM_REMOVE, ref d);
        hwnd = IntPtr.Zero;
    }

    void Position()
    {
        if (hwnd == IntPtr.Zero) return;
        var (bounds, scale) = PrimaryMonitor();
        int r = (int)Math.Round(reserve * scale), h = (int)Math.Round(height * scale);
        var d = Data();
        d.rc = bounds;
        if (edge == Top) d.rc.Bottom = d.rc.Top + r; else d.rc.Top = d.rc.Bottom - r;
        SHAppBarMessage(ABM_QUERYPOS, ref d);
        if (edge == Top) d.rc.Bottom = d.rc.Top + r; else d.rc.Top = d.rc.Bottom - r;
        SHAppBarMessage(ABM_SETPOS, ref d);
        var top = edge == Top ? d.rc.Top : d.rc.Bottom - h;
        SetWindowPos(hwnd, HWND_TOPMOST, d.rc.Left, top, d.rc.Right - d.rc.Left, h, SWP_NOACTIVATE);
    }

    IntPtr Hook(IntPtr h, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == CallbackMsg)
        {
            if (wParam == 1) Position(); // ABN_POSCHANGED
            else if (wParam == 2) window.Visibility = lParam != 0 ? Visibility.Hidden : Visibility.Visible; // ABN_FULLSCREENAPP
        }
        else if (msg == TaskbarCreated)
        {
            // Explorer restarted: it forgot our reservation and showed its taskbar again.
            Taskbar.Recheck();
            Register();
        }
        else if (msg is 0x7E or 0x2E0) // WM_DISPLAYCHANGE, WM_DPICHANGED
        {
            window.Dispatcher.BeginInvoke(Position);
        }
        return IntPtr.Zero;
    }
}
