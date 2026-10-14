using System.Net;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using Beam.Core.Diagnostics;
using Beam.Core.Direct;
using Beam.Core.Discovery;
using Beam.Core.Localization;
using Beam.Core.Protocol;
using Beam.Core.Settings;

namespace Beam.Core.Nearby;

/// <summary>
/// Finding and reaching devices with no shared network, over Bluetooth LE: the bootstrap a QR code used to be.
/// <para>
/// Receiver (Receive open): advertises a beacon and answers handshakes. A handshake starts its own network (if it
/// isn't on yet) and answers with its pairing code, encrypted for that sender only.
/// </para>
/// <para>
/// Sender (Send open): lists the beacons it hears. Sending to one runs the handshake, remembers the receiver's
/// network, and from there the direct manager joins it and the transfer runs over Beam's usual encrypted connection,
/// checked against the receiver's certificate fingerprint and approved on the receiver as always.
/// </para>
/// </summary>
public sealed class NearbyService : INearbyResponder, IBluetoothDirectControl
{
    /// <summary>Platform of the list entries for receivers only heard over Bluetooth (not yet known by their real id).</summary>
    public const string BluetoothPlatform = "bluetooth";

    private const string PlaceholderPrefix = "bt-";
    private const int MaxHandshakes = 8;
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan HostTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan SightingLifetime = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan JoinTimeout = TimeSpan.FromSeconds(100);

    private readonly INearbyRadio? _radio;
    private readonly DirectManager _direct;
    private readonly DiscoveryService _discovery;
    private readonly SettingsStore _settings;
    private readonly Func<string> _selfId;
    private readonly Func<bool, PairingCode> _pairingCode;
    private readonly bool _selfIsComputer;
    private readonly ConcurrentDictionary<string, (Task<byte[]> Answer, long At)> _answers = new();
    private readonly ConcurrentDictionary<string, NearbyHandshake.Keys> _keys = new(); // per sender, for its join request
    private readonly ConcurrentDictionary<string, (NearbySighting Sighting, long At)> _sightings = new(); // by id hash
    private readonly object _gate = new();
    private byte[]? _advertised;
    private bool _receiving;
    private CancellationTokenSource? _grace;
    private readonly Func<bool> _transfersRunning;
    private bool _looking;

    internal NearbyService(INearbyRadio? radio, DirectManager direct, DiscoveryService discovery, SettingsStore settings, Func<string> selfId,
        Func<bool, PairingCode> pairingCode, bool selfIsComputer, Func<bool> transfersRunning)
    {
        _transfersRunning = transfersRunning;
        _radio = radio;
        _direct = direct;
        _discovery = discovery;
        _settings = settings;
        _selfId = selfId;
        _pairingCode = pairingCode;
        _selfIsComputer = selfIsComputer;
        if (radio != null)
        {
            radio.StateChanged += OnRadioStateChanged;
            direct.Changed += () => UpdateAdvertisement();
            direct.Bluetooth = this;
        }
    }

    /// <summary>Bluetooth's state (Unsupported when the platform has none).</summary>
    public NearbyRadioState State => _radio?.State ?? NearbyRadioState.Unsupported;

    /// <summary>State or the list of nearby receivers changed (any thread).</summary>
    public event Action? Changed;

    public bool IsReceiving => _receiving;

    /// <summary>Phones: asks for the Bluetooth permission, then starts what was waiting for it.</summary>
    public async Task<bool> RequestPermissionAsync()
    {
        if (_radio == null) return false;
        var granted = await _radio.RequestPermissionAsync().ConfigureAwait(false);
        OnRadioStateChanged();
        return granted;
    }

    /// <summary>
    /// After Receive is left, how long this device stays receivable (visible over Bluetooth, its network on for senders)
    /// before turning that off. Running transfers keep it on until they finish. Closing Beam turns it off at once.
    /// </summary>
    internal TimeSpan ReceiveGrace { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Receive opened or closed: become visible over Bluetooth (and over Wi-Fi as receiving), or stop after a while.</summary>
    public void SetReceiving(bool receiving)
    {
        lock (_gate)
        {
            _grace?.Cancel();
            _grace = null;
            if (_receiving == receiving) return;
            if (!receiving && ReceiveGrace > TimeSpan.Zero)
            {
                var grace = _grace = new CancellationTokenSource();
                _ = StopAfterGraceAsync(grace.Token);
                Log.Info($"Nearby: Receive left; still receivable for {ReceiveGrace.TotalMinutes:0} min");
                return;
            }

            _receiving = receiving;
        }

        Apply(receiving);
    }

    private void Apply(bool receiving)
    {
        _direct.SetReceiving(receiving);
        if (!receiving) _answers.Clear();
        UpdateAdvertisement();
    }

    private async Task StopAfterGraceAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(ReceiveGrace, token).ConfigureAwait(false);
            while (_transfersRunning()) await Task.Delay(TimeSpan.FromSeconds(30), token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return; // Receive opened again
        }

        lock (_gate)
        {
            if (token.IsCancellationRequested || !_receiving) return;
            _receiving = false;
            _grace = null;
        }

        Log.Info("Nearby: no longer receivable (Receive was left a while ago)");
        Apply(false);
    }

    /// <summary>Send opened or closed: listen for receivers over Bluetooth.</summary>
    public void SetLooking(bool looking)
    {
        lock (_gate)
        {
            if (_looking == looking) return;
            _looking = looking;
        }

        UpdateScanning();
    }

    /// <summary>Turns Bluetooth on (Windows), or shows the system's prompt to (Android). True when it is on or turning on.</summary>
    public async Task<bool> TurnOnAsync()
    {
        if (_radio == null || _radio.State == NearbyRadioState.Unsupported) return false;
        if (_radio.State == NearbyRadioState.Ready) return true;
        try
        {
            return await _radio.TurnOnAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn($"Nearby: turning Bluetooth on failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>A list entry for a receiver heard only over Bluetooth (to send to it, <see cref="ResolveAsync"/> first).</summary>
    public static bool IsPlaceholder(DeviceInfo device) => device.Platform == BluetoothPlatform;

    /// <summary>Whether this receiver is being heard over Bluetooth right now.</summary>
    public bool IsNearby(string deviceId)
    {
        var hash = deviceId.StartsWith(PlaceholderPrefix, StringComparison.Ordinal) ? deviceId[PlaceholderPrefix.Length..] : HashText(deviceId);
        return _sightings.TryGetValue(hash, out var s) && Fresh(s.At);
    }

    private void OnRadioStateChanged()
    {
        Log.Info($"Nearby: Bluetooth {State}");
        lock (_gate) _advertised = null;
        UpdateAdvertisement();
        UpdateScanning();
        Changed?.Invoke();
    }

    private void UpdateScanning()
    {
        if (_radio == null) return;
        try
        {
            if (_looking && _radio.State == NearbyRadioState.Ready) _radio.StartScanning(OnSighting);
            else _radio.StopScanning();
        }
        catch (Exception ex)
        {
            Log.Warn($"Nearby: scanning failed: {ex.Message}");
        }
    }

    private void UpdateAdvertisement()
    {
        if (_radio == null) return;
        try
        {
            if (!_receiving || _radio.State != NearbyRadioState.Ready)
            {
                lock (_gate)
                {
                    if (_advertised == null) return;
                    _advertised = null;
                }

                _radio.StopAdvertising();
                Log.Info("Nearby: no longer visible over Bluetooth");
                return;
            }

            var flags = (_selfIsComputer ? NearbyBeaconFlags.Computer : NearbyBeaconFlags.None)
                        | (_direct.CanHost ? NearbyBeaconFlags.CanHost : NearbyBeaconFlags.None)
                        | (_direct.HostedNetwork != null ? NearbyBeaconFlags.Hosting : NearbyBeaconFlags.None)
                        | (_direct.AnnouncedRoles.HasFlag(DirectRoles.SenderHosted) ? NearbyBeaconFlags.SenderHosted : NearbyBeaconFlags.None);
            var beacon = new NearbyBeacon(NearbyBeacon.HashOf(_selfId()), _settings.Current.DeviceName, flags).ToBytes();
            lock (_gate)
            {
                if (_advertised != null && _advertised.AsSpan().SequenceEqual(beacon)) return;
                _advertised = beacon;
            }

            _radio.StartAdvertising(beacon, this);
            Log.Info($"Nearby: visible over Bluetooth ({flags})");
        }
        catch (Exception ex)
        {
            lock (_gate) _advertised = null;
            Log.Warn($"Nearby: advertising failed: {ex.Message}");
        }
    }

    // ---- Receiver: the handshake ----

    void INearbyResponder.Request(string sender, byte[] data)
    {
        if (!_receiving)
        {
            _answers[sender] = (Task.FromResult(NearbyHandshake.Refuse(L.T("Receive isn't open on {0}.", _settings.Current.DeviceName))), Environment.TickCount64);
            return;
        }

        if (NearbyHandshake.IsJoinRequest(data))
        {
            _answers[sender] = _keys.TryGetValue(sender, out var keys)
                ? (Task.Run(() => SessionMessageAsync(sender, data, keys)), Environment.TickCount64)
                : (Task.FromResult(NearbyHandshake.Refuse("No handshake yet.")), Environment.TickCount64);
            return;
        }

        if (!NearbyHandshake.IsRequest(data))
        {
            _answers[sender] = (Task.FromResult(NearbyHandshake.Refuse("Not a Beam request.")), Environment.TickCount64);
            return;
        }

        foreach (var (key, old) in _answers)
            if (Environment.TickCount64 - old.At > HandshakeTimeout.TotalMilliseconds * 2) _answers.TryRemove(key, out _);
        if (_answers.Count >= MaxHandshakes)
        {
            _answers[sender] = (Task.FromResult(NearbyHandshake.Refuse("Busy, try again.")), Environment.TickCount64);
            return;
        }

        _senderHashes[sender] = NearbyHandshake.SenderHash(data);
        _answers[sender] = (Task.Run(async () =>
        {
            var (answer, keys) = await AnswerAsync(data).ConfigureAwait(false);
            if (keys != null) _keys[sender] = keys;
            return answer;
        }), Environment.TickCount64);
    }

    byte[] INearbyResponder.Response(string sender)
    {
        if (!_answers.TryGetValue(sender, out var entry)) return NearbyHandshake.Refuse("No request.");
        return entry.Answer.IsCompletedSuccessfully ? entry.Answer.Result : entry.Answer.IsCompleted ? NearbyHandshake.Refuse("Failed.") : NearbyHandshake.Working();
    }

    void INearbyResponder.Forget(string sender)
    {
        _answers.TryRemove(sender, out _);
        _keys.TryRemove(sender, out _);
        _senderHashes.TryRemove(sender, out _);
    }

    /// <summary>Who each Bluetooth sender said it is in its handshake (its id hash), to check its later messages against.</summary>
    private readonly ConcurrentDictionary<string, byte[]> _senderHashes = new();

    private async Task<(byte[] Answer, NearbyHandshake.Keys? Keys)> AnswerAsync(byte[] request)
    {
        DirectNetwork? network = null;
        var senderHosts = NearbyHandshake.FlagsOf(request).HasFlag(NearbyHandshake.RequestFlags.SenderHosts);
        Log.Info($"Nearby: a sender asked over Bluetooth{(senderHosts ? " (it will host; asks this device to join)" : "")}");
        if (!senderHosts)
        {
            try
            {
                using var timeout = new CancellationTokenSource(HostTimeout);
                network = await _direct.HostForNearbyAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warn($"Nearby: starting the network for a sender failed: {ex.Message}");
            }
        }

        var code = _pairingCode(network != null);
        // A network's details only go to a sender this device started it for (an older sender that asked for it). This
        // version's senders host themselves and get none: anyone nearby can run this handshake.
        code.Ssid = network?.Ssid;
        code.Passphrase = network?.Passphrase;

        if (network == null && code.Addresses.Count == 0 && !senderHosts)
            return (NearbyHandshake.Refuse(_settings.Current.AllowDirectSenders
                ? L.T("{0} couldn't start its own Wi-Fi. Connect both devices to the same Wi-Fi.", _settings.Current.DeviceName)
                : L.T("{0} doesn't let senders connect directly. Connect both devices to the same Wi-Fi.", _settings.Current.DeviceName)), null);

        // A sender that isn't trusted here gets the passphrase only until Receive closes: then the network gets a new one.
        var senderHash = NearbyHandshake.SenderHash(request);
        var trusted = _settings.Current.TrustedDevices.Any(t => NearbyBeacon.HashOf(t.DeviceId).AsSpan().SequenceEqual(senderHash));
        if (network != null && !trusted) _direct.RotateWhenOff();
        Log.Info($"Nearby: answered a sender over Bluetooth{(network != null ? $" with {network.Ssid}" : "")}{(trusted ? " (paired)" : "")}");
        return (NearbyHandshake.Answer(request, code.ToJson(), out var keys), keys);
    }

    /// <summary>
    /// A sealed message after the handshake: a direct session's proposal, offer or end (sender-hosted sessions), checked
    /// against who the sender said it was in the handshake. Older senders' "join my network" (no session) is refused:
    /// it joined without asking anyone.
    /// </summary>
    private async Task<byte[]> SessionMessageAsync(string sender, byte[] data, NearbyHandshake.Keys keys)
    {
        try
        {
            var text = NearbyHandshake.OpenJoinRequest(data, keys);
            DirectRequestMessage? request = null;
            try
            {
                request = System.Text.Json.JsonSerializer.Deserialize(text, ProtocolJson.Default.DirectRequestMessage);
            }
            catch (System.Text.Json.JsonException)
            {
                // not a session message
            }

            if (request is not { Action: DirectActions.Propose or DirectActions.Offer or DirectActions.End })
            {
                Log.Info("Nearby: an older sender asked this device to join its network; refused (update Beam on it)");
                return NearbyHandshake.Refuse(L.T("Update Beam on the sending device to connect directly."));
            }

            // Its claims must match its handshake (still only claims: the direct network's TLS connection checks them).
            if (string.IsNullOrEmpty(request.SenderId) || !_senderHashes.TryGetValue(sender, out var hash)
                || !NearbyBeacon.HashOf(request.SenderId).AsSpan(0, 6).SequenceEqual(hash))
                return NearbyHandshake.Refuse("Not the device that started this handshake.");

            switch (request.Action)
            {
                case DirectActions.Propose:
                {
                    var reply = await _direct.ProposeIncomingAsync(request, request.SenderId, request.SenderName ?? "Beam", request.SenderFingerprint ?? "",
                        claimedOnly: true, DirectControlChannel.Bluetooth, Array.Empty<IPEndPoint>(), CancellationToken.None).ConfigureAwait(false);
                    return reply.Ok ? NearbyHandshake.Accepted() : NearbyHandshake.Refuse(reply.Message ?? L.T("{0} didn't accept the direct connection.", _settings.Current.DeviceName));
                }

                case DirectActions.Offer:
                {
                    var reply = _direct.OfferIncoming(request, request.SenderFingerprint ?? "", out var join);
                    if (!reply.Ok || join == null) return NearbyHandshake.Refuse(reply.Message ?? "Missing network details.");
                    return NearbyHandshake.Joined(await join.ConfigureAwait(false)); // the sender reads "working" meanwhile
                }

                default:
                    _direct.EndFromSenderOverBluetooth(request);
                    return NearbyHandshake.Joined("");
            }
        }
        catch (DirectLinkException ex)
        {
            Log.Warn($"Nearby: direct session over Bluetooth: {ex.Message}");
            return NearbyHandshake.Refuse(ex.Message);
        }
        catch (Exception ex)
        {
            Log.Warn($"Nearby: direct session over Bluetooth failed: {ex.GetType().Name}: {ex.Message}");
            return NearbyHandshake.Refuse(L.T("{0} couldn't join the direct network.", _settings.Current.DeviceName));
        }
    }

    // ---- Sender: listing receivers and the handshake ----

    private void OnSighting(NearbySighting sighting)
    {
        var beacon = sighting.Beacon;
        if (beacon.Matches(_selfId())) return;
        var hash = beacon.IdHashText;
        var isNew = !_sightings.TryGetValue(hash, out var previous) || !Fresh(previous.At);
        _sightings[hash] = (sighting, Environment.TickCount64);
        if (isNew) Log.Info($"Nearby: heard {beacon.Name} ({hash}) over Bluetooth, {beacon.Flags}");

        // Already listed with an address on this network: nothing to add.
        var listed = _discovery.Devices.FirstOrDefault(d => !IsPlaceholder(d) && beacon.Matches(d.Id));
        if (listed is { Endpoints.Count: > 0 })
        {
            _discovery.Forget(PlaceholderPrefix + hash);
            return;
        }

        // Known from an earlier handshake (or its code): list it by its real id, ready to join its network.
        var known = _settings.Current.KnownDirectNetworks.FirstOrDefault(k => beacon.Matches(k.DeviceId));
        if (known != null)
        {
            _discovery.Forget(PlaceholderPrefix + hash);
            _discovery.ReportReachable(listed ?? new DeviceInfo
            {
                Id = known.DeviceId,
                Name = known.Name,
                Fingerprint = known.Fingerprint,
                Kind = string.IsNullOrEmpty(known.Kind) ? KindOf(beacon) : known.Kind,
                DirectRoles = RolesOf(beacon),
            });
        }
        else
        {
            _discovery.ReportReachable(new DeviceInfo
            {
                Id = PlaceholderPrefix + hash,
                Name = string.IsNullOrWhiteSpace(beacon.Name) ? "Beam" : beacon.Name,
                Fingerprint = "",
                Kind = KindOf(beacon),
                Platform = BluetoothPlatform,
                DirectRoles = RolesOf(beacon),
            });
        }

        if (isNew) Changed?.Invoke();
    }

    /// <summary>What a device heard over Bluetooth can do (a computer joins others' networks as ordinary Wi-Fi).</summary>
    private static DirectRoles RolesOf(NearbyBeacon beacon) =>
        DirectRoles.Receiving
        | (beacon.Flags.HasFlag(NearbyBeaconFlags.CanHost) ? DirectRoles.Host : DirectRoles.None)
        | (beacon.Flags.HasFlag(NearbyBeaconFlags.SenderHosted) ? DirectRoles.SenderHosted : DirectRoles.None)
        | (beacon.Flags.HasFlag(NearbyBeaconFlags.Computer) ? DirectRoles.JoinComputers : DirectRoles.Join);

    private static string KindOf(NearbyBeacon beacon) => beacon.Flags.HasFlag(NearbyBeaconFlags.Computer) ? DeviceKinds.Desktop : DeviceKinds.Phone;

    /// <summary>
    /// Turns a receiver heard over Bluetooth into a device Beam can send to: runs the handshake (which only says who it
    /// is: the receiver starts nothing) and returns it under its real id. Devices that aren't placeholders come back
    /// unchanged.
    /// </summary>
    public async Task<DeviceInfo> ResolveAsync(DeviceInfo device, CancellationToken cancellationToken)
    {
        if (!IsPlaceholder(device)) return device;
        var hash = device.Id[PlaceholderPrefix.Length..];
        var (code, _) = await HandshakeAsync(hash, device.Name, null,
            (_, _, code, _) => Task.FromResult(code), cancellationToken).ConfigureAwait(false);
        var resolved = _direct.AcceptNearby(code);
        _discovery.Forget(device.Id);
        Changed?.Invoke();
        return resolved;
    }

    bool IBluetoothDirectControl.CanReach(string deviceId) =>
        _radio?.State == NearbyRadioState.Ready && _sightings.TryGetValue(HashText(deviceId), out var s) && Fresh(s.At);

    /// <summary>A direct session's control channel over Bluetooth: the handshake, then the proposal, on one connection kept open.</summary>
    async Task<IDirectControl> IBluetoothDirectControl.OpenAsync(DeviceInfo peer, string? expectedFingerprint, DirectRequestMessage propose,
        CancellationToken cancellationToken)
    {
        var (control, _) = await HandshakeAsync<IDirectControl>(HashText(peer.Id), peer.Name, expectedFingerprint, async (connection, sender, code, token) =>
        {
            if (code.DeviceId != peer.Id) throw new DirectLinkException(L.T("This isn't the {0} you connected to before. Beam didn't connect, to keep your files safe.", peer.Name));
            await connection.WriteAsync(sender.JoinRequest(Json(propose)), token).ConfigureAwait(false);
            await ReadUntilDoneAsync(connection, sender, peer.Name, NearbyHandshake.Status.Accepted, token).ConfigureAwait(false);
            return new BluetoothControl(connection, sender, code.Fingerprint, peer.Name);
        }, cancellationToken, keepOpen: true).ConfigureAwait(false);
        return control;
    }

    private static string Json(DirectRequestMessage message) =>
        System.Text.Json.JsonSerializer.Serialize(message, ProtocolJson.Default.DirectRequestMessage);

    /// <summary>The open Bluetooth connection of one direct session (its messages sealed with that handshake's keys).</summary>
    private sealed class BluetoothControl : IDirectControl
    {
        private readonly INearbyConnection _connection;
        private readonly NearbyHandshake.Sender _sender;
        private readonly string _name;
        private int _disposed;

        public BluetoothControl(INearbyConnection connection, NearbyHandshake.Sender sender, string fingerprint, string name)
        {
            _connection = connection;
            _sender = sender;
            PeerFingerprint = fingerprint;
            _name = name;
        }

        public DirectControlChannel Channel => DirectControlChannel.Bluetooth;

        public string PeerFingerprint { get; }

        public async Task OfferAsync(DirectRequestMessage offer, Action delivered, CancellationToken cancellationToken)
        {
            await _connection.WriteAsync(_sender.JoinRequest(Json(offer)), cancellationToken).ConfigureAwait(false);
            delivered();
            // "Working" while it joins; then joined (it also reports in over the direct network) or why it couldn't.
            await ReadUntilDoneAsync(_connection, _sender, _name, NearbyHandshake.Status.Joined, cancellationToken).ConfigureAwait(false);
        }

        public async Task TellAsync(DirectRequestMessage message)
        {
            if (Volatile.Read(ref _disposed) == 1) return;
            try
            {
                using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await _connection.WriteAsync(_sender.JoinRequest(Json(message)), limit.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Info($"Nearby: couldn't tell {_name} ({message.Action}) over Bluetooth: {ex.Message}");
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            try
            {
                await _connection.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Info($"Nearby: closing the Bluetooth connection to {_name}: {ex.Message}");
            }

            _sender.Dispose();
        }
    }

    /// <summary>
    /// The handshake with the receiver whose id hash is <paramref name="hash"/> (it never starts a network for this
    /// version's senders), then <paramref name="after"/> on the same connection. With <paramref name="keepOpen"/> the
    /// connection stays open for what <paramref name="after"/> returns.
    /// </summary>
    private async Task<(T Result, PairingCode Code)> HandshakeAsync<T>(string hash, string name, string? expectedFingerprint,
        Func<INearbyConnection, NearbyHandshake.Sender, PairingCode, CancellationToken, Task<T>> after, CancellationToken cancellationToken,
        bool keepOpen = false)
    {
        if (_radio == null || _radio.State != NearbyRadioState.Ready) throw new DirectLinkException(L.T("Turn on Bluetooth to connect to {0}.", name));
        if (!_sightings.TryGetValue(hash, out var seen) || !Fresh(seen.At))
            throw new DirectLinkException(L.T("{0} isn't nearby anymore. Open Receive on it and try again.", name));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(HandshakeTimeout);
        var token = timeout.Token;
        // Where to connect: a phone's service is at its beacon's address. Windows sends its beacon from another
        // (non-connectable) address, so for a computer try the addresses that advertised Beam's service first.
        var serviceAddresses = _radio.RecentServiceAddresses().Where(a => a != seen.Sighting.Address).Take(4).ToList();
        var candidates = seen.Sighting.Beacon.Flags.HasFlag(NearbyBeaconFlags.Computer)
            ? serviceAddresses.Append(seen.Sighting.Address).ToList()
            : serviceAddresses.Prepend(seen.Sighting.Address).ToList();
        Log.Info($"Nearby: handshake with {name} over Bluetooth; addresses to try: {string.Join(", ", candidates)}");
        Exception? last = null;
        try
        {
            foreach (var address in candidates)
            {
                try
                {
                    var result = await HandshakeAtAsync(address, name, hash, expectedFingerprint, after, keepOpen, token).ConfigureAwait(false);
                    if (result is { } found) return found;
                }
                catch (DirectLinkException)
                {
                    throw; // it answered: refused, or not who it was before
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
                {
                    last = ex;
                    Log.Info($"Nearby: no handshake at {address}: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new DirectLinkException(L.T("{0} didn't answer over Bluetooth. Move closer and try again.", name));
        }

        Log.Warn($"Nearby: handshake with {name} failed: {last?.Message ?? "no device answered"}");
        throw new DirectLinkException(L.T("{0} didn't answer over Bluetooth. Move closer and try again.", name), last);
    }

    /// <summary>
    /// One handshake at one address (the connection has a time limit of its own, so a dead address costs little). Null
    /// when the device there isn't the one wanted.
    /// </summary>
    private async Task<(T, PairingCode)?> HandshakeAtAsync<T>(string address, string name, string hash, string? expectedFingerprint,
        Func<INearbyConnection, NearbyHandshake.Sender, PairingCode, CancellationToken, Task<T>> after, bool keepOpen, CancellationToken token)
    {
        using var connecting = CancellationTokenSource.CreateLinkedTokenSource(token);
        connecting.CancelAfter(ConnectTimeout);
        INearbyConnection connection;
        var started = Environment.TickCount64;
        try
        {
            connection = await _radio!.ConnectAsync(address, connecting.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (connecting.IsCancellationRequested && !token.IsCancellationRequested)
        {
            throw new TimeoutException($"No connection within {ConnectTimeout.TotalSeconds:0} s.");
        }

        Log.Info($"Nearby: connected to {address} over Bluetooth in {Environment.TickCount64 - started} ms");
        // This version's senders always host themselves: the receiver never starts its network for this handshake.
        var sender = new NearbyHandshake.Sender(NearbyBeacon.HashOf(_selfId()), NearbyHandshake.RequestFlags.SenderHosts);
        var handedOver = false;
        try
        {
            await connection.WriteAsync(sender.Request(), token).ConfigureAwait(false);
            var text = await ReadUntilDoneAsync(connection, sender, name, NearbyHandshake.Status.Ready, token).ConfigureAwait(false);
            var code = PairingCode.FromJson(text) ?? throw new IOException("Not a pairing code.");
            if (HashText(code.DeviceId) != hash)
            {
                Log.Info($"Nearby: {address} is {code.Name}, not {name}; trying the next one");
                return null;
            }

            // A device seen before must still have the same certificate: otherwise someone is pretending to be it.
            var expected = _settings.Current.TrustedDevices.FirstOrDefault(t => t.DeviceId == code.DeviceId)?.Fingerprint
                           ?? _direct.KnownNetworkFor(code.DeviceId)?.Fingerprint
                           ?? expectedFingerprint;
            if (!string.IsNullOrEmpty(expected) && !string.Equals(expected, code.Fingerprint, StringComparison.OrdinalIgnoreCase))
            {
                Log.Warn($"Nearby: {code.Name} answered with a different certificate than before; not connecting");
                throw new DirectLinkException(L.T("This isn't the {0} you connected to before. Beam didn't connect, to keep your files safe.", code.Name));
            }

            Log.Info($"Nearby: {code.Name} answered (roles {code.Roles})");
            var result = await after(connection, sender, code, token).ConfigureAwait(false);
            handedOver = keepOpen;
            return (result, code);
        }
        finally
        {
            if (!handedOver)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                sender.Dispose();
            }
        }
    }

    /// <summary>Reads the answer until the receiver is done ("working" meanwhile); its text, or why it refused.</summary>
    private static async Task<string> ReadUntilDoneAsync(INearbyConnection connection, NearbyHandshake.Sender sender, string name,
        NearbyHandshake.Status wanted, CancellationToken token)
    {
        while (true)
        {
            await Task.Delay(400, token).ConfigureAwait(false);
            NearbyHandshake.Status status;
            string? text;
            try
            {
                (status, text) = sender.Open(await connection.ReadAsync(token).ConfigureAwait(false));
            }
            catch (CryptographicException ex)
            {
                throw new IOException($"Bad answer: {ex.Message}");
            }

            if (status == NearbyHandshake.Status.Working) continue;
            if (status == NearbyHandshake.Status.Refused) throw new DirectLinkException(text ?? L.T("{0} didn't answer.", name));
            if (status != wanted) throw new IOException($"Unexpected answer ({status}).");
            return text ?? "";
        }
    }

    private static string HashText(string deviceId) => Convert.ToHexString(NearbyBeacon.HashOf(deviceId)).ToLowerInvariant();

    private static bool Fresh(long at) => Environment.TickCount64 - at < SightingLifetime.TotalMilliseconds;

    internal void Stop()
    {
        lock (_gate) _grace?.Cancel();
        try
        {
            _radio?.StopAdvertising();
            _radio?.StopScanning();
        }
        catch (Exception ex)
        {
            Log.Warn($"Nearby: stopping Bluetooth failed: {ex.Message}");
        }
    }
}
