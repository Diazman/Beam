using Beam.Core.Transfer;

namespace Beam.Core.History;

public enum HistoryStatus
{
    Completed,
    CompletedWithErrors,
    Failed,
    Cancelled,
    Declined,
    Interrupted,
}

/// <summary>One finished transfer. Only metadata is kept — never copies of the files.</summary>
public sealed class HistoryEntry
{
    public string Id { get; set; } = "";

    public TransferDirection Direction { get; set; }

    public string DeviceName { get; set; } = "";

    public string DeviceId { get; set; } = "";

    /// <summary>Short description, e.g. "Photos", "report.pdf" or "report.pdf and 2 more".</summary>
    public string Title { get; set; } = "";

    public int FileCount { get; set; }

    public long TotalBytes { get; set; }

    public DateTimeOffset Timestamp { get; set; }

    public HistoryStatus Status { get; set; }

    public string? Message { get; set; }

    /// <summary>Received files: folder they were saved in.</summary>
    public string? Folder { get; set; }

    /// <summary>Text transfers: the text (shortened to <see cref="HistoryStore.MaxTextLength"/> characters).</summary>
    public string? Text { get; set; }

    /// <summary>Received files: full paths of the top-level items, used for "Show in folder".</summary>
    public List<string> Paths { get; set; } = new();
}
