namespace Beam.Core.Transfer;

public enum TransferDirection
{
    Send,
    Receive,
}

public enum TransferState
{
    /// <summary>Sender: scanning the selected files and folders.</summary>
    Preparing,
    Connecting,
    /// <summary>Sender: waiting for the other person to accept.</summary>
    WaitingForAcceptance,
    /// <summary>Receiver: asking the local user.</summary>
    AwaitingDecision,
    Transferring,
    /// <summary>Sender: connection dropped, trying to reconnect and resume.</summary>
    Reconnecting,
    /// <summary>Receiver: connection dropped; partial files are kept so the sender can resume.</summary>
    Interrupted,
    Completed,
    CompletedWithErrors,
    Failed,
    Cancelled,
    Declined,
}

public sealed record FileFailure(string RelativePath, string Reason);

/// <summary>Immutable view of a session for display.</summary>
public sealed record TransferSnapshot(
    TransferState State,
    long TotalBytes,
    long TransferredBytes,
    int TotalFiles,
    int CompletedFiles,
    int FailedFiles,
    int SkippedFiles,
    string? CurrentFile,
    TransferError? Error);

/// <summary>
/// Live state of one transfer, shared between the engine (which updates it from background
/// threads) and the UI (which polls <see cref="GetSnapshot"/> and listens to <see cref="StateChanged"/>).
/// </summary>
public sealed class TransferSession
{
    private readonly object _gate = new();
    private readonly List<FileFailure> _failures = new();
    private CancellationTokenSource _cts = new();
    private long _totalBytes;
    private long _transferredBytes;
    private int _totalFiles;
    private int _completedFiles;
    private int _failedFiles;
    private int _skippedFiles;
    private string? _currentFile;
    private TransferState _state;
    private TransferError? _error;
    private Action? _resume;
    private Action? _discard;

    internal TransferSession(string id, TransferDirection direction, string peerId, string peerName, string peerFingerprint)
    {
        Id = id;
        Direction = direction;
        PeerId = peerId;
        PeerName = peerName;
        PeerFingerprint = peerFingerprint;
        StartedAt = DateTimeOffset.Now;
        _state = direction == TransferDirection.Send ? TransferState.Preparing : TransferState.AwaitingDecision;
    }

    public string Id { get; }

    public TransferDirection Direction { get; }

    public string PeerId { get; }

    public string PeerName { get; }

    public string PeerFingerprint { get; }

    public DateTimeOffset StartedAt { get; }

    public DateTimeOffset? FinishedAt { get; private set; }

    public string Title { get; private set; } = "";

    public IReadOnlyList<string> RootNames { get; private set; } = Array.Empty<string>();

    /// <summary>Receiver: the folder files are saved into.</summary>
    public string? DestinationFolder { get; internal set; }

    /// <summary>For text transfers: the text itself (otherwise null).</summary>
    public string? Text { get; internal set; }

    public bool IsText => Text != null;

    /// <summary>Receiver: full local paths of the top-level items that were saved.</summary>
    public IReadOnlyList<string> SavedRootPaths { get; internal set; } = Array.Empty<string>();

    /// <summary>Raised (on a background thread) whenever <see cref="State"/> changes.</summary>
    public event Action<TransferSession>? StateChanged;

    public TransferState State
    {
        get
        {
            lock (_gate) return _state;
        }
    }

    public TransferError? Error
    {
        get
        {
            lock (_gate) return _error;
        }
    }

    public bool IsFinished => IsFinalState(State);

    public bool CanCancel => !IsFinished;

    public bool CanResume
    {
        get
        {
            lock (_gate) return _state == TransferState.Failed && _resume != null && (_error?.IsRetryable ?? false);
        }
    }

    public IReadOnlyList<FileFailure> Failures
    {
        get
        {
            lock (_gate) return _failures.ToList();
        }
    }

    internal CancellationToken CancellationToken
    {
        get
        {
            lock (_gate) return _cts.Token;
        }
    }

    public static bool IsFinalState(TransferState state) => state is TransferState.Completed or TransferState.CompletedWithErrors
        or TransferState.Failed or TransferState.Cancelled or TransferState.Declined;

    public TransferSnapshot GetSnapshot()
    {
        lock (_gate)
        {
            return new TransferSnapshot(_state, _totalBytes, Interlocked.Read(ref _transferredBytes), _totalFiles,
                _completedFiles, _failedFiles, _skippedFiles, _currentFile, _error);
        }
    }

    /// <summary>Stops the transfer. Partial files are removed on the receiving side.</summary>
    public void Cancel()
    {
        Action? discard;
        lock (_gate)
        {
            if (IsFinalState(_state)) return;
            discard = _state == TransferState.Interrupted ? _discard : null;
            _cts.Cancel();
        }

        // An interrupted receive has no running task to observe the cancellation; clean up directly.
        discard?.Invoke();
    }

    /// <summary>Sender: try again after a failure, continuing where the transfer stopped.</summary>
    public void Resume()
    {
        Action? resume;
        lock (_gate)
        {
            if (!(_state == TransferState.Failed && _resume != null)) return;
            resume = _resume;
            if (_cts.IsCancellationRequested)
            {
                _cts.Dispose();
                _cts = new CancellationTokenSource();
            }

            _error = null;
            FinishedAt = null;
        }

        resume();
    }

    internal void SetResumeAction(Action? resume)
    {
        lock (_gate) _resume = resume;
    }

    internal void SetDiscardAction(Action? discard)
    {
        lock (_gate) _discard = discard;
    }

    internal void SetDescription(IReadOnlyList<string> rootNames)
    {
        RootNames = rootNames;
        Title = Files.Manifest.Describe(rootNames);
    }

    internal void SetTotals(int totalFiles, long totalBytes, int completedFiles, long transferredBytes, int skippedFiles)
    {
        lock (_gate)
        {
            _totalFiles = totalFiles;
            _totalBytes = totalBytes;
            _completedFiles = completedFiles;
            _failedFiles = 0;
            _skippedFiles = skippedFiles;
            _failures.Clear();
            Interlocked.Exchange(ref _transferredBytes, transferredBytes);
        }
    }

    internal void AddTransferred(long bytes) => Interlocked.Add(ref _transferredBytes, bytes);

    internal void SetCurrentFile(string? relativePath)
    {
        lock (_gate) _currentFile = relativePath;
    }

    internal void FileCompleted()
    {
        lock (_gate) _completedFiles++;
    }

    internal void FileFailed(string relativePath, string reason)
    {
        lock (_gate)
        {
            _failedFiles++;
            _failures.Add(new FileFailure(relativePath, reason));
        }
    }

    internal void SetState(TransferState state, TransferError? error = null)
    {
        lock (_gate)
        {
            if (IsFinalState(_state) && IsFinalState(state)) return; // first final state wins
            if (_state == state && error == null) return;
            _state = state;
            if (error != null || !IsFinalState(state)) _error = error;
            if (IsFinalState(state))
            {
                FinishedAt = DateTimeOffset.Now;
                _currentFile = null;
            }
        }

        try
        {
            StateChanged?.Invoke(this);
        }
        catch (Exception ex)
        {
            Diagnostics.Log.Error("StateChanged handler threw", ex);
        }
    }
}
