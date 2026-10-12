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

    // Two computers connect directly only when a user asks ("Connect directly"): which side hosts, per computer.
    private readonly ConcurrentDictionary<string, Plan> _computerPlan = new();
    private readonly ConcurrentDictionary<string, byte> _pressing = new(); // computers this one is connecting to right now
    private readonly ConcurrentDictionary<string, long> _hostRequestedBy = new(); // when a computer last asked this one to host
    private readonly SemaphoreSlim _setup = new(1, 1);
    private readonly CancellationTokenSource _stopping = new();
    private readonly ConcurrentDictionary<string, long> _nearbyOnlyDirect = new();
    private readonly ConcurrentDictionary<string, long> _autoJoinedAt = new();
    private int _availabilityRunning;

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

    /// <summary>
    /// Runs before this device starts its own network (e.g. so the PC can ask to let phones through Windows Firewall,
    /// right when a direct connection is first used rather than at startup).
    /// </summary>
    public Func<Task>? BeforeHosting { get; set; }

    /// <summary>Setting up a direct link failed; the message is for the user (raised on a background thread).</summary>
    public event Action<string>? Problem;

    public DirectRoles Roles => _link?.Roles ?? DirectRoles.None;

    /// <summary>What this device announces: its roles plus, for computers, whether joining them keeps internet.</summary>
    public DirectRoles AnnouncedRoles => Roles == DirectRoles.None ? DirectRoles.None : Roles | Traits();

    /// <summary>This device's address on the network of <c>host</c> (replaceable in tests).</summary>
    internal Func<IPAddress, IPAddress?> OwnAddressNear { get; set; } = DirectAddresses.OwnAddressNear;

    /// <summary>Whether this computer has internet over a cable (replaceable in tests).</summary>
    internal Func<bool> HasWiredInternet { get; set; } = NetworkFacts.HasWiredInternet;

    private DirectRoles Traits()
    {
        if (!_selfIsComputer) return DirectRoles.None;
        var traits = DirectRoles.None;
        if (Roles.HasFlag(DirectRoles.JoinComputers) && HasWiredInternet()) traits |= DirectRoles.Wired;
        if (HostedNetwork?.SharesInternet == true || _link is IInternetSharing { CanShareInternet: true }) traits |= DirectRoles.SharesInternet;
        return traits;
    }

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
    /// Asked before this device starts its network for another device that isn't trusted, or (computers) before it
    /// leaves its Wi-Fi to join another computer's network. Null (tests): allowed.
    /// </summary>
    public Func<DirectApproval, Task<bool>>? Approve { get; set; }

    /// <summary>How long an untouched "Connect directly" link stays up with nothing to transfer.</summary>
    internal static TimeSpan ComputerLinkIdleTimeout { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>How long the other computer may be gone before this one drops its side of the link.</summary>
    internal static TimeSpan ComputerLinkLostTimeout { get; set; } = TimeSpan.FromMinutes(3);

    /// <summary>Both are computers that can connect directly: show "Connect directly" for <paramref name="peer"/>.</summary>
    public bool CanConnectComputer(DeviceInfo peer) =>
        _selfIsComputer && IsComputer(peer.Kind, peer.Platform)
                        && Roles.HasFlag(DirectRoles.Host) && Roles.HasFlag(DirectRoles.JoinComputers)
                        && peer.DirectRoles.HasFlag(DirectRoles.Host);

    /// <summary>Connected to <paramref name="peerId"/> through "Connect directly" (or connecting).</summary>
    public bool IsComputerLinked(string peerId) => _computerPlan.ContainsKey(peerId);

    /// <summary>Connecting to <paramref name="peerId"/> right now.</summary>
    public bool IsConnectingComputer(string peerId) => _pressing.ContainsKey(peerId);

    /// <summary>
    /// Joining <paramref name="peer"/>'s network would leave this computer without internet: its internet comes over
    /// Wi-Fi (no cable) and the other computer's network doesn't share internet.
    /// </summary>
    public bool JoinLeavesWithoutInternet(DeviceInfo peer) =>
        !HasWiredInternet() && !peer.DirectRoles.HasFlag(DirectRoles.SharesInternet);

    /// <summary>
    /// "Connect directly" pressed for another computer on the same network: this computer joins the other's network
    /// (the other only starts its network, which doesn't disturb its Wi-Fi). If this one can't join or the other can't
    /// host, the roles swap, which the other computer's user has to allow (it would leave its Wi-Fi). Transfers running
    /// with it move to the direct link. False (with <see cref="Problem"/> raised) when it couldn't be set up.
    /// </summary>
    public async Task<bool> ConnectComputerAsync(string peerId, CancellationToken cancellationToken = default)
    {
        if (_discovery.Find(peerId) is not { } peer || !CanConnectComputer(peer))
        {
            Problem?.Invoke(L.T("That device isn't nearby right now."));
            return false;
        }

        if (!_pressing.TryAdd(peerId, 0)) return false;
        Changed?.Invoke();
        Exception? failure = null;
        try
        {
            foreach (var plan in new[] { Plan.JoinPeer, Plan.HostForPeer })
            {
                if (plan == Plan.HostForPeer && !peer.DirectRoles.HasFlag(DirectRoles.JoinComputers)) break;
                _computerPlan[peerId] = plan;
                try
                {
                    Log.Info($"Direct: connecting to {peer.Name} ({(plan == Plan.JoinPeer ? "joining its network" : "it joins this computer's network")})");
                    await ConnectAsync(peer, viaKnownNetwork: false, cancellationToken).ConfigureAwait(false);
                    failure = null;
                    break;
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested
                                           && ex is DirectLinkException or TransferException or IOException or TimeoutException or OperationCanceledException)
                {
                    failure ??= ex;
                    // Both pressed at the same moment and the other one won: it is joining this computer's network.
                    if (plan == Plan.JoinPeer && await OtherSideIsConnectingAsync(peerId, cancellationToken).ConfigureAwait(false))
                    {
                        _computerPlan[peerId] = Plan.HostForPeer;
                        failure = null;
                        break;
                    }

                    if (ex is not DirectLinkException { Declined: false }) break; // unreachable, or its user said no: swapping roles won't help
                    Log.Info($"Direct: {ex.Message}");
                }
            }
        }
        finally
        {
            _pressing.TryRemove(peerId, out _);
        }

        if (failure != null)
        {
            _computerPlan.TryRemove(peerId, out _);
            Changed?.Invoke();
            Report(failure);
            return false;
        }

        _linkedPeers[peerId] = 0;
        _peerMethod[peerId] = ConnectionMethod.Direct;
        Changed?.Invoke();
        await TellAsync(peer, new DirectRequestMessage { Action = DirectActions.Prefer, Method = Encode(ConnectionMethod.Direct) }).ConfigureAwait(false);
        var moved = _transfers.InterruptConnections(peerId);
        if (moved > 0) _ = CheckReconnectedDirectlyAsync(peerId, peer.Name);
        _ = WatchComputerLinkAsync(peerId, pressedHere: true);
        return true;
    }

    /// <summary>"Disconnect": back to the shared Wi-Fi; the joining computer returns to its own Wi-Fi.</summary>
    public async Task DisconnectComputerAsync(string peerId)
    {
        _computerPlan.TryRemove(peerId, out _);
        await SwitchAsync(peerId, ConnectionMethod.SameNetwork).ConfigureAwait(false);
        _peerMethod.TryRemove(peerId, out _);
        Changed?.Invoke();
    }

    /// <summary>After this side's join was turned down: did the other computer meanwhile ask this one to host (it won)?</summary>
    private async Task<bool> OtherSideIsConnectingAsync(string peerId, CancellationToken token)
    {
        var deadline = Environment.TickCount64 + 3000;
        while (Environment.TickCount64 < deadline)
        {
            if (Environment.TickCount64 - _hostRequestedBy.GetValueOrDefault(peerId, long.MinValue / 2) < 30_000)
            {
                // Wait for its join to finish.
                var until = Environment.TickCount64 + (long)FindTimeout.TotalMilliseconds + 30_000;
                while (Environment.TickCount64 < until)
                {
                    if (_discovery.Find(peerId) is { } found && HasDirectPath(found)) return true;
                    await Task.Delay(250, token).ConfigureAwait(false);
                }

                return false;
            }

            await Task.Delay(200, token).ConfigureAwait(false);
        }

        return false;
    }

    /// <summary>
    /// Keeps a "Connect directly" link tidy: the computer where it was pressed disconnects after a while with nothing
    /// to transfer (the joining computer is off its Wi-Fi meanwhile); the other drops its side when that one is gone.
    /// </summary>
    private async Task WatchComputerLinkAsync(string peerId, bool pressedHere)
    {
        var idleSince = Environment.TickCount64;
        var goneSince = 0L;
        try
        {
            while (_computerPlan.ContainsKey(peerId) && !_stopping.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, ComputerLinkLostTimeout.TotalSeconds / 4)), _stopping.Token).ConfigureAwait(false);
                if (_transfers.Sessions.Any(s => s.PeerId == peerId && !s.IsFinished)) idleSince = Environment.TickCount64;
                var reachable = _discovery.Find(peerId) is { } found && HasDirectPath(found);
                goneSince = reachable ? 0 : goneSince == 0 ? Environment.TickCount64 : goneSince;

                if (pressedHere && Environment.TickCount64 - idleSince > ComputerLinkIdleTimeout.TotalMilliseconds)
                {
                    Log.Info($"Direct: nothing to transfer with {peerId} for a while; disconnecting");
                    await DisconnectComputerAsync(peerId).ConfigureAwait(false);
                    return;
                }

                if (!pressedHere && goneSince != 0 && Environment.TickCount64 - goneSince > ComputerLinkLostTimeout.TotalMilliseconds
                    && !_transfers.Sessions.Any(s => s.PeerId == peerId && !s.IsFinished))
                {
                    Log.Info($"Direct: {peerId} is gone; dropping the direct link");
                    _computerPlan.TryRemove(peerId, out _);
                    _peerMethod.TryRemove(peerId, out _);
                    if (_linkedPeers.TryRemove(peerId, out _)) StopLinkIfUnused();
                    Changed?.Invoke();
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // stopping
        }
    }

    /// <summary>The other side is trusted (paired, or "always accept" chosen for it).</summary>
    private bool IsTrusted(string fingerprint) =>
        !string.IsNullOrEmpty(fingerprint)
        && _settings.Current.TrustedDevices.Any(t => string.Equals(t.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase));

    private async Task<bool> AskAsync(DirectApproval approval)
    {
        if (Approve is not { } approve) return true;
        try
        {
            return await approve(approval).WaitAsync(TimeSpan.FromSeconds(90)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn($"Direct: no answer to {approval.PeerName}'s request ({ex.Message})");
            return false;
        }
    }

    /// <summary>
    /// Switches how this device talks to <paramref name="peerId"/>, including transfers already running: they
    /// reconnect over the new method and continue where they stopped. False (with <see cref="Problem"/> raised)
    /// when a direct link couldn't be set up; the transfers then continue over the network.
    /// </summary>
    public async Task<bool> SwitchAsync(string peerId, ConnectionMethod method, CancellationToken cancellationToken = default)
    {
        // Two computers: the one where Direct was chosen joins the other's network.
        if (method == ConnectionMethod.Direct && _discovery.Find(peerId) is { } computer && CanConnectComputer(computer))
            return IsComputerLinked(peerId) || await ConnectComputerAsync(peerId, cancellationToken).ConfigureAwait(false);
        if (method == ConnectionMethod.SameNetwork) _computerPlan.TryRemove(peerId, out _);
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
        if (_link == null || !Roles.HasFlag(DirectRoles.Host) || Roles.HasFlag(DirectRoles.Join)) return;
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
                    var network = await HostOwnNetworkAsync(timeout.Token).ConfigureAwait(false);
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

    /// <summary>The Connection setting changed: forget per-device choices and switch running transfers.</summary>
    internal void OnSettingChanged(ConnectionMethod method)
    {
        // Computers connected with "Connect directly" stay connected: that was asked for them specifically.
        var peers = _transfers.Sessions.Where(s => !s.IsFinished).Select(s => s.PeerId).Distinct().Where(p => !IsComputerLinked(p)).ToList();
        foreach (var peer in _peerMethod.Keys.Where(p => !IsComputerLinked(p)).ToList()) _peerMethod.TryRemove(peer, out _);
        Changed?.Invoke();
        foreach (var peer in peers)
        {
            // Between computers the setting doesn't start direct links; "Connect directly" does.
            if (method == ConnectionMethod.Direct && _discovery.Find(peer) is { } computer && CanConnectComputer(computer)) continue;
            _ = SwitchAsync(peer, method);
        }

        if (method == ConnectionMethod.SameNetwork && peers.Count == 0)
        {
            foreach (var peer in _linkedPeers.Keys.Where(p => !IsComputerLinked(p)).ToList()) _linkedPeers.TryRemove(peer, out _);
            StopLinkIfUnused();
        }

        UpdateAvailability();
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
            var current = _discovery.Find(peer.Id) ?? peer;
            if (HasDirectPath(current)) return;
            var link = _link ?? throw new DirectLinkException(L.T("This device can't connect directly."));
            IPAddress? host = null;
            var known = Roles.HasFlag(DirectRoles.Join) ? KnownNetworkFor(peer.Id) : null;
            var plan = PlanFor(peer);
            // Join the PC's known network straight away when there's no shared network to ask over, and when the direct
            // link was just lost (the phone usually fell back to its normal Wi-Fi, where asking the PC fails or is slow).
            var rejoining = _linkedPeers.ContainsKey(peer.Id);
            if (known != null && (viaKnownNetwork || rejoining || current.Endpoints.Count == 0 || IsNearbyOnlyDirectly(peer.Id)))
            {
                Log.Info($"Direct: {(rejoining ? "rejoining" : "joining")} {peer.Name}'s known network {known.Ssid}");
                try
                {
                    host = await link.JoinAsync(new DirectNetwork(known.Ssid, known.Passphrase) { HostIsComputer = IsComputer(known.Kind, current.Platform) }, token)
                        .ConfigureAwait(false);
                    plan = Plan.None;
                }
                catch (DirectLinkException ex) when (rejoining && !viaKnownNetwork && plan == Plan.JoinPeer && current.Endpoints.Count > 0 && !token.IsCancellationRequested)
                {
                    // Its network may have changed (new password): ask it over the shared network instead.
                    Log.Warn($"Direct: rejoining {known.Ssid} failed ({ex.Message}); asking {peer.Name} instead");
                }
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
                        throw new DirectLinkException(reply.Message ?? L.T("{0} couldn't start a direct connection.", peer.Name), declined: reply.Declined);
                    var network = new DirectNetwork(reply.Ssid, reply.Passphrase) { HostIsComputer = IsComputer(current.Kind, current.Platform) };
                    Remember(peer.Id, peer.Name, peer.Fingerprint, peer.Kind, network);
                    if (_selfIsComputer) await PrepareAsync().ConfigureAwait(false);
                    host = await link.JoinAsync(network, token).ConfigureAwait(false);
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
        var current = _discovery.Find(peer.Id) ?? peer;
        if (HasDirectPath(current)) return;
        // Only when Direct is chosen (in Settings, or for this device): with Same Wi-Fi, Beam never goes looking for
        // the other device's own network.
        if (MethodFor(peer.Id) != ConnectionMethod.Direct) return;
        if (!CanConnectDirectly(current) && KnownNetworkFor(peer.Id) == null) return;
        // Moving the other device onto this one's network by itself (a PC sending to a phone) only for a paired device.
        if (PlanFor(current) == Plan.HostForPeer && !IsTrusted(current.Fingerprint)) return;

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
        var port = (_discovery.Find(peer.Id) ?? peer).Endpoints.FirstOrDefault()?.Port ?? AppInfo.TransferPort;
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

            if (host != null && _transfers.IsDirectAddress(host) && await CanReachAsync(new IPEndPoint(host, port), token).ConfigureAwait(false))
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
                var fromComputer = IsComputer(peerKind, _discovery.Find(peerId)?.Platform);
                if (fromComputer && _selfIsComputer)
                {
                    // Both pressed "Connect directly" at the same moment: the lower id joins, the other hosts.
                    if (_pressing.ContainsKey(peerId) && string.CompareOrdinal(_selfId(), peerId) < 0)
                        throw new DirectLinkException(L.T("{0} is connecting to you at the same time.", _settings.Current.DeviceName));
                    _hostRequestedBy[peerId] = Environment.TickCount64;
                }

                if (!IsTrusted(peerFingerprint) && !await AskAsync(new DirectApproval(peerName, LeavesWiFi: false, KeepsInternet: true)).ConfigureAwait(false))
                    throw new DirectLinkException(L.T("{0} didn't allow the direct connection.", _settings.Current.DeviceName), declined: true);
                var network = await HostOwnNetworkAsync(token).ConfigureAwait(false);
                _linkedPeers[peerId] = 0;
                if (fromComputer && _selfIsComputer && _computerPlan.TryAdd(peerId, Plan.HostForPeer))
                    _ = WatchComputerLinkAsync(peerId, pressedHere: false);
                SetPeerMethod(peerId, ConnectionMethod.Direct);
                Log.Info($"Direct: started {network.Ssid} for {peerName}");
                return new DirectResponseMessage { Ok = true, Ssid = network.Ssid, Passphrase = network.Passphrase };
            }

            case DirectActions.Join:
            {
                if (_link == null || !CanJoin(IsComputer(peerKind, _discovery.Find(peerId)?.Platform)))
                    throw new DirectLinkException(L.T("This device can't connect directly."));
                if (string.IsNullOrEmpty(request.Ssid) || string.IsNullOrEmpty(request.Passphrase)) throw new DirectLinkException("Missing network details.");
                var hostIsComputer = IsComputer(peerKind, _discovery.Find(peerId)?.Platform);
                if (_selfIsComputer && hostIsComputer)
                {
                    // Joining takes this computer off its Wi-Fi: its user decides (unless its internet comes over a cable).
                    var keepsInternet = HasWiredInternet() || _discovery.Find(peerId)?.DirectRoles.HasFlag(DirectRoles.SharesInternet) == true;
                    if (!HasWiredInternet() && !await AskAsync(new DirectApproval(peerName, LeavesWiFi: true, keepsInternet)).ConfigureAwait(false))
                        throw new DirectLinkException(L.T("{0} didn't allow the direct connection.", _settings.Current.DeviceName), declined: true);
                }

                if (_selfIsComputer) await PrepareAsync().ConfigureAwait(false);
                Log.Info($"Direct: joining {request.Ssid} for {peerName}");
                var joining = new DirectNetwork(request.Ssid, request.Passphrase) { HostIsComputer = IsComputer(peerKind, _discovery.Find(peerId)?.Platform) };
                Remember(peerId, peerName, peerFingerprint, peerKind, joining);
                var host = await _link.JoinAsync(joining, token).ConfigureAwait(false);
                _linkedPeers[peerId] = 0;
                if (_selfIsComputer && hostIsComputer && _computerPlan.TryAdd(peerId, Plan.JoinPeer))
                    _ = WatchComputerLinkAsync(peerId, pressedHere: false);
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
                _computerPlan.TryRemove(peerId, out _);
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

    /// <summary>This device can join the direct network of a computer (<paramref name="hostIsComputer"/>) or a phone.</summary>
    private bool CanJoin(bool hostIsComputer) =>
        Roles.HasFlag(DirectRoles.Join) || (hostIsComputer && Roles.HasFlag(DirectRoles.JoinComputers));

    private Plan PlanFor(DeviceInfo peer)
    {
        var mine = Roles;
        var theirs = peer.DirectRoles;
        var peerIsComputer = IsComputer(peer.Kind, peer.Platform);
        // Two computers: only through "Connect directly" (where the computer it was pressed on joins), never by themselves.
        if (_selfIsComputer && peerIsComputer) return _computerPlan.TryGetValue(peer.Id, out var asked) ? asked : Plan.None;
        var canJoinPeer = CanJoin(peerIsComputer) && theirs.HasFlag(DirectRoles.Host);
        var canHostForPeer = mine.HasFlag(DirectRoles.Host)
                             && (theirs.HasFlag(DirectRoles.Join) || (_selfIsComputer && theirs.HasFlag(DirectRoles.JoinComputers)));
        if (canJoinPeer && canHostForPeer)
        {
            // A computer hosts for a phone (a phone's own network costs its battery and takes the computer off its Wi-Fi).
            if (_selfIsComputer != peerIsComputer) return _selfIsComputer ? Plan.HostForPeer : Plan.JoinPeer;
            return string.CompareOrdinal(_selfId(), peer.Id) < 0 ? Plan.HostForPeer : Plan.JoinPeer; // two phones
        }

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
