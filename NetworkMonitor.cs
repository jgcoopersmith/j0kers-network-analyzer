using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.CompilerServices;
using System.Windows.Threading;

namespace NetAnalyzer;

/// <summary>How the interface list is drawn.</summary>
public enum ViewMode
{
    /// <summary>Segmented spectrum meters with peak hold.</summary>
    Bars,
    /// <summary>Right-to-left flowing ribbon.</summary>
    Stream,
    /// <summary>Compact always-on-top panel: ribbons and rates for the selected interfaces only.</summary>
    Widget,
}

/// <summary>Polls the OS interface counters on a configurable interval and publishes per-NIC meters.</summary>
public sealed class NetworkMonitor : INotifyPropertyChanged
{
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<string, InterfaceMeter> _byId = new();

    /// <summary>Saved per-interface state, keyed by adapter id, including adapters not currently visible.</summary>
    private readonly Dictionary<string, InterfaceSetting> _saved = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Saved display order as adapter ids; position in this list is the sort rank.</summary>
    private List<string> _savedOrder = new();

    private long _lastTimestamp;
    private bool _polling;
    private int _intervalMs = 500;
    private bool _showInactive;
    private bool _showLoopback;
    private bool _hideFilterAdapters = true;
    private bool _useBits;
    private ViewMode _mode = ViewMode.Bars;
    private double _streamSpeed = 60;
    private bool _minimizeOnClose;
    private bool _minimizeToTray;

    public NetworkMonitor()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(_intervalMs),
        };
        _timer.Tick += async (_, _) => await PollAsync();

        // A drag reorder shows up here as a Move; that is a preference worth persisting.
        Interfaces.CollectionChanged += (_, e) =>
        {
            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Move)
                SettingsChanged?.Invoke();
        };
    }

    public ObservableCollection<InterfaceMeter> Interfaces { get; } = new();

    /// <summary>Raised whenever something worth writing to the settings file changes.</summary>
    public event Action? SettingsChanged;

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

    /// <summary>Hides the WFP/QoS filter pseudo-adapters Windows stacks on each real NIC.</summary>
    public bool HideFilterAdapters
    {
        get => _hideFilterAdapters;
        set { if (Set(ref _hideFilterAdapters, value)) Resync(); }
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

    /// <summary>Which of the three presentations is active.</summary>
    public ViewMode Mode
    {
        get => _mode;
        set
        {
            if (Set(ref _mode, value))
                OnPropertyChanged(nameof(ViewModeText));
        }
    }

    /// <summary>Advances Bars → Stream → Widget → Bars.</summary>
    public void CycleMode() => Mode = _mode switch
    {
        ViewMode.Bars => ViewMode.Stream,
        ViewMode.Stream => ViewMode.Widget,
        _ => ViewMode.Bars,
    };

    public string ViewModeText => $"View: {_mode}";

    /// <summary>When set, closing the window minimizes it to the taskbar instead of exiting.</summary>
    public bool MinimizeOnClose
    {
        get => _minimizeOnClose;
        set => Set(ref _minimizeOnClose, value);
    }

    /// <summary>
    /// When set, minimizing hides the window into the notification area rather than the taskbar.
    /// Also applies when closing, if <see cref="MinimizeOnClose"/> is on.
    /// </summary>
    public bool MinimizeToTray
    {
        get => _minimizeToTray;
        set => Set(ref _minimizeToTray, value);
    }

    /// <summary>How fast the stream ribbon scrolls, in pixels per second.</summary>
    public double StreamSpeed
    {
        get => _streamSpeed;
        set => Set(ref _streamSpeed, Math.Clamp(value, 10, 300));
    }

    /// <summary>Restores saved preferences. Call before <see cref="Start"/>.</summary>
    public void ApplySettings(AppSettings s)
    {
        // Assign fields directly: going through the setters would fire SettingsChanged for
        // every value and write the file back out during startup.
        _intervalMs = Math.Clamp(s.IntervalMs, 100, 10000);
        _timer.Interval = TimeSpan.FromMilliseconds(_intervalMs);
        _useBits = s.UseBits;
        _showInactive = s.ShowInactive;
        _showLoopback = s.ShowLoopback;
        _hideFilterAdapters = s.HideFilterAdapters;
        // Fall back to the old boolean when reading a settings file written before view modes.
        _mode = Enum.TryParse<ViewMode>(s.ViewMode, ignoreCase: true, out var mode)
            ? mode
            : s.StreamView ? ViewMode.Stream : ViewMode.Bars;
        _streamSpeed = Math.Clamp(s.StreamSpeed, 10, 300);
        _minimizeOnClose = s.MinimizeOnClose;
        _minimizeToTray = s.MinimizeToTray;

        _saved.Clear();
        _savedOrder = new List<string>();
        foreach (var i in s.Interfaces)
        {
            if (string.IsNullOrEmpty(i.Id) || _saved.ContainsKey(i.Id))
                continue;
            _saved[i.Id] = i;
            _savedOrder.Add(i.Id);
        }

        foreach (var name in new[]
        {
            nameof(IntervalMs), nameof(IntervalText), nameof(UseBits), nameof(ShowInactive),
            nameof(ShowLoopback), nameof(HideFilterAdapters), nameof(Mode),
            nameof(ViewModeText), nameof(StreamSpeed),
            nameof(MinimizeOnClose), nameof(MinimizeToTray),
        })
        {
            OnPropertyChanged(name);
        }
    }

    /// <summary>Snapshots current preferences for writing to disk.</summary>
    public AppSettings CaptureSettings() => new()
    {
        IntervalMs = _intervalMs,
        UseBits = _useBits,
        ShowInactive = _showInactive,
        ShowLoopback = _showLoopback,
        HideFilterAdapters = _hideFilterAdapters,
        ViewMode = _mode.ToString(),
        StreamView = _mode == NetAnalyzer.ViewMode.Stream,
        StreamSpeed = _streamSpeed,
        MinimizeOnClose = _minimizeOnClose,
        MinimizeToTray = _minimizeToTray,
        Interfaces = CaptureInterfaces(),
    };

    /// <summary>
    /// Visible interfaces first, in display order, followed by adapters we know about but are not
    /// showing right now — a filtered-out or unplugged NIC keeps its settings for next time.
    /// </summary>
    private List<InterfaceSetting> CaptureInterfaces()
    {
        var list = new List<InterfaceSetting>(Interfaces.Count + _saved.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var m in Interfaces)
        {
            list.Add(new InterfaceSetting { Id = m.Id, Name = m.Name, ShowActivity = m.ShowActivity });
            seen.Add(m.Id);
        }

        foreach (var id in _savedOrder)
        {
            if (seen.Add(id) && _saved.TryGetValue(id, out var saved))
                list.Add(saved);
        }

        return list;
    }

    /// <summary>
    /// Where a newly discovered adapter belongs, per the saved order. Adapters with no saved
    /// position sort to the end, so a NIC that appears mid-session lands at the bottom.
    /// </summary>
    private int RankedIndex(string id)
    {
        var rank = OrderRank(id);
        for (var i = 0; i < Interfaces.Count; i++)
        {
            if (OrderRank(Interfaces[i].Id) > rank)
                return i;
        }
        return Interfaces.Count;
    }

    private int OrderRank(string id)
    {
        var index = _savedOrder.IndexOf(id);
        return index < 0 ? int.MaxValue : index;
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
            if (_hideFilterAdapters && IsFilterAdapter(nic))
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

    /// <summary>
    /// Windows exposes an entry for each filter driver bound to a NIC — QoS scheduling, WFP MAC
    /// layer shims, the WiFi virtualization filters. They report the same traffic as the adapter
    /// they sit on, so counting them means counting everything several times over.
    /// </summary>
    private static readonly string[] FilterAdapterMarkers =
    {
        "LightWeight Filter",
        "Filter Driver",
        "QoS Packet Scheduler",
        "Packet Scheduler Miniport",
    };

    private static bool IsFilterAdapter(NetworkInterface nic)
    {
        foreach (var marker in FilterAdapterMarkers)
        {
            if (nic.Name.Contains(marker, StringComparison.OrdinalIgnoreCase) ||
                nic.Description.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private void Apply(List<Sample> samples, double elapsed)
    {
        var seen = new HashSet<string>();

        foreach (var s in samples)
        {
            seen.Add(s.Nic.Id);
            if (!_byId.TryGetValue(s.Nic.Id, out var meter))
            {
                meter = new InterfaceMeter(s.Nic)
                {
                    UseBits = _useBits,
                    ShowActivity = !_saved.TryGetValue(s.Nic.Id, out var saved) || saved.ShowActivity,
                };
                meter.PropertyChanged += Meter_PropertyChanged;
                _byId[s.Nic.Id] = meter;
                Interfaces.Insert(RankedIndex(s.Nic.Id), meter);
            }
            meter.Update(s.Status, s.BytesReceived, s.BytesSent, elapsed);
        }

        // Drop adapters that disappeared (VPN down, USB NIC unplugged, filter toggled).
        for (var i = Interfaces.Count - 1; i >= 0; i--)
        {
            var m = Interfaces[i];
            if (seen.Contains(m.Id))
                continue;

            // Keep this adapter's preferences on file even though it is going out of view.
            RememberInterface(m);
            m.PropertyChanged -= Meter_PropertyChanged;
            _byId.Remove(m.Id);
            Interfaces.RemoveAt(i);
        }

        OnPropertyChanged(nameof(AggregateInText));
        OnPropertyChanged(nameof(AggregateOutText));
    }

    /// <summary>Only the activity switch matters here; the rate properties change constantly.</summary>
    private void Meter_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(InterfaceMeter.ShowActivity))
            return;
        if (sender is InterfaceMeter m)
            RememberInterface(m);
        SettingsChanged?.Invoke();
    }

    private void RememberInterface(InterfaceMeter m)
    {
        _saved[m.Id] = new InterfaceSetting { Id = m.Id, Name = m.Name, ShowActivity = m.ShowActivity };
        if (!_savedOrder.Contains(m.Id))
            _savedOrder.Add(m.Id);
    }

    /// <summary>Forces a fresh enumeration after a filter change.</summary>
    private void Resync()
    {
        // Hold on to the current order so toggling a filter doesn't scramble it.
        foreach (var m in Interfaces)
            RememberInterface(m);

        _savedOrder = CaptureInterfaces().Select(i => i.Id).ToList();

        foreach (var m in Interfaces)
            m.PropertyChanged -= Meter_PropertyChanged;
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
        // Every property routed through Set is a persisted preference.
        SettingsChanged?.Invoke();
        return true;
    }
}
