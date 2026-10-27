using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Beam.Core.Diagnostics;
using Beam.Core.Files;
using Beam.Core.Storage;
using Beam.Core.Transfer;
using Beam.Core.Localization;

namespace Beam.Core.Phone;

// Files the phone sends to this computer: the offer, each file's upload, and the upload's state.
public sealed partial class PhoneLinkServer : IAsyncDisposable
{
    private async Task HandleOfferAsync(NetworkStream stream, HttpRequest request, string remote, CancellationToken token)
    {
        if (request.ContentLength > 2 * 1024 * 1024)
        {
            await MiniHttp.WriteTextAsync(stream, 413, "Too many files at once.", token).ConfigureAwait(false);
            return;
        }

        PhoneOffer? offer;
        try
        {
            var body = new byte[request.ContentLength];
            await request.Body.ReadExactlyAsync(body, token).ConfigureAwait(false);
            offer = JsonSerializer.Deserialize(body, PhoneJson.Default.PhoneOffer);
        }
        catch (JsonException)
        {
            offer = null;
        }

        if (offer == null || offer.Files.Count == 0 || offer.Files.Count > MaxFilesPerOffer || offer.Files.Any(f => f.Size < 0 || string.IsNullOrWhiteSpace(f.Name)))
        {
            await MiniHttp.WriteTextAsync(stream, 400, "Invalid list of files.", token).ConfigureAwait(false);
            return;
        }

        var files = offer.Files.Select(f => new PhoneUploadFile(SafePath.SanitizeSegment(Path.GetFileName(f.Name.Replace('\\', '/'))), f.Size)).ToList();
        var visitor = LastVisitor ?? L.T("Phone");
        var id = Guid.NewGuid().ToString("N");
        var policy = _policy();
        var session = new TransferSession(id, TransferDirection.Receive, PhonePeerId, visitor, "");
        session.SetDescription(files.Select(f => f.Name).ToList());
        session.SetTotals(files.Count, files.Sum(f => f.Size), 0, 0, 0);
        session.SetState(TransferState.AwaitingDecision);
        _transfers.RegisterExternal(session);

        var incoming = new IncomingRequest
        {
            TransferId = id,
            SenderName = visitor,
            SenderId = PhonePeerId,
            SenderFingerprint = "",
            SenderAddress = remote,
            FileCount = files.Count,
            FolderCount = 0,
            TotalBytes = files.Sum(f => f.Size),
            Items = files.Select(f => new IncomingItem(f.Name, false, f.Size, 1)).ToList(),
            DefaultFolder = policy.DefaultFolder,
        };

        IncomingDecision decision;
        using (var cts = CancellationTokenSource.CreateLinkedTokenSource(token, session.CancellationToken))
        {
            cts.CancelAfter(policy.ApprovalTimeout);
            try
            {
                decision = await _handler.RequestApprovalAsync(incoming, session, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                decision = IncomingDecision.Decline();
            }
        }

        if (!decision.Accepted)
        {
            session.SetState(TransferState.Declined, new TransferError(TransferErrorKind.Declined, L.T("You declined the files.")));
            await WriteJsonAsync(stream, new PhoneReply { Ok = false, Message = $"{_deviceName()} declined the files." }, PhoneJson.Default.PhoneReply, token).ConfigureAwait(false);
            return;
        }

        var folder = decision.DestinationFolder ?? policy.DefaultFolder;
        session.DestinationFolder = folder;
        session.SetState(TransferState.Transferring);
        var upload = new PhoneUpload(id, session, folder, files);
        _uploads[id] = upload;
        session.CancellationToken.Register(() => Task.Run(() =>
        {
            if (_uploads.TryRemove(id, out var cancelled)) cancelled.Finish(L.T("You cancelled the transfer."), cancelled: true);
        }));
        Activity?.Invoke();
        await WriteJsonAsync(stream, new PhoneReply { Ok = true, OfferId = id }, PhoneJson.Default.PhoneReply, token).ConfigureAwait(false);
    }

    private async Task HandleUploadAsync(NetworkStream stream, HttpRequest request, string offerId, int index, CancellationToken token)
    {
        if (!_uploads.TryGetValue(offerId, out var upload) || index < 0 || index >= upload.Files.Count)
        {
            await MiniHttp.WriteTextAsync(stream, 404, "This transfer is no longer active.", token).ConfigureAwait(false);
            return;
        }

        var file = upload.Files[index];
        if (!upload.TryBegin(index))
        {
            await MiniHttp.WriteTextAsync(stream, 409, "This file was already sent.", token).ConfigureAwait(false);
            return;
        }

        if (request.ContentLength != file.Size)
        {
            upload.Fail(index, L.T("The phone sent a different amount of data than announced."));
            if (upload.IsComplete) _uploads.TryRemove(offerId, out _);
            await MiniHttp.WriteTextAsync(stream, 400, "Size mismatch.", token).ConfigureAwait(false);
            return;
        }

        string? error = null;
        try
        {
            await upload.ReceiveAsync(index, request.Body, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (upload.Session.CancellationToken.IsCancellationRequested)
        {
            error = L.T("The transfer was cancelled on the computer.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            error = ex is EndOfStreamException
                ? L.T("The phone stopped sending before the file was complete.")
                : ErrorTranslator.FromLocalFileException(ex, "Saving a file from the phone").Message;
            upload.Fail(index, error);
        }

        if (upload.IsComplete) _uploads.TryRemove(offerId, out _);
        Activity?.Invoke();
        await WriteJsonAsync(stream, new PhoneReply { Ok = error == null, Message = error }, PhoneJson.Default.PhoneReply, token).ConfigureAwait(false);
    }

    private sealed record PhoneUploadFile(string Name, long Size);

    /// <summary>An accepted set of files from a phone, uploaded one request per file.</summary>
    private sealed class PhoneUpload
    {
        private readonly object _gate = new();
        private readonly int[] _status; // 0 = waiting, 1 = receiving, 2 = done, 3 = failed

        public PhoneUpload(string id, TransferSession session, string folder, List<PhoneUploadFile> files)
        {
            Id = id;
            Session = session;
            Folder = folder;
            Files = files;
            _status = new int[files.Count];
        }

        public string Id { get; }

        public TransferSession Session { get; }

        public string Folder { get; }

        public List<PhoneUploadFile> Files { get; }

        public DateTime LastActivity { get; private set; } = DateTime.UtcNow;

        public bool IsComplete
        {
            get
            {
                lock (_gate) return _status.All(s => s >= 2);
            }
        }

        public bool TryBegin(int index)
        {
            lock (_gate)
            {
                if (_status[index] != 0 || Session.IsFinished) return false;
                _status[index] = 1;
                LastActivity = DateTime.UtcNow;
                return true;
            }
        }

        /// <summary>Streams one file into a partial file, then moves it to a free name in the destination folder.</summary>
        public async Task ReceiveAsync(int index, Stream body, CancellationToken token)
        {
            var file = Files[index];
            Directory.CreateDirectory(Folder);
            var part = System.IO.Path.Combine(Folder, $".{Id[..8]}-{index}.beampart");
            if (!SafePath.IsInside(Folder, part)) throw new InvalidOperationException("Invalid destination.");
            Session.SetCurrentFile(file.Name);
            var received = 0L;
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(token, Session.CancellationToken);
                await using (var output = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1))
                {
                    var buffer = new byte[128 * 1024];
                    int read;
                    while ((read = await body.ReadAsync(buffer, cts.Token).ConfigureAwait(false)) > 0)
                    {
                        await output.WriteAsync(buffer.AsMemory(0, read), cts.Token).ConfigureAwait(false);
                        received += read;
                        Session.AddTransferred(read);
                        LastActivity = DateTime.UtcNow;
                    }
                }

                if (received != file.Size) throw new EndOfStreamException("The phone stopped sending.");
                var target = SafePath.Combine(Folder, new[] { file.Name });
                for (var attempt = 0; ; attempt++)
                {
                    if (FileNaming.Exists(target))
                        target = System.IO.Path.Combine(Folder, FileNaming.MakeUnique(file.Name, n => FileNaming.Exists(System.IO.Path.Combine(Folder, n))));
                    try
                    {
                        File.Move(part, target, overwrite: false);
                        break;
                    }
                    catch (IOException) when (attempt < 5 && FileNaming.Exists(target))
                    {
                        // another file took the name meanwhile
                    }
                }

                lock (_gate) _status[index] = 2;
                Session.FileCompleted();
                lock (_gate) Session.SavedRootPaths = Session.SavedRootPaths.Append(target).ToList();
                FinishIfDone();
            }
            catch (OperationCanceledException) when (Session.CancellationToken.IsCancellationRequested)
            {
                Session.AddTransferred(-received);
                FileCleanup.TryDelete(part);
                Finish(L.T("You cancelled the transfer."), cancelled: true);
                throw; // the phone is told it was cancelled, not that the file arrived
            }
            catch
            {
                Session.AddTransferred(-received);
                FileCleanup.TryDelete(part);
                throw;
            }
        }

        public void Fail(int index, string reason)
        {
            lock (_gate)
            {
                if (_status[index] >= 2) return;
                _status[index] = 3;
            }

            Session.FileFailed(Files[index].Name, reason);
            FinishIfDone();
        }

        /// <summary>Gives up on files that never arrived and ends the transfer.</summary>
        public void Finish(string reason, bool cancelled = false)
        {
            if (cancelled)
            {
                Session.SetState(TransferState.Cancelled, new TransferError(TransferErrorKind.CancelledByUser, reason));
                return;
            }

            List<int> missing;
            lock (_gate)
            {
                missing = Enumerable.Range(0, _status.Length).Where(i => _status[i] < 2).ToList();
                foreach (var i in missing) _status[i] = 3;
            }

            foreach (var i in missing) Session.FileFailed(Files[i].Name, reason);
            FinishIfDone();
        }

        private void FinishIfDone()
        {
            if (!IsComplete || Session.IsFinished) return;
            var snapshot = Session.GetSnapshot();
            Session.SetCurrentFile(null);
            if (snapshot.CompletedFiles == 0)
                Session.SetState(TransferState.Failed, new TransferError(TransferErrorKind.ConnectionLost, L.T("No files arrived from the phone.")));
            else
                Session.SetState(snapshot.FailedFiles > 0 ? TransferState.CompletedWithErrors : TransferState.Completed);
        }
    }
}
