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
/// Beam's two ways to send: over the shared network (Same Network, the usual path, which this class leaves alone) or,
/// when the user chooses Direct, over a direct Wi-Fi link that this device (the sender) starts when Send is pressed.
/// <para>
/// Direct sessions (<see cref="DirectSession"/>, one per receiver) are set up over a control channel: Beam's
/// authenticated TLS connection on the shared network when the receiver is reachable there, else the encrypted
/// Bluetooth handshake. The sender proposes, starts its network, offers that session's name and passphrase, the
/// receiver joins and reports in over the direct network, and the sender verifies peer, session and route there before
/// any file data goes, and then only to that verified endpoint. A failure is reported at the stage it happened; the
/// files never go over the shared network unless the user chooses that.
/// </para>
/// <para>
/// Older Beam versions (1.5.3 and before) let the receiver host instead: their requests (<see cref="DirectActions.Host"/>,
/// <see cref="DirectActions.Join"/>, <see cref="DirectActions.Prefer"/>, <see cref="DirectActions.Stop"/>) are still
/// answered here, with the same checks as before; this version never sends them.
/// </para>
/// </summary>
public sealed partial class DirectManager
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(150);
    private static readonly TimeSpan SetupTimeout = TimeSpan.FromSeconds(180);

    private readonly TransferService _transfers;
    private readonly DiscoveryService _discovery;
    private readonly SettingsStore _settings;
    private readonly Func<string> _selfId;
    private readonly IDirectLink? _link;
    private readonly bool _selfIsComputer;
    private readonly ConcurrentDictionary<string, ConnectionMethod> _peerMethod = new();

    /// <summary>
    /// Who uses the direct link (this device's network, or one it joined) right now: legacy peers by device id, the Phone
    /// page, and direct sessions ("session:…" for networks this device hosts, "rx:…" for one it joined). The link is
    /// stopped only when nobody is left.
    /// </summary>
    private readonly ConcurrentDictionary<string, byte> _linkedPeers = new();

    // Legacy (receiver-hosted) requests: the certificate each device asked with (only that device may end the link it
    // asked for), and the unpaired ones, which keep the network on only while Receive is open or their files are coming.
    private readonly ConcurrentDictionary<string, string> _linkOwners = new();
    private readonly ConcurrentDictionary<string, byte> _unpairedUsers = new();

    private readonly CancellationTokenSource _stopping = new();
    private volatile bool _receiving;
    private volatile bool _rotatePending;

    // Legacy: networks this device joined for an older sender (left again once that is done).
    private readonly ConcurrentDictionary<string, long> _joinedReceivers = new();

    /// <summary>After the last transfer to/from a device whose network this device joined, how long before leaving it.</summary>
    internal static TimeSpan LeaveReceiverAfter { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Joined another device's network to receive, and nothing came: leave it after this long.</summary>
    internal static TimeSpan UnusedJoinTimeout { get; set; } = TimeSpan.FromMinutes(2);

    internal DirectManager(TransferService transfers, DiscoveryService discovery, SettingsStore settings, Func<string> selfId, IDirectLink? link,
        bool selfIsComputer = true)
    {
        _selfIsComputer = selfIsComputer;
        _transfers = transfers;
        _discovery = discovery;
        _settings = settings;
        _selfId = selfId;
        _link = link;
        if (link is IDirectLinkEvents events) events.Lost += OnLinkLost;
        _transfers.SessionFinished += OnSessionFinished;
        _transfers.DirectHandler = HandleRequestAsync;
    }

    /// <summary>Stands for a phone's web browser (Phone page) using this device's direct network.</summary>
    private const string BrowserUser = "phone-browser";

    /// <summary>The direct link changed (raised on a background thread).</summary>
    public event Action? Changed;

    /// <summary>
    /// Runs before this device starts its own network or joins one (e.g. so the PC can ask to let Beam through Windows
    /// Firewall, right when a direct connection is first used rather than at startup).
    /// </summary>
    public Func<Task>? BeforeHosting { get; set; }

    /// <summary>Something about a direct link went wrong; the message is for the user (raised on a background thread).</summary>
    public event Action<string>? Problem;

    public DirectRoles Roles => _link?.Roles ?? DirectRoles.None;

    /// <summary>
    /// What this device announces: its roles, that it speaks sender-hosted sessions, and (computers) whether joining it
    /// keeps internet. None when senders may not connect to it directly.
    /// </summary>
    public DirectRoles AnnouncedRoles =>
        Roles == DirectRoles.None || !_settings.Current.AllowDirectSenders ? DirectRoles.None : Roles | DirectRoles.SenderHosted | Traits();

    /// <summary>This device's address on the network of <c>host</c> (replaceable in tests).</summary>
    internal Func<IPAddress, IPAddress?> OwnAddressNear { get; set; } = DirectAddresses.OwnAddressNear;

    /// <summary>Whether this computer has internet over a cable (replaceable in tests).</summary>
    internal Func<bool> HasWiredInternet { get; set; } = NetworkFacts.HasWiredInternet;

    private DirectRoles Traits()
    {
        var traits = _receiving ? DirectRoles.Receiving : DirectRoles.None;
        if (!_selfIsComputer) return traits;
        if (Roles.HasFlag(DirectRoles.JoinComputers) && HasWiredInternet()) traits |= DirectRoles.Wired;
        if (HostedNetwork?.SharesInternet == true) traits |= DirectRoles.SharesInternet;
        return traits;
    }

    /// <summary>This device can join another device's network (phones any; computers as an ordinary Wi-Fi client).</summary>
    private bool CanJoinAny => (Roles & (DirectRoles.Join | DirectRoles.JoinComputers)) != 0;

    /// <summary>Receive is open: this device is visible (Wi-Fi and Bluetooth) and accepts direct sessions from senders.</summary>
    public bool IsReceiving => _receiving;

    /// <summary>
    /// Receive was opened or closed. Opening it only makes this device visible: no network starts. Closing it lets go
    /// of what unpaired legacy senders kept on.
    /// </summary>
    public void SetReceiving(bool receiving)
    {
        if (_receiving == receiving) return;
        _receiving = receiving;
        Log.Info(receiving ? "Direct: Receive opened (visible; no network is started for that)" : "Direct: Receive closed");
        if (!receiving)
        {
            if (ReleaseUnpairedUsers()) StopLinkIfUnused(keepForRunningTransfers: true);
            RotateIfPending();
        }

        _discovery.AnnounceNow();
        Changed?.Invoke();
    }

    /// <summary>
    /// Legacy (an older sender asked over Bluetooth): starts this device's network for it. Null when this device can't
    /// host. This version's senders never ask for it: they host themselves.
    /// </summary>
    public async Task<DirectNetwork?> HostForNearbyAsync(CancellationToken cancellationToken)
    {
        if (_link == null || !CanHost || !_settings.Current.AllowDirectSenders) return null;
        if (LinkInUseBySessions()) return null; // a direct session uses the radio: don't take it away
        var network = await HostOwnNetworkAsync(cancellationToken).ConfigureAwait(false);
        _linkedPeers[LegacyNearbyUser] = 0;
        DirectAddresses.Invalidate();
        Changed?.Invoke();
        return network;
    }

    /// <summary>Stands for an older Bluetooth sender using this device's network (until Receive closes).</summary>
    private const string LegacyNearbyUser = "legacy-nearby";

    /// <summary>
    /// The network's passphrase went to a device that isn't trusted: give the network a new one once it is off, so
    /// that device can't join it again later without asking.
    /// </summary>
    internal void RotateWhenOff() => _rotatePending = true;

    private void RotateIfPending()
    {
        if (!_rotatePending || HostedNetwork != null) return;
        _rotatePending = false;
        DirectNetwork current;
        lock (_ownNetworkGate)
        {
            current = OwnNetworkLocked();
            var fresh = DirectNetwork.Create(_settings.Current.DeviceName);
            _settings.Update(s => s.DirectNetworkPassphrase = fresh.Passphrase);
        }

        Log.Info($"Direct: new passphrase for {current.Ssid}");
    }

    /// <summary>
    /// A receiver's details came over Bluetooth: list it under its real id (with the roles it announced). Nothing is
    /// joined or remembered: a direct session gets its own network details when it is set up.
    /// </summary>
    public DeviceInfo AcceptNearby(PairingCode code)
    {
        var roles = !string.IsNullOrEmpty(code.Roles)
            ? DirectRolesText.Parse(code.Roles)
            : DirectRoles.Host | (IsComputer(code.Kind, null) ? DirectRoles.JoinComputers : DirectRoles.Join);
        var device = code.ToDevice() with { Endpoints = Array.Empty<IPEndPoint>(), DirectRoles = roles | DirectRoles.Receiving };
        foreach (var address in code.Addresses)
            if (IPAddress.TryParse(address.Split(':')[0], out var ip)) _discovery.AddUnicastTarget(ip); // maybe on the same network after all
        var existing = _discovery.Find(code.DeviceId);
        if (existing is not { Endpoints.Count: > 0 }) _discovery.ReportReachable(device);
        return _discovery.Find(code.DeviceId) ?? device;
    }

    /// <summary>"Let senders connect directly" was turned off: this device leaves what it joined or hosts for others.</summary>
    internal void OnDirectSendersNotAllowed()
    {
        EndIncomingSessions(L.T("{0} no longer lets senders connect directly.", _settings.Current.DeviceName));
        if (HostedNetwork == null && _joinedReceivers.IsEmpty) return;
        if (_transfers.Sessions.Any(s => !s.IsFinished && s.Path == TransferPath.Direct && s.Direction == TransferDirection.Receive))
        {
            Log.Info("Direct: senders may no longer connect directly; the network stays until the running transfer is done");
            return;
        }

        Log.Info("Direct: senders may no longer connect directly; letting go of networks used for receiving");
        foreach (var peer in _linkOwners.Keys.ToList()) _linkedPeers.TryRemove(peer, out _);
        _linkedPeers.TryRemove(LegacyNearbyUser, out _);
        _linkOwners.Clear();
        _unpairedUsers.Clear();
        foreach (var peer in _joinedReceivers.Keys.ToList())
        {
            _joinedReceivers.TryRemove(peer, out _);
            _linkedPeers.TryRemove(peer, out _);
        }

        StopLinkIfUnused();
    }

    private DirectLinkException NotAllowed() =>
        new(L.T("{0} doesn't let senders connect directly.", _settings.Current.DeviceName), declined: true);

    /// <summary>This device can make direct links at all (Wi-Fi Direct hardware and OS support).</summary>
    public bool IsSupported => Roles != DirectRoles.None;

    /// <summary>Legacy: what an older peer last asked for (informational; this version's sessions carry their own mode).</summary>
    public ConnectionMethod MethodFor(string peerId) =>
        _peerMethod.TryGetValue(peerId, out var method) ? method : _settings.Current.Connection;

    /// <summary>The device's addresses, those of the preferred path first (the others stay a fallback). Same Network sends use this as before.</summary>
    public DeviceInfo Order(DeviceInfo device)
    {
        var preferDirect = MethodFor(device.Id) == ConnectionMethod.Direct;
        var ordered = device.Endpoints.OrderBy(e => _transfers.IsDirectAddress(e.Address) == preferDirect ? 0 : 1).ToList();
        return device with { Endpoints = ordered };
    }

    public bool HasDirectPath(DeviceInfo device) => device.Endpoints.Any(e => _transfers.IsDirectAddress(e.Address));

    /// <summary>This device's own direct network (Phone page, pairing code): created once and kept.</summary>
    public DirectNetwork OwnNetwork()
    {
        lock (_ownNetworkGate) return OwnNetworkLocked();
    }

    private readonly object _ownNetworkGate = new();

    private DirectNetwork OwnNetworkLocked()
    {
        var current = _settings.Current;
        if (!string.IsNullOrEmpty(current.DirectNetworkName) && !string.IsNullOrEmpty(current.DirectNetworkPassphrase))
            return new DirectNetwork(current.DirectNetworkName, current.DirectNetworkPassphrase);
        var created = DirectNetwork.Create(current.DeviceName);
        _settings.Update(s =>
        {
            s.DirectNetworkName = created.Ssid;
            s.DirectNetworkPassphrase = created.Passphrase;
        });
        return created;
    }

    /// <summary>The direct network of a paired device, if this device knows it (from its code, or an older version).</summary>
    public KnownDirectNetwork? KnownNetworkFor(string deviceId) =>
        _settings.Current.KnownDirectNetworks.FirstOrDefault(k => k.DeviceId == deviceId);

    /// <summary>Keeps a paired device's direct network (its code was scanned, or an older version shared it).</summary>
    public void Remember(string deviceId, string name, string fingerprint, string kind, DirectNetwork network, int port = 0)
    {
        if (!CanJoinAny || string.IsNullOrEmpty(deviceId) || string.IsNullOrEmpty(fingerprint)) return;
        var known = KnownNetworkFor(deviceId);
        if (known != null && known.Ssid == network.Ssid && known.Passphrase == network.Passphrase && known.Name == name && known.Port == port) return;
        _settings.Update(s =>
        {
            s.KnownDirectNetworks.RemoveAll(k => k.DeviceId == deviceId);
            s.KnownDirectNetworks.Add(new KnownDirectNetwork
            {
                DeviceId = deviceId,
                Name = name,
                Fingerprint = fingerprint.ToLowerInvariant(),
                Kind = kind,
                Ssid = network.Ssid,
                Passphrase = network.Passphrase,
                Port = port,
            });
        });
        Log.Info($"Direct: remembered {name}'s direct network {network.Ssid}");
    }

    /// <summary>Background work at startup: none (nothing is joined or started until the user sends).</summary>
    internal void Start()
    {
    }

    /// <summary>
    /// Beam is closing: end every direct session, turn this device's network off (and leave another's) whoever still
    /// uses it, and wait for that (a bit), so a network doesn't stay on after Beam has closed.
    /// </summary>
    internal async Task StopAsync()
    {
        _stopping.Cancel();
        foreach (var outgoing in _outgoing.Values) outgoing.Session.Cancel();
        EndIncomingSessions(L.T("Beam was closed on {0}.", _settings.Current.DeviceName), notify: false);
        _linkedPeers.Clear();
        _linkOwners.Clear();
        _unpairedUsers.Clear();
        if (_link == null) return;
        try
        {
            if (_link is IAsyncDisposable closing) await closing.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            else _link.Stop();
        }
        catch (Exception ex)
        {
            Log.Warn("Direct: stopping the link on exit failed", ex);
        }

        HostedNetwork = null;
    }

    /// <summary>This device can start a direct network.</summary>
    public bool CanHost => Roles.HasFlag(DirectRoles.Host);

    /// <summary>
    /// Starts this device's direct network for the Phone page: the phone joins it with the returned name and
    /// passphrase (shown as a Wi-Fi QR code) and then opens the page on this device's direct address.
    /// </summary>
    public async Task<DirectNetwork> HostForBrowserAsync(CancellationToken cancellationToken)
    {
        if (_link == null || !CanHost) throw new DirectLinkException(L.T("This device can't connect directly."));
        if (LinkInUseBySessions()) throw new DirectLinkException(L.T("This device's direct connection is in use right now. Try again when it's done."));
        var network = await HostOwnNetworkAsync(cancellationToken).ConfigureAwait(false);
        _linkedPeers[BrowserUser] = 0;
        _ = Task.Delay(3000).ContinueWith(_ => Log.Info($"Direct network {network.Ssid} for a phone's browser. Adapters: {DirectAddresses.Describe()}"), TaskScheduler.Default);
        DirectAddresses.Invalidate();
        Changed?.Invoke();
        return network;
    }

    /// <summary>The Phone page no longer needs the direct network (stopped unless something else still uses it).</summary>
    public void StopForBrowser()
    {
        if (_linkedPeers.TryRemove(BrowserUser, out _)) StopLinkIfUnused(keepForRunningTransfers: true);
    }

    /// <summary>Legacy: once the last transfer with a device is done, what it asked for no longer counts.</summary>
    private void OnSessionFinished(TransferSession session)
    {
        var peerId = session.PeerId;
        if (_transfers.Sessions.Any(s => s.PeerId == peerId && !s.IsFinished)) return;
        if (!_receiving && ReleaseUnpairedUsers()) StopLinkIfUnused(keepForRunningTransfers: true);
        if (_joinedReceivers.ContainsKey(peerId)) _ = LeaveReceiverLaterAsync(peerId, session.PeerName);
        if (_peerMethod.TryRemove(peerId, out _)) Changed?.Invoke();
    }

    /// <summary>
    /// Legacy: this device joined an older sender's network to receive; after a while with nothing more coming, leave
    /// it, so this device is back on its own Wi-Fi (and internet).
    /// </summary>
    private async Task LeaveReceiverLaterAsync(string peerId, string peerName, TimeSpan? after = null)
    {
        // Only for the join that is current now: a later join (the link dropped and came back) gets its own timer.
        if (!_joinedReceivers.TryGetValue(peerId, out var joinedAt)) return;
        try
        {
            await Task.Delay(after ?? LeaveReceiverAfter, _stopping.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (_transfers.Sessions.Any(s => s.PeerId == peerId && !s.IsFinished)) return;
        if (!_joinedReceivers.TryGetValue(peerId, out var now) || now != joinedAt) return;
        if (!((ICollection<KeyValuePair<string, long>>)_joinedReceivers).Remove(new KeyValuePair<string, long>(peerId, joinedAt))) return;
        _peerMethod.TryRemove(peerId, out _);
        if (!_linkedPeers.TryRemove(peerId, out _)) return;
        Log.Info($"Direct: nothing more to transfer with {peerName}; leaving its network");
        StopLinkIfUnused();
    }

    /// <summary>The Connection setting changed: it applies to the next Send (running transfers keep their mode).</summary>
    internal void OnSettingChanged(ConnectionMethod method)
    {
        foreach (var peer in _peerMethod.Keys.ToList()) _peerMethod.TryRemove(peer, out _);
        Log.Info($"Direct: sending mode is now {method}; running transfers keep theirs");
        Changed?.Invoke();
    }

    private static async Task<bool> CanReachAsync(IPEndPoint endpoint, CancellationToken token)
    {
        using var socket = new System.Net.Sockets.Socket(endpoint.AddressFamily, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(1.5));
        try
        {
            await socket.ConnectAsync(endpoint, timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception) when (!token.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>The joined network went away: forget its addresses; a direct session on it is interrupted.</summary>
    private void OnLinkLost()
    {
        Log.Warn("Direct: the direct network was lost");
        DirectAddresses.Invalidate();
        OnJoinedNetworkLost();
        _discovery.RescanNetworks();
        Changed?.Invoke();
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
            response = IsSessionAction(request.Action)
                ? await HandleSessionRequestAsync(request, connection, token).ConfigureAwait(false)
                : await HandleAsync(request, peerId, peerName, token, connection.RemoteFingerprint, connection.RemoteHello.Kind).ConfigureAwait(false);
        }
        catch (Exception ex) when (!token.IsCancellationRequested)
        {
            Log.Warn($"Direct: {request.Action} for {peerName} failed: {ex.GetType().Name}: {ex.Message}");
            response = new DirectResponseMessage
            {
                Ok = false,
                Message = ex is DirectLinkException ? ex.Message : L.T("Connecting directly took too long."),
                Declined = ex is DirectLinkException { Declined: true },
            };
        }

        await connection.Channel.SendAsync(FrameType.DirectResponse, response, token).ConfigureAwait(false);
    }

    /// <summary>Legacy (receiver-hosted) requests from Beam 1.5.3 and older, with the checks they always had.</summary>
    private async Task<DirectResponseMessage> HandleAsync(DirectRequestMessage request, string peerId, string peerName, CancellationToken serviceToken,
        string peerFingerprint = "", string peerKind = "")
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(serviceToken);
        timeout.CancelAfter(SetupTimeout);
        var token = timeout.Token;
        // Anyone on the network can connect and present a self-signed certificate, and the device id in its hello is
        // only what it says: refuse an id that this device knows with another certificate, so nobody can act for a
        // device this one knows (stop its link, replace its remembered network).
        if (!ClaimsOwnId(peerId, peerFingerprint))
        {
            Log.Warn($"Direct: refused {request.Action} from \"{peerName}\": its certificate isn't the one known for {peerId}");
            throw NotAllowed();
        }

        switch (request.Action)
        {
            case DirectActions.Host:
            {
                if (_link == null || !Roles.HasFlag(DirectRoles.Host)) throw new DirectLinkException(L.T("This device can't connect directly."));
                if (!_settings.Current.AllowDirectSenders) throw NotAllowed();
                // An older sender wants to connect directly: this device (the receiver) starts its network, without
                // asking (the files themselves still need this device's approval); but only for a paired device, while
                // Receive is open, or for a device whose files the user accepted.
                var trusted = IsTrusted(peerId, peerFingerprint);
                var accepted = IsReceivingFrom(peerFingerprint);
                if (!trusted && !accepted && !_receiving)
                {
                    Log.Info($"Direct: {peerName} asked for this device's network; refused (not paired, Receive isn't open, no transfer from it)");
                    throw NotAllowed();
                }

                // Starting it would take this device off the network it joined for another device (an unpaired device
                // mustn't break a link someone else uses, even one whose files were accepted).
                if (!trusted && _joinedReceivers.Keys.Any(k => k != peerId))
                {
                    Log.Info($"Direct: {peerName} asked for this device's network; refused (this device is on another device's network)");
                    throw NotAllowed();
                }

                // Nor may anyone take the radio from a direct session that is set up or running.
                if (LinkInUseBySessions())
                {
                    Log.Info($"Direct: {peerName} asked for this device's network; refused (a direct session uses it)");
                    throw new DirectLinkException(L.T("This device's direct connection is in use right now. Try again when it's done."));
                }

                var network = await HostOwnNetworkAsync(token).ConfigureAwait(false);
                if (!trusted)
                {
                    RotateWhenOff(); // like over Bluetooth: an unpaired device can't use the passphrase later
                    _unpairedUsers[peerId] = 0;
                }

                _linkOwners[peerId] = peerFingerprint;
                _linkedPeers[peerId] = 0;
                SetPeerMethod(peerId, ConnectionMethod.Direct);
                Log.Info($"Direct: started {network.Ssid} for {peerName}{(trusted ? " (paired)" : "")} (older Beam asked)");
                return new DirectResponseMessage { Ok = true, Ssid = network.Ssid, Passphrase = network.Passphrase };
            }

            case DirectActions.Join:
            {
                if (_link == null || !CanJoinAny)
                    throw new DirectLinkException(L.T("This device can't connect directly."));
                if (!_settings.Current.AllowDirectSenders) throw NotAllowed();
                // Joining takes this device off its own Wi-Fi onto a network the other device chose: only for a paired
                // device, or one whose files the user accepted and that are still coming (not just because Receive is open).
                var joinTrusted = IsTrusted(peerId, peerFingerprint);
                if (!joinTrusted && !IsReceivingFrom(peerFingerprint))
                {
                    Log.Warn($"Direct: {peerName} asked this device to join {request.Ssid}; refused (not paired, no transfer from it)");
                    throw NotAllowed();
                }

                // Joining ends this device's own network and any other link: not for an unpaired device while others use it,
                // and not for anyone while a direct session does.
                if ((!joinTrusted && LinkUsedByOthers(peerId)) || LinkInUseBySessions())
                {
                    Log.Warn($"Direct: {peerName} asked this device to join {request.Ssid}; refused (its direct link is in use by another device)");
                    throw NotAllowed();
                }

                if (string.IsNullOrEmpty(request.Ssid) || string.IsNullOrEmpty(request.Passphrase)) throw new DirectLinkException("Missing network details.");
                if (_selfIsComputer) await PrepareAsync().ConfigureAwait(false);
                Log.Info($"Direct: {peerName} started {request.Ssid}; joining it to receive (older Beam asked; this device's own network, if on, stops)");
                var joining = new DirectNetwork(request.Ssid, request.Passphrase) { HostIsComputer = IsComputer(peerKind, _discovery.Find(peerId)?.Platform) };
                Remember(peerId, peerName, peerFingerprint, peerKind, joining);
                var host = await _link.JoinAsync(joining, token).ConfigureAwait(false);
                HostedNetwork = null; // joining stops this device's own network
                _linkOwners[peerId] = peerFingerprint;
                _linkedPeers[peerId] = 0;
                _joinedReceivers[peerId] = Environment.TickCount64;
                _ = LeaveReceiverLaterAsync(peerId, peerName, UnusedJoinTimeout);
                SetPeerMethod(peerId, ConnectionMethod.Direct);
                DirectAddresses.Invalidate();
                _discovery.RescanNetworks();
                if (host != null)
                {
                    _discovery.AddUnicastTarget(host);
                    if (_discovery.Find(peerId) is { } hostDevice)
                    {
                        var port = hostDevice.Endpoints.FirstOrDefault()?.Port ?? AppInfo.TransferPort;
                        _discovery.ReportReachable(hostDevice with
                        {
                            Endpoints = new[] { new IPEndPoint(host, port) }.Concat(hostDevice.Endpoints.Where(e => !e.Address.Equals(host))).Take(4).ToList(),
                        });
                    }
                }

                var own = host != null ? OwnAddressNear(host) : null;
                return new DirectResponseMessage { Ok = true, Address = own?.ToString() };
            }

            case DirectActions.Prefer:
            {
                if (!OwnsLink(peerId, peerFingerprint))
                {
                    Log.Warn($"Direct: refused {request.Action} from \"{peerName}\": the link for {peerId} was set up with another certificate");
                    throw NotAllowed();
                }

                // A device that joined this device's own network keeps it on: only while it is on (this doesn't start
                // anything), and an unpaired one only as long as Receive is open or its files come.
                var trusted = IsTrusted(peerId, peerFingerprint);
                if (Decode(request.Method) == ConnectionMethod.Direct && Roles.HasFlag(DirectRoles.Host) && HostedNetwork != null
                    && (trusted || _receiving || IsReceivingFrom(peerFingerprint)))
                {
                    if (!trusted) _unpairedUsers[peerId] = 0;
                    _linkOwners.TryAdd(peerId, peerFingerprint);
                    _linkedPeers[peerId] = 0;
                }

                SetPeerMethod(peerId, Decode(request.Method));
                return new DirectResponseMessage { Ok = true };
            }

            case DirectActions.Stop:
                // Only the device the link was set up for can end it (not one that never used it, nor one using its id).
                if (!OwnsLink(peerId, peerFingerprint))
                {
                    Log.Warn($"Direct: refused {request.Action} from \"{peerName}\": the link for {peerId} was set up with another certificate");
                    throw NotAllowed();
                }

                SetPeerMethod(peerId, ConnectionMethod.SameNetwork);
                _linkOwners.TryRemove(peerId, out _);
                _unpairedUsers.TryRemove(peerId, out _);
                if (_linkedPeers.TryRemove(peerId, out _)) StopLinkIfUnused();
                return new DirectResponseMessage { Ok = true };

            default:
                return new DirectResponseMessage { Ok = false, Message = "Unknown request." };
        }
    }

    /// <summary>
    /// The certificate presented is the one this device knows for <paramref name="peerId"/> (paired, remembered, or
    /// announced), or the id is new here. Device ids are only claimed in the hello; the certificate is what's proven.
    /// </summary>
    private bool ClaimsOwnId(string peerId, string fingerprint)
    {
        if (string.IsNullOrEmpty(peerId) || string.IsNullOrEmpty(fingerprint)) return false;
        bool Same(string? known) => string.Equals(known, fingerprint, StringComparison.OrdinalIgnoreCase);
        // Strongest first: paired by the user, then remembered from an earlier link. Wi-Fi announcements are anyone's to
        // send (the last one wins), so they only count for a device known no other way.
        var settings = _settings.Current;
        var paired = settings.TrustedDevices.Where(t => t.DeviceId == peerId && !string.IsNullOrEmpty(t.Fingerprint)).Select(t => t.Fingerprint).ToList();
        if (paired.Count > 0) return paired.Any(Same);
        var remembered = settings.KnownDirectNetworks.FirstOrDefault(k => k.DeviceId == peerId && !string.IsNullOrEmpty(k.Fingerprint));
        if (remembered != null) return Same(remembered.Fingerprint);
        var announced = _discovery.Find(peerId)?.Fingerprint;
        return string.IsNullOrEmpty(announced) || Same(announced);
    }

    /// <summary>
    /// The fingerprint to insist on when connecting to <paramref name="peer"/>: the paired one, else the remembered one,
    /// else what it announced (anyone can announce, so that one only proves it is the same device as before).
    /// </summary>
    private string? ExpectedFingerprint(DeviceInfo peer)
    {
        var settings = _settings.Current;
        var paired = settings.TrustedDevices.FirstOrDefault(t => t.DeviceId == peer.Id && !string.IsNullOrEmpty(t.Fingerprint))?.Fingerprint;
        if (paired != null) return paired;
        var remembered = KnownNetworkFor(peer.Id)?.Fingerprint;
        if (!string.IsNullOrEmpty(remembered)) return remembered;
        return string.IsNullOrEmpty(peer.Fingerprint) ? null : peer.Fingerprint;
    }

    /// <summary>
    /// Legacy: the direct link is used by a device other than <paramref name="peerId"/> (one it was set up for, the Phone
    /// page, a direct session, or a transfer running over it).
    /// </summary>
    private bool LinkUsedByOthers(string peerId) =>
        _linkedPeers.Keys.Any(k => k != peerId)
        || _transfers.Sessions.Any(s => !s.IsFinished && s.Path == TransferPath.Direct && s.PeerId != peerId);

    /// <summary>No link was set up for <paramref name="peerId"/> with another certificate.</summary>
    private bool OwnsLink(string peerId, string fingerprint) =>
        !_linkOwners.TryGetValue(peerId, out var owner) || string.Equals(owner, fingerprint, StringComparison.OrdinalIgnoreCase);

    /// <summary>Paired with this device (its code was scanned here, or "always accept" was chosen for it).</summary>
    private bool IsTrusted(string peerId, string fingerprint) =>
        !string.IsNullOrEmpty(fingerprint)
        && _settings.Current.TrustedDevices.Any(t => string.Equals(t.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase)
                                                     && (string.IsNullOrEmpty(t.DeviceId) || t.DeviceId == peerId));

    /// <summary>The user accepted files from this device and they are still coming (or paused while switching paths).</summary>
    private bool IsReceivingFrom(string fingerprint) =>
        !string.IsNullOrEmpty(fingerprint)
        && _transfers.Sessions.Any(s => s.Direction == TransferDirection.Receive && !s.IsFinished
                                        && s.State is TransferState.Transferring or TransferState.Interrupted
                                        && string.Equals(s.PeerFingerprint, fingerprint, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Receive is closed: unpaired legacy senders that got this device's network while it was open (or while their files
    /// came) no longer keep it on once nothing from them is running. True when one let go.
    /// </summary>
    private bool ReleaseUnpairedUsers()
    {
        if (_receiving) return false;
        var released = _linkedPeers.TryRemove(LegacyNearbyUser, out _);
        foreach (var peer in _unpairedUsers.Keys.ToList())
        {
            if (_transfers.Sessions.Any(s => s.PeerId == peer && !s.IsFinished)) continue;
            _unpairedUsers.TryRemove(peer, out _);
            _linkOwners.TryRemove(peer, out _);
            released |= _linkedPeers.TryRemove(peer, out _);
        }

        if (released) Log.Info("Direct: Receive is closed; unpaired senders no longer keep this device's network on");
        return released;
    }

    /// <summary>Before this device's network starts or it joins another's (e.g. letting Beam through the firewall there).</summary>
    private async Task PrepareAsync()
    {
        if (BeforeHosting is not { } before) return;
        try
        {
            await before().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn("Direct: preparing the direct connection failed", ex);
        }
    }

    private async Task<DirectNetwork> HostOwnNetworkAsync(CancellationToken cancellationToken)
    {
        await PrepareAsync().ConfigureAwait(false);
        var network = await _link!.HostAsync(OwnNetwork(), cancellationToken).ConfigureAwait(false);
        HostedNetwork = network;
        return network;
    }

    /// <summary>This device's own network while it is on (null when off or never started).</summary>
    public DirectNetwork? HostedNetwork { get; private set; }

    private static bool IsComputer(string? kind, string? platform) =>
        platform == "windows" || kind is DeviceKinds.Desktop or DeviceKinds.Laptop;

    private void SetPeerMethod(string peerId, ConnectionMethod method)
    {
        _peerMethod[peerId] = method;
        Changed?.Invoke();
    }

    /// <summary>Stops the link when nobody uses it any more (never one a session, the Phone page or a legacy peer still uses).</summary>
    private void StopLinkIfUnused(bool keepForRunningTransfers = false)
    {
        if (!_linkedPeers.IsEmpty || _link == null) return;
        // Leaving the Phone page mustn't pull the network from under a transfer that runs over it.
        if (keepForRunningTransfers && _transfers.Sessions.Any(s => !s.IsFinished && s.Path == TransferPath.Direct))
        {
            Log.Info("Direct: link kept, a transfer still uses it");
            return;
        }

        try
        {
            _link.Stop();
            HostedNetwork = null;
            Log.Info("Direct: link stopped");
            RotateIfPending();
        }
        catch (Exception ex)
        {
            Log.Warn("Direct: stopping the link failed", ex);
        }

        DirectAddresses.Invalidate();
        _discovery.RescanNetworks(); // back on the normal Wi-Fi: look for devices there right away
        Changed?.Invoke();
    }

    /// <summary>One request over Beam's TLS connection to <paramref name="endpoints"/>, checked against <paramref name="fingerprint"/>.</summary>
    private async Task<(DirectResponseMessage Reply, string Fingerprint)> RequestAsync(IReadOnlyList<IPEndPoint> endpoints, string? fingerprint,
        string name, DirectRequestMessage request, TimeSpan wait, CancellationToken token, Func<IPEndPoint, IPAddress?>? localAddressFor = null)
    {
        await using var connection = await SecureTransport.ConnectAsync(
            _transfers.Identity,
            _transfers.CreateHello(ConnectionPurpose.Direct),
            endpoints,
            fingerprint,
            name,
            token,
            localAddressFor).ConfigureAwait(false);
        await connection.Channel.SendAsync(FrameType.DirectRequest, request, token).ConfigureAwait(false);
        var reply = await connection.Channel.ReadMessageAsync<DirectResponseMessage>(FrameType.DirectResponse, wait, token).ConfigureAwait(false);
        return (reply, connection.RemoteFingerprint);
    }

    private void Report(string message)
    {
        Log.Warn($"Direct: {message}");
        Problem?.Invoke(message);
    }

    private static ConnectionMethod Decode(string? method) => method == "direct" ? ConnectionMethod.Direct : ConnectionMethod.SameNetwork;
}
