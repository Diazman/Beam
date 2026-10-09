using Beam.Core.Diagnostics;

namespace Beam.Core.Files;

public static class ManifestBuilder
{
    /// <summary>
    /// Expands the selected files and folders into a flat list. Folder structure is preserved
    /// via relative paths ("Photos/2026/January/a.jpg"); empty folders are included.
    /// Symbolic links / junctions to folders are not followed (they can create loops).
    /// Unreadable items are skipped and reported in <see cref="Manifest.Warnings"/>.
    /// </summary>
    public static Manifest Build(IEnumerable<string> paths, CancellationToken cancellationToken = default, Action<int>? progress = null,
        IExternalFiles? external = null)
    {
        var entries = new List<ManifestEntry>();
        var rootNames = new List<string>();
        var warnings = new List<string>();
        var usedRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenSources = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        foreach (var input in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (external != null && external.TryGet(input, out var file))
            {
                if (!seenSources.Add(input)) continue;
                var fileName = FileNaming.MakeUnique(SafePath.SanitizeSegment(file.Name), usedRoots.Contains);
                usedRoots.Add(fileName);
                rootNames.Add(fileName);
                entries.Add(new ManifestEntry
                {
                    RelativePath = fileName,
                    SourcePath = input,
                    OpenExternal = file.OpenRead,
                    Size = file.Size,
                    ModifiedUtc = file.ModifiedUtc,
                });
                progress?.Invoke(entries.Count);
                continue;
            }

            string full;
            try
            {
                full = Path.GetFullPath(input);
            }
            catch (Exception ex)
            {
                warnings.Add($"{input}: {ex.Message}");
                continue;
            }

            full = Path.TrimEndingDirectorySeparator(full);
            if (!seenSources.Add(full)) continue;

            if (Directory.Exists(full))
            {
                var dirInfo = new DirectoryInfo(full);
                var name = dirInfo.Name;
                if (string.IsNullOrEmpty(name) || name.EndsWith(':') || full == Path.GetPathRoot(full))
                {
                    // A whole drive (e.g. "D:\") was dropped: name the folder after the drive.
                    name = "Drive " + full.TrimEnd(Path.DirectorySeparatorChar, ':');
                }

                name = FileNaming.MakeUnique(SafePath.SanitizeSegment(name), usedRoots.Contains, isDirectory: true);
                usedRoots.Add(name);
                rootNames.Add(name);
                entries.Add(new ManifestEntry
                {
                    RelativePath = name,
                    SourcePath = full,
                    IsDirectory = true,
                    ModifiedUtc = SafeTime(() => dirInfo.LastWriteTimeUtc),
                });
                AddDirectoryContents(dirInfo, name, entries, warnings, cancellationToken, progress);
            }
            else if (File.Exists(full))
            {
                var info = new FileInfo(full);
                var name = FileNaming.MakeUnique(SafePath.SanitizeSegment(info.Name), usedRoots.Contains);
                usedRoots.Add(name);
                rootNames.Add(name);
                entries.Add(new ManifestEntry
                {
                    RelativePath = name,
                    SourcePath = full,
                    Size = info.Length,
                    ModifiedUtc = SafeTime(() => info.LastWriteTimeUtc),
                });
                progress?.Invoke(entries.Count);
            }
            else
            {
                warnings.Add($"{full}: not found");
            }
        }

        return new Manifest { Entries = entries, RootNames = rootNames, Warnings = warnings };
    }

    private static void AddDirectoryContents(
        DirectoryInfo root,
        string rootRelative,
        List<ManifestEntry> entries,
        List<string> warnings,
        CancellationToken cancellationToken,
        Action<int>? progress)
    {
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = false,
            RecurseSubdirectories = false,
            AttributesToSkip = 0,
            ReturnSpecialDirectories = false,
        };

        var pending = new Stack<(DirectoryInfo Dir, string Relative)>();
        pending.Push((root, rootRelative));
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (dir, relative) = pending.Pop();
            List<FileSystemInfo> children;
            try
            {
                children = dir.EnumerateFileSystemInfos("*", options).ToList();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
            {
                warnings.Add($"{dir.FullName}: {ex.Message}");
                Log.Warn($"Skipping unreadable folder {dir.FullName}: {ex.Message}");
                continue;
            }

            // Sort for a stable, natural order (files are sent in this order).
            children.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
            var subdirectories = new List<(DirectoryInfo, string)>();
            foreach (var child in children)
            {
                var childRelative = relative + "/" + child.Name;
                if (child is DirectoryInfo childDir)
                {
                    if (childDir.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        warnings.Add($"{childDir.FullName}: linked folders are not followed");
                        continue;
                    }

                    entries.Add(new ManifestEntry
                    {
                        RelativePath = childRelative,
                        SourcePath = childDir.FullName,
                        IsDirectory = true,
                        ModifiedUtc = SafeTime(() => childDir.LastWriteTimeUtc),
                    });
                    subdirectories.Add((childDir, childRelative));
                }
                else if (child is FileInfo file)
                {
                    long length;
                    try
                    {
                        length = file.Length;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        warnings.Add($"{file.FullName}: {ex.Message}");
                        continue;
                    }

                    entries.Add(new ManifestEntry
                    {
                        RelativePath = childRelative,
                        SourcePath = file.FullName,
                        Size = length,
                        ModifiedUtc = SafeTime(() => file.LastWriteTimeUtc),
                    });
                    if (entries.Count % 500 == 0) progress?.Invoke(entries.Count);
                }
            }

            // Push in reverse so folders are processed in alphabetical order.
            for (var i = subdirectories.Count - 1; i >= 0; i--) pending.Push(subdirectories[i]);
        }

        progress?.Invoke(entries.Count);
    }

    private static DateTime SafeTime(Func<DateTime> getter)
    {
        try
        {
            return getter();
        }
        catch
        {
            return default;
        }
    }
}
