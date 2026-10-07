using System.Text;

namespace Beam.Core.Files;

/// <summary>
/// Turns relative paths received from another device into safe local paths.
/// A malicious or buggy sender must never be able to write outside the chosen folder,
/// and names that are legal elsewhere but not on Windows are adjusted instead of failing.
/// </summary>
public static class SafePath
{
    public const int MaxSegmentLength = 200;
    public const int MaxDepth = 64;

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private const string InvalidChars = "<>:\"/\\|?*";

    /// <summary>
    /// Splits and sanitizes a relative path. Returns null when the path is unsafe
    /// (absolute, contains "." or ".." segments, empty, or too deep).
    /// </summary>
    public static string[]? SplitRelative(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        // Rooted paths are never valid. Drive letters ("C:x") are neutralised below because ':' is
        // replaced in every segment, and Combine() verifies the final path stays inside the root.
        if (path.StartsWith('/') || path.StartsWith('\\')) return null;

        var raw = path.Split('/', '\\');
        if (raw.Length > MaxDepth) return null;
        var result = new string[raw.Length];
        for (var i = 0; i < raw.Length; i++)
        {
            var segment = raw[i];
            if (segment.Length == 0 || segment == "." || segment == "..") return null;
            result[i] = SanitizeSegment(segment);
        }

        return result;
    }

    /// <summary>Makes a single file or folder name valid on Windows.</summary>
    public static string SanitizeSegment(string name)
    {
        var builder = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            builder.Append(c < 32 || InvalidChars.Contains(c) ? '_' : c);
        }

        var cleaned = builder.ToString().TrimEnd(' ', '.');
        if (cleaned.Length == 0 || cleaned.All(c => c == '.')) cleaned = "_";

        var stem = cleaned.Split('.')[0];
        if (ReservedNames.Contains(stem.TrimEnd(' '))) cleaned = "_" + cleaned;

        if (cleaned.Length > MaxSegmentLength)
        {
            var extension = Path.GetExtension(cleaned);
            if (extension.Length > 20) extension = "";
            cleaned = cleaned[..(MaxSegmentLength - extension.Length)].TrimEnd(' ', '.') + extension;
        }

        return cleaned;
    }

    /// <summary>Combines a root with sanitized segments and verifies the result stays inside the root.</summary>
    public static string Combine(string root, IEnumerable<string> segments)
    {
        var fullRoot = Path.GetFullPath(root);
        var combined = Path.GetFullPath(Path.Combine(new[] { fullRoot }.Concat(segments).ToArray()));
        if (!IsInside(fullRoot, combined))
            throw new InvalidOperationException("Path escapes the destination folder.");
        return combined;
    }

    public static bool IsInside(string root, string candidate)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(candidate);
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return full.StartsWith(normalizedRoot, comparison);
    }
}
