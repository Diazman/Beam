using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using Beam.Core.Diagnostics;
using Beam.Core.Files;
using Beam.Core.Protocol;
using Beam.Core.Storage;
using Beam.Core.Localization;

namespace Beam.Core.Transfer;

// Reading the offered file list and planning where each file goes (names, conflicts, folders).
internal sealed partial class IncomingTransfer
{
    private static List<IncomingEntry>? ParseEntries(OfferMessage offer)
    {
        if (string.IsNullOrEmpty(offer.TransferId) || offer.TransferId.Length > 64 || !offer.TransferId.All(char.IsAsciiLetterOrDigit))
            return null;
        if (offer.Entries.Count == 0 || offer.Entries.Count > 500_000) return null;

        var result = new List<IncomingEntry>(offer.Entries.Count);
        long total = 0;
        for (var i = 0; i < offer.Entries.Count; i++)
        {
            var entry = offer.Entries[i];
            var segments = SafePath.SplitRelative(entry.Path);
            if (segments == null || entry.Size < 0 || (entry.IsDirectory && entry.Size != 0)) return null;
            total += entry.Size;
            if (total < 0) return null;
            result.Add(new IncomingEntry(i, segments, entry.Size, entry.IsDirectory, entry.Modified));
        }

        return result;
    }

    private static IReadOnlyList<IncomingItem> SummarizeItems(List<IncomingEntry> entries)
    {
        var items = new List<IncomingItem>();
        foreach (var group in entries.GroupBy(e => e.Segments[0], StringComparer.OrdinalIgnoreCase))
        {
            var isDirectory = group.Any(e => e.IsDirectory || e.Segments.Length > 1);
            var files = group.Where(e => !e.IsDirectory).ToList();
            items.Add(new IncomingItem(group.Key, isDirectory, files.Sum(f => f.Size), files.Count));
        }

        return items;
    }

    private sealed record IncomingEntry(int Index, string[] Segments, long Size, bool IsDirectory, long Modified);

    private sealed class Plan
    {
        public List<ResumeFile> Files { get; } = new();

        public List<FileConflict> Conflicts { get; } = new();

        public List<string> Directories { get; } = new();

        /// <summary>The same as <see cref="Directories"/>, to look up (a folder of 100,000 files has many).</summary>
        public HashSet<string> DirectorySet { get; } = new(PathComparer);

        public List<string> RootPaths { get; } = new();

        public HashSet<string> PlannedPaths { get; } = new(PathComparer);

        public int SkippedCount { get; set; }
    }

    private static StringComparer PathComparer => OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    /// <summary>
    /// Decides where every incoming entry goes. Folders merge into existing folders of the same name;
    /// a folder whose name is taken by a file gets a "(1)" suffix; existing files become conflicts.
    /// </summary>
    private static Plan BuildPlan(string destination, List<IncomingEntry> entries, string transferId)
    {
        var plan = new Plan();
        var directoryMap = new Dictionary<string, string[]>(PathComparer);
        var partSuffix = "." + transferId[..Math.Min(8, transferId.Length)] + PartExtension;

        string[] ResolveDirectory(string[] segments)
        {
            string[] actual = Array.Empty<string>();
            for (var i = 0; i < segments.Length; i++)
            {
                var key = string.Join('/', segments.Take(i + 1));
                if (directoryMap.TryGetValue(key, out var mapped))
                {
                    actual = mapped;
                    continue;
                }

                var parent = actual;
                var name = segments[i];
                var full = SafePath.Combine(destination, parent.Append(name));
                if (File.Exists(full) || plan.PlannedPaths.Contains(full) || IsLink(full))
                {
                    // Never merge into a symbolic link / junction: it could point outside the destination.
                    name = FileNaming.MakeUnique(name, n =>
                    {
                        var candidate = SafePath.Combine(destination, parent.Append(n));
                        return File.Exists(candidate) || plan.PlannedPaths.Contains(candidate) || IsLink(candidate);
                    }, isDirectory: true);
                    full = SafePath.Combine(destination, parent.Append(name));
                }

                actual = parent.Append(name).ToArray();
                directoryMap[key] = actual;
                if (plan.DirectorySet.Add(full)) plan.Directories.Add(full);
            }

            return actual;
        }

        foreach (var entry in entries)
        {
            if (entry.IsDirectory)
            {
                var resolved = ResolveDirectory(entry.Segments);
                if (entry.Segments.Length == 1) plan.RootPaths.Add(SafePath.Combine(destination, resolved));
                continue;
            }

            var directory = entry.Segments.Length > 1 ? ResolveDirectory(entry.Segments[..^1]) : Array.Empty<string>();
            var name = entry.Segments[^1];
            var target = SafePath.Combine(destination, directory.Append(name));
            var isConflict = false;
            if (plan.PlannedPaths.Contains(target) || Directory.Exists(target))
            {
                name = FileNaming.MakeUnique(name, n =>
                {
                    var candidate = SafePath.Combine(destination, directory.Append(n));
                    return plan.PlannedPaths.Contains(candidate) || FileNaming.Exists(candidate);
                });
                target = SafePath.Combine(destination, directory.Append(name));
            }
            else if (File.Exists(target))
            {
                isConflict = true;
            }

            plan.PlannedPaths.Add(target);
            var file = new ResumeFile
            {
                Index = entry.Index,
                RelativePath = string.Join('/', entry.Segments),
                TargetPath = target,
                PartPath = target + partSuffix,
                Size = entry.Size,
                ModifiedUnixMs = entry.Modified,
            };
            plan.Files.Add(file);
            if (entry.Segments.Length == 1) plan.RootPaths.Add(target);

            if (isConflict)
            {
                var existing = new FileInfo(target);
                plan.Conflicts.Add(new FileConflict
                {
                    Index = entry.Index,
                    RelativePath = Path.GetRelativePath(destination, target),
                    ExistingPath = target,
                    ExistingSize = existing.Exists ? existing.Length : 0,
                    ExistingModifiedUtc = existing.Exists ? existing.LastWriteTimeUtc : default,
                    IncomingSize = entry.Size,
                    IncomingModifiedUtc = entry.Modified > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(entry.Modified).UtcDateTime : default,
                });
            }
        }

        return plan;
    }

    private static bool IsLink(string path)
    {
        try
        {
            var info = new DirectoryInfo(path);
            return info.Exists && (info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.LinkTarget != null);
        }
        catch
        {
            return true; // can't tell: treat as unsafe
        }
    }

    private static void ApplyConflictActions(Plan plan, IReadOnlyList<ConflictAction> actions)
    {
        if (actions.Count != plan.Conflicts.Count) throw new InvalidOperationException("One action is required per conflict.");
        // Lookups instead of searching the lists for each conflict: skipping 100,000 existing files stays quick.
        var byIndex = plan.Files.ToDictionary(f => f.Index);
        var rootIndexes = new Dictionary<string, int>(PathComparer);
        for (var r = 0; r < plan.RootPaths.Count; r++) rootIndexes.TryAdd(plan.RootPaths[r], r);
        var skipped = new HashSet<ResumeFile>();
        var skippedRoots = new HashSet<string>(PathComparer);
        for (var i = 0; i < actions.Count; i++)
        {
            var file = byIndex[plan.Conflicts[i].Index];
            switch (actions[i])
            {
                case ConflictAction.Replace:
                    file.Replace = true;
                    break;
                case ConflictAction.KeepBoth:
                    var directory = Path.GetDirectoryName(file.TargetPath)!;
                    var name = FileNaming.MakeUnique(Path.GetFileName(file.TargetPath), n =>
                    {
                        var candidate = Path.Combine(directory, n);
                        return FileNaming.Exists(candidate) || plan.PlannedPaths.Contains(candidate);
                    });
                    var oldTarget = file.TargetPath;
                    file.TargetPath = Path.Combine(directory, name);
                    file.PartPath = file.TargetPath + file.PartPath[oldTarget.Length..];
                    plan.PlannedPaths.Add(file.TargetPath);
                    if (rootIndexes.Remove(oldTarget, out var rootIndex))
                    {
                        plan.RootPaths[rootIndex] = file.TargetPath;
                        rootIndexes[file.TargetPath] = rootIndex;
                    }
                    break;
                case ConflictAction.Skip:
                    skipped.Add(file);
                    plan.SkippedCount++;
                    skippedRoots.Add(file.TargetPath);
                    break;
            }
        }

        // Removed in one pass each (removing one by one went through the whole list every time).
        if (skipped.Count > 0) plan.Files.RemoveAll(skipped.Contains);
        if (skippedRoots.Count > 0) plan.RootPaths.RemoveAll(skippedRoots.Contains);
    }

    private static List<string> ComputeSavedRootPaths(ResumeRecord record)
    {
        // Files may have been renamed at the last moment; prefer their final paths.
        var byPath = new Dictionary<string, ResumeFile>(PathComparer);
        foreach (var f in record.Files)
        {
            byPath.TryAdd(f.TargetPath, f);
            byPath.TryAdd(f.PartPath, f);
        }

        var result = new List<string>();
        var listed = new HashSet<string>(PathComparer);
        foreach (var path in record.SavedRootPaths)
        {
            if (byPath.TryGetValue(path, out var file) && !file.Done) continue;
            if (FileNaming.Exists(path) && listed.Add(path)) result.Add(path);
        }

        foreach (var file in record.Files.Where(f => f.Done && !f.RelativePath.Contains('/')))
        {
            if (listed.Add(file.TargetPath)) result.Add(file.TargetPath);
        }

        return result;
    }
}
