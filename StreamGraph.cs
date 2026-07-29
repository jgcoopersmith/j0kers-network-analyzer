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

    private readonly record struct Sample(double Time, double In, double Out);

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
            _history.Add(new Sample(now, _smoothIn, _smoothOut));

        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 0 || h <= 0)
            return;

        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0x0E, 0x11, 0x16)), null,
            new Rect(0, 0, w, h), 3, 3);

        var centre = h / 2.0;
        var maxHalf = Math.Max(IdleHalfThickness, centre - 2);
        var pps = Math.Max(1, PixelsPerSecond);
        var now = Now;

        // Drop samples that have scrolled off the left edge.
        var cutoff = now - (w / pps) - 1.0;
        var stale = 0;
        while (stale < _history.Count && _history[stale].Time < cutoff)
            stale++;
        if (stale > 0)
            _history.RemoveRange(0, stale);

        dc.PushClip(new RectangleGeometry(new Rect(0, 0, w, h), 3, 3));

        // Centre line: the stream's spine, visible wherever traffic is near zero.
        var spine = new SolidColorBrush(Color.FromRgb(0x25, 0x2C, 0x37));
        spine.Freeze();
        dc.DrawRectangle(spine, null, new Rect(0, centre - 0.5, w, 1));

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

            DrawRibbon(dc, inPoints, centre, upward: true, h);
            DrawRibbon(dc, outPoints, centre, upward: false, h);
        }

        dc.Pop();
    }

    private static double Thickness(double level, double maxHalf)
        => IdleHalfThickness + Math.Clamp(level, 0, 1) * (maxHalf - IdleHalfThickness);

    private void DrawRibbon(DrawingContext dc, List<Point> points, double centre, bool upward, double h)
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

        // Colour ramps outward from the spine, so a fat stream reads hot at its edges.
        var near = upward ? Color.FromRgb(0x3A, 0xD1, 0x7E) : Color.FromRgb(0x3A, 0xC6, 0xE8);
        var far = upward ? Color.FromRgb(0xF2, 0x4E, 0x4E) : Color.FromRgb(0xF0, 0x54, 0xC0);

        // Absolute mapping ties the ramp to the element, not the geometry bounds, so a thin
        // ribbon keeps its cool colour instead of stretching the whole ramp across two pixels.
        var fill = new LinearGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute,
            StartPoint = new Point(0, centre),
            EndPoint = new Point(0, upward ? 0 : h),
        };
        fill.GradientStops.Add(new GradientStop(Color.FromArgb(0xE0, near.R, near.G, near.B), 0));
        fill.GradientStops.Add(new GradientStop(Color.FromArgb(0xC0, far.R, far.G, far.B), 1));
        fill.Freeze();

        var pen = new Pen(new SolidColorBrush(near), 1.0);
        pen.Freeze();

        dc.DrawGeometry(fill, pen, geo);
    }
}
