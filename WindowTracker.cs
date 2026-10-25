using System.Diagnostics;
using static Mindows.Native;

namespace Mindows;

/// <summary>Key: shell:AppsFolder\&lt;AUMID&gt; for packaged apps, else the exe path. Matches <see cref="PinnedApp.Path"/>.</summary>
public record AppInfo(string Key, string ExePath, string Name);

public record AppWindow(IntPtr Hwnd, AppInfo App);

/// <summary>Finds the windows that would have a taskbar button and the app each belongs to.</summary>
static class WindowTracker
{
    static readonly uint OwnPid = (uint)Environment.ProcessId;
    static readonly string[] ShellClasses = { "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd" };
    static readonly Dictionary<string, string> names = new(StringComparer.OrdinalIgnoreCase);

    public static bool IsAppWindow(IntPtr h)
    {
        if (!IsWindowVisible(h) || GetWindow(h, GW_OWNER) != IntPtr.Zero) return false;
        var ex = (long)GetWindowLongPtr(h, GWL_EXSTYLE);
        if ((ex & WS_EX_APPWINDOW) == 0 && (ex & (WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE)) != 0) return false;
        // Cloaked: suspended UWP frames, windows on other virtual desktops.
        if (DwmGetWindowAttribute(h, 14, out var cloaked, sizeof(int)) == 0 && cloaked != 0) return false;
        return GetWindowTextLength(h) > 0 && !ShellClasses.Contains(ClassName(h));
    }

    /// <summary>App windows in z-order, topmost first.</summary>
    public static List<AppWindow> List()
    {
        var list = new List<AppWindow>();
        EnumWindows((h, _) =>
        {
            if (IsAppWindow(h) && Resolve(h) is { } app) list.Add(new(h, app));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// <summary>The app owning a window; null for Mindows' own windows or inaccessible processes.</summary>
    public static AppInfo? Resolve(IntPtr h)
    {
        GetWindowThreadProcessId(h, out var pid);
        if (pid == OwnPid) return null;
        var (exe, aumid) = QueryProcess(pid);
        if (exe == null) return null;
        // UWP apps live in a child window of an ApplicationFrameHost frame.
        if (Path.GetFileName(exe).Equals("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase))
        {
            uint child = 0;
            EnumChildWindows(h, (c, _) =>
            {
                GetWindowThreadProcessId(c, out var p);
                if (p == pid) return true;
                child = p;
                return false;
            }, IntPtr.Zero);
            if (child != 0 && QueryProcess(child) is { exe: { } childExe } info) (exe, aumid) = (childExe, info.aumid);
            // A minimized or suspended frame has no app child; the frame itself carries the app's ID.
            else if (WindowAumid(h) is { } frameAumid)
            {
                var frameKey = Config.AppsFolder + frameAumid;
                return new(frameKey, exe, names.GetValueOrDefault(frameKey) ?? WindowText(h));
            }
        }
        var key = aumid != null ? Config.AppsFolder + aumid : exe;
        return new(key, exe, NameOf(key, exe));
    }

    static string NameOf(string key, string exe)
    {
        if (names.TryGetValue(key, out var name)) return name;
        try { name = FileVersionInfo.GetVersionInfo(exe).FileDescription; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return names[key] = string.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(exe) : name.Trim();
    }
}
