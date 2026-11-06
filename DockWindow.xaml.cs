using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using ManagedShell.Common.Helpers;
using ManagedShell.WindowsTray;
using static Mindows.Native;

namespace Mindows;

public partial class DockWindow : Window
{
    // Sizes in DIPs. The name tag and the dots keep their size at any icon size, as on macOS.
    const double Magnification = 1.75, TagArrowW = 16, TagArrowH = 6, TagLift = 4;
    const string TrashKey = "::{645FF040-5081-101B-9F08-00AA002F954E}", SearchKey = "mindows:search";

    double Base => config.DockIconSize;
    double Max => config.DockMagnification ? Math.Round(Base * Magnification) : Base;
    double Range => Base * 3.3;
    // Proportions measured from a macOS dock, in 52ths of the icon size (a 52px icon there).
    double Gap => Base * 8 / 52;           // each side of an icon
    double Inset => Base * 14 / 52;        // shelf edge to icon edge, on all four sides
    double Pad => Inset - Gap;
    double SepW => Base * 30 / 52;
    double Radius => Base * 24 / 52;
    double BottomMargin => Base * 4 / 52;  // screen edge to shelf
    double ShelfH => Base + Inset * 2;
    double WindowH => BottomMargin + Inset * 2 + Max + 36; // room for the magnified icon, the launch bounce and the name tag

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

    public NotificationArea? Tray { get; set; }

    readonly Config config;
    readonly AppBar bar;
    readonly Slot separator, trash, search;
    // As on the taskbar: a plain Search icon, and a colored one while Search is open.
    readonly ImageSource? searchOff = SearchIcon("Off"), searchOn = SearchIcon("On");
    readonly DispatcherTimer searchTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    IntPtr searchWindow;
    bool searchSeen;     // Search has shown since the icon was lit
    long searchLitAt;
    readonly Slot runningSeparator; // between pinned apps and running unpinned ones, shown only when there are some
    List<Slot> slots = new();
    Slot? hovered;
    double mouseX = double.NaN;  // NaN while the pointer is off the dock
    double anchorX = double.NaN; // the dock point under this x stays put while icons grow or shrink
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
        trash = NewSlot(TrashKey, "휴지통", null); // Refresh sets its icon once it loads
        trash.Trash = true;
        search = NewSlot(SearchKey, "검색", searchOff);
        search.Img!.RenderTransform = new ScaleTransform(); // Search never bounces; it shrinks and regrows instead
        search.Img.RenderTransformOrigin = new Point(0.5, 0.5);
        searchTimer.Tick += (_, _) => WatchSearch();

        MouseMove += (_, e) =>
        {
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
        IconLoader.Arrived += QueueRefresh; // icons fill in as they load, in the order Refresh keeps
        // MenuBarWindow's timer calls Refresh every second.
    }

    bool refreshQueued;

    // At startup many icons arrive back to back; one Refresh covers them all.
    void QueueRefresh()
    {
        if (refreshQueued) return;
        refreshQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () => { refreshQueued = false; Refresh(); });
    }

    /// <summary>The taskbar's own Search icon, "Off" or "On", inset like an app icon; null if a Windows update moves it.</summary>
    static ImageSource? SearchIcon(string state)
    {
        // shortcut: the shell ships it at 64px at most, so it is soft when magnified; draw it as a vector if that shows.
        var path = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"SystemApps\MicrosoftWindows.Client.Core_cw5n1h2txyewy",
            $@"StartMenu\Assets\SearchIcons\SearchIcon{state}{(App.LightTheme ? "Light" : "Dark")}.scale-400.png");
        if (!File.Exists(path)) return null;
        var drawing = new DrawingGroup();
        drawing.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, 10, 10))));
        drawing.Children.Add(new ImageDrawing(new BitmapImage(new Uri(path)), new Rect(1, 1, 8, 8)));
        var icon = new DrawingImage(drawing);
        icon.Freeze();
        return icon;
    }

    bool SearchShown()
    {
        if (!IsWindow(searchWindow)) searchWindow = AllTopLevelWindows().FirstOrDefault(MenuBarWindow.IsSearch);
        return MenuBarWindow.IsShown(searchWindow);
    }

    void ClickSearch()
    {
        var open = SearchShown();
        Sys.WinKey('S'); // toggles it; a close puts the plain icon back in WatchSearch
        if (!open && searchOn != null) LightSearch(animate: true);
    }

    /// <summary>Shows the colored icon until Search closes; a click shrinks the plain one and regrows it colored, as the taskbar does.</summary>
    void LightSearch(bool animate)
    {
        (searchSeen, searchLitAt) = (false, Environment.TickCount64);
        searchTimer.Start();
        if (!animate)
        {
            search.Img!.Source = searchOn;
            return;
        }
        var scale = (ScaleTransform)search.Img!.RenderTransform;
        void Scale(double to, int ms, IEasingFunction ease, EventHandler? done = null)
        {
            var x = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease };
            if (done != null) x.Completed += done;
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, x);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease });
        }
        Scale(0.7, 100, new QuadraticEase { EasingMode = EasingMode.EaseOut }, (_, _) =>
        {
            search.Img.Source = searchOn;
            Scale(1, 250, new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.5 });
        });
    }

    void WatchSearch()
    {
        var shown = SearchShown();
        searchSeen |= shown;
        // Back to the plain icon once Search closes, or if it never opened.
        if (shown || !searchSeen && Environment.TickCount64 - searchLitAt < 2000) return;
        searchTimer.Stop();
        search.Img!.Source = searchOff;
    }

    /// <summary>Screen y of the shelf's top edge at rest, in physical pixels; Search opens above it.</summary>
    public int ShelfTop => (int)Math.Round(PointToScreen(new Point(0, ActualHeight - BottomMargin - ShelfH)).Y);

    static double Magnify(double distance, double size, double max, double range) =>
        distance >= range ? size : size + (max - size) * (Math.Cos(Math.PI * distance / range) + 1) / 2;

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
        // An auto-hidden dock shows while the size is dragged, then hides at the new shelf height.
        if (config.DockAutoHide) Slide(hide: commit && !IsMouseOver);
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

    /// <summary>Existing slot for the key, or a new one; null when the app has no icon (e.g. uninstalled) or it is still loading.</summary>
    Slot? SlotFor(string key, string name, bool pinned, string? exe = null)
    {
        var s = slots.FirstOrDefault(x => Same(x.Key, key));
        if (s == null)
        {
            if (!IconLoader.TryGet(key, out var icon)) return null;
            if (icon == null && (exe == null || !IconLoader.TryGet(exe, out icon))) return null;
            if (icon == null) return null;
            s = NewSlot(key, name, icon);
        }
        (s.Name, s.Pinned) = (name, pinned);
        return s;
    }

    internal void Refresh()
    {
        if (dragged != null) return; // the drag owns the order until the drop
        var windows = WindowTracker.List();
        var wanted = new List<Slot>();
        if (search.Img?.Source != null) wanted.Add(search); // first, where the taskbar has it
        if (!searchTimer.IsEnabled && searchOn != null && SearchShown()) LightSearch(animate: false); // opened with Win+S
        foreach (var p in config.Pinned)
            if (!wanted.Any(s => Same(s.Key, p.Path)) && SlotFor(p.Path, p.Name, true) is { } s) wanted.Add(s);
        var pinnedCount = wanted.Count;
        // The window list is in z-order; as on macOS, running apps keep their spot and newly launched ones join at the right end.
        int Spot(AppWindow w) => slots.FindIndex(s => Same(s.Key, w.App.Key)) is var i and >= 0 ? i : int.MaxValue;
        foreach (var w in windows.OrderBy(Spot))
            if (!wanted.Any(s => Same(s.Key, w.App.Key)) && SlotFor(w.App.Key, w.App.Name, false, w.App.ExePath) is { } s) wanted.Add(s);
        if (wanted.Count > pinnedCount) wanted.Insert(pinnedCount, runningSeparator);
        runningSeparator.Line!.Visibility = wanted.Contains(runningSeparator) ? Visibility.Visible : Visibility.Collapsed;
        if (trash.Img!.Source == null && IconLoader.TryGet(TrashKey, out var trashIcon)) trash.Img.Source = trashIcon;
        if (trash.Img.Source != null) wanted.AddRange(new[] { separator, trash });

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
        Shelf.CornerRadius = new CornerRadius(Radius);
        var zoneTop = shelfTop - (Max - Base) - 4;
        Place(HoverZone, left, zoneTop, total, height - zoneTop);

        var x = left + Pad;
        foreach (var s in slots)
        {
            var sx = x + s.ShiftX;
            if (s.Separator) Place(s.Line!, sx + SepW / 2, shelfTop + Base * 10 / 52, 1, ShelfH - Base * 20 / 52);
            else
            {
                var gap = (SlotWidth(s) - s.Size) / 2;
                Place(s.Img!, sx + gap, height - BottomMargin - Inset - s.Size + s.ShiftY, s.Size, s.Size);
                Canvas.SetLeft(s.Dot!, sx + gap + s.Size / 2 - 2);
                Canvas.SetTop(s.Dot!, height - BottomMargin - 6);
            }
            x += SlotWidth(s);
        }

        if (hovered?.Img is { } img && dragged == null)
        {
            // Layout runs every animation frame; the tag's shape only changes with its text.
            if (LabelText.Text != hovered.Name || LabelShape.Data == null)
            {
                LabelText.Text = hovered.Name;
                LabelShape.Data = TagShape(LabelText);
            }
            Label.Visibility = Visibility.Visible;
            double w = LabelText.DesiredSize.Width, h = LabelText.DesiredSize.Height - TagArrowH;
            Canvas.SetLeft(Label, Canvas.GetLeft(img) + hovered.Size / 2 - w / 2);
            // As on macOS, the arrow sits just above the shelf, and rises with a magnified icon.
            Canvas.SetTop(Label, Canvas.GetTop(img) - Inset - TagLift - h - TagArrowH);
        }
        else Label.Visibility = Visibility.Collapsed;
    }

    /// <summary>A name tag's pill and arrow as one shape, sized to its text, whose bottom margin leaves room for the arrow.</summary>
    static Geometry TagShape(TextBlock text)
    {
        text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double w = text.DesiredSize.Width, h = text.DesiredSize.Height - TagArrowH;
        var arrow = new PathGeometry(new[] { new PathFigure(new Point(w / 2 - TagArrowW / 2, h - 1),
            new[] { new PolyLineSegment(new[] { new Point(w / 2, h + TagArrowH), new Point(w / 2 + TagArrowW / 2, h - 1) }, true) }, true) });
        return new CombinedGeometry(GeometryCombineMode.Union, new RectangleGeometry(new Rect(0, 0, w, h), h / 2, h / 2), arrow);
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
        var first = slots.IndexOf(search) + 1; // pinned icons start after Search
        var pinned = first + slots.Count(s => s.Pinned && s != dragged);
        // An unpinned app stays put until it is brought into the pinned icons.
        return index < first ? first : index <= pinned ? index : dragged!.Pinned ? pinned : homeIndex;
    }

    /// <summary>Moves the pressed icon with the pointer once past the drag threshold; false while it is off the dock.</summary>
    bool DragTo(Point p)
    {
        var s = pressed!;
        if (dragged == null)
        {
            if (s.Trash || s == search || Math.Abs(p.X - pressAt.X) < SystemParameters.MinimumHorizontalDragDistance
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
        RemoveShape.Data ??= TagShape(RemoveText);
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
        else if (s == search) ClickSearch();
        else if (s.Windows.Count == 0)
        {
            // A running app that lives in the tray would only get a slow second launch; double-click its icon instead.
            if (!DoubleClickTrayIcon(s.Key)) Sys.Launch(s.Key);
            Bounce(s);
        }
        // Already in front: cycle to its bottom-most window.
        else Sys.Focus(s.Windows.Contains(GetForegroundWindow()) ? s.Windows[^1] : s.Windows[0]);
    }

    bool DoubleClickTrayIcon(string exe)
    {
        // Explorer's tray icons are system icons (network, volume), not its windows; double-clicking one opens Settings.
        if (string.Equals(System.IO.Path.GetFileName(exe), "explorer.exe", StringComparison.OrdinalIgnoreCase)) return false;
        var icon = Tray?.TrayIcons.OfType<NotifyIcon>().FirstOrDefault(i => string.Equals(i.Path, exe, StringComparison.OrdinalIgnoreCase));
        if (icon == null) return false;
        AllowSetForegroundWindow(uint.MaxValue);
        var at = MouseHelper.GetCursorPositionParam();
        for (var i = 0; i < 2; i++)
        {
            icon.IconMouseDown(MouseButton.Left, at, GetDoubleClickTime());
            icon.IconMouseUp(MouseButton.Left, at, GetDoubleClickTime());
        }
        return true;
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
        if (s == search) return; // nothing to keep in the dock or open the location of
        var items = new List<Control?>();
        if (s.Trash)
        {
            items.Add(Menus.Item("열기", () => Sys.Launch("shell:RecycleBinFolder")));
            items.Add(Menus.Item("휴지통 비우기...", () => SHEmptyRecycleBin(new WindowInteropHelper(this).Handle, null, 0)));
        }
        else
        {
            foreach (var h in s.Windows) items.Add(Menus.Item(WindowText(h) is { Length: > 0 } t ? t : s.Name, () => Sys.Focus(h)));
            if (s.Windows.Count > 0) items.Add(null);
            if (Browsers.TryGetValue(System.IO.Path.GetFileName(s.Key), out var b))
            {
                items.Add(Menus.Item("새 창", () => Sys.Launch(s.Key, b.NewArg)));
                items.Add(Menus.Item(b.PrivateLabel, () => Sys.Launch(s.Key, b.PrivateArg)));
                items.Add(null);
            }
            else if (s.Windows.Count > 0)
            {
                // Most apps open another window when launched again; single-instance ones just come to front.
                items.Add(Menus.Item("새 창", () => Sys.Launch(s.Key)));
                items.Add(null);
            }
            items.Add(Menus.Item("Dock에 유지", () => TogglePin(s), isChecked: s.Pinned));
            if (!s.Key.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
                items.Add(Menus.Item("파일 위치 열기", () => Sys.Launch("explorer.exe", $"/select,\"{s.Key}\"")));
            items.Add(null);
            items.Add(s.Windows.Count > 0
                ? Menus.Item("종료", () => s.Windows.ForEach(Sys.Close))
                : Menus.Item("열기", () => Click(s)));
        }
        Menus.Show(s.Img!, PlacementMode.Top, items);
    }

    static readonly Dictionary<string, (string NewArg, string PrivateArg, string PrivateLabel)> Browsers =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["chrome.exe"] = ("--new-window", "--incognito", "새 시크릿 창"),
            ["msedge.exe"] = ("--new-window", "--inprivate", "새 InPrivate 창"),
            ["whale.exe"] = ("--new-window", "--incognito", "새 시크릿 창"),
            ["brave.exe"] = ("--new-window", "--incognito", "새 개인 창"),
            ["vivaldi.exe"] = ("--new-window", "--incognito", "새 개인 창"),
            ["opera.exe"] = ("--new-window", "--private", "새 개인 창"),
            ["firefox.exe"] = ("-new-window", "-private-window", "새 사생활 보호 창"),
        };

    void TogglePin(Slot s)
    {
        if (s.Pinned) config.Pinned.RemoveAll(p => Same(p.Path, s.Key));
        else config.Pinned.Add(new() { Name = s.Name, Path = s.Key });
        config.Save();
        Refresh();
    }
}
