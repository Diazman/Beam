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

    /// <summary>How long joining and reporting in may take before this device gives up and leaves.</summary>
    internal static TimeSpan ReportInTimeout { get; set; } = TimeSpan.FromSeconds(25);

    private readonly ConcurrentDictionary<string, Incoming> _incoming = new(); // by session id
    private readonly ConcurrentDictionary<string, long> _endedSessions = new(); // session ids that may not come back (replays)
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

        public DirectControlChannel Channel { get; init; }

        /// <summary>Where the sender's Beam is on the shared network (to report a failure there).</summary>
        public IReadOnlyList<IPEndPoint> SenderLan { get; init; } = Array.Empty<IPEndPoint>();

        public IncomingStage Stage { get; set; }

        public string? Nonce { get; set; }

        public long Deadline { get; set; }

        public CancellationTokenSource Cts { get; } = new();

        /// <summary>This session's join put this device on the sender's network (only then does ending it leave).</summary>
        public bool OwnsJoin { get; set; }

        public string Key => "rx:" + SessionId;

        public override string ToString() => $"incoming direct session {SessionId[..Math.Min(8, SessionId.Length)]} from {SenderName} ({Stage})";
    }

    private static bool IsSessionAction(string action) => action is DirectActions.Propose or DirectActions.Offer or DirectActions.Ready
        or DirectActions.Verify or DirectActions.End or DirectActions.Failed;

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
            if (_hostLeases.Count > 0 || _linkedPeers.Keys.Any(k => !k.StartsWith("rx:", StringComparison.Ordinal)) || !_joinedReceivers.IsEmpty)
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
            if (!trusted)
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

            Log.Info($"Direct: {incoming}: accepted ({(trusted ? "paired" : "this device's user agreed")}, over {channel})");
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

        return new DirectResponseMessage { Ok = true };
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
    internal void EndFromSenderOverBluetooth(DirectRequestMessage request) => EndFromSender(request, request.SenderFingerprint ?? "");

    /// <summary>
    /// Ends an incoming session once: what it joined is left (only if this session joined it, and only when nobody
    /// else uses the link), and its id can't be used again.
    /// </summary>
    private void EndIncoming(Incoming incoming, string why)
    {
        lock (_incomingGate)
        {
            if (incoming.Stage == IncomingStage.Ended) return;
            incoming.Stage = IncomingStage.Ended;
            _endedSessions[incoming.SessionId] = Environment.TickCount64;
            _incoming.TryRemove(new KeyValuePair<string, Incoming>(incoming.SessionId, incoming));
        }

        foreach (var (id, at) in _endedSessions)
            if (Environment.TickCount64 - at > TimeSpan.FromHours(1).TotalMilliseconds) _endedSessions.TryRemove(id, out _);
        incoming.Cts.Cancel();
        Log.Info($"Direct: {incoming}: ended ({why})");
        if (_linkedPeers.TryRemove(incoming.Key, out _)) StopLinkIfUnused();
        Changed?.Invoke();
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
        foreach (var incoming in _incoming.Values.Where(i => i.OwnsJoin).ToList()) EndIncoming(incoming, "its network was lost");
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
