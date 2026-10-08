using System.Text.Json;
using Beam.Core.Diagnostics;
using Beam.Core.Storage;

namespace Beam.Core.History;

/// <summary>Recent transfers, newest first, capped so the file stays small.</summary>
public sealed class HistoryStore
{
    public const int MaxEntries = 500;

    /// <summary>Longest text kept in history (the full text is only in the transfer).</summary>
    public const int MaxTextLength = 4000;

    private readonly string _path;
    private readonly object _gate = new();
    private List<HistoryEntry> _entries;

    public HistoryStore(string path)
    {
        _path = path;
        _entries = Load(path);
    }

    public event Action? Changed;

    public IReadOnlyList<HistoryEntry> Entries
    {
        get
        {
            lock (_gate) return _entries.ToList();
        }
    }

    public void Add(HistoryEntry entry)
    {
        lock (_gate)
        {
            _entries.RemoveAll(e => e.Id == entry.Id && e.Direction == entry.Direction);
            _entries.Insert(0, entry);
            if (_entries.Count > MaxEntries) _entries.RemoveRange(MaxEntries, _entries.Count - MaxEntries);
            Save();
        }

        Changed?.Invoke();
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            Save();
        }

        Changed?.Invoke();
    }

    private void Save()
    {
        try
        {
            AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(_entries, StorageJson.Default.ListHistoryEntry));
        }
        catch (Exception ex)
        {
            Log.Error("Could not save history", ex);
        }
    }

    private static List<HistoryEntry> Load(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize(File.ReadAllText(path), StorageJson.Default.ListHistoryEntry) ?? new();
        }
        catch (Exception ex)
        {
            Log.Warn("History file was unreadable; starting fresh", ex);
        }

        return new();
    }
}
