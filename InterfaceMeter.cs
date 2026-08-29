using System.ComponentModel;
using System.Net.NetworkInformation;
using System.Runtime.CompilerServices;

namespace NetAnalyzer;

/// <summary>Where a dragged interface would land relative to this one.</summary>
public enum DropHint
{
    None,
    Above,
    Below,
}

/// <summary>Live throughput state for a single network interface.</summary>
public sealed class InterfaceMeter : INotifyPropertyChanged
{
    /// <summary>Bottom of the logarithmic scale: rates below this read as silence.</summary>
    public const double FloorBytesPerSecond = 512.0;

    /// <summary>Smallest top-of-scale, so an idle adapter doesn't amplify noise.</summary>
    private const double MinScaleBytesPerSecond = 64.0 * 1024.0;

    /// <summary>
    /// Above this, a reported link speed is treated as fiction rather than a ceiling. The VPN
    /// pseudo-adapters advertise 100 Gb/s; scaling against that would draw every byte they carry
    /// as a flat line.
    /// </summary>
    private const double MaxCredibleLinkBytesPerSecond = 10_000_000_000.0 / 8.0;

    /// <summary>Headroom above the held peak on the adaptive path, so a burst isn't flush at the top.</summary>
    private const double ScaleHeadroom = 1.25;

    /// <summary>
    /// Half-life of the held peak on the adaptive path. Long enough that a burst still gives
    /// context to the quiet stretch after it, which is what makes a trickle read as a trickle.
    /// </summary>
    private const double PeakHalfLifeSeconds = 30.0;

    private double _inRate, _outRate;
    private double _inLevel, _outLevel;
    private double _inPeak, _outPeak;
    private double _totalIn, _totalOut;
    private double _scaleMax = MinScaleBytesPerSecond;

    /// <summary>Decaying high-water mark driving the scale on adapters with no credible link speed.</summary>
    private double _heldPeak;

    /// <summary>
    /// Fixed top-of-scale for adapters that report a link speed we can believe, or zero to put
    /// this adapter on the adaptive path. Fixed is what lets two interfaces be read against each
    /// other: the same rate has to draw the same thickness everywhere for a busy link to look
    /// busier than a quiet one.
    /// </summary>
    private readonly double _fixedCeiling;

    private bool _isUp;
    private bool _showActivity = true;
    private DropHint _dropHint = DropHint.None;
    private string _status = "";
    private TalkerMix? _mix;
    private string _addressText = "";
    private bool _isDefaultRoute;

    public InterfaceMeter(NetworkInterface nic)
    {
        Id = nic.Id;
        Name = nic.Name;
        Description = nic.Description;
        InterfaceType = Describe(nic.NetworkInterfaceType);
        LinkSpeedBytesPerSecond = nic.Speed > 0 ? nic.Speed / 8.0 : 0;
        LinkSpeedText = nic.Speed > 0 ? Rate.Format(LinkSpeedBytesPerSecond, asBits: true) : "unknown";

        // Disconnected adapters report -1, and the tunnel adapters report a number they invented,
        // so neither reaching here is a fault worth surfacing — both just fall back to adaptive.
        _fixedCeiling = LinkSpeedBytesPerSecond > 0
            && LinkSpeedBytesPerSecond <= MaxCredibleLinkBytesPerSecond
            ? Math.Max(LinkSpeedBytesPerSecond, MinScaleBytesPerSecond)
            : 0;

        if (_fixedCeiling > 0)
            _scaleMax = _fixedCeiling;
    }

    public string Id { get; }
    public string Name { get; }
    public string Description { get; }
    public string InterfaceType { get; }
    public double LinkSpeedBytesPerSecond { get; }
    public string LinkSpeedText { get; }

    /// <summary>Raw counters from the previous poll, used to derive the delta.</summary>
    public long LastBytesReceived { get; set; } = -1;
    public long LastBytesSent { get; set; } = -1;

    public bool IsUp { get => _isUp; private set => Set(ref _isUp, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }

    public double InRate { get => _inRate; private set => Set(ref _inRate, value); }
    public double OutRate { get => _outRate; private set => Set(ref _outRate, value); }

    /// <summary>Normalized 0..1 bar fill for inbound / outbound.</summary>
    public double InLevel { get => _inLevel; private set => Set(ref _inLevel, value); }
    public double OutLevel { get => _outLevel; private set => Set(ref _outLevel, value); }

    /// <summary>Decaying peak-hold marker positions, 0..1.</summary>
    public double InPeak { get => _inPeak; private set => Set(ref _inPeak, value); }
    public double OutPeak { get => _outPeak; private set => Set(ref _outPeak, value); }

    public double TotalIn { get => _totalIn; private set => Set(ref _totalIn, value); }
    public double TotalOut { get => _totalOut; private set => Set(ref _totalOut, value); }

    /// <summary>Top of the current logarithmic scale, in bytes/sec.</summary>
    public double ScaleMax { get => _scaleMax; private set => Set(ref _scaleMax, value); }

    public bool UseBits { get; set; }

    /// <summary>
    /// Whether this interface draws its meters. Turning it off collapses the card to a single
    /// header line with an inline rate summary — counters and totals keep running underneath.
    /// </summary>
    public bool ShowActivity
    {
        get => _showActivity;
        set => Set(ref _showActivity, value);
    }

    /// <summary>
    /// How this interface's traffic currently divides between the top consumers, or null when
    /// none could be attributed. The ribbon colours its bands from this.
    /// </summary>
    public TalkerMix? Mix
    {
        get => _mix;
        set => Set(ref _mix, value);
    }

    /// <summary>
    /// This adapter's IPv4 address and prefix, e.g. "192.168.8.124/24", or empty when it holds
    /// none. Shown in the header because the adapter name says nothing about which network it
    /// carries: without it there is no way to tell from the window that 192.168.8.x traffic
    /// belongs on Wi-Fi and everything else leaves by the wire.
    /// </summary>
    public string AddressText
    {
        get => _addressText;
        private set => Set(ref _addressText, value);
    }

    /// <summary>
    /// Whether this is the adapter Windows picks for a destination it has no specific route to.
    /// Two adapters can both carry a default route, and only one of them wins — which is what
    /// sends traffic out the wire that looks like it should have gone wireless.
    /// </summary>
    public bool IsDefaultRoute
    {
        get => _isDefaultRoute;
        private set => Set(ref _isDefaultRoute, value);
    }

    /// <summary>Applies freshly read addressing. Called off the poll, not per tick.</summary>
    public void SetAddressing(string addressText, bool isDefaultRoute)
    {
        AddressText = addressText;
        IsDefaultRoute = isDefaultRoute;
    }

    /// <summary>Drives the insertion marker drawn above or below this card while dragging.</summary>
    public DropHint DropHint
    {
        get => _dropHint;
        set => Set(ref _dropHint, value);
    }

    /// <summary>Compact one-line rate readout, shown in the header while the meters are collapsed.</summary>
    public string SummaryText => $"↓ {Rate.Format(InRate, UseBits)}    ↑ {Rate.Format(OutRate, UseBits)}";

    // Display strings, refreshed alongside the numeric values.
    public string InRateText => Rate.Format(InRate, UseBits);
    public string OutRateText => Rate.Format(OutRate, UseBits);
    public string TotalInText => Rate.FormatBytes(TotalIn);
    public string TotalOutText => Rate.FormatBytes(TotalOut);
    public string ScaleText => $"scale {Rate.Format(ScaleMax, UseBits)}";

    /// <summary>Applies a new counter reading taken <paramref name="elapsedSeconds"/> after the last one.</summary>
    public void Update(OperationalStatus status, long bytesReceived, long bytesSent, double elapsedSeconds)
    {
        IsUp = status == OperationalStatus.Up;
        Status = status.ToString();

        double inRate = 0, outRate = 0;
        if (LastBytesReceived >= 0 && elapsedSeconds > 0)
        {
            // A negative delta means the counter wrapped or the adapter reset; skip that sample.
            var dIn = bytesReceived - LastBytesReceived;
            var dOut = bytesSent - LastBytesSent;
            if (dIn >= 0 && dOut >= 0)
            {
                inRate = dIn / elapsedSeconds;
                outRate = dOut / elapsedSeconds;
                TotalIn += dIn;
                TotalOut += dOut;
            }
        }

        LastBytesReceived = bytesReceived;
        LastBytesSent = bytesSent;

        InRate = inRate;
        OutRate = outRate;

        UpdateScale(Math.Max(inRate, outRate), elapsedSeconds);

        InLevel = ToLevel(inRate);
        OutLevel = ToLevel(outRate);

        InPeak = DecayPeak(InPeak, InLevel, elapsedSeconds);
        OutPeak = DecayPeak(OutPeak, OutLevel, elapsedSeconds);

        RaiseTextChanged();
    }

    /// <summary>
    /// Forgets the counter baseline so the next reading starts a fresh interval. Used when
    /// resuming after a pause: without this, the bytes that accumulated while paused would be
    /// divided by the few milliseconds since the timer restarted, reading as an enormous spike.
    /// </summary>
    public void ResetBaseline()
    {
        LastBytesReceived = -1;
        LastBytesSent = -1;
        InRate = OutRate = 0;
        InLevel = OutLevel = 0;
        RaiseTextChanged();
    }

    public void ResetTotals()
    {
        TotalIn = TotalOut = 0;
        RaiseTextChanged();
    }

    /// <summary>
    /// Sets the top of the logarithmic scale.
    ///
    /// Where the adapter reports a credible link speed the ceiling is that speed and never moves,
    /// so a given rate draws the same thickness on every interface and a quiet link reads as quiet
    /// beside a busy one. The ceiling used to be derived from the very rate it was scaling —
    /// max(rate * 1.25, ...) — so it rose in lockstep with the signal and left everything above a
    /// few tens of KB/s pinned within a couple of percent of full: a 260 KB/s background trickle
    /// and a 173 MB/s transfer drew the same ribbon, which read as traffic landing on the wrong
    /// interface.
    ///
    /// Adapters with no usable link speed keep an adaptive ceiling, but taken off a slowly
    /// decaying held peak rather than off the live sample, so a burst still gives context to the
    /// quiet stretch that follows it instead of the quiet stretch re-inflating to fill the meter.
    /// </summary>
    private void UpdateScale(double peakRate, double elapsedSeconds)
    {
        if (_fixedCeiling > 0)
        {
            ScaleMax = _fixedCeiling;
            return;
        }

        if (peakRate >= _heldPeak)
            _heldPeak = peakRate;
        else
            _heldPeak *= Math.Pow(0.5, elapsedSeconds / PeakHalfLifeSeconds);

        ScaleMax = Math.Max(_heldPeak * ScaleHeadroom, MinScaleBytesPerSecond);
    }

    /// <summary>Maps a rate onto 0..1 logarithmically, the way an audio spectrum meter does.</summary>
    private double ToLevel(double rate)
    {
        if (rate <= FloorBytesPerSecond)
            return 0;

        var top = Math.Max(ScaleMax, FloorBytesPerSecond * 2);
        var level = Math.Log(rate / FloorBytesPerSecond) / Math.Log(top / FloorBytesPerSecond);
        return Math.Clamp(level, 0, 1);
    }

    private static double DecayPeak(double peak, double level, double elapsedSeconds)
    {
        if (level >= peak)
            return level;

        // Peak marker slides back at ~40% of the bar per second.
        return Math.Max(level, peak - 0.4 * elapsedSeconds);
    }

    private void RaiseTextChanged()
    {
        OnPropertyChanged(nameof(InRateText));
        OnPropertyChanged(nameof(OutRateText));
        OnPropertyChanged(nameof(TotalInText));
        OnPropertyChanged(nameof(TotalOutText));
        OnPropertyChanged(nameof(ScaleText));
        OnPropertyChanged(nameof(SummaryText));
    }

    /// <summary>Called when the bytes/bits display toggle changes.</summary>
    public void RefreshUnits() => RaiseTextChanged();

    private static string Describe(NetworkInterfaceType type) => type switch
    {
        NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet
            or NetworkInterfaceType.FastEthernetT or NetworkInterfaceType.FastEthernetFx => "Ethernet",
        NetworkInterfaceType.Wireless80211 => "Wi-Fi",
        NetworkInterfaceType.Loopback => "Loopback",
        NetworkInterfaceType.Ppp => "PPP",
        NetworkInterfaceType.Tunnel => "Tunnel",
        _ => type.ToString(),
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        OnPropertyChanged(name);
    }
}
