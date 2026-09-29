using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;

namespace screengrab.Controls;

/// <summary>
/// Shows a screenshot, shrunk to fit or zoomed, and lets the user select part of it: drag to
/// draw a rectangle, drag within 10 pixels of its edges or corners to resize it, and (unless
/// <see cref="MoveInside"/> is off) drag inside it to move it. The rest of the picture is covered
/// with 50% black, the rectangle has a 2px dashed border, and its size is shown beside it.
///
/// Keyboard (once clicked): arrows move the selection one pixel (ten with Shift), Ctrl+A selects
/// everything, Escape clears the selection. Ctrl+wheel zooms around the pointer.
///
/// When zoomed (<see cref="Zoom"/> above 0) it's as big as the zoomed picture, for a
/// ScrollViewer to scroll; to fit, it fills the space it's given.
/// </summary>
public sealed class CaptureView : Control
{
    public static readonly StyledProperty<Bitmap?> SourceProperty =
        AvaloniaProperty.Register<CaptureView, Bitmap?>(nameof(Source));

    /// <summary>Screen pixels per picture pixel; 0 shrinks the picture to fit (but never enlarges it).</summary>
    public static readonly StyledProperty<double> ZoomProperty =
        AvaloniaProperty.Register<CaptureView, double>(nameof(Zoom));

    /// <summary>
    /// Whether dragging inside the selection moves it. When false (the full-screen selection
    /// window), a press anywhere but on an edge starts a new rectangle.
    /// </summary>
    public static readonly StyledProperty<bool> MoveInsideProperty =
        AvaloniaProperty.Register<CaptureView, bool>(nameof(MoveInside), true);

    /// <summary>Whether the whole picture is dimmed while nothing is selected (the full-screen selection window).</summary>
    public static readonly StyledProperty<bool> DimWhenEmptyProperty =
        AvaloniaProperty.Register<CaptureView, bool>(nameof(DimWhenEmpty));

    /// <summary>The zoom levels Ctrl+wheel steps through.</summary>
    public static readonly double[] ZoomSteps = [0.125, 0.25, 0.5, 1, 2, 4, 8];

    /// <summary>How close to an edge (in device-independent pixels) a press resizes the selection.</summary>
    private const double GrabDistance = 10;

    /// <summary>Everything outside the selection is covered with 50% black.</summary>
    private static readonly IBrush DimBrush = new SolidColorBrush(Color.FromArgb(128, 0, 0, 0));

    // The border: white dashes over a black line, 2px wide, so it shows on any picture.
    private static readonly IPen BorderUnderPen = new Pen(Brushes.Black, 2);
    private static readonly IPen BorderDashPen = new Pen(Brushes.White, 2, new DashStyle([3, 3], 0));
    private static readonly IBrush LabelBrush = new SolidColorBrush(Color.FromArgb(200, 20, 20, 20));

    private PixelRect? _selection;
    private DragMode _drag;
    private Edges _edges;
    private Point _dragStart;
    private PixelRect _dragOriginal;

    static CaptureView()
    {
        AffectsRender<CaptureView>(SourceProperty, ZoomProperty);
        AffectsMeasure<CaptureView>(SourceProperty, ZoomProperty);
        FocusableProperty.OverrideDefaultValue<CaptureView>(true);
        ClipToBoundsProperty.OverrideDefaultValue<CaptureView>(true);
    }

    public Bitmap? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public bool MoveInside
    {
        get => GetValue(MoveInsideProperty);
        set => SetValue(MoveInsideProperty, value);
    }

    public bool DimWhenEmpty
    {
        get => GetValue(DimWhenEmptyProperty);
        set => SetValue(DimWhenEmptyProperty, value);
    }

    public double Zoom
    {
        get => GetValue(ZoomProperty);
        set => SetValue(ZoomProperty, value);
    }

    /// <summary>The selected part of the picture in picture pixels, or null for none.</summary>
    public PixelRect? Selection
    {
        get => _selection;
        set
        {
            PixelRect? selection = null;
            if (value is { } rect && Source is { } source)
            {
                rect = rect.Intersect(new PixelRect(source.PixelSize));
                if (rect.Width > 0 && rect.Height > 0)
                    selection = rect;
            }
            if (selection == _selection)
                return;
            _selection = selection;
            InvalidateVisual();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler? SelectionChanged;

    /// <summary>The zoom actually in use, as a fraction of the picture's real size (1 = 100%).</summary>
    public double EffectiveZoom => Scale * RenderScaling;

    private enum DragMode { None, New, Move, Resize }

    [Flags]
    private enum Edges { None = 0, Left = 1, Top = 2, Right = 4, Bottom = 8 }

    private double RenderScaling => TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;

    /// <summary>Device-independent pixels per picture pixel.</summary>
    private double Scale
    {
        get
        {
            var actualSize = 1 / RenderScaling;
            if (Zoom > 0 || Source is null)
                return (Zoom > 0 ? Zoom : 1) * actualSize;
            var fit = Math.Min(Bounds.Width / Source.PixelSize.Width, Bounds.Height / Source.PixelSize.Height);
            return Math.Min(fit, actualSize);
        }
    }

    /// <summary>Where the picture is drawn, centred when it's smaller than the control.</summary>
    private Rect ImageRect
    {
        get
        {
            if (Source is null)
                return default;
            var scale = Scale;
            var width = Source.PixelSize.Width * scale;
            var height = Source.PixelSize.Height * scale;
            return new Rect(Math.Max(0, (Bounds.Width - width) / 2), Math.Max(0, (Bounds.Height - height) / 2), width, height);
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        // A selection belongs to one picture.
        if (change.Property == SourceProperty)
        {
            _drag = DragMode.None;
            Selection = null;
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Source is { } source && Zoom > 0)
        {
            var scale = Scale;
            return new Size(source.PixelSize.Width * scale, source.PixelSize.Height * scale);
        }
        return new Size(
            double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? 0 : availableSize.Height);
    }

    public override void Render(DrawingContext context)
    {
        // Paint the whole control, so all of it takes the mouse.
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        if (Source is not { } source)
            return;

        var image = ImageRect;
        // Sharp pixels at 100% and above; smooth when shrunk.
        var interpolation = EffectiveZoom >= 1 ? BitmapInterpolationMode.None : BitmapInterpolationMode.HighQuality;
        using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = interpolation }))
            context.DrawImage(source, new Rect(source.Size), image);

        if (_selection is { } selection)
            DrawSelection(context, image, ToView(selection), selection);
        else if (DimWhenEmpty)
            context.FillRectangle(DimBrush, image);
    }

    private void DrawSelection(DrawingContext context, Rect image, Rect area, PixelRect selection)
    {
        // Darken everything outside the selection.
        context.FillRectangle(DimBrush, new Rect(image.Left, image.Top, image.Width, area.Top - image.Top));
        context.FillRectangle(DimBrush, new Rect(image.Left, area.Bottom, image.Width, image.Bottom - area.Bottom));
        context.FillRectangle(DimBrush, new Rect(image.Left, area.Top, area.Left - image.Left, area.Height));
        context.FillRectangle(DimBrush, new Rect(area.Right, area.Top, image.Right - area.Right, area.Height));

        // Inset by half the pen, so the 2px line sits just inside the selection and stays crisp.
        var border = area.Deflate(1);
        context.DrawRectangle(null, BorderUnderPen, border);
        context.DrawRectangle(null, BorderDashPen, border);

        // The size, just above the top-left corner (or just inside it, at the top of the picture).
        var text = new FormattedText($"{selection.Width} × {selection.Height}", CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, Typeface.Default, 12, Brushes.White);
        var labelWidth = text.Width + 10;
        var labelHeight = text.Height + 4;
        var labelTop = area.Top - labelHeight - 4 >= 0 ? area.Top - labelHeight - 4 : area.Top + 4;
        var labelLeft = Math.Clamp(area.Left, 0, Math.Max(0, Bounds.Width - labelWidth));
        context.DrawRectangle(LabelBrush, null, new Rect(labelLeft, labelTop, labelWidth, labelHeight), 3, 3);
        context.DrawText(text, new Point(labelLeft + 5, labelTop + 2));
    }

    private Point ToImage(Point view)
    {
        var image = ImageRect;
        var scale = Scale;
        return new Point((view.X - image.X) / scale, (view.Y - image.Y) / scale);
    }

    private Rect ToView(PixelRect rect)
    {
        var image = ImageRect;
        var scale = Scale;
        return new Rect(image.X + rect.X * scale, image.Y + rect.Y * scale, rect.Width * scale, rect.Height * scale);
    }

    /// <summary>The picture pixel under a point (in picture coordinates), kept inside the picture.</summary>
    private PixelPoint PixelAt(Point point)
    {
        var size = Source!.PixelSize;
        return new PixelPoint(
            Math.Clamp((int)Math.Floor(point.X), 0, size.Width - 1),
            Math.Clamp((int)Math.Floor(point.Y), 0, size.Height - 1));
    }

    /// <summary>
    /// The rectangle from one pixel to another, both included, in either order. The pixel under
    /// the pointer is part of the selection, so a drag from corner to corner of a 1920 x 1080
    /// screen (pixels 0 to 1919 and 0 to 1079) selects all 1920 x 1080.
    /// </summary>
    private static PixelRect Spanning(int x1, int y1, int x2, int y2) =>
        new(Math.Min(x1, x2), Math.Min(y1, y2), Math.Abs(x2 - x1) + 1, Math.Abs(y2 - y1) + 1);

    /// <summary>Which edges of the selection are under <paramref name="view"/>, for resizing.</summary>
    private Edges EdgesAt(Point view)
    {
        if (_selection is not { } selection)
            return Edges.None;

        var r = ToView(selection);
        var withinX = view.X >= r.Left - GrabDistance && view.X <= r.Right + GrabDistance;
        var withinY = view.Y >= r.Top - GrabDistance && view.Y <= r.Bottom + GrabDistance;
        var edges = Edges.None;
        if (withinY && Math.Abs(view.X - r.Right) <= GrabDistance)
            edges |= Edges.Right;
        else if (withinY && Math.Abs(view.X - r.Left) <= GrabDistance)
            edges |= Edges.Left;
        if (withinX && Math.Abs(view.Y - r.Bottom) <= GrabDistance)
            edges |= Edges.Bottom;
        else if (withinX && Math.Abs(view.Y - r.Top) <= GrabDistance)
            edges |= Edges.Top;
        return edges;
    }

    private bool InsideSelection(Point view) =>
        MoveInside && _selection is { } selection && ToView(selection).Contains(view);

    private static StandardCursorType CursorFor(Edges edges) => edges switch
    {
        Edges.Left | Edges.Top => StandardCursorType.TopLeftCorner,
        Edges.Right | Edges.Top => StandardCursorType.TopRightCorner,
        Edges.Left | Edges.Bottom => StandardCursorType.BottomLeftCorner,
        Edges.Right | Edges.Bottom => StandardCursorType.BottomRightCorner,
        Edges.Left or Edges.Right => StandardCursorType.SizeWestEast,
        _ => StandardCursorType.SizeNorthSouth,
    };

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Source is null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        Focus();
        var view = e.GetPosition(this);
        var point = ToImage(view);
        _edges = EdgesAt(view);
        if (_edges != Edges.None)
            _drag = DragMode.Resize;
        else if (InsideSelection(view))
            _drag = DragMode.Move;
        else
        {
            _drag = DragMode.New;
            Selection = null;
        }
        _dragStart = point;
        _dragOriginal = _selection ?? default;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (Source is null)
            return;

        var view = e.GetPosition(this);
        if (_drag == DragMode.None)
        {
            var edges = EdgesAt(view);
            Cursor = new Cursor(edges != Edges.None ? CursorFor(edges)
                : InsideSelection(view) ? StandardCursorType.SizeAll
                : StandardCursorType.Cross);
            return;
        }

        var point = ToImage(view);
        var size = Source.PixelSize;
        var original = _dragOriginal;
        switch (_drag)
        {
            case DragMode.New:
                var start = PixelAt(_dragStart);
                var end = PixelAt(point);
                Selection = Spanning(start.X, start.Y, end.X, end.Y);
                break;

            case DragMode.Move:
                var x = original.X + (int)Math.Round(point.X - _dragStart.X);
                var y = original.Y + (int)Math.Round(point.Y - _dragStart.Y);
                Selection = new PixelRect(
                    Math.Clamp(x, 0, size.Width - original.Width),
                    Math.Clamp(y, 0, size.Height - original.Height),
                    original.Width, original.Height);
                break;

            case DragMode.Resize:
                // The first and last pixels in each direction, both included; the dragged edges follow the pointer.
                var pointer = PixelAt(point);
                int left = original.X, top = original.Y, right = original.Right - 1, bottom = original.Bottom - 1;
                if (_edges.HasFlag(Edges.Left))
                    left = pointer.X;
                if (_edges.HasFlag(Edges.Right))
                    right = pointer.X;
                if (_edges.HasFlag(Edges.Top))
                    top = pointer.Y;
                if (_edges.HasFlag(Edges.Bottom))
                    bottom = pointer.Y;
                Selection = Spanning(left, top, right, bottom);
                break;
        }
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_drag == DragMode.None)
            return;

        // A click without a drag clears the selection rather than making a one-pixel one.
        if (_drag == DragMode.New && _selection is { Width: < 3 } or { Height: < 3 })
            Selection = null;
        _drag = DragMode.None;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _drag = DragMode.None;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (Source is null || e.KeyModifiers != KeyModifiers.Control || e.Delta.Y == 0)
            return;

        var current = EffectiveZoom;
        var next = e.Delta.Y > 0
            ? ZoomSteps.FirstOrDefault(z => z > current + 0.001, ZoomSteps[^1])
            : ZoomSteps.LastOrDefault(z => z < current - 0.001, ZoomSteps[0]);
        ZoomAround(next, e.GetPosition(this));
        e.Handled = true;
    }

    /// <summary>
    /// Sets the zoom, scrolling so the picture point under <paramref name="view"/> stays put.
    /// Used for Ctrl+wheel; with no point, it keeps the middle of what's showing.
    /// </summary>
    public void ZoomAround(double zoom, Point? view = null)
    {
        var scroller = this.FindAncestorOfType<ScrollViewer>();
        if (Source is null || scroller is null)
        {
            Zoom = zoom;
            return;
        }

        var anchor = view ?? new Point(scroller.Offset.X + scroller.Viewport.Width / 2, scroller.Offset.Y + scroller.Viewport.Height / 2);
        var picturePoint = ToImage(anchor);
        var inViewport = anchor - new Point(scroller.Offset.X, scroller.Offset.Y);

        Zoom = zoom;
        scroller.UpdateLayout();

        var image = ImageRect;
        var scale = Scale;
        scroller.Offset = new Vector(
            image.X + picturePoint.X * scale - inViewport.X,
            image.Y + picturePoint.Y * scale - inViewport.Y);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (Source is null)
            return;

        if (e.Key == Key.A && e.KeyModifiers == KeyModifiers.Control)
        {
            Selection = new PixelRect(Source.PixelSize);
            e.Handled = true;
            return;
        }
        if (_selection is not { } selection)
            return;

        if (e.Key == Key.Escape)
        {
            Selection = null;
            e.Handled = true;
            return;
        }

        var step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 10 : 1;
        var (dx, dy) = e.Key switch
        {
            Key.Left => (-step, 0),
            Key.Right => (step, 0),
            Key.Up => (0, -step),
            Key.Down => (0, step),
            _ => (0, 0),
        };
        if (dx == 0 && dy == 0)
            return;

        var size = Source.PixelSize;
        Selection = new PixelRect(
            Math.Clamp(selection.X + dx, 0, size.Width - selection.Width),
            Math.Clamp(selection.Y + dy, 0, size.Height - selection.Height),
            selection.Width, selection.Height);
        e.Handled = true;
    }
}
