using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using Windows.Networking.Connectivity;

namespace NetAnalyzer;

/// <summary>One application's share of an interface's traffic over the sampled window.</summary>
public sealed class AppUsage
{
    public required string Name { get; init; }
    public required double BytesReceived { get; init; }
    public required double BytesSent { get; init; }

    public double Total => BytesReceived + BytesSent;
    public string DownText { get; init; } = "";
    public string UpText { get; init; } = "";
}

/// <summary>
/// Per-application bandwidth for a single interface, from the WinRT network usage store.
///
/// This is the only per-process view Windows offers without elevation — the ETW kernel provider
/// that Task Manager uses refuses to start unless the app runs as administrator. The trade-off is
/// that figures come from an aggregated store rather than live counters, so they lag the meters
/// slightly and are attributed per application rather than per process.
///
/// Exposed as a singleton because the tooltip lives in a popup, outside the window's DataContext.
/// </summary>
public sealed class TopTalkers : INotifyPropertyChanged
{
    /// <summary>How far back to sample. The store buckets coarsely, so a very short window is empty.</summary>
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private const int MaxEntries = 5;

    public static TopTalkers Instance { get; } = new();

    private string _status = "Hover to load…";
    private string _scope = "";
    private bool _busy;

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

    /// <summary>Loads the top consumers for one adapter. Safe to call repeatedly; overlaps are dropped.</summary>
    public async Task RefreshAsync(string adapterId, string adapterName, bool useBits)
    {
        if (_busy)
            return;
        _busy = true;

        try
        {
            Scope = $"{adapterName} · last {Window.TotalSeconds:N0}s";

            var usage = await Task.Run(() => Collect(adapterId));

            Items.Clear();
            foreach (var app in usage
                .OrderByDescending(u => u.Value.rx + u.Value.tx)
                .Take(MaxEntries))
            {
                var seconds = Window.TotalSeconds;
                Items.Add(new AppUsage
                {
                    Name = app.Key,
                    BytesReceived = app.Value.rx,
                    BytesSent = app.Value.tx,
                    DownText = Rate.Format(app.Value.rx / seconds, useBits),
                    UpText = Rate.Format(app.Value.tx / seconds, useBits),
                });
            }

            Status = Items.Count == 0 ? "No attributed traffic recorded" : "";
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Items.Clear();
            Status = "Per-app usage unavailable";
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>Sums attributed usage across the connection profiles bound to this adapter.</summary>
    private static Dictionary<string, (double rx, double tx)> Collect(string adapterId)
    {
        var totals = new Dictionary<string, (double rx, double tx)>(StringComparer.OrdinalIgnoreCase);

        var end = DateTimeOffset.Now;
        var start = end - Window;
        var states = new NetworkUsageStates { Roaming = TriStates.DoNotCare, Shared = TriStates.DoNotCare };

        foreach (var profile in NetworkInformation.GetConnectionProfiles())
        {
            if (!MatchesAdapter(profile, adapterId))
                continue;

            var attributed = profile.GetAttributedNetworkUsageAsync(start, end, states)
                .AsTask().GetAwaiter().GetResult();

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

        return totals;
    }

    private static bool MatchesAdapter(ConnectionProfile profile, string adapterId)
    {
        try
        {
            var id = profile.NetworkAdapter?.NetworkAdapterId;
            if (id is null)
                return false;

            // NetworkInterface.Id is the same GUID in registry braces form.
            return string.Equals(
                id.Value.ToString("B"), adapterId.Trim(), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            // A profile whose adapter has gone away throws rather than returning null.
            return false;
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
