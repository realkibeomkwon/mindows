using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using static Mindows.Native;

namespace Mindows;

public partial class DockWindow : Window
{
    // Sizes in DIPs.
    const double Pad = 6, Gap = 5, SepW = 15, BottomMargin = 5, Magnification = 1.75;
    const string TrashKey = "::{645FF040-5081-101B-9F08-00AA002F954E}";

    double Base => config.DockIconSize;
    double Max => config.DockMagnification ? Math.Round(Base * Magnification) : Base;
    double Range => Base * 3.3;
    double ShelfH => Base + 18;
    double WindowH => Max + 56; // room for the magnified icon, the launch bounce and the name label

    sealed class Slot
    {
        public string Key = "", Name = "";
        public bool Pinned, Separator, Trash;
        public List<IntPtr> Windows = new();
        public double Size, Target;
        public Image? Img;
        public Ellipse? Dot;
        public Rectangle? Line;
    }

    readonly Config config;
    readonly AppBar bar;
    readonly Slot separator, trash;
    readonly Slot runningSeparator; // between pinned apps and running unpinned ones, shown only when there are some
    readonly Rectangle resizeHandle;
    List<Slot> slots = new();
    Slot? hovered;
    double mouseX = double.NaN;  // NaN while the pointer is off the dock
    double anchorX = double.NaN; // the dock point under this x stays put while icons grow or shrink
    double? dragStartY;          // screen y where a separator drag started
    double dragStartSize;
    bool animating;

    static DockWindow()
    {
        Debug.Assert(Magnify(0, 48, 84, 160) == 84 && Magnify(160, 48, 84, 160) == 48
            && Magnify(40, 48, 84, 160) > Magnify(80, 48, 84, 160) && Magnify(80, 48, 84, 160) > 48
            && Magnify(0, 48, 48, 160) == 48,
            "magnification must peak under the pointer, fall off to the base size, and stay flat when off");
    }

    public DockWindow(Config config)
    {
        this.config = config;
        InitializeComponent();
        bar = new AppBar(this, AppBar.Bottom, () => BottomMargin + ShelfH + 3, () => WindowH);

        Slot NewSeparator()
        {
            var s = new Slot { Separator = true, Line = new Rectangle() };
            s.Line.SetResourceReference(Shape.FillProperty, "MenuBorder");
            Root.Children.Add(s.Line);
            return s;
        }
        separator = NewSeparator();
        runningSeparator = NewSeparator();
        trash = NewSlot(TrashKey, "휴지통", IconLoader.Get(TrashKey));
        trash.Trash = true;

        // As on macOS: drag the separator up or down to resize, right-click it or the shelf for options.
        resizeHandle = new Rectangle { Fill = Brushes.Transparent, Cursor = Cursors.SizeNS };
        Root.Children.Add(resizeHandle);
        resizeHandle.MouseLeftButtonDown += (_, e) =>
        {
            (dragStartY, dragStartSize) = (ScreenY(), Base);
            resizeHandle.CaptureMouse();
            e.Handled = true;
        };
        resizeHandle.MouseMove += (_, _) =>
        {
            if (dragStartY is { } y0) SetIconSize(dragStartSize + y0 - ScreenY(), commit: false);
        };
        resizeHandle.MouseLeftButtonUp += (_, _) =>
        {
            if (dragStartY == null) return;
            dragStartY = null;
            resizeHandle.ReleaseMouseCapture();
            SetIconSize(Base, commit: true);
        };
        resizeHandle.MouseRightButtonUp += (_, e) => { ShowDockMenu(); e.Handled = true; };
        Shelf.MouseRightButtonUp += (_, e) => { ShowDockMenu(); e.Handled = true; };

        MouseMove += (_, e) =>
        {
            if (dragStartY != null) return;
            anchorX = mouseX = e.GetPosition(Root).X;
            HoverZone.Visibility = Visibility.Visible;
            Retarget();
        };
        MouseLeave += (_, _) =>
        {
            mouseX = double.NaN;
            hovered = null;
            HoverZone.Visibility = Visibility.Collapsed;
            Retarget();
        };
        SizeChanged += (_, _) => Layout();
        Loaded += (_, _) => Refresh();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => Refresh();
        timer.Start();
    }

    static double Magnify(double distance, double size, double max, double range) =>
        distance >= range ? size : size + (max - size) * (Math.Cos(Math.PI * distance / range) + 1) / 2;

    double ScreenY() => PointToScreen(Mouse.GetPosition(this)).Y / VisualTreeHelper.GetDpi(this).DpiScaleY;

    /// <summary>Resizes live while dragging; <paramref name="commit"/> also re-reserves the screen strip and saves.</summary>
    void SetIconSize(double size, bool commit)
    {
        config.DockIconSize = Math.Round(Math.Clamp(size, Config.MinIconSize, Config.MaxIconSize));
        foreach (var s in slots) s.Size = s.Target = Base;
        if (commit)
        {
            bar.Position();
            config.Save();
        }
        else bar.Resize();
        Layout();
    }

    void ShowDockMenu()
    {
        var items = new List<Control?>
        {
            Menus.Item("확대 효과", () =>
            {
                config.DockMagnification = !config.DockMagnification;
                config.Save();
                bar.Resize();
                Retarget();
            }, isChecked: config.DockMagnification),
            null,
        };
        foreach (var (name, size) in new[] { ("작게", 36.0), ("보통", 48.0), ("크게", 64.0), ("아주 크게", 80.0) })
            items.Add(Menus.Item($"크기: {name}", () => SetIconSize(size, commit: true), isChecked: Base == size));
        Menus.Show(Shelf, PlacementMode.MousePoint, items);
    }

    static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    double BaseWidth(Slot s) => s.Separator ? SepW : Base + Gap * 2;
    static double SlotWidth(Slot s) => s.Separator ? SepW : s.Size + Gap * 2;

    Slot NewSlot(string key, string name, ImageSource? icon)
    {
        var s = new Slot
        {
            Key = key, Name = name, Size = Base, Target = Base,
            Img = new Image { Source = icon, RenderTransform = new TranslateTransform() },
        };
        RenderOptions.SetBitmapScalingMode(s.Img, BitmapScalingMode.HighQuality);
        s.Img.MouseLeftButtonUp += (_, _) => Click(s);
        s.Img.MouseRightButtonUp += (_, e) => { ShowMenu(s); e.Handled = true; };
        s.Img.MouseEnter += (_, _) => { hovered = s; Layout(); };
        s.Img.MouseLeave += (_, _) => { if (hovered == s) hovered = null; Layout(); };
        s.Dot = new Ellipse { Width = 4, Height = 4, Visibility = Visibility.Hidden };
        s.Dot.SetResourceReference(Shape.FillProperty, "MenuFg");
        Root.Children.Add(s.Img);
        Root.Children.Add(s.Dot);
        return s;
    }

    /// <summary>Existing slot for the key, or a new one; null when the app has no icon (e.g. uninstalled).</summary>
    Slot? SlotFor(string key, string name, bool pinned, string? exe = null)
    {
        var s = slots.FirstOrDefault(x => Same(x.Key, key));
        if (s == null)
        {
            var icon = IconLoader.Get(key) ?? (exe != null ? IconLoader.Get(exe) : null);
            if (icon == null) return null;
            s = NewSlot(key, name, icon);
        }
        (s.Name, s.Pinned) = (name, pinned);
        return s;
    }

    void Refresh()
    {
        var windows = WindowTracker.List();
        var wanted = new List<Slot>();
        foreach (var p in config.Pinned)
            if (!wanted.Any(s => Same(s.Key, p.Path)) && SlotFor(p.Path, p.Name, true) is { } s) wanted.Add(s);
        var pinnedCount = wanted.Count;
        // The window list is in z-order; as on macOS, running apps keep their spot and newly launched ones join at the right end.
        int Spot(AppWindow w) => slots.FindIndex(s => Same(s.Key, w.App.Key)) is var i and >= 0 ? i : int.MaxValue;
        foreach (var w in windows.OrderBy(Spot))
            if (!wanted.Any(s => Same(s.Key, w.App.Key)) && SlotFor(w.App.Key, w.App.Name, false, w.App.ExePath) is { } s) wanted.Add(s);
        if (wanted.Count > pinnedCount) wanted.Insert(pinnedCount, runningSeparator);
        runningSeparator.Line!.Visibility = wanted.Contains(runningSeparator) ? Visibility.Visible : Visibility.Collapsed;
        if (trash.Img?.Source != null) wanted.AddRange(new[] { separator, trash });

        foreach (var gone in slots.Except(wanted).Where(s => !s.Separator))
        {
            Root.Children.Remove(gone.Img);
            Root.Children.Remove(gone.Dot);
            if (gone == hovered) hovered = null;
        }
        var changed = !slots.SequenceEqual(wanted);
        slots = wanted;
        foreach (var s in slots.Where(s => !s.Separator && !s.Trash))
        {
            s.Windows = windows.Where(w => Same(w.App.Key, s.Key)).Select(w => w.Hwnd).ToList();
            s.Dot!.Visibility = s.Windows.Count > 0 ? Visibility.Visible : Visibility.Hidden;
        }
        if (changed) Retarget();
    }

    void Retarget()
    {
        var x = (Root.ActualWidth - (Pad * 2 + slots.Sum(BaseWidth))) / 2 + Pad;
        foreach (var s in slots)
        {
            var w = BaseWidth(s);
            s.Target = s.Separator || double.IsNaN(mouseX) ? Base : Magnify(Math.Abs(mouseX - (x + w / 2)), Base, Max, Range);
            x += w;
        }
        if (animating) return;
        animating = true;
        CompositionTarget.Rendering += Animate;
    }

    void Animate(object? sender, EventArgs e)
    {
        var done = true;
        foreach (var s in slots)
        {
            var d = s.Target - s.Size;
            if (Math.Abs(d) < 0.25) s.Size = s.Target;
            else { s.Size += d * 0.3; done = false; }
        }
        Layout();
        if (!done) return;
        CompositionTarget.Rendering -= Animate;
        animating = false;
    }

    /// <summary>Left edge of the dock: centered at rest, and keeping the point under the pointer fixed while magnified.</summary>
    double LeftEdge(double width)
    {
        var baseLeft = (width - (Pad * 2 + slots.Sum(BaseWidth))) / 2;
        if (double.IsNaN(anchorX)) return baseLeft;
        var offset = anchorX - baseLeft;
        double b = Pad, m = Pad; // running right ends of the base and the magnified layout
        if (offset <= b) return baseLeft;
        foreach (var s in slots)
        {
            double bw = BaseWidth(s), mw = SlotWidth(s);
            if (offset <= b + bw) return anchorX - (m + (offset - b) / bw * mw);
            (b, m) = (b + bw, m + mw);
        }
        return anchorX - (m + offset - b);
    }

    void Layout()
    {
        double width = Root.ActualWidth, height = Root.ActualHeight;
        if (width == 0) return;
        var total = Pad * 2 + slots.Sum(SlotWidth);
        var left = LeftEdge(width);
        var shelfTop = height - BottomMargin - ShelfH;
        Place(Shelf, left, shelfTop, total, ShelfH);
        var zoneTop = shelfTop - (Max - Base) - 4;
        Place(HoverZone, left, zoneTop, total, height - zoneTop);

        var x = left + Pad;
        foreach (var s in slots)
        {
            if (s.Separator)
            {
                Place(s.Line!, x + SepW / 2, shelfTop + 12, 1, ShelfH - 24);
                if (s == separator) Place(resizeHandle, x, shelfTop, SepW, ShelfH);
            }
            else
            {
                Place(s.Img!, x + Gap, height - BottomMargin - 9 - s.Size, s.Size, s.Size);
                Canvas.SetLeft(s.Dot!, x + Gap + s.Size / 2 - 2);
                Canvas.SetTop(s.Dot!, height - BottomMargin - 6);
            }
            x += SlotWidth(s);
        }

        if (hovered?.Img is { } img)
        {
            LabelText.Text = hovered.Name;
            Label.Visibility = Visibility.Visible;
            Label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(Label, Canvas.GetLeft(img) + hovered.Size / 2 - Label.DesiredSize.Width / 2);
            Canvas.SetTop(Label, Canvas.GetTop(img) - Label.DesiredSize.Height - 8);
        }
        else Label.Visibility = Visibility.Collapsed;
    }

    static void Place(FrameworkElement e, double x, double y, double width, double height)
    {
        Canvas.SetLeft(e, x);
        Canvas.SetTop(e, y);
        e.Width = width;
        e.Height = height;
    }

    void Click(Slot s)
    {
        if (s.Trash) Sys.Launch("shell:RecycleBinFolder");
        else if (s.Windows.Count == 0)
        {
            Sys.Launch(s.Key);
            Bounce(s);
        }
        // Already in front: cycle to its bottom-most window.
        else Sys.Focus(s.Windows.Contains(GetForegroundWindow()) ? s.Windows[^1] : s.Windows[0]);
    }

    static void Bounce(Slot s)
    {
        var bounce = new DoubleAnimation(0, -22, TimeSpan.FromMilliseconds(280))
        {
            AutoReverse = true,
            RepeatBehavior = new RepeatBehavior(3),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        s.Img!.RenderTransform.BeginAnimation(TranslateTransform.YProperty, bounce);
    }

    void ShowMenu(Slot s)
    {
        var items = new List<Control?>();
        if (s.Trash)
        {
            items.Add(Menus.Item("열기", () => Sys.Launch("shell:RecycleBinFolder")));
            items.Add(Menus.Item("휴지통 비우기...", () => SHEmptyRecycleBin(new WindowInteropHelper(this).Handle, null, 0)));
        }
        else
        {
            foreach (var h in s.Windows) items.Add(Menus.Item(WindowText(h), () => Sys.Focus(h)));
            if (s.Windows.Count > 0) items.Add(null);
            items.Add(Menus.Item("Dock에 유지", () => TogglePin(s), isChecked: s.Pinned));
            if (!s.Key.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
                items.Add(Menus.Item("파일 위치 열기", () => Process.Start("explorer.exe", $"/select,\"{s.Key}\"")));
            items.Add(null);
            items.Add(s.Windows.Count > 0
                ? Menus.Item("종료", () => s.Windows.ForEach(Sys.Close))
                : Menus.Item("열기", () => Click(s)));
        }
        Menus.Show(s.Img!, PlacementMode.Top, items);
    }

    void TogglePin(Slot s)
    {
        if (s.Pinned) config.Pinned.RemoveAll(p => Same(p.Path, s.Key));
        else config.Pinned.Add(new() { Name = s.Name, Path = s.Key });
        config.Save();
        Refresh();
    }
}
