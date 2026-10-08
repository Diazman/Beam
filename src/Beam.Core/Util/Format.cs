using System.Globalization;
using Beam.Core.Localization;

namespace Beam.Core.Util;

/// <summary>Human-friendly formatting of sizes, speeds and durations.</summary>
public static class Format
{
    // Computed at use (not cached) so the units follow the language chosen at startup.
    private static string Unit(int index) => index switch
    {
        0 => L.T("B"),
        1 => L.T("KB"),
        2 => L.T("MB"),
        3 => L.T("GB"),
        4 => L.T("TB"),
        _ => L.T("PB"),
    };

    private const int UnitCount = 6;

    /// <summary>"0 B", "512 B", "1.4 KB", "482 MB", "1.4 GB" (binary units, like Windows Explorer).</summary>
    public static string Bytes(long bytes)
    {
        if (bytes < 0) bytes = 0;
        if (bytes < 1024) return bytes.ToString(CultureInfo.CurrentCulture) + " " + Unit(0);
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < UnitCount - 1)
        {
            value /= 1024;
            unit++;
        }

        var format = value >= 100 ? "0" : value >= 10 ? "0.#" : "0.##";
        return value.ToString(format, CultureInfo.CurrentCulture) + " " + Unit(unit);
    }

    public static string Speed(double bytesPerSecond) => L.T("{0}/s", Bytes((long)Math.Max(0, bytesPerSecond)));

    /// <summary>"About 11 seconds remaining", "About 3 minutes remaining", "About 1 hour 5 minutes remaining".</summary>
    public static string Remaining(TimeSpan remaining)
    {
        if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
        var seconds = (int)Math.Ceiling(remaining.TotalSeconds);
        if (seconds <= 5) return L.T("A few seconds remaining");
        if (seconds < 60) return L.Plural(RoundSeconds(seconds), "About {0} second remaining", "About {0} seconds remaining");
        var minutes = (int)Math.Round(remaining.TotalMinutes);
        if (minutes < 60) return L.Plural(minutes, "About {0} minute remaining", "About {0} minutes remaining");
        var hours = (int)remaining.TotalHours;
        var restMinutes = remaining.Minutes;
        if (restMinutes == 0) return L.Plural(hours, "About {0} hour remaining", "About {0} hours remaining");
        return L.Plural(hours, "About {0} hour {1} min remaining", "About {0} hours {1} min remaining", restMinutes);
    }

    /// <summary>"3 files", "1 folder, 12 files".</summary>
    public static string Contents(int files, int folders)
    {
        if (folders == 0) return L.Plural(files, "{0} file", "{0} files");
        if (files == 0) return L.Plural(folders, "{0} folder", "{0} folders");
        return L.Plural(folders, "{0} folder", "{0} folders") + ", " + L.Plural(files, "{0} file", "{0} files");
    }

    private static int RoundSeconds(int seconds) => seconds < 20 ? seconds : (int)(Math.Round(seconds / 5.0) * 5);
}
