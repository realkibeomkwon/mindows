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
    // Search and Start are flyouts, not apps: the dock has its own Search icon and the shell opens Start.
    static readonly HashSet<string> ShellFlyouts = new(StringComparer.OrdinalIgnoreCase) { "SearchHost.exe", "StartMenuExperienceHost.exe" };
    static readonly Dictionary<string, string> names = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="anyDesktop">Also windows on other virtual desktops, which Windows cloaks.</param>
    public static bool IsAppWindow(IntPtr h, bool anyDesktop = false)
    {
        if (!IsWindowVisible(h) || GetWindow(h, GW_OWNER) != IntPtr.Zero) return false;
        var ex = (long)GetWindowLongPtr(h, GWL_EXSTYLE);
        if ((ex & WS_EX_APPWINDOW) == 0 && (ex & (WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE)) != 0) return false;
        // Cloaked: suspended UWP frames, windows on other virtual desktops.
        if (!anyDesktop && DwmGetWindowAttribute(h, 14, out var cloaked, sizeof(int)) == 0 && cloaked != 0) return false;
        // WS_EX_APPWINDOW asks for a taskbar button even without a title (e.g. WPF windows with an empty Title).
        return ((ex & WS_EX_APPWINDOW) != 0 || GetWindowTextLength(h) > 0) && !ShellClasses.Contains(ClassName(h));
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
        foreach (var gone in resolved.Keys.Where(h => !IsWindow(h)).ToList()) resolved.Remove(gone);
        return list;
    }

    /// <summary>The app owning a window; null for Mindows' own windows, shell flyouts, or inaccessible processes.</summary>
    public static AppInfo? Resolve(IntPtr h)
    {
        GetWindowThreadProcessId(h, out var pid);
        if (pid == OwnPid) return null;
        // The pid check guards against a recycled handle.
        if (resolved.TryGetValue(h, out var hit) && hit.Pid == pid) return hit.App;
        var (exe, aumid) = QueryProcess(pid);
        if (exe == null || ShellFlyouts.Contains(Path.GetFileName(exe))) return null;
        var cacheable = true;
        // UWP apps live in a child window of an ApplicationFrameHost frame.
        if (Path.GetFileName(exe).Equals("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase))
        {
            cacheable = false; // its app child window appears later
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
        // Chrome's installed web apps run in the browser's process but tag their windows with the app ID of their
        // Start menu shortcut; plain browser windows carry a profile ID that no shortcut has, so they stay with chrome.exe.
        var chrome = aumid == null && Path.GetFileName(exe).Equals("chrome.exe", StringComparison.OrdinalIgnoreCase);
        if (chrome && WindowAumid(h) is { } webApp && webApp.Contains("._crx_"))
        {
            var appKey = Config.AppsFolder + webApp;
            if (!names.TryGetValue(appKey, out var appName)) names[appKey] = appName = ShellItemName(appKey) ?? WindowText(h);
            return Cache(h, pid, new(appKey, exe, appName), cacheable);
        }
        var key = aumid != null ? Config.AppsFolder + aumid : exe;
        // A plain Chrome window may still get its web app ID, so it is asked again.
        return Cache(h, pid, new(key, exe, NameOf(key, exe)), cacheable && !chrome);
    }

    static readonly Dictionary<IntPtr, (uint Pid, AppInfo App)> resolved = new();

    static AppInfo Cache(IntPtr h, uint pid, AppInfo app, bool cacheable)
    {
        if (cacheable) resolved[h] = (pid, app);
        return app;
    }

    static string NameOf(string key, string exe)
    {
        if (names.TryGetValue(key, out var name)) return name;
        try { name = FileVersionInfo.GetVersionInfo(exe).FileDescription; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return names[key] = string.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(exe) : name.Trim();
    }
}
