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

    /// <summary>Fraction of the adaptive scale headroom shed per second when traffic drops.</summary>
    private const double ScaleDecayPerSecond = 0.25;

    private double _inRate, _outRate;
    private double _inLevel, _outLevel;
    private double _inPeak, _outPeak;
    private double _totalIn, _totalOut;
    private double _scaleMax = MinScaleBytesPerSecond;
    private bool _isUp;
    private bool _showActivity = true;
    private DropHint _dropHint = DropHint.None;
    private string _status = "";

    public InterfaceMeter(NetworkInterface nic)
    {
        Id = nic.Id;
        Name = nic.Name;
        Description = nic.Description;
        InterfaceType = Describe(nic.NetworkInterfaceType);
        LinkSpeedBytesPerSecond = nic.Speed > 0 ? nic.Speed / 8.0 : 0;
        LinkSpeedText = nic.Speed > 0 ? Rate.Format(LinkSpeedBytesPerSecond, asBits: true) : "unknown";
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

    /// <summary>Zeroes the live readings without discarding session totals (used when a NIC goes quiet).</summary>
    public void Idle(double elapsedSeconds)
    {
        InRate = OutRate = 0;
        InLevel = OutLevel = 0;
        InPeak = DecayPeak(InPeak, 0, elapsedSeconds);
        OutPeak = DecayPeak(OutPeak, 0, elapsedSeconds);
        RaiseTextChanged();
    }

    public void ResetTotals()
    {
        TotalIn = TotalOut = 0;
        RaiseTextChanged();
    }

    /// <summary>
    /// Grows the scale instantly to fit new traffic and lets it sink back slowly, so a burst
    /// stays legible and an idle link doesn't turn background chatter into a full bar.
    /// </summary>
    private void UpdateScale(double peakRate, double elapsedSeconds)
    {
        var target = Math.Max(peakRate * 1.25, MinScaleBytesPerSecond);
        if (LinkSpeedBytesPerSecond > 0)
            target = Math.Min(target, Math.Max(LinkSpeedBytesPerSecond, MinScaleBytesPerSecond));

        if (target >= ScaleMax)
        {
            ScaleMax = target;
        }
        else
        {
            var decay = Math.Clamp(ScaleDecayPerSecond * elapsedSeconds, 0, 1);
            ScaleMax = ScaleMax + (target - ScaleMax) * decay;
        }
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
