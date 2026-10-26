using System.Collections.Concurrent;
using System.Net;
using Beam.Core.Diagnostics;
using Beam.Core.Discovery;
using Beam.Core.Localization;
using Beam.Core.Protocol;
using Beam.Core.Settings;
using Beam.Core.Transfer;

namespace Beam.Core.Direct;

/// <summary>The control channel of one direct session (Beam's TLS connection on the shared network, or Bluetooth).</summary>
internal interface IDirectControl : IAsyncDisposable
{
    DirectControlChannel Channel { get; }

    /// <summary>The receiver's certificate fingerprint as this channel authenticated it.</summary>
    string PeerFingerprint { get; }

    /// <summary>
    /// Hands the receiver the session's network details. <paramref name="delivered"/> runs once they reached it. Throws
    /// <see cref="DirectLinkException"/> when the receiver refuses (or, over Bluetooth, when it couldn't join).
    /// </summary>
    Task OfferAsync(DirectRequestMessage offer, Action delivered, CancellationToken cancellationToken);

    /// <summary>Best effort (end, cancel): a failure only means the receiver finds out by its own deadline.</summary>
    Task TellAsync(DirectRequestMessage message);
}

/// <summary>Set by the nearby service: direct sessions set up over Bluetooth when there's no shared network.</summary>
internal interface IBluetoothDirectControl
{
    /// <summary>
    /// Whether the device can be reached over Bluetooth: its beacon heard just now, or within <paramref name="wait"/>
    /// (scanning meanwhile, whatever page is open).
    /// </summary>
    Task<bool> CanReachAsync(string deviceId, TimeSpan wait, CancellationToken cancellationToken);

    /// <summary>
    /// Bluetooth handshake with the device (its identity checked against <paramref name="expectedFingerprint"/> when
    /// known), then <paramref name="propose"/>. Returns once the receiver accepted; throws <see cref="DirectLinkException"/>.
    /// </summary>
    Task<IDirectControl> OpenAsync(DeviceInfo peer, string? expectedFingerprint, DirectRequestMessage propose, CancellationToken cancellationToken);
}

public sealed partial class DirectManager
{
    /// <summary>
    /// Receivers one sender's direct network serves at once. Windows documents up to 8 clients for its Mobile hotspot;
    /// Wi-Fi Direct groups (Windows and Android) depend on the Wi-Fi driver. 4 is a conservative limit that hasn't been
    /// confirmed on hardware; more receivers are told to wait.
    /// </summary>
    public const int MaxDirectReceivers = 4;

    /// <summary>How long to listen for the receiver's Bluetooth beacon when it wasn't heard just now.</summary>
    internal static TimeSpan BluetoothSightingWait { get; set; } = TimeSpan.FromSeconds(8);

    /// <summary>How long the receiver may take to accept (it may ask its user).</summary>
    internal static TimeSpan ProposeTimeout { get; set; } = TimeSpan.FromSeconds(90);

    /// <summary>How long starting this device's network may take.</summary>
    internal static TimeSpan HostStartTimeout { get; set; } = TimeSpan.FromSeconds(75);

    /// <summary>After the platform said the network started, how long until it must be usable (an address on it).</summary>
    internal TimeSpan HostReadyTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>How long the receiver may take to join and report in (it may have to approve the system's prompt).</summary>
    internal TimeSpan JoinWait { get; set; } = TimeSpan.FromSeconds(180);

    internal static TimeSpan VerifyTimeout { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>Setup (propose to verified) must be done within this.</summary>
    internal static TimeSpan SessionLifetime { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>How long a receiver may use an offer's network details to join.</summary>
    internal static TimeSpan OfferLifetime { get; set; } = TimeSpan.FromMinutes(4);

    /// <summary>
    /// How often a dropped direct link is set up again in a row without any file data getting through in between,
    /// before the transfer fails (a link that keeps dropping but makes progress each time keeps going, up to
    /// <see cref="MaxReconnectsTotal"/>).
    /// </summary>
    internal static int MaxReconnects { get; set; } = 2;

    internal static int MaxReconnectsTotal { get; set; } = 30;

    /// <summary>
    /// After a running transfer's link dropped, how long Beam keeps trying to reach the receiver again (it may be between
    /// networks: a phone that left the direct network takes a while to be back on its Wi-Fi) before the transfer fails.
    /// </summary>
    internal TimeSpan RecoveryWindow { get; set; } = TimeSpan.FromMinutes(2);

    internal TimeSpan RecoveryRetryDelay { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Whether the network this device started is usable (an address on it): a platform saying "started" isn't enough
    /// (replaceable in tests).
    /// </summary>
    internal Func<bool> HostNetworkReady { get; set; } = DirectAddresses.HasHostedDirectNetwork;

    /// <summary>Set by the nearby service (null without Bluetooth).</summary>
    internal IBluetoothDirectControl? Bluetooth { get; set; }

    private readonly ConcurrentDictionary<string, Outgoing> _outgoing = new(); // by session id on the wire

    /// <summary>
    /// Phones that couldn't join this device's internet-sharing network (hotspot), and when: they get the usual network
    /// for <see cref="HotspotRetryAfter"/>, or until one joins the hotspot after all.
    /// </summary>
    private readonly ConcurrentDictionary<string, long> _hotspotJoinFailed = new();

    internal TimeSpan HotspotRetryAfter { get; set; } = TimeSpan.FromMinutes(30);

    private bool HotspotFailedFor(string peerId) =>
        _hotspotJoinFailed.TryGetValue(peerId, out var at) && Environment.TickCount64 - at < HotspotRetryAfter.TotalMilliseconds;

    /// <summary>Held while this device joins a receiver's network, so it doesn't start its own meanwhile (one adapter).</summary>
    private readonly SemaphoreSlim _receiverJoinGate = new(1, 1);

    private const string ReceiverJoinPrefix = "session:join:";

    /// <summary>A send's hold on the receiver's network this device joined (kept while its link is set up again).</summary>
    private static string ReceiverJoinKey(string sendKey) => ReceiverJoinPrefix + sendKey;
    private readonly ConcurrentDictionary<string, DirectSend> _sends = new(); // by transfer id
    private readonly object _hostGate = new();
    private readonly HashSet<string> _hostLeases = new(); // one per Direct transfer (shared by its setups)
    private Task<DirectNetwork>? _sessionHostStart;

    /// <summary>One send: the transfer, its mode, and (Direct) the session it may send over.</summary>
    private sealed class DirectSend
    {
        public required DeviceInfo Receiver { get; init; }

        public required DirectSession Session { get; set; }

        public ConnectionMethod Mode { get; set; }

        public TransferSession? Transfer { get; set; }

        /// <summary>Owns this transfer's share of this device's network (kept while the link is set up again).</summary>
        public string Key { get; } = DirectSession.NewToken();

        /// <summary>One setup, recovery or switch at a time for this transfer.</summary>
        public SemaphoreSlim Gate { get; } = new(1, 1);

        /// <summary>Bytes transferred when the link was last verified (progress since then resets the reconnect budget).</summary>
        public long BytesAtVerify { get; set; }

        /// <summary>When the link was last verified (for its speed in the log).</summary>
        public long VerifiedAt { get; set; }

        public int ReconnectsWithoutProgress { get; set; }

        public int Reconnects { get; set; }

        /// <summary>The key the receiver gave on the last verified session, to set the link up again without its user (<see cref="ReconnectProof"/>).</summary>
        public string? ReconnectKey { get; set; }

        /// <summary>The receiver's Beam on its own network, when the last link was set up there.</summary>
        public IPEndPoint? ReceiversNetworkEndpoint { get; set; }

        /// <summary>This device lost the network the verified link ran on: it is set up again without asking first.</summary>
        public bool LinkLost { get; set; }
    }

    private sealed class Outgoing
    {
        public required DirectSession Session { get; init; }

        public TaskCompletionSource<IPEndPoint> Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IDirectControl? Control { get; set; }

        public int Ended;
    }

    /// <summary>Whether this device could send to <paramref name="peer"/> directly (it hosts; the peer joins and speaks sessions).</summary>
    public bool CanSendDirectlyTo(DeviceInfo peer) =>
        CanHost && peer.DirectRoles.HasFlag(DirectRoles.SenderHosted) && (peer.DirectRoles & (DirectRoles.Join | DirectRoles.JoinComputers)) != 0
        || CanJoinReceiversNetwork(peer);

    /// <summary>This device's Wi-Fi is switched off (so it can't start or join a direct network until the user turns it on).</summary>
    public bool IsWifiOff => _link is IDirectRadio { IsWifiEnabled: false };

    /// <summary>Shows the system's Wi-Fi panel and waits (up to <paramref name="wait"/>) for Wi-Fi to be on. True when it is.</summary>
    public async Task<bool> RequestWifiOnAsync(TimeSpan wait, CancellationToken cancellationToken = default)
    {
        if (_link is not IDirectRadio radio || radio.IsWifiEnabled != false) return true;
        Log.Info("Direct: Wi-Fi is off; asking the user to turn it on");
        var on = await radio.RequestWifiOnAsync(wait, cancellationToken).ConfigureAwait(false);
        Log.Info(on ? "Direct: Wi-Fi is on" : "Direct: Wi-Fi is still off");
        return on;
    }

    /// <summary>Whether an address is on a direct network (as the transfer service tells it).</summary>
    public bool IsDirectAddress(IPAddress address) => _transfers.IsDirectAddress(address);

    /// <summary>Tests: no direct session (sending or receiving) is set up or holds the link.</summary>
    internal bool IsIdleForTests => _outgoing.IsEmpty && _incoming.IsEmpty && !LinkInUseBySessions();

    internal IDirectLink? LinkForTests => _link;

    private static string LeaseKey(string owner) => "session:" + owner;

    /// <summary>A direct session uses the radio (this device hosts for one, or joined one's network).</summary>
    private bool LinkInUseBySessions() => _linkedPeers.Keys.Any(k => k.StartsWith("session:", StringComparison.Ordinal) || k.StartsWith("rx:", StringComparison.Ordinal));

    // ---- Sending ----

    /// <summary>
    /// Sends to <paramref name="receiver"/> in the chosen <paramref name="mode"/>. Same Network: the shared network's usual
    /// way, unchanged. Direct: a direct session is set up first (this device hosts, the receiver joins) and the files go
    /// only to the verified direct endpoint; if that fails, the transfer fails at that stage and nothing goes another way.
    /// </summary>
    internal TransferSession Send(DeviceInfo receiver, IReadOnlyList<string> paths, ConnectionMethod mode, bool networkOnly = false)
    {
        var send = new DirectSend { Receiver = receiver, Session = NewSession(receiver), Mode = mode };
        var options = new SendOptions
        {
            BeforeConnect = (_, token) => send.Mode == ConnectionMethod.Direct ? PrepareDirectAsync(send, token) : Task.CompletedTask,
            RequiresEndpoint = () => send.Mode == ConnectionMethod.Direct,
            RequiredEndpoint = () => send.Session.VerifiedEndpoint,
            LocalAddressFor = endpoint => send.Mode == ConnectionMethod.Direct ? LocalAddressFor(endpoint) : null,
        };
        return _transfers.Send(receiver, paths, () => TargetFor(send), options, transfer =>
        {
            send.Transfer = transfer;
            _sends[transfer.Id] = send;
            if (networkOnly) _switchedToNetwork[transfer.Id] = 0;
            transfer.SetMode(mode, mode == ConnectionMethod.Direct ? send.Session : null);
            transfer.StateChanged += _ => OnTransferChanged(send);
            transfer.PathChanged += _ => OnTransferChanged(send);
            if (mode == ConnectionMethod.Direct) Log.Info($"Direct: sending to {receiver.Name} directly ({send.Session.Id[..8]}); the files go nowhere else");
        });
    }

    /// <summary>
    /// The user chose to send a failed Direct transfer over the shared network instead: a new, separate Same Network
    /// transfer of the same files (shown as such). Throws <see cref="TransferException"/> when the device isn't on it.
    /// </summary>
    internal TransferSession SendOverNetworkInstead(TransferSession failed)
    {
        if (failed.Direction != TransferDirection.Send || failed.Mode != ConnectionMethod.Direct || !failed.IsFinished || failed.SourcePaths.Count == 0)
            throw new InvalidOperationException("Only a finished Direct send can be sent over the network instead.");
        var device = _discovery.Find(failed.PeerId);
        if (device == null || !device.Endpoints.Any(e => !_transfers.IsDirectAddress(e.Address)))
            throw new TransferException(TransferErrorKind.ConnectFailed, L.T("{0} isn't on this Wi-Fi network, so Beam can't send over it.", failed.PeerName));
        Log.Info($"Direct: the user chose to send {failed.Id} to {failed.PeerName} over the network instead");
        return Send(device, failed.SourcePaths, ConnectionMethod.SameNetwork, networkOnly: true);
    }

    private DeviceInfo TargetFor(DirectSend send)
    {
        if (send.Mode == ConnectionMethod.Direct)
        {
            // Only the verified endpoint, checked against the certificate the session authenticated.
            var session = send.Session;
            return send.Receiver with
            {
                Endpoints = session.VerifiedEndpoint is { } verified ? new[] { verified } : Array.Empty<IPEndPoint>(),
                Fingerprint = string.IsNullOrEmpty(session.PeerFingerprint) ? send.Receiver.Fingerprint : session.PeerFingerprint,
            };
        }

        var current = _discovery.Find(send.Receiver.Id) ?? send.Receiver;
        // Moved from Direct by the user: only the shared network from now on.
        if (send.Transfer is { } transfer && _switchedToNetwork.ContainsKey(transfer.Id))
            return current with { Endpoints = current.Endpoints.Where(e => !_transfers.IsDirectAddress(e.Address)).ToList() };
        return Order(current); // Same Network: as it always was
    }

    /// <summary>Transfers the user moved from Direct to the shared network (they don't go back to the direct link by themselves).</summary>
    private readonly ConcurrentDictionary<string, byte> _switchedToNetwork = new();

    private DirectSession NewSession(DeviceInfo receiver) => new(receiver.Id, receiver.Name);

    /// <summary>This device's address on the direct network that <paramref name="endpoint"/> is on (null when it isn't on one).</summary>
    private IPAddress? LocalAddressFor(IPEndPoint endpoint) =>
        _transfers.IsDirectAddress(endpoint.Address) ? OwnAddressNear(endpoint.Address) : null;

    /// <summary>
    /// Before each connection of a Direct transfer: the session is verified (set up on the first attempt; checked again
    /// after a drop, and recovered when the link is gone). One at a time per transfer. Throws
    /// <see cref="TransferException"/> (<see cref="TransferErrorKind.DirectFailed"/>) naming the stage that failed.
    /// </summary>
    private async Task PrepareDirectAsync(DirectSend send, CancellationToken token)
    {
        await send.Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var session = send.Session;
            if (session.IsFinished)
            {
                // Tried again after a failure (or after the session ended): a new session, set up like the first one.
                session = send.Session = NewSession(send.Receiver);
                send.ReconnectsWithoutProgress = 0;
                send.Transfer?.SetMode(ConnectionMethod.Direct, session);
            }

            if (session.State == DirectSessionState.Negotiating)
            {
                await EstablishAsync(session, send, token).ConfigureAwait(false);
                return;
            }

            if (session.State is DirectSessionState.Verified or DirectSessionState.Transferring or DirectSessionState.Interrupted
                && !send.LinkLost && await StillVerifiedAsync(send, session, token).ConfigureAwait(false))
                return;

            await RecoverAsync(send, session, token).ConfigureAwait(false);
        }
        finally
        {
            send.Gate.Release();
        }
    }

    /// <summary>
    /// The verified link is gone (the receiver left the network, it went out of range, or the OS dropped it): set the
    /// session up again with a new id and nonce, the receiver authenticated again, on the network this device keeps
    /// hosting. While the receiver can't be reached (between networks) this keeps trying for <see cref="RecoveryWindow"/>.
    /// The transfer then resumes from what the receiver confirms it has.
    /// </summary>
    private async Task RecoverAsync(DirectSend send, DirectSession lost, CancellationToken token)
    {
        lost.TryMove(DirectSessionState.Interrupted);
        var bytes = send.Transfer?.GetSnapshot().TransferredBytes ?? 0;
        if (bytes > send.BytesAtVerify) send.ReconnectsWithoutProgress = 0; // the last link moved data: not a dead end
        if (send.ReconnectsWithoutProgress >= MaxReconnects || send.Reconnects >= MaxReconnectsTotal)
        {
            lost.Fail(L.T("The direct connection to {0} was lost and couldn't be set up again.", lost.PeerName));
            await EndOutgoingAsync(lost, notify: true).ConfigureAwait(false);
            throw Failure(lost);
        }

        send.ReconnectsWithoutProgress++;
        send.Reconnects++;
        var seconds = send.VerifiedAt == 0 ? 0 : (Environment.TickCount64 - send.VerifiedAt) / 1000.0;
        var moved = Math.Max(0, bytes - send.BytesAtVerify);
        Log.Info($"Direct: {lost}: link lost after {bytes} bytes ({moved / 1e6:0.0} MB over this link in {seconds:0} s, " +
                 $"{(seconds > 0 ? moved / 1e6 / seconds : 0):0.00} MB/s); setting it up again (reconnect {send.Reconnects}, " +
                 $"{send.ReconnectsWithoutProgress} of {MaxReconnects} without progress)");
        await EndOutgoingAsync(lost, notify: true).ConfigureAwait(false);
        var deadline = Environment.TickCount64 + (long)RecoveryWindow.TotalMilliseconds;
        while (true)
        {
            var next = NewSession(send.Receiver);
            next.Reconnects = send.Reconnects;
            send.Session = next;
            send.Transfer?.SetMode(ConnectionMethod.Direct, next);
            try
            {
                await EstablishAsync(next, send, token).ConfigureAwait(false);
                return;
            }
            catch (TransferException) when (send.Session.FailedTransiently && Environment.TickCount64 < deadline && !token.IsCancellationRequested)
            {
                var left = (deadline - Environment.TickCount64) / 1000;
                Log.Info($"Direct: {next.PeerName} isn't reachable yet ({send.Session.Error}); trying again in {RecoveryRetryDelay.TotalSeconds:0} s ({left} s left)");
                await Task.Delay(RecoveryRetryDelay, token).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Why a session failed, as the transfer reports it ("Direct connection failed at receiver join: …").</summary>
    private static TransferException Failure(DirectSession session) =>
        new(TransferErrorKind.DirectFailed,
            L.T("Direct connection failed at {0}: {1}", DirectSession.StageName(session.FailedAt ?? DirectSessionState.Negotiating),
                session.Error ?? L.T("Something went wrong.")),
            $"{session}, failed at {session.FailedAt}");

    /// <summary>
    /// Sets <paramref name="session"/> up, from proposing to verified: on the receiver's network when this device can't
    /// start one and the receiver offers to host (<see cref="DirectRoles.HostsForSenders"/>), else on this device's own.
    /// Throws like <see cref="EstablishOnceAsync"/>.
    /// </summary>
    private Task EstablishAsync(DirectSession session, DirectSend send, CancellationToken token, bool receiversNetworkOk = true)
    {
        var current = _discovery.Find(send.Receiver.Id) ?? send.Receiver;
        var onReceivers = receiversNetworkOk && WantsReceiversNetwork(send, current);
        return EstablishOnceAsync(session, send, onReceivers, token);
    }

    /// <summary>
    /// This device can't start a network (an iPhone) and sends to a computer that offers to host: this device joins the
    /// computer's network. One at a time (it can be on one network only), and only when nothing else uses its link.
    /// </summary>
    private bool WantsReceiversNetwork(DirectSend send, DeviceInfo receiver) => CanJoinReceiversNetwork(receiver) && IsOnlyDirectSend(send);

    /// <summary>This device can't host but can join, and <paramref name="receiver"/> offers to host for it.</summary>
    private bool CanJoinReceiversNetwork(DeviceInfo receiver) =>
        !CanHost && CanJoinAny && _link != null && receiver.DirectRoles.HasFlag(DirectRoles.HostsForSenders)
        && receiver.DirectRoles.HasFlag(DirectRoles.SenderHosted);

    private bool IsOnlyDirectSend(DirectSend send)
    {
        if (_sends.Values.Any(s => !ReferenceEquals(s, send) && s.Mode == ConnectionMethod.Direct && s.Transfer is not { IsFinished: true })) return false;
        if (!_incoming.IsEmpty || !_joinedReceivers.IsEmpty) return false;
        lock (_hostGate)
            if (_hostLeases.Count > 0) return false;
        return _linkedPeers.Keys.All(k => k == ReceiverJoinKey(send.Key));
    }

    /// <summary>
    /// Sets <paramref name="session"/> up once, from proposing to verified. Throws <see cref="TransferException"/> naming
    /// the stage that failed (the session is then failed and what it set up is let go), or
    /// <see cref="OperationCanceledException"/> when the transfer or session was cancelled.
    /// </summary>
    private async Task EstablishOnceAsync(DirectSession session, DirectSend send, bool receiversNetwork, CancellationToken token)
    {
        var peer = send.Receiver;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token, session.CancellationToken, _stopping.Token);
        limit.CancelAfter(SessionLifetime);
        var outgoing = new Outgoing { Session = session };
        _outgoing[session.Id] = outgoing;
        DirectNetwork? hosted = null;
        try
        {
            var current = _discovery.Find(peer.Id) ?? peer;
            if (_link == null || (!CanHost && !CanJoinReceiversNetwork(current))) throw new DirectLinkException(L.T("This device can't start a direct connection."));
            if (!CanHost && !receiversNetwork)
                throw new DirectLinkException(L.T("This device sends directly to one device at a time. Send to the others when this is done."));
            if (IsWifiOff) throw new DirectLinkException(L.T("Wi-Fi is off on this device. Turn it on, then press Try again.")) { WifiOff = true };
            if (!CanSendDirectlyTo(current))
                throw new DirectLinkException(L.T("{0} can't join a direct connection from this device. Update Beam on it; phones need Android 10 or newer.", peer.Name));
            if (_outgoing.Values.Count(o => o.Session != session && !o.Session.IsFinished && Volatile.Read(ref o.Ended) == 0) >= MaxDirectReceivers)
                throw new DirectLinkException(L.T("Beam connects directly to at most {0} devices at a time. Send to the others when these are done.", MaxDirectReceivers));

            // 1. Who is it, and does it accept? (Nothing is started before it says yes.)
            Log.Info($"Direct: {session}: negotiating (roles {DirectRolesText.Format(current.DirectRoles)}; addresses {string.Join(", ", current.Endpoints)})");
            // Still on the receiver's network (setting a dropped connection up again): its Beam there is reachable for sure.
            var onItsNetwork = send.ReceiversNetworkEndpoint is { } there && _linkedPeers.ContainsKey(ReceiverJoinKey(send.Key)) && OwnAddressNear(there.Address) != null
                ? new[] { there }
                : Array.Empty<IPEndPoint>();
            outgoing.Control = await OpenControlAsync(session, current, send.ReconnectKey, limit.Token, onItsNetwork).ConfigureAwait(false);
            session.Channel = outgoing.Control.Channel;
            session.PeerFingerprint = outgoing.Control.PeerFingerprint;

            IPEndPoint reported;
            if (receiversNetwork && outgoing.Control is LanControl lan)
            {
                // 2-4. The receiver's network (it shares its internet), joined by this device.
                reported = await JoinReceiversNetworkAsync(session, send, lan, limit.Token).ConfigureAwait(false);
            }
            else
            {
                if (receiversNetwork && !CanHost)
                    throw new DirectLinkException(L.T("{0} can only be reached over Bluetooth, which can't set up its network for this device. Connect both to the same Wi-Fi first.", peer.Name));
                await LeaveReceiversNetworksAsync("this device starts its own network", limit.Token).ConfigureAwait(false);

                // 2. This device's network, now that someone will join it.
                Move(session, DirectSessionState.Hosting);
                // A phone: this device's internet-sharing network when it has one (the phone then stays on it). A computer
                // joining a phone: the phone's hotspot when it can run one (a PC dropped off its Wi-Fi Direct group).
                var receiverIsComputer = IsComputer(current.Kind, current.Platform);
                var forPhone = !receiverIsComputer && !HotspotFailedFor(peer.Id);
                var forComputer = receiverIsComputer && !HotspotFailedFor(peer.Id);
                var network = await AcquireHostAsync(send.Key, session.ToString(), forPhone, forComputer, limit.Token).ConfigureAwait(false);
                hosted = network;

                // 3. Its details, for this receiver only, over the authenticated control channel.
                Move(session, DirectSessionState.OfferingCredentials);
                Log.Info($"Direct: {session}: offering {network.Ssid} over {session.Channel}");
                var offer = new DirectRequestMessage
                {
                    Action = DirectActions.Offer,
                    SessionId = session.Id,
                    Nonce = session.Nonce,
                    ReceiverId = peer.Id,
                    SenderId = _selfId(),
                    SenderName = _settings.Current.DeviceName,
                    SenderFingerprint = _transfers.Identity.Fingerprint,
                    Ssid = network.Ssid,
                    Passphrase = network.Passphrase,
                    TtlSeconds = (int)OfferLifetime.TotalSeconds,
                    // Join it as ordinary Wi-Fi: a computer's network, or a phone's hotspot (not a Wi-Fi Direct group).
                    HostIsComputer = _selfIsComputer || network.IsAccessPoint,
                    HostAddress = OwnHostedAddress()?.ToString(),
                    Port = _transfers.Port,
                };
                if (send.ReconnectKey is { } offerKey) offer.Proof = ReconnectProof.ForOffer(offerKey, offer);
                await outgoing.Control.OfferAsync(offer, () => session.TryMove(DirectSessionState.Joining), limit.Token).ConfigureAwait(false);
                session.TryMove(DirectSessionState.Joining);

                // 4. The receiver reports in over the direct network itself (not just "joined" on the control channel).
                Log.Info($"Direct: {session}: waiting for {peer.Name} to join");
                try
                {
                    reported = await outgoing.Ready.Task.WaitAsync(JoinWait, limit.Token).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    throw new DirectLinkException(L.T("{0} didn't join the direct network in time. If it asks to connect to a Wi-Fi network, tap Connect there.", peer.Name))
                        { JoinFailed = true };
                }
            }

            // 5. Both ends of the route, the session and the receiver's certificate, before any file data.
            Move(session, DirectSessionState.Verifying);
            send.ReconnectKey = await VerifyAsync(session, reported, limit.Token, retryFor: session.OnReceiversNetwork ? ReportInTimeout : TimeSpan.Zero)
                .ConfigureAwait(false);

            if (session.OnReceiversNetwork) send.ReceiversNetworkEndpoint = reported;
            if (hosted is { SharesInternet: true } or { IsAccessPoint: true } && _hotspotJoinFailed.TryRemove(peer.Id, out _))
                Log.Info($"Direct: {peer.Name} joined the hotspot after all");
            if (!session.MarkVerified(reported)) throw new OperationCanceledException(limit.Token);
            send.LinkLost = false;
            send.BytesAtVerify = send.Transfer?.GetSnapshot().TransferredBytes ?? 0;
            send.VerifiedAt = Environment.TickCount64;
            Log.Info($"Direct: {session}: verified at {reported} (channel {session.Channel}, reconnect {session.Reconnects}, " +
                     $"{send.BytesAtVerify} bytes already sent{(session.OnReceiversNetwork ? ", on the receiver's network" : "")})");
        }
        catch (Exception ex) when (token.IsCancellationRequested || session.CancellationToken.IsCancellationRequested || _stopping.IsCancellationRequested)
        {
            // Cancelled: whatever the call in progress threw (cancelling aborts its socket, which may show as a socket
            // error rather than a cancellation), it ends as cancelled, not as a failure.
            Log.Info($"Direct: {session}: cancelled at {session.State}{(ex is OperationCanceledException ? "" : $" ({ex.GetType().Name})")}");
            session.Cancel();
            await EndOutgoingAsync(session, notify: true).ConfigureAwait(false);
            if (ex is OperationCanceledException) throw;
            throw new OperationCanceledException(ex.Message, ex, token);
        }
        catch (Exception ex)
        {
            var message = ex switch
            {
                DirectLinkException => ex.Message,
                OperationCanceledException => L.T("Setting up the direct connection with {0} took too long.", peer.Name),
                TransferException te => te.Error.Message,
                _ => L.T("Something went wrong."),
            };
            Log.Warn($"Direct: {session}: failed at {session.State}: {ex.GetType().Name}: {ex.Message}");
            // Only a join that really failed (not a cancellation, nor a dropped Bluetooth connection) says the phone can't
            // use the hotspot.
            if (ex is DirectLinkException { JoinFailed: true }
                && (hosted is { SharesInternet: true } && !IsComputer(peer.Kind, peer.Platform) || hosted is { IsAccessPoint: true } && IsComputer(peer.Kind, peer.Platform)))
            {
                _hotspotJoinFailed[peer.Id] = Environment.TickCount64;
                Log.Info($"Direct: {peer.Name} couldn't join this device's hotspot; it gets the usual direct network for a while");
            }
            session.FailedTransiently = ex is DirectLinkException { Transient: true };
            session.Fail(message);
            await EndOutgoingAsync(session, notify: true).ConfigureAwait(false);
            throw Failure(session);
        }
    }

    private static void Move(DirectSession session, DirectSessionState next)
    {
        if (!session.TryMove(next)) throw new OperationCanceledException(); // ended meanwhile (cancelled elsewhere)
    }

    /// <summary>
    /// Steps 2-4 on the receiver's network: it starts its network that shares its internet (asked over Beam's TLS
    /// connection, which proved its certificate), and this device joins it. Returns the receiver's Beam there (verified
    /// next, as always).
    /// </summary>
    private async Task<IPEndPoint> JoinReceiversNetworkAsync(DirectSession session, DirectSend send, LanControl control, CancellationToken token)
    {
        var peer = send.Receiver;
        Move(session, DirectSessionState.Hosting);
        Log.Info($"Direct: {session}: asking {peer.Name} to start its network (it shares its internet, so this phone stays on it)");
        var request = new DirectRequestMessage
        {
            Action = DirectActions.HostForSender,
            SessionId = session.Id,
            Nonce = session.Nonce,
            ReceiverId = peer.Id,
            SenderId = _selfId(),
            SenderName = _settings.Current.DeviceName,
            SenderFingerprint = _transfers.Identity.Fingerprint,
            TtlSeconds = (int)OfferLifetime.TotalSeconds,
            Port = _transfers.Port,
        };
        DirectResponseMessage reply;
        try
        {
            reply = await control.AskAsync(request, HostStartTimeout + HostReadyTimeout + TimeSpan.FromSeconds(15), token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TransferException or IOException or System.Net.Sockets.SocketException or TimeoutException
                                       or System.Security.Authentication.AuthenticationException && !token.IsCancellationRequested)
        {
            throw new DirectLinkException(L.T("{0} didn't start its network.", peer.Name), ex);
        }

        if (!reply.Ok)
            throw new DirectLinkException(reply.Message ?? L.T("{0} didn't start its network.", peer.Name), declined: reply.Declined);
        if (string.IsNullOrEmpty(reply.Ssid) || reply.Ssid.Length > 32 || reply.Passphrase is not { Length: >= 8 and <= 63 } || reply.Port is <= 0 or > 65535)
            throw new DirectLinkException(L.T("{0} didn't start its network.", peer.Name));
        var network = new DirectNetwork(reply.Ssid, reply.Passphrase)
        {
            HostIsComputer = true,
            SharesInternet = reply.SharesInternet,
            HostAddress = IPAddress.TryParse(reply.Address, out var offeredHost) ? offeredHost : null,
        };

        Move(session, DirectSessionState.OfferingCredentials);
        Move(session, DirectSessionState.Joining);
        Log.Info($"Direct: {session}: joining {peer.Name}'s network {network.Ssid}{(network.SharesInternet ? " (it shares its internet)" : "")}");
        IPAddress? host;
        await _receiverJoinGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            _linkedPeers[ReceiverJoinKey(send.Key)] = 0; // this device's link is this send's now (nobody starts a network meanwhile)
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(token);
            wait.CancelAfter(JoinWait);
            try
            {
                host = await _link!.JoinAsync(network, wait.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                throw new DirectLinkException(L.T("This device didn't manage to join {0}'s network in time.", peer.Name));
            }
        }
        finally
        {
            _receiverJoinGate.Release();
        }

        HostedNetwork = null;
        DirectAddresses.Invalidate();
        host ??= network.HostAddress; // iOS can't read the gateway: the address the receiver gave
        if (host == null)
            throw new DirectLinkException(L.T("This device joined {0}'s network but couldn't find it there.", peer.Name));
        if (!_transfers.IsDirectAddress(host) && OwnAddressNear(host) is { } ownThere) DirectAddresses.MarkDirectNetwork(ownThere, host);
        session.OnReceiversNetwork = true;
        Log.Info($"Direct: {session}: on {network.Ssid} at {OwnAddressNear(host)?.ToString() ?? "?"}; {peer.Name} at {host} (Beam on port {reply.Port})");
        return new IPEndPoint(host, reply.Port);
    }

    /// <summary>
    /// Before this device starts its own network: it leaves a receiver's network it joined (one adapter; and a phone
    /// on another network can't reach its own network's guests). Transfers that ran over it are set up again.
    /// </summary>
    private async Task LeaveReceiversNetworksAsync(string why, CancellationToken token)
    {
        if (!_linkedPeers.Keys.Any(k => k.StartsWith(ReceiverJoinPrefix, StringComparison.Ordinal))) return;
        await _receiverJoinGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var keys = _linkedPeers.Keys.Where(k => k.StartsWith(ReceiverJoinPrefix, StringComparison.Ordinal)).ToList();
            if (keys.Count == 0) return;
            Log.Info($"Direct: leaving the receiver's network ({why})");
            foreach (var key in keys) _linkedPeers.TryRemove(key, out _);
            foreach (var other in _sends.Values.Where(s => s.Session.OnReceiversNetwork).ToList())
            {
                other.ReceiversNetworkEndpoint = null;
                other.LinkLost = true; // left on purpose: no use asking over it
                if (other.Session.State is DirectSessionState.Verified or DirectSessionState.Transferring && other.Transfer?.InterruptConnection(switchingPath: false) == true)
                    Log.Info($"Direct: {other.Session}: its link was on that network; setting it up again");
            }

            StopLinkIfUnused();
        }
        finally
        {
            _receiverJoinGate.Release();
        }
    }

    /// <summary>The send is over: this device leaves the receiver's network it joined for it (unless something else uses the link).</summary>
    private void ReleaseReceiversNetwork(DirectSend send)
    {
        if (!_linkedPeers.TryRemove(ReceiverJoinKey(send.Key), out _)) return;
        send.ReceiversNetworkEndpoint = null;
        Log.Info($"Direct: no transfer needs {send.Receiver.Name}'s network any more; leaving it");
        StopLinkIfUnused();
    }

    /// <summary>The receiver's network this device joined went away: transfers on it are set up again right away.</summary>
    private void OnReceiversNetworkLost()
    {
        foreach (var send in _sends.Values.Where(s => s.Session.OnReceiversNetwork && _linkedPeers.ContainsKey(ReceiverJoinKey(s.Key))).ToList())
        {
            send.ReceiversNetworkEndpoint = null;
            send.LinkLost = true;
            if (send.Session.State is DirectSessionState.Verified or DirectSessionState.Transferring && send.Transfer?.InterruptConnection(switchingPath: false) == true)
                Log.Info($"Direct: {send.Session}: this device lost {send.Receiver.Name}'s network; setting the link up again");
        }
    }

    /// <summary>
    /// The control channel for setting up: Beam's authenticated connection on the shared network when the receiver
    /// answers there, else Bluetooth. Either way the receiver is asked (propose) before anything starts.
    /// </summary>
    private async Task<IDirectControl> OpenControlAsync(DirectSession session, DeviceInfo peer, string? reconnectKey, CancellationToken token,
        IReadOnlyList<IPEndPoint>? onItsNetwork = null)
    {
        var propose = new DirectRequestMessage
        {
            Action = DirectActions.Propose,
            SessionId = session.Id,
            ReceiverId = peer.Id,
            SenderId = _selfId(),
            SenderName = _settings.Current.DeviceName,
            SenderFingerprint = _transfers.Identity.Fingerprint,
        };
        if (reconnectKey != null) propose.Proof = ReconnectProof.ForPropose(reconnectKey, propose);
        var expected = ExpectedFingerprint(peer);
        var lan = (onItsNetwork ?? Array.Empty<IPEndPoint>()).Concat(peer.Endpoints.Where(e => !_transfers.IsDirectAddress(e.Address))).ToList();
        if (lan.Count > 0)
        {
            try
            {
                var (reply, fingerprint) = await RequestAsync(lan, expected, peer.Name, propose, ProposeTimeout, token).ConfigureAwait(false);
                if (!reply.Ok)
                    throw new DirectLinkException(reply.Message ?? L.T("{0} didn't accept the direct connection.", peer.Name), declined: reply.Declined);
                Log.Info($"Direct: {session}: {peer.Name} accepted over the network");
                return new LanControl(this, lan, fingerprint, peer.Name);
            }
            catch (TransferException ex) when (ex.Error.Kind == TransferErrorKind.IdentityMismatch)
            {
                throw new DirectLinkException(ex.Error.Message); // someone else answers there: not trying another way
            }
            catch (Exception ex) when (ex is TransferException or IOException or System.Net.Sockets.SocketException or TimeoutException
                                           or System.Security.Authentication.AuthenticationException && !token.IsCancellationRequested)
            {
                Log.Info($"Direct: {session}: {peer.Name} isn't reachable over the network ({ex.Message}); trying Bluetooth");
            }
        }

        if (Bluetooth is { } bluetooth && await bluetooth.CanReachAsync(peer.Id, BluetoothSightingWait, token).ConfigureAwait(false))
        {
            var control = await bluetooth.OpenAsync(peer, expected, propose, token).ConfigureAwait(false);
            Log.Info($"Direct: {session}: {peer.Name} accepted over Bluetooth");
            return control;
        }

        throw new DirectLinkException(lan.Count > 0
            ? L.T("Couldn't reach {0} over the network or Bluetooth to set up the direct connection.", peer.Name)
            : L.T("{0} isn't reachable over the network or Bluetooth right now. Open Receive on it and try again.", peer.Name), transient: true);
    }

    private sealed class LanControl : IDirectControl
    {
        private readonly DirectManager _owner;
        private readonly IReadOnlyList<IPEndPoint> _endpoints;
        private readonly string _name;

        public LanControl(DirectManager owner, IReadOnlyList<IPEndPoint> endpoints, string fingerprint, string name)
        {
            _owner = owner;
            _endpoints = endpoints;
            PeerFingerprint = fingerprint;
            _name = name;
        }

        public DirectControlChannel Channel => DirectControlChannel.Lan;

        public string PeerFingerprint { get; }

        public async Task OfferAsync(DirectRequestMessage offer, Action delivered, CancellationToken cancellationToken)
        {
            var (reply, _) = await _owner.RequestAsync(_endpoints, PeerFingerprint, _name, offer, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            if (!reply.Ok) throw new DirectLinkException(reply.Message ?? L.T("{0} couldn't join the direct network.", _name), declined: reply.Declined);
            delivered(); // it joins now; it reports in over the direct network
        }

        /// <summary>One request and its answer (this channel is Beam's TLS connection: the receiver's certificate is checked).</summary>
        public async Task<DirectResponseMessage> AskAsync(DirectRequestMessage request, TimeSpan wait, CancellationToken cancellationToken) =>
            (await _owner.RequestAsync(_endpoints, PeerFingerprint, _name, request, wait, cancellationToken).ConfigureAwait(false)).Reply;

        public async Task TellAsync(DirectRequestMessage message)
        {
            try
            {
                using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                await _owner.RequestAsync(_endpoints, PeerFingerprint, _name, message, TimeSpan.FromSeconds(8), limit.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Info($"Direct: couldn't tell {_name} ({message.Action}) over the network: {ex.Message}");
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>
    /// A share of this device's direct network for <paramref name="session"/>: started on the first one (after Send,
    /// with a fresh passphrase), shared by the sessions that follow while it's on. Refused while the link is someone
    /// else's (a legacy peer, this device being on another device's network).
    /// </summary>
    private async Task<DirectNetwork> AcquireHostAsync(string owner, string session, bool forPhone, bool forComputer, CancellationToken token)
    {
        Task<DirectNetwork> start;
        lock (_hostGate)
        {
            // Started earlier, but gone since (the OS or driver stopped it): start it again rather than offer a dead network.
            if (_sessionHostStart is { IsCompletedSuccessfully: true } && !HostNetworkReady())
            {
                Log.Warn($"Direct: this device's network for direct sessions is gone; starting it again. Adapters: {DirectAddresses.Describe()}");
                _sessionHostStart = null;
                HostedNetwork = null;
            }

            if (!_hostLeases.Contains(owner) || _sessionHostStart == null || _sessionHostStart.IsFaulted || _sessionHostStart.IsCanceled)
            {
                if (_sessionHostStart == null || _sessionHostStart.IsFaulted || _sessionHostStart.IsCanceled)
                {
                    // Nobody else may be using the radio: starting would end their network (one Wi-Fi adapter).
                    var others = _linkedPeers.Keys.Where(k => !k.StartsWith("session:", StringComparison.Ordinal)).ToList();
                    if (others.Count > 0 || !_joinedReceivers.IsEmpty)
                    {
                        Log.Info($"Direct: not starting a network for {session}: the link is in use ({string.Join(", ", others.Concat(_joinedReceivers.Keys))})");
                        throw new DirectLinkException(L.T("This device's direct connection is in use right now. Try again when it's done."));
                    }

                    _sessionHostStart = StartSessionHostAsync(forPhone, forComputer);
                }

                _hostLeases.Add(owner);
                _linkedPeers[LeaseKey(owner)] = 0;
            }

            start = _sessionHostStart!;
        }

        try
        {
            return await start.WaitAsync(token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A start that failed or was given up is retried by the next setup; the share itself is given back when the
            // transfer is over (or the user moves it to the network), not here: a recovering transfer still needs it.
            if (ex is not OperationCanceledException) lock (_hostGate) if (ReferenceEquals(_sessionHostStart, start) && start.IsCompleted) _sessionHostStart = null;
            throw;
        }
    }

    /// <param name="forPhone">A phone joins first: this device's internet-sharing network if it can (later receivers join the same).</param>
    /// <param name="forComputer">A computer joins first: this phone's hotspot if it can run one (later receivers join the same).</param>
    private async Task<DirectNetwork> StartSessionHostAsync(bool forPhone, bool forComputer)
    {
        await Task.Yield(); // started under the lock: run outside it
        try
        {
            await PrepareAsync().ConfigureAwait(false);
            // The same name as always (so a phone that approved it before may connect without asking again), with a
            // passphrase made for these sessions: it is useless once the network goes off.
            var fresh = DirectNetwork.ForSession(OwnNetwork().Ssid);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
            limit.CancelAfter(HostStartTimeout);
            DirectNetwork started;
            try
            {
                if (_link is IInternetSharing sharing) sharing.PreferInternetSharing = forPhone;
                if (_link is IAccessPointHosting accessPoint) accessPoint.PreferAccessPoint = forComputer;
                Log.Info($"Direct: starting {fresh.Ssid} for direct sessions{(forPhone && _link is IInternetSharing ? " (a phone joins: sharing this device's internet if it can)" : "")}");
                started = await _link!.HostAsync(fresh, limit.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!_stopping.IsCancellationRequested)
            {
                throw new DirectLinkException(L.T("This device's Wi-Fi didn't start the direct network in time. Make sure Wi-Fi is on."));
            }

            // "Started" is not "usable": wait for this device's address on it.
            var until = Environment.TickCount64 + (long)HostReadyTimeout.TotalMilliseconds;
            while (!HostNetworkReady())
            {
                if (Environment.TickCount64 > until)
                {
                    Log.Warn($"Direct: {started.Ssid} said it started, but has no address after {HostReadyTimeout.TotalSeconds:0} s. Adapters: {DirectAddresses.Describe()}");
                    StopSessionHost();
                    throw new DirectLinkException(L.T("The direct network said it started, but it isn't usable. Turn Wi-Fi off and on, then try again."));
                }

                await Task.Delay(250, _stopping.Token).ConfigureAwait(false);
                DirectAddresses.Invalidate();
            }

            bool unused;
            lock (_hostGate)
            {
                unused = _hostLeases.Count == 0;
                if (unused) _sessionHostStart = null;
            }

            HostedNetwork = started;
            DirectAddresses.Invalidate();
            Changed?.Invoke();
            if (unused)
            {
                Log.Info($"Direct: {started.Ssid} came up after every session let go of it; stopping it");
                StopLinkIfUnused();
            }
            else
            {
                Log.Info($"Direct: {started.Ssid} is on for direct sessions{(started.SharesInternet ? ", sharing this device's internet" : "")}" +
                         $"{(started.IsAccessPoint ? " (this phone's hotspot)" : "")}");
            }

            return started;
        }
        catch
        {
            lock (_hostGate) _sessionHostStart = null;
            throw;
        }
    }

    /// <summary>The network started for sessions turned out unusable: off again (it is this device's own, started just now).</summary>
    private void StopSessionHost()
    {
        try
        {
            _link?.Stop();
        }
        catch (Exception ex)
        {
            Log.Warn("Direct: stopping the unusable network failed", ex);
        }

        HostedNetwork = null;
    }

    private bool HoldsHost(string owner)
    {
        lock (_hostGate) return _hostLeases.Contains(owner);
    }

    /// <summary>Gives back <paramref name="session"/>'s share; the network goes off once no session (or anyone else) uses it.</summary>
    private void ReleaseHost(string owner)
    {
        bool stop;
        lock (_hostGate)
        {
            if (!_hostLeases.Remove(owner)) return;
            _linkedPeers.TryRemove(LeaseKey(owner), out _);
            stop = _hostLeases.Count == 0 && _sessionHostStart is { IsCompleted: true };
            if (stop) _sessionHostStart = null;
        }

        if (stop)
        {
            Log.Info("Direct: no direct session needs this device's network any more");
            StopLinkIfUnused();
        }
    }

    /// <summary>
    /// Over the direct network: a TLS connection to the endpoint the receiver reported from, checked against its
    /// certificate, leaving from this device's own address there, and answered for this session and nonce.
    /// </summary>
    /// <returns>The receiver's reconnect key, if it gave one (older versions don't).</returns>
    private async Task<string?> VerifyAsync(DirectSession session, IPEndPoint endpoint, CancellationToken token, TimeSpan retryFor = default)
    {
        // Just joined the receiver's network: its address there may need a moment.
        var until = Environment.TickCount64 + (long)retryFor.TotalMilliseconds;
        while (true)
        {
            try
            {
                return await VerifyOnceAsync(session, endpoint, token).ConfigureAwait(false);
            }
            catch (DirectLinkException ex) when (ex.Transient && Environment.TickCount64 < until && !token.IsCancellationRequested)
            {
                await Task.Delay(500, token).ConfigureAwait(false);
            }
        }
    }

    private async Task<string?> VerifyOnceAsync(DirectSession session, IPEndPoint endpoint, CancellationToken token)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(VerifyTimeout);
        try
        {
            var (reply, _) = await ExchangeOnDirectAsync(session, endpoint,
                new DirectRequestMessage { Action = DirectActions.Verify, SessionId = session.Id, Nonce = session.Nonce }, limit.Token).ConfigureAwait(false);
            if (!reply.Ok) throw new DirectLinkException(reply.Message ?? L.T("{0} couldn't confirm the direct connection.", session.PeerName));
            return ReconnectProof.IsKey(reply.ReconnectKey) ? reply.ReconnectKey : null;
        }
        catch (TransferException ex) when (ex.Error.Kind == TransferErrorKind.IdentityMismatch)
        {
            throw new DirectLinkException(ex.Error.Message);
        }
        catch (Exception ex) when (ex is TransferException or IOException or System.Net.Sockets.SocketException
                                       || (ex is OperationCanceledException && limit.IsCancellationRequested && !token.IsCancellationRequested))
        {
            throw new DirectLinkException(L.T("Beam couldn't reach {0} over the direct network. Check that Beam is allowed through the firewall.", session.PeerName), ex,
                transient: true);
        }
    }

    private async Task<(DirectResponseMessage Reply, IPEndPoint Remote)> ExchangeOnDirectAsync(DirectSession session, IPEndPoint endpoint,
        DirectRequestMessage request, CancellationToken token)
    {
        if (!_transfers.IsDirectAddress(endpoint.Address))
            throw new DirectLinkException(L.T("{0} answered from outside the direct network.", session.PeerName));
        await using var connection = await SecureTransport.ConnectAsync(_transfers.Identity, _transfers.CreateHello(ConnectionPurpose.Direct),
            new[] { endpoint }, session.PeerFingerprint, session.PeerName, token, LocalAddressFor).ConfigureAwait(false);
        if (!connection.RemoteEndPoint.Address.Equals(endpoint.Address) || !_transfers.IsDirectAddress(connection.RemoteEndPoint.Address))
            throw new DirectLinkException(L.T("{0} answered from outside the direct network.", session.PeerName));
        await connection.Channel.SendAsync(FrameType.DirectRequest, request, token).ConfigureAwait(false);
        var reply = await connection.Channel.ReadMessageAsync<DirectResponseMessage>(FrameType.DirectResponse, VerifyTimeout, token).ConfigureAwait(false);
        return (reply, connection.RemoteEndPoint);
    }

    /// <summary>After a dropped connection: whether the receiver still confirms the session on the direct network.</summary>
    private async Task<bool> StillVerifiedAsync(DirectSend send, DirectSession session, CancellationToken token)
    {
        if (session.VerifiedEndpoint is not { } endpoint) return false;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(TimeSpan.FromSeconds(6));
        try
        {
            var (reply, _) = await ExchangeOnDirectAsync(session, endpoint,
                new DirectRequestMessage { Action = DirectActions.Verify, SessionId = session.Id, Nonce = session.Nonce }, limit.Token).ConfigureAwait(false);
            if (reply.Ok && ReconnectProof.IsKey(reply.ReconnectKey)) send.ReconnectKey = reply.ReconnectKey; // each verify gives a new one
            return reply.Ok;
        }
        catch (Exception ex) when (!token.IsCancellationRequested)
        {
            Log.Info($"Direct: {session}: no longer confirmed at {endpoint} ({ex.Message})");
            return false;
        }
    }

    /// <summary>
    /// Lets go of what <paramref name="session"/> set up (once): tells the receiver it is over (over the direct network
    /// if verified, else the control channel). This device's network stays: it belongs to the transfer.
    /// </summary>
    private async Task EndOutgoingAsync(DirectSession session, bool notify)
    {
        if (!_outgoing.TryGetValue(session.Id, out var outgoing) || Interlocked.Exchange(ref outgoing.Ended, 1) == 1) return;

        _outgoing.TryRemove(new KeyValuePair<string, Outgoing>(session.Id, outgoing));
        outgoing.Ready.TrySetCanceled();
        try
        {
            if (notify)
            {
                var end = new DirectRequestMessage { Action = DirectActions.End, SessionId = session.Id, Nonce = session.Nonce, SenderFingerprint = _transfers.Identity.Fingerprint };
                var told = false;
                if (session.VerifiedEndpoint is { } endpoint)
                {
                    try
                    {
                        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                        told = (await ExchangeOnDirectAsync(session, endpoint, end, limit.Token).ConfigureAwait(false)).Reply.Ok;
                    }
                    catch (Exception ex)
                    {
                        Log.Info($"Direct: {session}: couldn't say it ended over the direct network: {ex.Message}");
                    }
                }

                if (!told && outgoing.Control != null) await outgoing.Control.TellAsync(end).ConfigureAwait(false);
            }
        }
        finally
        {
            if (outgoing.Control != null) await outgoing.Control.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>The transfer moved on: the session follows (transferring, interrupted, or over), and lets go when it's over.</summary>
    private void OnTransferChanged(DirectSend send)
    {
        if (send.Transfer is not { } transfer) return;
        if (transfer.IsFinished) _ = FinishSendAsync(send);
        if (send.Mode != ConnectionMethod.Direct) return;
        var session = send.Session;
        switch (transfer.State)
        {
            case TransferState.Transferring when transfer.Path == TransferPath.Direct:
                session.TryMove(DirectSessionState.Transferring);
                break;
            case TransferState.Reconnecting when session.State == DirectSessionState.Transferring:
                session.TryMove(DirectSessionState.Interrupted);
                break;
            case TransferState.Completed or TransferState.CompletedWithErrors:
                session.TryMove(DirectSessionState.Completed);
                break;
            case TransferState.Cancelled or TransferState.Declined:
                session.Cancel();
                break;
            case TransferState.Failed:
                session.Fail(transfer.Error?.Message ?? L.T("Something went wrong."));
                break;
        }

        if (transfer.IsFinished && !transfer.CanResume) _sends.TryRemove(transfer.Id, out _);
    }

    /// <summary>The transfer is over (done, cancelled, or failed for now): its session ends, and its share of this device's network goes.</summary>
    private async Task FinishSendAsync(DirectSend send)
    {
        try
        {
            await EndOutgoingAsync(send.Session, notify: true).ConfigureAwait(false);
        }
        finally
        {
            ReleaseHost(send.Key);
            ReleaseReceiversNetwork(send);
        }
    }

    /// <summary>
    /// The user switched a running send: to the shared network (the direct session ends; the transfer resumes over the
    /// network, shown as such), or to Direct (a session is set up while the transfer keeps going over the network, and
    /// it moves over only once verified). False, with <see cref="Problem"/> raised, when that isn't possible.
    /// </summary>
    public async Task<bool> SwitchAsync(TransferSession transfer, ConnectionMethod method, CancellationToken cancellationToken = default)
    {
        if (!_sends.TryGetValue(transfer.Id, out var send) || transfer.IsFinished || send.Mode == method) return false;
        if (method == ConnectionMethod.SameNetwork)
        {
            var device = _discovery.Find(transfer.PeerId);
            if (device == null || !device.Endpoints.Any(e => !_transfers.IsDirectAddress(e.Address)))
            {
                Report(L.T("{0} isn't on this Wi-Fi network, so Beam can't send over it.", transfer.PeerName));
                return false;
            }

            Log.Info($"Direct: the user moved {transfer.Id} to {transfer.PeerName} to the network");
            await send.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                _switchedToNetwork[transfer.Id] = 0;
                send.Mode = ConnectionMethod.SameNetwork;
                var old = send.Session;
                transfer.SetMode(ConnectionMethod.SameNetwork, null);
                transfer.InterruptConnection(switchingPath: true);
                old.Cancel();
                await EndOutgoingAsync(old, notify: true).ConfigureAwait(false);
                ReleaseHost(send.Key);
                ReleaseReceiversNetwork(send);
            }
            finally
            {
                send.Gate.Release();
            }

            return true;
        }

        var session = NewSession(send.Receiver);
        Log.Info($"Direct: the user moved {transfer.Id} to {transfer.PeerName} to a direct connection; setting it up meanwhile");
        await send.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, transfer.CancellationToken);
                // Not on the receiver's network: joining it would take this device off the network the transfer runs over meanwhile.
                await EstablishAsync(session, send, linked.Token, receiversNetworkOk: false).ConfigureAwait(false);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                ReleaseHost(send.Key);
                Report(ex is TransferException te ? te.Error.Message : ex.Message);
                return false; // still over the network, as it was
            }

            if (transfer.IsFinished)
            {
                session.Cancel();
                await EndOutgoingAsync(session, notify: true).ConfigureAwait(false);
                ReleaseHost(send.Key);
                return false;
            }

            send.Session = session;
            send.Mode = ConnectionMethod.Direct;
            send.ReconnectsWithoutProgress = 0;
            _switchedToNetwork.TryRemove(transfer.Id, out _);
            transfer.SetMode(ConnectionMethod.Direct, session);
            transfer.InterruptConnection(switchingPath: true);
            return true;
        }
        finally
        {
            send.Gate.Release();
        }
    }

    // ---- Messages to the sender (over the direct network or the control channel) ----

    private DirectResponseMessage HandleReady(DirectRequestMessage request, PeerConnection connection)
    {
        if (!TryGetOutgoing(request, connection.RemoteFingerprint, out var outgoing, out var refusal)) return refusal!;
        var session = outgoing.Session;
        if (session.State is not (DirectSessionState.OfferingCredentials or DirectSessionState.Joining))
            return Refuse($"{session}: ready while {session.State}", "Not expecting that now.");
        if (!_transfers.IsDirectAddress(connection.RemoteEndPoint.Address))
            return Refuse($"{session}: ready from {connection.RemoteEndPoint}, outside the direct network", L.T("{0} answered from outside the direct network.", session.PeerName));
        if (request.Port is <= 0 or > 65535) return Refuse($"{session}: ready with port {request.Port}", "Bad port.");
        session.TryMove(DirectSessionState.Joining);
        var endpoint = new IPEndPoint(connection.RemoteEndPoint.Address, request.Port);
        Log.Info($"Direct: {session}: {session.PeerName} reported in from {connection.RemoteEndPoint} (Beam on port {request.Port})");
        outgoing.Ready.TrySetResult(endpoint);
        return new DirectResponseMessage { Ok = true };
    }

    private DirectResponseMessage HandleReceiverFailed(DirectRequestMessage request, string fingerprint)
    {
        if (!TryGetOutgoing(request, fingerprint, out var outgoing, out var refusal)) return refusal!;
        var message = string.IsNullOrWhiteSpace(request.Message) ? L.T("{0} couldn't join the direct network.", outgoing.Session.PeerName) : Trim(request.Message);
        Log.Warn($"Direct: {outgoing.Session}: the receiver reports it failed ({request.Stage}): {message}");
        outgoing.Ready.TrySetException(new DirectLinkException(message) { JoinFailed = request.Stage == "join" });
        return new DirectResponseMessage { Ok = true };
    }

    private DirectResponseMessage HandleReceiverEnded(Outgoing outgoing, DirectRequestMessage request)
    {
        Log.Info($"Direct: {outgoing.Session}: the receiver ended it ({Trim(request.Message ?? "")}, stage {request.Stage ?? "-"})");
        if (outgoing.Session.State is DirectSessionState.Verified or DirectSessionState.Transferring
            && _sends.Values.FirstOrDefault(s => ReferenceEquals(s.Session, outgoing.Session))?.Transfer is { } transfer
            && transfer.InterruptConnection(switchingPath: false))
            Log.Info($"Direct: {outgoing.Session}: dropped the transfer's connection, which ran over the lost link; setting it up again");
        outgoing.Ready.TrySetException(new DirectLinkException(string.IsNullOrWhiteSpace(request.Message)
            ? L.T("{0} ended the direct connection.", outgoing.Session.PeerName)
            : Trim(request.Message)));
        return new DirectResponseMessage { Ok = true };
    }

    /// <summary>The session a message to the sender is about, if it comes from that session's receiver with its nonce.</summary>
    private bool TryGetOutgoing(DirectRequestMessage request, string fingerprint, out Outgoing outgoing, out DirectResponseMessage? refusal)
    {
        refusal = null;
        if (request.SessionId == null || !_outgoing.TryGetValue(request.SessionId, out outgoing!))
        {
            outgoing = null!;
            refusal = Refuse($"{request.Action} for unknown session {Short(request.SessionId)}", "Unknown session.");
            return false;
        }

        var session = outgoing.Session;
        if (!string.Equals(fingerprint, session.PeerFingerprint, StringComparison.OrdinalIgnoreCase))
        {
            refusal = Refuse($"{session}: {request.Action} from another certificate", "Not the device this session is with.");
            return false;
        }

        if (!string.Equals(request.Nonce, session.Nonce, StringComparison.Ordinal))
        {
            refusal = Refuse($"{session}: {request.Action} with a stale or wrong nonce", "Stale session.");
            return false;
        }

        return true;
    }

    private static DirectResponseMessage Refuse(string log, string message)
    {
        Log.Warn($"Direct: refused: {log}");
        return new DirectResponseMessage { Ok = false, Message = message };
    }

    private static string Short(string? id) => string.IsNullOrEmpty(id) ? "-" : id.Length > 8 ? id[..8] : id;

    private static string Trim(string text) => text.Length > 300 ? text[..300] : text;
}
