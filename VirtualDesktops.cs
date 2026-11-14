using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using static Mindows.Native;

namespace Mindows;

/// <summary>
/// Global shortcuts that jump straight to virtual desktop N. Windows has no documented call for that, but giving the
/// foreground to a window on another desktop makes Windows switch to it, in one step.
/// </summary>
static class VirtualDesktops
{
    [ComImport, Guid("a5cd92ff-29be-454c-8d04-d82879fb3f1b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IVirtualDesktopManager
    {
        [PreserveSig] int IsWindowOnCurrentVirtualDesktop(IntPtr h, out bool onCurrent);
        [PreserveSig] int GetWindowDesktopId(IntPtr h, out Guid desktop);
        [PreserveSig] int MoveWindowToDesktop(IntPtr h, ref Guid desktop);
    }

    [ComImport, Guid("aa509086-5ca9-4c25-8f95-589d3c07b48a")] class VirtualDesktopManager { }

    const string DesktopsKey = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops";
    static IVirtualDesktopManager? manager;
    static Config? config;
    static HwndSource? hotkeys;
    static Window? helper;
    /// <summary>Per Config.DesktopShortcuts entry: RegisterHotKey refused it, usually because another app owns the combination.</summary>
    public static bool[] Failed { get; } = new bool[9];

    public static void Start(Config c)
    {
        config = c;
        hotkeys = new HwndSource(new HwndSourceParameters("MindowsDesktops") { ParentWindow = new IntPtr(-3) }); // HWND_MESSAGE
        hotkeys.AddHook((IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled) =>
        {
            if (msg == 0x312 && (int)w is >= 1 and <= 9) Switch((int)w); // WM_HOTKEY; the id is the desktop number
            return IntPtr.Zero;
        });
        Apply();
    }

    /// <summary>Registers the shortcuts again; off, it only lets go of them, so the settings recorder can receive them as keys.</summary>
    public static void Apply(bool register = true)
    {
        for (var i = 0; i < 9; i++)
        {
            UnregisterHotKey(hotkeys!.Handle, i + 1);
            var s = config!.DesktopShortcuts[i];
            // ModifierKeys has the MOD_ALT/CONTROL/SHIFT/WIN values; MOD_NOREPEAT keeps a held key from switching again.
            // A hand-edited entry without Ctrl, Alt or Win would take a key away from typing everywhere, so it is skipped.
            if (!register || s.Key == Key.None || (s.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) == 0)
            {
                Failed[i] = false;
                continue;
            }
            Failed[i] = !RegisterHotKey(hotkeys.Handle, i + 1, (uint)s.Modifiers | 0x4000, (uint)KeyInterop.VirtualKeyFromKey(s.Key));
        }
    }

    public static string Describe(DesktopShortcut s) =>
        s.Key == Key.None ? "없음" : $"{HotCorners.Glyphs(s.Modifiers)}+{KeyName(s.Key)}".TrimStart('+');

    static string KeyName(Key k) => k switch
    {
        >= Key.D0 and <= Key.D9 => $"{k - Key.D0}",
        >= Key.NumPad0 and <= Key.NumPad9 => $"Num {k - Key.NumPad0}",
        // The characters these keys type on the US and Korean layouts.
        Key.OemComma => ",", Key.OemPeriod => ".", Key.OemQuestion => "/", Key.OemSemicolon => ";", Key.OemQuotes => "'",
        Key.OemOpenBrackets => "[", Key.OemCloseBrackets => "]", Key.OemMinus => "-", Key.OemPlus => "=", Key.OemPipe => "\\", Key.OemTilde => "`",
        _ => $"{k}",
    };

    /// <summary>Desktops in the order Task View shows them; Windows rewrites the list as they are added, removed or moved.</summary>
    public static Guid[] List() => Registry.GetValue(DesktopsKey, "VirtualDesktopIDs", null) is byte[] ids
        ? Enumerable.Range(0, ids.Length / 16).Select(i => new Guid(ids.AsSpan(i * 16, 16))).ToArray()
        : Array.Empty<Guid>();

    static Guid Current() => Registry.GetValue(DesktopsKey, "CurrentVirtualDesktop", null) is byte[] { Length: 16 } id ? new Guid(id) : Guid.Empty;

    static void Switch(int number)
    {
        var desktops = List();
        if (number > desktops.Length || desktops[number - 1] == Current()) return;
        var target = desktops[number - 1];
        manager ??= (IVirtualDesktopManager)new VirtualDesktopManager();
        // The desktop's top app window, as Windows itself would focus there. WM_HOTKEY gave this process the right to activate it.
        IntPtr top = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            if (!WindowTracker.IsAppWindow(h, anyDesktop: true) || IsIconic(h) || WindowTracker.Resolve(h) == null
                || manager.GetWindowDesktopId(h, out var d) != 0 || d != target) return true;
            top = h;
            return false;
        }, IntPtr.Zero);
        if (top == IntPtr.Zero || !SetForegroundWindow(top)) ShowHelperOn(target);
        // The shortcut's Alt or Win is still held; this tap keeps letting go of it from opening a menu or Start.
        keybd_event(0xE8, 0, 0, UIntPtr.Zero);
        keybd_event(0xE8, 0, 2, UIntPtr.Zero); // KEYEVENTF_KEYUP
    }

    /// <summary>An empty desktop has no window to activate, so Mindows moves its own off-screen window there and activates that.</summary>
    static void ShowHelperOn(Guid desktop)
    {
        // Not a tool window: Windows shows those on every desktop, so activating one would switch nothing.
        helper ??= new Window
        {
            Width = 1, Height = 1, Left = -10000, Top = -10000, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false, ShowActivated = false, Title = "",
        };
        helper.Show();
        var h = new WindowInteropHelper(helper).Handle;
        if (manager!.MoveWindowToDesktop(h, ref desktop) != 0) return;
        SetForegroundWindow(h);
        // Hidden once Windows has finished switching. The desktop (shown on every desktop) takes the focus first, as when
        // Windows itself opens an empty desktop: with no foreground window at all, the shortcuts stopped arriving.
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (GetForegroundWindow() == h) SetForegroundWindow(GetShellWindow());
            helper.Hide();
        };
        timer.Start();
    }
}
