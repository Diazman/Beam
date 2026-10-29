using Beam.Core.Diagnostics;
using Beam.Core.Files;

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
        var entries = new List<ManifestEntry>();
        var roots = new List<string>();
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

            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var count = 0;
            foreach (var item in items)
            {
                if (source.Kind == BackupSourceKinds.Folder && !BackupFileTypes.Includes(source, item.RelativePath)) continue;
                var segments = item.RelativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).Select(SafePath.SanitizeSegment).ToList();
                if (segments.Count == 0) continue;
                var relative = root + "/" + string.Join('/', segments);
                if (!paths.Add(relative)) continue; // the same name twice (two storage volumes): the first one
                entries.Add(new ManifestEntry
                {
                    RelativePath = relative,
                    SourcePath = item.Address,
                    OpenExternal = item.OpenRead,
                    Size = item.Size,
                    ModifiedUtc = item.ModifiedUtc,
                });
                count++;
            }

            if (count > 0) roots.Add(root);
            Log.Info($"Backup: {source.Name} ({source.Kind}, {source.FileTypes}) has {count} files");
        }

        return new Manifest { Entries = entries, RootNames = roots, Warnings = warnings };
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
