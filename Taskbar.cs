using static Mindows.Native;

namespace Mindows;

/// <summary>Hides the Windows taskbar and puts it back exactly as it was.</summary>
static class Taskbar
{
    const int ABS_AUTOHIDE = 1;

    // Original auto-hide state, written before we change it so `--restore` works after a crash or kill.
    static string StateFile => Path.Combine(Config.Dir, "taskbar-state.txt");

    public static bool Peeking { get; private set; }

    public static void Hide()
    {
        Peeking = false;
        var tray = FindWindow("Shell_TrayWnd", null);
        if (tray == IntPtr.Zero) return;
        if (!File.Exists(StateFile)) File.WriteAllText(StateFile, GetState(tray).ToString());
        // A hidden taskbar still reserves its work area unless auto-hide is on.
        SetState(tray, ABS_AUTOHIDE);
        foreach (var h in TrayWindows()) ShowWindow(h, SW_HIDE);
    }

    public static void Restore()
    {
        var tray = FindWindow("Shell_TrayWnd", null);
        if (tray == IntPtr.Zero) return; // Explorer not running; keep the state file for later.
        if (File.Exists(StateFile) && int.TryParse(File.ReadAllText(StateFile), out var state)) SetState(tray, state);
        foreach (var h in TrayWindows()) ShowWindow(h, SW_SHOW);
        File.Delete(StateFile);
    }

    public static void Peek()
    {
        Restore();
        Peeking = true;
    }

    /// <summary>Explorer re-shows the taskbar on its own (restart, some notifications).</summary>
    public static void Recheck()
    {
        if (!Peeking && TrayWindows().Any(IsWindowVisible)) Hide();
    }

    static IEnumerable<IntPtr> TrayWindows()
    {
        var tray = FindWindow("Shell_TrayWnd", null);
        if (tray != IntPtr.Zero) yield return tray;
        for (var h = FindWindowEx(IntPtr.Zero, IntPtr.Zero, "Shell_SecondaryTrayWnd", null); h != IntPtr.Zero;
             h = FindWindowEx(IntPtr.Zero, h, "Shell_SecondaryTrayWnd", null))
            yield return h;
    }

    static int GetState(IntPtr tray)
    {
        var d = new APPBARDATA { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<APPBARDATA>(), hWnd = tray };
        return (int)SHAppBarMessage(ABM_GETSTATE, ref d);
    }

    static void SetState(IntPtr tray, int state)
    {
        var d = new APPBARDATA { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<APPBARDATA>(), hWnd = tray, lParam = state };
        SHAppBarMessage(ABM_SETSTATE, ref d);
    }
}
