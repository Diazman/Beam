using Beam.Core.Diagnostics;
using Beam.Core.Files;
using Beam.Core.Localization;

namespace Beam.Core.Backup;

/// <summary>A file a backup covers: its path inside its source ("DCIM/Camera/IMG_1.jpg"), and how to read it.</summary>
public sealed record BackupItem(string RelativePath, long Size, DateTime ModifiedUtc, string Address, Func<Stream>? OpenRead);

/// <summary>
/// What the platform can list for a backup beyond plain folders: the phone's photos and videos (its media library),
/// or a folder it only has an address for. Returns null for a source it doesn't handle.
/// </summary>
public interface IBackupSources
{
    IReadOnlyList<BackupItem>? List(BackupSource source, CancellationToken cancellationToken);
}

/// <summary>Turns a backup plan's sources into the list of files that is offered (everything; the computer picks).</summary>
internal static class BackupManifest
{
    public static Manifest Build(BackupPlan plan, IBackupSources? platform, IExternalFiles? external, CancellationToken cancellationToken)
    {
        var found = new List<(string[] Path, BackupItem Item, string Origin)>();
        var warnings = new List<string>();
        var usedRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in plan.Sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var root = FileNaming.MakeUnique(SafePath.SanitizeSegment(string.IsNullOrWhiteSpace(source.Name) ? "Backup" : source.Name),
                usedRoots.Contains, isDirectory: true);
            usedRoots.Add(root);
            IReadOnlyList<BackupItem> items;
            try
            {
                items = platform?.List(source, cancellationToken) ?? ListFolder(source, external, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Warn($"Backup: couldn't list {source.Name} ({source.Kind})", ex);
                warnings.Add($"{source.Name}: {ex.Message}");
                continue;
            }

            var types = source.EffectiveTypes();
            var count = 0;
            foreach (var item in items)
            {
                var kind = BackupFileTypes.KindOf(item.RelativePath);
                if (source.Kind == BackupSourceKinds.Folder && !types.Contains(kind)) continue;
                var segments = item.RelativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).Select(SafePath.SanitizeSegment).ToArray();
                if (segments.Length == 0) continue;
                // Folder names kept: "Camera/DCIM/IMG_1.jpg" under the source's name. Otherwise one folder per kind.
                var path = plan.KeepFolderNames
                    ? segments.Prepend(root).ToArray()
                    : new[] { KindFolder(source.Kind switch { BackupSourceKinds.Photos => BackupFileTypes.Photos, BackupSourceKinds.Videos => BackupFileTypes.Videos, _ => kind }), segments[^1] };
                found.Add((path, item, root + "/" + string.Join('/', segments)));
                count++;
            }

            Log.Info($"Backup: {source.Name} ({source.Kind}, {string.Join(" ", types)}) has {count} files");
        }

        var entries = new List<ManifestEntry>();
        foreach (var group in found.GroupBy(f => string.Join('/', f.Path), StringComparer.OrdinalIgnoreCase))
        {
            // The same name twice: one file listed twice (a photo in the media library and in a chosen folder) is sent
            // once; different files keep their name apart by where they are on the phone, the same way every time.
            var distinct = group.OrderBy(f => f.Origin, StringComparer.Ordinal)
                .DistinctBy(f => (f.Item.Size, Seconds(f.Item.ModifiedUtc)))
                .ToList();
            for (var n = 0; n < distinct.Count; n++)
            {
                var (path, item, origin) = distinct[n];
                if (n > 0) path = path[..^1].Append(WithTag(path[^1], origin)).ToArray();
                entries.Add(new ManifestEntry
                {
                    RelativePath = string.Join('/', path),
                    SourcePath = item.Address,
                    OpenExternal = item.OpenRead,
                    Size = item.Size,
                    ModifiedUtc = item.ModifiedUtc,
                });
            }
        }

        var roots = entries.Select(e => e.RelativePath[..e.RelativePath.IndexOf('/')]).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return new Manifest { Entries = entries, RootNames = roots, Warnings = warnings };
    }

    /// <summary>Where a kind goes on the computer when folder names aren't kept.</summary>
    internal static string KindFolder(string kind) => kind switch
    {
        BackupFileTypes.Photos => L.T("Photos"),
        BackupFileTypes.Videos => L.T("Videos"),
        BackupFileTypes.Music => L.T("Music"),
        BackupFileTypes.Archives => L.T("Archives"),
        BackupFileTypes.Other => L.T("Other files"),
        _ => L.T("Documents"),
    };

    private static long Seconds(DateTime time) => time.Ticks / TimeSpan.TicksPerSecond;

    /// <summary>"IMG_1.jpg" from "Camera/IMG_1.jpg" → "IMG_1 (3fa2c1).jpg": the same tag for that file on every backup.</summary>
    private static string WithTag(string name, string origin)
    {
        var tag = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(origin)))[..6].ToLowerInvariant();
        return Path.GetFileNameWithoutExtension(name) + $" ({tag})" + Path.GetExtension(name);
    }

    /// <summary>A folder by path (desktops, tests) or by address (an Android folder the user chose).</summary>
    private static IReadOnlyList<BackupItem> ListFolder(BackupSource source, IExternalFiles? external, CancellationToken cancellationToken)
    {
        if (source.Kind != BackupSourceKinds.Folder || string.IsNullOrEmpty(source.Address)) return Array.Empty<BackupItem>();
        if (external != null && external.TryGetFolderName(source.Address, out _))
        {
            return external.ListFolder(source.Address, cancellationToken)
                .Where(i => i.File != null)
                .Select(i => new BackupItem(i.RelativePath, i.File!.Size, i.ModifiedUtc, source.Address + "#" + i.RelativePath, i.File.OpenRead))
                .ToList();
        }

        if (!Directory.Exists(source.Address))
        {
            Log.Warn($"Backup: the folder {source.Address} isn't there");
            return Array.Empty<BackupItem>();
        }

        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System };
        var result = new List<BackupItem>();
        foreach (var file in new DirectoryInfo(source.Address).EnumerateFiles("*", options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.Add(new BackupItem(Path.GetRelativePath(source.Address, file.FullName), file.Length, file.LastWriteTimeUtc, file.FullName, null));
        }

        return result;
    }
}
