using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Beam.Core.Diagnostics;
using Beam.Core.Protocol;
using Beam.Core.Storage;

namespace Beam.Core.Transfer;

public sealed class ResumeFile
{
    public int Index { get; set; }

    public string RelativePath { get; set; } = "";

    public string TargetPath { get; set; } = "";

    public string PartPath { get; set; } = "";

    public long Size { get; set; }

    public long ModifiedUnixMs { get; set; }

    /// <summary>User chose "Replace" for an existing file at <see cref="TargetPath"/>.</summary>
    public bool Replace { get; set; }

    public bool Done { get; set; }

    public bool Failed { get; set; }
}

/// <summary>Receiver-side state of an accepted transfer, persisted so an interrupted transfer can resume.</summary>
public sealed class ResumeRecord
{
    public string TransferId { get; set; } = "";

    public string SenderFingerprint { get; set; } = "";

    public string SenderId { get; set; } = "";

    public string SenderName { get; set; } = "";

    public string DestinationFolder { get; set; } = "";

    public string ManifestHash { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public int SkippedCount { get; set; }

    public List<string> RootNames { get; set; } = new();

    public List<string> SavedRootPaths { get; set; } = new();

    public List<ResumeFile> Files { get; set; } = new();
}

/// <summary>Stores <see cref="ResumeRecord"/>s and cleans up partial files that will never be resumed.</summary>
public sealed class ResumeStore
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);

    private readonly string _directory;
    private readonly object _gate = new();

    public ResumeStore(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);
    }

    public void Save(ResumeRecord record)
    {
        lock (_gate)
        {
            try
            {
                AtomicFile.WriteAllText(PathFor(record.TransferId), JsonSerializer.Serialize(record, StorageJson.Default.ResumeRecord));
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not save resume state for {record.TransferId}", ex);
            }
        }
    }

    public ResumeRecord? TryLoad(string transferId)
    {
        if (!IsValidId(transferId)) return null;
        lock (_gate)
        {
            try
            {
                var path = PathFor(transferId);
                if (!File.Exists(path)) return null;
                var record = JsonSerializer.Deserialize(File.ReadAllText(path), StorageJson.Default.ResumeRecord);
                if (record == null || record.CreatedAt < DateTimeOffset.UtcNow - Lifetime) return null;
                return record;
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not read resume state for {transferId}", ex);
                return null;
            }
        }
    }

    /// <summary>Forgets a transfer. With <paramref name="deleteParts"/>, its unfinished partial files are deleted too.</summary>
    public void Delete(ResumeRecord record, bool deleteParts)
    {
        if (deleteParts)
        {
            foreach (var file in record.Files.Where(f => !f.Done))
            {
                TryDelete(file.PartPath);
            }
        }

        lock (_gate) TryDelete(PathFor(record.TransferId));
    }

    /// <summary>Removes expired records and their partial files. Run at startup.</summary>
    public void CleanupExpired()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_directory, "*.json"))
            {
                ResumeRecord? record = null;
                try
                {
                    record = JsonSerializer.Deserialize(File.ReadAllText(file), StorageJson.Default.ResumeRecord);
                }
                catch
                {
                    // unreadable: remove below
                }

                if (record == null)
                {
                    TryDelete(file);
                }
                else if (record.CreatedAt < DateTimeOffset.UtcNow - Lifetime)
                {
                    Log.Info($"Discarding expired partial transfer {record.TransferId} from {record.SenderName}");
                    Delete(record, deleteParts: true);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Resume cleanup failed", ex);
        }
    }

    /// <summary>Hash identifying the exact list of files offered, so a resume only matches the same transfer.</summary>
    public static string ComputeManifestHash(IReadOnlyList<OfferEntry> entries)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var entry in entries)
        {
            hash.AppendData(Encoding.UTF8.GetBytes($"{entry.Path}\n{entry.Size}\n{(entry.IsDirectory ? 1 : 0)}\n"));
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private string PathFor(string transferId) => Path.Combine(_directory, transferId + ".json");

    private static bool IsValidId(string id) => id.Length is > 0 and <= 64 && id.All(char.IsAsciiLetterOrDigit);

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not delete {path}: {ex.Message}");
        }
    }
}
