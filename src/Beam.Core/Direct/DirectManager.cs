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
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(150); // the phone may first try Wi-Fi Direct, then ask its user to connect
    private static readonly TimeSpan FindTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan PairingLanWait = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan SetupTimeout = TimeSpan.FromSeconds(180);

    /// <summary>How long a send waits for the direct link when it could go over the shared Wi-Fi instead.</summary>
    internal static TimeSpan QuickDirectAttempt { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>After a failed direct link, how long to send over Wi-Fi before trying the direct link again.</summary>
    internal static TimeSpan RetryDirectAfter { get; set; } = TimeSpan.FromSeconds(45);

    internal static TimeSpan UpgradeCheckInterval { get; set; } = TimeSpan.FromSeconds(10);

    private readonly TransferService _transfers;
    private readonly DiscoveryService _discovery;
    private readonly SettingsStore _settings;
    private readonly Func<string> _selfId;
    private readonly IDirectLink? _link;
    private readonly bool _selfIsComputer;
    private readonly ConcurrentDictionary<string, ConnectionMethod> _peerMethod = new();
    private readonly ConcurrentDictionary<string, (int Count, long At)> _directFailures = new();
    private readonly ConcurrentDictionary<string, byte> _upgrading = new();
    private readonly ConcurrentDictionary<string, byte> _linkedPeers = new();

    private readonly SemaphoreSlim _setup = new(1, 1);
    private readonly CancellationTokenSource _stopping = new();
    private readonly ConcurrentDictionary<string, long> _nearbyOnlyDirect = new();
    private readonly ConcurrentDictionary<string, long> _autoJoinedAt = new();
    private volatile bool _receiving;
    private volatile bool _rotatePending;

    // Networks this device joined (to send to their host, or to receive from it): left again once that is done.
    private readonly ConcurrentDictionary<string, byte> _joinedReceivers = new();

    /// <summary>After the last transfer to a receiver whose network this device joined, how long before leaving it.</summary>
    internal static TimeSpan LeaveReceiverAfter { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Joined another device's network to receive, and nothing came: leave it after this long.</summary>
    internal static TimeSpan UnusedJoinTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Set by the nearby (Bluetooth) service: fetches the device's current network details again when joining the
    /// remembered ones failed (its passphrase changed). True when there was something new to try.
    /// </summary>
    internal Func<DeviceInfo, CancellationToken, Task<bool>>? RefreshNetwork { get; set; }

    /// <summary>
    /// Set by the nearby service: asks a device heard over Bluetooth to join this device's network (no shared network to
    /// ask over). Returns its address on this network (null if it didn't say).
    /// </summary>
    internal Func<DeviceInfo, DirectNetwork, CancellationToken, Task<IPAddress?>>? AskToJoinOverBluetooth { get; set; }

    /// <summary>
    /// A sender heard over Bluetooth started its own network and asks this device to join it (a phone sending to this
    /// computer). Returns this device's address on that network.
    /// </summary>
    public async Task<IPAddress?> JoinForNearbyAsync(PairingCode sender, CancellationToken cancellationToken)
    {
        if (_link == null || !CanJoinAny) throw new DirectLinkException(L.T("This device can't connect directly."));
        if (!_settings.Current.AllowDirectSenders) throw NotAllowed();
        var network = sender.Network ?? throw new DirectLinkException("Missing network details.");
        if (_selfIsComputer) await PrepareAsync().ConfigureAwait(false);
        Log.Info($"Direct: {sender.Name} started {network.Ssid}; joining it to receive (asked over Bluetooth)");
        var host = await _link.JoinAsync(network with { HostIsComputer = IsComputer(sender.Kind, null) }, cancellationToken).ConfigureAwait(false);
        HostedNetwork = null;
        _linkedPeers[sender.DeviceId] = 0;
        _joinedReceivers[sender.DeviceId] = 0;
        _ = LeaveReceiverLaterAsync(sender.DeviceId, sender.Name, UnusedJoinTimeout);
        SetPeerMethod(sender.DeviceId, ConnectionMethod.Direct);
        DirectAddresses.Invalidate();
        _discovery.RescanNetworks();
        var device = sender.ToDevice();
        if (host != null)
        {
            _discovery.AddUnicastTarget(host);
            var port = sender.Addresses.Select(a => a.Split(':')).Where(p => p.Length == 2 && int.TryParse(p[1], out _)).Select(p => int.Parse(p[1]))
                .FirstOrDefault(AppInfo.TransferPort);
            device = device with { Endpoints = new[] { new IPEndPoint(host, port) } };
        }

        _discovery.ReportReachable(device);
        var own = host != null ? OwnAddressNear(host) : null;
        Log.Info($"Direct: joined {network.Ssid}; {sender.Name} at {host?.ToString() ?? "unknown"}, this device at {own?.ToString() ?? "unknown"}");
        return own;
    }

    /// <summary>Set by the nearby service: whether the device's own network is on, as its beacon says (null: unknown).</summary>
    internal Func<string, bool?>? PeerNetworkOn { get; set; }

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
        _transfers.BeforeConnect = BeforeConnectAsync;
        _transfers.SessionFinished += OnSessionFinished;
        _transfers.DirectHandler = HandleRequestAsync;
    }

    /// <summary>Stands for "Receive is open" keeping this device's network on.</summary>
    private const string ReceiveUser = "receive";

    /// <summary>Stands for a phone's web browser (Phone page) using this device's direct network.</summary>
    private const string BrowserUser = "phone-browser";


    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan AutoJoinInterval = TimeSpan.FromMinutes(2);

    /// <summary>The preferred method or the direct link changed (raised on a background thread).</summary>
    public event Action? Changed;

    /// <summary>
    /// Runs before this device starts its own network (e.g. so the PC can ask to let phones through Windows Firewall,
    /// right when a direct connection is first used rather than at startup).
    /// </summary>
    public Func<Task>? BeforeHosting { get; set; }

    /// <summary>Setting up a direct link failed; the message is for the user (raised on a background thread).</summary>
    public event Action<string>? Problem;

    public DirectRoles Roles => _link?.Roles ?? DirectRoles.None;

    /// <summary>What this device announces: its roles plus, for computers, whether joining them keeps internet.</summary>
    /// (None when senders may not connect to it directly: they then don't ask.)
    public DirectRoles AnnouncedRoles => Roles == DirectRoles.None || !_settings.Current.AllowDirectSenders ? DirectRoles.None : Roles | Traits();

    /// <summary>This device's address on the network of <c>host</c> (replaceable in tests).</summary>
    internal Func<IPAddress, IPAddress?> OwnAddressNear { get; set; } = DirectAddresses.OwnAddressNear;

    /// <summary>Whether this computer has internet over a cable (replaceable in tests).</summary>
    internal Func<bool> HasWiredInternet { get; set; } = NetworkFacts.HasWiredInternet;

    private DirectRoles Traits()
    {
        var traits = _receiving && Roles.HasFlag(DirectRoles.Host) ? DirectRoles.Receiving : DirectRoles.None;
        if (!_selfIsComputer) return traits;
        if (Roles.HasFlag(DirectRoles.JoinComputers) && HasWiredInternet()) traits |= DirectRoles.Wired;
        if (HostedNetwork?.SharesInternet == true || _link is IInternetSharing { CanShareInternet: true }) traits |= DirectRoles.SharesInternet;
        return traits;
    }

    /// <summary>This device can join another device's network (phones any; computers can always join as ordinary Wi-Fi).</summary>
    private bool CanJoinAny => (Roles & (DirectRoles.Join | DirectRoles.JoinComputers)) != 0;

    /// <summary>Receive is open: this device is visible over Bluetooth and starts its network for senders who want it.</summary>
    public bool IsReceiving => _receiving;

    /// <summary>
    /// Receive was opened or closed. Closing turns off the network started for senders (unless a transfer still uses it).
    /// </summary>
    public void SetReceiving(bool receiving)
    {
        if (_receiving == receiving) return;
        _receiving = receiving;
        Log.Info(receiving ? "Direct: Receive opened" : "Direct: Receive closed");
        if (!receiving)
        {
            if (_linkedPeers.TryRemove(ReceiveUser, out _)) StopLinkIfUnused(keepForRunningTransfers: true);
            RotateIfPending();
        }

        _discovery.AnnounceNow();
        Changed?.Invoke();
    }

    /// <summary>
    /// Starts this device's network for a sender found over Bluetooth (kept while Receive is open). Null when this
    /// device can't host.
    /// </summary>
    public async Task<DirectNetwork?> HostForNearbyAsync(CancellationToken cancellationToken)
    {
        if (_link == null || !CanHost || !_settings.Current.AllowDirectSenders) return null;
        var network = await HostOwnNetworkAsync(cancellationToken).ConfigureAwait(false);
        if (_receiving) _linkedPeers[ReceiveUser] = 0;
        DirectAddresses.Invalidate();
        Changed?.Invoke();
        return network;
    }

    /// <summary>
    /// The network's passphrase went to a device that isn't trusted: give the network a new one once it is off, so
    /// that device can't join it again later without asking.
    /// </summary>
    internal void RotateWhenOff() => _rotatePending = true;

    private void RotateIfPending()
    {
        if (!_rotatePending || HostedNetwork != null) return;
        _rotatePending = false;
        var current = OwnNetwork();
        var fresh = DirectNetwork.Create(_settings.Current.DeviceName);
        _settings.Update(s => s.DirectNetworkPassphrase = fresh.Passphrase);
        Log.Info($"Direct: new passphrase for {current.Ssid}");
    }

    /// <summary>
    /// The receiver's details came over Bluetooth: remember its network and list it, ready to join when sending.
    /// </summary>
    public DeviceInfo AcceptNearby(PairingCode code)
    {
        var roles = !string.IsNullOrEmpty(code.Roles)
            ? DirectRolesText.Parse(code.Roles)
            : DirectRoles.Host | (IsComputer(code.Kind, null) ? DirectRoles.JoinComputers : DirectRoles.Join);
        var device = code.ToDevice() with { Endpoints = Array.Empty<IPEndPoint>(), DirectRoles = roles | DirectRoles.Receiving };
        var port = code.Addresses.Select(a => a.Split(':')).Where(p => p.Length == 2 && int.TryParse(p[1], out _)).Select(p => int.Parse(p[1])).FirstOrDefault();
        if (code.Network is { } network) Remember(code.DeviceId, code.Name, code.Fingerprint, device.Kind, network, port);
        foreach (var address in code.Addresses)
            if (IPAddress.TryParse(address.Split(':')[0], out var ip)) _discovery.AddUnicastTarget(ip); // maybe on the same network after all
        var existing = _discovery.Find(code.DeviceId);
        if (existing is not { Endpoints.Count: > 0 }) _discovery.ReportReachable(device);
        return _discovery.Find(code.DeviceId) ?? device;
    }

    /// <summary>
    /// "Let senders connect directly" was turned off: this device's own network goes off (and it leaves a network it
    /// joined to receive), unless a transfer still runs over it.
    /// </summary>
    internal void OnDirectSendersNotAllowed()
    {
        if (HostedNetwork == null && _joinedReceivers.IsEmpty) return;
        if (_transfers.Sessions.Any(s => !s.IsFinished && s.Path == TransferPath.Direct))
        {
            Log.Info("Direct: senders may no longer connect directly; the network stays until the running transfer is done");
            return;
        }

        Log.Info("Direct: senders may no longer connect directly; turning this device's network off");
        foreach (var peer in _linkedPeers.Keys.ToList()) _linkedPeers.TryRemove(peer, out _);
        _joinedReceivers.Clear();
        StopLinkIfUnused();
    }

    private DirectLinkException NotAllowed() =>
        new(L.T("{0} doesn't let senders connect directly. Beam uses the Wi-Fi network instead.", _settings.Current.DeviceName), declined: true);

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
        if (interrupted > 0)
        {
            Log.Info($"Switching {interrupted} transfer(s) with {peer?.Name ?? peerId} to {method}");
            if (method == ConnectionMethod.Direct) _ = CheckReconnectedDirectlyAsync(peerId, peer?.Name ?? peerId);
        }

        if (method == ConnectionMethod.SameNetwork && _linkedPeers.TryRemove(peerId, out _))
        {
            if (peer != null) await TellAsync(peer, new DirectRequestMessage { Action = DirectActions.Stop }).ConfigureAwait(false);
            StopLinkIfUnused();
        }

        return true;
    }

    /// <summary>This device's own direct network: created once and kept, so paired devices can find it again.</summary>
    public DirectNetwork OwnNetwork()
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

    /// <summary>The direct network of a paired device, if this device knows it.</summary>
    public KnownDirectNetwork? KnownNetworkFor(string deviceId) =>
        _settings.Current.KnownDirectNetworks.FirstOrDefault(k => k.DeviceId == deviceId);

    /// <summary>Paired devices whose direct network is in range but that aren't on this device's network.</summary>
    public bool IsNearbyOnlyDirectly(string deviceId) => _nearbyOnlyDirect.ContainsKey(deviceId);

    /// <summary>Keeps a paired device's direct network, so this device can join it later without any other network.</summary>
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

    /// <summary>Starts the background work: on phones, watching for paired PCs' networks.</summary>
    internal void Start()
    {
        if (_link is IDirectNetworkScanner scanner && Roles.HasFlag(DirectRoles.Join))
            _ = Task.Run(() => WatchKnownNetworksAsync(scanner, _stopping.Token));
    }

    /// <summary>
    /// Beam is closing: turn this device's network off (and leave another's) whoever still uses it, and wait for that
    /// (a bit), so a hotspot doesn't stay on after Beam has closed.
    /// </summary>
    internal async Task StopAsync()
    {
        _stopping.Cancel();
        _linkedPeers.Clear();
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

    /// <summary>
    /// Phones: notices paired PCs whose direct network is in range while they aren't on the phone's network, lists them
    /// as nearby, and (Direct setting) joins one so both sides can send without any Wi-Fi network.
    /// </summary>
    private async Task WatchKnownNetworksAsync(IDirectNetworkScanner scanner, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                CheckKnownNetworks(scanner);
            }
            catch (Exception ex)
            {
                Log.Warn("Direct: checking for paired direct networks failed", ex);
            }

            try
            {
                await Task.Delay(ScanInterval, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    internal void CheckKnownNetworks(IDirectNetworkScanner scanner)
    {
        var known = _settings.Current.KnownDirectNetworks;
        if (known.Count == 0) return;
        var visible = scanner.VisibleNetworks();
        foreach (var network in known)
        {
            var found = _discovery.Find(network.DeviceId);
            var onNetwork = found != null && found.Endpoints.Count > 0;
            // With Same Wi-Fi chosen, a computer is only found on the shared network, never through its own network.
            if (onNetwork || !visible.Contains(network.Ssid) || MethodFor(network.DeviceId) != ConnectionMethod.Direct)
            {
                _nearbyOnlyDirect.TryRemove(network.DeviceId, out _);
                continue;
            }

            // In range, but not on this phone's network: list it, ready to connect directly.
            _nearbyOnlyDirect[network.DeviceId] = Environment.TickCount64;
            _discovery.ReportReachable(new DeviceInfo
            {
                Id = network.DeviceId,
                Name = network.Name,
                Fingerprint = network.Fingerprint,
                Kind = string.IsNullOrEmpty(network.Kind) ? DeviceKinds.Desktop : network.Kind,
                Platform = "windows",
                DirectRoles = DirectRoles.Host,
            });

            var last = _autoJoinedAt.GetValueOrDefault(network.DeviceId);
            if (Environment.TickCount64 - last > AutoJoinInterval.TotalMilliseconds)
            {
                _autoJoinedAt[network.DeviceId] = Environment.TickCount64;
                Log.Info($"Direct: {network.Name}'s network {network.Ssid} is in range; joining it");
                _ = JoinKnownInBackgroundAsync(network.DeviceId);
            }
        }
    }

    private async Task JoinKnownInBackgroundAsync(string deviceId)
    {
        if (_discovery.Find(deviceId) is not { } device) return;
        try
        {
            await ConnectAsync(device, _stopping.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (!_stopping.IsCancellationRequested)
        {
            Log.Warn($"Direct: joining {device.Name}'s network failed: {ex.Message}");
        }
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
        var network = await HostOwnNetworkAsync(cancellationToken).ConfigureAwait(false);
        _linkedPeers[BrowserUser] = 0;
        _ = Task.Delay(3000).ContinueWith(_ => Log.Info($"Direct network {network.Ssid} for a phone's browser. Adapters: {DirectAddresses.Describe()}"), TaskScheduler.Default);
        DirectAddresses.Invalidate();
        Changed?.Invoke();
        return network;
    }

    /// <summary>The Phone page no longer needs the direct network (stopped unless a transfer still uses it).</summary>
    public void StopForBrowser()
    {
        if (_linkedPeers.TryRemove(BrowserUser, out _)) StopLinkIfUnused(keepForRunningTransfers: true);
    }

    /// <summary>
    /// The direct link came up, but if the transfer resumed over Wi-Fi the other device couldn't be reached on it
    /// (usually a firewall that blocks the direct network): say so instead of silently staying on Wi-Fi.
    /// </summary>
    private async Task CheckReconnectedDirectlyAsync(string peerId, string peerName)
    {
        var deadline = Environment.TickCount64 + 60_000;
        while (Environment.TickCount64 < deadline)
        {
            await Task.Delay(500).ConfigureAwait(false);
            var sessions = _transfers.Sessions.Where(s => s.PeerId == peerId && !s.IsFinished).ToList();
            if (sessions.Count == 0 || MethodFor(peerId) != ConnectionMethod.Direct) return;
            var reconnected = sessions.Where(s => !s.IsSwitchingPath && s.Path != TransferPath.Unknown).ToList();
            if (reconnected.Count == 0) continue;
            if (reconnected.All(s => s.Path == TransferPath.Direct)) return;
            Log.Warn($"Direct: the link to {peerName} is up but the transfer resumed over the network");
            Problem?.Invoke(L.T("The direct connection to {0} started, but Beam couldn't reach it. Check that Beam is allowed through the firewall.", peerName));
            return;
        }
    }

    /// <summary>
    /// "Switch to Wi-Fi" / "Switch to direct" on a transfer is for the transfers running with that device: once the last
    /// one is done, the Connection setting decides again.
    /// </summary>
    private void OnSessionFinished(TransferSession session)
    {
        var peerId = session.PeerId;
        if (_transfers.Sessions.Any(s => s.PeerId == peerId && !s.IsFinished)) return;
        if (_joinedReceivers.ContainsKey(peerId)) _ = LeaveReceiverLaterAsync(peerId, session.PeerName);
        if (_peerMethod.TryGetValue(peerId, out var method) && method != _settings.Current.Connection && _peerMethod.TryRemove(peerId, out _))
        {
            Log.Info($"Direct: transfers with {session.PeerName} done; back to the Connection setting ({_settings.Current.Connection})");
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// Sending to a device whose network this one joined is done: after a while with nothing more to send, leave its
    /// network, so this device is back on its own Wi-Fi (and internet).
    /// </summary>
    private async Task LeaveReceiverLaterAsync(string peerId, string peerName, TimeSpan? after = null)
    {
        try
        {
            await Task.Delay(after ?? LeaveReceiverAfter, _stopping.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (_transfers.Sessions.Any(s => s.PeerId == peerId && !s.IsFinished)) return;
        if (!_joinedReceivers.TryRemove(peerId, out _)) return;
        _peerMethod.TryRemove(peerId, out _);
        if (!_linkedPeers.TryRemove(peerId, out _)) return;
        Log.Info($"Direct: nothing more to transfer with {peerName}; leaving its network");
        StopLinkIfUnused();
    }

    /// <summary>
    /// The Connection setting changed: forget per-device choices and move what this device is sending to the chosen
    /// connection (the sender decides; what it receives follows the other device's choice).
    /// </summary>
    internal void OnSettingChanged(ConnectionMethod method)
    {
        var peers = _transfers.Sessions.Where(s => !s.IsFinished && s.Direction == TransferDirection.Send).Select(s => s.PeerId).Distinct().ToList();
        foreach (var peer in _peerMethod.Keys.ToList()) _peerMethod.TryRemove(peer, out _);
        Changed?.Invoke();
        foreach (var peer in peers) _ = SwitchAsync(peer, method);

        if (method == ConnectionMethod.SameNetwork && _transfers.Sessions.All(s => s.IsFinished))
        {
            foreach (var peer in _joinedReceivers.Keys.ToList())
            {
                _joinedReceivers.TryRemove(peer, out _);
                _linkedPeers.TryRemove(peer, out _);
            }

            StopLinkIfUnused();
        }
    }

    /// <summary>Brings up a direct link with <paramref name="peer"/> (no-op when one is already up).</summary>
    public Task ConnectAsync(DeviceInfo peer, CancellationToken cancellationToken) => ConnectAsync(peer, viaKnownNetwork: false, cancellationToken);

    /// <summary>
    /// Right after pairing by code. With <paramref name="connectDirectly"/> (the code was shown with the computer's
    /// network on) joins that network and switches to it, even on the same Wi-Fi. Otherwise joins it only when the
    /// computer isn't on this phone's network but its network is in range. False when there was nothing to do.
    /// </summary>
    public async Task<bool> ConnectAfterPairingAsync(DeviceInfo peer, bool connectDirectly, CancellationToken cancellationToken)
    {
        if (_link is not { } link || !Roles.HasFlag(DirectRoles.Join) || KnownNetworkFor(peer.Id) is not { } known) return false;
        if (HasDirectPath(_discovery.Find(peer.Id) ?? peer)) return false;
        if (!connectDirectly)
        {
            var deadline = Environment.TickCount64 + (long)PairingLanWait.TotalMilliseconds;
            bool? visible = null; // null: this device can't see Wi-Fi scan results (then just try)
            while (Environment.TickCount64 < deadline)
            {
                // Answered over the shared network (or already linked): nothing to join.
                if (_discovery.Find(peer.Id) is { Endpoints.Count: > 0 } found && (!IsNearbyOnlyDirectly(peer.Id) || HasDirectPath(found))) return false;
                if (link is IDirectNetworkScanner scanner && scanner.VisibleNetworks() is { Count: > 0 } networks)
                    visible = visible == true || networks.Contains(known.Ssid);
                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            }

            if (visible == false)
            {
                Log.Info($"Direct: {peer.Name} isn't on this network and its network {known.Ssid} isn't in range yet");
                return false;
            }
        }

        Log.Info($"Direct: joining {peer.Name}'s network {known.Ssid} after pairing{(connectDirectly ? " (asked by its code)" : "")}");
        try
        {
            await ConnectAsync(peer, viaKnownNetwork: true, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            Report(ex);
            return false;
        }

        // Send over the direct link from now on (shown as Direct in Settings, where it can be switched back), and
        // tell the computer to do the same.
        _peerMethod[peer.Id] = ConnectionMethod.Direct;
        if (_settings.Current.Connection != ConnectionMethod.Direct) _settings.Update(s => s.Connection = ConnectionMethod.Direct);
        Changed?.Invoke();
        if (_discovery.Find(peer.Id) is { } reachable)
            await TellAsync(reachable, new DirectRequestMessage { Action = DirectActions.Prefer, Method = Encode(ConnectionMethod.Direct) }).ConfigureAwait(false);
        return true;
    }

    private async Task ConnectAsync(DeviceInfo peer, bool viaKnownNetwork, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(SetupTimeout);
        var token = timeout.Token;
        await _setup.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var current = await WithWorkingDirectPathAsync(peer, token).ConfigureAwait(false);
            if (HasDirectPath(current)) return;
            var link = _link ?? throw new DirectLinkException(L.T("This device can't connect directly."));
            IPAddress? host = null;
            // Computers use a remembered network only to reach a device they can't reach otherwise, or one with Receive
            // open; between two computers "Connect directly" asks the other side each time.
            var known = Roles.HasFlag(DirectRoles.Join) || (CanJoinAny && (current.Endpoints.Count == 0 || current.DirectRoles.HasFlag(DirectRoles.Receiving)))
                ? KnownNetworkFor(peer.Id)
                : null;
            var joinedKnown = false;
            var joinedTheirs = false;
            var plan = PlanFor(peer);
            Log.Info($"Direct: connecting to {peer.Name}: {Describe(plan)} (its roles: {DirectRolesText.Format(current.DirectRoles)}; " +
                     $"addresses: {(current.Endpoints.Count == 0 ? "none" : string.Join(", ", current.Endpoints))})");
            // This phone hosts for a computer: its network is what they use, not the computer's.
            if (plan == Plan.HostForPeer && !_selfIsComputer && current.Endpoints.Count > 0) known = null;
            // Join the PC's known network straight away when there's no shared network to ask over, and when the direct
            // link was just lost (the phone usually fell back to its normal Wi-Fi, where asking the PC fails or is slow).
            var rejoining = _linkedPeers.ContainsKey(peer.Id);
            // A paired computer's network that this phone can see: join it right away, without asking the computer first
            // (asking needs the shared Wi-Fi, which may be slow or blocked).
            var inSight = known != null && _link is IDirectNetworkScanner scanner && scanner.VisibleNetworks().Contains(known.Ssid);
            if (known != null && (viaKnownNetwork || rejoining || inSight || current.Endpoints.Count == 0 || IsNearbyOnlyDirectly(peer.Id)))
            {
                Log.Info($"Direct: {(rejoining ? "rejoining" : "joining")} {peer.Name}'s known network {known.Ssid}");
                try
                {
                    if (_selfIsComputer) await PrepareAsync().ConfigureAwait(false);
                    host = await link.JoinAsync(new DirectNetwork(known.Ssid, known.Passphrase) { HostIsComputer = IsComputer(known.Kind, current.Platform) }, token)
                        .ConfigureAwait(false);
                    HostedNetwork = null; // joining stops this device's own network
                    plan = Plan.None;
                    joinedKnown = true;
                    joinedTheirs = true;
                }
                catch (DirectLinkException ex) when ((rejoining || inSight) && !viaKnownNetwork && plan == Plan.JoinPeer && current.Endpoints.Count > 0
                                                    && !token.IsCancellationRequested)
                {
                    // Its network may have changed (new password): ask it over the shared network instead.
                    Log.Warn($"Direct: joining {known.Ssid} failed ({ex.Message}); asking {peer.Name} instead");
                }
            }

            switch (plan)
            {
                case Plan.None when joinedKnown:
                    break;

                case Plan.JoinPeer:
                {
                    Log.Info($"Direct: asking {peer.Name} to start a direct network");
                    var reply = await RequestAsync(peer, new DirectRequestMessage { Action = DirectActions.Host }, token).ConfigureAwait(false);
                    if (!reply.Ok || string.IsNullOrEmpty(reply.Ssid) || string.IsNullOrEmpty(reply.Passphrase))
                        throw new DirectLinkException(reply.Message ?? L.T("{0} couldn't start a direct connection.", peer.Name), declined: reply.Declined);
                    joinedTheirs = true;
                    var network = new DirectNetwork(reply.Ssid, reply.Passphrase) { HostIsComputer = IsComputer(current.Kind, current.Platform) };
                    Remember(peer.Id, peer.Name, peer.Fingerprint, peer.Kind, network);
                    if (_selfIsComputer) await PrepareAsync().ConfigureAwait(false);
                    host = await link.JoinAsync(network, token).ConfigureAwait(false);
                    HostedNetwork = null; // joining stops this device's own network
                    break;
                }

                case Plan.HostForPeer:
                {
                    var network = await HostOwnNetworkAsync(token).ConfigureAwait(false);
                    Log.Info($"Direct: started {network.Ssid}; asking {peer.Name} to join");
                    try
                    {
                        var reply = await RequestAsync(peer, new DirectRequestMessage { Action = DirectActions.Join, Ssid = network.Ssid, Passphrase = network.Passphrase }, token)
                            .ConfigureAwait(false);
                        if (!reply.Ok) throw new DirectLinkException(reply.Message ?? L.T("{0} couldn't join the direct connection.", peer.Name), declined: reply.Declined);
                        if (IPAddress.TryParse(reply.Address, out var joined)) host = joined; // where the other device is now
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
            if (joinedTheirs) _joinedReceivers[peer.Id] = 0;
            await FindOnDirectLinkAsync(peer, host, token).ConfigureAwait(false);
            Log.Info($"Direct: connected to {peer.Name} directly");
            Changed?.Invoke();
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            _discovery.RescanNetworks();
            throw new DirectLinkException(L.T("Connecting directly to {0} took too long.", peer.Name));
        }
        catch
        {
            _discovery.RescanNetworks(); // the attempt may have moved this device off its Wi-Fi and back
            throw;
        }
        finally
        {
            _setup.Release();
        }
    }

    private async Task BeforeConnectAsync(DeviceInfo peer, CancellationToken token)
    {
        var current = await WithWorkingDirectPathAsync(peer, token).ConfigureAwait(false);
        if (HasDirectPath(current)) return;
        if (current.Endpoints.Count == 0 && CanJoinAny
            && (KnownNetworkFor(peer.Id) != null
                || (PlanFor(current) == Plan.HostForPeer && AskToJoinOverBluetooth != null && PeerNetworkOn?.Invoke(peer.Id) != null)))
        {
            // Not on this device's network at all (found over Bluetooth, or its network is in range): joining its
            // network is the only way to reach it, whatever the Connection setting says.
            await ConnectOnlyWayAsync(current, token).ConfigureAwait(false);
            return;
        }

        // Only when Direct is chosen (in Settings, or for this device): with Same Wi-Fi, Beam never goes looking for
        // the other device's own network.
        if (MethodFor(peer.Id) != ConnectionMethod.Direct) return;
        if (!CanConnectDirectly(current) && KnownNetworkFor(peer.Id) == null) return;

        // Reachable over the shared Wi-Fi too: don't hold the transfer up. Try the direct link briefly (unless that just
        // failed), else send over Wi-Fi and keep trying to switch to the direct link in the background.
        var overNetwork = current.Endpoints.Any(e => !_transfers.IsDirectAddress(e.Address));
        if (overNetwork)
        {
            if (DirectFailedRecently(peer.Id) || !LooksInRange(peer.Id))
            {
                WatchForDirectLink(peer.Id);
                return;
            }
        }

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        if (overNetwork) limit.CancelAfter(QuickDirectAttempt);
        try
        {
            await ConnectAsync(peer, limit.Token).ConfigureAwait(false);
            _directFailures.TryRemove(peer.Id, out _);
        }
        catch (Exception ex) when (!token.IsCancellationRequested)
        {
            DirectFailed(peer.Id);
            if (overNetwork)
            {
                Log.Info($"Direct: no direct link to {peer.Name} yet ({ex.Message}); sending over Wi-Fi meanwhile");
                WatchForDirectLink(peer.Id);
            }
            else
            {
                // Nothing else to send over; the transfer reports that it can't reach the device.
                Report(ex);
            }
        }
    }

    private async Task ConnectOnlyWayAsync(DeviceInfo peer, CancellationToken token)
    {
        // This device hosts (a phone sending to a computer): start its network, then ask the other device over Bluetooth
        // to join it.
        if (PlanFor(peer) == Plan.HostForPeer && AskToJoinOverBluetooth is { } askToJoin && PeerNetworkOn?.Invoke(peer.Id) != null)
        {
            var network = await HostOwnNetworkAsync(token).ConfigureAwait(false);
            Log.Info($"Direct: started {network.Ssid}; asking {peer.Name} over Bluetooth to join it");
            var theirs = await askToJoin(peer, network, token).ConfigureAwait(false);
            _linkedPeers[peer.Id] = 0;
            await FindOnDirectLinkAsync(peer, theirs, token).ConfigureAwait(false);
            Log.Info($"Direct: connected to {peer.Name} directly (it joined this device's network)");
            _directFailures.TryRemove(peer.Id, out _);
            Changed?.Invoke();
            return;
        }

        // Its network is off (it starts it on request): ask it over Bluetooth first instead of failing to join.
        if (PeerNetworkOn?.Invoke(peer.Id) == false && RefreshNetwork is { } prepare)
        {
            Log.Info($"Direct: {peer.Name}'s network is off; asking it over Bluetooth to start it");
            await prepare(peer, token).ConfigureAwait(false);
        }

        try
        {
            await ConnectAsync(peer, viaKnownNetwork: true, token).ConfigureAwait(false);
        }
        catch (DirectLinkException ex) when (!ex.Declined && RefreshNetwork is { } refresh && !token.IsCancellationRequested)
        {
            // Its passphrase may have changed since: ask it again over Bluetooth, then try once more.
            Log.Info($"Direct: joining {peer.Name}'s network failed ({ex.Message}); asking it for its current details");
            if (!await refresh(peer, token).ConfigureAwait(false)) throw;
            await ConnectAsync(peer, viaKnownNetwork: true, token).ConfigureAwait(false);
        }

        _directFailures.TryRemove(peer.Id, out _);
    }

    /// <summary>
    /// The device as known now, minus direct addresses that no longer answer (its network was stopped, or this device
    /// left it): otherwise Beam would think the direct link is up and keep "switching" to it.
    /// </summary>
    private async Task<DeviceInfo> WithWorkingDirectPathAsync(DeviceInfo peer, CancellationToken token)
    {
        var current = _discovery.Find(peer.Id) ?? peer;
        var direct = current.Endpoints.Where(e => _transfers.IsDirectAddress(e.Address)).ToList();
        if (direct.Count == 0) return current;
        foreach (var endpoint in direct)
            if (await CanReachAsync(endpoint, token).ConfigureAwait(false)) return current;
        Log.Info($"Direct: {peer.Name} no longer answers at {string.Join(", ", direct)}; connecting again");
        _discovery.DropEndpoints(peer.Id, e => _transfers.IsDirectAddress(e.Address));
        _linkedPeers.TryRemove(peer.Id, out _);
        return _discovery.Find(peer.Id) ?? current with { Endpoints = current.Endpoints.Except(direct).ToList() };
    }

    private void DirectFailed(string peerId) =>
        _directFailures.AddOrUpdate(peerId, _ => (1, Environment.TickCount64), (_, f) => (f.Count + 1, Environment.TickCount64));

    /// <summary>
    /// A direct link failed lately: wait before trying again (longer after each failure, as each try can briefly
    /// take the phone off its Wi-Fi), and send over Wi-Fi meanwhile.
    /// </summary>
    private bool DirectFailedRecently(string peerId)
    {
        if (!_directFailures.TryGetValue(peerId, out var failed)) return false;
        var wait = RetryDirectAfter.TotalMilliseconds * Math.Min(1 << Math.Min(failed.Count - 1, 3), 8);
        return Environment.TickCount64 - failed.At < wait;
    }

    /// <summary>False when this phone can see Wi-Fi networks and the device's known network isn't among them.</summary>
    private bool LooksInRange(string peerId)
    {
        if (_link is not IDirectNetworkScanner scanner || KnownNetworkFor(peerId) is not { } known) return true;
        var visible = scanner.VisibleNetworks();
        return visible.Count == 0 || visible.Contains(known.Ssid); // nothing visible: can't tell (no scan permission)
    }

    /// <summary>
    /// Direct is chosen but transfers with <paramref name="peerId"/> run over Wi-Fi (the direct link failed or was
    /// out of range): keep trying in the background, and move them to the direct link once it's up.
    /// </summary>
    private void WatchForDirectLink(string peerId)
    {
        if (!_upgrading.TryAdd(peerId, 0)) return;
        _ = Task.Run(async () =>
        {
            try
            {
                var token = _stopping.Token;
                while (!token.IsCancellationRequested)
                {
                    await Task.Delay(UpgradeCheckInterval, token).ConfigureAwait(false);
                    if (MethodFor(peerId) != ConnectionMethod.Direct) return;
                    var running = _transfers.Sessions.Where(s => s.PeerId == peerId && !s.IsFinished).ToList();
                    if (running.Count == 0) return;
                    if (running.All(s => s.Path == TransferPath.Direct)) return;
                    if (_discovery.Find(peerId) is not { } peer) continue;
                    if (DirectFailedRecently(peerId) || !LooksInRange(peerId)) continue;

                    using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
                    limit.CancelAfter(QuickDirectAttempt);
                    try
                    {
                        Log.Info($"Direct: trying the direct link to {peer.Name} again");
                        await ConnectAsync(peer, limit.Token).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (!token.IsCancellationRequested)
                    {
                        DirectFailed(peerId);
                        Log.Info($"Direct: still no direct link to {peer.Name} ({ex.Message})");
                        continue;
                    }

                    _directFailures.TryRemove(peerId, out _);
                    var moved = _transfers.InterruptConnections(peerId);
                    Log.Info($"Direct: link to {peer.Name} is up; moving {moved} transfer(s) to it");
                    if (moved > 0) _ = CheckReconnectedDirectlyAsync(peerId, peer.Name);
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                // stopping
            }
            finally
            {
                _upgrading.TryRemove(peerId, out _);
            }
        });
    }

    private async Task FindOnDirectLinkAsync(DeviceInfo peer, IPAddress? host, CancellationToken token)
    {
        DirectAddresses.Invalidate();
        // The host's address on the new network is known: try Beam's port there right away instead of waiting for
        // discovery (which can take several seconds after a network change).
        var port = (_discovery.Find(peer.Id) ?? peer).Endpoints.FirstOrDefault()?.Port
                   ?? (KnownNetworkFor(peer.Id) is { Port: > 0 } known ? known.Port : AppInfo.TransferPort);
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

            if (_discovery.Find(peer.Id) is { } found && HasDirectPath(found))
            {
                Log.Info($"Direct: found {peer.Name} at {string.Join(", ", found.Endpoints)}");
                return;
            }

            // (Not one of this device's own addresses: two Windows hotspots both use 192.168.137.1.)
            if (host != null && _transfers.IsDirectAddress(host) && !DirectAddresses.IsOwnAddress(host)
                && await CanReachAsync(new IPEndPoint(host, port), token).ConfigureAwait(false))
            {
                var current = _discovery.Find(peer.Id) ?? peer;
                _discovery.ReportReachable(current with { Endpoints = new[] { new IPEndPoint(host, port) }.Concat(current.Endpoints).Take(4).ToList() });
                Log.Info($"Direct: reached {peer.Name} at {host}:{port}");
                return;
            }

            await Task.Delay(250, token).ConfigureAwait(false);
        }

        Log.Warn($"Direct: {peer.Name} not found on the direct link. Adapters: {DirectAddresses.Describe()}");
        throw new DirectLinkException(L.T("The direct connection to {0} started, but Beam couldn't reach it. Check that Beam is allowed through the firewall.", peer.Name));
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

    /// <summary>The joined network went away: forget its addresses, so transfers reconnect (re-joining, or over Wi-Fi).</summary>
    private void OnLinkLost()
    {
        Log.Warn("Direct: the direct network was lost");
        DirectAddresses.Invalidate();
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
            response = await HandleAsync(request, peerId, peerName, token, connection.RemoteFingerprint, connection.RemoteHello.Kind).ConfigureAwait(false);
        }
        catch (Exception ex) when (!token.IsCancellationRequested)
        {
            Log.Warn($"Direct: {request.Action} for {peerName} failed: {ex.Message}");
            response = new DirectResponseMessage
            {
                Ok = false,
                Message = ex is DirectLinkException ? ex.Message : L.T("Connecting directly took too long."),
                Declined = ex is DirectLinkException { Declined: true },
            };
        }

        await connection.Channel.SendAsync(FrameType.DirectResponse, response, token).ConfigureAwait(false);
    }

    private async Task<DirectResponseMessage> HandleAsync(DirectRequestMessage request, string peerId, string peerName, CancellationToken serviceToken,
        string peerFingerprint = "", string peerKind = "")
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(serviceToken);
        timeout.CancelAfter(SetupTimeout);
        var token = timeout.Token;
        switch (request.Action)
        {
            case DirectActions.Host:
            {
                if (_link == null || !Roles.HasFlag(DirectRoles.Host)) throw new DirectLinkException(L.T("This device can't connect directly."));
                if (!_settings.Current.AllowDirectSenders) throw NotAllowed();
                // A sender wants to connect directly: this device (the receiver) starts its network, without asking. It
                // doesn't disturb this device's own Wi-Fi, and the files themselves still need this device's approval.
                var network = await HostOwnNetworkAsync(token).ConfigureAwait(false);
                _linkedPeers[peerId] = 0;
                SetPeerMethod(peerId, ConnectionMethod.Direct);
                Log.Info($"Direct: started {network.Ssid} for {peerName}");
                return new DirectResponseMessage { Ok = true, Ssid = network.Ssid, Passphrase = network.Passphrase };
            }

            case DirectActions.Join:
            {
                if (_link == null || !CanJoinAny)
                    throw new DirectLinkException(L.T("This device can't connect directly."));
                if (!_settings.Current.AllowDirectSenders) throw NotAllowed();
                if (string.IsNullOrEmpty(request.Ssid) || string.IsNullOrEmpty(request.Passphrase)) throw new DirectLinkException("Missing network details.");
                // Allowed by "Let senders connect directly"; this device leaves the network again once the transfer is done.
                if (_selfIsComputer) await PrepareAsync().ConfigureAwait(false);
                Log.Info($"Direct: {peerName} started {request.Ssid}; joining it to receive (this device's own network, if on, stops)");
                var joining = new DirectNetwork(request.Ssid, request.Passphrase) { HostIsComputer = IsComputer(peerKind, _discovery.Find(peerId)?.Platform) };
                Remember(peerId, peerName, peerFingerprint, peerKind, joining);
                var host = await _link.JoinAsync(joining, token).ConfigureAwait(false);
                HostedNetwork = null; // joining stops this device's own network
                _linkedPeers[peerId] = 0;
                _joinedReceivers[peerId] = 0;
                _ = LeaveReceiverLaterAsync(peerId, peerName, UnusedJoinTimeout);
                SetPeerMethod(peerId, ConnectionMethod.Direct);
                DirectAddresses.Invalidate();
                _discovery.RescanNetworks();
                if (host != null)
                {
                    _discovery.AddUnicastTarget(host);
                    // Reach the host at its address on this network from now on (e.g. to tell it "back to Wi-Fi"): this
                    // device may have left the network where it knew the host's other addresses.
                    if (_discovery.Find(peerId) is { } hostDevice)
                    {
                        var port = hostDevice.Endpoints.FirstOrDefault()?.Port ?? AppInfo.TransferPort;
                        _discovery.ReportReachable(hostDevice with
                        {
                            Endpoints = new[] { new IPEndPoint(host, port) }.Concat(hostDevice.Endpoints.Where(e => !e.Address.Equals(host))).Take(4).ToList(),
                        });
                    }
                }

                // Tell the host where to find this device on its network: it reaches it right away, without waiting
                // for discovery (which a phone on a computer's Wi-Fi can't always announce itself on).
                var own = host != null ? OwnAddressNear(host) : null;
                return new DirectResponseMessage { Ok = true, Address = own?.ToString() };
            }

            case DirectActions.Prefer:
                // A phone that joined this device's own network (e.g. after scanning its code) keeps it on.
                if (Decode(request.Method) == ConnectionMethod.Direct && Roles.HasFlag(DirectRoles.Host)) _linkedPeers[peerId] = 0;
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

    /// <summary>This computer's own network while it is on (null when off or never started).</summary>
    public DirectNetwork? HostedNetwork { get; private set; }

    private static bool IsComputer(string? kind, string? platform) =>
        platform == "windows" || kind is DeviceKinds.Desktop or DeviceKinds.Laptop;

    private void SetPeerMethod(string peerId, ConnectionMethod method)
    {
        _peerMethod[peerId] = method;
        Changed?.Invoke();
    }

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

    /// <summary>
    /// How to connect to <paramref name="peer"/> (the device this one sends to). A phone and a computer: the phone hosts
    /// (its Wi-Fi Direct group, which the computer joins in seconds without any prompt; a phone joining a computer's
    /// hotspot needs Android's approval and fails on some phones), whichever of them sends. Otherwise the receiver
    /// hosts and the sender joins; when the receiver can't host, this device hosts and the receiver joins.
    /// </summary>
    private Plan PlanFor(DeviceInfo peer)
    {
        var theirs = peer.DirectRoles;
        var peerIsComputer = IsComputer(peer.Kind, peer.Platform);
        var canJoinPeer = theirs.HasFlag(DirectRoles.Host) && CanJoinAny;
        var canHostForPeer = Roles.HasFlag(DirectRoles.Host) && (theirs & (DirectRoles.Join | DirectRoles.JoinComputers)) != 0;
        if (!_selfIsComputer && peerIsComputer && canHostForPeer) return Plan.HostForPeer;
        if (_selfIsComputer && !peerIsComputer && canJoinPeer) return Plan.JoinPeer;
        return canJoinPeer ? Plan.JoinPeer : canHostForPeer ? Plan.HostForPeer : Plan.None;
    }

    private static string Describe(Plan plan) => plan switch
    {
        Plan.JoinPeer => "it hosts, this device joins",
        Plan.HostForPeer => "this device hosts, it joins",
        _ => "no direct connection possible",
    };

    private static string Encode(ConnectionMethod method) => method == ConnectionMethod.Direct ? "direct" : "network";

    private static ConnectionMethod Decode(string? method) => method == "direct" ? ConnectionMethod.Direct : ConnectionMethod.SameNetwork;

    private enum Plan
    {
        None,
        JoinPeer,
        HostForPeer,
    }
}
