using System.Windows.Media;

namespace NetAnalyzer;

/// <summary>
/// The colours the top bandwidth consumers are drawn in — one per slot, plus a neutral for
/// everything that falls outside the top few.
///
/// The five hues were checked for colour-vision separation against a dark surface rather than
/// picked by eye. The closest adjacent pair (amber and green) sits in the marginal band, which is
/// only acceptable because identity never rests on colour alone here: the tooltip pairs every
/// swatch with a name, and the ribbon keeps a fixed band order with a hairline between bands.
/// </summary>
public static class TalkerPalette
{
    /// <summary>How many applications get a colour of their own.</summary>
    public const int SlotCount = 5;

    /// <summary>Slots plus the neutral "everything else" band.</summary>
    public const int BandCount = SlotCount + 1;

    /// <summary>Band index of the neutral remainder.</summary>
    public const int OtherSlot = SlotCount;

    private static readonly Color[] SlotColors =
    {
        Color.FromRgb(0x2E, 0x9D, 0xCC),
        Color.FromRgb(0xB8, 0x87, 0x1E),
        Color.FromRgb(0xD4, 0x4A, 0x93),
        Color.FromRgb(0x2A, 0xA3, 0x5F),
        Color.FromRgb(0x85, 0x58, 0xD6),
    };

    private static readonly Color OtherColor = Color.FromRgb(0x55, 0x60, 0x6E);

    public static Color ColorFor(int slot)
        => slot >= 0 && slot < SlotCount ? SlotColors[slot] : OtherColor;

    // Brushes are built once and frozen: the ribbon asks for them at frame rate.
    private static readonly Brush[] SwatchBrushes = Build(0xFF);
    private static readonly Brush[] InboundBrushes = Build(0xE6);
    private static readonly Brush[] OutboundBrushes = Build(0xBE);

    /// <summary>Solid colour for the legend dot beside an application's name.</summary>
    public static Brush Swatch(int slot) => SwatchBrushes[Index(slot)];

    /// <summary>
    /// Ribbon fill. Outbound is carried at a lower opacity so the two halves of the stream still
    /// read as different directions once they are both split into the same coloured bands.
    /// </summary>
    public static Brush Fill(int slot, bool inbound)
        => (inbound ? InboundBrushes : OutboundBrushes)[Index(slot)];

    private static int Index(int slot)
        => slot >= 0 && slot < SlotCount ? slot : OtherSlot;

    private static Brush[] Build(byte alpha)
    {
        var brushes = new Brush[BandCount];
        for (var i = 0; i < BandCount; i++)
        {
            var c = ColorFor(i);
            var brush = new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));
            brush.Freeze();
            brushes[i] = brush;
        }
        return brushes;
    }
}

/// <summary>
/// Which application holds which colour on one interface.
///
/// A slot is held for as long as its application stays in the top few, so a process that slips
/// from first to third keeps the colour it had. Colours that track rank instead of identity make
/// the whole ribbon repaint whenever two applications trade places, which reads as a change in
/// the traffic itself.
/// </summary>
public sealed class TalkerSlots
{
    private readonly string?[] _slots = new string?[TalkerPalette.SlotCount];

    /// <summary>Re-seats the slots against the current leaders, ordered highest first.</summary>
    public void Sync(IReadOnlyList<string> leaders)
    {
        // Release slots whose holder has dropped out; everyone still present keeps their colour.
        for (var i = 0; i < _slots.Length; i++)
        {
            if (_slots[i] is { } held && !leaders.Contains(held, StringComparer.OrdinalIgnoreCase))
                _slots[i] = null;
        }

        foreach (var name in leaders)
        {
            if (SlotOf(name) >= 0)
                continue;

            var free = Array.IndexOf(_slots, null);
            if (free < 0)
                break;
            _slots[free] = name;
        }
    }

    /// <summary>The slot this application holds, or <see cref="TalkerPalette.OtherSlot"/>.</summary>
    public int SlotOf(string name)
    {
        for (var i = 0; i < _slots.Length; i++)
        {
            if (string.Equals(_slots[i], name, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return -1;
    }
}

/// <summary>
/// How one interface's traffic divides between the coloured slots, as cumulative fractions.
///
/// Immutable and shared: the ribbon stamps the current mix onto every sample it plots, so a
/// snapshot is held by hundreds of samples at once and must never be edited in place. Boundaries
/// are stored for all bands whether or not anything occupies them, so samples taken under
/// different mixes still line up band for band as they scroll past each other.
/// </summary>
public sealed class TalkerMix
{
    private readonly double[] _cumIn;
    private readonly double[] _cumOut;

    private TalkerMix(double[] cumIn, double[] cumOut)
    {
        _cumIn = cumIn;
        _cumOut = cumOut;
    }

    /// <summary>
    /// Builds a mix from per-band byte totals, or null when nothing was attributed — in which
    /// case the ribbon falls back to its plain two-tone gradient rather than showing a lie.
    /// </summary>
    public static TalkerMix? Build(double[] bytesIn, double[] bytesOut)
    {
        var cumIn = Cumulate(bytesIn);
        var cumOut = Cumulate(bytesOut);
        return cumIn is null && cumOut is null
            ? null
            : new TalkerMix(cumIn ?? Unattributed, cumOut ?? Unattributed);
    }

    /// <summary>Fraction of the ribbon's thickness consumed by bands below <paramref name="boundary"/>.</summary>
    public double Cum(int boundary, bool inbound)
        => (inbound ? _cumIn : _cumOut)[Math.Clamp(boundary, 0, TalkerPalette.BandCount)];

    /// <summary>Boundaries for a sample plotted before any mix was known: all of it is neutral.</summary>
    public static double NullCum(int boundary) => boundary >= TalkerPalette.BandCount ? 1 : 0;

    public bool HasBand(int band, bool inbound)
        => Cum(band + 1, inbound) - Cum(band, inbound) > 0.0005;

    /// <summary>Everything in the neutral band, used when one direction has no attribution.</summary>
    private static readonly double[] Unattributed = BuildUnattributed();

    private static double[] BuildUnattributed()
    {
        var cum = new double[TalkerPalette.BandCount + 1];
        cum[TalkerPalette.BandCount] = 1;
        return cum;
    }

    private static double[]? Cumulate(double[] bytes)
    {
        var total = 0.0;
        foreach (var b in bytes)
            total += b;
        if (total <= 0)
            return null;

        var cum = new double[TalkerPalette.BandCount + 1];
        var running = 0.0;
        for (var i = 0; i < TalkerPalette.BandCount; i++)
        {
            running += bytes[i];
            cum[i + 1] = running / total;
        }
        // Guard against rounding leaving a sliver of unpainted ribbon at the outer edge.
        cum[TalkerPalette.BandCount] = 1;
        return cum;
    }
}
