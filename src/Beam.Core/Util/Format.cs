using System.Globalization;

namespace Beam.Core.Util;

/// <summary>Human-friendly formatting of sizes, speeds and durations.</summary>
public static class Format
{
    private static readonly string[] Units = { "B", "KB", "MB", "GB", "TB", "PB" };

    /// <summary>"0 B", "512 B", "1.4 KB", "482 MB", "1.4 GB" (binary units, like Windows Explorer).</summary>
    public static string Bytes(long bytes)
    {
        if (bytes < 0) bytes = 0;
        if (bytes < 1024) return $"{bytes} B";
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        var format = value >= 100 ? "0" : value >= 10 ? "0.#" : "0.##";
        return value.ToString(format, CultureInfo.CurrentCulture) + " " + Units[unit];
    }

    public static string Speed(double bytesPerSecond) => Bytes((long)Math.Max(0, bytesPerSecond)) + "/s";

    /// <summary>"About 11 seconds remaining", "About 3 minutes remaining", "About 1 hour 5 minutes remaining".</summary>
    public static string Remaining(TimeSpan remaining)
    {
        if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
        var seconds = (int)Math.Ceiling(remaining.TotalSeconds);
        if (seconds <= 5) return "A few seconds remaining";
        if (seconds < 60) return $"About {RoundSeconds(seconds)} seconds remaining";
        var minutes = (int)Math.Round(remaining.TotalMinutes);
        if (minutes < 60) return minutes == 1 ? "About 1 minute remaining" : $"About {minutes} minutes remaining";
        var hours = (int)remaining.TotalHours;
        var restMinutes = remaining.Minutes;
        var hourText = hours == 1 ? "1 hour" : $"{hours} hours";
        return restMinutes == 0 ? $"About {hourText} remaining" : $"About {hourText} {restMinutes} min remaining";
    }

    public static string Count(int count, string singular, string? plural = null) =>
        count == 1 ? $"1 {singular}" : $"{count.ToString("N0", CultureInfo.CurrentCulture)} {plural ?? singular + "s"}";

    /// <summary>"3 files", "1 folder, 12 files".</summary>
    public static string Contents(int files, int folders)
    {
        if (folders == 0) return Count(files, "file");
        if (files == 0) return Count(folders, "folder");
        return $"{Count(folders, "folder")}, {Count(files, "file")}";
    }

    private static int RoundSeconds(int seconds) => seconds < 20 ? seconds : (int)(Math.Round(seconds / 5.0) * 5);
}
