namespace Beam.Core.Transfer;

/// <summary>A top-level item in an incoming offer, as shown in the "wants to send you" prompt.</summary>
public sealed record IncomingItem(string Name, bool IsDirectory, long Size, int FileCount);

/// <summary>Everything the receiving user needs to decide whether to accept.</summary>
public sealed class IncomingRequest
{
    public required string TransferId { get; init; }

    public required string SenderName { get; init; }

    public required string SenderId { get; init; }

    public required string SenderFingerprint { get; init; }

    public required string SenderAddress { get; init; }

    public required int FileCount { get; init; }

    public required int FolderCount { get; init; }

    public required long TotalBytes { get; init; }

    public required IReadOnlyList<IncomingItem> Items { get; init; }

    public required string DefaultFolder { get; init; }
}

public sealed class IncomingDecision
{
    public bool Accepted { get; init; }

    /// <summary>Folder to save into; defaults to the configured receive folder.</summary>
    public string? DestinationFolder { get; init; }

    public static IncomingDecision Decline() => new() { Accepted = false };

    public static IncomingDecision Accept(string? folder = null) => new() { Accepted = true, DestinationFolder = folder };
}

public enum ConflictAction
{
    Replace,
    KeepBoth,
    Skip,
}

/// <summary>An incoming file whose name is already used in the destination folder.</summary>
public sealed class FileConflict
{
    public required int Index { get; init; }

    /// <summary>Path relative to the destination folder, for display.</summary>
    public required string RelativePath { get; init; }

    public required string ExistingPath { get; init; }

    public required long ExistingSize { get; init; }

    public required DateTime ExistingModifiedUtc { get; init; }

    public required long IncomingSize { get; init; }

    public required DateTime IncomingModifiedUtc { get; init; }
}

/// <summary>Implemented by the UI (or by tests) to involve the user in incoming transfers.</summary>
public interface IIncomingTransferHandler
{
    /// <summary>Ask the user to accept or decline. Cancelled if the sender gives up or the request expires.</summary>
    Task<IncomingDecision> RequestApprovalAsync(IncomingRequest request, TransferSession session, CancellationToken cancellationToken);

    /// <summary>Ask the user what to do with files that already exist. Must return one action per conflict.</summary>
    Task<IReadOnlyList<ConflictAction>> ResolveConflictsAsync(IncomingRequest request, IReadOnlyList<FileConflict> conflicts, CancellationToken cancellationToken);
}

/// <summary>Receiver-side rules supplied by the host application.</summary>
public sealed class IncomingPolicy
{
    public required string DefaultFolder { get; init; }

    /// <summary>Fingerprints of devices whose transfers are accepted without asking.</summary>
    public IReadOnlySet<string> TrustedFingerprints { get; init; } = new HashSet<string>();

    /// <summary>How long the user has to answer an incoming request.</summary>
    public TimeSpan ApprovalTimeout { get; init; } = TimeSpan.FromMinutes(2);
}
