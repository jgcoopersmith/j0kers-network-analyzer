using System.Windows;
using System.Windows.Media;

namespace NetAnalyzer;

/// <summary>
/// A history graph in the style of the CPU widget's metric panels: one thin polyline per series,
/// newest sample at the right edge, and a wider panel buying more history rather than fatter
/// pixels. Here there is a line per top consumer, drawn upward from a centre spine for its share
/// of inbound and downward for outbound.
///
/// The per-application figures behind it come from a store bucketed by the minute, far too coarse
/// to plot directly. Each sample instead takes the consumer's <em>share</em> from the latest store
/// reading and applies it to the live interface rate, so the lines move at the polling interval
/// and only the split between them lags.
/// </summary>
public sealed class TalkerLineGraph : FrameworkElement
{
    /// <summary>Horizontal pixels per sample, matching the CPU widget's graphs.</summary>
    private const double PixelsPerSample = 3.0;

    private const int MinSamples = 40;
    private const int MaxSamples = 900;

    /// <summary>Lines are drawn for the named consumers only; the neutral remainder is not a series.</summary>
    private const int LineCount = TalkerPalette.SlotCount;

    /// <summary>One plotted moment: every consumer's rate in each direction, in bytes/sec.</summary>
    private readonly record struct Sample(double[] In, double[] Out);

    private readonly List<Sample> _history = new();
    private int _lastSequence = -1;

    public static readonly DependencyProperty InRateProperty = DependencyProperty.Register(
        nameof(InRate), typeof(double), typeof(TalkerLineGraph), new PropertyMetadata(0.0));

    public static readonly DependencyProperty OutRateProperty = DependencyProperty.Register(
        nameof(OutRate), typeof(double), typeof(TalkerLineGraph), new PropertyMetadata(0.0));

    public static readonly DependencyProperty ScaleMaxProperty = DependencyProperty.Register(
        nameof(ScaleMax), typeof(double), typeof(TalkerLineGraph), new PropertyMetadata(1.0));

    public static readonly DependencyProperty MixProperty = DependencyProperty.Register(
        nameof(Mix), typeof(TalkerMix), typeof(TalkerLineGraph), new PropertyMetadata(null));

    /// <summary>
    /// Bumped once per poll by the meter. Sampling off this rather than off a rate change keeps
    /// one sample per reading: the two rates are separate properties and arrive separately, and
    /// a quiet interface can report the same rate twice without that meaning no time passed.
    /// </summary>
    public static readonly DependencyProperty SequenceProperty = DependencyProperty.Register(
        nameof(Sequence), typeof(int), typeof(TalkerLineGraph),
        new PropertyMetadata(0, OnSequenceChanged));

    public double InRate
    {
        get => (double)GetValue(InRateProperty);
        set => SetValue(InRateProperty, value);
    }

    public double OutRate
    {
        get => (double)GetValue(OutRateProperty);
        set => SetValue(OutRateProperty, value);
    }

    public double ScaleMax
    {
        get => (double)GetValue(ScaleMaxProperty);
        set => SetValue(ScaleMaxProperty, value);
    }

    public TalkerMix? Mix
    {
        get => (TalkerMix?)GetValue(MixProperty);
        set => SetValue(MixProperty, value);
    }

    public int Sequence
    {
        get => (int)GetValue(SequenceProperty);
        set => SetValue(SequenceProperty, value);
    }

    private static void OnSequenceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((TalkerLineGraph)d).Append((int)e.NewValue);

    /// <summary>Samples that fit at the current width. Narrowing drops the oldest.</summary>
    private int Capacity => ActualWidth <= 1
        ? MinSamples
        : Math.Clamp((int)(ActualWidth / PixelsPerSample), MinSamples, MaxSamples);

    private void Append(int sequence)
    {
        // A collapsed graph is never drawn, so it must not keep collecting: the history would
        // grow behind a hidden element and every sample would be stale by the time it showed.
        if (!IsVisible)
        {
            _history.Clear();
            _lastSequence = sequence;
            return;
        }

        if (sequence == _lastSequence)
            return;
        _lastSequence = sequence;

        var mix = Mix;
        var inRates = new double[LineCount];
        var outRates = new double[LineCount];

        for (var i = 0; i < LineCount; i++)
        {
            // Share of the interface's traffic this consumer holds, applied to the live rate.
            var shareIn = mix is null ? 0 : mix.Cum(i + 1, inbound: true) - mix.Cum(i, inbound: true);
            var shareOut = mix is null ? 0 : mix.Cum(i + 1, inbound: false) - mix.Cum(i, inbound: false);
            inRates[i] = InRate * shareIn;
            outRates[i] = OutRate * shareOut;
        }

        _history.Add(new Sample(inRates, outRates));
        Trim();
        InvalidateVisual();
    }

    private void Trim()
    {
        var capacity = Capacity;
        if (_history.Count > capacity)
            _history.RemoveRange(0, _history.Count - capacity);
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);
        Trim();
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 0 || h <= 0)
            return;

        // Faint panel behind the plot, as the CPU widget's graphs use.
        dc.DrawRoundedRectangle(PanelBrush, null, new Rect(0, 0, w, h), 5, 5);

        var centre = h / 2.0;
        dc.DrawRectangle(SpineBrush, null, new Rect(0, centre - 0.5, w, 1));

        if (_history.Count < 2)
            return;

        dc.PushClip(new RectangleGeometry(new Rect(0, 0, w, h), 5, 5));

        var capacity = Capacity;
        var step = w / (capacity - 1);
        var first = capacity - _history.Count;
        var reach = Math.Max(1.0, centre - 2);
        var scale = ScaleMax;

        for (var line = 0; line < LineCount; line++)
        {
            DrawSeries(dc, line, first, step, centre, reach, scale, inbound: true);
            DrawSeries(dc, line, first, step, centre, reach, scale, inbound: false);
        }

        dc.Pop();
    }

    /// <summary>
    /// Plots one consumer in one direction. A series that never leaves the spine across the whole
    /// history is skipped rather than drawn flat: with five consumers in two directions, ten lines
    /// stacked on the centre would read as a thick band whenever the interface is quiet.
    /// </summary>
    private void DrawSeries(DrawingContext dc, int line, int first, double step,
        double centre, double reach, double scale, bool inbound)
    {
        var sign = inbound ? -1.0 : 1.0;
        var geo = new StreamGeometry();
        var moved = false;
        var carries = false;

        using (var ctx = geo.Open())
        {
            for (var i = 0; i < _history.Count; i++)
            {
                var rate = inbound ? _history[i].In[line] : _history[i].Out[line];
                if (rate > 0)
                    carries = true;

                var y = centre + sign * InterfaceMeter.Level(rate, scale) * reach;
                var p = new Point((first + i) * step, y);

                if (!moved)
                {
                    ctx.BeginFigure(p, isFilled: false, isClosed: false);
                    moved = true;
                }
                else
                {
                    ctx.LineTo(p, isStroked: true, isSmoothJoin: true);
                }
            }
        }

        if (!carries)
            return;

        geo.Freeze();
        dc.DrawGeometry(null, Pens[line], geo);
    }

    // ---- Drawing resources ----
    //
    // Frozen and shared: OnRender walks ten series per interface and must not allocate brushes.

    private static readonly Brush PanelBrush = Frozen(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
    private static readonly Brush SpineBrush = Frozen(Color.FromRgb(0x25, 0x2C, 0x37));
    private static readonly Pen[] Pens = BuildPens();

    private static Brush Frozen(Color c)
    {
        var brush = new SolidColorBrush(c);
        brush.Freeze();
        return brush;
    }

    private static Pen[] BuildPens()
    {
        var pens = new Pen[LineCount];
        for (var i = 0; i < LineCount; i++)
        {
            // 1.4px with round joins, matching the CPU widget's usage line.
            var pen = new Pen(Frozen(TalkerPalette.ColorFor(i)), 1.4)
            {
                LineJoin = PenLineJoin.Round,
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
            };
            pen.Freeze();
            pens[i] = pen;
        }
        return pens;
    }
}
