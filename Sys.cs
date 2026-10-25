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
    public static void Launch(string path)
    {
        try
        {
            if (path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) || path.StartsWith("::"))
                Process.Start("explorer.exe", path);
            else
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            MessageBox.Show($"{path}\n\n{e.Message}", "Mindows", MessageBoxButton.OK, MessageBoxImage.Warning);
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
    public static MenuItem Item(string header, Action onClick, bool enabled = true, bool isChecked = false)
    {
        var item = new MenuItem { Header = header, IsEnabled = enabled, IsChecked = isChecked };
        item.Click += (_, _) => onClick();
        return item;
    }

    /// <summary>Opens a menu at <paramref name="target"/>; null entries become separators.</summary>
    public static void Show(UIElement target, PlacementMode placement, IEnumerable<Control?> items)
    {
        var menu = new ContextMenu { PlacementTarget = target, Placement = placement };
        foreach (var item in items) menu.Items.Add(item ?? new Separator());

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
