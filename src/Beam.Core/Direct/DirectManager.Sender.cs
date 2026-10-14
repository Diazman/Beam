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
    /// <summary>The device's beacon was heard just now (Bluetooth on and allowed).</summary>
    bool CanReach(string deviceId);

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

    /// <summary>How often a session whose verified link dropped sets up again before the transfer fails.</summary>
    internal static int MaxReconnects { get; set; } = 2;

    /// <summary>
    /// Whether the network this device started is usable (an address on it): a platform saying "started" isn't enough
    /// (replaceable in tests).
    /// </summary>
    internal Func<bool> HostNetworkReady { get; set; } = DirectAddresses.HasHostedDirectNetwork;

    /// <summary>Set by the nearby service (null without Bluetooth).</summary>
    internal IBluetoothDirectControl? Bluetooth { get; set; }

    private readonly ConcurrentDictionary<string, Outgoing> _outgoing = new(); // by session id on the wire
    private readonly ConcurrentDictionary<string, DirectSend> _sends = new(); // by transfer id
    private readonly object _hostGate = new();
    private readonly HashSet<DirectSession> _hostLeases = new();
    private Task<DirectNetwork>? _sessionHostStart;
    private DirectNetwork? _sessionNetwork;

    /// <summary>One send: the transfer, its mode, and (Direct) the session it may send over.</summary>
    private sealed class DirectSend
    {
        public required DeviceInfo Receiver { get; init; }

        public required DirectSession Session { get; set; }

        public ConnectionMethod Mode { get; set; }

        public TransferSession? Transfer { get; set; }
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
        CanHost && peer.DirectRoles.HasFlag(DirectRoles.SenderHosted) && (peer.DirectRoles & (DirectRoles.Join | DirectRoles.JoinComputers)) != 0;

    /// <summary>Whether an address is on a direct network (as the transfer service tells it).</summary>
    public bool IsDirectAddress(IPAddress address) => _transfers.IsDirectAddress(address);

    /// <summary>Tests: no direct session (sending or receiving) is set up or holds the link.</summary>
    internal bool IsIdleForTests => _outgoing.IsEmpty && _incoming.IsEmpty && !LinkInUseBySessions();

    internal IDirectLink? LinkForTests => _link;

    private static string LeaseKey(DirectSession session) => "session:" + session.Key;

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

    private DirectSession NewSession(DeviceInfo receiver) => new(receiver.Id, receiver.Name, SessionLifetime);

    /// <summary>This device's address on the direct network that <paramref name="endpoint"/> is on (null when it isn't on one).</summary>
    private IPAddress? LocalAddressFor(IPEndPoint endpoint) =>
        _transfers.IsDirectAddress(endpoint.Address) ? OwnAddressNear(endpoint.Address) : null;

    /// <summary>
    /// Before each connection of a Direct transfer: the session is verified (set up on the first attempt; checked again
    /// after a drop, and set up again a bounded number of times when the link is gone). Throws
    /// <see cref="TransferException"/> (<see cref="TransferErrorKind.DirectFailed"/>) naming the stage that failed.
    /// </summary>
    private async Task PrepareDirectAsync(DirectSend send, CancellationToken token)
    {
        var session = send.Session;
        if (session.IsFinished)
        {
            // Tried again after a failure (or after the session ended): a new session.
            session = send.Session = NewSession(send.Receiver);
            send.Transfer?.SetMode(ConnectionMethod.Direct, session);
        }

        if (session.State == DirectSessionState.Negotiating)
        {
            await EstablishAsync(session, send.Receiver, token).ConfigureAwait(false);
            return;
        }

        if (session.State is DirectSessionState.Verified or DirectSessionState.Transferring or DirectSessionState.Interrupted
            && await StillVerifiedAsync(session, token).ConfigureAwait(false))
            return;

        // The verified link is gone (the receiver left the network, or it went out of range).
        session.TryMove(DirectSessionState.Interrupted);
        if (session.Reconnects >= MaxReconnects)
        {
            session.Fail(L.T("The direct connection to {0} was lost and couldn't be set up again.", session.PeerName));
            await EndOutgoingAsync(session, notify: true).ConfigureAwait(false);
            throw Failure(session);
        }

        Log.Info($"Direct: the link to {session.PeerName} was lost; setting the session up again ({session.Reconnects + 1} of {MaxReconnects})");
        await EndOutgoingAsync(session, notify: true, keepHost: true).ConfigureAwait(false);
        session.Restart();
        await EstablishAsync(session, send.Receiver, token).ConfigureAwait(false);
    }

    /// <summary>Why a session failed, as the transfer reports it ("Direct connection failed at receiver join: …").</summary>
    private static TransferException Failure(DirectSession session) =>
        new(TransferErrorKind.DirectFailed,
            L.T("Direct connection failed at {0}: {1}", DirectSession.StageName(session.FailedAt ?? DirectSessionState.Negotiating),
                session.Error ?? L.T("Something went wrong.")),
            $"{session}, failed at {session.FailedAt}");

    /// <summary>
    /// Sets <paramref name="session"/> up, from proposing to verified. Throws <see cref="TransferException"/> naming the
    /// stage that failed (the session is then failed and what it set up is let go), or
    /// <see cref="OperationCanceledException"/> when the transfer or session was cancelled.
    /// </summary>
    private async Task EstablishAsync(DirectSession session, DeviceInfo peer, CancellationToken token)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token, session.CancellationToken, _stopping.Token);
        limit.CancelAfter(SessionLifetime);
        var outgoing = new Outgoing { Session = session };
        _outgoing[session.Id] = outgoing;
        try
        {
            var current = _discovery.Find(peer.Id) ?? peer;
            if (_link == null || !CanHost) throw new DirectLinkException(L.T("This device can't start a direct connection."));
            if (!CanSendDirectlyTo(current))
                throw new DirectLinkException(L.T("{0} can't join a direct connection from this device. Update Beam on it; phones need Android 10 or newer.", peer.Name));
            if (_outgoing.Values.Count(o => o.Session != session && !o.Session.IsFinished && Volatile.Read(ref o.Ended) == 0) >= MaxDirectReceivers)
                throw new DirectLinkException(L.T("Beam connects directly to at most {0} devices at a time. Send to the others when these are done.", MaxDirectReceivers));

            // 1. Who is it, and does it accept? (Nothing is started before it says yes.)
            Log.Info($"Direct: {session}: negotiating (roles {DirectRolesText.Format(current.DirectRoles)}; addresses {string.Join(", ", current.Endpoints)})");
            outgoing.Control = await OpenControlAsync(session, current, limit.Token).ConfigureAwait(false);
            session.Channel = outgoing.Control.Channel;
            session.PeerFingerprint = outgoing.Control.PeerFingerprint;

            // 2. This device's network, now that someone will join it.
            Move(session, DirectSessionState.Hosting);
            var network = await AcquireHostAsync(session, limit.Token).ConfigureAwait(false);

            // 3. Its details, for this receiver only, over the authenticated control channel.
            Move(session, DirectSessionState.OfferingCredentials);
            Log.Info($"Direct: {session}: offering {network.Ssid} over {session.Channel}");
            await outgoing.Control.OfferAsync(new DirectRequestMessage
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
                HostIsComputer = _selfIsComputer,
                Port = _transfers.Port,
            }, () => session.TryMove(DirectSessionState.Joining), limit.Token).ConfigureAwait(false);
            session.TryMove(DirectSessionState.Joining);

            // 4. The receiver reports in over the direct network itself (not just "joined" on the control channel).
            Log.Info($"Direct: {session}: waiting for {peer.Name} to join");
            IPEndPoint reported;
            try
            {
                reported = await outgoing.Ready.Task.WaitAsync(JoinWait, limit.Token).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                throw new DirectLinkException(L.T("{0} didn't join the direct network in time. If it asks to connect to a Wi-Fi network, tap Connect there.", peer.Name));
            }

            // 5. Both ends of the route, the session and the receiver's certificate, before any file data.
            Move(session, DirectSessionState.Verifying);
            await VerifyAsync(session, reported, limit.Token).ConfigureAwait(false);
            if (!session.MarkVerified(reported)) throw new OperationCanceledException(limit.Token);
            Log.Info($"Direct: {session}: verified at {reported}");
        }
        catch (Exception ex) when (ex is OperationCanceledException && (token.IsCancellationRequested || session.CancellationToken.IsCancellationRequested
                                                                        || _stopping.IsCancellationRequested))
        {
            Log.Info($"Direct: {session}: cancelled at {session.State}");
            session.Cancel();
            await EndOutgoingAsync(session, notify: true).ConfigureAwait(false);
            throw;
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
    /// The control channel for setting up: Beam's authenticated connection on the shared network when the receiver
    /// answers there, else Bluetooth. Either way the receiver is asked (propose) before anything starts.
    /// </summary>
    private async Task<IDirectControl> OpenControlAsync(DirectSession session, DeviceInfo peer, CancellationToken token)
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
        var expected = ExpectedFingerprint(peer);
        var lan = peer.Endpoints.Where(e => !_transfers.IsDirectAddress(e.Address)).ToList();
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

        if (Bluetooth is { } bluetooth && bluetooth.CanReach(peer.Id))
        {
            var control = await bluetooth.OpenAsync(peer, expected, propose, token).ConfigureAwait(false);
            Log.Info($"Direct: {session}: {peer.Name} accepted over Bluetooth");
            return control;
        }

        throw new DirectLinkException(lan.Count > 0
            ? L.T("Couldn't reach {0} over the network or Bluetooth to set up the direct connection.", peer.Name)
            : L.T("{0} isn't reachable over the network or Bluetooth right now. Open Receive on it and try again.", peer.Name));
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
    /// else's (the Phone page, this device being on another device's network).
    /// </summary>
    private async Task<DirectNetwork> AcquireHostAsync(DirectSession session, CancellationToken token)
    {
        Task<DirectNetwork> start;
        lock (_hostGate)
        {
            if (!_hostLeases.Contains(session))
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

                    _sessionHostStart = StartSessionHostAsync();
                }

                _hostLeases.Add(session);
                _linkedPeers[LeaseKey(session)] = 0;
            }

            start = _sessionHostStart!;
        }

        try
        {
            return await start.WaitAsync(token).ConfigureAwait(false);
        }
        catch
        {
            ReleaseHost(session);
            throw;
        }
    }

    private async Task<DirectNetwork> StartSessionHostAsync()
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
                Log.Info($"Direct: starting {fresh.Ssid} for direct sessions");
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
                else _sessionNetwork = started;
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
                Log.Info($"Direct: {started.Ssid} is on for direct sessions");
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

    /// <summary>Gives back <paramref name="session"/>'s share; the network goes off once no session (or anyone else) uses it.</summary>
    private void ReleaseHost(DirectSession session)
    {
        bool stop;
        lock (_hostGate)
        {
            if (!_hostLeases.Remove(session)) return;
            _linkedPeers.TryRemove(LeaseKey(session), out _);
            stop = _hostLeases.Count == 0 && _sessionHostStart is { IsCompleted: true };
            if (_hostLeases.Count == 0 && stop)
            {
                _sessionHostStart = null;
                _sessionNetwork = null;
            }
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
    private async Task VerifyAsync(DirectSession session, IPEndPoint endpoint, CancellationToken token)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(VerifyTimeout);
        try
        {
            var (reply, _) = await ExchangeOnDirectAsync(session, endpoint,
                new DirectRequestMessage { Action = DirectActions.Verify, SessionId = session.Id, Nonce = session.Nonce }, limit.Token).ConfigureAwait(false);
            if (!reply.Ok) throw new DirectLinkException(reply.Message ?? L.T("{0} couldn't confirm the direct connection.", session.PeerName));
        }
        catch (TransferException ex) when (ex.Error.Kind == TransferErrorKind.IdentityMismatch)
        {
            throw new DirectLinkException(ex.Error.Message);
        }
        catch (Exception ex) when (ex is TransferException or IOException or System.Net.Sockets.SocketException
                                       || (ex is OperationCanceledException && limit.IsCancellationRequested && !token.IsCancellationRequested))
        {
            throw new DirectLinkException(L.T("Beam couldn't reach {0} over the direct network. Check that Beam is allowed through the firewall.", session.PeerName), ex);
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
    private async Task<bool> StillVerifiedAsync(DirectSession session, CancellationToken token)
    {
        if (session.VerifiedEndpoint is not { } endpoint) return false;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(TimeSpan.FromSeconds(6));
        try
        {
            var (reply, _) = await ExchangeOnDirectAsync(session, endpoint,
                new DirectRequestMessage { Action = DirectActions.Verify, SessionId = session.Id, Nonce = session.Nonce }, limit.Token).ConfigureAwait(false);
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
    /// if verified, else the control channel), and gives back its share of this device's network.
    /// </summary>
    private async Task EndOutgoingAsync(DirectSession session, bool notify, bool keepHost = false)
    {
        if (!_outgoing.TryGetValue(session.Id, out var outgoing) || Interlocked.Exchange(ref outgoing.Ended, 1) == 1)
        {
            if (!keepHost) ReleaseHost(session);
            return;
        }

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
            if (!keepHost) ReleaseHost(session);
        }
    }

    /// <summary>The transfer moved on: the session follows (transferring, interrupted, or over), and lets go when it's over.</summary>
    private void OnTransferChanged(DirectSend send)
    {
        if (send.Transfer is not { } transfer || send.Mode != ConnectionMethod.Direct) return;
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
                _ = EndOutgoingAsync(session, notify: true);
                break;
            case TransferState.Cancelled or TransferState.Declined:
                session.Cancel();
                _ = EndOutgoingAsync(session, notify: true);
                break;
            case TransferState.Failed:
                session.Fail(transfer.Error?.Message ?? L.T("Something went wrong."));
                _ = EndOutgoingAsync(session, notify: true);
                break;
        }

        if (transfer.IsFinished && !transfer.CanResume) _sends.TryRemove(transfer.Id, out _);
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
            _switchedToNetwork[transfer.Id] = 0;
            send.Mode = ConnectionMethod.SameNetwork;
            var old = send.Session;
            transfer.SetMode(ConnectionMethod.SameNetwork, null);
            transfer.InterruptConnection(switchingPath: true);
            old.Cancel();
            await EndOutgoingAsync(old, notify: true).ConfigureAwait(false);
            return true;
        }

        var session = NewSession(send.Receiver);
        Log.Info($"Direct: the user moved {transfer.Id} to {transfer.PeerName} to a direct connection; setting it up meanwhile");
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, transfer.CancellationToken);
            await EstablishAsync(session, send.Receiver, linked.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            Report(ex is TransferException te ? te.Error.Message : ex.Message);
            return false; // still over the network, as it was
        }

        if (transfer.IsFinished)
        {
            session.Cancel();
            await EndOutgoingAsync(session, notify: true).ConfigureAwait(false);
            return false;
        }

        send.Session = session;
        send.Mode = ConnectionMethod.Direct;
        _switchedToNetwork.TryRemove(transfer.Id, out _);
        transfer.SetMode(ConnectionMethod.Direct, session);
        transfer.InterruptConnection(switchingPath: true);
        return true;
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
        outgoing.Ready.TrySetException(new DirectLinkException(message));
        return new DirectResponseMessage { Ok = true };
    }

    private DirectResponseMessage HandleReceiverEnded(Outgoing outgoing, DirectRequestMessage request)
    {
        Log.Info($"Direct: {outgoing.Session}: the receiver ended it ({Trim(request.Message ?? "")})");
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
