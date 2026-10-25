using static Mindows.Native;

namespace Mindows;

/// <summary>Hides the Windows taskbar and puts it back exactly as it was.</summary>
static class Taskbar
{
    const int ABS_AUTOHIDE = 1;

    // Original auto-hide state, written before we change it so `--restore` works after a crash or kill.
    static string StateFile => Path.Combine(Config.Dir, "taskbar-state.txt");

    public static void Hide()
    {
        var tray = ExplorerTray();
        if (tray == IntPtr.Zero) return;
        if (!File.Exists(StateFile)) File.WriteAllText(StateFile, GetState(tray).ToString());
        // A hidden taskbar still reserves its work area unless auto-hide is on.
        SetState(tray, ABS_AUTOHIDE);
        foreach (var h in TrayWindows()) ShowWindow(h, SW_HIDE);
    }

    public static void Restore()
    {
        var tray = ExplorerTray();
        if (tray == IntPtr.Zero) return; // Explorer not running; keep the state file for later.
        if (File.Exists(StateFile) && int.TryParse(File.ReadAllText(StateFile), out var state)) SetState(tray, state);
        foreach (var h in TrayWindows()) ShowWindow(h, SW_SHOW);
        File.Delete(StateFile);
    }

    /// <summary>Explorer re-shows the taskbar on its own (restart, some notifications).</summary>
    public static void Recheck()
    {
        if (TrayWindows().Any(IsWindowVisible)) Hide();
    }

    /// <summary>Asks every app to re-add its tray icons, e.g. to Explorer after our tray host died.</summary>
    public static void BroadcastTaskbarCreated() =>
        PostMessage(new IntPtr(0xFFFF) /* HWND_BROADCAST */, RegisterWindowMessage("TaskbarCreated"), IntPtr.Zero, IntPtr.Zero);

    /// <summary>Explorer's taskbar; our tray host registers a window of the same class.</summary>
    static IntPtr ExplorerTray()
    {
        for (var h = FindWindowEx(IntPtr.Zero, IntPtr.Zero, "Shell_TrayWnd", null); h != IntPtr.Zero;
             h = FindWindowEx(IntPtr.Zero, h, "Shell_TrayWnd", null))
        {
            GetWindowThreadProcessId(h, out var pid);
            if (pid != Environment.ProcessId) return h;
        }
        return IntPtr.Zero;
    }

    static IEnumerable<IntPtr> TrayWindows()
    {
        var tray = ExplorerTray();
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
