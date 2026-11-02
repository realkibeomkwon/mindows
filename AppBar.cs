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
    // Explorer keeps reporting full screen while the foreground moves between windows it counts as full screen.
    static readonly WinEventProc foregroundHook = (_, _, _, idObject, _, _, _) => { if (idObject == 0) foreach (var bar in all) bar.UpdateVisibility(); };

    readonly Window window;
    readonly uint edge;
    readonly Func<double> reserve, height; // DIPs
    IntPtr hwnd;
    RECT reserved;
    bool fullScreenReported; // last ABN_FULLSCREENAPP

    public AppBar(Window window, uint edge, Func<double> reserve, Func<double> height)
    {
        (this.window, this.edge, this.reserve, this.height) = (window, edge, reserve, height);
        if (all.Count == 0) SetWinEventHook(3, 3, IntPtr.Zero, foregroundHook, 0, 0, 0); // EVENT_SYSTEM_FOREGROUND, out of context
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

    /// <summary>Re-reserves the screen strip and moves the window. Every maximized window resizes, so not per frame.</summary>
    public void Position()
    {
        if (hwnd == IntPtr.Zero) return;
        var (bounds, scale) = PrimaryMonitor();
        var r = (int)Math.Round(reserve() * scale);
        var d = Data();
        d.rc = bounds;
        if (edge == Top) d.rc.Bottom = d.rc.Top + r; else d.rc.Top = d.rc.Bottom - r;
        SHAppBarMessage(ABM_QUERYPOS, ref d);
        if (edge == Top) d.rc.Bottom = d.rc.Top + r; else d.rc.Top = d.rc.Bottom - r;
        SHAppBarMessage(ABM_SETPOS, ref d);
        reserved = d.rc;
        Resize();
    }

    /// <summary>Fits the window to the current height without changing the reservation.</summary>
    public void Resize()
    {
        if (hwnd == IntPtr.Zero) return;
        var h = (int)Math.Round(height() * PrimaryMonitor().scale);
        var top = edge == Top ? reserved.Top : reserved.Bottom - h;
        SetWindowPos(hwnd, HWND_TOPMOST, reserved.Left, top, reserved.Right - reserved.Left, h, SWP_NOACTIVATE);
    }

    IntPtr Hook(IntPtr h, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == CallbackMsg)
        {
            if (wParam == 1) Position(); // ABN_POSCHANGED
            else if (wParam == 2) // ABN_FULLSCREENAPP
            {
                fullScreenReported = lParam != 0;
                UpdateVisibility();
            }
        }
        else if (msg == TaskbarCreated && hwnd != IntPtr.Zero) // ignored once removed, so exit can't re-hide the taskbar
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

    void UpdateVisibility()
    {
        if (hwnd == IntPtr.Zero) return;
        window.Visibility = fullScreenReported && IsFullScreen(GetForegroundWindow()) ? Visibility.Hidden : Visibility.Visible;
    }

    // Explorer also reports a full-screen app once the last window closes and whatever is left takes the foreground:
    // the desktop, the lock screen's screen-sized window that stays visible behind it, or a background app's zero-size window.
    static bool IsFullScreen(IntPtr h)
    {
        if (h == IntPtr.Zero || ClassName(h) is "Progman" or "WorkerW") return false;
        GetWindowThreadProcessId(h, out var pid);
        if (Path.GetFileName(QueryProcess(pid).exe) is "LockApp.exe") return false;
        GetWindowRect(h, out var r);
        var m = PrimaryMonitor().bounds;
        return r.Left <= m.Left && r.Top <= m.Top && r.Right >= m.Right && r.Bottom >= m.Bottom;
    }
}
