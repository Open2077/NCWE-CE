// SPDX-License-Identifier: GPL-3.0-only
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace NCWE.Studio.Viewport;

// An item of the radial menu: an action, or a category whose items unfold around it.
internal sealed record RadialItem(string Label, string Glyph)
{
    public Action? Run { get; init; }
    public IReadOnlyList<RadialItem> Children { get; init; } = [];
    // Shown in the centre while pointed at (its key, to learn it).
    public string Shortcut { get; init; } = "";
    public Func<bool>? Checked { get; init; }
    public Func<bool>? Enabled { get; init; }
    public bool HasChildren => Children.Count > 0;
    public bool IsEnabled => Enabled?.Invoke() ?? true;
}

// A pie menu over the 3D view, opened around the cursor while a key is held: a ring of
// categories; pointing at one unfolds its items as an outer ring beside it. The direction picks
// (the distance does not matter past the hole), so a flick of the mouse is enough.
// - Releasing the key runs the item pointed at; on a category, or after a quick tap, the menu
//   stays open for a click.
// - A click runs an item; a click elsewhere, a right click or Esc closes it.
internal sealed class RadialMenu
{
    private const double Hole = 52, InnerFrom = 60, InnerTo = 150, OuterFrom = 156, OuterTo = 246, Gap = 3;
    private static readonly double Deg = Math.PI / 180;

    private readonly Canvas _layer = new() { Visibility = Visibility.Collapsed, Background = new SolidColorBrush(Windows.UI.Color.FromArgb(1, 0, 0, 0)) };
    private readonly Canvas _menu = new();
    private readonly Canvas _outer = new();
    // The centre: what is pointed at and a hint, kept inside the hole (wrapped, never over the ring).
    private readonly Border _pill = new() { CornerRadius = new CornerRadius(10), Padding = new Thickness(8, 4, 8, 5), Background = Brush("#F01B1C1F"), BorderBrush = Brush("#55FFFFFF"), BorderThickness = new Thickness(1), MaxWidth = 2 * InnerFrom - 6 };
    private readonly TextBlock _title = new() { FontSize = 12.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxLines = 2, Foreground = Brush("#FFE8E9EC") };
    private readonly TextBlock _hint = new() { FontSize = 10, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxLines = 4, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Brush("#FF9A9DA4") };
    private IReadOnlyList<RadialItem> _items = [];
    private readonly List<Path> _innerShapes = [];
    private readonly List<Path> _outerShapes = [];
    private int _hoverInner = -1, _hoverOuter = -1, _unfolded = -1;
    private double _outerStart, _outerStep;
    private DateTime _openedAt;
    private string _idleHint = "";

    private static readonly Brush Fill = Brush("#F0262830"), Stroke = Brush("#30FFFFFF"), HoverFill = Brush("#FF3D7DCA"), CheckedFill = Brush("#FF2E3A4D"),
        Text = Brush("#FFDADCE0"), Dim = Brush("#FF6E727A"), Accent = Brush("#FF7FB2F0");

    public UIElement Layer => _layer;
    public bool IsOpen => _layer.Visibility == Visibility.Visible;
    // Whether a release of the key still decides (false once it stays open for a click).
    public bool Held { get; private set; }

    public RadialMenu()
    {
        _layer.Children.Add(_menu);
        _menu.Children.Add(_outer);
        var pillText = new StackPanel { Spacing = 1 };
        pillText.Children.Add(_title); pillText.Children.Add(_hint);
        _pill.Child = pillText;
        _pill.IsHitTestVisible = false;
        _pill.SizeChanged += (_, _) => PlacePill();
        _layer.PointerMoved += (_, e) => Track(e.GetCurrentPoint(_menu).Position);
        _layer.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            var point = e.GetCurrentPoint(_menu);
            if (point.Properties.IsRightButtonPressed) { Close(); return; }
            Track(point.Position);
            if (Pointed() is { HasChildren: false } item) Run(item);
            else if (Pointed() is null) Close();
        };
        _layer.PointerWheelChanged += (_, e) => e.Handled = true;
    }

    // Opens around a point of the layer (kept inside it).
    public void Open(Point at, IReadOnlyList<RadialItem> items, string idleHint)
    {
        _items = items; _idleHint = idleHint;
        _hoverInner = _hoverOuter = _unfolded = -1;
        _openedAt = DateTime.UtcNow; Held = true;
        // The layer is collapsed until now (no size of its own): the view it covers gives the size.
        var host = _layer.Parent as FrameworkElement;
        double width = host?.ActualWidth ?? _layer.ActualWidth, height = host?.ActualHeight ?? _layer.ActualHeight;
        double Clamp(double v, double size) => size > 2 * (OuterTo + 8) ? Math.Clamp(v, OuterTo + 8, size - OuterTo - 8) : size / 2;
        Canvas.SetLeft(_menu, Clamp(at.X, width)); Canvas.SetTop(_menu, Clamp(at.Y, height));
        BuildInner();
        _layer.Visibility = Visibility.Visible;
        Animate(_menu, .86, 120);
        UpdateCentre();
    }

    public void Close()
    {
        _layer.Visibility = Visibility.Collapsed;
        Held = false;
        _outer.Children.Clear(); _outerShapes.Clear();
    }

    // The key is up: run what is pointed at, or stay open for a click (a category, or a quick tap).
    public void Release()
    {
        if (!IsOpen || !Held) return;
        Held = false;
        var item = Pointed();
        if (item is { HasChildren: false }) { Run(item); return; }
        if (item is null && (DateTime.UtcNow - _openedAt).TotalMilliseconds > 350) { Close(); return; }
        _hint.Text = Loc.T("Click an item · Esc closes");
    }

    private void Run(RadialItem item)
    {
        if (!item.IsEnabled || item.Run is not { } run) return;
        Close();
        run();
    }

    // Automation: points at a category (and one of its items) as the pointer would; what is pointed at.
    internal string? PointAt(string category, string? item)
    {
        int i = _items.Select((x, k) => (x, k)).FirstOrDefault(x => x.x.Label == category, (null!, -1)).Item2;
        if (i < 0) return null;
        Track(At((InnerFrom + InnerTo) / 2, i * InnerStep));
        if (item is null) return Pointed()?.Label;
        int j = _items[i].Children.Select((x, k) => (x, k)).FirstOrDefault(x => x.x.Label == item, (null!, -1)).Item2;
        if (j < 0) return null;
        Track(At((OuterFrom + OuterTo) / 2, _outerStart + (j + .5) * _outerStep));
        return Pointed()?.Label;
    }

    // Automation: a click on what is pointed at.
    internal bool Click()
    {
        if (Pointed() is not { HasChildren: false } item || !item.IsEnabled) return false;
        Run(item);
        return true;
    }

    internal string? PointedLabel => Pointed()?.Label;

    private RadialItem? Pointed() => _hoverOuter >= 0 && _unfolded >= 0 ? _items[_unfolded].Children[_hoverOuter] : _hoverInner >= 0 ? _items[_hoverInner] : null;

    // --- Geometry: angle 0 is up, clockwise; points relative to the centre ----------------------------

    private static Point At(double radius, double angle) => new(radius * Math.Sin(angle), -radius * Math.Cos(angle));

    private static Geometry Sector(double r0, double r1, double a0, double a1)
    {
        double i0 = a0 + Gap / r0, i1 = a1 - Gap / r0, o0 = a0 + Gap / r1, o1 = a1 - Gap / r1;
        var figure = new PathFigure { StartPoint = At(r1, o0), IsClosed = true, IsFilled = true };
        figure.Segments.Add(new ArcSegment { Point = At(r1, o1), Size = new Size(r1, r1), SweepDirection = SweepDirection.Clockwise, IsLargeArc = o1 - o0 > Math.PI });
        figure.Segments.Add(new LineSegment { Point = At(r0, i1) });
        figure.Segments.Add(new ArcSegment { Point = At(r0, i0), Size = new Size(r0, r0), SweepDirection = SweepDirection.Counterclockwise, IsLargeArc = i1 - i0 > Math.PI });
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return geometry;
    }

    private double InnerStep => 2 * Math.PI / Math.Max(1, _items.Count);

    private void BuildInner()
    {
        for (int i = _menu.Children.Count - 1; i >= 0; i--) if (!ReferenceEquals(_menu.Children[i], _outer)) _menu.Children.RemoveAt(i);
        _innerShapes.Clear(); _outer.Children.Clear(); _outerShapes.Clear();
        for (int i = 0; i < _items.Count; i++)
        {
            double centre = i * InnerStep;
            _innerShapes.Add(AddSegment(_menu, _items[i], InnerFrom, InnerTo, centre - InnerStep / 2, centre + InnerStep / 2));
        }
        _menu.Children.Add(_pill);
        PlacePill();
    }

    private void PlacePill()
    {
        Canvas.SetLeft(_pill, -_pill.ActualWidth / 2); Canvas.SetTop(_pill, -_pill.ActualHeight / 2);
    }

    // A segment with its icon, label, state and (a category) the arrow to its items.
    private Path AddSegment(Canvas into, RadialItem item, double r0, double r1, double a0, double a1)
    {
        bool on = item.Checked?.Invoke() == true, enabled = item.IsEnabled;
        var shape = new Path { Data = Sector(r0, r1, a0, a1), Fill = on ? CheckedFill : Fill, Stroke = on ? Accent : Stroke, StrokeThickness = 1 };
        into.Children.Add(shape);
        double centre = (a0 + a1) / 2, middle = (r0 + r1) / 2, width = Math.Min(96, (a1 - a0) * middle - 10);
        var content = new StackPanel { Spacing = 3, Width = width, IsHitTestVisible = false };
        content.Children.Add(new FontIcon { Glyph = item.Glyph, FontSize = 18, FontFamily = (FontFamily)Application.Current.Resources["IconFont"], Foreground = !enabled ? Dim : on ? Accent : Text, HorizontalAlignment = HorizontalAlignment.Center });
        content.Children.Add(new TextBlock { Text = item.Label, FontSize = 10, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = enabled ? Text : Dim, LineHeight = 12 });
        var p = At(middle, centre);
        Canvas.SetLeft(content, p.X - width / 2); Canvas.SetTop(content, p.Y - 20);
        into.Children.Add(content);
        if (item.HasChildren)
        {
            // A small triangle at the outer edge, pointing outwards: this one unfolds.
            var tip = At(r1 - 5, centre); var left = At(r1 - 12, centre - 5 / (r1 - 12)); var right = At(r1 - 12, centre + 5 / (r1 - 12));
            into.Children.Add(new Polygon { Points = new PointCollection { tip, left, right }, Fill = Brush("#CCFFFFFF"), IsHitTestVisible = false });
        }
        return shape;
    }

    private void Unfold(int index)
    {
        if (index == _unfolded) return;
        _unfolded = index; _hoverOuter = -1;
        _outer.Children.Clear(); _outerShapes.Clear();
        if (index < 0 || !_items[index].HasChildren) return;
        var children = _items[index].Children;
        // Items side by side around the category, centred on it: within about 200° (narrower when
        // there are many), so they stay beside it.
        _outerStep = Math.Clamp(200.0 / children.Count, 18, 30) * Deg;
        _outerStart = index * InnerStep - children.Count * _outerStep / 2;
        for (int j = 0; j < children.Count; j++)
            _outerShapes.Add(AddSegment(_outer, children[j], OuterFrom, OuterTo, _outerStart + j * _outerStep, _outerStart + (j + 1) * _outerStep));
        Animate(_outer, .92, 90);
    }

    // --- Pointing --------------------------------------------------------------------------------------

    private void Track(Point p)
    {
        double distance = Math.Sqrt(p.X * p.X + p.Y * p.Y);
        double angle = Math.Atan2(p.X, -p.Y); if (angle < 0) angle += 2 * Math.PI;
        int inner = -1, outer = -1;
        if (distance >= Hole * .8 && _items.Count > 0)
        {
            inner = (int)Math.Floor((angle + InnerStep / 2) / InnerStep) % _items.Count;
            // Past the inner ring, within the unfolded category's items: one of them.
            if (_unfolded >= 0 && distance >= InnerTo - 6)
            {
                double offset = angle - _outerStart; offset -= Math.Floor(offset / (2 * Math.PI)) * 2 * Math.PI;
                int j = (int)Math.Floor(offset / _outerStep);
                if (j >= 0 && j < _items[_unfolded].Children.Count) { outer = j; inner = _unfolded; }
            }
        }
        if (inner == _hoverInner && outer == _hoverOuter) return;
        if (outer < 0) Unfold(inner >= 0 && _items[inner].HasChildren ? inner : -1);
        _hoverInner = inner; _hoverOuter = outer;
        for (int i = 0; i < _innerShapes.Count; i++) _innerShapes[i].Fill = i == inner && (outer < 0 || !_items[i].HasChildren) ? HoverFill : i == inner ? Brush("#FF2F4F78") : Base(_items[i]);
        if (_unfolded >= 0)
            for (int j = 0; j < _outerShapes.Count; j++) _outerShapes[j].Fill = j == outer ? HoverFill : Base(_items[_unfolded].Children[j]);
        UpdateCentre();
    }

    private static Brush Base(RadialItem item) => item.Checked?.Invoke() == true ? CheckedFill : Fill;

    private void UpdateCentre()
    {
        var item = Pointed();
        _title.Text = item?.Label ?? Loc.T("Shortcuts");
        _hint.Text = item is null ? _idleHint
            : !item.IsEnabled ? Loc.T("Not available here")
            : item.HasChildren ? Loc.T("Point at one of its items")
            : item.Shortcut.Length > 0 ? item.Shortcut : "";
        _hint.Visibility = _hint.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // --- Looks -----------------------------------------------------------------------------------------

    // A quick grow from slightly smaller (never transparent: shown at once, wherever the animation is).
    private static void Animate(UIElement element, double from, int milliseconds)
    {
        var scale = new ScaleTransform { ScaleX = from, ScaleY = from };
        element.RenderTransform = scale;
        var board = new Storyboard();
        void Add(DependencyObject target, string property, double start, double end)
        {
            var animation = new DoubleAnimation { From = start, To = end, Duration = TimeSpan.FromMilliseconds(milliseconds), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            Storyboard.SetTarget(animation, target); Storyboard.SetTargetProperty(animation, property);
            board.Children.Add(animation);
        }
        Add(scale, "ScaleX", from, 1); Add(scale, "ScaleY", from, 1);
        board.Begin();
    }

    private static SolidColorBrush Brush(string hex)
    {
        uint v = Convert.ToUInt32(hex.TrimStart('#'), 16);
        return new SolidColorBrush(Windows.UI.Color.FromArgb((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v));
    }
}
