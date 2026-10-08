using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Beam.Core.Diagnostics;
using Beam.Core.Files;
using Beam.Core.Licensing;
using Beam.Core.Transfer;
using Beam.Core.Localization;

namespace Beam.Core.Phone;

/// <summary>
/// Lets any phone (or tablet, or another computer) send and receive files with this computer
/// through its web browser — no app needed. The phone opens a link (shown as a QR code) that
/// contains a random secret; without it nothing is reachable. Files from the phone still need
/// the user's approval, like transfers from other computers.
/// </summary>
public sealed class PhoneLinkServer : IAsyncDisposable
{
    public const int DefaultPort = 47831;
    public const int MaxFilesPerOffer = 5000;
    private const int MaxConnections = 16;
    private static readonly TimeSpan HeaderTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan UploadIdleLimit = TimeSpan.FromMinutes(3);

    private readonly TransferService _transfers;
    private readonly SendQuota _quota;
    private readonly Func<string> _deviceName;
    private readonly Func<IncomingPolicy> _policy;
    private readonly IIncomingTransferHandler _handler;
    private readonly ConcurrentDictionary<string, PhoneUpload> _uploads = new();
    private readonly object _shareGate = new();
    private List<SharedEntry> _shared = new();
    private TransferSession? _shareSession;
    private CancellationTokenSource? _cts;
    private TcpListener? _listener;
    private Task? _acceptTask;
    private Task? _watchdog;
    private int _connections;
    private byte[] _token = Array.Empty<byte>();

    internal PhoneLinkServer(TransferService transfers, SendQuota quota, Func<string> deviceName, Func<IncomingPolicy> policy, IIncomingTransferHandler handler)
    {
        _transfers = transfers;
        _quota = quota;
        _deviceName = deviceName;
        _policy = policy;
        _handler = handler;
    }

    /// <summary>A phone opened the page or did something (raised on a background thread).</summary>
    public event Action? Activity;

    public bool IsRunning => _listener != null;

    public int Port { get; private set; }

    /// <summary>The secret part of the link (changes every time the server starts).</summary>
    public string Token { get; private set; } = "";

    /// <summary>The kind of phone that last opened the page ("iPhone", "Android phone"…), if any.</summary>
    public string? LastVisitor { get; private set; }

    public IReadOnlyList<string> SharedNames
    {
        get
        {
            lock (_shareGate) return _shared.Select(s => s.Name).ToList();
        }
    }

    /// <summary>The link for each of this computer's network addresses (as shown by <see cref="BeamNode.GetLocalAddresses"/>).</summary>
    public IReadOnlyList<string> GetLinks(IEnumerable<string> addresses) =>
        addresses.Select(a => a.Split(':')[0]).Distinct().Select(ip => $"http://{ip}:{Port}/{Token}/").ToList();

    public void Start(int preferredPort = DefaultPort)
    {
        if (IsRunning) return;
        _token = RandomNumberGenerator.GetBytes(12);
        Token = Convert.ToHexString(_token).ToLowerInvariant();
        var candidates = preferredPort == 0 ? new[] { 0 } : Enumerable.Range(preferredPort, 5).Append(0).ToArray();
        foreach (var port in candidates)
        {
            var listener = new TcpListener(IPAddress.Any, port);
            try
            {
                if (OperatingSystem.IsWindows()) listener.ExclusiveAddressUse = true;
                listener.Start(backlog: 16);
                _listener = listener;
                Port = ((IPEndPoint)listener.LocalEndpoint).Port;
                break;
            }
            catch (SocketException)
            {
                listener.Stop();
            }
        }

        if (_listener == null) throw new InvalidOperationException("Could not open a network port for phones.");
        _cts = new CancellationTokenSource();
        _acceptTask = Task.Run(() => AcceptLoopAsync(_listener, _cts.Token));
        _watchdog = Task.Run(() => WatchdogAsync(_cts.Token));
        Log.Info($"Phone link listening on TCP {Port}");
    }

    public async Task StopAsync()
    {
        var cts = _cts;
        if (cts == null) return;
        _cts = null;
        cts.Cancel();
        try { _listener?.Stop(); } catch { /* ignore */ }
        foreach (var task in new[] { _acceptTask, _watchdog })
        {
            if (task == null) continue;
            try { await task.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); } catch { /* ignore */ }
        }

        foreach (var upload in _uploads.Values) upload.Finish(L.T("The connection to the phone was closed."));
        _uploads.Clear();
        StopSharing();
        Token = "";
        LastVisitor = null;
        _listener = null; // IsRunning turns false only once everything is cleaned up
        Log.Info("Phone link stopped");
    }

    /// <summary>
    /// Offers files (folders are expanded) for the phone to download. Counts as one send towards the free
    /// edition's daily limit; throws <see cref="TransferException"/> (<see cref="TransferErrorKind.SendLimitReached"/>) when it is used up.
    /// </summary>
    public TransferSession Share(IReadOnlyList<string> paths)
    {
        var entries = new List<SharedEntry>();
        foreach (var path in paths)
        {
            if (File.Exists(path))
            {
                entries.Add(new SharedEntry(Path.GetFileName(path), path, new FileInfo(path).Length));
            }
            else if (Directory.Exists(path))
            {
                var root = Path.TrimEndingDirectorySeparator(path);
                var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
                foreach (var file in new DirectoryInfo(root).EnumerateFiles("*", options))
                {
                    var relative = Path.GetRelativePath(Path.GetDirectoryName(root) ?? root, file.FullName).Replace('\\', '/');
                    entries.Add(new SharedEntry(relative, file.FullName, file.Length));
                }
            }
        }

        if (entries.Count == 0) throw new TransferException(TransferErrorKind.NothingToSend, L.T("There's nothing to share: the files are empty folders or can't be found."));
        if (!_quota.TryUse())
            throw new TransferException(TransferErrorKind.SendLimitReached,
                L.Plural(FreeLimits.SendsPerDay, "You've used today's {0} free send. Upgrade to Beam Pro for unlimited sends, or send again tomorrow.", "You've used today's {0} free sends. Upgrade to Beam Pro for unlimited sends, or send again tomorrow."));

        StopSharing();
        var session = new TransferSession(Guid.NewGuid().ToString("N"), TransferDirection.Send, PhonePeerId, "your phone", "");
        session.SetDescription(paths.Select(p => Path.GetFileName(Path.TrimEndingDirectorySeparator(p))).Where(n => n.Length > 0).ToList());
        session.SetTotals(entries.Count, entries.Sum(e => e.Size), 0, 0, 0);
        session.SetState(TransferState.WaitingForAcceptance);
        session.CancellationToken.Register(() => Task.Run(() => StopSharing(session)));
        lock (_shareGate)
        {
            _shared = entries;
            _shareSession = session;
        }

        _transfers.RegisterExternal(session);
        Activity?.Invoke();
        return session;
    }

    /// <summary>Peer id used for transfers with phones (lets the UI word things differently).</summary>
    public const string PhonePeerId = "phone";

    /// <summary>Stops offering files to the phone (only if <paramref name="only"/> is still the current share, when given).</summary>
    public void StopSharing(TransferSession? only = null)
    {
        TransferSession? session;
        lock (_shareGate)
        {
            if (only != null && _shareSession != only) return;
            session = _shareSession;
            _shareSession = null;
            _shared = new List<SharedEntry>();
        }

        if (session != null && !session.IsFinished)
        {
            var snapshot = session.GetSnapshot();
            if (session.CancellationToken.IsCancellationRequested)
                session.SetState(TransferState.Cancelled, new TransferError(TransferErrorKind.CancelledByUser, L.T("You stopped sharing with your phone.")));
            else if (snapshot.CompletedFiles > 0)
                session.SetState(TransferState.Completed);
            else
                session.SetState(TransferState.Cancelled, new TransferError(TransferErrorKind.CancelledByUser, L.T("Stopped sharing with your phone.")));
        }

        Activity?.Invoke();
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            Socket socket;
            try
            {
                socket = await listener.AcceptSocketAsync(token).ConfigureAwait(false);
            }
            catch
            {
                return;
            }

            if (Interlocked.Increment(ref _connections) > MaxConnections)
            {
                Interlocked.Decrement(ref _connections);
                socket.Dispose();
                continue;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await HandleConnectionAsync(socket, token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
                {
                    // phone went away
                }
                catch (Exception ex)
                {
                    Log.Warn("Phone request failed", ex);
                }
                finally
                {
                    socket.Dispose();
                    Interlocked.Decrement(ref _connections);
                }
            }, CancellationToken.None);
        }
    }

    private async Task HandleConnectionAsync(Socket socket, CancellationToken serviceToken)
    {
        socket.NoDelay = true;
        await using var stream = new NetworkStream(socket, ownsSocket: false);
        HttpRequest? request;
        using (var headerCts = CancellationTokenSource.CreateLinkedTokenSource(serviceToken))
        {
            headerCts.CancelAfter(HeaderTimeout);
            request = await MiniHttp.ReadRequestAsync(stream, headerCts.Token).ConfigureAwait(false);
        }

        if (request == null) return;
        var remote = (socket.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "unknown";
        var segments = request.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || !IsToken(segments[0]))
        {
            await MiniHttp.WriteTextAsync(stream, 404, "Not found. Scan the QR code shown in Beam again.", serviceToken).ConfigureAwait(false);
            return;
        }

        LastVisitor = DescribeDevice(request.UserAgent);
        var route = string.Join('/', segments.Skip(1));
        switch (request.Method, route)
        {
            case ("GET", ""):
                if (!request.Path.EndsWith('/'))
                {
                    await MiniHttp.WriteAsync(stream, 301, "text/plain", Array.Empty<byte>(), serviceToken, $"Location: /{Token}/\r\n").ConfigureAwait(false);
                    return;
                }

                Activity?.Invoke();
                await MiniHttp.WriteAsync(stream, 200, "text/html; charset=utf-8", PageBytes.Value, serviceToken,
                    "Content-Security-Policy: default-src 'self'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; img-src 'self' data:\r\n").ConfigureAwait(false);
                return;
            case ("GET", "api/info"):
                await WriteJsonAsync(stream, new PhoneInfo { Device = _deviceName(), Version = AppInfo.Version }, PhoneJson.Default.PhoneInfo, serviceToken).ConfigureAwait(false);
                return;
            case ("GET", "api/files"):
                await WriteJsonAsync(stream, ListShared(), PhoneJson.Default.PhoneFileList, serviceToken).ConfigureAwait(false);
                return;
            case ("POST", "api/offer"):
                await HandleOfferAsync(stream, request, remote, serviceToken).ConfigureAwait(false);
                return;
        }

        if (request.Method == "GET" && segments.Length == 4 && segments[1] == "api" && segments[2] == "files")
        {
            await HandleDownloadAsync(stream, segments[3], serviceToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "PUT" && segments.Length == 5 && segments[1] == "api" && segments[2] == "upload" && int.TryParse(segments[4], out var index))
        {
            await HandleUploadAsync(stream, request, segments[3], index, serviceToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && segments.Length == 5 && segments[1] == "api" && segments[2] == "offer" && segments[4] == "finish")
        {
            if (_uploads.TryRemove(segments[3], out var finished)) finished.Finish(L.T("The phone didn't send this file."));
            await WriteJsonAsync(stream, new PhoneReply { Ok = true }, PhoneJson.Default.PhoneReply, serviceToken).ConfigureAwait(false);
            return;
        }

        await MiniHttp.WriteTextAsync(stream, 404, "Not found.", serviceToken).ConfigureAwait(false);
    }

    private bool IsToken(string candidate)
    {
        if (_token.Length == 0 || candidate.Length != _token.Length * 2) return false;
        try
        {
            return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(candidate), _token);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private PhoneFileList ListShared()
    {
        lock (_shareGate)
            return new PhoneFileList { Files = _shared.Select((s, i) => new PhoneFileInfo { Id = i.ToString(), Name = s.Name, Size = s.Size }).ToList() };
    }

    // ----- phone → computer -----

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

    private async Task WatchdogAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            foreach (var (id, upload) in _uploads)
            {
                if (DateTime.UtcNow - upload.LastActivity > UploadIdleLimit && _uploads.TryRemove(id, out _))
                    upload.Finish(L.T("The phone stopped sending."));
            }
        }
    }

    // ----- computer → phone -----

    private async Task HandleDownloadAsync(NetworkStream stream, string id, CancellationToken token)
    {
        SharedEntry? entry = null;
        TransferSession? session;
        lock (_shareGate)
        {
            session = _shareSession;
            if (int.TryParse(id, out var index) && index >= 0 && index < _shared.Count) entry = _shared[index];
        }

        // A finished share stays available (the phone may download again) until it is stopped or replaced.
        if (entry == null || session == null || session.State == TransferState.Cancelled)
        {
            await MiniHttp.WriteTextAsync(stream, 404, "This file is no longer shared.", token).ConfigureAwait(false);
            return;
        }

        FileStream file;
        try
        {
            file = new FileStream(entry.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await MiniHttp.WriteTextAsync(stream, 404, "The file can't be read on the computer.", token).ConfigureAwait(false);
            return;
        }

        await using (file.ConfigureAwait(false))
        {
            var name = Path.GetFileName(entry.Name);
            var length = file.Length;
            var disposition = $"Content-Disposition: attachment; filename=\"{AsciiName(name)}\"; filename*=UTF-8''{Uri.EscapeDataString(name)}\r\n";
            await MiniHttp.WriteHeadAsync(stream, 200, ContentTypeFor(name), length, token, disposition).ConfigureAwait(false);
            if (!session.IsFinished)
            {
                session.SetState(TransferState.Transferring);
                session.SetCurrentFile(entry.Name);
            }
            var counted = !entry.Downloaded;
            var sent = 0L;
            var buffer = new byte[64 * 1024];
            try
            {
                int read;
                while (sent < length && (read = await file.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, length - sent)), token).ConfigureAwait(false)) > 0)
                {
                    await _transfers.SendLimiter.WaitAsync(read, token).ConfigureAwait(false);
                    await stream.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                    sent += read;
                    if (counted) session.AddTransferred(read);
                }
            }
            catch
            {
                if (counted) session.AddTransferred(-sent);
                throw;
            }

            if (counted && sent == length && entry.MarkDownloaded())
            {
                session.FileCompleted();
                var snapshot = session.GetSnapshot();
                if (snapshot.CompletedFiles >= snapshot.TotalFiles)
                {
                    session.SetCurrentFile(null);
                    session.SetState(TransferState.Completed);
                }
            }

            Activity?.Invoke();
        }
    }

    private static string AsciiName(string name)
    {
        var builder = new StringBuilder(name.Length);
        foreach (var c in name) builder.Append(c is >= ' ' and < (char)127 and not '"' and not '\\' ? c : '_');
        return builder.ToString();
    }

    private static string ContentTypeFor(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".heic" => "image/heic",
        ".mp4" => "video/mp4",
        ".mov" => "video/quicktime",
        ".mp3" => "audio/mpeg",
        ".pdf" => "application/pdf",
        ".txt" => "text/plain",
        _ => "application/octet-stream",
    };

    private static string DescribeDevice(string userAgent) =>
        userAgent.Contains("iPhone", StringComparison.OrdinalIgnoreCase) ? "iPhone" :
        userAgent.Contains("iPad", StringComparison.OrdinalIgnoreCase) ? "iPad" :
        userAgent.Contains("Android", StringComparison.OrdinalIgnoreCase)
            ? userAgent.Contains("Mobile", StringComparison.OrdinalIgnoreCase) ? L.T("Android phone") : L.T("Android tablet")
            : L.T("Phone");

    private static Task WriteJsonAsync<T>(Stream stream, T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type, CancellationToken token) =>
        MiniHttp.WriteJsonAsync(stream, 200, JsonSerializer.Serialize(value, type), token);

    private static readonly Lazy<byte[]> PageBytes = new(() =>
    {
        using var resource = typeof(PhoneLinkServer).Assembly.GetManifestResourceStream("Beam.Core.Phone.phone.html")
                             ?? throw new InvalidOperationException("Phone page missing from the build.");
        using var memory = new MemoryStream();
        resource.CopyTo(memory);
        return memory.ToArray();
    });

    private sealed class SharedEntry
    {
        private int _downloaded;

        public SharedEntry(string name, string path, long size)
        {
            Name = name;
            Path = path;
            Size = size;
        }

        public string Name { get; }

        public string Path { get; }

        public long Size { get; }

        public bool Downloaded => Volatile.Read(ref _downloaded) == 1;

        public bool MarkDownloaded() => Interlocked.Exchange(ref _downloaded, 1) == 0;
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
                TryDelete(part);
                Finish(L.T("You cancelled the transfer."), cancelled: true);
            }
            catch
            {
                Session.AddTransferred(-received);
                TryDelete(part);
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

        private static void TryDelete(string path)
        {
            try { File.Delete(path); } catch { /* best effort */ }
        }
    }
}
