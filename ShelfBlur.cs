using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using static Mindows.Native;

namespace Mindows;

/// <summary>
/// Blurs the desktop behind the dock's shelf. The dock window spans the screen width and draws magnified icons above
/// the shelf, so the blur lives in a click-through window of its own that follows the shelf just beneath the dock.
/// </summary>
sealed class ShelfBlur
{
    [StructLayout(LayoutKind.Sequential)]
    struct BlurBehind { public uint Flags; public int Enable; public IntPtr Region; public int Transition; }

    [DllImport("dwmapi.dll")] static extern int DwmEnableBlurBehindWindow(IntPtr h, ref BlurBehind bb);

    readonly Window dock, win;
    readonly Visual root;
    IntPtr hwnd;
    (double Left, double Top, double Width, double Height, double Radius) shelf;
    (int W, int H, int R) applied;

    public ShelfBlur(Window dock, Visual root)
    {
        (this.dock, this.root) = (dock, root);
        win = new Window
        {
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, ShowActivated = false,
            Topmost = true, Background = Brushes.Transparent, Left = -20000, Top = -20000, Width = 10, Height = 10,
        };
        dock.IsVisibleChanged += (_, _) => Apply(); // the dock hides itself for full-screen apps and the lock screen
        dock.Closed += (_, _) => win.Close();
        UserPreferenceChangedEventHandler onPrefs = (_, _) => dock.Dispatcher.BeginInvoke(Apply);
        SystemEvents.UserPreferenceChanged += onPrefs;
        dock.Closed += (_, _) => SystemEvents.UserPreferenceChanged -= onPrefs;
    }

    /// <summary>Follows the shelf, given in the dock's DIPs; also called while the dock slides, so it keeps the last shelf.</summary>
    public void Sync(double left, double top, double width, double height, double radius)
    {
        shelf = (left, top, width, height, radius);
        Apply();
    }

    public void Apply()
    {
        var dockHwnd = new WindowInteropHelper(dock).Handle;
        if (dockHwnd == IntPtr.Zero || shelf.Width == 0) return;
        if (!dock.IsVisible || !SettingsWindow.WindowsTransparency())
        {
            if (win.IsVisible) win.Hide();
            return;
        }
        if (hwnd == IntPtr.Zero) Init();

        var scale = VisualTreeHelper.GetDpi(dock).DpiScaleX;
        var p = ((Visual)root).PointToScreen(new Point(shelf.Left, shelf.Top));
        // Inset by a pixel so the shelf's anti-aliased border covers the region's jagged edge.
        var (x, y) = ((int)Math.Round(p.X) + 1, (int)Math.Round(p.Y) + 1);
        var size = ((int)Math.Round(shelf.Width * scale) - 2, (int)Math.Round(shelf.Height * scale) - 2, (int)Math.Round(shelf.Radius * scale) - 1);
        var resized = size != applied;
        if (!win.IsVisible) win.Show();
        // Re-inserted under the dock every time: the dock re-asserts HWND_TOPMOST on resize.
        SetWindowPos(hwnd, dockHwnd, x, y, size.Item1, size.Item2, SWP_NOACTIVATE | (resized ? 0 : SWP_NOSIZE));
        if (!resized) return;
        applied = (size.Item1, size.Item2, size.Item3);
        // The accent-policy blur ignores window regions and blurs the whole rectangle; only DWM's blur region follows the
        // rounded shelf. The ellipse arguments are diameters, and DWM copies the region, so it is deleted afterwards.
        var region = CreateRoundRectRgn(0, 0, size.Item1 + 1, size.Item2 + 1, size.Item3 * 2, size.Item3 * 2);
        var bb = new BlurBehind { Flags = 3, Enable = 1, Region = region }; // DWM_BB_ENABLE | DWM_BB_BLURREGION
        DwmEnableBlurBehindWindow(hwnd, ref bb);
        DeleteObject(region);
    }

    void Init()
    {
        hwnd = new WindowInteropHelper(win).EnsureHandle();
        // Tool window and no-activate: it must not appear as a running app in the dock, nor take the user's focus.
        var ex = (long)GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, (IntPtr)(ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE));
        var source = HwndSource.FromHwnd(hwnd);
        source.CompositionTarget.BackgroundColor = Colors.Transparent;
        source.AddHook((IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled) =>
        {
            if (msg != 0x84) return IntPtr.Zero; // WM_NCHITTEST: clicks fall through to what is beneath
            handled = true;
            return (IntPtr)(-1); // HTTRANSPARENT
        });
    }
}
