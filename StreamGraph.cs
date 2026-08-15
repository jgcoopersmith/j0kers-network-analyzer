using System.Diagnostics;
using System.Windows;
using System.Windows.Media;

namespace NetAnalyzer;

/// <summary>
/// A continuously scrolling traffic ribbon. New samples enter at the right edge and drift left at a
/// constant real-time speed; the ribbon's thickness tracks throughput — inbound swells above the
/// centre line, outbound below. Scrolling is driven by wall-clock time, so the flow stays smooth
/// and evenly paced no matter what polling interval is selected.
/// </summary>
public sealed class StreamGraph : FrameworkElement
{
    /// <summary>Half-thickness, in pixels, of a stream carrying no traffic.</summary>
    private const double IdleHalfThickness = 1.0;

    private readonly List<Sample> _history = new();
    private bool _hooked;

    /// <summary>
    /// One plotted knot. Each carries the consumer mix that was current when it was taken, so a
    /// change in the mix enters at the right edge and scrolls off with the traffic it describes
    /// rather than repainting history that was already drawn.
    /// </summary>
    private readonly record struct Sample(double Time, double In, double Out, TalkerMix? Mix);

    /// <summary>Seconds for the drawn level to close most of the gap to a new reading.</summary>
    private const double SmoothingSeconds = 0.55;

    /// <summary>Spacing of plotted knots. Dense enough that the curve reads as a continuous flow.</summary>
    private const double SampleSeconds = 0.04;

    private double _smoothIn, _smoothOut;
    private double _lastFrameTime;

    public static readonly DependencyProperty InLevelProperty = DependencyProperty.Register(
        nameof(InLevel), typeof(double), typeof(StreamGraph), new PropertyMetadata(0.0));

    public static readonly DependencyProperty OutLevelProperty = DependencyProperty.Register(
        nameof(OutLevel), typeof(double), typeof(StreamGraph), new PropertyMetadata(0.0));

    /// <summary>Scroll speed. Also sets how much history fits: width / this = seconds on screen.</summary>
    public static readonly DependencyProperty PixelsPerSecondProperty = DependencyProperty.Register(
        nameof(PixelsPerSecond), typeof(double), typeof(StreamGraph), new PropertyMetadata(60.0));

    /// <summary>
    /// How the traffic divides between the top consumers. Null draws the plain two-tone ribbon;
    /// otherwise the ribbon is split into bands, one per consumer, stacked outward from the spine
    /// in the same colours the tooltip lists them in.
    /// </summary>
    public static readonly DependencyProperty MixProperty = DependencyProperty.Register(
        nameof(Mix), typeof(TalkerMix), typeof(StreamGraph), new PropertyMetadata(null));

    public TalkerMix? Mix
    {
        get => (TalkerMix?)GetValue(MixProperty);
        set => SetValue(MixProperty, value);
    }

    public double InLevel
    {
        get => (double)GetValue(InLevelProperty);
        set => SetValue(InLevelProperty, value);
    }

    public double OutLevel
    {
        get => (double)GetValue(OutLevelProperty);
        set => SetValue(OutLevelProperty, value);
    }

    public double PixelsPerSecond
    {
        get => (double)GetValue(PixelsPerSecondProperty);
        set => SetValue(PixelsPerSecondProperty, value);
    }

    public StreamGraph()
    {
        Loaded += (_, _) => Hook();
        Unloaded += (_, _) => Unhook();
    }

    private static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    private void Hook()
    {
        if (_hooked)
            return;
        _lastFrameTime = Now;
        CompositionTarget.Rendering += OnFrame;
        _hooked = true;
    }

    private void Unhook()
    {
        if (!_hooked)
            return;
        CompositionTarget.Rendering -= OnFrame;
        _hooked = false;
        _history.Clear();
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        var now = Now;

        // A collapsed element is never rendered, so it must not keep collecting samples: the
        // history would grow without bound behind a hidden graph. Drop the backlog instead —
        // by the time it is shown again this data would all have scrolled off anyway.
        if (!IsVisible)
        {
            _history.Clear();
            _smoothIn = _smoothOut = 0;
            _lastFrameTime = now;
            return;
        }

        var dt = Math.Clamp(now - _lastFrameTime, 0, 0.5);
        _lastFrameTime = now;

        // Exponential low-pass on the raw poll values. Framing it as a time constant rather than
        // a per-frame fraction keeps the response identical at any frame rate or poll interval.
        var alpha = 1 - Math.Exp(-dt / SmoothingSeconds);
        _smoothIn += (Math.Clamp(InLevel, 0, 1) - _smoothIn) * alpha;
        _smoothOut += (Math.Clamp(OutLevel, 0, 1) - _smoothOut) * alpha;

        // Plot the smoothed value on a fixed cadence, independent of polling: the ribbon gets
        // closely spaced knots, so its outline curves instead of stepping from poll to poll.
        if (_history.Count == 0 || now - _history[^1].Time >= SampleSeconds)
            _history.Add(new Sample(now, _smoothIn, _smoothOut, Mix));

        // Prune here rather than in OnRender: pruning has to happen on the same schedule as
        // appending, and OnRender is not guaranteed to run.
        Prune(now);

        InvalidateVisual();
    }

    /// <summary>Drops samples that have scrolled past the left edge.</summary>
    private void Prune(double now)
    {
        var pps = Math.Max(1, PixelsPerSecond);
        var cutoff = now - (ActualWidth / pps) - 1.0;

        var stale = 0;
        while (stale < _history.Count && _history[stale].Time < cutoff)
            stale++;
        if (stale > 0)
            _history.RemoveRange(0, stale);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 0 || h <= 0)
            return;

        dc.DrawRoundedRectangle(TrackBrush, null, new Rect(0, 0, w, h), 3, 3);

        var centre = h / 2.0;
        var maxHalf = Math.Max(IdleHalfThickness, centre - 2);
        var pps = Math.Max(1, PixelsPerSecond);
        var now = Now;

        Prune(now);
        EnsureBrushes(centre, h);

        dc.PushClip(new RectangleGeometry(new Rect(0, 0, w, h), 3, 3));

        // Centre line: the stream's spine, visible wherever traffic is near zero.
        dc.DrawRectangle(SpineBrush, null, new Rect(0, centre - 0.5, w, 1));

        if (_history.Count > 0)
        {
            var inPoints = new List<Point>(_history.Count + 1);
            var outPoints = new List<Point>(_history.Count + 1);

            foreach (var s in _history)
            {
                var x = w - (now - s.Time) * pps;
                inPoints.Add(new Point(x, centre - Thickness(s.In, maxHalf)));
                outPoints.Add(new Point(x, centre + Thickness(s.Out, maxHalf)));
            }

            // Extend to the right edge with the live smoothed value so the head stays flush
            // between sample ticks rather than snapping forward every cadence.
            inPoints.Add(new Point(w, centre - Thickness(_smoothIn, maxHalf)));
            outPoints.Add(new Point(w, centre + Thickness(_smoothOut, maxHalf)));

            DrawRibbon(dc, inPoints, centre, upward: true);
            DrawRibbon(dc, outPoints, centre, upward: false);

            DrawBands(dc, w, centre, maxHalf, pps, now, inbound: true);
            DrawBands(dc, w, centre, maxHalf, pps, now, inbound: false);
        }

        dc.Pop();
    }

    /// <summary>
    /// Paints one coloured band per top consumer over the base ribbon, innermost first. Each band
    /// is the strip between two boundary curves, both drawn through the same knots as the ribbon
    /// outline so they bend with it. Whatever the consumers do not account for is left showing the
    /// base gradient, so unattributed traffic reads as it always did.
    /// </summary>
    private void DrawBands(DrawingContext dc, double w, double centre, double maxHalf,
        double pps, double now, bool inbound)
    {
        var count = _history.Count + 1;
        var lower = new List<Point>(count);
        var upper = new List<Point>(count);
        var sign = inbound ? -1 : 1;

        for (var band = 0; band < TalkerPalette.SlotCount; band++)
        {
            lower.Clear();
            upper.Clear();
            var occupied = false;

            foreach (var s in _history)
            {
                var x = w - (now - s.Time) * pps;
                var t = Thickness(inbound ? s.In : s.Out, maxHalf);
                var from = s.Mix?.Cum(band, inbound) ?? TalkerMix.NullCum(band);
                var to = s.Mix?.Cum(band + 1, inbound) ?? TalkerMix.NullCum(band + 1);
                occupied |= to - from > 0.0005;
                lower.Add(new Point(x, centre + sign * t * from));
                upper.Add(new Point(x, centre + sign * t * to));
            }

            // Head of the band follows the live smoothed value, like the ribbon outline.
            {
                var mix = Mix;
                var t = Thickness(inbound ? _smoothIn : _smoothOut, maxHalf);
                var from = mix?.Cum(band, inbound) ?? TalkerMix.NullCum(band);
                var to = mix?.Cum(band + 1, inbound) ?? TalkerMix.NullCum(band + 1);
                occupied |= to - from > 0.0005;
                lower.Add(new Point(w, centre + sign * t * from));
                upper.Add(new Point(w, centre + sign * t * to));
            }

            if (!occupied || lower.Count < 2)
                continue;

            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                ctx.BeginFigure(lower[0], isFilled: true, isClosed: true);
                Trace(ctx, lower, forward: true);
                ctx.LineTo(upper[^1], isStroked: true, isSmoothJoin: true);
                Trace(ctx, upper, forward: false);
            }
            geo.Freeze();

            // The hairline in the track colour keeps neighbouring bands apart even where two
            // consumers happen to land on similar hues.
            dc.DrawGeometry(TalkerPalette.Fill(band, inbound), BandGapPen, geo);
        }
    }

    /// <summary>Appends a smoothed path through <paramref name="points"/>, in either direction.</summary>
    private static void Trace(StreamGeometryContext ctx, List<Point> points, bool forward)
    {
        var n = points.Count;
        Point At(int i) => forward ? points[i] : points[n - 1 - i];

        for (var i = 1; i < n; i++)
        {
            var prev = At(i - 1);
            var cur = At(i);
            var mid = new Point((prev.X + cur.X) / 2, (prev.Y + cur.Y) / 2);
            ctx.QuadraticBezierTo(prev, mid, isStroked: true, isSmoothJoin: true);
            if (i == n - 1)
                ctx.LineTo(cur, isStroked: true, isSmoothJoin: true);
        }
    }

    private static double Thickness(double level, double maxHalf)
        => IdleHalfThickness + Math.Clamp(level, 0, 1) * (maxHalf - IdleHalfThickness);

    private void DrawRibbon(DrawingContext dc, List<Point> points, double centre, bool upward)
    {
        if (points.Count < 2)
            return;

        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(new Point(points[0].X, centre), isFilled: true, isClosed: true);
            ctx.LineTo(points[0], isStroked: true, isSmoothJoin: true);

            // Quadratic segments through sample midpoints: smooths the polyline without
            // overshooting, which matters when the polling interval is long.
            for (var i = 1; i < points.Count; i++)
            {
                var prev = points[i - 1];
                var cur = points[i];
                var mid = new Point((prev.X + cur.X) / 2, (prev.Y + cur.Y) / 2);
                ctx.QuadraticBezierTo(prev, mid, isStroked: true, isSmoothJoin: true);
                if (i == points.Count - 1)
                    ctx.LineTo(cur, isStroked: true, isSmoothJoin: true);
            }

            ctx.LineTo(new Point(points[^1].X, centre), isStroked: false, isSmoothJoin: false);
        }
        geo.Freeze();

        dc.DrawGeometry(
            upward ? _fillUp! : _fillDown!,
            upward ? PenUp : PenDown,
            geo);
    }

    // ---- Cached drawing resources ----
    //
    // OnRender runs at frame rate, so nothing here is rebuilt per frame. The gradients are the
    // only height-dependent pieces; everything else is fixed and shared across instances.

    private static readonly Brush TrackBrush = Frozen(Color.FromRgb(0x0E, 0x11, 0x16));
    private static readonly Brush SpineBrush = Frozen(Color.FromRgb(0x25, 0x2C, 0x37));

    private static readonly Color NearUp = Color.FromRgb(0x3A, 0xD1, 0x7E);
    private static readonly Color FarUp = Color.FromRgb(0xF2, 0x4E, 0x4E);
    private static readonly Color NearDown = Color.FromRgb(0x3A, 0xC6, 0xE8);
    private static readonly Color FarDown = Color.FromRgb(0xF0, 0x54, 0xC0);

    private static readonly Pen PenUp = FrozenPen(NearUp);
    private static readonly Pen PenDown = FrozenPen(NearDown);
    private static readonly Pen BandGapPen = FrozenPen(Color.FromRgb(0x0E, 0x11, 0x16));

    private LinearGradientBrush? _fillUp, _fillDown;
    private double _cachedCentre = -1, _cachedHeight = -1;

    private void EnsureBrushes(double centre, double h)
    {
        if (_fillUp is not null && _cachedCentre == centre && _cachedHeight == h)
            return;

        _fillUp = Ramp(centre, 0, NearUp, FarUp);
        _fillDown = Ramp(centre, h, NearDown, FarDown);
        _cachedCentre = centre;
        _cachedHeight = h;
    }

    /// <summary>
    /// Absolute mapping ties the ramp to the element rather than the geometry bounds, so a thin
    /// ribbon keeps its cool colour instead of stretching the whole ramp across two pixels.
    /// </summary>
    private static LinearGradientBrush Ramp(double from, double to, Color near, Color far)
    {
        var brush = new LinearGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute,
            StartPoint = new Point(0, from),
            EndPoint = new Point(0, to),
        };
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0xE0, near.R, near.G, near.B), 0));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0xC0, far.R, far.G, far.B), 1));
        brush.Freeze();
        return brush;
    }

    private static Brush Frozen(Color c)
    {
        var brush = new SolidColorBrush(c);
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenPen(Color c)
    {
        var pen = new Pen(Frozen(c), 1.0);
        pen.Freeze();
        return pen;
    }
}
