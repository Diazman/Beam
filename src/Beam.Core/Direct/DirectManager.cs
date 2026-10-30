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
/// One exception to who hosts: a sender that can't start a network (an iPhone) sending to a computer that offers to
/// host (<see cref="DirectRoles.HostsForSenders"/>) asks it to start its network and joins it
/// (<see cref="DirectActions.HostForSender"/>, over Beam's TLS connection only). Verification is the same.
/// </para>
/// <para>
/// Beam 1.5.3 and older let the receiver host instead ("host", "join", "prefer" and "stop" requests). Those requests are
/// refused: such a device updates Beam to connect directly, or both use the same Wi-Fi.
/// </para>
/// </summary>
public sealed partial class DirectManager
{
    private readonly TransferService _transfers;
    private readonly DiscoveryService _discovery;
    private readonly SettingsStore _settings;
    private readonly Func<string> _selfId;
    private readonly IDirectLink? _link;
    private readonly bool _selfIsComputer;

    /// <summary>
    /// Who uses the direct link (this device's network, or one it joined) right now: direct sessions ("session:…" for
    /// networks this device hosts, "rx:…" for one it joined). The link is stopped only when nobody is left.
    /// </summary>
    private readonly ConcurrentDictionary<string, byte> _linkedPeers = new();

    private readonly CancellationTokenSource _stopping = new();
    private volatile bool _receiving;

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
        _transfers.DirectHandler = HandleRequestAsync;
    }

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
    /// keeps internet. None when it can't make direct links.
    /// </summary>
    public DirectRoles AnnouncedRoles => Roles == DirectRoles.None ? DirectRoles.None : Roles | DirectRoles.SenderHosted | Traits();

    /// <summary>This device's address on the network it hosts (replaceable in tests).</summary>
    internal Func<IPAddress?> OwnHostedAddress { get; set; } = DirectAddresses.OwnHostedAddress;

    /// <summary>This device's address on the network of <c>host</c> (replaceable in tests).</summary>
    internal Func<IPAddress, IPAddress?> OwnAddressNear { get; set; } = DirectAddresses.OwnAddressNear;

    private DirectRoles Traits()
    {
        var traits = _receiving ? DirectRoles.Receiving : DirectRoles.None;
        if (!_selfIsComputer) return traits;
        if (HostedNetwork?.SharesInternet == true) traits |= DirectRoles.SharesInternet;
        if (CanHostForSenders) traits |= DirectRoles.HostsForSenders;
        return traits;
    }

    /// <summary>
    /// A computer that can start its network for a sender that can't start one (an iPhone) and joins it instead
    /// (<see cref="DirectRoles.HostsForSenders"/>).
    /// </summary>
    private bool CanHostForSenders => _selfIsComputer && CanHost;

    /// <summary>This device can join another device's network (phones any; computers as an ordinary Wi-Fi client).</summary>
    private bool CanJoinAny => (Roles & (DirectRoles.Join | DirectRoles.JoinComputers)) != 0;

    /// <summary>Receive is open: this device is visible (Wi-Fi and Bluetooth) and accepts direct sessions from senders.</summary>
    public bool IsReceiving => _receiving;

    /// <summary>Receive was opened or closed. Opening it only makes this device visible: no network starts.</summary>
    public void SetReceiving(bool receiving)
    {
        if (_receiving == receiving) return;
        _receiving = receiving;
        Log.Info(receiving ? "Direct: Receive opened (visible; no network is started for that)" : "Direct: Receive closed");
        _discovery.AnnounceNow();
        Changed?.Invoke();
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

    private DirectLinkException NotAllowed() =>
        new(L.T("{0} doesn't let senders connect directly.", _settings.Current.DeviceName), declined: true);

    /// <summary>This device can make direct links at all (Wi-Fi Direct hardware and OS support).</summary>
    public bool IsSupported => Roles != DirectRoles.None;

    /// <summary>The device's addresses, those of the preferred path first (the others stay a fallback). Same Network sends use this as before.</summary>
    public DeviceInfo Order(DeviceInfo device)
    {
        var preferDirect = _settings.Current.Connection == ConnectionMethod.Direct;
        var ordered = device.Endpoints.OrderBy(e => _transfers.IsDirectAddress(e.Address) == preferDirect ? 0 : 1).ToList();
        return device with { Endpoints = ordered };
    }

    /// <summary>The name of this device's direct networks (each session gets its own passphrase): created once and kept.</summary>
    public string OwnNetworkName()
    {
        lock (_ownNetworkGate)
        {
            var current = _settings.Current;
            if (!string.IsNullOrEmpty(current.DirectNetworkName)) return current.DirectNetworkName;
            var created = DirectNetwork.Create(current.DeviceName).Ssid;
            _settings.Update(s => s.DirectNetworkName = created);
            return created;
        }
    }

    private readonly object _ownNetworkGate = new();

    /// <summary>A paired device, if this device knows it (from its code).</summary>
    public KnownDirectNetwork? KnownNetworkFor(string deviceId) =>
        _settings.Current.KnownDirectNetworks.FirstOrDefault(k => k.DeviceId == deviceId);

    /// <summary>Keeps a paired device (its code was scanned), so it is known by name and certificate when heard over Bluetooth.</summary>
    public void Remember(string deviceId, string name, string fingerprint, string kind)
    {
        if (!CanJoinAny || string.IsNullOrEmpty(deviceId) || string.IsNullOrEmpty(fingerprint)) return;
        var known = KnownNetworkFor(deviceId);
        if (known != null && known.Name == name && known.Kind == kind
            && string.Equals(known.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase)) return;
        _settings.Update(s =>
        {
            s.KnownDirectNetworks.RemoveAll(k => k.DeviceId == deviceId);
            s.KnownDirectNetworks.Add(new KnownDirectNetwork
            {
                DeviceId = deviceId,
                Name = name,
                Fingerprint = fingerprint.ToLowerInvariant(),
                Kind = kind,
            });
        });
        Log.Info($"Direct: remembered {name}");
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

    /// <summary>The Connection setting changed: it applies to the next Send (running transfers keep their mode).</summary>
    internal void OnSettingChanged(ConnectionMethod method)
    {
        Log.Info($"Direct: sending mode is now {method}; running transfers keep theirs");
        Changed?.Invoke();
    }

    /// <summary>The joined network went away: forget its addresses; a direct session on it is interrupted.</summary>
    private void OnLinkLost()
    {
        Log.Warn("Direct: the direct network was lost");
        DirectAddresses.ForgetDirectNetworks(hosted: false);
        DirectAddresses.Invalidate();
        OnJoinedNetworkLost();
        OnReceiversNetworkLost();
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
                : OlderSenderRefused(request.Action, peerName);
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

    /// <summary>A receiver-hosted request from Beam 1.5.3 or older ("host", "join", "prefer", "stop"): refused.</summary>
    private DirectResponseMessage OlderSenderRefused(string action, string peerName)
    {
        Log.Info($"Direct: refused \"{action}\" from {peerName}: an older Beam's request (1.5.3 and before), not supported any more");
        return new DirectResponseMessage { Ok = false, Message = OlderBeamMessage() };
    }

    /// <summary>For a device with Beam 1.5.3 or older, which asks the receiver to host.</summary>
    internal string OlderBeamMessage() =>
        L.T("{0} needs a newer Beam on your device to connect directly. Update Beam, or connect both devices to the same Wi-Fi.", _settings.Current.DeviceName);

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

    /// <summary>This device's own network while it is on (null when off or never started).</summary>
    public DirectNetwork? HostedNetwork { get; private set; }

    private static bool IsComputer(string? kind, string? platform) =>
        platform == "windows" || kind is DeviceKinds.Desktop or DeviceKinds.Laptop;

    /// <summary>Stops the link when nobody uses it any more (never one a session still uses).</summary>
    private void StopLinkIfUnused()
    {
        if (!_linkedPeers.IsEmpty || _link == null) return;
        try
        {
            _link.Stop();
            HostedNetwork = null;
            DirectAddresses.ForgetDirectNetworks(hosted: false); // left the network it joined
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

}
