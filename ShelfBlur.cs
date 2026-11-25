using System.Numerics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using Windows.Graphics.Effects;
using Windows.UI.Composition.Desktop;
using WinRT;
using static Mindows.Native;
using Comp = Windows.UI.Composition;

namespace Mindows;

/// <summary>
/// Blurs and saturates the desktop behind the dock's shelf. The dock window spans the screen width and draws magnified
/// icons above the shelf, so the glass lives in a click-through window of its own that follows the shelf just beneath
/// the dock. DWM's own blur has a fixed radius and no saturation, so the glass is a Composition effect over the
/// backdrop instead.
/// </summary>
sealed class ShelfBlur
{
    const float BlurSigma = 4, Saturate = 1.5f; // device pixels; 1 leaves the colours as they are

    static readonly Guid GaussianBlur = new("1FEB6D69-2FE6-4AC9-8C58-1D7F93E7A6A5"), Saturation = new("5CB2D9CF-327D-459F-A0CE-40C0B2086BF7");

    [StructLayout(LayoutKind.Sequential)]
    struct QueueOptions { public int Size, ThreadType, ApartmentType; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr CreateWindowEx(uint ex, string cls, string? title, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr h);
    [DllImport("CoreMessaging.dll")] static extern int CreateDispatcherQueueController(QueueOptions options, out IntPtr controller);

    [ComImport, Guid("29E691FA-4567-4DCA-B319-D0F207EB6807"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ICompositorDesktopInterop { void CreateDesktopWindowTarget(IntPtr hwnd, bool topmost, out IntPtr target); }

    [ComImport, Guid("2FC57384-A068-44D7-A331-30982FCF7177"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IGraphicsEffectD2D1Interop
    {
        [PreserveSig] int GetEffectId(out Guid id);
        [PreserveSig] int GetNamedPropertyMapping(IntPtr name, out uint index, out int mapping);
        [PreserveSig] int GetPropertyCount(out uint count);
        [PreserveSig] int GetProperty(uint index, out IntPtr value);
        [PreserveSig] int GetSource(uint index, out IntPtr source);
        [PreserveSig] int GetSourceCount(out uint count);
    }

    /// <summary>Describes one Direct2D effect to the compositor, which is the job Win2D does in a UWP app.</summary>
    sealed class Effect(Guid id, IGraphicsEffectSource source, params object[] properties) : IGraphicsEffect, IGraphicsEffectD2D1Interop, ICustomQueryInterface
    {
        static readonly Guid PropertyValue = new("4BD682DD-7554-40E9-9A9B-82654EDE7E62");

        public string Name { get; set; } = "";

        public int GetEffectId(out Guid effect) { effect = id; return 0; }
        public int GetNamedPropertyMapping(IntPtr name, out uint index, out int mapping) { (index, mapping) = (0, 0); return unchecked((int)0x80004001); } // E_NOTIMPL: nothing is animated
        public int GetPropertyCount(out uint count) { count = (uint)properties.Length; return 0; }
        public int GetSourceCount(out uint count) { count = 1; return 0; }
        public int GetSource(uint index, out IntPtr value) { value = MarshalInterface<IGraphicsEffectSource>.FromManaged(source); return 0; }

        public int GetProperty(uint index, out IntPtr value)
        {
            var boxed = MarshalInspectable<object>.FromManaged(properties[index]);
            var hr = Marshal.QueryInterface(boxed, in PropertyValue, out value);
            Marshal.Release(boxed);
            return hr;
        }

        // The WinRT wrapper of this object only knows its WinRT interfaces; the classic COM one is handed out here.
        public CustomQueryInterfaceResult GetInterface(ref Guid iid, out IntPtr ppv)
        {
            ppv = IntPtr.Zero;
            if (iid != typeof(IGraphicsEffectD2D1Interop).GUID) return CustomQueryInterfaceResult.NotHandled;
            ppv = Marshal.GetComInterfaceForObject(this, typeof(IGraphicsEffectD2D1Interop), CustomQueryInterfaceMode.Ignore);
            return CustomQueryInterfaceResult.Handled;
        }
    }

    static IntPtr queue;

    readonly Window dock;
    readonly Visual root;
    Comp.Compositor? compositor;
    DesktopWindowTarget? target; // kept alive: the glass goes away with it
    Comp.CompositionRoundedRectangleGeometry? clip;
    IntPtr hwnd;
    (double Left, double Top, double Width, double Height, double Radius) shelf;
    (int W, int H, int R) applied;

    public ShelfBlur(Window dock, Visual root)
    {
        (this.dock, this.root) = (dock, root);
        dock.IsVisibleChanged += (_, _) => Apply(); // the dock hides itself for full-screen apps and the lock screen
        dock.Closed += (_, _) => DestroyWindow(hwnd);
        UserPreferenceChangedEventHandler onPrefs = (_, _) => dock.Dispatcher.BeginInvoke(Apply);
        SystemEvents.UserPreferenceChanged += onPrefs;
        dock.Closed += (_, _) => SystemEvents.UserPreferenceChanged -= onPrefs;

        // After sleep or a lock the backdrop can stay opaque white until DWM recomposes, so the glass is built anew.
        PowerModeChangedEventHandler onPower = (_, e) => { if (e.Mode == PowerModes.Resume) dock.Dispatcher.BeginInvoke(Rebuild); };
        SessionSwitchEventHandler onSession = (_, e) => { if (e.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.SessionLogon) dock.Dispatcher.BeginInvoke(Rebuild); };
        SystemEvents.PowerModeChanged += onPower;
        SystemEvents.SessionSwitch += onSession;
        dock.Closed += (_, _) => { SystemEvents.PowerModeChanged -= onPower; SystemEvents.SessionSwitch -= onSession; };
    }

    void Rebuild()
    {
        if (hwnd == IntPtr.Zero) return;
        DestroyWindow(hwnd);
        (hwnd, applied) = (IntPtr.Zero, default);
        Apply();
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
            ShowWindow(hwnd, SW_HIDE);
            return;
        }
        if (hwnd == IntPtr.Zero) Init();

        var scale = VisualTreeHelper.GetDpi(dock).DpiScaleX;
        var p = ((Visual)root).PointToScreen(new Point(shelf.Left, shelf.Top));
        // Inset by a pixel so the shelf's anti-aliased border covers the region's jagged edge.
        var (x, y) = ((int)Math.Round(p.X) + 1, (int)Math.Round(p.Y) + 1);
        var size = ((int)Math.Round(shelf.Width * scale) - 2, (int)Math.Round(shelf.Height * scale) - 2, (int)Math.Round(shelf.Radius * scale) - 1);
        var resized = size != applied;
        if (!IsWindowVisible(hwnd)) ShowWindow(hwnd, 4); // SW_SHOWNOACTIVATE
        // Re-inserted under the dock every time: the dock re-asserts HWND_TOPMOST on resize.
        SetWindowPos(hwnd, dockHwnd, x, y, size.Item1, size.Item2, SWP_NOACTIVATE | (resized ? 0 : SWP_NOSIZE));
        if (!resized) return;
        applied = (size.Item1, size.Item2, size.Item3);
        clip!.Size = new Vector2(size.Item1, size.Item2);
        clip.CornerRadius = new Vector2(size.Item3);
    }

    void Init()
    {
        // A bare window, not a WPF one: WPF already composes its windows, which rules out a Composition target on them.
        // The Static class answers hit tests with HTTRANSPARENT, and without a redirection bitmap only the glass shows.
        // Tool window and no-activate: it must not appear as a running app in the dock, nor take the user's focus.
        const long WS_EX_NOREDIRECTIONBITMAP = 0x200000, WS_EX_TOPMOST = 8;
        hwnd = CreateWindowEx((uint)(WS_EX_NOREDIRECTIONBITMAP | WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE), "Static", null,
            0x80000000, -20000, -20000, 10, 10, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero); // WS_POPUP

        // A compositor needs a dispatcher queue on its thread; WPF does not create one.
        if (queue == IntPtr.Zero) CreateDispatcherQueueController(new QueueOptions { Size = 12, ThreadType = 2, ApartmentType = 2 }, out queue); // DQTYPE_THREAD_CURRENT, DQTAT_COM_STA
        compositor = new Comp.Compositor();
        compositor.As<ICompositorDesktopInterop>().CreateDesktopWindowTarget(hwnd, true, out var raw);
        target = DesktopWindowTarget.FromAbi(raw);
        Marshal.Release(raw);

        // Optimization balanced (1) and a hard border (1), so the blur does not fade to transparent at the shelf's edge.
        var blur = new Effect(GaussianBlur, new Comp.CompositionEffectSourceParameter("backdrop"), BlurSigma, 1u, 1u);
        var brush = compositor.CreateEffectFactory(new Effect(Saturation, blur, Saturate)).CreateBrush();
        // Not the host backdrop brush: DWM hands that one out already blurred, at low resolution and off by the DPI scale.
        // In a window without a redirection bitmap the plain backdrop brush samples the sharp desktop behind the window.
        // The glass stays flat: Composition will not bend a backdrop (no displacement map, a transform effect on it is
        // refused or ignored, a visual surface leaves it out, and a scaled visual still samples it in screen space).
        brush.SetSourceParameter("backdrop", compositor.CreateBackdropBrush());
        var glass = compositor.CreateSpriteVisual();
        glass.RelativeSizeAdjustment = Vector2.One;
        glass.Brush = brush;
        clip = compositor.CreateRoundedRectangleGeometry();
        glass.Clip = compositor.CreateGeometricClip(clip);
        target.Root = glass;

        // shortcut: the backdrop seems to be re-sampled only while this tree changes, so an endless imperceptible fade keeps
        // DWM recomposing the glass; costs a little GPU, replace if the cause turns out to be the z-order against the dock.
        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0.5f, 0.999f);
        fade.InsertKeyFrame(1f, 1f);
        fade.Duration = TimeSpan.FromSeconds(1);
        fade.IterationBehavior = Comp.AnimationIterationBehavior.Forever;
        glass.StartAnimation("Opacity", fade);
    }
}
