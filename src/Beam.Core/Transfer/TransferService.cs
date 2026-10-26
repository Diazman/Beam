using Beam.Core.Files;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Beam.Core.Diagnostics;
using Beam.Core.Discovery;
using Beam.Core.Identity;
using Beam.Core.Protocol;
using Beam.Core.Util;
using Beam.Core.Localization;

namespace Beam.Core.Transfer;

/// <summary>How one outgoing transfer connects (a direct session's; null for the shared network's usual way).</summary>
internal sealed class SendOptions
{
    /// <summary>Runs before each connection attempt instead of <see cref="TransferService.BeforeConnect"/>.</summary>
    public Func<DeviceInfo, CancellationToken, Task>? BeforeConnect { get; init; }

    /// <summary>Whether the transfer must go to <see cref="RequiredEndpoint"/> right now (Direct mode).</summary>
    public Func<bool>? RequiresEndpoint { get; init; }

    /// <summary>The only endpoint this transfer may send to while required (checked before anything is sent); null: none yet.</summary>
    public Func<IPEndPoint?>? RequiredEndpoint { get; init; }

    /// <summary>The local address each connection leaves from (this device's address on the direct network).</summary>
    public Func<IPEndPoint, IPAddress?>? LocalAddressFor { get; init; }
}

/// <summary>
/// Runs the TCP listener that accepts incoming transfers and starts outgoing ones.
/// Each transfer is an independent connection, so several can run at the same time.
/// </summary>
public sealed class TransferService : IAsyncDisposable
{
    private const int MaxConcurrentConnections = 16;
    private const int MaxPendingPrompts = 4;

    private readonly ConcurrentDictionary<string, TransferSession> _sessions = new();
    private readonly ConcurrentDictionary<string, byte> _promptingFingerprints = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentBag<(byte[] A, byte[] B)> _bufferPool = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Func<string> _deviceName;
    private readonly Func<string> _deviceKind;
    private TcpListener? _listener;
    private Task? _acceptTask;
    private int _connections;

    public TransferService(
        DeviceIdentity identity,
        ResumeStore resumeStore,
        IIncomingTransferHandler handler,
        Func<IncomingPolicy> policy,
        Func<string> deviceName,
        Func<string>? deviceKind = null)
    {
        Identity = identity;
        ResumeStore = resumeStore;
        Handler = handler;
        Policy = policy;
        _deviceName = deviceName;
        _deviceKind = deviceKind ?? (() => DeviceKinds.Desktop);
    }

    /// <summary>A transfer was created (sent or received). Raised on a background thread.</summary>
    public event Action<TransferSession>? SessionStarted;

    /// <summary>A transfer reached a final state. Raised on a background thread; may be raised again after a resume.</summary>
    public event Action<TransferSession>? SessionFinished;

    public DeviceIdentity Identity { get; }

    internal ResumeStore ResumeStore { get; }

    internal IIncomingTransferHandler Handler { get; }

    internal Func<IncomingPolicy> Policy { get; }

    /// <summary>Files handed over as streams (Android content:// URIs); null on desktop.</summary>
    public IExternalFiles? ExternalFiles { get; init; }

    /// <summary>
    /// How long a running transfer keeps trying to reconnect, without any data getting through, before it fails (it
    /// can still be resumed later).
    /// </summary>
    internal TimeSpan ReconnectPatience { get; set; } = TimeSpan.FromMinutes(3);

    /// <summary>Runs before each connection attempt to a device (sets up a direct link when that's the chosen method).</summary>
    public Func<DeviceInfo, CancellationToken, Task>? BeforeConnect { get; set; }

    /// <summary>Handles <see cref="ConnectionPurpose.Direct"/> connections (null: such connections are closed).</summary>
    internal Func<PeerConnection, CancellationToken, Task>? DirectHandler { get; set; }

    /// <summary>Whether an address is reached over a direct Wi-Fi link (replaced in tests).</summary>
    public Func<IPAddress, bool> IsDirectAddress { get; set; } = Direct.DirectAddresses.IsDirect;

    internal TransferPath PathOf(IPEndPoint remote) => IsDirectAddress(remote.Address) ? TransferPath.Direct : TransferPath.Network;

    public int Port { get; private set; }

    public bool IsListening => _listener != null;

    public IReadOnlyList<TransferSession> Sessions => _sessions.Values.OrderBy(s => s.StartedAt).ToList();

    public bool HasActiveTransfers => _sessions.Values.Any(s => !s.IsFinished && s.State != TransferState.Interrupted);

    internal string CancelReason => _cts.IsCancellationRequested ? Reasons.Shutdown : Reasons.Cancelled;

    /// <summary>Starts listening on <paramref name="preferredPort"/>, or a nearby free port if it is taken.</summary>
    public void Start(int preferredPort = AppInfo.TransferPort)
    {
        var candidates = preferredPort == 0
            ? new[] { 0 }
            : Enumerable.Range(preferredPort, 10).Append(0).ToArray();
        foreach (var port in candidates)
        {
            var listener = new TcpListener(IPAddress.Any, port);
            try
            {
                if (OperatingSystem.IsWindows()) listener.ExclusiveAddressUse = true;
                listener.Start(backlog: 32);
                _listener = listener;
                Port = ((IPEndPoint)listener.LocalEndpoint).Port;
                break;
            }
            catch (SocketException ex)
            {
                Log.Info($"TCP port {port} unavailable: {ex.SocketErrorCode}");
                listener.Stop();
            }
        }

        if (_listener == null) throw new InvalidOperationException("Could not open a network port for receiving files.");
        Log.Info($"Listening for transfers on TCP {Port}");
        _acceptTask = Task.Run(AcceptLoopAsync);
    }

    /// <summary>Starts sending files and folders to a device. Progress is reported through the returned session.</summary>
    public TransferSession Send(DeviceInfo target, IReadOnlyList<string> paths, Func<DeviceInfo>? refreshTarget = null) =>
        Send(target, paths, refreshTarget, null);

    /// <summary>
    /// Like <see cref="Send(DeviceInfo, IReadOnlyList{string}, Func{DeviceInfo}?)"/>, with <paramref name="options"/> for a
    /// direct session (its own preparation before each connection, and the one endpoint the files may go to).
    /// <paramref name="configure"/> runs on the session before it starts.
    /// </summary>
    internal TransferSession Send(DeviceInfo target, IReadOnlyList<string> paths, Func<DeviceInfo>? refreshTarget, SendOptions? options,
        Action<TransferSession>? configure = null)
    {
        if (paths.Count == 0) throw new ArgumentException("Nothing to send.", nameof(paths));
        var session = new TransferSession(Guid.NewGuid().ToString("N"), TransferDirection.Send, target.Id, target.Name, target.Fingerprint);
        session.SetDescription(paths.Select(DisplayName).Where(n => n.Length > 0).ToList());
        session.SourcePaths = paths;
        configure?.Invoke(session);
        Register(session);
        var transfer = new OutgoingTransfer(this, session, paths, refreshTarget ?? (() => target), options);
        transfer.Start();
        return session;
    }

    private string DisplayName(string path) =>
        ExternalFiles != null && ExternalFiles.TryGetFolderName(path, out var folder) ? folder
        : ExternalFiles != null && ExternalFiles.TryGet(path, out var file) ? file.Name
        : Path.GetFileName(Path.TrimEndingDirectorySeparator(path));

    /// <summary>Sends a piece of text or a link to a device. The other side shows it (or copies it, if it trusts this device).</summary>
    public TransferSession SendText(DeviceInfo target, string text, Func<DeviceInfo>? refreshTarget = null)
    {
        if (string.IsNullOrEmpty(text)) throw new ArgumentException("Nothing to send.", nameof(text));
        if (text.Length > TextTransfer.MaxLength)
            throw new TransferException(TransferErrorKind.NothingToSend, L.T("That text is too long to send. Save it as a file and send the file instead."));
        var session = new TransferSession(Guid.NewGuid().ToString("N"), TransferDirection.Send, target.Id, target.Name, target.Fingerprint)
        {
            Text = text,
        };
        session.SetDescription(new[] { TextTransfer.Describe(text) });
        session.SetTotals(1, text.Length, 0, 0, 0);
        Register(session);
        _ = Task.Run(() => TextTransfer.SendAsync(this, session, text, refreshTarget ?? (() => target)));
        return session;
    }

    /// <summary>Connects to an address to learn which device is there (used for manual connections).</summary>
    public async Task<DeviceInfo> ProbeAsync(IPEndPoint endpoint, CancellationToken cancellationToken)
    {
        await using var connection = await SecureTransport.ConnectAsync(
            Identity, CreateHello(ConnectionPurpose.Probe), new[] { endpoint }, null, endpoint.Address.ToString(), cancellationToken)
            .ConfigureAwait(false);
        var hello = connection.RemoteHello;
        if (string.IsNullOrWhiteSpace(hello.DeviceId)) throw new ProtocolException("Device did not identify itself.");
        if (hello.DeviceId == Identity.DeviceId)
            throw new TransferException(TransferErrorKind.ConnectFailed, L.ForDevice("That address belongs to this computer.", "That address belongs to this phone."));
        return new DeviceInfo
        {
            Id = hello.DeviceId,
            Name = Settings.AppSettings.NormalizeDeviceName(hello.DeviceName),
            Fingerprint = connection.RemoteFingerprint,
            Platform = hello.Platform,
            Kind = string.IsNullOrEmpty(hello.Kind) ? DeviceKinds.Desktop : hello.Kind,
            AppVersion = hello.AppVersion,
            Endpoints = new[] { new IPEndPoint(endpoint.Address, hello.Port > 0 ? hello.Port : endpoint.Port) },
            IsManual = true,
        };
    }

    /// <summary>Removes finished transfers from <see cref="Sessions"/>.</summary>
    public void Forget(TransferSession session)
    {
        if (session.IsFinished) _sessions.TryRemove(new KeyValuePair<string, TransferSession>(session.Id + session.Direction, session));
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts.IsCancellationRequested) return;
        _cts.Cancel();
        try { _listener?.Stop(); } catch { /* ignore */ }

        foreach (var session in _sessions.Values.Where(s => !s.IsFinished && s.State != TransferState.Interrupted))
            session.Cancel();

        var deadline = DateTime.UtcNow.AddSeconds(4);
        while (DateTime.UtcNow < deadline && _sessions.Values.Any(s => !s.IsFinished && s.State != TransferState.Interrupted))
            await Task.Delay(50).ConfigureAwait(false);

        if (_acceptTask != null)
        {
            try { await _acceptTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { /* ignore */ }
        }
    }

    internal HelloMessage CreateHello(string purpose) => new()
    {
        DeviceId = Identity.DeviceId,
        DeviceName = _deviceName(),
        Platform = AppInfo.Platform,
        Kind = _deviceKind(),
        AppVersion = AppInfo.Version,
        Purpose = purpose,
        Port = Port,
    };

    internal bool TryReservePrompt(string fingerprint)
    {
        if (_promptingFingerprints.Count >= MaxPendingPrompts) return false;
        return _promptingFingerprints.TryAdd(fingerprint, 0);
    }

    internal void ReleasePrompt(string fingerprint) => _promptingFingerprints.TryRemove(fingerprint, out _);

    /// <summary>
    /// The sender reconnected to resume <paramref name="transferId"/> while this device still thinks the old connection is
    /// alive (a dropped Wi-Fi often isn't noticed here for a while): drop the old connection and wait until it has saved
    /// its progress, so the new one can take over instead of being refused.
    /// </summary>
    internal async Task TakeOverStaleTransferAsync(string transferId, string fingerprint, CancellationToken cancellationToken)
    {
        if (!_sessions.TryGetValue(transferId + TransferDirection.Receive, out var existing) || existing.IsFinished
            || existing.State is TransferState.Interrupted
            || !string.Equals(existing.PeerFingerprint, fingerprint, StringComparison.OrdinalIgnoreCase)) return;
        if (!existing.InterruptConnection(switchingPath: false)) return;
        Log.Info($"{existing.PeerName} reconnected for {transferId}; dropping the old connection");
        var deadline = Environment.TickCount64 + 10_000;
        while (!existing.IsFinished && existing.State is not TransferState.Interrupted && Environment.TickCount64 < deadline)
            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
    }

    internal TransferSession CreateIncomingSession(string transferId, PeerConnection connection, bool reuseInterrupted = false)
    {
        var key = transferId + TransferDirection.Receive;
        if (_sessions.TryGetValue(key, out var existing))
        {
            if (!string.Equals(existing.PeerFingerprint, connection.RemoteFingerprint, StringComparison.OrdinalIgnoreCase))
                throw new ProtocolException("Transfer id already in use by another device.");
            var reusable = reuseInterrupted && !existing.CancellationToken.IsCancellationRequested
                           && existing.State is TransferState.Interrupted or TransferState.Failed;
            if (reusable) return existing;
            if (!existing.IsFinished) throw new ProtocolException("Transfer is already in progress.");
        }

        var hello = connection.RemoteHello;
        var session = new TransferSession(transferId, TransferDirection.Receive, hello.DeviceId,
            Settings.AppSettings.NormalizeDeviceName(hello.DeviceName), connection.RemoteFingerprint);
        Register(session);
        return session;
    }

    internal (byte[] A, byte[] B) RentBuffers()
    {
        if (_bufferPool.TryTake(out var buffers)) return buffers;
        var size = FrameChannel.HeaderSize + FrameChannel.DataChunkSize;
        return (new byte[size], new byte[size]);
    }

    internal void ReturnBuffers((byte[] A, byte[] B) buffers)
    {
        if (_bufferPool.Count < 8) _bufferPool.Add(buffers);
    }

    /// <summary>Adds a transfer that runs outside this service (e.g. with a phone's browser) so it shows up and is recorded like the others.</summary>
    internal void RegisterExternal(TransferSession session) => Register(session);

    private void Register(TransferSession session)
    {
        _sessions[session.Id + session.Direction] = session;
        session.StateChanged += s =>
        {
            if (s.IsFinished) SessionFinished?.Invoke(s);
        };
        SessionStarted?.Invoke(session);
    }

    private async Task AcceptLoopAsync()
    {
        var listener = _listener!;
        var token = _cts.Token;
        while (!token.IsCancellationRequested)
        {
            Socket socket;
            try
            {
                socket = await listener.AcceptSocketAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException ex)
            {
                if (token.IsCancellationRequested) break;
                Log.Warn($"Accept failed: {ex.SocketErrorCode}");
                continue;
            }

            if (Interlocked.Increment(ref _connections) > MaxConcurrentConnections)
            {
                Interlocked.Decrement(ref _connections);
                socket.Dispose();
                continue;
            }

            _ = Task.Run(() => HandleConnectionAsync(socket, token));
        }
    }

    private async Task HandleConnectionAsync(Socket socket, CancellationToken token)
    {
        var remote = socket.RemoteEndPoint?.ToString() ?? "unknown";
        PeerConnection? connection = null;
        try
        {
            SecureTransport.ConfigureSocket(socket);
            var (ssl, fingerprint) = await SecureTransport.AuthenticateAsServerAsync(socket, Identity.Certificate, token).ConfigureAwait(false);
            var channel = new FrameChannel(ssl);
            HelloMessage hello;
            try
            {
                hello = await channel.ReadMessageAsync<HelloMessage>(FrameType.Hello, SecureTransport.HandshakeTimeout, token).ConfigureAwait(false);
            }
            catch
            {
                await ssl.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            hello.DeviceName = Settings.AppSettings.NormalizeDeviceName(hello.DeviceName);
            connection = new PeerConnection(socket, ssl, channel, fingerprint, hello);
            await channel.SendAsync(FrameType.HelloReply, CreateHello(ConnectionPurpose.Transfer), token).ConfigureAwait(false);

            if (!SecureTransport.IsCompatible(hello))
            {
                Log.Info($"Incompatible device {hello.DeviceName} at {remote} (protocol {hello.Protocol})");
                return;
            }

            if (hello.Purpose == ConnectionPurpose.Probe || string.IsNullOrWhiteSpace(hello.DeviceId)) return;
            if (hello.Purpose == ConnectionPurpose.Direct)
            {
                if (DirectHandler != null) await DirectHandler(connection, token).ConfigureAwait(false);
                return;
            }

            if (hello.Purpose == ConnectionPurpose.Text)
            {
                await TextTransfer.ReceiveAsync(this, connection, token).ConfigureAwait(false);
                return;
            }

            await new IncomingTransfer(this, connection).RunAsync(token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException && token.IsCancellationRequested)
        {
            // shutting down
        }
        catch (Exception ex)
        {
            Log.Warn($"Incoming connection from {remote} ended: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (connection != null) await connection.DisposeAsync().ConfigureAwait(false);
            else socket.Dispose();
            Interlocked.Decrement(ref _connections);
        }
    }
}
