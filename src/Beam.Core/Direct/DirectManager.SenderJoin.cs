using System.Collections.Concurrent;
using System.Net;
using Beam.Core.Diagnostics;
using Beam.Core.Discovery;
using Beam.Core.Localization;
using Beam.Core.Protocol;
using Beam.Core.Settings;
using Beam.Core.Transfer;

namespace Beam.Core.Direct;

// A sender that can't host (an iPhone) joins the receiving computer's network instead (HostForSender).
public sealed partial class DirectManager
{
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
}
