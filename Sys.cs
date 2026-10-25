using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
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
    }
}
