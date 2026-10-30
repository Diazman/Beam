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

/// <summary>
/// The kinds of files a folder backup can take, one checkbox each: photos, videos, the documents (PDF, Word, Excel,
/// PowerPoint, text, e-books), music, archives, and <see cref="Other"/> for every file none of them covers.
/// </summary>
public static class BackupFileTypes
{
    public const string Photos = "photos";
    public const string Videos = "videos";
    public const string Pdf = "pdf";
    public const string Word = "word";
    public const string Excel = "excel";
    public const string PowerPoint = "powerpoint";
    public const string Text = "text";
    public const string Ebooks = "ebooks";
    public const string Music = "music";
    public const string Archives = "archives";

    /// <summary>Everything not in the other kinds.</summary>
    public const string Other = "other";

    // Before the checkboxes: one choice per folder (kept to read plans saved then).
    private const string LegacyAll = "all";
    private const string LegacyDocuments = "documents";
    private const string LegacyCustom = "custom";

    /// <summary>A kind: its id, its extensions, and whether it is one of the documents.</summary>
    public sealed record Kind(string Id, string[] Extensions, bool IsDocument);

    public static IReadOnlyList<Kind> Kinds { get; } = new Kind[]
    {
        new(Photos, new[] { ".jpg", ".jpeg", ".png", ".heic", ".heif", ".gif", ".webp", ".bmp", ".tif", ".tiff", ".dng", ".raw", ".cr2", ".nef", ".arw" }, false),
        new(Videos, new[] { ".mp4", ".mov", ".m4v", ".mkv", ".3gp", ".webm", ".avi", ".wmv", ".mts" }, false),
        new(Pdf, new[] { ".pdf" }, true),
        new(Word, new[] { ".doc", ".docx", ".odt", ".rtf", ".pages" }, true),
        new(Excel, new[] { ".xls", ".xlsx", ".ods", ".csv", ".numbers" }, true),
        new(PowerPoint, new[] { ".ppt", ".pptx", ".odp", ".key" }, true),
        new(Text, new[] { ".txt", ".md" }, true),
        new(Ebooks, new[] { ".epub", ".mobi", ".fb2", ".djvu" }, true),
        new(Music, new[] { ".mp3", ".m4a", ".aac", ".wav", ".flac", ".ogg", ".opus", ".amr", ".wma" }, false),
        new(Archives, new[] { ".zip", ".rar", ".7z", ".tar", ".gz" }, false),
        new(Other, Array.Empty<string>(), false),
    };

    /// <summary>Every kind (a new folder backs up everything).</summary>
    public static IReadOnlyList<string> All { get; } = Kinds.Select(k => k.Id).ToArray();

    /// <summary>The documents' kinds (the "Documents" checkbox ticks them all).</summary>
    public static IReadOnlyList<string> Documents { get; } = Kinds.Where(k => k.IsDocument).Select(k => k.Id).ToArray();

    private static readonly Dictionary<string, string> KindOfExtension =
        Kinds.SelectMany(k => k.Extensions.Select(e => (e, k.Id))).ToDictionary(p => p.e, p => p.Id);

    /// <summary>The kind of a file, by its extension; <see cref="Other"/> when no kind covers it.</summary>
    public static string KindOf(string name) =>
        KindOfExtension.TryGetValue(Path.GetExtension(name).ToLowerInvariant(), out var kind) ? kind : Other;

    /// <summary>Whether a file named <paramref name="name"/> is included by <paramref name="source"/>'s ticked kinds.</summary>
    public static bool Includes(BackupSource source, string name) => source.EffectiveTypes().Contains(KindOf(name));

    /// <summary>The kinds an older plan's single choice meant ("documents" → the document kinds; "pdf, epub" → those).</summary>
    internal static IReadOnlyList<string> FromLegacy(string? fileTypes, string? extensions) => fileTypes switch
    {
        null or LegacyAll => All,
        Photos => new[] { Photos },
        Videos => new[] { Videos },
        LegacyDocuments => Documents,
        LegacyCustom => (extensions ?? "").Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(e => KindOf("x." + e.TrimStart('.', '*'))).Distinct().ToArray(),
        _ => All,
    };
}

/// <summary>One thing to back up: the photos, the videos, or a folder (with the kinds of files to take from it).</summary>
public sealed class BackupSource
{
    public string Kind { get; set; } = BackupSourceKinds.Folder;

    /// <summary>The folder (for <see cref="BackupSourceKinds.Folder"/>).</summary>
    public string? Address { get; set; }

    /// <summary>Its name ("Camera", "Documents"): the folder its files go into when folder names are kept.</summary>
    public string Name { get; set; } = "";

    /// <summary>For folders: the ticked <see cref="BackupFileTypes"/> kinds (null in plans saved before the checkboxes).</summary>
    public List<string>? Types { get; set; }

    /// <summary>Before the checkboxes: one choice ("all", "photos", "documents", "custom"); read once, then <see cref="Types"/>.</summary>
    public string? FileTypes { get; set; }

    /// <summary>Before the checkboxes: the extensions of "custom".</summary>
    public string? Extensions { get; set; }

    public BackupSource Copy() => new()
    {
        Kind = Kind, Address = Address, Name = Name, Types = EffectiveTypes().ToList(),
    };

    /// <summary>The ticked kinds (for photos and videos: that kind).</summary>
    public IReadOnlyList<string> EffectiveTypes() => Kind switch
    {
        BackupSourceKinds.Photos => new[] { BackupFileTypes.Photos },
        BackupSourceKinds.Videos => new[] { BackupFileTypes.Videos },
        _ => Types ?? BackupFileTypes.FromLegacy(FileTypes, Extensions),
    };
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

    /// <summary>
    /// On the computer, keep the folders the files are in on the phone ("Camera/IMG_1.jpg"); off (the default), files
    /// are sorted into one folder per kind: Photos, Videos, Documents, Music, Archives, Other.
    /// </summary>
    public bool KeepFolderNames { get; set; }

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
