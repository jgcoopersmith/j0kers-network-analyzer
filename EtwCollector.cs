using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.Json;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace NetAnalyzer;

/// <summary>One process's traffic on one local address over a collection interval.</summary>
public sealed record ProcessUsage(int Pid, string Name, string LocalAddress, long Sent, long Received);

/// <summary>
/// Per-process byte counts from the ETW kernel network provider.
///
/// This replaces the WinRT usage store, which was the only source available without privilege and
/// accounted for 0.1% of an adapter's traffic here — it misses bulk transfers from ordinary
/// processes, not merely from virtual machines. Measured against the same adapters over the same
/// window, this source totalled 100.7% of what they moved, the excess being wire framing the
/// adapter counts and ETW does not.
///
/// The session runs in a CHILD PROCESS rather than in the window's own, for one reason: an ETW
/// session outlives the process that created it. A crash, a taskkill or a power cut leaves the
/// session running, and only a clean shutdown removes it. A child can be killed unconditionally
/// and its leftovers swept on the next launch; the same failure inside the UI process would wedge
/// the app itself. Teardown was measured and is not the hazard it first appeared to be — Stop(),
/// Dispose() and an external logman stop all return in under a quarter second — but an unclean
/// kill still leaks, and that is the case this arrangement is for.
/// </summary>
public sealed class EtwCollector : IDisposable
{
    /// <summary>Argument that puts a second copy of this executable into collector mode.</summary>
    public const string ChildSwitch = "--collect";

    /// <summary>
    /// Stable prefix so a leaked session is always recognisable on the next launch. The suffix is
    /// the collector's own process id, so two instances never contend for one session name.
    /// </summary>
    private const string SessionPrefix = "NetAnalyzerCollector";

    private readonly int _intervalMs;
    private Process? _child;
    private Thread? _reader;
    private volatile bool _stopping;

    /// <summary>Latest interval's readings, or empty before the first arrives.</summary>
    public IReadOnlyList<ProcessUsage> Latest { get; private set; } = Array.Empty<ProcessUsage>();

    /// <summary>Seconds the latest reading covers, used to turn byte totals into rates.</summary>
    public double LatestSeconds { get; private set; }

    /// <summary>Raised on the collector thread whenever a fresh interval arrives.</summary>
    public event Action? Updated;

    /// <summary>Why the collector is not running, or empty while it is.</summary>
    public string Status { get; private set; } = "not started";

    public bool Running => _child is { HasExited: false };

    public EtwCollector(int intervalMs = 2000) => _intervalMs = Math.Clamp(intervalMs, 500, 30000);

    // ---- parent side -------------------------------------------------------------------------

    /// <summary>
    /// Sweeps sessions left by a collector that was killed rather than shut down, then starts a
    /// fresh child. Safe to call when nothing was left behind.
    /// </summary>
    public void Start()
    {
        if (Running)
            return;

        SweepOrphanedSessions();

        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
        {
            Status = "cannot locate own executable";
            return;
        }

        try
        {
            _child = Process.Start(new ProcessStartInfo(exe, $"{ChildSwitch} {_intervalMs}")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardInput = true,
            });
        }
        catch (Exception e)
        {
            Status = "collector would not start: " + e.Message;
            return;
        }

        if (_child is null)
        {
            Status = "collector would not start";
            return;
        }

        Status = "";
        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "EtwCollectorReader" };
        _reader.Start();
    }

    private void ReadLoop()
    {
        try
        {
            var stdout = _child!.StandardOutput;
            while (!_stopping && _child is { HasExited: false })
            {
                var line = stdout.ReadLine();
                if (line is null)
                    break;
                if (line.Length == 0 || line[0] != '{')
                    continue;

                var snapshot = Parse(line);
                if (snapshot is null)
                    continue;

                Latest = snapshot.Rows;
                LatestSeconds = snapshot.Seconds;
                Updated?.Invoke();
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The child went away underneath the reader; Stop() or the next Start() handles it.
        }

        if (!_stopping)
            Status = "collector stopped unexpectedly";
    }

    private sealed record Snapshot(double Seconds, List<ProcessUsage> Rows);

    private static Snapshot? Parse(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            var seconds = root.TryGetProperty("secs", out var s) ? s.GetDouble() : 0;
            var rows = new List<ProcessUsage>();
            if (root.TryGetProperty("rows", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var r in arr.EnumerateArray())
                {
                    rows.Add(new ProcessUsage(
                        r.TryGetProperty("pid", out var p) ? p.GetInt32() : 0,
                        r.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                        r.TryGetProperty("addr", out var a) ? a.GetString() ?? "" : "",
                        r.TryGetProperty("sent", out var x) ? x.GetInt64() : 0,
                        r.TryGetProperty("recv", out var y) ? y.GetInt64() : 0));
                }
            }
            return new Snapshot(seconds, rows);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Stops sessions a previous collector left running. Uses logman rather than attaching to the
    /// session: attaching to a session whose owner died can block, and this runs at startup where
    /// a stall would be a hang on launch.
    /// </summary>
    private static void SweepOrphanedSessions()
    {
        string[] names;
        try
        {
            names = TraceEventSession.GetActiveSessionNames()
                .Where(n => n.StartsWith(SessionPrefix, StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }
        catch (Exception)
        {
            return;
        }

        foreach (var name in names)
            RunLogman($"stop {name} -ets");
    }

    private static void RunLogman(string arguments)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("logman", arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (p is null)
                return;
            if (!p.WaitForExit(5000))
            {
                // A wedged session can hang the stop as well; never let that hold up launch.
                try { p.Kill(true); } catch { }
            }
        }
        catch (Exception)
        {
            // logman missing or refused; the sweep is best effort by design.
        }
    }

    public void Stop()
    {
        _stopping = true;
        var child = _child;
        _child = null;

        if (child is not null)
        {
            // Closing stdin is the child's cue to shut its session down cleanly. It is given a
            // moment to do that, then killed — a collector must never delay the app's exit.
            try { child.StandardInput.Close(); } catch { }
            try
            {
                if (!child.WaitForExit(3000))
                    child.Kill(true);
            }
            catch (Exception) { }
            try { child.Dispose(); } catch { }
        }

        // Whether it left cleanly or was killed, make sure nothing of ours is still collecting.
        SweepOrphanedSessions();
        Status = "stopped";
    }

    public void Dispose() => Stop();

    // ---- child side --------------------------------------------------------------------------

    /// <summary>
    /// Runs this process as the collector: opens an ETW session, totals bytes per process per
    /// local address, and writes one JSON line per interval to standard output. Returns when the
    /// parent closes standard input, or when the parent dies.
    /// </summary>
    public static int RunAsChild(string[] args)
    {
        var intervalMs = 2000;
        for (var i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], ChildSwitch, StringComparison.OrdinalIgnoreCase))
                int.TryParse(args[i + 1], out intervalMs);
        intervalMs = Math.Clamp(intervalMs, 500, 30000);

        if (TraceEventSession.IsElevated() != true)
        {
            Console.Error.WriteLine("collector requires elevation");
            return 2;
        }

        var sessionName = $"{SessionPrefix}_{Environment.ProcessId}";
        var gate = new object();
        var tally = new Dictionary<(int pid, string addr), (long sent, long recv, string name)>();

        void Add(int pid, string name, string addr, long sent, long recv)
        {
            lock (gate)
            {
                var key = (pid, addr);
                tally.TryGetValue(key, out var cur);
                tally[key] = (cur.sent + sent, cur.recv + recv, string.IsNullOrEmpty(name) ? cur.name : name);
            }
        }

        using var session = new TraceEventSession(sessionName) { StopOnDispose = true };
        try
        {
            session.EnableKernelProvider(KernelTraceEventParser.Keywords.NetworkTCPIP);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("could not enable kernel network provider: " + e.Message);
            return 3;
        }

        session.Source.Kernel.TcpIpSend     += d => Add(d.ProcessID, d.ProcessName, d.saddr.ToString(), d.size, 0);
        session.Source.Kernel.TcpIpRecv     += d => Add(d.ProcessID, d.ProcessName, d.daddr.ToString(), 0, d.size);
        session.Source.Kernel.TcpIpSendIPV6 += d => Add(d.ProcessID, d.ProcessName, d.saddr.ToString(), d.size, 0);
        session.Source.Kernel.TcpIpRecvIPV6 += d => Add(d.ProcessID, d.ProcessName, d.daddr.ToString(), 0, d.size);
        session.Source.Kernel.UdpIpSend     += d => Add(d.ProcessID, d.ProcessName, d.saddr.ToString(), d.size, 0);
        session.Source.Kernel.UdpIpRecv     += d => Add(d.ProcessID, d.ProcessName, d.daddr.ToString(), 0, d.size);

        var last = Stopwatch.GetTimestamp();

        // Publishing on a timer rather than per event: the window only needs a rate, and writing
        // a line per packet would cost more than the measurement is worth.
        using var publish = new Timer(_ =>
        {
            Dictionary<(int pid, string addr), (long sent, long recv, string name)> batch;
            double seconds;
            lock (gate)
            {
                batch = new Dictionary<(int, string), (long, long, string)>(tally);
                tally.Clear();
                var now = Stopwatch.GetTimestamp();
                seconds = (now - last) / (double)Stopwatch.Frequency;
                last = now;
            }
            Emit(batch, seconds);
        }, null, intervalMs, intervalMs);

        // The parent closing stdin is the shutdown signal. Reading it on a background thread
        // means the session is torn down on the way out rather than left to an unclean kill.
        var stdinClosed = new ManualResetEventSlim(false);
        new Thread(() =>
        {
            try { while (Console.In.ReadLine() is not null) { } } catch (Exception) { }
            stdinClosed.Set();
            try { session.Source.StopProcessing(); } catch (Exception) { }
        })
        { IsBackground = true }.Start();

        session.Source.Process();
        return 0;
    }

    private static void Emit(Dictionary<(int pid, string addr), (long sent, long recv, string name)> batch, double seconds)
    {
        var sb = new StringBuilder(256);
        sb.Append("{\"secs\":").Append(seconds.ToString("0.###")).Append(",\"rows\":[");
        var first = true;
        foreach (var kv in batch)
        {
            if (kv.Value.sent == 0 && kv.Value.recv == 0)
                continue;
            if (!first)
                sb.Append(',');
            first = false;
            sb.Append("{\"pid\":").Append(kv.Key.pid)
              .Append(",\"name\":").Append(JsonSerializer.Serialize(kv.Value.name ?? ""))
              .Append(",\"addr\":").Append(JsonSerializer.Serialize(kv.Key.addr ?? ""))
              .Append(",\"sent\":").Append(kv.Value.sent)
              .Append(",\"recv\":").Append(kv.Value.recv).Append('}');
        }
        sb.Append("]}");

        try
        {
            Console.Out.WriteLine(sb.ToString());
            Console.Out.Flush();
        }
        catch (IOException)
        {
            // Parent closed the pipe; the stdin watcher will bring the loop down.
        }
    }
}
