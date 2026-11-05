using System.Net;
using System.Security.Cryptography;
using Beam.Core.Diagnostics;
using Beam.Core.Direct;
using Beam.Core.Discovery;
using Beam.Core.Files;
using Beam.Core.Localization;
using Beam.Core.Protocol;
using Beam.Core.Transfer;

namespace Beam.Core.Browse;

/// <summary>Something in the other device's shared folders.</summary>
public sealed record BrowseItem(string Root, string Path, string Name, bool IsFolder, long Size, DateTimeOffset Modified);

public sealed partial class BrowseService
{
    /// <summary>
    /// Starts browsing a trusted device: on the Wi-Fi both are on, or else over a direct link (set up like a Direct
    /// send's). The other device's user is asked (unless they always allow this device). Throws
    /// <see cref="TransferException"/> with a message for the user.
    /// </summary>
    public async Task<BrowseSession> OpenAsync(DeviceInfo device, CancellationToken cancellationToken)
    {
        var trusted = _settings.Current.TrustedDevices.FirstOrDefault(t => t.DeviceId == device.Id
                                                                           || string.Equals(t.Fingerprint, device.Fingerprint, StringComparison.OrdinalIgnoreCase));
        if (trusted == null)
            throw new TransferException(TransferErrorKind.Declined,
                L.T("Only trusted devices can browse each other. Add {0} as a trusted device first (Settings → Trusted devices).", device.Name));
        var session = new BrowseSession(this, device, trusted.Fingerprint);
        try
        {
            await session.StartAsync(cancellationToken).ConfigureAwait(false);
            return session;
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal TransferService Transfers => _transfers;

    internal DiscoveryService Discovery => _discovery;

    internal DirectManager Direct => _direct;

    internal string ReceiveFolder => _settings.Current.EffectiveReceiveFolder;
}

/// <summary>
/// Browsing one trusted device. Requests go over one connection; downloads and uploads over their own. A dropped
/// connection is made again by itself (the shared Wi-Fi when the device is on it, else the direct link) and carries on
/// where it was, without the other device asking again.
/// </summary>
public sealed class BrowseSession : IAsyncDisposable
{
    private readonly BrowseService _service;
    private readonly string _fingerprint;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _closed = new();
    private PeerConnection? _connection;
    private DirectLinkLease? _link;
    private string? _session;

    internal BrowseSession(BrowseService service, DeviceInfo device, string fingerprint)
    {
        _service = service;
        Device = device;
        _fingerprint = fingerprint;
        Name = device.Name;
    }

    public DeviceInfo Device { get; }

    /// <summary>The other device's name (as it calls itself).</summary>
    public string Name { get; private set; }

    /// <summary>Files may be added to its shared folders.</summary>
    public bool CanAdd { get; private set; }

    public IReadOnlyList<BrowseRoot> Roots { get; private set; } = Array.Empty<BrowseRoot>();

    /// <summary>How it is connected now: the shared Wi-Fi or a direct link.</summary>
    public TransferPath Path { get; private set; } = TransferPath.Unknown;

    /// <summary>The connection changed (reconnected, another way), or the other device ended the visit.</summary>
    public event Action? Changed;

    internal async Task StartAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _connection = await ConnectAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>A shared folder's contents: folders first, then files, by name.</summary>
    public async Task<IReadOnlyList<BrowseItem>> ListAsync(string root, string path, CancellationToken cancellationToken)
    {
        var response = await RequestAsync(new BrowseRequestMessage { Action = BrowseActions.List, Root = root, Path = path }, cancellationToken)
            .ConfigureAwait(false);
        return response.Entries
            .Select(e => new BrowseItem(root, path.Length == 0 ? e.Name : path + "/" + e.Name, e.Name, e.IsFolder, e.Size,
                DateTimeOffset.FromUnixTimeMilliseconds(e.Modified)))
            .OrderBy(e => e.IsFolder ? 0 : 1).ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>A small preview of a photo or video (JPEG), or null.</summary>
    public async Task<byte[]?> ThumbnailAsync(BrowseItem item, int max, CancellationToken cancellationToken)
    {
        var response = await RequestAsync(new BrowseRequestMessage { Action = BrowseActions.Thumbnail, Root = item.Root, Path = item.Path, Max = max },
            cancellationToken, failOk: true).ConfigureAwait(false);
        return response is { Ok: true, Image: { } image } ? Convert.FromBase64String(image) : null;
    }

    /// <summary>
    /// Downloads <paramref name="items"/> (folders with everything in them) into this device's received-files folder.
    /// Shown in Transfers like any transfer; a dropped connection resumes where it was; every file is checked.
    /// </summary>
    public TransferSession Download(IReadOnlyList<BrowseItem> items)
    {
        var transfer = new TransferSession(Guid.NewGuid().ToString("N"), TransferDirection.Receive, Device.Id, Name, _fingerprint);
        transfer.SetDescription(items.Select(i => i.Name).ToList());
        transfer.DestinationFolder = _service.ReceiveFolder;
        transfer.SetState(TransferState.Preparing);
        _service.Transfers.RegisterExternal(transfer);
        _ = Task.Run(() => DownloadAsync(transfer, items));
        return transfer;
    }

    /// <summary>Adds files from this device to a folder on the other one (when it allows adding files). Never replaces anything there.</summary>
    public TransferSession Upload(string root, string folder, IReadOnlyList<string> paths)
    {
        var transfer = new TransferSession(Guid.NewGuid().ToString("N"), TransferDirection.Send, Device.Id, Name, _fingerprint);
        transfer.SetDescription(paths.Select(p => System.IO.Path.GetFileName(p)).ToList());
        transfer.SetState(TransferState.Preparing);
        _service.Transfers.RegisterExternal(transfer);
        _ = Task.Run(() => UploadAsync(transfer, root, folder, paths));
        return transfer;
    }

    public async ValueTask DisposeAsync()
    {
        _closed.Cancel();
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_connection != null) await _connection.DisposeAsync().ConfigureAwait(false);
            _connection = null;
        }
        finally
        {
            _gate.Release();
        }

        if (_link != null) await _service.Direct.CloseLinkAsync(_link).ConfigureAwait(false);
        _link = null;
    }

    // ---------------------------------------------------------------- connecting

    /// <summary>
    /// A connection to the device, checked against its trusted certificate: on the shared Wi-Fi when it's there, else
    /// over the direct link. The visit is opened (or carried on, with the session given before).
    /// </summary>
    private async Task<PeerConnection> ConnectAsync(CancellationToken cancellationToken)
    {
        var transfers = _service.Transfers;
        var current = _service.Discovery.Find(Device.Id) ?? Device;
        var lan = current.Endpoints.Where(e => !transfers.IsDirectAddress(e.Address)).ToList();
        PeerConnection? connection = null;
        TransferException? failure = null;
        if (lan.Count > 0)
        {
            try
            {
                connection = await SecureTransport.ConnectAsync(transfers.Identity, transfers.CreateHello(ConnectionPurpose.Browse), lan, _fingerprint, Name,
                    cancellationToken).ConfigureAwait(false);
                Path = TransferPath.Network;
            }
            catch (TransferException ex) when (ex.Error.Kind == TransferErrorKind.ConnectFailed)
            {
                failure = ex;
            }
        }

        if (connection == null && _service.Direct.CanSendDirectlyTo(current))
        {
            _link ??= _service.Direct.OpenLink(current);
            var endpoint = await _service.Direct.ConnectLinkAsync(_link, cancellationToken).ConfigureAwait(false);
            connection = await SecureTransport.ConnectAsync(transfers.Identity, transfers.CreateHello(ConnectionPurpose.Browse), new[] { endpoint },
                _fingerprint, Name, cancellationToken).ConfigureAwait(false);
            Path = TransferPath.Direct;
        }

        if (connection == null)
            throw failure ?? new TransferException(TransferErrorKind.ConnectFailed,
                L.T("{0} isn't nearby. Both need to be on the same Wi-Fi, or close enough to connect directly.", Name));

        try
        {
            await connection.Channel.SendAsync(FrameType.BrowseRequest, new BrowseRequestMessage { Action = BrowseActions.Open, Session = _session },
                cancellationToken).ConfigureAwait(false);
            // The other device's user may be asked first.
            var response = await connection.Channel.ReadMessageAsync<BrowseResponseMessage>(FrameType.BrowseResponse,
                BrowseService.ApprovalTimeout + TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
            if (!response.Ok)
                throw new TransferException(response.Reason == BrowseReasons.TimedOut ? TransferErrorKind.TimedOut : TransferErrorKind.Declined,
                    response.Message ?? L.T("{0} didn't allow browsing.", Name));
            _session = response.Session;
            Name = string.IsNullOrWhiteSpace(response.Name) ? Name : response.Name;
            CanAdd = response.CanAdd;
            Roots = response.Roots;
            Log.Info($"Browse: browsing {Name} ({(Path == TransferPath.Direct ? "direct link" : "shared Wi-Fi")})");
            Changed?.Invoke();
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Lost: the next request reconnects (another way, if this one is gone).</summary>
    private async Task DropAsync(PeerConnection? connection)
    {
        if (connection != null) await connection.DisposeAsync().ConfigureAwait(false);
        if (Path == TransferPath.Direct && _link != null) _service.Direct.LinkLost(_link);
        Path = TransferPath.Unknown;
        Changed?.Invoke();
    }

    private static bool IsDropped(Exception ex) =>
        ex is IOException or EndOfStreamException or ObjectDisposedException or System.Security.Authentication.AuthenticationException
        || (ex is TransferException t && t.Error.Kind is TransferErrorKind.ConnectFailed or TransferErrorKind.ConnectionLost)
        || (ex is TimeoutException);

    private async Task<BrowseResponseMessage> RequestAsync(BrowseRequestMessage request, CancellationToken cancellationToken, bool failOk = false)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _closed.Token);
        var token = linked.Token;
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    _connection ??= await ConnectAsync(token).ConfigureAwait(false);
                    await _connection.Channel.SendAsync(FrameType.BrowseRequest, request, token).ConfigureAwait(false);
                    var response = await _connection.Channel.ReadMessageAsync<BrowseResponseMessage>(FrameType.BrowseResponse, TimeSpan.FromSeconds(60), token)
                        .ConfigureAwait(false);
                    if (!response.Ok && !failOk)
                        throw new TransferException(TransferErrorKind.Declined, response.Message ?? L.T("Something went wrong."));
                    return response;
                }
                catch (Exception ex) when (IsDropped(ex) && attempt < 2 && !token.IsCancellationRequested)
                {
                    Log.Info($"Browse: the connection to {Name} dropped ({ex.GetType().Name}); connecting again");
                    var dropped = _connection;
                    _connection = null;
                    await DropAsync(dropped).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    // ---------------------------------------------------------------- downloading and adding files

    private async Task DownloadAsync(TransferSession transfer, IReadOnlyList<BrowseItem> items)
    {
        var token = transfer.CancellationToken;
        PeerConnection? connection = null;
        try
        {
            // What the folders hold, as paths inside the download ("Camera/2026/a.jpg").
            var files = new List<(BrowseItem Item, string[] Target)>();
            foreach (var item in items) await CollectAsync(item, new[] { item.Name }, files, token).ConfigureAwait(false);
            transfer.SetTotals(files.Count, files.Sum(f => f.Item.Size), 0, 0, 0);
            transfer.SetState(TransferState.Transferring);
            transfer.SetPath(Path);
            var destination = _service.ReceiveFolder;
            Directory.CreateDirectory(destination);
            var roots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var saved = new List<string>();
            foreach (var (item, segments) in files)
            {
                token.ThrowIfCancellationRequested();
                // A top-level name already here gets a new one ("Camera (1)"), as with any received files.
                if (!roots.TryGetValue(segments[0], out var top))
                {
                    top = FileNaming.MakeUnique(SafePath.SanitizeSegment(segments[0]), n => FileNaming.Exists(System.IO.Path.Combine(destination, n)),
                        isDirectory: segments.Length > 1);
                    roots[segments[0]] = top;
                    saved.Add(System.IO.Path.Combine(destination, top));
                }

                var target = SafePath.Combine(destination, new[] { top }.Concat(segments.Skip(1).Select(SafePath.SanitizeSegment)));
                transfer.SetCurrentFile(string.Join('/', segments));
                connection = await DownloadFileAsync(transfer, item, target, connection, token).ConfigureAwait(false);
            }

            transfer.SavedRootPaths = saved;
            var snapshot = transfer.GetSnapshot();
            transfer.SetState(snapshot.FailedFiles > 0 ? TransferState.CompletedWithErrors : TransferState.Completed);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            transfer.SetState(TransferState.Cancelled, new TransferError(TransferErrorKind.CancelledByUser, L.T("You cancelled the transfer.")));
        }
        catch (Exception ex)
        {
            Log.Warn($"Browse: downloading from {Name} failed ({ex.GetType().Name}: {ex.Message})");
            transfer.SetState(TransferState.Failed, ex is TransferException te ? te.Error : ErrorTranslator.FromException(ex, Name));
        }
        finally
        {
            if (connection != null) await connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task CollectAsync(BrowseItem item, string[] target, List<(BrowseItem, string[])> files, CancellationToken token)
    {
        if (!item.IsFolder)
        {
            files.Add((item, target));
            return;
        }

        foreach (var child in await ListAsync(item.Root, item.Path, token).ConfigureAwait(false))
            await CollectAsync(child, target.Append(child.Name).ToArray(), files, token).ConfigureAwait(false);
    }

    /// <summary>One file into <paramref name="target"/> (a new name if taken), resuming its part file after a drop.</summary>
    private async Task<PeerConnection?> DownloadFileAsync(TransferSession transfer, BrowseItem item, string target, PeerConnection? connection,
        CancellationToken token)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
        var part = target + "." + transfer.Id[..8] + ".beam-part";
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                connection ??= await ConnectAsync(token).ConfigureAwait(false);
                transfer.SetPath(Path);
                var offset = File.Exists(part) ? new FileInfo(part).Length : 0;
                await connection.Channel.SendAsync(FrameType.BrowseRequest,
                    new BrowseRequestMessage { Action = BrowseActions.Read, Root = item.Root, Path = item.Path, Offset = offset }, token).ConfigureAwait(false);
                var start = await connection.Channel.ReadMessageAsync<BrowseResponseMessage>(FrameType.BrowseResponse, TimeSpan.FromSeconds(60), token)
                    .ConfigureAwait(false);
                if (!start.Ok)
                {
                    transfer.FileFailed(item.Name, start.Message ?? L.T("This file can't be opened."));
                    return connection;
                }

                if (offset > start.Size) offset = 0; // changed meanwhile: from the start
                await using (var file = new FileStream(part, offset == 0 ? FileMode.Create : FileMode.Open, FileAccess.Write, FileShare.None, 1,
                                 FileOptions.Asynchronous))
                {
                    file.Seek(offset, SeekOrigin.Begin);
                    var received = offset;
                    while (received < start.Size)
                    {
                        var frame = await connection.Channel.ReadAsync(TimeSpan.FromSeconds(60), token).ConfigureAwait(false);
                        if (frame.Type != FrameType.BrowseData || received + frame.Payload.Length > start.Size)
                            throw new ProtocolException("Unexpected data while downloading.");
                        await file.WriteAsync(frame.Payload, token).ConfigureAwait(false);
                        received += frame.Payload.Length;
                        transfer.AddTransferred(frame.Payload.Length);
                    }
                }

                var end = await connection.Channel.ReadMessageAsync<BrowseResponseMessage>(FrameType.BrowseResponse, TimeSpan.FromSeconds(60), token)
                    .ConfigureAwait(false);
                if (!end.Ok || !string.Equals(end.Hash, await HashAsync(part, token).ConfigureAwait(false), StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(part);
                    transfer.FileFailed(item.Name, L.T("The file arrived damaged."));
                    return connection;
                }

                var final = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(target)!,
                    FileNaming.MakeUnique(System.IO.Path.GetFileName(target), n => FileNaming.Exists(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(target)!, n))));
                File.Move(part, final, overwrite: false);
                try { File.SetLastWriteTimeUtc(final, item.Modified.UtcDateTime); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { /* the time is a nicety */ }
                transfer.FileCompleted();
                return connection;
            }
            catch (Exception ex) when (!(IsDropped(ex) && attempt < 30 && !token.IsCancellationRequested))
            {
                // Cancelled or given up: no half file left behind.
                try { File.Delete(part); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* a hidden leftover */ }
                throw;
            }
            catch (Exception ex)
            {
                // The Wi-Fi went, or the direct link: connect again (the other way if need be) and carry on from the part file.
                Log.Info($"Browse: downloading from {Name} dropped ({ex.GetType().Name}); resuming");
                var dropped = connection;
                connection = null;
                await DropAsync(dropped).ConfigureAwait(false);
                transfer.SetState(TransferState.Reconnecting);
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(10, 1 + attempt)), token).ConfigureAwait(false);
                transfer.SetState(TransferState.Transferring);
            }
        }
    }

    private static async Task<string> HashAsync(string path, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan | FileOptions.Asynchronous);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
    }

    private async Task UploadAsync(TransferSession transfer, string root, string folder, IReadOnlyList<string> paths)
    {
        var token = transfer.CancellationToken;
        PeerConnection? connection = null;
        try
        {
            var files = paths.Where(File.Exists).Select(p => new FileInfo(p)).ToList();
            transfer.SetTotals(files.Count, files.Sum(f => f.Length), 0, 0, 0);
            transfer.SetState(TransferState.Transferring);
            foreach (var file in files)
            {
                token.ThrowIfCancellationRequested();
                transfer.SetCurrentFile(file.Name);
                for (var attempt = 0; ; attempt++)
                {
                    try
                    {
                        connection ??= await ConnectAsync(token).ConfigureAwait(false);
                        transfer.SetPath(Path);
                        await UploadFileAsync(transfer, connection, root, folder, file, token).ConfigureAwait(false);
                        break;
                    }
                    catch (Exception ex) when (IsDropped(ex) && attempt < 30 && !token.IsCancellationRequested)
                    {
                        Log.Info($"Browse: adding files to {Name} dropped ({ex.GetType().Name}); trying again");
                        var dropped = connection;
                        connection = null;
                        await DropAsync(dropped).ConfigureAwait(false);
                        transfer.SetState(TransferState.Reconnecting);
                        await Task.Delay(TimeSpan.FromSeconds(Math.Min(10, 1 + attempt)), token).ConfigureAwait(false);
                        transfer.SetState(TransferState.Transferring);
                    }
                }
            }

            transfer.SetState(transfer.GetSnapshot().FailedFiles > 0 ? TransferState.CompletedWithErrors : TransferState.Completed);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            transfer.SetState(TransferState.Cancelled, new TransferError(TransferErrorKind.CancelledByUser, L.T("You cancelled the transfer.")));
        }
        catch (Exception ex)
        {
            Log.Warn($"Browse: adding files to {Name} failed ({ex.GetType().Name}: {ex.Message})");
            transfer.SetState(TransferState.Failed, ex is TransferException te ? te.Error : ErrorTranslator.FromException(ex, Name));
        }
        finally
        {
            if (connection != null) await connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>One file, sent whole (a drop sends it again; the other side never keeps a half file).</summary>
    private async Task UploadFileAsync(TransferSession transfer, PeerConnection connection, string root, string folder, FileInfo file, CancellationToken token)
    {
        var channel = connection.Channel;
        await channel.SendAsync(FrameType.BrowseRequest, new BrowseRequestMessage
        {
            Action = BrowseActions.Write,
            Root = root,
            Path = folder,
            Name = file.Name,
            Size = file.Length,
            Modified = new DateTimeOffset(file.LastWriteTimeUtc).ToUnixTimeMilliseconds(),
        }, token).ConfigureAwait(false);
        var ready = await channel.ReadMessageAsync<BrowseResponseMessage>(FrameType.BrowseResponse, TimeSpan.FromSeconds(60), token).ConfigureAwait(false);
        if (!ready.Ok)
        {
            transfer.FileFailed(file.Name, ready.Message ?? L.T("The file couldn't be added."));
            return;
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[256 * 1024];
        long sent = 0;
        await using (var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1, FileOptions.SequentialScan | FileOptions.Asynchronous))
        {
            while (sent < file.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, file.Length - sent)), token).ConfigureAwait(false);
                if (read == 0) throw new IOException("The file got shorter while it was sent.");
                hash.AppendData(buffer, 0, read);
                await channel.SendDataAsync(FrameType.BrowseData, buffer.AsMemory(0, read), token).ConfigureAwait(false);
                sent += read;
                transfer.AddTransferred(read);
            }
        }

        await channel.SendAsync(FrameType.BrowseRequest, new BrowseRequestMessage { Action = BrowseActions.Commit, Hash = Convert.ToHexString(hash.GetHashAndReset()) },
            token).ConfigureAwait(false);
        var done = await channel.ReadMessageAsync<BrowseResponseMessage>(FrameType.BrowseResponse, TimeSpan.FromSeconds(60), token).ConfigureAwait(false);
        if (done.Ok) transfer.FileCompleted();
        else transfer.FileFailed(file.Name, done.Message ?? L.T("The file couldn't be added."));
    }
}
