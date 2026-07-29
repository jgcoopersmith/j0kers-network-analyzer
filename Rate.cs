namespace NetAnalyzer;

/// <summary>Formatting helpers for throughput values.</summary>
public static class Rate
{
    private static readonly string[] ByteUnits = { "B/s", "KB/s", "MB/s", "GB/s", "TB/s" };
    private static readonly string[] BitUnits = { "bps", "Kbps", "Mbps", "Gbps", "Tbps" };

    /// <summary>Formats a bytes-per-second value, optionally converted to bits per second.</summary>
    public static string Format(double bytesPerSecond, bool asBits)
    {
        var value = asBits ? bytesPerSecond * 8.0 : bytesPerSecond;
        var units = asBits ? BitUnits : ByteUnits;
        var divisor = asBits ? 1000.0 : 1024.0;

        var i = 0;
        while (value >= divisor && i < units.Length - 1)
        {
            value /= divisor;
            i++;
        }

        var digits = value >= 100 ? 0 : value >= 10 ? 1 : 2;
        return $"{value.ToString("N" + digits)} {units[i]}";
    }

    /// <summary>Formats a cumulative byte total (session volume).</summary>
    public static string FormatBytes(double bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        var i = 0;
        while (bytes >= 1024.0 && i < units.Length - 1)
        {
            bytes /= 1024.0;
            i++;
        }

        var digits = bytes >= 100 ? 0 : bytes >= 10 ? 1 : 2;
        return $"{bytes.ToString("N" + digits)} {units[i]}";
    }
}
