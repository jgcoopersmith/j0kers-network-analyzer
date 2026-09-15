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

    /// <summary>
    /// Colour assignments, shared by every interface rather than kept per adapter. Five hues is
    /// the most that stays reliably distinguishable on this background, so they are handed out
    /// across the whole window: two interfaces whose busiest applications differ get different
    /// colours, instead of each starting again at the first hue and looking identical.
    /// </summary>
    private readonly TalkerSlots _slots = new();

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
    /// How long a consumer is remembered for the legend after it stops leading.
    ///
    /// This has to outlast what the graph is still drawing, or a line remains on screen with
    /// nothing in the tooltip naming it. The longest history the line graph holds is 900 samples,
    /// one per poll, and the poll floor is 100 ms — but at that rate the samples span only a
    /// minute and a half. The worst case that matters is the default half-second poll, where 900
    /// samples reach back seven and a half minutes.
    /// </summary>
    private static readonly TimeSpan LegendMemory = TimeSpan.FromMinutes(8);

    /// <summary>A consumer seen on one adapter recently enough that its colour may still be drawn.</summary>
    private sealed class Remembered
    {
        public int Slot;
        public double Rx;
        public double Tx;
        public DateTime LastSeen;

        /// <summary>Whether this entry was in the most recent reading rather than only in history.</summary>
        public bool Current;
    }

    /// <summary>Consumers seen per adapter within <see cref="LegendMemory"/>, keyed by name.</summary>
    private readonly Dictionary<string, Dictionary<string, Remembered>> _recent =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Folds a fresh reading into the adapter's roster and ages out whatever has scrolled off.
    /// Entries that dropped out of the top few are kept, with their rates zeroed: their lines are
    /// still on the graph, and the point of the roster is that every drawn colour has a name.
    /// </summary>
    private void Remember(string adapterId, List<Talker> talkers, DateTime now)
    {
        if (!_recent.TryGetValue(adapterId, out var roster))
            _recent[adapterId] = roster = new Dictionary<string, Remembered>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in roster.Values)
        {
            entry.Current = false;
            entry.Rx = 0;
            entry.Tx = 0;
        }

        foreach (var t in talkers)
        {
            if (!roster.TryGetValue(t.Name, out var e))
                roster[t.Name] = e = new Remembered();
            e.Slot = t.Slot;
            e.Rx = t.Rx;
            e.Tx = t.Tx;
            e.LastSeen = now;
            e.Current = true;
        }

        // A colour belongs to whoever holds it now. An entry that was evicted from one still
        // carries the slot it used to have, and listing it that way puts two names against one
        // colour — worse than the missing legend this roster exists to fix. It stays on the list,
        // because it was recently active and the user may be looking for it, but it gives the
        // colour up and shows the neutral swatch.
        foreach (var kv in roster)
        {
            if (kv.Value.Slot >= 0 &&
                !string.Equals(_slots.HolderOf(kv.Value.Slot), kv.Key, StringComparison.OrdinalIgnoreCase))
            {
                kv.Value.Slot = -1;
            }
        }

        foreach (var name in roster.Where(kv => now - kv.Value.LastSeen > LegendMemory)
                                   .Select(kv => kv.Key).ToList())
        {
            roster.Remove(name);
        }
    }

    /// <summary>
    /// Fills the tooltip from the latest reading for one interface. Cheap and synchronous — the
    /// figures are already in hand, so hovering shows them immediately.
    /// </summary>
    /// <param name="visibleSlots">
    /// Colour slots the graph under the pointer is drawing right now, or null when there is no
    /// graph to ask. A consumer that has dropped out of the top few is listed only while its line
    /// is still on screen — which the graph knows and nothing else does.
    /// </param>
    public void Show(InterfaceMeter meter, bool useBits, IReadOnlyCollection<int>? visibleSlots = null)
    {
        // The ETW reading covers the collector's interval; the store's covers two minutes.
        var window = _fromEtw && _etwSeconds > 0 ? _etwSeconds : Window.TotalSeconds;
        Scope = $"{meter.Name} · last {window:N0}s";

        Items.Clear();

        // Everything still drawn, not merely the latest reading. A consumer that has dropped out
        // of the top few keeps its line on the graph until the sample carrying it scrolls off, and
        // a colour on screen with nothing naming it is exactly the gap this closes. Those entries
        // are listed at a rate of zero, which is what they are moving now.
        if (_recent.TryGetValue(meter.Id, out var roster) && roster.Count > 0)
        {
            var rows = roster
                .Select(kv => (Name: kv.Key, E: kv.Value))
                // Currently moving traffic, or still being drawn. Nothing else: an entry whose
                // line has scrolled off is no longer on screen and padding the list with it is
                // the opposite failure to the one being fixed.
                .Where(r => r.E.Current ||
                            (r.E.Slot >= 0 && visibleSlots is not null && visibleSlots.Contains(r.E.Slot)))
                .OrderByDescending(r => r.E.Current)
                .ThenByDescending(r => r.E.Rx + r.E.Tx)
                .ThenByDescending(r => r.E.LastSeen);

            foreach (var (name, e) in rows)
            {
                Items.Add(new AppUsage
                {
                    Name = name,
                    Slot = e.Slot,
                    BytesReceived = e.Rx,
                    BytesSent = e.Tx,
                    DownText = Rate.Format(e.Rx / window, useBits),
                    UpText = Rate.Format(e.Tx / window, useBits),
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

            // Rank each interface on its own first, then hand the shared colours out across all
            // of them at once — see AssignSlots for why that has to happen in one pass.
            var ranked = meters
                .Select(m => (meter: m, leaders: Leaders(usage, m.Id)))
                .ToList();

            AssignSlots(ranked.Select(r => r.leaders).ToList(), now);

            foreach (var (meter, leaders) in ranked)
            {
                // An empty poll is far more often the store mid-flush than the link going
                // quiet, so the previous reading stands until it ages out.
                if (leaders.Count == 0)
                {
                    Expire(meter, now);
                    continue;
                }

                var talkers = leaders
                    .Select(a => new Talker(a.Key, _slots.SlotOf(a.Key), a.Value.rx, a.Value.tx))
                    .ToList();

                _byAdapter[meter.Id] = talkers;
                _readAt[meter.Id] = now;
                Remember(meter.Id, talkers, now);
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

    /// <summary>
    /// Publishes a reading taken from the ETW kernel network provider instead of the WinRT usage
    /// store. Rows arrive keyed by the local address the traffic left from, which is what places
    /// each process on an adapter; anything on an address no adapter owns — loopback, multicast —
    /// is not interface traffic and is dropped.
    ///
    /// Unlike the store this replaces, the figures here account for essentially all of what the
    /// adapters moved, so the shares below are shares of the interface rather than of whatever
    /// fraction happened to be attributable.
    /// </summary>
    public void ApplyEtw(IReadOnlyList<ProcessUsage> rows, double seconds,
                         IReadOnlyList<InterfaceMeter> meters,
                         IReadOnlyDictionary<string, string> owners)
    {
        if (seconds <= 0)
            return;

        var now = DateTime.UtcNow;
        var usage = new Dictionary<string, Dictionary<string, (double rx, double tx)>>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows)
        {
            if (!owners.TryGetValue(row.LocalAddress, out var adapterId))
                continue;

            if (!usage.TryGetValue(adapterId, out var apps))
                usage[adapterId] = apps = new Dictionary<string, (double rx, double tx)>(
                    StringComparer.OrdinalIgnoreCase);

            var name = string.IsNullOrWhiteSpace(row.Name) ? $"pid {row.Pid}" : row.Name;
            apps.TryGetValue(name, out var cur);
            apps[name] = (cur.rx + row.Received, cur.tx + row.Sent);
        }

        // Same two-step as the store path: rank each interface alone, then hand the shared
        // colours out across all of them at once.
        var ranked = meters.Select(m => (meter: m, leaders: Leaders(usage, m.Id))).ToList();
        AssignSlots(ranked.Select(r => r.leaders).ToList(), now);

        foreach (var (meter, leaders) in ranked)
        {
            if (leaders.Count == 0)
            {
                Expire(meter, now);
                continue;
            }

            var talkers = leaders
                .Select(a => new Talker(a.Key, _slots.SlotOf(a.Key), a.Value.rx, a.Value.tx))
                .ToList();

            _byAdapter[meter.Id] = talkers;
            _readAt[meter.Id] = now;
            Remember(meter.Id, talkers, now);
            meter.Mix = BuildMix(talkers);
        }

        _etwSeconds = seconds;
        _fromEtw = true;
        _polledOnce = true;
        _unavailable = false;
    }

    /// <summary>Window the ETW figures cover, which is the collector's interval rather than two minutes.</summary>
    private double _etwSeconds;

    /// <summary>Whether the readings on show came from ETW rather than the usage store.</summary>
    private bool _fromEtw;

    /// <summary>Drops an adapter's held reading once it is older than <see cref="HoldFor"/>.</summary>
    private void Expire(InterfaceMeter meter, DateTime now)
    {
        if (_readAt.TryGetValue(meter.Id, out var at) && now - at <= HoldFor)
            return;

        _byAdapter.Remove(meter.Id);
        _readAt.Remove(meter.Id);
        _recent.Remove(meter.Id);
        meter.Mix = null;
    }

    /// <summary>One adapter's biggest consumers, heaviest first.</summary>
    private static List<KeyValuePair<string, (double rx, double tx)>> Leaders(
        Dictionary<string, Dictionary<string, (double rx, double tx)>> usage, string adapterId)
    {
        if (!usage.TryGetValue(adapterId, out var apps) || apps.Count == 0)
            return new List<KeyValuePair<string, (double rx, double tx)>>();

        return apps
            .OrderByDescending(a => a.Value.rx + a.Value.tx)
            .Take(MaxEntries)
            .ToList();
    }

    /// <summary>
    /// Shares the five colours out over every interface at once.
    ///
    /// Claims go round by round — every interface's heaviest application, then every interface's
    /// second, and so on — rather than filling up from the busiest interface downwards. Ranking
    /// purely by volume would let one busy link take all five, leaving every other stream a wash
    /// of the same neutral; going round by round means each interface's leader gets a colour of
    /// its own first, which is what makes two streams read as different at a glance.
    ///
    /// An application seen on two interfaces claims once and keeps one colour on both, so a shared
    /// hue means the same program rather than a coincidence.
    /// </summary>
    private void AssignSlots(List<List<KeyValuePair<string, (double rx, double tx)>>> perAdapter, DateTime now)
    {
        var order = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var depth = 0; depth < MaxEntries; depth++)
        {
            foreach (var leaders in perAdapter)
            {
                if (depth < leaders.Count && seen.Add(leaders[depth].Key))
                    order.Add(leaders[depth].Key);
            }
        }

        // Only the first few can be held; the rest share the neutral band.
        if (order.Count > TalkerPalette.SlotCount)
            order.RemoveRange(TalkerPalette.SlotCount, order.Count - TalkerPalette.SlotCount);

        _slots.Sync(order, now);
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
