using System.Collections.Concurrent;
using System.Net;
using Beam.Core.Diagnostics;
using Beam.Core.Localization;
using Beam.Core.Protocol;
using Beam.Core.Transfer;

namespace Beam.Core.Direct;

public sealed partial class DirectManager
{
    /// <summary>How long this device's user has to answer "join this sender's direct network?".</summary>
    internal static TimeSpan DirectApprovalTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>After accepting a proposal, how long the sender has to send the network details.</summary>
    internal TimeSpan ProposalLifetime { get; set; } = TimeSpan.FromMinutes(3);

    /// <summary>How long a receiver whose Wi-Fi is off waits for its user to turn it on.</summary>
    internal static TimeSpan WifiOnWait { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>How long joining and reporting in may take before this device gives up and leaves.</summary>
    internal static TimeSpan ReportInTimeout { get; set; } = TimeSpan.FromSeconds(25);

    private readonly ConcurrentDictionary<string, Incoming> _incoming = new(); // by session id
    private readonly ConcurrentDictionary<string, long> _endedSessions = new(); // session ids that may not come back (replays)

    /// <summary>
    /// Per sender certificate: the key it was given on its last verified session, to set a lost link up again without
    /// asking the user (<see cref="ReconnectProof"/>), and until when it counts.
    /// </summary>
    private readonly ConcurrentDictionary<string, (string Key, long Until)> _reconnectKeys = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How long a reconnect key counts after the session it was given on was verified.</summary>
    internal TimeSpan ReconnectKeyLifetime { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>The sender's current reconnect key, if it hasn't expired.</summary>
    private string? ReconnectKeyOf(string senderFingerprint) =>
        _reconnectKeys.TryGetValue(senderFingerprint, out var entry) && Environment.TickCount64 < entry.Until ? entry.Key : null;

    /// <summary>Tests: gives <paramref name="senderFingerprint"/> a reconnect key, as a verified session does.</summary>
    internal string IssueReconnectKeyForTests(string senderFingerprint) => IssueReconnectKey(senderFingerprint);

    private string IssueReconnectKey(string senderFingerprint)
    {
        var key = ReconnectProof.NewKey();
        _reconnectKeys[senderFingerprint] = (key, Environment.TickCount64 + (long)ReconnectKeyLifetime.TotalMilliseconds);
        return key;
    }
    private readonly object _incomingGate = new();

    private enum IncomingStage
    {
        Proposed,
        Accepted,
        Joining,
        Joined,
        Verified,
        Ended,
    }

    /// <summary>A direct session this device takes part in as the receiver (at most one at a time: one Wi-Fi adapter).</summary>
    private sealed class Incoming
    {
        public required string SessionId { get; init; }

        public required string SenderId { get; init; }

        public required string SenderName { get; init; }

        public required string SenderFingerprint { get; init; }

        /// <summary>The sender's identity is only claimed (asked over Bluetooth); the direct network's TLS checks it.</summary>
        public bool ClaimedOnly { get; init; }

        /// <summary>
        /// Proposed with a valid reconnect proof: the key it was made with, which the offer must be proven with too.
        /// </summary>
        public string? ProofKey { get; set; }

        public DirectControlChannel Channel { get; init; }

        /// <summary>Where the sender's Beam is on the shared network (to report a failure there).</summary>
        public IReadOnlyList<IPEndPoint> SenderLan { get; init; } = Array.Empty<IPEndPoint>();

        public IncomingStage Stage { get; set; }

        public string? Nonce { get; set; }

        public long Deadline { get; set; }

        public CancellationTokenSource Cts { get; } = new();

        /// <summary>This session's join put this device on the sender's network (only then does ending it leave).</summary>
        public bool OwnsJoin { get; set; }

        /// <summary>This device started its own network for this session and the sender joins it (<see cref="DirectActions.HostForSender"/>).</summary>
        public bool HostsHere { get; set; }

        public string Key => "rx:" + SessionId;

        public override string ToString() => $"incoming direct session {SessionId[..Math.Min(8, SessionId.Length)]} from {SenderName} ({Stage})";
    }

    private static bool IsSessionAction(string action) => action is DirectActions.Propose or DirectActions.Offer or DirectActions.Ready
        or DirectActions.Verify or DirectActions.End or DirectActions.Failed or DirectActions.HostForSender;

    /// <summary>Session messages that came over Beam's TLS connection (shared network or direct network).</summary>
    private async Task<DirectResponseMessage> HandleSessionRequestAsync(DirectRequestMessage request, PeerConnection connection, CancellationToken token)
    {
        var fingerprint = connection.RemoteFingerprint;
        var hello = connection.RemoteHello;
        switch (request.Action)
        {
            case DirectActions.Propose:
                // Anyone can claim any id in a hello: an id known here with another certificate is refused outright.
                if (!ClaimsOwnId(hello.DeviceId, fingerprint))
                {
                    Log.Warn($"Direct: refused a direct session from \"{hello.DeviceName}\": its certificate isn't the one known for {hello.DeviceId}");
                    throw NotAllowed();
                }

                var senderLan = hello.Port is > 0 and <= 65535 && !_transfers.IsDirectAddress(connection.RemoteEndPoint.Address)
                    ? new[] { new IPEndPoint(connection.RemoteEndPoint.Address, hello.Port) }
                    : Array.Empty<IPEndPoint>();
                return await ProposeIncomingAsync(request, hello.DeviceId, hello.DeviceName, fingerprint, claimedOnly: false, DirectControlChannel.Lan,
                    senderLan, token).ConfigureAwait(false);

            case DirectActions.Offer:
                return OfferIncoming(request, fingerprint, out _);

            case DirectActions.HostForSender:
                return await HostForSenderAsync(request, fingerprint, token).ConfigureAwait(false);

            case DirectActions.Ready:
                return HandleReady(request, connection);

            case DirectActions.Failed:
                return HandleReceiverFailed(request, fingerprint);

            case DirectActions.Verify:
                return VerifyIncoming(request, fingerprint, connection.RemoteEndPoint);

            case DirectActions.End:
                if (request.SessionId != null && _outgoing.TryGetValue(request.SessionId, out var outgoing))
                {
                    if (!TryGetOutgoing(request, fingerprint, out _, out var refusal)) return refusal!;
                    return HandleReceiverEnded(outgoing, request);
                }

                return EndFromSender(request, fingerprint);

            default:
                return new DirectResponseMessage { Ok = false, Message = "Unknown request." };
        }
    }

    /// <summary>
    /// A sender proposes a direct session (nothing has started yet). Accepted when this device can join, lets senders
    /// connect directly, isn't busy with another direct link, and either the sender is paired (proven by its
    /// certificate) or Receive is open and this device's user agrees. Answering again for the same session from the
    /// same sender is harmless; a session that ended (or one meant for another device) is refused.
    /// </summary>
    internal async Task<DirectResponseMessage> ProposeIncomingAsync(DirectRequestMessage request, string senderId, string senderName, string senderFingerprint,
        bool claimedOnly, DirectControlChannel channel, IReadOnlyList<IPEndPoint> senderLan, CancellationToken token)
    {
        var sessionId = request.SessionId;
        if (!IsToken(sessionId) || string.IsNullOrEmpty(senderId) || string.IsNullOrEmpty(senderFingerprint))
            return Refuse("a proposal without a valid session", "Not a valid session.");
        if (request.ReceiverId != _selfId())
            return Refuse($"a proposal from {senderName} meant for {request.ReceiverId}", L.T("This request was meant for another device."));
        if (_endedSessions.ContainsKey(sessionId!))
            return Refuse($"a proposal from {senderName} for session {Short(sessionId)}, which already ended (replayed?)", L.T("This direct session has already ended."));
        if (_link == null || !CanJoinAny) throw new DirectLinkException(L.T("This device can't connect directly."));
        if (!_settings.Current.AllowDirectSenders) throw NotAllowed();

        // The same sender setting a lost link up again on this device's network: its old session there gives way (it
        // may not have heard that the link dropped). Only for a certificate proven over TLS, not one claimed over Bluetooth.
        foreach (var stale in _incoming.Values.Where(i => !claimedOnly && i.HostsHere && i.SessionId != sessionId && i.Stage != IncomingStage.Ended
                                                          && string.Equals(i.SenderFingerprint, senderFingerprint, StringComparison.OrdinalIgnoreCase)).ToList())
            EndIncoming(stale, "the sender set up a new session");

        Incoming incoming;
        lock (_incomingGate)
        {
            if (_incoming.TryGetValue(sessionId!, out var existing))
            {
                if (!string.Equals(existing.SenderFingerprint, senderFingerprint, StringComparison.OrdinalIgnoreCase))
                    return Refuse($"session {Short(sessionId)} proposed again by another certificate", "Not the device this session is with.");
                return existing.Stage is IncomingStage.Proposed or IncomingStage.Ended
                    ? new DirectResponseMessage { Ok = false, Message = L.T("Still waiting for an answer.") }
                    : new DirectResponseMessage { Ok = true }; // a repeat (e.g. the answer got lost): nothing new happens
            }

            if (_incoming.Values.Any(i => i.Stage != IncomingStage.Ended))
                throw new DirectLinkException(L.T("{0} is receiving over another direct connection right now. Try again when it's done.", _settings.Current.DeviceName));
            // This device's network kept on for a moment for this same sender (its link just dropped) doesn't count.
            var ownLease = ReceiverHostOwner(senderFingerprint);
            bool busy;
            lock (_hostGate) busy = _hostLeases.Any(o => o != ownLease);
            if (busy || _linkedPeers.Keys.Any(k => !k.StartsWith("rx:", StringComparison.Ordinal) && k != LeaseKey(ownLease)) || !_joinedReceivers.IsEmpty)
                throw new DirectLinkException(L.T("This device's direct connection is in use right now. Try again when it's done."));
            incoming = new Incoming
            {
                SessionId = sessionId!,
                SenderId = senderId,
                SenderName = Settings.AppSettings.NormalizeDeviceName(senderName),
                SenderFingerprint = senderFingerprint.ToLowerInvariant(),
                ClaimedOnly = claimedOnly,
                Channel = channel,
                SenderLan = senderLan,
                Stage = IncomingStage.Proposed,
                Deadline = Environment.TickCount64 + (long)ProposalLifetime.TotalMilliseconds,
            };
            _incoming[sessionId!] = incoming;
        }

        try
        {
            // Paired only counts when the certificate proved it (not a claim made over Bluetooth); so does a sender whose
            // files this device's user accepted and that are still coming (moving them to a direct link).
            var trusted = !claimedOnly && (IsTrusted(senderId, senderFingerprint) || IsReceivingFrom(senderFingerprint));
            // Setting a lost link up again: proven with the key this device gave that sender over TLS (not just claimed).
            var proven = false;
            if (!trusted && request.Proof != null)
            {
                if (ReconnectKeyOf(senderFingerprint) is { } key && ReconnectProof.Matches(ReconnectProof.ForPropose(key, request), request.Proof))
                {
                    proven = true;
                    incoming.ProofKey = key;
                }
                else
                {
                    Log.Warn($"Direct: {incoming}: its reconnect proof didn't check out (expired, or not from that device); asking the user");
                }
            }

            if (!trusted && !proven)
            {
                if (!_receiving)
                {
                    Log.Info($"Direct: {incoming}: refused (not paired, Receive isn't open)");
                    throw new DirectLinkException(L.T("Open Receive on {0} to get files directly from a device that isn't paired.", _settings.Current.DeviceName), declined: true);
                }

                using var answer = CancellationTokenSource.CreateLinkedTokenSource(token, incoming.Cts.Token);
                answer.CancelAfter(DirectApprovalTimeout);
                bool approved;
                try
                {
                    approved = await _transfers.Handler.ApproveDirectConnectionAsync(
                        new IncomingDirectRequest(senderId, incoming.SenderName, incoming.SenderFingerprint, claimedOnly), answer.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    approved = false;
                }

                if (!approved)
                {
                    Log.Info($"Direct: {incoming}: the user didn't accept it");
                    throw new DirectLinkException(L.T("{0} didn't accept the direct connection.", _settings.Current.DeviceName), declined: true);
                }
            }

            lock (_incomingGate)
            {
                if (incoming.Stage != IncomingStage.Proposed) throw new DirectLinkException(L.T("This direct session has already ended."));
                incoming.Stage = IncomingStage.Accepted;
            }

            Log.Info($"Direct: {incoming}: accepted ({(trusted ? "paired" : proven ? "setting up again a link its user accepted (proven)" : "this device's user agreed")}, over {channel})");
            _ = WatchIncomingAsync(incoming);
            return new DirectResponseMessage { Ok = true };
        }
        catch
        {
            EndIncoming(incoming, "not accepted");
            throw;
        }
    }

    /// <summary>
    /// The sender's network details for an accepted session: checked, then this device joins in the background (the
    /// answer goes back right away: joining may take this device off the network the answer would travel over).
    /// <paramref name="join"/> ends with this device's address on the direct network, or fails.
    /// </summary>
    internal DirectResponseMessage OfferIncoming(DirectRequestMessage request, string senderFingerprint, out Task<string>? join)
    {
        join = null;
        if (request.SessionId == null || !_incoming.TryGetValue(request.SessionId, out var incoming))
            return Refuse($"an offer for unknown session {Short(request.SessionId)}", L.T("This direct session has already ended."));
        if (!string.Equals(incoming.SenderFingerprint, senderFingerprint, StringComparison.OrdinalIgnoreCase))
            return Refuse($"{incoming}: offer from another certificate", "Not the device this session is with.");
        if (string.IsNullOrEmpty(request.Ssid) || request.Ssid.Length > 32 || request.Passphrase is not { Length: >= 8 and <= 63 }
            || request.TtlSeconds is <= 0 or > 900 || request.Port is <= 0 or > 65535 || !IsToken(request.Nonce))
            return Refuse($"{incoming}: malformed offer", "Missing network details.");
        // Accepted without asking because of a reconnect proof: the network details must come from the same key holder.
        if (incoming.ProofKey is { } proofKey && !ReconnectProof.Matches(ReconnectProof.ForOffer(proofKey, request), request.Proof))
            return Refuse($"{incoming}: offer without a valid reconnect proof", "Not the device this session is with.");

        lock (_incomingGate)
        {
            if (incoming.Stage != IncomingStage.Accepted || Environment.TickCount64 > incoming.Deadline)
                return Refuse($"{incoming}: offer while {incoming.Stage}", L.T("This direct session has already ended."));
            incoming.Stage = IncomingStage.Joining;
            incoming.Nonce = request.Nonce;
            incoming.Deadline = Environment.TickCount64 + request.TtlSeconds * 1000L;
        }

        var network = new DirectNetwork(request.Ssid, request.Passphrase) { HostIsComputer = request.HostIsComputer };
        join = Task.Run(() => JoinForSessionAsync(incoming, network, request.Port));
        join.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        return new DirectResponseMessage { Ok = true };
    }

    private async Task<string> JoinForSessionAsync(Incoming incoming, DirectNetwork network, int senderPort)
    {
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(incoming.Cts.Token, _stopping.Token);
            limit.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(1000, incoming.Deadline - Environment.TickCount64)));
            lock (_incomingGate)
            {
                if (incoming.Stage != IncomingStage.Joining) throw new OperationCanceledException();
                _linkedPeers[incoming.Key] = 0; // from here the link is this session's (nobody else may take it)
            }

            if (_link is IDirectRadio { IsWifiEnabled: false } radio)
            {
                // Apps can't switch Wi-Fi on (Android 10+): show the system's Wi-Fi panel and wait, within the offer's time.
                Log.Info($"Direct: {incoming}: Wi-Fi is off; asking this device's user to turn it on");
                var left = TimeSpan.FromMilliseconds(Math.Max(1000, incoming.Deadline - Environment.TickCount64));
                if (!await radio.RequestWifiOnAsync(left < WifiOnWait ? left : WifiOnWait, limit.Token).ConfigureAwait(false))
                    throw new DirectLinkException(L.T("Wi-Fi is off on {0}. Turn it on there, then press Try again.", _settings.Current.DeviceName)) { WifiOff = true };
            }

            if (_selfIsComputer) await PrepareAsync().ConfigureAwait(false);
            Log.Info($"Direct: {incoming}: joining {network.Ssid}");
            IPAddress? host;
            try
            {
                host = await _link!.JoinAsync(network, limit.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!incoming.Cts.IsCancellationRequested && !_stopping.IsCancellationRequested)
            {
                throw new DirectLinkException(L.T("{0} didn't manage to join the direct network in time.", _settings.Current.DeviceName));
            }

            bool late;
            lock (_incomingGate)
            {
                late = incoming.Stage != IncomingStage.Joining;
                incoming.OwnsJoin = true;
            }

            if (late)
            {
                // Joined after the session ended (a late OS answer): leave again, unless someone else uses the link by now.
                Log.Info($"Direct: {incoming}: joined after it ended; leaving again");
                StopLinkIfUnused();
                throw new OperationCanceledException();
            }

            HostedNetwork = null; // joining stopped this device's own network, if it was on
            DirectAddresses.Invalidate();
            if (host == null) throw new DirectLinkException(L.T("{0} joined the direct network but couldn't find the sender on it.", _settings.Current.DeviceName));

            // Report in over the direct network itself: the sender learns where this device is there, and that the route works.
            await ReportReadyAsync(incoming, new IPEndPoint(host, senderPort), limit.Token).ConfigureAwait(false);
            lock (_incomingGate)
            {
                if (incoming.Stage == IncomingStage.Joining) incoming.Stage = IncomingStage.Joined;
            }

            var own = OwnAddressNear(host);
            Log.Info($"Direct: {incoming}: on {network.Ssid} at {own?.ToString() ?? "?"}; sender at {host}");
            Changed?.Invoke();
            return own?.ToString() ?? "";
        }
        catch (Exception ex)
        {
            var cancelled = ex is OperationCanceledException && (incoming.Cts.IsCancellationRequested || _stopping.IsCancellationRequested
                                                                 || incoming.Stage == IncomingStage.Ended);
            var message = ex is DirectLinkException ? ex.Message : L.T("{0} couldn't join the direct network.", _settings.Current.DeviceName);
            if (!cancelled) Log.Warn($"Direct: {incoming}: joining failed: {ex.GetType().Name}: {ex.Message}");
            EndIncoming(incoming, cancelled ? "cancelled" : "join failed");
            if (!cancelled && incoming.Channel == DirectControlChannel.Lan) _ = TellSenderFailedAsync(incoming, message);
            if (cancelled) throw;
            throw new DirectLinkException(message, ex);
        }
    }

    private async Task ReportReadyAsync(Incoming incoming, IPEndPoint sender, CancellationToken token)
    {
        var deadline = Environment.TickCount64 + (long)ReportInTimeout.TotalMilliseconds;
        var ready = new DirectRequestMessage { Action = DirectActions.Ready, SessionId = incoming.SessionId, Nonce = incoming.Nonce, Port = _transfers.Port };
        Exception? last = null;
        while (Environment.TickCount64 < deadline)
        {
            try
            {
                var (reply, _) = await RequestAsync(new[] { sender }, incoming.SenderFingerprint, incoming.SenderName, ready, TimeSpan.FromSeconds(10), token,
                    LocalAddressFor).ConfigureAwait(false);
                if (reply.Ok) return;
                throw new DirectLinkException(reply.Message ?? L.T("{0} ended the direct connection.", incoming.SenderName));
            }
            catch (TransferException ex) when (ex.Error.Kind == TransferErrorKind.IdentityMismatch)
            {
                // Whoever runs this network isn't the device that asked (the Bluetooth claim was false): leave.
                throw new DirectLinkException(L.T("The device on the direct network isn't {0}. Beam left that network.", incoming.SenderName));
            }
            catch (Exception ex) when (ex is TransferException or IOException or System.Net.Sockets.SocketException or TimeoutException
                                           or System.Security.Authentication.AuthenticationException && !token.IsCancellationRequested)
            {
                last = ex; // the new network may need a moment (addresses, firewall)
                await Task.Delay(500, token).ConfigureAwait(false);
            }
        }

        throw new DirectLinkException(L.T("{0} joined the direct network but couldn't reach the sender on it. Check that Beam is allowed through the firewall.",
            _settings.Current.DeviceName), last);
    }

    /// <summary>Joining failed: tell the sender over the shared network (after leaving), so it can say where it failed.</summary>
    private async Task TellSenderFailedAsync(Incoming incoming, string message)
    {
        if (incoming.SenderLan.Count == 0) return;
        try
        {
            await Task.Delay(500).ConfigureAwait(false); // back on the shared network
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await RequestAsync(incoming.SenderLan, incoming.SenderFingerprint, incoming.SenderName, new DirectRequestMessage
            {
                Action = DirectActions.Failed,
                SessionId = incoming.SessionId,
                Nonce = incoming.Nonce,
                Stage = "join",
                Message = Trim(message),
            }, TimeSpan.FromSeconds(8), limit.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Info($"Direct: {incoming}: couldn't tell the sender it failed: {ex.Message}");
        }
    }

    private DirectResponseMessage VerifyIncoming(DirectRequestMessage request, string fingerprint, IPEndPoint remote)
    {
        if (request.SessionId == null || !_incoming.TryGetValue(request.SessionId, out var incoming))
            return Refuse($"verify for unknown session {Short(request.SessionId)}", L.T("This direct session has already ended."));
        if (!string.Equals(incoming.SenderFingerprint, fingerprint, StringComparison.OrdinalIgnoreCase))
            return Refuse($"{incoming}: verify from another certificate", "Not the device this session is with.");
        if (!string.Equals(request.Nonce, incoming.Nonce, StringComparison.Ordinal))
            return Refuse($"{incoming}: verify with a stale or wrong nonce", "Stale session.");
        if (!_transfers.IsDirectAddress(remote.Address))
            return Refuse($"{incoming}: verify from {remote}, outside the direct network", "Not over the direct network.");
        lock (_incomingGate)
        {
            if (incoming.Stage is not (IncomingStage.Joining or IncomingStage.Joined or IncomingStage.Verified))
                return Refuse($"{incoming}: verify while {incoming.Stage}", L.T("This direct session has already ended."));
            if (incoming.Stage != IncomingStage.Verified) Log.Info($"Direct: {incoming}: verified by the sender from {remote}");
            incoming.Stage = IncomingStage.Verified;
        }

        // Proven by its certificate over TLS just now: a key to set this link up again over Bluetooth without asking.
        return new DirectResponseMessage { Ok = true, ReconnectKey = IssueReconnectKey(incoming.SenderFingerprint) };
    }

    private DirectResponseMessage EndFromSender(DirectRequestMessage request, string fingerprint)
    {
        if (request.SessionId == null || !_incoming.TryGetValue(request.SessionId, out var incoming)) return new DirectResponseMessage { Ok = true };
        if (!string.Equals(incoming.SenderFingerprint, fingerprint, StringComparison.OrdinalIgnoreCase))
            return Refuse($"{incoming}: end from another certificate", "Not the device this session is with.");
        if (incoming.Nonce != null && !string.Equals(request.Nonce, incoming.Nonce, StringComparison.Ordinal))
            return Refuse($"{incoming}: end with a stale or wrong nonce", "Stale session.");
        EndIncoming(incoming, "the sender ended it");
        return new DirectResponseMessage { Ok = true };
    }

    /// <summary>Over Bluetooth: the sender ends a session (it can't use TLS; the session's nonce and claimed certificate must match).</summary>
    internal void EndFromSenderOverBluetooth(DirectRequestMessage request, string claimedFingerprint) => EndFromSender(request, claimedFingerprint);

    /// <summary>
    /// Ends an incoming session once: what it joined is left (only if this session joined it, and only when nobody
    /// else uses the link), and its id can't be used again.
    /// </summary>
    private void EndIncoming(Incoming incoming, string why, bool releaseNow = false)
    {
        IncomingStage wasStage;
        lock (_incomingGate)
        {
            if (incoming.Stage == IncomingStage.Ended) return;
            wasStage = incoming.Stage;
            incoming.Stage = IncomingStage.Ended;
            _endedSessions[incoming.SessionId] = Environment.TickCount64;
            _incoming.TryRemove(new KeyValuePair<string, Incoming>(incoming.SessionId, incoming));
        }

        foreach (var (id, at) in _endedSessions)
            if (Environment.TickCount64 - at > TimeSpan.FromHours(1).TotalMilliseconds) _endedSessions.TryRemove(id, out _);
        incoming.Cts.Cancel();
        Log.Info($"Direct: {incoming}: ended ({why})");
        // This device's network for this sender (this session's, or kept on for it after its last one).
        if (incoming.HostsHere || HoldsHost(ReceiverHostOwner(incoming.SenderFingerprint)))
            ReleaseReceiverHost(incoming, releaseNow || wasStage != IncomingStage.Verified);
        if (_linkedPeers.TryRemove(incoming.Key, out _)) StopLinkIfUnused();
        Changed?.Invoke();
    }

    // ---- This device's own network for a phone that sends to it ----

    /// <summary>
    /// After a session on this device's network ends, how long the network stays on for the same sender: a link that just
    /// dropped is set up again on the same network (the phone may still be on it), without restarting it.
    /// </summary>
    internal TimeSpan ReceiverHostGrace { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>The share of this device's network that sessions from one sender hold (one per sender certificate).</summary>
    private static string ReceiverHostOwner(string senderFingerprint) => "rxhost:" + senderFingerprint.ToLowerInvariant();

    /// <summary>
    /// A phone that sends to this computer asks it to start its network that shares its internet (Windows' Mobile
    /// hotspot) for the accepted session, and joins it: a phone keeps a network with internet, and the PC is the host
    /// rather than a guest on the phone's network. Only over Beam's TLS connection (the sender's certificate is proven).
    /// </summary>
    private async Task<DirectResponseMessage> HostForSenderAsync(DirectRequestMessage request, string senderFingerprint, CancellationToken token)
    {
        if (request.SessionId == null || !_incoming.TryGetValue(request.SessionId, out var incoming))
            return Refuse($"a request for this device's network for unknown session {Short(request.SessionId)}", L.T("This direct session has already ended."));
        if (!string.Equals(incoming.SenderFingerprint, senderFingerprint, StringComparison.OrdinalIgnoreCase))
            return Refuse($"{incoming}: request for this device's network from another certificate", "Not the device this session is with.");
        if (incoming.ClaimedOnly || incoming.Channel != DirectControlChannel.Lan)
            return Refuse($"{incoming}: request for this device's network outside Beam's network connection", "Not over Beam's network connection.");
        if (!IsToken(request.Nonce) || request.TtlSeconds is <= 0 or > 900)
            return Refuse($"{incoming}: malformed request for this device's network", "Not a valid session.");
        if (!CanHostForSenders)
            throw new DirectLinkException(L.T("{0} can't start a network that shares its internet right now.", _settings.Current.DeviceName));

        lock (_incomingGate)
        {
            if (incoming.Stage != IncomingStage.Accepted || Environment.TickCount64 > incoming.Deadline)
                return Refuse($"{incoming}: request for this device's network while {incoming.Stage}", L.T("This direct session has already ended."));
            incoming.Stage = IncomingStage.Joining; // from here: waiting for the sender to join and verify
            incoming.Nonce = request.Nonce;
            incoming.Deadline = Environment.TickCount64 + request.TtlSeconds * 1000L;
            incoming.HostsHere = true;
        }

        DirectNetwork network;
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token, incoming.Cts.Token, _stopping.Token);
            limit.CancelAfter(HostStartTimeout + HostReadyTimeout + TimeSpan.FromSeconds(5));
            Log.Info($"Direct: {incoming}: {incoming.SenderName} asks this device to start its network (sharing its internet) and joins it");
            network = await AcquireHostAsync(ReceiverHostOwner(incoming.SenderFingerprint), incoming.ToString(), forPhone: true, limit.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var cancelled = incoming.Stage == IncomingStage.Ended || _stopping.IsCancellationRequested;
            Log.Warn($"Direct: {incoming}: {(cancelled ? "ended while starting this device's network" : $"couldn't start this device's network: {ex.GetType().Name}: {ex.Message}")}");
            EndIncoming(incoming, "this device's network didn't start", releaseNow: true);
            throw new DirectLinkException(ex is DirectLinkException && !cancelled ? ex.Message : L.T("{0} didn't start its network.", _settings.Current.DeviceName), ex);
        }

        if (!network.SharesInternet)
        {
            // A network without internet is what the phone would leave after a while: it starts its own instead.
            Log.Info($"Direct: {incoming}: {network.Ssid} doesn't share this device's internet; the sender starts its own network instead");
            EndIncoming(incoming, "this device's network wouldn't share its internet", releaseNow: true);
            throw new DirectLinkException(L.T("{0} couldn't start its Mobile hotspot.", _settings.Current.DeviceName));
        }

        if (incoming.Stage != IncomingStage.Joining)
            return Refuse($"{incoming}: ended while starting this device's network", L.T("This direct session has already ended."));
        Log.Info($"Direct: {incoming}: {network.Ssid} is on, sharing this device's internet; waiting for {incoming.SenderName} to join it");
        return new DirectResponseMessage { Ok = true, Ssid = network.Ssid, Passphrase = network.Passphrase, Port = _transfers.Port, SharesInternet = true };
    }

    /// <summary>
    /// A session on this device's network ended: the network goes off (unless someone else uses it), at once when it never
    /// carried the session (it failed or was replaced before), else after <see cref="ReceiverHostGrace"/> unless the same
    /// sender's next session holds it by then.
    /// </summary>
    private void ReleaseReceiverHost(Incoming incoming, bool now)
    {
        var owner = ReceiverHostOwner(incoming.SenderFingerprint);
        bool StillWanted() => _incoming.Values.Any(i => i.Stage != IncomingStage.Ended
                                                        && string.Equals(i.SenderFingerprint, incoming.SenderFingerprint, StringComparison.OrdinalIgnoreCase));
        if (now || _stopping.IsCancellationRequested || ReceiverHostGrace <= TimeSpan.Zero)
        {
            if (!StillWanted()) ReleaseHost(owner);
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(ReceiverHostGrace, _stopping.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // closing: let go now
            }

            if (StillWanted()) return; // the same sender's next session uses it now
            ReleaseHost(owner);
        });
    }

    /// <summary>Ends every incoming session (closing Beam, or "Let senders connect directly" turned off).</summary>
    private void EndIncomingSessions(string why, bool notify = true)
    {
        foreach (var incoming in _incoming.Values.ToList())
        {
            if (notify && incoming.Stage is IncomingStage.Joined or IncomingStage.Verified) _ = TellSenderEndedAsync(incoming, why);
            EndIncoming(incoming, why);
        }
    }

    private async Task TellSenderEndedAsync(Incoming incoming, string why)
    {
        if (incoming.SenderLan.Count == 0) return;
        try
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            await RequestAsync(incoming.SenderLan, incoming.SenderFingerprint, incoming.SenderName, new DirectRequestMessage
            {
                Action = DirectActions.End,
                SessionId = incoming.SessionId,
                Nonce = incoming.Nonce,
                Message = Trim(why),
            }, TimeSpan.FromSeconds(5), limit.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Info($"Direct: {incoming}: couldn't tell the sender it ended: {ex.Message}");
        }
    }

    /// <summary>
    /// The network this device joined went away (the sender stopped it, or it went out of range): the session on it is
    /// over here; the sender sets a new one up if its transfer still needs it.
    /// </summary>
    private void OnJoinedNetworkLost()
    {
        foreach (var incoming in _incoming.Values.Where(i => i.OwnsJoin).ToList())
        {
            var wasUp = incoming.Stage is IncomingStage.Joined or IncomingStage.Verified;
            EndIncoming(incoming, "its network was lost");
            // The sender may not notice for a minute (its data just stops being acknowledged): tell it, so it sets the
            // link up again right away. Only over the shared network, once this device is back on it.
            if (wasUp) _ = TellSenderLinkLostAsync(incoming);
        }
    }

    /// <summary>How long a receiver keeps trying to tell the sender its direct link was lost (it may be between networks).</summary>
    internal TimeSpan LinkLostNoticeWindow { get; set; } = TimeSpan.FromSeconds(45);

    private async Task TellSenderLinkLostAsync(Incoming incoming)
    {
        if (incoming.SenderLan.Count == 0) return; // set up over Bluetooth: the sender finds out by its own timeout
        var deadline = Environment.TickCount64 + (long)LinkLostNoticeWindow.TotalMilliseconds;
        var message = new DirectRequestMessage
        {
            Action = DirectActions.End,
            SessionId = incoming.SessionId,
            Nonce = incoming.Nonce,
            Stage = "lost",
            Message = "link lost",
        };
        while (Environment.TickCount64 < deadline && !_stopping.IsCancellationRequested)
        {
            try
            {
                using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(6));
                await RequestAsync(incoming.SenderLan, incoming.SenderFingerprint, incoming.SenderName, message, TimeSpan.FromSeconds(5), limit.Token)
                    .ConfigureAwait(false);
                Log.Info($"Direct: {incoming}: told the sender the direct network was lost");
                return;
            }
            catch (Exception ex) when (ex is TransferException or IOException or System.Net.Sockets.SocketException or TimeoutException
                                           or OperationCanceledException or System.Security.Authentication.AuthenticationException)
            {
                await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false); // not back on the shared network yet
            }
        }

        Log.Info($"Direct: {incoming}: couldn't tell the sender the direct network was lost (not back on the shared network in time)");
    }

    /// <summary>
    /// An accepted session that never gets verified in time ends; a verified one ends once no transfer from its sender
    /// has run for a while (the sender may have vanished without saying so), so this device goes back to its Wi-Fi.
    /// </summary>
    private async Task WatchIncomingAsync(Incoming incoming)
    {
        var idleSince = Environment.TickCount64;
        var hadTransfer = false;
        try
        {
            while (incoming.Stage != IncomingStage.Ended)
            {
                await Task.Delay(250, incoming.Cts.Token).ConfigureAwait(false);
                var now = Environment.TickCount64;
                if (incoming.Stage != IncomingStage.Verified)
                {
                    if (now > incoming.Deadline) EndIncoming(incoming, "not set up in time");
                    continue;
                }

                if (IsReceivingFrom(incoming.SenderFingerprint)
                    || _transfers.Sessions.Any(s => s.Direction == TransferDirection.Receive && !s.IsFinished
                                                    && string.Equals(s.PeerFingerprint, incoming.SenderFingerprint, StringComparison.OrdinalIgnoreCase)))
                {
                    hadTransfer = true;
                    idleSince = now;
                    continue;
                }

                var limit = hadTransfer ? LeaveReceiverAfter : UnusedJoinTimeout;
                if (now - idleSince > limit.TotalMilliseconds) EndIncoming(incoming, hadTransfer ? "nothing more came" : "nothing came");
            }
        }
        catch (OperationCanceledException)
        {
            // ended
        }
    }

    private static bool IsToken(string? text) => text is { Length: 32 } && text.All(char.IsAsciiHexDigitLower);
}
