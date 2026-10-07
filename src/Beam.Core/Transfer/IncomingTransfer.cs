using System.Security.Cryptography;
using Beam.Core.Diagnostics;
using Beam.Core.Files;
using Beam.Core.Protocol;

namespace Beam.Core.Transfer;

/// <summary>
/// Handles one incoming connection that wants to send files: asks the user, resolves name
/// conflicts, writes data to temporary ".beampart" files, verifies each file's SHA-256 and only
/// then moves it into place. Partial files are kept after a connection drop so the sender can resume.
/// </summary>
internal sealed class IncomingTransfer
{
    public const string PartExtension = ".beampart";
    private const long FreeSpaceMargin = 16 * 1024 * 1024;
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan SaveInterval = TimeSpan.FromSeconds(1);

    private readonly TransferService _service;
    private readonly PeerConnection _connection;
    private TransferSession? _session;

    public IncomingTransfer(TransferService service, PeerConnection connection)
    {
        _service = service;
        _connection = connection;
    }

    private string PeerName => _connection.RemoteHello.DeviceName;

    private string Fingerprint => _connection.RemoteFingerprint;

    public async Task RunAsync(CancellationToken serviceToken)
    {
        var offer = await _connection.Channel.ReadMessageAsync<OfferMessage>(FrameType.Offer, TimeSpan.FromSeconds(30), serviceToken)
            .ConfigureAwait(false);

        var entries = ParseEntries(offer);
        if (entries == null)
        {
            Log.Warn($"Rejected malformed offer from {PeerName} ({_connection.RemoteEndPoint})");
            await _connection.SendCancelAndCloseAsync(Reasons.Protocol, "Invalid file list").ConfigureAwait(false);
            return;
        }

        var manifestHash = ResumeStore.ComputeManifestHash(offer.Entries);
        if (offer.Resume)
        {
            var record = _service.ResumeStore.TryLoad(offer.TransferId);
            if (record != null && string.Equals(record.SenderFingerprint, Fingerprint, StringComparison.OrdinalIgnoreCase)
                && record.ManifestHash == manifestHash)
            {
                await ResumeAsync(record, serviceToken).ConfigureAwait(false);
                return;
            }

            Log.Info($"Resume of {offer.TransferId} from {PeerName} not possible; asking again");
        }

        await HandleNewOfferAsync(offer, entries, manifestHash, serviceToken).ConfigureAwait(false);
    }

    private async Task HandleNewOfferAsync(OfferMessage offer, List<IncomingEntry> entries, string manifestHash, CancellationToken serviceToken)
    {
        if (!_service.TryReservePrompt(Fingerprint))
        {
            await RespondAsync(new OfferResponseMessage { Accepted = false, Reason = Reasons.Busy }, serviceToken).ConfigureAwait(false);
            return;
        }

        var policy = _service.Policy();
        IncomingRequest request;
        TransferSession session;
        IncomingDecision decision;
        Task<Frame> pendingRead;
        try
        {
            session = _service.CreateIncomingSession(offer.TransferId, _connection);
            _session = session;
            var rootNames = entries.Select(e => e.Segments[0]).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            session.SetDescription(rootNames);
            var files = entries.Where(e => !e.IsDirectory).ToList();
            session.SetTotals(files.Count, files.Sum(f => f.Size), 0, 0, 0);

            request = new IncomingRequest
            {
                TransferId = offer.TransferId,
                SenderName = PeerName,
                SenderId = _connection.RemoteHello.DeviceId,
                SenderFingerprint = Fingerprint,
                SenderAddress = _connection.RemoteEndPoint.Address.ToString(),
                FileCount = files.Count,
                FolderCount = entries.Count(e => e.IsDirectory),
                TotalBytes = files.Sum(f => f.Size),
                Items = SummarizeItems(entries),
                DefaultFolder = policy.DefaultFolder,
            };

            // Watch the connection while the user decides: the sender may cancel or disappear.
            pendingRead = _connection.Channel.ReadAsync(session.CancellationToken);

            if (policy.TrustedFingerprints.Contains(Fingerprint))
            {
                Log.Info($"Auto-accepting {session.Title} from trusted device {PeerName}");
                decision = IncomingDecision.Accept(policy.DefaultFolder);
            }
            else
            {
                using var decisionCts = CancellationTokenSource.CreateLinkedTokenSource(session.CancellationToken, serviceToken);
                decisionCts.CancelAfter(policy.ApprovalTimeout);
                var decisionTask = _service.Handler.RequestApprovalAsync(request, session, decisionCts.Token);
                var first = await Task.WhenAny(decisionTask, pendingRead).ConfigureAwait(false);
                if (first == pendingRead)
                {
                    decisionCts.Cancel();
                    _ = decisionTask.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
                    SenderWithdrew(session, pendingRead);
                    return;
                }

                try
                {
                    decision = await decisionTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (session.CancellationToken.IsCancellationRequested || serviceToken.IsCancellationRequested)
                {
                    decision = IncomingDecision.Decline();
                }
                catch (OperationCanceledException)
                {
                    Log.Info($"Request from {PeerName} expired without an answer");
                    await RespondAsync(new OfferResponseMessage { Accepted = false, Reason = Reasons.TimedOut }, serviceToken).ConfigureAwait(false);
                    session.SetState(TransferState.Declined,
                        new TransferError(TransferErrorKind.TimedOut, $"The request from {PeerName} expired because nobody answered it."));
                    return;
                }
            }
        }
        finally
        {
            _service.ReleasePrompt(Fingerprint);
        }

        if (!decision.Accepted)
        {
            await RespondAsync(new OfferResponseMessage { Accepted = false, Reason = Reasons.Declined }, serviceToken).ConfigureAwait(false);
            session.SetState(TransferState.Declined, new TransferError(TransferErrorKind.Declined, $"You declined the files from {PeerName}."));
            return;
        }

        var destination = decision.DestinationFolder ?? policy.DefaultFolder;
        session.DestinationFolder = destination;
        if (!await PrepareDestinationAsync(session, destination, serviceToken).ConfigureAwait(false)) return;

        var plan = BuildPlan(destination, entries, offer.TransferId);
        if (plan.Conflicts.Count > 0)
        {
            IReadOnlyList<ConflictAction> actions;
            using (var conflictCts = CancellationTokenSource.CreateLinkedTokenSource(session.CancellationToken, serviceToken))
            {
                var conflictTask = _service.Handler.ResolveConflictsAsync(request, plan.Conflicts, conflictCts.Token);
                var first = await Task.WhenAny(conflictTask, pendingRead).ConfigureAwait(false);
                if (first == pendingRead)
                {
                    conflictCts.Cancel();
                    _ = conflictTask.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
                    SenderWithdrew(session, pendingRead);
                    return;
                }

                try
                {
                    actions = await conflictTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    await RespondAsync(new OfferResponseMessage { Accepted = false, Reason = Reasons.Declined }, serviceToken).ConfigureAwait(false);
                    session.SetState(TransferState.Cancelled, new TransferError(TransferErrorKind.CancelledByUser, "You cancelled the transfer."));
                    return;
                }
            }

            ApplyConflictActions(plan, actions);
        }

        var required = plan.Files.Sum(f => f.Size);
        if (!await CheckFreeSpaceAsync(session, destination, required, serviceToken).ConfigureAwait(false)) return;

        try
        {
            foreach (var directory in plan.Directories) Directory.CreateDirectory(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await FailLocallyAsync(session, ErrorTranslator.FromLocalFileException(ex, "Creating folders"), sendResponse: true, serviceToken).ConfigureAwait(false);
            return;
        }

        var record = new ResumeRecord
        {
            TransferId = offer.TransferId,
            SenderFingerprint = Fingerprint,
            SenderId = _connection.RemoteHello.DeviceId,
            SenderName = PeerName,
            DestinationFolder = destination,
            ManifestHash = manifestHash,
            SkippedCount = plan.SkippedCount,
            RootNames = session.RootNames.ToList(),
            SavedRootPaths = plan.RootPaths,
            Files = plan.Files,
        };
        _service.ResumeStore.Save(record);
        session.SavedRootPaths = record.SavedRootPaths;

        await RespondAsync(new OfferResponseMessage
        {
            Accepted = true,
            Plan = plan.Files.Select(f => new PlanItem { Index = f.Index }).ToList(),
        }, serviceToken).ConfigureAwait(false);

        session.SetTotals(plan.Files.Count, required, 0, 0, plan.SkippedCount);
        session.SetState(TransferState.Transferring);
        Log.Info($"Receiving {session.Title} from {PeerName}: {plan.Files.Count} files, {required} bytes into {destination}");
        await ReceiveAsync(session, record, pendingRead, serviceToken).ConfigureAwait(false);
    }

    private async Task ResumeAsync(ResumeRecord record, CancellationToken serviceToken)
    {
        var session = _service.CreateIncomingSession(record.TransferId, _connection, reuseInterrupted: true);
        _session = session;
        session.SetDescription(record.RootNames);
        session.DestinationFolder = record.DestinationFolder;
        session.SavedRootPaths = record.SavedRootPaths;

        if (!Directory.Exists(record.DestinationFolder))
        {
            await FailLocallyAsync(session, new TransferError(TransferErrorKind.DestinationUnavailable,
                "The folder where files are being saved is no longer available. Check that the drive is connected.",
                record.DestinationFolder), sendResponse: true, serviceToken).ConfigureAwait(false);
            return;
        }

        var plan = new List<PlanItem>();
        long transferred = 0, remaining = 0;
        foreach (var file in record.Files)
        {
            file.Failed = false;
            long offset = 0;
            if (!file.Done)
            {
                try
                {
                    var part = new FileInfo(file.PartPath);
                    if (part.Exists) offset = part.Length <= file.Size ? part.Length : 0;
                }
                catch
                {
                    offset = 0;
                }
            }

            plan.Add(new PlanItem { Index = file.Index, Offset = offset, Done = file.Done });
            transferred += file.Done ? file.Size : offset;
            if (!file.Done) remaining += file.Size - offset;
        }

        if (!await CheckFreeSpaceAsync(session, record.DestinationFolder, remaining, serviceToken).ConfigureAwait(false)) return;

        await RespondAsync(new OfferResponseMessage { Accepted = true, Plan = plan }, serviceToken).ConfigureAwait(false);
        session.SetTotals(record.Files.Count, record.Files.Sum(f => f.Size), record.Files.Count(f => f.Done), transferred, record.SkippedCount);
        session.SetDiscardAction(null);
        session.SetState(TransferState.Transferring);
        Log.Info($"Resuming {record.TransferId} from {PeerName}: {transferred} bytes already here");
        await ReceiveAsync(session, record, null, serviceToken).ConfigureAwait(false);
    }

    private async Task ReceiveAsync(TransferSession session, ResumeRecord record, Task<Frame>? pendingRead, CancellationToken serviceToken)
    {
        var token = session.CancellationToken;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, serviceToken);
        var files = record.Files.ToDictionary(f => f.Index);
        var retried = new HashSet<int>();
        ActiveFile? current = null;
        var lastSave = Environment.TickCount64;

        try
        {
            while (true)
            {
                Frame frame;
                if (pendingRead != null)
                {
                    frame = await pendingRead.ConfigureAwait(false);
                    pendingRead = null;
                }
                else
                {
                    frame = await _connection.Channel.ReadAsync(IdleTimeout, linked.Token).ConfigureAwait(false);
                }

                switch (frame.Type)
                {
                    case FrameType.FileHeader:
                    {
                        var header = FrameChannel.Parse<FileHeaderMessage>(frame);
                        if (current != null || !files.TryGetValue(header.Index, out var file) || file.Done || header.Offset < 0 || header.Offset > file.Size)
                            throw new ProtocolException("Unexpected file header.");
                        current = await OpenPartAsync(file, header.Offset, linked.Token).ConfigureAwait(false);
                        session.SetCurrentFile(file.RelativePath);
                        break;
                    }

                    case FrameType.FileData:
                    {
                        if (current == null) throw new ProtocolException("Data without a file.");
                        var payload = frame.Payload;
                        if (current.Written + payload.Length > current.File.Size) throw new ProtocolException("More data than announced.");
                        current.Hash.AppendData(payload.Span);
                        await current.Writer.WriteAsync(payload, linked.Token).ConfigureAwait(false);
                        current.Written += payload.Length;
                        session.AddTransferred(payload.Length);
                        break;
                    }

                    case FrameType.FileFooter:
                    {
                        var footer = FrameChannel.Parse<FileFooterMessage>(frame);
                        if (current == null || footer.Index != current.File.Index) throw new ProtocolException("Unexpected file footer.");
                        var active = current;
                        current = null;
                        var result = await FinishFileAsync(session, active, footer, retried).ConfigureAwait(false);
                        await _connection.Channel.SendAsync(FrameType.FileResult, result, linked.Token).ConfigureAwait(false);
                        if (Environment.TickCount64 - lastSave > SaveInterval.TotalMilliseconds)
                        {
                            _service.ResumeStore.Save(record);
                            lastSave = Environment.TickCount64;
                        }

                        break;
                    }

                    case FrameType.SourceFileError:
                    {
                        var error = FrameChannel.Parse<SourceFileErrorMessage>(frame);
                        if (!files.TryGetValue(error.Index, out var file) || file.Done) throw new ProtocolException("Unexpected file error.");
                        if (current != null && current.File.Index == error.Index)
                        {
                            session.AddTransferred(-current.Written);
                            await current.DisposeAsync().ConfigureAwait(false);
                            TryDelete(file.PartPath);
                            current = null;
                        }

                        file.Failed = true;
                        session.FileFailed(file.RelativePath, $"Couldn't be read on {PeerName}: {Truncate(error.Error, 200)}");
                        break;
                    }

                    case FrameType.Done:
                    {
                        if (current != null) throw new ProtocolException("Transfer ended in the middle of a file.");
                        var snapshot = session.GetSnapshot();
                        await _connection.Channel.SendAsync(FrameType.Result, new ResultMessage
                        {
                            Completed = snapshot.CompletedFiles,
                            Failed = snapshot.FailedFiles,
                            Skipped = snapshot.SkippedFiles,
                        }, linked.Token).ConfigureAwait(false);

                        session.SavedRootPaths = ComputeSavedRootPaths(record);
                        _service.ResumeStore.Delete(record, deleteParts: true);
                        session.SetCurrentFile(null);
                        session.SetState(snapshot.FailedFiles > 0 ? TransferState.CompletedWithErrors : TransferState.Completed);
                        Log.Info($"Received {session.Title} from {PeerName}: {snapshot.CompletedFiles} ok, {snapshot.FailedFiles} failed, {snapshot.SkippedFiles} skipped");
                        return;
                    }

                    case FrameType.Cancel:
                    {
                        var cancel = FrameChannel.Parse<CancelMessage>(frame);
                        if (current != null) await current.DisposeAsync().ConfigureAwait(false);
                        current = null;
                        _service.ResumeStore.Delete(record, deleteParts: true);
                        session.SavedRootPaths = ComputeSavedRootPaths(record);
                        session.SetState(TransferState.Cancelled, ErrorTranslator.FromRemoteReason(cancel.Reason, PeerName, cancel.Detail));
                        return;
                    }

                    default:
                        throw new ProtocolException($"Unexpected {frame.Type} from sender.");
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (current != null) await current.DisposeAsync().ConfigureAwait(false);
            await _connection.SendCancelAndCloseAsync(_service.CancelReason).ConfigureAwait(false);
            _service.ResumeStore.Delete(record, deleteParts: true);
            session.SavedRootPaths = ComputeSavedRootPaths(record);
            session.SetState(TransferState.Cancelled, new TransferError(TransferErrorKind.CancelledByUser, "You cancelled the transfer."));
        }
        catch (LocalFileException ex)
        {
            if (current != null) await current.DisposeAsync().ConfigureAwait(false);
            _service.ResumeStore.Save(record);
            var error = ErrorTranslator.FromLocalFileException(ex.InnerException ?? ex, ex.Message);
            Log.Warn($"Receiving from {PeerName} failed locally: {error.Details}");
            await _connection.SendCancelAndCloseAsync(ErrorTranslator.ReasonFor(error), error.Message).ConfigureAwait(false);
            session.SetState(TransferState.Failed, error);
        }
        catch (ProtocolException ex)
        {
            if (current != null) await current.DisposeAsync().ConfigureAwait(false);
            Log.Warn($"Protocol error from {PeerName}: {ex.Message}");
            await _connection.SendCancelAndCloseAsync(Reasons.Protocol, ex.Message).ConfigureAwait(false);
            _service.ResumeStore.Delete(record, deleteParts: true);
            session.SetState(TransferState.Failed, ErrorTranslator.FromException(ex, PeerName));
        }
        catch (Exception ex) when (serviceToken.IsCancellationRequested)
        {
            // Beam is closing: keep partial files so the transfer can resume after a restart.
            if (current != null) await current.DisposeAsync().ConfigureAwait(false);
            _service.ResumeStore.Save(record);
            await _connection.SendCancelAndCloseAsync(Reasons.Shutdown).ConfigureAwait(false);
            session.SetState(TransferState.Failed, new TransferError(TransferErrorKind.ConnectionLost, "Beam was closed before the transfer finished.", ex.Message));
        }
        catch (Exception ex)
        {
            if (current != null) await current.DisposeAsync().ConfigureAwait(false);
            _service.ResumeStore.Save(record);
            var error = ErrorTranslator.FromException(ex, PeerName);
            Log.Warn($"Connection from {PeerName} lost during {record.TransferId}: {error.Details}");
            session.SetCurrentFile(null);
            session.SetDiscardAction(() =>
            {
                _service.ResumeStore.Delete(record, deleteParts: true);
                session.SetState(TransferState.Cancelled, new TransferError(TransferErrorKind.CancelledByUser, "You cancelled the transfer. Partial files were removed."));
            });
            session.SetState(TransferState.Interrupted, new TransferError(TransferErrorKind.ConnectionLost,
                $"Connection to {PeerName} was lost. The transfer will continue if {PeerName} reconnects.", error.Details));
        }
    }

    private async Task<FileResultMessage> FinishFileAsync(TransferSession session, ActiveFile active, FileFooterMessage footer, HashSet<int> retried)
    {
        var file = active.File;
        var hash = Convert.ToHexString(active.Hash.GetHashAndReset()).ToLowerInvariant();
        var written = active.Written;
        try
        {
            await active.Writer.CompleteAsync().ConfigureAwait(false);
        }
        finally
        {
            await active.DisposeAsync().ConfigureAwait(false);
        }

        var intact = footer.Length == file.Size && written == file.Size && string.Equals(hash, footer.Sha256, StringComparison.OrdinalIgnoreCase);
        if (!intact)
        {
            TryDelete(file.PartPath);
            session.AddTransferred(-written);
            Log.Warn($"Verification failed for {file.RelativePath} from {PeerName} (got {written} bytes, hash {hash}, expected {footer.Sha256})");
            if (retried.Add(file.Index)) return new FileResultMessage { Index = file.Index, Ok = false, Retry = true };

            file.Failed = true;
            const string reason = "The file was damaged in transit and couldn't be verified.";
            session.FileFailed(file.RelativePath, reason);
            return new FileResultMessage { Index = file.Index, Ok = false, Error = reason };
        }

        try
        {
            file.TargetPath = MoveIntoPlace(file);
            if (file.ModifiedUnixMs > 0)
            {
                try
                {
                    File.SetLastWriteTimeUtc(file.TargetPath, DateTimeOffset.FromUnixTimeMilliseconds(file.ModifiedUnixMs).UtcDateTime);
                }
                catch
                {
                    // Timestamps are cosmetic.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && !ErrorTranslator.IsDiskFull(ex))
        {
            TryDelete(file.PartPath);
            file.Failed = true;
            var reason = ex is UnauthorizedAccessException
                ? "Couldn't be saved: the existing file is read-only or protected."
                : "Couldn't be saved: the existing file may be open in another program.";
            Log.Warn($"Could not move {file.PartPath} to {file.TargetPath}: {ex.Message}");
            session.FileFailed(file.RelativePath, reason);
            return new FileResultMessage { Index = file.Index, Ok = false, Error = reason };
        }

        file.Done = true;
        session.FileCompleted();
        return new FileResultMessage { Index = file.Index, Ok = true };
    }

    private static string MoveIntoPlace(ResumeFile file)
    {
        var target = file.TargetPath;
        if (file.Replace && File.Exists(target))
        {
            File.Move(file.PartPath, target, overwrite: true);
            return target;
        }

        for (var attempt = 0; attempt < 5; attempt++)
        {
            if (FileNaming.Exists(target))
            {
                // Something appeared with this name meanwhile: never overwrite it.
                var directory = Path.GetDirectoryName(target)!;
                target = Path.Combine(directory, FileNaming.MakeUnique(Path.GetFileName(target), n => FileNaming.Exists(Path.Combine(directory, n))));
            }

            try
            {
                File.Move(file.PartPath, target, overwrite: false);
                return target;
            }
            catch (IOException) when (FileNaming.Exists(target))
            {
                // Lost a race with another program creating the same name; pick another.
            }
        }

        throw new IOException($"Could not find a free name for {file.TargetPath}");
    }

    private async Task<ActiveFile> OpenPartAsync(ResumeFile file, long offset, CancellationToken token)
    {
        FileStream? stream = null;
        var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file.PartPath)!);
            stream = new FileStream(file.PartPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, 1, FileOptions.SequentialScan);
            if (offset > stream.Length) throw new ProtocolException("Resume offset beyond the partial file.");
            stream.SetLength(offset);
            if (offset > 0)
            {
                var buffer = new byte[1024 * 1024];
                stream.Position = 0;
                var remaining = offset;
                while (remaining > 0)
                {
                    var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), token).ConfigureAwait(false);
                    if (read == 0) throw new IOException("Partial file is shorter than expected.");
                    hash.AppendData(buffer, 0, read);
                    remaining -= read;
                }
            }

            stream.Position = offset;
            return new ActiveFile(file, new PipelinedFileWriter(stream, $"Writing {file.PartPath}"), hash) { Written = offset };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (stream != null) await stream.DisposeAsync().ConfigureAwait(false);
            hash.Dispose();
            throw new LocalFileException($"Opening {file.PartPath}", ex);
        }
        catch
        {
            if (stream != null) await stream.DisposeAsync().ConfigureAwait(false);
            hash.Dispose();
            throw;
        }
    }

    private async Task<bool> PrepareDestinationAsync(TransferSession session, string destination, CancellationToken token)
    {
        try
        {
            Directory.CreateDirectory(destination);
            var probe = Path.Combine(destination, $".beam-write-test-{Guid.NewGuid():N}");
            await File.WriteAllBytesAsync(probe, Array.Empty<byte>(), token).ConfigureAwait(false);
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            var error = ErrorTranslator.FromLocalFileException(ex is ArgumentException or NotSupportedException ? new DirectoryNotFoundException(ex.Message) : ex, destination);
            await FailLocallyAsync(session, error, sendResponse: true, token).ConfigureAwait(false);
            return false;
        }
    }

    private async Task<bool> CheckFreeSpaceAsync(TransferSession session, string destination, long required, CancellationToken token)
    {
        var available = DiskSpace.GetAvailableBytes(destination);
        if (available < 0 || available >= required + FreeSpaceMargin) return true;
        var error = new TransferError(TransferErrorKind.NotEnoughSpace,
            $"There isn't enough free space to receive these files. {Util.Format.Bytes(required)} is needed but only {Util.Format.Bytes(available)} is free.",
            destination);
        await FailLocallyAsync(session, error, sendResponse: true, token).ConfigureAwait(false);
        return false;
    }

    private async Task FailLocallyAsync(TransferSession session, TransferError error, bool sendResponse, CancellationToken token)
    {
        Log.Warn($"Cannot receive from {PeerName}: {error.Message} ({error.Details})");
        if (sendResponse)
            await RespondAsync(new OfferResponseMessage { Accepted = false, Reason = ErrorTranslator.ReasonFor(error) }, token).ConfigureAwait(false);
        session.SetState(TransferState.Failed, error);
    }

    private void SenderWithdrew(TransferSession session, Task<Frame> pendingRead)
    {
        var error = new TransferError(TransferErrorKind.CancelledByRemote, $"{PeerName} cancelled the request.");
        if (pendingRead.IsCompletedSuccessfully && pendingRead.Result.Type == FrameType.Cancel)
        {
            try
            {
                var cancel = FrameChannel.Parse<CancelMessage>(pendingRead.Result);
                if (cancel.Reason == Reasons.Shutdown) error = ErrorTranslator.FromRemoteReason(cancel.Reason, PeerName);
            }
            catch (ProtocolException)
            {
                // keep the generic message
            }
        }

        session.SetState(TransferState.Cancelled, error);
    }

    private async Task RespondAsync(OfferResponseMessage response, CancellationToken token)
    {
        try
        {
            await _connection.Channel.SendAsync(FrameType.OfferResponse, response, token).ConfigureAwait(false);
        }
        catch (Exception ex) when (!response.Accepted)
        {
            // The sender is gone; nothing else to do for a refusal.
            Log.Info($"Could not deliver refusal to {PeerName}: {ex.Message}");
        }
    }

    private static List<IncomingEntry>? ParseEntries(OfferMessage offer)
    {
        if (string.IsNullOrEmpty(offer.TransferId) || offer.TransferId.Length > 64 || !offer.TransferId.All(char.IsAsciiLetterOrDigit))
            return null;
        if (offer.Entries.Count == 0 || offer.Entries.Count > 2_000_000) return null;

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
                if (File.Exists(full) || plan.PlannedPaths.Contains(full))
                {
                    name = FileNaming.MakeUnique(name, n =>
                    {
                        var candidate = SafePath.Combine(destination, parent.Append(n));
                        return File.Exists(candidate) || plan.PlannedPaths.Contains(candidate);
                    }, isDirectory: true);
                    full = SafePath.Combine(destination, parent.Append(name));
                }

                actual = parent.Append(name).ToArray();
                directoryMap[key] = actual;
                if (!plan.Directories.Contains(full, PathComparer)) plan.Directories.Add(full);
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

    private static void ApplyConflictActions(Plan plan, IReadOnlyList<ConflictAction> actions)
    {
        if (actions.Count != plan.Conflicts.Count) throw new InvalidOperationException("One action is required per conflict.");
        var byIndex = plan.Files.ToDictionary(f => f.Index);
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
                    var rootIndex = plan.RootPaths.FindIndex(p => PathComparer.Equals(p, oldTarget));
                    if (rootIndex >= 0) plan.RootPaths[rootIndex] = file.TargetPath;
                    break;
                case ConflictAction.Skip:
                    plan.Files.Remove(file);
                    plan.SkippedCount++;
                    plan.RootPaths.RemoveAll(p => PathComparer.Equals(p, file.TargetPath));
                    break;
            }
        }
    }

    private static List<string> ComputeSavedRootPaths(ResumeRecord record)
    {
        // Files may have been renamed at the last moment; prefer their final paths.
        var result = new List<string>();
        foreach (var path in record.SavedRootPaths)
        {
            var file = record.Files.FirstOrDefault(f => PathComparer.Equals(f.TargetPath, path) || PathComparer.Equals(f.PartPath, path));
            if (file != null && !file.Done) continue;
            if (FileNaming.Exists(path)) result.Add(path);
        }

        foreach (var file in record.Files.Where(f => f.Done && !f.RelativePath.Contains('/')))
        {
            if (!result.Contains(file.TargetPath, PathComparer)) result.Add(file.TargetPath);
        }

        return result;
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

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

    private sealed class ActiveFile : IAsyncDisposable
    {
        public ActiveFile(ResumeFile file, PipelinedFileWriter writer, IncrementalHash hash)
        {
            File = file;
            Writer = writer;
            Hash = hash;
        }

        public ResumeFile File { get; }

        public PipelinedFileWriter Writer { get; }

        public IncrementalHash Hash { get; }

        public long Written { get; set; }

        public async ValueTask DisposeAsync()
        {
            await Writer.DisposeAsync().ConfigureAwait(false);
            Hash.Dispose();
        }
    }
}

internal static class DiskSpace
{
    /// <summary>Free bytes available to the user on the drive holding <paramref name="path"/>, or -1 if unknown.</summary>
    public static long GetAvailableBytes(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal)) return -1;
            return new DriveInfo(root).AvailableFreeSpace;
        }
        catch
        {
            return -1;
        }
    }
}
