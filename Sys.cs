using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Runtime.InteropServices;
using System.Windows.Controls.Primitives;
using static Mindows.Native;

namespace Mindows;

/// <summary>Actions shared by the menu bar and the dock.</summary>
static class Sys
{
    public static void Launch(string path, string args = "", string? verb = null)
    {
        try
        {
            if (LaunchViaExplorer(path, args, verb)) return;
            if (path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) || path.StartsWith("::"))
                Process.Start("explorer.exe", path);
            else
                Process.Start(new ProcessStartInfo(path, args) { UseShellExecute = true, Verb = verb ?? "" });
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            MessageBox.Show($"{path}\n\n{e.Message}", "Mindows", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// Has Explorer start the app through its desktop view. A child of this process would inherit the uiAccess token's
    /// raised integrity, and dock-launched apps must run as a normal user. Returns false when Explorer's desktop is not available.
    /// </summary>
    static bool LaunchViaExplorer(string path, string args, string? verb)
    {
        try
        {
            dynamic windows = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39"))!)!; // ShellWindows
            object location = 0 /* CSIDL_DESKTOP */, root = Type.Missing;
            dynamic desktop = windows.FindWindowSW(ref location, ref root, 8 /* SWC_DESKTOP */, out int _, 1 /* SWFO_NEEDDISPATCH */);
            dynamic shell = desktop.Document.Application;
            GetWindowThreadProcessId(GetShellWindow(), out var pid);
            AllowSetForegroundWindow(pid); // the app's window would otherwise open behind
            shell.ShellExecute(path, args, null, verb, 1 /* SW_SHOWNORMAL */);
            return true;
        }
        catch (Exception) // COM failures surface as many exception types; the direct launch is the fallback
        {
            return false;
        }
    }

    public static void Run(string exe, string args) =>
        Process.Start(new ProcessStartInfo(exe, args) { CreateNoWindow = true, UseShellExecute = false });

    public static void Focus(IntPtr h)
    {
        if (IsIconic(h)) ShowWindow(h, SW_RESTORE);
        SetForegroundWindow(h);
    }

    public static void Close(IntPtr h) => PostMessage(h, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);

    /// <summary>Presses Win+key, e.g. N for notifications, A for quick settings, S for search.</summary>
    public static void WinKey(char key)
    {
        const byte LWin = 0x5B;
        const uint Up = 2;
        keybd_event(LWin, 0, 0, UIntPtr.Zero);
        keybd_event((byte)key, 0, 0, UIntPtr.Zero);
        keybd_event((byte)key, 0, Up, UIntPtr.Zero);
        keybd_event(LWin, 0, Up, UIntPtr.Zero);
    }

    public static bool Confirm(string text) =>
        MessageBox.Show(text, "Mindows", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
}

static class Menus
{
    public const double ShadowMargin = 28; // keep equal to the ContextMenu template's outer margin in App.xaml

    public static MenuItem Item(string header, Action onClick, bool enabled = true, bool? isChecked = null, string? shortcut = null)
    {
        // Tag marks items that have a check state, so Show can reserve the check column for the whole menu.
        var item = new MenuItem { Header = header, IsEnabled = enabled, IsChecked = isChecked == true, InputGestureText = shortcut, Tag = isChecked == null ? null : "check" };
        item.Click += (_, _) => onClick();
        return item;
    }

    /// <summary>Opens a menu at <paramref name="target"/>; null entries become separators.</summary>
    public static void Show(UIElement target, PlacementMode placement, IEnumerable<Control?> items)
    {
        // The template keeps ShadowMargin of transparent space around the menu for its shadow; offset it away so the visible edge meets the target.
        var menu = new ContextMenu { PlacementTarget = target, Placement = placement, HorizontalOffset = -ShadowMargin };
        menu.VerticalOffset = placement == PlacementMode.Top ? ShadowMargin : -ShadowMargin;
        foreach (var item in items) menu.Items.Add(item ?? new Separator());
        // macOS aligns every title right of a shared state column when any item can be checked.
        var entries = menu.Items.OfType<MenuItem>().ToList();
        if (entries.Any(i => i.Tag is "check"))
            entries.ForEach(i => i.Padding = new Thickness(26, 0, 12, 0));

        menu.IsOpen = true;
        WatchForDismiss(menu);
    }

    // The bars are WS_EX_NOACTIVATE, so Windows sends an open menu neither keys (Esc) nor clicks on other
    // apps' windows, and WPF never closes it. Activating the menu instead makes WPF close it at once
    // (it loses capture/focus), so watch input globally while it is open.
    static IntPtr mouseHook, keyHook;
    static HookProc? mouseProc, keyProc; // held so the GC keeps the native callbacks alive
    static ContextMenu? watched;
    public static bool IsOpen => watched != null;

    static void WatchForDismiss(ContextMenu menu)
    {
        Unhook();
        watched = menu;
        void Dismiss() => menu.Dispatcher.BeginInvoke(() => menu.IsOpen = false);
        mouseProc = (code, msg, data) =>
        {
            // WM_LBUTTONDOWN, WM_RBUTTONDOWN, WM_MBUTTONDOWN; clicks on our own windows reach the menu normally.
            if (code >= 0 && msg is 0x201 or 0x204 or 0x207)
            {
                long x = Marshal.ReadInt32(data), y = Marshal.ReadInt32(data, 4);
                GetWindowThreadProcessId(WindowFromPoint((y << 32) | (uint)x), out var pid);
                if (pid != Environment.ProcessId) Dismiss();
            }
            return CallNextHookEx(IntPtr.Zero, code, msg, data);
        };
        keyProc = (code, msg, data) =>
        {
            if (code >= 0 && msg == 0x100 && Marshal.ReadInt32(data) == 0x1B) // WM_KEYDOWN, VK_ESCAPE
            {
                Dismiss();
                return 1; // swallow it so the app behind the menu doesn't also act on Esc
            }
            return CallNextHookEx(IntPtr.Zero, code, msg, data);
        };
        var module = GetModuleHandle(null);
        mouseHook = SetWindowsHookEx(14, mouseProc, module, 0); // WH_MOUSE_LL
        keyHook = SetWindowsHookEx(13, keyProc, module, 0);     // WH_KEYBOARD_LL
        // A late Closed from the previous menu must not unhook the one just opened.
        menu.Closed += (_, _) => { if (watched == menu) Unhook(); };
    }

    static void Unhook()
    {
        if (mouseHook != IntPtr.Zero) UnhookWindowsHookEx(mouseHook);
        if (keyHook != IntPtr.Zero) UnhookWindowsHookEx(keyHook);
        mouseHook = keyHook = IntPtr.Zero;
        watched = null;
    }
}
