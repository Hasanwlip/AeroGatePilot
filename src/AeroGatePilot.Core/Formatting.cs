using System.Globalization;

namespace AeroGatePilot.Core;

public static class Formatting
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    public static string Bytes(long bytes)
    {
        double value = bytes;
        var unit = 0;
        while (Math.Abs(value) >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0
            ? $"{bytes} B"
            : value.ToString(value >= 100 ? "0" : "0.##", CultureInfo.InvariantCulture) + " " + Units[unit];
    }

    public static string Rate(double bytesPerSecond)
    {
        var bits = bytesPerSecond * 8;
        if (bits >= 1_000_000)
            return (bits / 1_000_000).ToString("0.##", CultureInfo.InvariantCulture) + " Mbps";
        if (bits >= 1_000)
            return (bits / 1_000).ToString("0.#", CultureInfo.InvariantCulture) + " Kbps";
        return bits.ToString("0", CultureInfo.InvariantCulture) + " bps";
    }

    public static string Duration(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
            span = TimeSpan.Zero;
        if (span.TotalDays >= 1)
            return $"{(int)span.TotalDays}d {span.Hours}h";
        if (span.TotalHours >= 1)
            return $"{(int)span.TotalHours}h {span.Minutes:00}m";
        return $"{span.Minutes}m {span.Seconds:00}s";
    }

    /// <summary>Compact form for configured limits: "45 min", "2 h", "1 h 30 min", "3 d".</summary>
    public static string Minutes(int minutes)
    {
        if (minutes % 1440 == 0 && minutes >= 1440)
            return $"{minutes / 1440} d";
        if (minutes < 60)
            return $"{minutes} min";
        return minutes % 60 == 0 ? $"{minutes / 60} h" : $"{minutes / 60} h {minutes % 60} min";
    }
}
