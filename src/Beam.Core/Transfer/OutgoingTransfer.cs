using System.Security.Cryptography;
using Beam.Core.Diagnostics;
using Beam.Core.Discovery;
using Beam.Core.Files;
using Beam.Core.Protocol;

namespace Beam.Core.Transfer;

/// <summary>
/// Sends a set of files and folders to one device. Survives connection drops by reconnecting
/// and asking the receiver where to continue; every file is verified with SHA-256.
/// </summary>
internal sealed class OutgoingTransfer
{
    private static readonly TimeSpan IoTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ApprovalWait = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan ResultWait = TimeSpan.FromMinutes(2);

    private readonly TransferService _service;
    private readonly TransferSession _session;
    private readonly IReadOnlyList<string> _sourcePaths;
    private readonly Func<DeviceInfo> _target;
    private Manifest? _manifest;
    private List<OfferEntry>? _offer;
    private bool _accepted;
    private int _running;

    public OutgoingTransfer(TransferService service, TransferSession session, IReadOnlyList<string> sourcePaths, Func<DeviceInfo> target)
    {
        _service = service;
        _session = session;
        _sourcePaths = sourcePaths;
        _target = target;
        _session.SetResumeAction(Start);
    }

    public void Start()
    {
        if (Interlocked.Exchange(ref _running, 1) == 1) return;
        // RunAsync handles every exception and always ends in Finish(), which clears _running,
        // or in a completed transfer (which can't be resumed).
        _ = Task.Run(RunAsync);
    }

    private string PeerName => _session.PeerName;

    private async Task RunAsync()
    {
        var token = _session.CancellationToken;
        try
        {
            if (_manifest == null)
            {
                _session.SetState(TransferState.Preparing);
                _manifest = await Task.Run(() => ManifestBuilder.Build(_sourcePaths, token), token).ConfigureAwait(false);
                if (_manifest.Entries.Count == 0)
                {
                    throw new TransferException(TransferErrorKind.NothingToSend,
                        "None of the selected files could be read. They may have been moved or deleted.",
                        string.Join(Environment.NewLine, _manifest.Warnings));
                }

                foreach (var warning in _manifest.Warnings) Log.Warn($"Not sending: {warning}");
                _offer = _manifest.Entries.Select(e => new OfferEntry
                {
                    Path = e.RelativePath,
                    Size = e.Size,
                    IsDirectory = e.IsDirectory,
                    Modified = e.ModifiedUtc == default ? 0 : new DateTimeOffset(e.ModifiedUtc, TimeSpan.Zero).ToUnixTimeMilliseconds(),
                }).ToList();
                _session.SetDescription(_manifest.RootNames);
                _session.SetTotals(_manifest.FileCount, _manifest.TotalBytes, 0, 0, 0);
            }

            var failures = 0;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    await RunAttemptAsync(token).ConfigureAwait(false);
                    return;
                }
                catch (Exception ex) when (!token.IsCancellationRequested && ErrorTranslator.IsTransientNetworkFailure(ex))
                {
                    failures++;
                    var maxAttempts = _accepted ? 6 : 2;
                    Log.Warn($"Transfer {_session.Id} to {PeerName}: attempt {failures} failed ({ex.GetType().Name}: {ex.Message})");
                    if (failures >= maxAttempts) throw;
                    _session.SetState(_accepted ? TransferState.Reconnecting : TransferState.Connecting);
                    await Task.Delay(Backoff(failures), token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            Finish(TransferState.Cancelled, new TransferError(TransferErrorKind.CancelledByUser, "You cancelled the transfer."));
        }
        catch (Exception ex)
        {
            var error = ErrorTranslator.FromException(ex, PeerName);
            Log.Warn($"Transfer {_session.Id} to {PeerName} ended: {error.Kind} — {error.Details ?? error.Message}");
            var state = error.Kind switch
            {
                TransferErrorKind.Declined or TransferErrorKind.TimedOut => TransferState.Declined,
                TransferErrorKind.CancelledByRemote => TransferState.Cancelled,
                _ => TransferState.Failed,
            };
            Finish(state, error);
        }
    }

    /// <summary>
    /// Ends this run. The running flag is cleared *before* the final state is published, so a
    /// "Try again" pressed the moment the failure appears always starts a new run.
    /// </summary>
    private void Finish(TransferState state, TransferError error)
    {
        Volatile.Write(ref _running, 0);
        _session.SetState(state, error);
    }

    private static TimeSpan Backoff(int failures) => TimeSpan.FromSeconds(failures switch
    {
        1 => 2,
        2 => 3,
        3 => 5,
        4 => 8,
        _ => 12,
    });

    private async Task RunAttemptAsync(CancellationToken token)
    {
        var target = _target();
        if (!_accepted) _session.SetState(TransferState.Connecting);

        await using var connection = await SecureTransport.ConnectAsync(
            _service.Identity,
            _service.CreateHello(ConnectionPurpose.Transfer),
            target.Endpoints,
            string.IsNullOrEmpty(target.Fingerprint) ? null : target.Fingerprint,
            PeerName,
            token).ConfigureAwait(false);

        await connection.Channel.SendAsync(FrameType.Offer,
            new OfferMessage { TransferId = _session.Id, Entries = _offer!, Resume = _accepted }, token).ConfigureAwait(false);

        if (!_accepted) _session.SetState(TransferState.WaitingForAcceptance);

        OfferResponseMessage response;
        try
        {
            response = await connection.Channel.ReadMessageAsync<OfferResponseMessage>(
                FrameType.OfferResponse, _accepted ? IoTimeout : ApprovalWait, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            await connection.SendCancelAndCloseAsync(_service.CancelReason).ConfigureAwait(false);
            throw;
        }

        if (!response.Accepted)
            throw new RemoteCancelException(new CancelMessage { Reason = response.Reason ?? Reasons.Declined });

        _accepted = true;
        var plan = ApplyPlan(response.Plan);
        _session.SetState(TransferState.Transferring);
        await SendFilesAsync(connection, plan, token).ConfigureAwait(false);
    }

    private List<PlanItem> ApplyPlan(List<PlanItem> plan)
    {
        var entries = _manifest!.Entries;
        var seen = new HashSet<int>();
        foreach (var item in plan)
        {
            if (item.Index < 0 || item.Index >= entries.Count || entries[item.Index].IsDirectory || !seen.Add(item.Index)
                || item.Offset < 0 || item.Offset > entries[item.Index].Size)
                throw new ProtocolException("Invalid transfer plan.");
        }

        var totalBytes = plan.Sum(p => entries[p.Index].Size);
        var transferred = plan.Sum(p => p.Done ? entries[p.Index].Size : p.Offset);
        var completed = plan.Count(p => p.Done);
        _session.SetTotals(plan.Count, totalBytes, completed, transferred, _manifest.FileCount - plan.Count);
        return plan;
    }

    private async Task SendFilesAsync(PeerConnection connection, List<PlanItem> plan, CancellationToken token)
    {
        var entries = _manifest!.Entries;
        var state = new ReaderState();
        using var io = new CancellationTokenSource();
        var reader = Task.Run(() => ReadLoopAsync(connection, state, token));

        try
        {
            var toSend = plan.Where(p => !p.Done).Select(p => (p.Index, p.Offset)).ToList();
            for (var round = 1; round <= 2 && toSend.Count > 0; round++)
            {
                var expectedResults = state.ResultCount;
                // Small files are read ahead in parallel, so opening them (and antivirus scans) overlaps with sending.
                var readAhead = new Dictionary<int, Task<SmallFile>>();
                var nextToRead = 0;
                for (var i = 0; i < toSend.Count; i++)
                {
                    for (; nextToRead < toSend.Count && nextToRead <= i + TransferTuning.ReadAhead; nextToRead++)
                    {
                        var (readIndex, readOffset) = toSend[nextToRead];
                        if (readOffset == 0 && entries[readIndex].Size <= TransferTuning.SmallFileLimit)
                            readAhead[nextToRead] = Task.Run(() => ReadSmallFileAsync(entries[readIndex], token), CancellationToken.None);
                    }

                    var (index, offset) = toSend[i];
                    var sent = readAhead.Remove(i, out var small)
                        ? await SendSmallFileAsync(connection, index, await small.ConfigureAwait(false), state, io, token).ConfigureAwait(false)
                        : await SendFileAsync(connection, index, offset, state, io, token).ConfigureAwait(false);
                    if (sent) expectedResults++;
                }

                await WaitForResultsAsync(state, reader, expectedResults, token).ConfigureAwait(false);
                toSend = state.TakeRetries().Select(i => (i, 0L)).ToList();
                foreach (var (index, _) in toSend)
                    Log.Warn($"Re-sending {entries[index].RelativePath}: it failed verification on {PeerName}");
            }

            // Anything still flagged for retry after the second round has failed for good.
            foreach (var index in state.TakeRetries())
                _session.FileFailed(entries[index].RelativePath, "The file was damaged in transit and couldn't be verified.");

            ThrowIfRemoteCancelled(state);
            await WriteAsync(io, t => connection.Channel.SendAsync(FrameType.Done, new DoneMessage(), t), token).ConfigureAwait(false);

            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                cts.CancelAfter(ResultWait);
                await reader.WaitAsync(cts.Token).ConfigureAwait(false);
            }

            ThrowIfRemoteCancelled(state);
            if (state.Final == null) throw state.ReaderError ?? new EndOfStreamException("Connection closed before the result arrived.");

            var snapshot = _session.GetSnapshot();
            _session.SetCurrentFile(null);
            _session.SetState(snapshot.FailedFiles > 0 ? TransferState.CompletedWithErrors : TransferState.Completed);
            Log.Info($"Sent {_session.Title} to {PeerName}: {snapshot.CompletedFiles} ok, {snapshot.FailedFiles} failed, {snapshot.SkippedFiles} skipped");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            await connection.SendCancelAndCloseAsync(_service.CancelReason).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex) when (ex is not RemoteCancelException && ex is not ProtocolException)
        {
            // If the receiver explained why it stopped, that explanation is more useful than "connection lost".
            await Task.WhenAny(reader, Task.Delay(1000, CancellationToken.None)).ConfigureAwait(false);
            ThrowIfRemoteCancelled(state);
            if (ex is OperationCanceledException && io.IsCancellationRequested)
                throw new TimeoutException($"{PeerName} stopped receiving data.");
            throw;
        }
        finally
        {
            connection.Abort();
            try { await reader.ConfigureAwait(false); } catch { /* reported via state */ }
        }
    }

    /// <summary>Returns true if the file was sent (a FileResult will follow), false if it couldn't be read.</summary>
    private async Task<bool> SendFileAsync(PeerConnection connection, int index, long planOffset, ReaderState state, CancellationTokenSource io, CancellationToken token)
    {
        var entry = _manifest!.Entries[index];
        FileStream stream;
        try
        {
            stream = new FileStream(entry.SourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            await ReportUnreadableAsync(connection, index, planOffset, ex, io, token).ConfigureAwait(false);
            return false;
        }

        await using (stream.ConfigureAwait(false))
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffers = _service.RentBuffers();
            Task<int>? pendingRead = null;
            try
            {
                long offset = planOffset;
                try
                {
                    if (stream.Length != entry.Size)
                        throw new IOException("The file was changed after it was selected.");
                    if (offset > 0) offset = await HashPrefixAsync(stream, hash, offset, buffers.A, token).ConfigureAwait(false);
                    stream.Position = offset;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    await ReportUnreadableAsync(connection, index, planOffset, ex, io, token).ConfigureAwait(false);
                    return false;
                }

                if (offset != planOffset) _session.AddTransferred(offset - planOffset);
                _session.SetCurrentFile(entry.RelativePath);
                await WriteAsync(io, t => connection.Channel.SendAsync(FrameType.FileHeader, new FileHeaderMessage { Index = index, Offset = offset }, t), token)
                    .ConfigureAwait(false);

                var current = buffers.A;
                var next = buffers.B;
                var sent = offset;
                int read;
                try
                {
                    read = await ReadAndHashAsync(stream, hash, current, entry.Size - sent, token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    await ReportUnreadableAsync(connection, index, 0, ex, io, token).ConfigureAwait(false);
                    _session.AddTransferred(-offset);
                    return false;
                }

                while (read > 0)
                {
                    // Read and hash the next chunk while this one is encrypted and sent.
                    var remainingAfter = entry.Size - sent - read;
                    var nextBuffer = next;
                    pendingRead = remainingAfter > 0
                        ? Task.Run(() => ReadAndHashAsync(stream, hash, nextBuffer, remainingAfter, token), token)
                        : null;

                    var chunk = current;
                    var length = read;
                    await _service.SendLimiter.WaitAsync(length, token).ConfigureAwait(false);
                    await WriteAsync(io, t => connection.Channel.SendDataAsync(chunk, length, t), token).ConfigureAwait(false);
                    sent += read;
                    _session.AddTransferred(read);

                    if (state.RemoteCancel != null) ThrowIfRemoteCancelled(state);
                    token.ThrowIfCancellationRequested();

                    try
                    {
                        read = pendingRead == null ? 0 : await pendingRead.ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        pendingRead = null;
                        await ReportUnreadableAsync(connection, index, 0, ex, io, token).ConfigureAwait(false);
                        _session.AddTransferred(-sent);
                        return false;
                    }

                    pendingRead = null;
                    (current, next) = (next, current);
                }

                if (sent != entry.Size)
                {
                    await ReportUnreadableAsync(connection, index, 0, new IOException("The file got smaller while it was being sent."), io, token)
                        .ConfigureAwait(false);
                    _session.AddTransferred(-sent);
                    return false;
                }

                var footer = new FileFooterMessage
                {
                    Index = index,
                    Length = sent,
                    Sha256 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(),
                };
                await WriteAsync(io, t => connection.Channel.SendAsync(FrameType.FileFooter, footer, t), token).ConfigureAwait(false);
                return true;
            }
            finally
            {
                if (pendingRead != null)
                {
                    try { await pendingRead.ConfigureAwait(false); } catch { /* abandoned */ }
                }

                _service.ReturnBuffers(buffers);
            }
        }
    }

    private sealed record SmallFile(byte[]? Data, string Sha256, Exception? Error);

    /// <summary>Reads and hashes a whole small file. Never throws: problems are returned in <see cref="SmallFile.Error"/>.</summary>
    private static async Task<SmallFile> ReadSmallFileAsync(ManifestEntry entry, CancellationToken token)
    {
        try
        {
            await using var stream = new FileStream(entry.SourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
            if (stream.Length != entry.Size) throw new IOException("The file was changed after it was selected.");
            var data = new byte[entry.Size];
            await stream.ReadExactlyAsync(data, token).ConfigureAwait(false);
            if (stream.Length != entry.Size) throw new IOException("The file was changed after it was selected.");
            return new SmallFile(data, Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant(), null);
        }
        catch (EndOfStreamException)
        {
            return new SmallFile(null, "", new IOException("The file got smaller while it was being sent."));
        }
        catch (Exception ex)
        {
            return new SmallFile(null, "", ex);
        }
    }

    /// <summary>Sends a whole small file (header, data and footer) in a single write. Returns false if it couldn't be read.</summary>
    private async Task<bool> SendSmallFileAsync(PeerConnection connection, int index, SmallFile file, ReaderState state, CancellationTokenSource io, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (file.Error is OperationCanceledException) throw file.Error;
        if (file.Error != null || file.Data == null)
        {
            if (file.Error is not (IOException or UnauthorizedAccessException or System.Security.SecurityException))
                Log.Warn($"Unexpected error reading {_manifest!.Entries[index].SourcePath}", file.Error);
            await ReportUnreadableAsync(connection, index, 0, file.Error ?? new IOException("The file couldn't be read."), io, token).ConfigureAwait(false);
            return false;
        }

        var data = file.Data;
        var header = FrameChannel.Encode(FrameType.FileHeader, new FileHeaderMessage { Index = index, Offset = 0 });
        var footer = FrameChannel.Encode(FrameType.FileFooter, new FileFooterMessage { Index = index, Length = data.Length, Sha256 = file.Sha256 });
        var chunks = (data.Length + FrameChannel.DataChunkSize - 1) / FrameChannel.DataChunkSize;
        var frames = new byte[header.Length + data.Length + chunks * FrameChannel.HeaderSize + footer.Length];
        header.CopyTo(frames, 0);
        var position = header.Length;
        for (var offset = 0; offset < data.Length; offset += FrameChannel.DataChunkSize)
        {
            var length = Math.Min(FrameChannel.DataChunkSize, data.Length - offset);
            FrameChannel.WriteDataHeader(frames.AsSpan(position), length);
            data.AsSpan(offset, length).CopyTo(frames.AsSpan(position + FrameChannel.HeaderSize));
            position += FrameChannel.HeaderSize + length;
        }

        footer.CopyTo(frames, position);

        _session.SetCurrentFile(_manifest!.Entries[index].RelativePath);
        if (data.Length > 0) await _service.SendLimiter.WaitAsync(data.Length, token).ConfigureAwait(false);
        await WriteAsync(io, t => connection.Channel.SendFramesAsync(frames, t), token).ConfigureAwait(false);
        _session.AddTransferred(data.Length);
        if (state.RemoteCancel != null) ThrowIfRemoteCancelled(state);
        return true;
    }

    private async Task ReportUnreadableAsync(PeerConnection connection, int index, long countedBytes, Exception ex, CancellationTokenSource io, CancellationToken token)
    {
        var entry = _manifest!.Entries[index];
        Log.Warn($"Could not read {entry.SourcePath}: {ex.Message}");
        var reason = ex is UnauthorizedAccessException
            ? "Beam isn't allowed to read this file."
            : ex.Message.Contains("changed", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("smaller", StringComparison.OrdinalIgnoreCase)
                ? "The file changed while it was being sent. Send it again."
                : "The file couldn't be read. It may be open in another program, moved, or deleted.";
        _session.FileFailed(entry.RelativePath, reason);
        _session.AddTransferred(-countedBytes);
        await WriteAsync(io, t => connection.Channel.SendAsync(FrameType.SourceFileError, new SourceFileErrorMessage { Index = index, Error = reason }, t), token)
            .ConfigureAwait(false);
    }

    private static async Task<long> HashPrefixAsync(FileStream stream, IncrementalHash hash, long length, byte[] buffer, CancellationToken token)
    {
        stream.Position = 0;
        var remaining = length;
        while (remaining > 0)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), token).ConfigureAwait(false);
            if (read == 0) return 0; // file is shorter than expected: start over
            hash.AppendData(buffer, 0, read);
            remaining -= read;
        }

        return length;
    }

    /// <summary>
    /// Fills the buffer after its header space and adds the bytes to the hash; returns 0 at end of file.
    /// Calls never overlap, so the hash sees chunks in order.
    /// </summary>
    private static async Task<int> ReadAndHashAsync(FileStream stream, IncrementalHash hash, byte[] buffer, long remaining, CancellationToken token)
    {
        var want = (int)Math.Min(FrameChannel.DataChunkSize, Math.Max(0, remaining));
        var total = 0;
        while (total < want)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(FrameChannel.HeaderSize + total, want - total), token).ConfigureAwait(false);
            if (read == 0) break;
            total += read;
        }

        hash.AppendData(buffer, FrameChannel.HeaderSize, total);
        return total;
    }

    /// <summary>Writes with a stall timeout. Not tied to user cancellation so a frame is never cut in half.</summary>
    private static async Task WriteAsync(CancellationTokenSource io, Func<CancellationToken, Task> write, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        io.CancelAfter(IoTimeout);
        await write(io.Token).ConfigureAwait(false);
    }

    private async Task WaitForResultsAsync(ReaderState state, Task reader, int expected, CancellationToken token)
    {
        while (state.ResultCount < expected)
        {
            ThrowIfRemoteCancelled(state);
            if (reader.IsCompleted)
                throw state.ReaderError ?? new EndOfStreamException("Connection closed while waiting for confirmation.");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(ResultWait);
            try
            {
                await Task.WhenAny(state.Signal.WaitAsync(cts.Token), reader).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                throw new TimeoutException($"{PeerName} didn't confirm the files it received.");
            }

            token.ThrowIfCancellationRequested();
            if (cts.IsCancellationRequested) throw new TimeoutException($"{PeerName} didn't confirm the files it received.");
        }

        ThrowIfRemoteCancelled(state);
    }

    private static void ThrowIfRemoteCancelled(ReaderState state)
    {
        if (state.RemoteCancel != null) throw state.RemoteCancel;
    }

    private async Task ReadLoopAsync(PeerConnection connection, ReaderState state, CancellationToken token)
    {
        var entries = _manifest!.Entries;
        try
        {
            while (true)
            {
                var frame = await connection.Channel.ReadAsync(token).ConfigureAwait(false);
                switch (frame.Type)
                {
                    case FrameType.FileResult:
                        var result = FrameChannel.Parse<FileResultMessage>(frame);
                        if (result.Index < 0 || result.Index >= entries.Count) throw new ProtocolException("Invalid file index.");
                        if (result.Ok)
                        {
                            _session.FileCompleted();
                        }
                        else if (result.Retry)
                        {
                            _session.AddTransferred(-entries[result.Index].Size);
                            state.AddRetry(result.Index);
                        }
                        else
                        {
                            _session.FileFailed(entries[result.Index].RelativePath, result.Error ?? "The file couldn't be saved.");
                        }

                        state.ResultReceived();
                        break;
                    case FrameType.Result:
                        state.Final = FrameChannel.Parse<ResultMessage>(frame);
                        return;
                    case FrameType.Cancel:
                        state.RemoteCancel = new RemoteCancelException(FrameChannel.Parse<CancelMessage>(frame));
                        state.Signal.Release();
                        return;
                    default:
                        throw new ProtocolException($"Unexpected {frame.Type} from receiver.");
                }
            }
        }
        catch (Exception ex)
        {
            state.ReaderError = ex;
            state.Signal.Release();
        }
    }

    private sealed class ReaderState
    {
        private readonly object _gate = new();
        private readonly List<int> _retries = new();
        private int _resultCount;

        public SemaphoreSlim Signal { get; } = new(0);

        public int ResultCount => Volatile.Read(ref _resultCount);

        public ResultMessage? Final { get; set; }

        public RemoteCancelException? RemoteCancel { get; set; }

        public Exception? ReaderError { get; set; }

        public void ResultReceived()
        {
            Interlocked.Increment(ref _resultCount);
            Signal.Release();
        }

        public void AddRetry(int index)
        {
            lock (_gate) _retries.Add(index);
        }

        public List<int> TakeRetries()
        {
            lock (_gate)
            {
                var copy = _retries.ToList();
                _retries.Clear();
                return copy;
            }
        }
    }
}
