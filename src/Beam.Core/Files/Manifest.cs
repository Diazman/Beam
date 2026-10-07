namespace Beam.Core.Files;

/// <summary>One file or folder to send.</summary>
public sealed class ManifestEntry
{
    public required string RelativePath { get; init; }

    /// <summary>Full path on the sending computer; never sent over the network.</summary>
    public required string SourcePath { get; init; }

    public bool IsDirectory { get; init; }

    public long Size { get; init; }

    public DateTime ModifiedUtc { get; init; }
}

/// <summary>Everything the user selected, flattened into a list with relative paths.</summary>
public sealed class Manifest
{
    public required IReadOnlyList<ManifestEntry> Entries { get; init; }

    /// <summary>Names of the items the user selected (top-level files and folders).</summary>
    public required IReadOnlyList<string> RootNames { get; init; }

    /// <summary>Things that could not be read and were left out.</summary>
    public required IReadOnlyList<string> Warnings { get; init; }

    public int FileCount => Entries.Count(e => !e.IsDirectory);

    public int DirectoryCount => Entries.Count(e => e.IsDirectory);

    public long TotalBytes => Entries.Where(e => !e.IsDirectory).Sum(e => e.Size);

    public string Title => Describe(RootNames);

    /// <summary>"report.pdf", "Photos", or "report.pdf and 2 more".</summary>
    public static string Describe(IReadOnlyList<string> rootNames) => rootNames.Count switch
    {
        0 => "Nothing",
        1 => rootNames[0],
        2 => $"{rootNames[0]} and 1 more",
        _ => $"{rootNames[0]} and {rootNames.Count - 1} more",
    };
}
