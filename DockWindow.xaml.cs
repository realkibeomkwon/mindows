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
        public double ShiftX, ShiftY; // drawn offset from the laid-out spot, eased to 0 so moves slide
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
    Slot? pressed, dragged;      // icon under the left button; set as dragged once it moves past the drag threshold
    Point pressAt, grab;         // press point in Root, and in the icon
    Point ghostAt;               // dragged icon's top-left in Root
    int homeIndex;
    bool removing;               // a pinned icon dragged off the dock: drop removes it, its gap closes
    double leftShift;
    bool animating;
    bool hidden;                 // auto-hide has slid the dock below the screen edge
    readonly DispatcherTimer hideTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };

    static DockWindow()
    {
        Debug.Assert(Magnify(0, 48, 84, 160) == 84 && Magnify(160, 48, 84, 160) == 48
            && Magnify(40, 48, 84, 160) > Magnify(80, 48, 84, 160) && Magnify(80, 48, 84, 160) > 48
            && Magnify(0, 48, 48, 160) == 48,
            "magnification must peak under the pointer, fall off to the base size, and stay flat when off");
        var pinned = new[] { "a", "hidden", "b", "c" }.Select(p => new PinnedApp { Path = p }).ToList();
        PinBefore(pinned, "c", "", "b");
        PinBefore(pinned, "a", "", null);
        PinBefore(pinned, "new", "", "c");
        Debug.Assert(string.Join(",", pinned.Select(p => p.Path)) == "hidden,new,c,b,a",
            "a dragged icon must land before its new neighbor without moving entries the dock can't show");
    }

    public DockWindow(Config config)
    {
        this.config = config;
        InitializeComponent();
        // Auto-hide reserves nothing, so maximized windows reach the bottom of the screen.
        bar = new AppBar(this, AppBar.Bottom, () => config.DockAutoHide ? 0 : BottomMargin + ShelfH + 3, () => WindowH);

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
            var p = e.GetPosition(Root);
            if (pressed != null && !DragTo(p)) return;
            anchorX = mouseX = p.X;
            HoverZone.Visibility = Visibility.Visible;
            Retarget();
        };
        MouseLeave += (_, _) =>
        {
            mouseX = double.NaN;
            hovered = null;
            HoverZone.Visibility = Visibility.Collapsed;
            Retarget();
            if (!config.DockAutoHide) return;
            hideTimer.Start();
            TryHide(); // the timer retries while a drag or an open menu keeps the dock up
        };
        EdgeTrigger.MouseEnter += (_, _) => { if (hidden) Slide(hide: false); };
        hideTimer.Tick += (_, _) => TryHide();
        ApplyAutoHide();
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
    public void SetIconSize(double size, bool commit)
    {
        config.DockIconSize = Math.Round(Math.Clamp(size, Config.MinIconSize, Config.MaxIconSize));
        foreach (var s in slots) s.Size = s.Target = Base;
        if (commit)
        {
            bar.Position();
            config.Save();
        }
        else bar.Resize();
        if (hidden) Slide(hide: true); // the hidden offset follows the shelf height
        Layout();
    }

    public void SetMagnification(bool on)
    {
        config.DockMagnification = on;
        config.Save();
        bar.Resize();
        Retarget();
    }

    public void SetAutoHide(bool on)
    {
        config.DockAutoHide = on;
        config.Save();
        bar.Position();
        ApplyAutoHide();
    }

    void ApplyAutoHide()
    {
        EdgeTrigger.Visibility = config.DockAutoHide ? Visibility.Visible : Visibility.Collapsed;
        hideTimer.Stop();
        Slide(hide: config.DockAutoHide && !IsMouseOver);
    }

    void TryHide()
    {
        if (IsMouseOver || Mouse.Captured != null || Menus.IsOpen) return; // a drag or an open menu keeps the dock up
        hideTimer.Stop();
        Slide(hide: true);
    }

    void Slide(bool hide)
    {
        hidden = hide;
        var duration = TimeSpan.FromMilliseconds(config.DockAutoHideAnimation ? 250 : 0);
        var slide = new DoubleAnimation(hide ? BottomMargin + ShelfH + 2 : 0, duration)
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        Root.RenderTransform.BeginAnimation(TranslateTransform.YProperty, slide);
    }

    void ShowDockMenu()
    {
        var items = new List<Control?>
        {
            Menus.Item("확대 효과", () => SetMagnification(!config.DockMagnification), isChecked: config.DockMagnification),
            null,
        };
        foreach (var (name, size) in new[] { ("작게", 36.0), ("보통", 48.0), ("크게", 64.0), ("아주 크게", 80.0) })
            items.Add(Menus.Item($"크기: {name}", () => SetIconSize(size, commit: true), isChecked: Base == size));
        Menus.Show(Shelf, PlacementMode.MousePoint, items);
    }

    static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    double BaseWidth(Slot s) => s.Separator ? SepW : removing && s == dragged ? 0 : Base + Gap * 2;
    // The gap shrinks with an icon collapsing below the base size, so a removed icon's slot closes fully.
    double SlotWidth(Slot s) => s.Separator ? SepW : s.Size + Gap * 2 * Math.Min(1, s.Size / Base);

    Slot NewSlot(string key, string name, ImageSource? icon)
    {
        var s = new Slot
        {
            Key = key, Name = name, Size = Base, Target = Base,
            Img = new Image { Source = icon, RenderTransform = new TranslateTransform() },
        };
        RenderOptions.SetBitmapScalingMode(s.Img, BitmapScalingMode.HighQuality);
        s.Img.MouseLeftButtonDown += (_, e) =>
        {
            (pressed, pressAt, grab) = (s, e.GetPosition(Root), e.GetPosition(s.Img));
            s.Img.CaptureMouse();
        };
        s.Img.MouseLeftButtonUp += (_, _) => EndPress(commit: true);
        s.Img.LostMouseCapture += (_, _) => EndPress(commit: false);
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
        if (dragged != null) return; // the drag owns the order until the drop
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
            s.Target = removing && s == dragged ? 0
                : s.Separator || double.IsNaN(mouseX) ? Base : Magnify(Math.Abs(mouseX - (x + w / 2)), Base, Max, Range);
            x += w;
        }
        if (animating) return;
        animating = true;
        CompositionTarget.Rendering += Animate;
    }

    void Animate(object? sender, EventArgs e)
    {
        var done = true;
        double Ease(double v)
        {
            if (Math.Abs(v) < 0.25) return 0;
            done = false;
            return v * 0.7;
        }
        foreach (var s in slots)
        {
            s.Size = s.Target - Ease(s.Target - s.Size);
            (s.ShiftX, s.ShiftY) = (Ease(s.ShiftX), Ease(s.ShiftY));
        }
        leftShift = Ease(leftShift);
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
        var left = LeftEdge(width) + leftShift;
        var shelfTop = height - BottomMargin - ShelfH;
        Place(Shelf, left, shelfTop, total, ShelfH);
        var zoneTop = shelfTop - (Max - Base) - 4;
        Place(HoverZone, left, zoneTop, total, height - zoneTop);

        var x = left + Pad;
        foreach (var s in slots)
        {
            var sx = x + s.ShiftX;
            if (s.Separator)
            {
                Place(s.Line!, sx + SepW / 2, shelfTop + 12, 1, ShelfH - 24);
                if (s == separator) Place(resizeHandle, sx, shelfTop, SepW, ShelfH);
            }
            else
            {
                var gap = (SlotWidth(s) - s.Size) / 2;
                Place(s.Img!, sx + gap, height - BottomMargin - 9 - s.Size + s.ShiftY, s.Size, s.Size);
                Canvas.SetLeft(s.Dot!, sx + gap + s.Size / 2 - 2);
                Canvas.SetTop(s.Dot!, height - BottomMargin - 6);
            }
            x += SlotWidth(s);
        }

        if (hovered?.Img is { } img && dragged == null)
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

    /// <summary>Moves (or adds) the entry for key to just before next, or to the end; entries the dock can't show keep their place.</summary>
    static void PinBefore(List<PinnedApp> pinned, string key, string name, string? next)
    {
        var entry = pinned.Find(p => Same(p.Path, key)) ?? new() { Name = name, Path = key };
        pinned.Remove(entry);
        var at = next == null ? -1 : pinned.FindIndex(p => Same(p.Path, next));
        pinned.Insert(at < 0 ? pinned.Count : at, entry);
    }

    double X(Slot s) => Canvas.GetLeft(s.Separator ? s.Line! : s.Img!);

    /// <summary>Applies an order or width change, then slides everything over from where it was drawn.</summary>
    void Rearrange(Action change)
    {
        var left = LeftEdge(Root.ActualWidth);
        var before = slots.ToDictionary(s => s, X);
        change();
        leftShift += left - LeftEdge(Root.ActualWidth);
        Layout();
        foreach (var s in slots) s.ShiftX += before[s] - X(s);
        Retarget();
    }

    void MoveTo(Slot s, int index)
    {
        var i = slots.IndexOf(s);
        if (i != index) Rearrange(() => { slots.RemoveAt(i); slots.Insert(index, s); });
    }

    /// <summary>Index for the dragged icon under x, kept among the pinned icons as on macOS.</summary>
    int DropIndex(double px)
    {
        var x = LeftEdge(Root.ActualWidth) + Pad;
        var index = 0;
        foreach (var s in slots)
        {
            var w = SlotWidth(s);
            if (s != dragged && px > x + w / 2) index++;
            x += w;
        }
        var pinned = slots.Count(s => s.Pinned && s != dragged);
        // An unpinned app stays put until it is brought into the pinned icons.
        return index <= pinned ? index : dragged!.Pinned ? pinned : homeIndex;
    }

    /// <summary>Moves the pressed icon with the pointer once past the drag threshold; false while it is off the dock.</summary>
    bool DragTo(Point p)
    {
        var s = pressed!;
        if (dragged == null)
        {
            if (s.Trash || Math.Abs(p.X - pressAt.X) < SystemParameters.MinimumHorizontalDragDistance
                && Math.Abs(p.Y - pressAt.Y) < SystemParameters.MinimumVerticalDragDistance) return true;
            (dragged, hovered, homeIndex) = (s, null, slots.IndexOf(s));
            GhostImg.Source = s.Img!.Source;
            GhostImg.Width = GhostImg.Height = s.Size;
            s.Img.Opacity = 0; // keeps the mouse capture, unlike hiding it
        }

        var zone = new Rect(Canvas.GetLeft(HoverZone), Canvas.GetTop(HoverZone), HoverZone.Width, HoverZone.Height);
        zone.Inflate(Base / 2, Base / 2);
        var off = !zone.Contains(p);
        var remove = off && s.Pinned;
        if (remove != removing) Rearrange(() => removing = remove);
        // Off the dock, an icon that can't be removed waits in its old spot.
        MoveTo(s, remove ? slots.IndexOf(s) : off ? homeIndex : DropIndex(p.X));
        RemoveLabel.Visibility = remove ? Visibility.Visible : Visibility.Collapsed;
        s.Dot!.Opacity = remove ? 0 : 1;

        ghostAt = new Point(p.X - grab.X, p.Y - grab.Y);
        GhostPanel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Ghost.HorizontalOffset = ghostAt.X - (GhostPanel.DesiredSize.Width - GhostImg.Width) / 2;
        Ghost.VerticalOffset = ghostAt.Y - (GhostPanel.DesiredSize.Height - GhostImg.Height);
        Ghost.IsOpen = true;
        if (off)
        {
            mouseX = double.NaN;
            Retarget();
        }
        return !off;
    }

    /// <summary>Ends a press: a click if it never dragged, otherwise keeps the new spot (pinning the app) or removes it.</summary>
    void EndPress(bool commit)
    {
        if (pressed is not { } s) return;
        pressed = null;
        s.Img!.ReleaseMouseCapture(); // re-enters through LostMouseCapture and returns above
        if (dragged == null)
        {
            if (commit) Click(s);
            return;
        }
        dragged = null;
        Ghost.IsOpen = false;
        RemoveLabel.Visibility = Visibility.Collapsed;
        s.Img.Opacity = s.Dot!.Opacity = 1;

        if (commit && removing) config.Pinned.RemoveAll(p => Same(p.Path, s.Key));
        else if (commit && slots.IndexOf(s) != homeIndex)
            PinBefore(config.Pinned, s.Key, s.Name, slots.Skip(slots.IndexOf(s) + 1).FirstOrDefault(x => x.Pinned)?.Key);
        else commit = false;
        removing = false;
        if (commit) config.Save();
        Refresh();
        Layout();
        // The icon slides from where it was let go into its spot.
        s.ShiftX += ghostAt.X - Canvas.GetLeft(s.Img);
        s.ShiftY += ghostAt.Y - Canvas.GetTop(s.Img);
        Retarget();
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
