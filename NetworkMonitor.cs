using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.CompilerServices;
using System.Windows.Threading;

namespace NetAnalyzer;

/// <summary>Polls the OS interface counters on a configurable interval and publishes per-NIC meters.</summary>
public sealed class NetworkMonitor : INotifyPropertyChanged
{
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<string, InterfaceMeter> _byId = new();
    private long _lastTimestamp;
    private bool _polling;
    private int _intervalMs = 500;
    private bool _showInactive;
    private bool _showLoopback;
    private bool _useBits;
    private bool _streamView;
    private double _streamSpeed = 60;

    public NetworkMonitor()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(_intervalMs),
        };
        _timer.Tick += async (_, _) => await PollAsync();
    }

    public ObservableCollection<InterfaceMeter> Interfaces { get; } = new();

    /// <summary>Polling interval in milliseconds; takes effect on the next tick.</summary>
    public int IntervalMs
    {
        get => _intervalMs;
        set
        {
            var clamped = Math.Clamp(value, 100, 10000);
            if (!Set(ref _intervalMs, clamped))
                return;
            _timer.Interval = TimeSpan.FromMilliseconds(clamped);
            OnPropertyChanged(nameof(IntervalText));
        }
    }

    public string IntervalText => _intervalMs >= 1000
        ? $"{_intervalMs / 1000.0:0.##} s"
        : $"{_intervalMs} ms";

    /// <summary>Keep interfaces in the list even when the adapter is down or disconnected.</summary>
    public bool ShowInactive
    {
        get => _showInactive;
        set { if (Set(ref _showInactive, value)) Resync(); }
    }

    public bool ShowLoopback
    {
        get => _showLoopback;
        set { if (Set(ref _showLoopback, value)) Resync(); }
    }

    /// <summary>Display throughput in bits/sec rather than bytes/sec.</summary>
    public bool UseBits
    {
        get => _useBits;
        set
        {
            if (!Set(ref _useBits, value))
                return;
            foreach (var m in Interfaces)
            {
                m.UseBits = value;
                m.RefreshUnits();
            }
        }
    }

    /// <summary>False shows segmented spectrum bars; true shows the scrolling stream ribbon.</summary>
    public bool StreamView
    {
        get => _streamView;
        set
        {
            if (Set(ref _streamView, value))
                OnPropertyChanged(nameof(ViewModeText));
        }
    }

    public string ViewModeText => _streamView ? "View: Stream" : "View: Bars";

    /// <summary>How fast the stream ribbon scrolls, in pixels per second.</summary>
    public double StreamSpeed
    {
        get => _streamSpeed;
        set => Set(ref _streamSpeed, Math.Clamp(value, 10, 300));
    }

    public void Start()
    {
        _lastTimestamp = Stopwatch.GetTimestamp();
        _timer.Start();
        _ = PollAsync();
    }

    public void Stop() => _timer.Stop();

    public void ResetTotals()
    {
        foreach (var m in Interfaces)
            m.ResetTotals();
    }

    private async Task PollAsync()
    {
        if (_polling)
            return;
        _polling = true;
        try
        {
            // Enumeration touches the IP helper API, so keep it off the UI thread.
            var samples = await Task.Run(ReadSamples);

            var now = Stopwatch.GetTimestamp();
            var elapsed = (now - _lastTimestamp) / (double)Stopwatch.Frequency;
            _lastTimestamp = now;
            if (elapsed <= 0)
                return;

            Apply(samples, elapsed);
        }
        catch (NetworkInformationException)
        {
            // Adapter list changed underneath us; the next tick picks it up.
        }
        finally
        {
            _polling = false;
        }
    }

    private List<Sample> ReadSamples()
    {
        var list = new List<Sample>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback && !_showLoopback)
                continue;
            if (nic.OperationalStatus != OperationalStatus.Up && !_showInactive)
                continue;

            try
            {
                var stats = nic.GetIPStatistics();
                list.Add(new Sample(nic, nic.OperationalStatus, stats.BytesReceived, stats.BytesSent));
            }
            catch (NetworkInformationException)
            {
                // Some virtual adapters refuse statistics; just skip them this round.
            }
        }
        return list;
    }

    private void Apply(List<Sample> samples, double elapsed)
    {
        var seen = new HashSet<string>();

        foreach (var s in samples)
        {
            seen.Add(s.Nic.Id);
            if (!_byId.TryGetValue(s.Nic.Id, out var meter))
            {
                meter = new InterfaceMeter(s.Nic) { UseBits = _useBits };
                _byId[s.Nic.Id] = meter;
                Interfaces.Add(meter);
            }
            meter.Update(s.Status, s.BytesReceived, s.BytesSent, elapsed);
        }

        // Drop adapters that disappeared (VPN down, USB NIC unplugged, filter toggled).
        for (var i = Interfaces.Count - 1; i >= 0; i--)
        {
            var m = Interfaces[i];
            if (seen.Contains(m.Id))
                continue;
            _byId.Remove(m.Id);
            Interfaces.RemoveAt(i);
        }

        OnPropertyChanged(nameof(AggregateInText));
        OnPropertyChanged(nameof(AggregateOutText));
    }

    /// <summary>Forces a fresh enumeration after a filter change.</summary>
    private void Resync()
    {
        Interfaces.Clear();
        _byId.Clear();
        if (_timer.IsEnabled)
            _ = PollAsync();
    }

    /// <summary>Combined current throughput across every visible interface.</summary>
    public string AggregateInText => Rate.Format(Interfaces.Sum(m => m.InRate), _useBits);
    public string AggregateOutText => Rate.Format(Interfaces.Sum(m => m.OutRate), _useBits);

    private readonly record struct Sample(
        NetworkInterface Nic, OperationalStatus Status, long BytesReceived, long BytesSent);

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}
