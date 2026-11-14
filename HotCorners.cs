using System.Diagnostics;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using static Mindows.HotCornerAction;
using static Mindows.Native;

namespace Mindows;

public enum HotCornerAction
{
    None, MissionControl, ApplicationWindows, Desktop, NotificationCenter, Apps, QuickNote,
    StartScreenSaver, DisableScreenSaver, DisplaySleep, LockScreen,
}

/// <summary>
/// macOS hot corners: pushing the pointer into a corner of the primary monitor, with that corner's modifiers held,
/// runs its action once per visit.
/// </summary>
static class HotCorners
{
    /// <summary>Menu order and groups as on macOS; null is a separator.</summary>
    public static readonly HotCornerAction?[] Menu =
    {
        MissionControl, ApplicationWindows, Desktop, NotificationCenter, null, Apps, null, QuickNote, null,
        StartScreenSaver, DisableScreenSaver, null, DisplaySleep, null, LockScreen, null, None,
    };

    static readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    static Config? config;
    static int at = -1;          // corner the pointer is in
    static bool fired;           // its action has run during this visit
    static int? screenSaverWas;  // SPI_GETSCREENSAVEACTIVE before Disable Screen Saver switched it off
    static bool masked;          // Win has been masked during this press
    static List<IntPtr> minimized = new(); // windows Desktop minimized, topmost first

    static HotCorners()
    {
        var m = new RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };
        Debug.Assert(Corner(new() { X = 0, Y = 0 }, m) == 0 && Corner(new() { X = 1919, Y = 0 }, m) == 1
            && Corner(new() { X = 0, Y = 1079 }, m) == 2 && Corner(new() { X = 1919, Y = 1079 }, m) == 3,
            "the pointer stops one pixel inside the monitor's right and bottom edges");
        Debug.Assert(Corner(new() { X = 1, Y = 0 }, m) == -1 && Corner(new() { X = -5, Y = 0 }, m) == -1,
            "only the corner pixel counts, not a pointer passing over it on a neighboring monitor");
    }

    public static void Start(Config c)
    {
        config = c;
        timer.Tick += (_, _) => Poll();
        Apply();
    }

    /// <summary>Polls only while some corner has an action.</summary>
    public static void Apply()
    {
        timer.IsEnabled = config!.HotCorners.Any(c => c.Action != None);
        if (!timer.IsEnabled) Leave();
    }

    public static void Stop()
    {
        timer.Stop();
        Leave();
    }

    public static string Describe(HotCornerAction action, ModifierKeys modifiers) =>
        action == None ? "-" : $"{Glyphs(modifiers)} {Label(action)}".TrimStart();

    static string Label(HotCornerAction action) => action switch
    {
        MissionControl => "Mission Control",
        ApplicationWindows => "응용 프로그램 윈도우",
        Desktop => "데스크탑",
        NotificationCenter => "알림 센터",
        Apps => "앱",
        QuickNote => "빠른 메모",
        StartScreenSaver => "화면 보호기 시작",
        DisableScreenSaver => "화면 보호기 비활성화",
        DisplaySleep => "디스플레이를 잠자기 상태로 전환",
        LockScreen => "화면 잠금",
        _ => "-",
    };

    /// <summary>Windows key names, in the keyboard's order: Ctrl+Win+Alt+Shift.</summary>
    public static string Glyphs(ModifierKeys m) => string.Join("+", new[]
    {
        m.HasFlag(ModifierKeys.Control) ? "Ctrl" : null, m.HasFlag(ModifierKeys.Windows) ? "Win" : null,
        m.HasFlag(ModifierKeys.Alt) ? "Alt" : null, m.HasFlag(ModifierKeys.Shift) ? "Shift" : null,
    }.OfType<string>());

    static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    /// <summary>Read from the keyboard itself: the pointer is usually over another app's window.</summary>
    public static ModifierKeys HeldModifiers() =>
        (Down(0x11) ? ModifierKeys.Control : 0) | (Down(0x12) ? ModifierKeys.Alt : 0) | (Down(0x10) ? ModifierKeys.Shift : 0)
        | (Down(0x5B) || Down(0x5C) ? ModifierKeys.Windows : 0);

    /// <summary>Taps an unassigned key while Win is held, so letting go of Win doesn't open Start.</summary>
    public static void MaskWin()
    {
        var win = Down(0x5B) || Down(0x5C);
        if (win && !masked) Tap(0xE8);
        masked = win;
    }

    static void Tap(byte vk)
    {
        keybd_event(vk, 0, 0, UIntPtr.Zero);
        keybd_event(vk, 0, 2, UIntPtr.Zero); // KEYEVENTF_KEYUP
    }

    /// <summary>
    /// Runs <paramref name="action"/> with the held modifiers let go, so they don't join a shortcut it sends (Ctrl+Win+D adds a
    /// desktop), then presses them again: they are still held, and the next visit must see them. The masking taps keep letting
    /// go of Alt or Win from opening the app's menu or Start.
    /// </summary>
    static void WithoutModifiers(Action action)
    {
        // Left and right keys apart: a generic VK_CONTROL up lets go of the left key only.
        var held = new byte[] { 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0x5B, 0x5C }.Where(vk => Down(vk)).ToList();
        static uint Extended(byte vk) => vk is 0xA3 or 0xA5 or 0x5B or 0x5C ? 1u : 0u; // KEYEVENTF_EXTENDEDKEY
        Tap(0xE8);
        foreach (var vk in held) keybd_event(vk, 0, Extended(vk) | 2, UIntPtr.Zero); // KEYEVENTF_KEYUP
        action();
        foreach (var vk in held) keybd_event(vk, 0, Extended(vk), UIntPtr.Zero);
        Tap(0xE8);
    }

    /// <summary>0 top-left, 1 top-right, 2 bottom-left, 3 bottom-right, else -1.</summary>
    static int Corner(POINT p, RECT m)
    {
        bool left = p.X == m.Left, right = p.X == m.Right - 1, top = p.Y == m.Top, bottom = p.Y == m.Bottom - 1;
        return top && left ? 0 : top && right ? 1 : bottom && left ? 2 : bottom && right ? 3 : -1;
    }

    static void Poll()
    {
        if (!GetCursorPos(out var p)) return; // another desktop, e.g. a UAC prompt
        var corner = Corner(p, PrimaryMonitor().bounds);
        if (corner != at)
        {
            Leave();
            at = corner;
        }
        // The modifiers may come after the pointer; once the action has run, only leaving the corner re-arms it.
        // shortcut: no held modifier reads while an elevated window is in front (UIPI, raw input too); uiAccess would lift it.
        if (at < 0 || fired || config!.HotCorners[at] is not { Action: not None } c || c.Modifiers != HeldModifiers()) return;
        fired = true;
        if (c.Modifiers != 0) WithoutModifiers(() => Run(c.Action));
        else Run(c.Action);
    }

    static void Leave()
    {
        at = -1;
        fired = false;
        if (screenSaverWas is not { } was) return;
        SystemParametersInfo(0x11, (uint)was, ref was, 0); // SPI_SETSCREENSAVEACTIVE, for this session only
        SetThreadExecutionState(0x80000000); // ES_CONTINUOUS
        screenSaverWas = null;
    }

    /// <summary>
    /// Minimizes every app window, or brings back the ones it minimized while any of them still is. Unlike Win+D, which
    /// restores them in a different stacking order each time, they come back bottom first so the old top window ends up on top.
    /// They get the minimize and restore buttons' WM_SYSCOMMAND: Visual Studio ignores ShowWindow for both.
    /// </summary>
    static void ToggleDesktop()
    {
        var back = minimized.Where(h => IsWindow(h) && IsIconic(h)).ToList();
        minimized = new();
        if (back.Count == 0)
        {
            minimized = WindowTracker.List().Select(w => w.Hwnd).Where(h => !IsIconic(h)).ToList(); // topmost first
            // As Win+D does, give the desktop the foreground first. Otherwise minimizing the active window activates the next one,
            // which can be an elevated app's stray window; while that is in front, GetAsyncKeyState reads no held modifiers.
            TakeForeground(GetShellWindow());
            foreach (var h in minimized) PostMessage(h, 0x112, new IntPtr(0xF020), IntPtr.Zero); // WM_SYSCOMMAND, SC_MINIMIZE
            return;
        }
        // Sent, not posted, so each is restored before the next one stacks on it; a hung app is skipped after half a second.
        for (var i = back.Count - 1; i >= 0; i--)
            SendMessageTimeout(back[i], 0x112, new IntPtr(0xF120), IntPtr.Zero, 2, 500, out _); // SC_RESTORE, SMTO_ABORTIFHUNG
        // Windows may refuse to activate them from here, which would leave them unordered, so stack them explicitly too.
        // An elevated window refuses this (UIPI), but the restores above already stacked it in order.
        for (var i = 1; i < back.Count; i++) SetWindowPos(back[i], back[i - 1], 0, 0, 0, 0, SWP_NOSIZE | 2 | SWP_NOACTIVATE); // SWP_NOMOVE
        TakeForeground(back[0]);
    }

    /// <summary>
    /// Nothing in Mindows got the input that triggered the corner, so Windows may refuse the foreground; it lets a process
    /// take it while that process holds Alt down. The masking tap keeps letting go of Alt from opening a menu.
    /// </summary>
    static void TakeForeground(IntPtr h)
    {
        if (SetForegroundWindow(h)) return;
        keybd_event(0x12, 0, 0, UIntPtr.Zero); // VK_MENU
        SetForegroundWindow(h);
        Tap(0xE8);
        keybd_event(0x12, 0, 2, UIntPtr.Zero);
    }

    static void Run(HotCornerAction action)
    {
        switch (action)
        {
            // shortcut: Windows has no view of one app's windows, so Application Windows shows Task View like Mission Control.
            case MissionControl or ApplicationWindows: Sys.WinKey('\t'); break;
            case Desktop: ToggleDesktop(); break;
            case NotificationCenter: Sys.WinKey('N'); break;
            case Apps: Tap(0x5B); break; // Start
            case QuickNote: Sys.Launch("notepad.exe"); break;
            case StartScreenSaver:
                // With no screen saver chosen, macOS still starts one; Blank is Windows' built-in.
                Sys.Launch(Registry.GetValue(@"HKEY_CURRENT_USER\Control Panel\Desktop", "SCRNSAVE.EXE", null) is string { Length: > 0 } saver
                    ? saver : Path.Combine(Environment.SystemDirectory, "scrnsave.scr"));
                break;
            case DisableScreenSaver:
                // Held only while the pointer stays in the corner. Keeping the display on does not stop the screen saver by itself.
                int active = 0;
                SystemParametersInfo(0x10, 0, ref active, 0); // SPI_GETSCREENSAVEACTIVE
                screenSaverWas = active;
                SystemParametersInfo(0x11, 0, ref active, 0);
                SetThreadExecutionState(0x80000002); // ES_CONTINUOUS | ES_DISPLAY_REQUIRED
                break;
            case DisplaySleep: PostMessage(new IntPtr(0xFFFF), 0x112, new IntPtr(0xF170), new IntPtr(2)); break; // HWND_BROADCAST, WM_SYSCOMMAND, SC_MONITORPOWER, off
            case LockScreen: LockWorkStation(); break;
        }
    }
}
