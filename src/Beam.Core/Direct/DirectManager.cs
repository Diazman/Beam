using System.Collections.Concurrent;
using System.Net;
using Beam.Core.Diagnostics;
using Beam.Core.Discovery;
using Beam.Core.Localization;
using Beam.Core.Protocol;
using Beam.Core.Settings;
using Beam.Core.Transfer;

namespace Beam.Core.Direct;

/// <summary>
/// Decides how this device reaches others: over the shared Wi-Fi network or a direct Wi-Fi link
/// (<see cref="ConnectionMethod"/>, per device and switchable during a transfer).
/// <para>
/// A direct link is set up over the normal network first: one device starts a direct network
/// (<see cref="IDirectLink.HostAsync"/>), the other joins it with the name and passphrase it was sent over
/// the encrypted connection. Discovery then finds the device on the new link too, and connections try the
/// preferred path first and the other one as a fallback. Switching drops the live connection; the
/// transfer reconnects on the other path and resumes where it stopped.
/// </para>
/// </summary>
public sealed class DirectManager
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan FindTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan SetupTimeout = TimeSpan.FromSeconds(75);

    private readonly TransferService _transfers;
    private readonly DiscoveryService _discovery;
    private readonly SettingsStore _settings;
    private readonly Func<string> _selfId;
    private readonly IDirectLink? _link;
    private readonly ConcurrentDictionary<string, ConnectionMethod> _peerMethod = new();
    private readonly ConcurrentDictionary<string, byte> _linkedPeers = new();
    private readonly SemaphoreSlim _setup = new(1, 1);

    internal DirectManager(TransferService transfers, DiscoveryService discovery, SettingsStore settings, Func<string> selfId, IDirectLink? link)
    {
        _transfers = transfers;
        _discovery = discovery;
        _settings = settings;
        _selfId = selfId;
        _link = link;
        _transfers.BeforeConnect = BeforeConnectAsync;
        _transfers.DirectHandler = HandleRequestAsync;
    }

    /// <summary>Stands for a phone's web browser (Phone page) using this device's direct network.</summary>
    private const string BrowserUser = "phone-browser";

    /// <summary>The preferred method or the direct link changed (raised on a background thread).</summary>
    public event Action? Changed;

    /// <summary>Setting up a direct link failed; the message is for the user (raised on a background thread).</summary>
    public event Action<string>? Problem;

    public DirectRoles Roles => _link?.Roles ?? DirectRoles.None;

    /// <summary>This device can make direct links at all (Wi-Fi Direct hardware and OS support).</summary>
    public bool IsSupported => Roles != DirectRoles.None;

    /// <summary>The connection method used with <paramref name="peerId"/>: what either side last chose, else the setting.</summary>
    public ConnectionMethod MethodFor(string peerId) =>
        _peerMethod.TryGetValue(peerId, out var method) ? method : _settings.Current.Connection;

    /// <summary>Both devices support direct links in compatible roles.</summary>
    public bool CanConnectDirectly(DeviceInfo peer) => PlanFor(peer) != Plan.None;

    /// <summary>The device's addresses with those of the preferred path first (the others stay as a fallback).</summary>
    public DeviceInfo Order(DeviceInfo device)
    {
        var preferDirect = MethodFor(device.Id) == ConnectionMethod.Direct;
        var ordered = device.Endpoints.OrderBy(e => _transfers.IsDirectAddress(e.Address) == preferDirect ? 0 : 1).ToList();
        return device with { Endpoints = ordered };
    }

    public bool HasDirectPath(DeviceInfo device) => device.Endpoints.Any(e => _transfers.IsDirectAddress(e.Address));

    /// <summary>
    /// Switches how this device talks to <paramref name="peerId"/>, including transfers already running: they
    /// reconnect over the new method and continue where they stopped. False (with <see cref="Problem"/> raised)
    /// when a direct link couldn't be set up; the transfers then continue over the network.
    /// </summary>
    public async Task<bool> SwitchAsync(string peerId, ConnectionMethod method, CancellationToken cancellationToken = default)
    {
        var peer = _discovery.Find(peerId);
        var previous = MethodFor(peerId);
        _peerMethod[peerId] = method;
        Changed?.Invoke();
        Log.Info($"Connection with {peer?.Name ?? peerId}: {previous} -> {method}");

        if (method == ConnectionMethod.Direct)
        {
            try
            {
                if (peer == null || !CanConnectDirectly(peer))
                    throw new DirectLinkException(peer == null
                        ? L.T("That device isn't nearby right now.")
                        : L.T("{0} can't connect directly. Both devices need the latest Beam, and phones need Android 10 or newer.", peer.Name));
                await ConnectAsync(peer, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is DirectLinkException or TransferException or IOException or TimeoutException
                                       || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
            {
                _peerMethod[peerId] = previous;
                Changed?.Invoke();
                Report(ex);
                return false;
            }
        }

        if (peer != null) await TellAsync(peer, new DirectRequestMessage { Action = DirectActions.Prefer, Method = Encode(method) }).ConfigureAwait(false);
        var interrupted = _transfers.InterruptConnections(peerId);
        if (interrupted > 0) Log.Info($"Switching {interrupted} transfer(s) with {peer?.Name ?? peerId} to {method}");

        if (method == ConnectionMethod.SameNetwork && _linkedPeers.TryRemove(peerId, out _))
        {
            if (peer != null) await TellAsync(peer, new DirectRequestMessage { Action = DirectActions.Stop }).ConfigureAwait(false);
            StopLinkIfUnused();
        }

        return true;
    }

    /// <summary>This device can start a direct network that phones join without an app (iPhone: by scanning a Wi-Fi code).</summary>
    public bool CanHost => Roles.HasFlag(DirectRoles.Host);

    /// <summary>
    /// Starts this device's direct network for the Phone page: the phone joins it with the returned name and
    /// passphrase (shown as a Wi-Fi QR code) and then opens the page on this device's direct address.
    /// </summary>
    public async Task<DirectNetwork> HostForBrowserAsync(CancellationToken cancellationToken)
    {
        if (_link == null || !CanHost) throw new DirectLinkException(L.T("This device can't connect directly."));
        var network = await _link.HostAsync(cancellationToken).ConfigureAwait(false);
        _linkedPeers[BrowserUser] = 0;
        DirectAddresses.Invalidate();
        Changed?.Invoke();
        return network;
    }

    /// <summary>The Phone page no longer needs the direct network (stopped unless a transfer still uses it).</summary>
    public void StopForBrowser()
    {
        if (_linkedPeers.TryRemove(BrowserUser, out _)) StopLinkIfUnused();
    }

    /// <summary>The Connection setting changed: forget per-device choices and switch running transfers.</summary>
    internal void OnSettingChanged(ConnectionMethod method)
    {
        var peers = _transfers.Sessions.Where(s => !s.IsFinished).Select(s => s.PeerId).Distinct().ToList();
        _peerMethod.Clear();
        Changed?.Invoke();
        foreach (var peer in peers) _ = SwitchAsync(peer, method);
        if (method == ConnectionMethod.SameNetwork && peers.Count == 0)
        {
            _linkedPeers.Clear();
            StopLinkIfUnused();
        }
    }

    /// <summary>Brings up a direct link with <paramref name="peer"/> (no-op when one is already up).</summary>
    public async Task ConnectAsync(DeviceInfo peer, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(SetupTimeout);
        var token = timeout.Token;
        await _setup.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (HasDirectPath(_discovery.Find(peer.Id) ?? peer)) return;
            var link = _link ?? throw new DirectLinkException(L.T("This device can't connect directly."));
            IPAddress? host = null;
            switch (PlanFor(peer))
            {
                case Plan.JoinPeer:
                {
                    Log.Info($"Direct: asking {peer.Name} to start a direct network");
                    var reply = await RequestAsync(peer, new DirectRequestMessage { Action = DirectActions.Host }, token).ConfigureAwait(false);
                    if (!reply.Ok || string.IsNullOrEmpty(reply.Ssid) || string.IsNullOrEmpty(reply.Passphrase))
                        throw new DirectLinkException(reply.Message ?? L.T("{0} couldn't start a direct connection.", peer.Name));
                    host = await link.JoinAsync(new DirectNetwork(reply.Ssid, reply.Passphrase), token).ConfigureAwait(false);
                    break;
                }

                case Plan.HostForPeer:
                {
                    var network = await link.HostAsync(token).ConfigureAwait(false);
                    Log.Info($"Direct: started {network.Ssid}; asking {peer.Name} to join");
                    try
                    {
                        var reply = await RequestAsync(peer, new DirectRequestMessage { Action = DirectActions.Join, Ssid = network.Ssid, Passphrase = network.Passphrase }, token)
                            .ConfigureAwait(false);
                        if (!reply.Ok) throw new DirectLinkException(reply.Message ?? L.T("{0} couldn't join the direct connection.", peer.Name));
                    }
                    catch (Exception ex) when (ex is IOException or TransferException { Error.Kind: TransferErrorKind.ConnectionLost })
                    {
                        // Joining can briefly disturb the phone's Wi-Fi so the answer gets lost; it may well have worked.
                        Log.Info($"Direct: no answer from {peer.Name} while it joined ({ex.Message}); looking for it on the direct link");
                    }

                    break;
                }

                default:
                    throw new DirectLinkException(L.T("{0} can't connect directly. Both devices need the latest Beam, and phones need Android 10 or newer.", peer.Name));
            }

            _linkedPeers[peer.Id] = 0;
            await FindOnDirectLinkAsync(peer, host, token).ConfigureAwait(false);
            Log.Info($"Direct: connected to {peer.Name} directly");
            Changed?.Invoke();
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new DirectLinkException(L.T("Connecting directly to {0} took too long.", peer.Name));
        }
        finally
        {
            _setup.Release();
        }
    }

    private async Task BeforeConnectAsync(DeviceInfo peer, CancellationToken token)
    {
        if (MethodFor(peer.Id) != ConnectionMethod.Direct || !CanConnectDirectly(peer)) return;
        if (HasDirectPath(_discovery.Find(peer.Id) ?? peer)) return;
        try
        {
            await ConnectAsync(peer, token).ConfigureAwait(false);
        }
        catch (Exception ex) when (!token.IsCancellationRequested)
        {
            // The network still works: send over it instead of failing.
            Report(ex);
        }
    }

    private async Task FindOnDirectLinkAsync(DeviceInfo peer, IPAddress? host, CancellationToken token)
    {
        DirectAddresses.Invalidate();
        var deadline = Environment.TickCount64 + (long)FindTimeout.TotalMilliseconds;
        var next = 0L;
        while (Environment.TickCount64 < deadline)
        {
            if (Environment.TickCount64 >= next)
            {
                // Ask on the new adapter (and the host's address directly) instead of waiting for the next announcement.
                DirectAddresses.Invalidate();
                _discovery.RescanNetworks();
                if (host != null) _discovery.AddUnicastTarget(host);
                next = Environment.TickCount64 + 2000;
            }

            if (_discovery.Find(peer.Id) is { } found && HasDirectPath(found)) return;
            await Task.Delay(250, token).ConfigureAwait(false);
        }

        throw new DirectLinkException(L.T("The direct connection to {0} started, but Beam couldn't reach it. Check that Beam is allowed through the firewall.", peer.Name));
    }

    private async Task HandleRequestAsync(PeerConnection connection, CancellationToken token)
    {
        var request = await connection.Channel.ReadMessageAsync<DirectRequestMessage>(FrameType.DirectRequest, TimeSpan.FromSeconds(30), token)
            .ConfigureAwait(false);
        var peerId = connection.RemoteHello.DeviceId;
        var peerName = connection.RemoteHello.DeviceName;
        DirectResponseMessage response;
        try
        {
            response = await HandleAsync(request, peerId, peerName, token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DirectLinkException or OperationCanceledException && !token.IsCancellationRequested)
        {
            Log.Warn($"Direct: {request.Action} for {peerName} failed: {ex.Message}");
            response = new DirectResponseMessage { Ok = false, Message = ex is DirectLinkException ? ex.Message : L.T("Connecting directly took too long.") };
        }

        await connection.Channel.SendAsync(FrameType.DirectResponse, response, token).ConfigureAwait(false);
    }

    private async Task<DirectResponseMessage> HandleAsync(DirectRequestMessage request, string peerId, string peerName, CancellationToken serviceToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(serviceToken);
        timeout.CancelAfter(SetupTimeout);
        var token = timeout.Token;
        switch (request.Action)
        {
            case DirectActions.Host:
            {
                if (_link == null || !Roles.HasFlag(DirectRoles.Host)) throw new DirectLinkException(L.T("This device can't connect directly."));
                var network = await _link.HostAsync(token).ConfigureAwait(false);
                _linkedPeers[peerId] = 0;
                SetPeerMethod(peerId, ConnectionMethod.Direct);
                Log.Info($"Direct: started {network.Ssid} for {peerName}");
                return new DirectResponseMessage { Ok = true, Ssid = network.Ssid, Passphrase = network.Passphrase };
            }

            case DirectActions.Join:
            {
                if (_link == null || !Roles.HasFlag(DirectRoles.Join)) throw new DirectLinkException(L.T("This device can't connect directly."));
                if (string.IsNullOrEmpty(request.Ssid) || string.IsNullOrEmpty(request.Passphrase)) throw new DirectLinkException("Missing network details.");
                Log.Info($"Direct: joining {request.Ssid} for {peerName}");
                var host = await _link.JoinAsync(new DirectNetwork(request.Ssid, request.Passphrase), token).ConfigureAwait(false);
                _linkedPeers[peerId] = 0;
                SetPeerMethod(peerId, ConnectionMethod.Direct);
                DirectAddresses.Invalidate();
                _discovery.RescanNetworks();
                if (host != null) _discovery.AddUnicastTarget(host);
                return new DirectResponseMessage { Ok = true };
            }

            case DirectActions.Prefer:
                SetPeerMethod(peerId, Decode(request.Method));
                return new DirectResponseMessage { Ok = true };

            case DirectActions.Stop:
                SetPeerMethod(peerId, ConnectionMethod.SameNetwork);
                _linkedPeers.TryRemove(peerId, out _);
                StopLinkIfUnused();
                return new DirectResponseMessage { Ok = true };

            default:
                return new DirectResponseMessage { Ok = false, Message = "Unknown request." };
        }
    }

    private void SetPeerMethod(string peerId, ConnectionMethod method)
    {
        _peerMethod[peerId] = method;
        Changed?.Invoke();
    }

    private void StopLinkIfUnused()
    {
        if (!_linkedPeers.IsEmpty || _link == null) return;
        try
        {
            _link.Stop();
            Log.Info("Direct: link stopped");
        }
        catch (Exception ex)
        {
            Log.Warn("Direct: stopping the link failed", ex);
        }

        DirectAddresses.Invalidate();
        Changed?.Invoke();
    }

    private async Task<DirectResponseMessage> RequestAsync(DeviceInfo peer, DirectRequestMessage request, CancellationToken token)
    {
        // Over the network when possible: the direct link may be what is being set up or torn down.
        var current = _discovery.Find(peer.Id) ?? peer;
        var endpoints = current.Endpoints.OrderBy(e => _transfers.IsDirectAddress(e.Address) ? 1 : 0).ToList();
        await using var connection = await SecureTransport.ConnectAsync(
            _transfers.Identity,
            _transfers.CreateHello(ConnectionPurpose.Direct),
            endpoints,
            string.IsNullOrEmpty(current.Fingerprint) ? null : current.Fingerprint,
            current.Name,
            token).ConfigureAwait(false);
        await connection.Channel.SendAsync(FrameType.DirectRequest, request, token).ConfigureAwait(false);
        return await connection.Channel.ReadMessageAsync<DirectResponseMessage>(FrameType.DirectResponse, RequestTimeout, token).ConfigureAwait(false);
    }

    /// <summary>Best effort: the other side learns what this side chose; a failure only means it follows a bit later.</summary>
    private async Task TellAsync(DeviceInfo peer, DirectRequestMessage request)
    {
        if (peer.DirectRoles == DirectRoles.None) return;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            await RequestAsync(peer, request, cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Info($"Direct: couldn't tell {peer.Name} ({request.Action}): {ex.Message}");
        }
    }

    private void Report(Exception ex)
    {
        var message = ex is DirectLinkException ? ex.Message : L.T("Couldn't connect directly. Beam continues over the network.");
        Log.Warn($"Direct: {ex.GetType().Name}: {ex.Message}");
        Problem?.Invoke(message);
    }

    private Plan PlanFor(DeviceInfo peer)
    {
        var mine = Roles;
        var theirs = peer.DirectRoles;
        var canJoinPeer = mine.HasFlag(DirectRoles.Join) && theirs.HasFlag(DirectRoles.Host);
        var canHostForPeer = mine.HasFlag(DirectRoles.Host) && theirs.HasFlag(DirectRoles.Join);
        if (canJoinPeer && canHostForPeer)
            return string.CompareOrdinal(_selfId(), peer.Id) < 0 ? Plan.HostForPeer : Plan.JoinPeer; // both sides agree
        return canJoinPeer ? Plan.JoinPeer : canHostForPeer ? Plan.HostForPeer : Plan.None;
    }

    private static string Encode(ConnectionMethod method) => method == ConnectionMethod.Direct ? "direct" : "network";

    private static ConnectionMethod Decode(string? method) => method == "direct" ? ConnectionMethod.Direct : ConnectionMethod.SameNetwork;

    private enum Plan
    {
        None,
        JoinPeer,
        HostForPeer,
    }
}
