namespace Beam.Core.Backup;

/// <summary>What one source of a backup is.</summary>
public static class BackupSourceKinds
{
    /// <summary>The phone's photos (its media library): camera, screenshots, downloads of pictures…</summary>
    public const string Photos = "photos";

    /// <summary>The phone's videos (its media library).</summary>
    public const string Videos = "videos";

    /// <summary>A folder the user chose (a path, or an Android content://…/tree/… address with lasting access).</summary>
    public const string Folder = "folder";
}

/// <summary>Which files of a folder are backed up.</summary>
public static class BackupFileTypes
{
    public const string All = "all";
    public const string Photos = "photos";
    public const string Videos = "videos";
    public const string Documents = "documents";

    /// <summary>The extensions in <see cref="BackupSource.Extensions"/> ("pdf, epub").</summary>
    public const string Custom = "custom";

    private static readonly string[] PhotoExtensions =
        { ".jpg", ".jpeg", ".png", ".heic", ".heif", ".gif", ".webp", ".bmp", ".tif", ".tiff", ".dng", ".raw", ".cr2", ".nef", ".arw" };

    private static readonly string[] VideoExtensions =
        { ".mp4", ".mov", ".m4v", ".mkv", ".3gp", ".webm", ".avi", ".wmv", ".mts" };

    private static readonly string[] DocumentExtensions =
        { ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".odt", ".ods", ".odp", ".rtf", ".txt", ".csv", ".md", ".epub", ".pages", ".numbers", ".key" };

    /// <summary>Whether a file named <paramref name="name"/> is included by <paramref name="source"/>'s file types.</summary>
    public static bool Includes(BackupSource source, string name)
    {
        var extension = Path.GetExtension(name).ToLowerInvariant();
        return source.FileTypes switch
        {
            Photos => PhotoExtensions.Contains(extension),
            Videos => VideoExtensions.Contains(extension),
            Documents => DocumentExtensions.Contains(extension),
            Custom => ParseExtensions(source.Extensions).Contains(extension),
            _ => true,
        };
    }

    /// <summary>"pdf, .EPUB docx" → ".pdf", ".epub", ".docx".</summary>
    public static IReadOnlySet<string> ParseExtensions(string? text) =>
        (text ?? "").Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(e => "." + e.TrimStart('.', '*').ToLowerInvariant())
        .Where(e => e.Length > 1)
        .ToHashSet();
}

/// <summary>One thing to back up: the photos, the videos, or a folder (with the kinds of files to take from it).</summary>
public sealed class BackupSource
{
    public string Kind { get; set; } = BackupSourceKinds.Folder;

    /// <summary>The folder (for <see cref="BackupSourceKinds.Folder"/>).</summary>
    public string? Address { get; set; }

    /// <summary>Its name in the backup ("Photos", "Documents"): the folder its files go into on the computer.</summary>
    public string Name { get; set; } = "";

    /// <summary>For folders: <see cref="BackupFileTypes"/>.</summary>
    public string FileTypes { get; set; } = BackupFileTypes.All;

    /// <summary>For <see cref="BackupFileTypes.Custom"/>: "pdf, epub".</summary>
    public string? Extensions { get; set; }
}

/// <summary>
/// On the device that backs up (a phone): what goes to which computer. The computer is pinned by its certificate
/// fingerprint at setup (from its QR code); backups go to that certificate only.
/// </summary>
public sealed class BackupPlan
{
    public string TargetId { get; set; } = "";

    public string TargetName { get; set; } = "";

    /// <summary>The computer's certificate (SHA-256, hex): a device that can't prove it gets nothing.</summary>
    public string TargetFingerprint { get; set; } = "";

    public List<BackupSource> Sources { get; set; } = new();

    /// <summary>The computer's last known addresses, asked directly too (networks that drop Beam's announcements).</summary>
    public List<string> Addresses { get; set; } = new();

    /// <summary>Back up by itself whenever the computer is on the same Wi-Fi (otherwise only with "Back up now").</summary>
    public bool Automatic { get; set; } = true;

    public DateTimeOffset SetUpAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>The last time the computer confirmed that everything chosen was backed up.</summary>
    public DateTimeOffset? LastCompletedAt { get; set; }

    /// <summary>Files sent in the last backup that sent any.</summary>
    public int LastFileCount { get; set; }

    /// <summary>Why the last backup didn't finish (for the user); null when it did.</summary>
    public string? LastError { get; set; }
}

/// <summary>
/// On the computer: a device allowed to back up here, by its certificate fingerprint (learned when it scanned this
/// computer's one-time setup code). Its backups are accepted without asking, into <see cref="Folder"/> only.
/// </summary>
public sealed class BackupPartner
{
    public string DeviceId { get; set; } = "";

    public string Name { get; set; } = "";

    public string Fingerprint { get; set; } = "";

    /// <summary>Where its backups go (its own folder; nothing it sends can land outside it).</summary>
    public string Folder { get; set; } = "";

    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? LastBackupAt { get; set; }

    public int LastFileCount { get; set; }
}
