using System.ComponentModel;
using System.Windows;
using System.Windows.Media;

namespace NetAnalyzer;

/// <summary>
/// Keeps the top-consumers tooltip in step with the graph it is describing for as long as it is
/// open, rather than only at the moment it opened.
///
/// The tooltip stays up for up to a minute while the graph under it keeps moving: new colours
/// enter at the right edge every few seconds. Filled once, the list goes stale within seconds and
/// the colours arriving after it opened are exactly the ones it cannot name. So it is refilled
/// on every reading of the hovered interface, which is also when the graph plots.
/// </summary>
public sealed class TalkerLegend
{
    private readonly Func<bool> _useBits;
    private InterfaceMeter? _meter;
    private FrameworkElement? _host;

    public TalkerLegend(Func<bool> useBits) => _useBits = useBits;

    /// <summary>Starts following <paramref name="meter"/>, whose graph sits somewhere under <paramref name="host"/>.</summary>
    public void Open(FrameworkElement host, InterfaceMeter meter)
    {
        Close();
        _host = host;
        _meter = meter;
        _meter.PropertyChanged += OnMeterChanged;
        Refresh();
    }

    /// <summary>Stops following. Safe to call when nothing is open.</summary>
    public void Close()
    {
        if (_meter is not null)
            _meter.PropertyChanged -= OnMeterChanged;
        _meter = null;
        _host = null;
    }

    private void OnMeterChanged(object? sender, PropertyChangedEventArgs e)
    {
        // One refresh per reading: the meter raises a dozen property changes per poll and the
        // sequence number is the last of them, so by then the line graph has plotted it. Also on
        // a new mix, which the ribbon paints at its head straight away rather than on the poll.
        if (e.PropertyName is nameof(InterfaceMeter.SampleSequence) or nameof(InterfaceMeter.Mix))
            Refresh();
    }

    private void Refresh()
    {
        if (_meter is null || _host is null)
            return;
        TopTalkers.Instance.Show(_meter, _useBits(), DrawnUnder(_host));
    }

    /// <summary>
    /// Finds the consumer graph inside a hovered meter and asks it what it is drawing. Both graphs
    /// live in the same cell with only one of them visible, so the search skips anything
    /// collapsed. Null when neither is present — a collapsed card, or the bars view.
    /// </summary>
    public static IReadOnlyList<DrawnConsumer>? DrawnUnder(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);

            if (child is UIElement { Visibility: not Visibility.Visible })
                continue;

            switch (child)
            {
                case TalkerLineGraph lines:
                    return lines.DrawnConsumers();
                case StreamGraph ribbon:
                    return ribbon.DrawnConsumers();
            }

            if (DrawnUnder(child) is { } found)
                return found;
        }

        return null;
    }
}
