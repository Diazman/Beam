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
    private static readonly TimeSpan SetupTimeout = TimeSpan.FromSeconds(180);

    private readonly TransferService _transfers;
    private readonly DiscoveryService _discovery;
    private readonly SettingsStore _settings;
    private readonly Func<string> _selfId;
    private readonly IDirectLink? _link;
    private readonly ConcurrentDictionary<string, ConnectionMethod> _peerMethod = new();
    private readonly ConcurrentDictionary<string, byte> _linkedPeers = new();
    private readonly SemaphoreSlim _setup = new(1, 1);
    private readonly CancellationTokenSource _stopping = new();
    private readonly ConcurrentDictionary<string, long> _nearbyOnlyDirect = new();
    private readonly ConcurrentDictionary<string, long> _autoJoinedAt = new();
    private int _availabilityRunning;

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

    /// <summary>Stands for "keep the network on so paired phones can find this PC" (Connection setting: Direct).</summary>
    private const string AvailableUser = "available";

    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan AutoJoinInterval = TimeSpan.FromMinutes(2);

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
    public void Remember(string deviceId, string name, string fingerprint, string kind, DirectNetwork network)
    {
        if (!Roles.HasFlag(DirectRoles.Join) || string.IsNullOrEmpty(deviceId) || string.IsNullOrEmpty(fingerprint)) return;
        var known = KnownNetworkFor(deviceId);
        if (known != null && known.Ssid == network.Ssid && known.Passphrase == network.Passphrase && known.Name == name) return;
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
            });
        });
        Log.Info($"Direct: remembered {name}'s direct network {network.Ssid}");
    }

    /// <summary>Starts the background work: keeping this PC's network on (Direct setting) and, on phones, watching for paired PCs.</summary>
    internal void Start()
    {
        UpdateAvailability();
        if (_link is IDirectNetworkScanner scanner && Roles.HasFlag(DirectRoles.Join))
            _ = Task.Run(() => WatchKnownNetworksAsync(scanner, _stopping.Token));
    }

    internal void Stop()
    {
        _stopping.Cancel();
        if (_linkedPeers.TryRemove(AvailableUser, out _)) StopLinkIfUnused();
    }

    /// <summary>
    /// A PC set to Direct keeps its own network on, so paired phones can find and join it with no Wi-Fi network around.
    /// (Only devices that can't join others' networks, i.e. PCs: a phone keeping a network on would cost battery.)
    /// </summary>
    private void UpdateAvailability()
    {
        if (_link == null || Roles != DirectRoles.Host) return;
        if (_settings.Current.Connection == ConnectionMethod.Direct)
        {
            if (Interlocked.Exchange(ref _availabilityRunning, 1) == 0) _ = Task.Run(KeepAvailableAsync);
        }
        else if (_linkedPeers.TryRemove(AvailableUser, out _))
        {
            StopLinkIfUnused();
        }
    }

    private async Task KeepAvailableAsync()
    {
        try
        {
            while (!_stopping.IsCancellationRequested && _settings.Current.Connection == ConnectionMethod.Direct)
            {
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(30));
                    var network = await _link!.HostAsync(OwnNetwork(), timeout.Token).ConfigureAwait(false);
                    if (_linkedPeers.TryAdd(AvailableUser, 0))
                    {
                        Log.Info($"Direct: {network.Ssid} is on for paired phones");
                        DirectAddresses.Invalidate();
                        Changed?.Invoke();
                    }
                }
                catch (Exception ex) when (!_stopping.IsCancellationRequested)
                {
                    Log.Warn($"Direct: keeping the direct network on failed: {ex.Message}");
                }

                await Task.Delay(TimeSpan.FromSeconds(60), _stopping.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // stopping
        }
        finally
        {
            Volatile.Write(ref _availabilityRunning, 0);
        }
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
            if (onNetwork || !visible.Contains(network.Ssid))
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
            if (_settings.Current.Connection == ConnectionMethod.Direct && Environment.TickCount64 - last > AutoJoinInterval.TotalMilliseconds)
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
        var network = await _link.HostAsync(OwnNetwork(), cancellationToken).ConfigureAwait(false);
        _linkedPeers[BrowserUser] = 0;
        _ = Task.Delay(3000).ContinueWith(_ => Log.Info($"Direct network {network.Ssid} for a phone's browser. Adapters: {DirectAddresses.Describe()}"), TaskScheduler.Default);
        DirectAddresses.Invalidate();
        Changed?.Invoke();
        return network;
    }

    /// <summary>The Phone page no longer needs the direct network (stopped unless a transfer still uses it).</summary>
    public void StopForBrowser()
    {
        if (_linkedPeers.TryRemove(BrowserUser, out _)) StopLinkIfUnused();
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

        UpdateAvailability();
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
            var current = _discovery.Find(peer.Id) ?? peer;
            if (HasDirectPath(current)) return;
            var link = _link ?? throw new DirectLinkException(L.T("This device can't connect directly."));
            IPAddress? host = null;
            var known = Roles.HasFlag(DirectRoles.Join) ? KnownNetworkFor(peer.Id) : null;
            var plan = PlanFor(peer);
            if (known != null && (current.Endpoints.Count == 0 || IsNearbyOnlyDirectly(peer.Id)))
            {
                // No shared network to ask over, but this phone knows the PC's network (paired): join it directly.
                Log.Info($"Direct: joining {peer.Name}'s known network {known.Ssid}");
                host = await link.JoinAsync(new DirectNetwork(known.Ssid, known.Passphrase), token).ConfigureAwait(false);
                plan = Plan.None;
            }

            switch (plan)
            {
                case Plan.None when known != null && host != null:
                    break;

                case Plan.JoinPeer:
                {
                    Log.Info($"Direct: asking {peer.Name} to start a direct network");
                    var reply = await RequestAsync(peer, new DirectRequestMessage { Action = DirectActions.Host }, token).ConfigureAwait(false);
                    if (!reply.Ok || string.IsNullOrEmpty(reply.Ssid) || string.IsNullOrEmpty(reply.Passphrase))
                        throw new DirectLinkException(reply.Message ?? L.T("{0} couldn't start a direct connection.", peer.Name));
                    var network = new DirectNetwork(reply.Ssid, reply.Passphrase);
                    Remember(peer.Id, peer.Name, peer.Fingerprint, peer.Kind, network);
                    host = await link.JoinAsync(network, token).ConfigureAwait(false);
                    break;
                }

                case Plan.HostForPeer:
                {
                    var network = await link.HostAsync(OwnNetwork(), token).ConfigureAwait(false);
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
        var current = _discovery.Find(peer.Id) ?? peer;
        if (HasDirectPath(current)) return;
        // Direct when chosen, or when it's the only way: a paired PC seen only by its direct network.
        var onlyDirect = (current.Endpoints.Count == 0 || IsNearbyOnlyDirectly(peer.Id)) && KnownNetworkFor(peer.Id) != null;
        if (!onlyDirect && (MethodFor(peer.Id) != ConnectionMethod.Direct || !CanConnectDirectly(peer))) return;
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

            if (_discovery.Find(peer.Id) is { } found && HasDirectPath(found))
            {
                Log.Info($"Direct: found {peer.Name} at {string.Join(", ", found.Endpoints)}");
                return;
            }

            await Task.Delay(250, token).ConfigureAwait(false);
        }

        Log.Warn($"Direct: {peer.Name} not found on the direct link. Adapters: {DirectAddresses.Describe()}");
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
            response = await HandleAsync(request, peerId, peerName, token, connection.RemoteFingerprint, connection.RemoteHello.Kind).ConfigureAwait(false);
        }
        catch (Exception ex) when (!token.IsCancellationRequested)
        {
            Log.Warn($"Direct: {request.Action} for {peerName} failed: {ex.Message}");
            response = new DirectResponseMessage { Ok = false, Message = ex is DirectLinkException ? ex.Message : L.T("Connecting directly took too long.") };
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
                var network = await _link.HostAsync(OwnNetwork(), token).ConfigureAwait(false);
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
                var joining = new DirectNetwork(request.Ssid, request.Passphrase);
                Remember(peerId, peerName, peerFingerprint, peerKind, joining);
                var host = await _link.JoinAsync(joining, token).ConfigureAwait(false);
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
