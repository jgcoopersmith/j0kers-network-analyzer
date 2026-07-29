using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace NetAnalyzer;

public enum BarPalette
{
    /// <summary>Green → amber → red, used for inbound.</summary>
    Inbound,
    /// <summary>Cyan → violet → magenta, used for outbound.</summary>
    Outbound,
}

/// <summary>
/// A horizontal segmented level meter: lit blocks run left to right, with a peak-hold marker
/// that slides back after a burst. Levels arrive already normalized to 0..1.
/// </summary>
public sealed class SpectrumBar : FrameworkElement
{
    private const double SegmentWidth = 8.0;
    private const double SegmentGap = 3.0;

    /// <summary>Bar units the smoothed display level falls per second when the signal drops.</summary>
    private const double FallPerSecond = 2.2;

    private double _displayLevel;
    private DateTime _lastFrame = DateTime.UtcNow;
    private bool _hooked;

    public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(
        nameof(Level), typeof(double), typeof(SpectrumBar),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty PeakProperty = DependencyProperty.Register(
        nameof(Peak), typeof(double), typeof(SpectrumBar),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty PaletteProperty = DependencyProperty.Register(
        nameof(Palette), typeof(BarPalette), typeof(SpectrumBar),
        new FrameworkPropertyMetadata(BarPalette.Inbound, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Level
    {
        get => (double)GetValue(LevelProperty);
        set => SetValue(LevelProperty, value);
    }

    public double Peak
    {
        get => (double)GetValue(PeakProperty);
        set => SetValue(PeakProperty, value);
    }

    public BarPalette Palette
    {
        get => (BarPalette)GetValue(PaletteProperty);
        set => SetValue(PaletteProperty, value);
    }

    public SpectrumBar()
    {
        Loaded += (_, _) => Hook();
        Unloaded += (_, _) => Unhook();
    }

    private void Hook()
    {
        if (_hooked)
            return;
        _lastFrame = DateTime.UtcNow;
        CompositionTarget.Rendering += OnFrame;
        _hooked = true;
    }

    private void Unhook()
    {
        if (!_hooked)
            return;
        CompositionTarget.Rendering -= OnFrame;
        _hooked = false;
    }

    /// <summary>
    /// Eases the drawn level between polls: instant attack so spikes are never missed,
    /// timed release so the bar reads smoothly at any polling interval.
    /// </summary>
    private void OnFrame(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        var dt = (now - _lastFrame).TotalSeconds;
        _lastFrame = now;

        var target = Math.Clamp(Level, 0, 1);
        var next = target >= _displayLevel
            ? target
            : Math.Max(target, _displayLevel - FallPerSecond * dt);

        if (Math.Abs(next - _displayLevel) > 0.0005)
        {
            _displayLevel = next;
            InvalidateVisual();
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 0 || h <= 0)
            return;

        // Track behind the segments.
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0x0E, 0x11, 0x16)), null,
            new Rect(0, 0, w, h), 3, 3);

        var pitch = SegmentWidth + SegmentGap;
        var count = Math.Max(1, (int)((w + SegmentGap) / pitch));
        var peak = Math.Clamp(Peak, 0, 1);
        var peakIndex = peak > 0 ? (int)Math.Min(count - 1, peak * count) : -1;
        var litCount = (int)Math.Round(_displayLevel * count);

        for (var i = 0; i < count; i++)
        {
            var fraction = count == 1 ? 0 : i / (double)(count - 1);
            var x = i * pitch;
            var rect = new Rect(x, 0, SegmentWidth, h);

            Brush brush;
            if (i < litCount)
            {
                brush = new SolidColorBrush(ColorAt(fraction));
            }
            else if (i == peakIndex)
            {
                // Peak-hold marker: same hue as its position, dimmed but clearly visible.
                var c = ColorAt(fraction);
                brush = new SolidColorBrush(Color.FromArgb(0xB0, c.R, c.G, c.B));
            }
            else
            {
                var c = ColorAt(fraction);
                brush = new SolidColorBrush(Color.FromArgb(0x22, c.R, c.G, c.B));
            }

            brush.Freeze();
            dc.DrawRectangle(brush, null, rect);
        }
    }

    /// <summary>Interpolates the palette across the length of the bar.</summary>
    private Color ColorAt(double t)
    {
        return Palette == BarPalette.Inbound
            ? Ramp(t, Color.FromRgb(0x3A, 0xD1, 0x7E), Color.FromRgb(0xE8, 0xC4, 0x4A), Color.FromRgb(0xF2, 0x4E, 0x4E))
            : Ramp(t, Color.FromRgb(0x3A, 0xC6, 0xE8), Color.FromRgb(0x8A, 0x7A, 0xF0), Color.FromRgb(0xF0, 0x54, 0xC0));
    }

    private static Color Ramp(double t, Color a, Color b, Color c)
    {
        t = Math.Clamp(t, 0, 1);
        return t < 0.5 ? Lerp(a, b, t * 2) : Lerp(b, c, (t - 0.5) * 2);
    }

    private static Color Lerp(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t),
        (byte)(a.G + (b.G - a.G) * t),
        (byte)(a.B + (b.B - a.B) * t));
}
