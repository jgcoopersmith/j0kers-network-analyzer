using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using Windows.Networking.Connectivity;

namespace NetAnalyzer;

/// <summary>One application's share of an interface's traffic over the sampled window.</summary>
public sealed class AppUsage
{
    public required string Name { get; init; }
    public required double BytesReceived { get; init; }
    public required double BytesSent { get; init; }

    /// <summary>Colour slot this application holds on its interface.</summary>
    public required int Slot { get; init; }

    /// <summary>Legend dot, the same colour as this application's band in the ribbon.</summary>
    public Brush Swatch => TalkerPalette.Swatch(Slot);

    public double Total => BytesReceived + BytesSent;
    public string DownText { get; init; } = "";
    public string UpText { get; init; } = "";
}

/// <summary>
/// Per-application bandwidth for every interface, from the WinRT network usage store.
///
/// This is the only per-process view Windows offers without elevation — the ETW kernel provider
/// that Task Manager uses refuses to start unless the app runs as administrator. The trade-off is
/// that figures come from an aggregated store rather than live counters, so they lag the meters
/// slightly and are attributed per application rather than per process.
///
/// Sampled on a slow timer rather than on hover: the same figures now colour the stream ribbons,
/// so they have to be there before anybody points at anything. Exposed as a singleton because the
/// tooltip lives in a popup, outside the window's DataContext.
/// </summary>
public sealed class TopTalkers : INotifyPropertyChanged
{
    /// <summary>
    /// How far back to sample. The store only flushes attributed usage every so often and in
    /// coarse buckets, so a short window sometimes falls entirely inside a bucket that has not
    /// been written yet and comes back empty. Two minutes always straddles a flushed bucket.
    /// </summary>
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(2);

    /// <summary>
    /// How long a reading is kept when the store returns nothing or throws. Both happen in
    /// passing — the store is mid-flush, or a profile is being torn down — and blanking the
    /// tooltip and ribbons for the duration reads as traffic vanishing. Past this, the reading is
    /// treated as stale and dropped.
    /// </summary>
    private static readonly TimeSpan HoldFor = TimeSpan.FromMinutes(5);

    private const int MaxEntries = TalkerPalette.SlotCount;

    public static TopTalkers Instance { get; } = new();

    private string _status = "Sampling…";
    private string _scope = "";
    private bool _busy;

    /// <summary>Latest non-empty reading per adapter, ordered highest first, keyed by adapter id.</summary>
    private readonly Dictionary<string, List<Talker>> _byAdapter =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>When each adapter's reading was last refreshed with real data.</summary>
    private readonly Dictionary<string, DateTime> _readAt =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Colour assignments per adapter, kept across refreshes so they stay put.</summary>
    private readonly Dictionary<string, TalkerSlots> _slots =
        new(StringComparer.OrdinalIgnoreCase);

    private bool _unavailable;

    public ObservableCollection<AppUsage> Items { get; } = new();

    /// <summary>Progress or failure text; empty once results are showing.</summary>
    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    /// <summary>Which interface and window the figures cover.</summary>
    public string Scope
    {
        get => _scope;
        private set => Set(ref _scope, value);
    }

    /// <summary>One application's slice of an interface, before it is dressed up for display.</summary>
    private readonly record struct Talker(string Name, int Slot, double Rx, double Tx);

    /// <summary>
    /// Fills the tooltip from the latest reading for one interface. Cheap and synchronous — the
    /// figures are already in hand, so hovering shows them immediately.
    /// </summary>
    public void Show(InterfaceMeter meter, bool useBits)
    {
        Scope = $"{meter.Name} · last {Window.TotalSeconds:N0}s";

        Items.Clear();
        if (_byAdapter.TryGetValue(meter.Id, out var talkers))
        {
            var seconds = Window.TotalSeconds;
            foreach (var t in talkers)
            {
                Items.Add(new AppUsage
                {
                    Name = t.Name,
                    Slot = t.Slot,
                    BytesReceived = t.Rx,
                    BytesSent = t.Tx,
                    DownText = Rate.Format(t.Rx / seconds, useBits),
                    UpText = Rate.Format(t.Tx / seconds, useBits),
                });
            }
        }

        Status = Items.Count > 0 ? ""
            : !_polledOnce ? "Sampling…"
            : _unavailable ? "Per-app usage unavailable"
            : "No attributed traffic recorded";
    }

    private bool _polledOnce;

    /// <summary>
    /// Re-reads the usage store and republishes each meter's colour mix. Safe to call repeatedly;
    /// overlapping calls are dropped rather than queued.
    /// </summary>
    public async Task RefreshAsync(IReadOnlyList<InterfaceMeter> meters)
    {
        if (_busy)
            return;
        _busy = true;

        var now = DateTime.UtcNow;
        try
        {
            var usage = await Task.Run(Collect);
            _unavailable = false;

            foreach (var meter in meters)
            {
                usage.TryGetValue(meter.Id, out var apps);
                var talkers = Rank(meter.Id, apps);

                // An empty poll is far more often the store mid-flush than the link going
                // quiet, so the previous reading stands until it ages out.
                if (talkers.Count == 0)
                {
                    Expire(meter, now);
                    continue;
                }

                _byAdapter[meter.Id] = talkers;
                _readAt[meter.Id] = now;
                meter.Mix = BuildMix(talkers);
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // Same policy for a throw: keep showing what we had, and only admit the store is
            // unavailable once nothing recent is left to show.
            _unavailable = true;
            foreach (var meter in meters)
                Expire(meter, now);
        }
        finally
        {
            _polledOnce = true;
            _busy = false;
        }
    }

    /// <summary>Drops an adapter's held reading once it is older than <see cref="HoldFor"/>.</summary>
    private void Expire(InterfaceMeter meter, DateTime now)
    {
        if (_readAt.TryGetValue(meter.Id, out var at) && now - at <= HoldFor)
            return;

        _byAdapter.Remove(meter.Id);
        _readAt.Remove(meter.Id);
        meter.Mix = null;
    }

    /// <summary>Takes the leaders for one adapter and settles them into their colour slots.</summary>
    private List<Talker> Rank(string adapterId, Dictionary<string, (double rx, double tx)>? apps)
    {
        if (apps is null || apps.Count == 0)
            return new List<Talker>();

        var leaders = apps
            .OrderByDescending(a => a.Value.rx + a.Value.tx)
            .Take(MaxEntries)
            .ToList();

        if (!_slots.TryGetValue(adapterId, out var slots))
            _slots[adapterId] = slots = new TalkerSlots();
        slots.Sync(leaders.Select(a => a.Key).ToList());

        return leaders
            .Select(a => new Talker(a.Key, slots.SlotOf(a.Key), a.Value.rx, a.Value.tx))
            .ToList();
    }

    /// <summary>
    /// Folds the leaders into per-band totals. Anything the store attributed but that did not make
    /// the cut lands in the neutral band, so the bands still add up to the whole ribbon.
    /// </summary>
    private static TalkerMix? BuildMix(List<Talker> talkers)
    {
        if (talkers.Count == 0)
            return null;

        var rx = new double[TalkerPalette.BandCount];
        var tx = new double[TalkerPalette.BandCount];

        foreach (var t in talkers)
        {
            var band = t.Slot >= 0 ? t.Slot : TalkerPalette.OtherSlot;
            rx[band] += t.Rx;
            tx[band] += t.Tx;
        }

        return TalkerMix.Build(rx, tx);
    }

    /// <summary>Sums attributed usage per adapter across every connection profile, in one sweep.</summary>
    private static Dictionary<string, Dictionary<string, (double rx, double tx)>> Collect()
    {
        var byAdapter = new Dictionary<string, Dictionary<string, (double rx, double tx)>>(
            StringComparer.OrdinalIgnoreCase);

        var end = DateTimeOffset.Now;
        var start = end - Window;
        var states = new NetworkUsageStates { Roaming = TriStates.DoNotCare, Shared = TriStates.DoNotCare };

        foreach (var profile in NetworkInformation.GetConnectionProfiles())
        {
            var adapterId = AdapterIdOf(profile);
            if (adapterId is null)
                continue;

            var attributed = profile.GetAttributedNetworkUsageAsync(start, end, states)
                .AsTask().GetAwaiter().GetResult();

            if (!byAdapter.TryGetValue(adapterId, out var totals))
                byAdapter[adapterId] = totals =
                    new Dictionary<string, (double rx, double tx)>(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in attributed)
            {
                var name = FriendlyName(entry);
                var rx = (double)entry.BytesReceived;
                var tx = (double)entry.BytesSent;
                if (rx + tx <= 0)
                    continue;

                totals.TryGetValue(name, out var current);
                totals[name] = (current.rx + rx, current.tx + tx);
            }
        }

        return byAdapter;
    }

    /// <summary>The adapter this profile runs over, in the same form as NetworkInterface.Id.</summary>
    private static string? AdapterIdOf(ConnectionProfile profile)
    {
        try
        {
            // NetworkInterface.Id is the same GUID in registry braces form.
            return profile.NetworkAdapter?.NetworkAdapterId.ToString("B");
        }
        catch (Exception)
        {
            // A profile whose adapter has gone away throws rather than returning null.
            return null;
        }
    }

    /// <summary>
    /// Unpackaged desktop apps come back with an empty AttributionName and a device-path
    /// AttributionId, so the executable name is the only usable label for them.
    /// </summary>
    private static string FriendlyName(AttributedNetworkUsage usage)
    {
        var name = usage.AttributionName;
        if (!string.IsNullOrWhiteSpace(name))
            return name;

        var id = usage.AttributionId;
        if (string.IsNullOrWhiteSpace(id))
            return "System";

        if (id.Contains('\\'))
        {
            var file = Path.GetFileNameWithoutExtension(id);
            if (!string.IsNullOrWhiteSpace(file))
                return file;
        }

        // Packaged apps report a package family name: "Claude_pzs8sxrjxfjjc".
        var underscore = id.LastIndexOf('_');
        return underscore > 0 ? id[..underscore] : id;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
