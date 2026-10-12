using System.Collections.Concurrent;
using System.Security.Cryptography;
using Beam.Core.Diagnostics;
using Beam.Core.Direct;
using Beam.Core.Discovery;
using Beam.Core.Localization;
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
public sealed class NearbyService : INearbyResponder
{
    /// <summary>Platform of the list entries for receivers only heard over Bluetooth (not yet known by their real id).</summary>
    public const string BluetoothPlatform = "bluetooth";

    private const string PlaceholderPrefix = "bt-";
    private const int MaxHandshakes = 8;
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan HostTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan SightingLifetime = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    private readonly INearbyRadio? _radio;
    private readonly DirectManager _direct;
    private readonly DiscoveryService _discovery;
    private readonly SettingsStore _settings;
    private readonly Func<string> _selfId;
    private readonly Func<bool, PairingCode> _pairingCode;
    private readonly bool _selfIsComputer;
    private readonly ConcurrentDictionary<string, (Task<byte[]> Answer, long At)> _answers = new();
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
            direct.RefreshNetwork = RefreshAsync;
            direct.PeerNetworkOn = NetworkOn;
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

    /// <summary>Whether the device's beacon says its network is on (null: not heard over Bluetooth).</summary>
    private bool? NetworkOn(string deviceId) =>
        _sightings.TryGetValue(HashText(deviceId), out var s) && Fresh(s.At) ? s.Sighting.Beacon.Flags.HasFlag(NearbyBeaconFlags.Hosting) : null;

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
                        | (_direct.HostedNetwork != null ? NearbyBeaconFlags.Hosting : NearbyBeaconFlags.None);
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

        _answers[sender] = (Task.Run(() => AnswerAsync(data)), Environment.TickCount64);
    }

    byte[] INearbyResponder.Response(string sender)
    {
        if (!_answers.TryGetValue(sender, out var entry)) return NearbyHandshake.Refuse("No request.");
        return entry.Answer.IsCompletedSuccessfully ? entry.Answer.Result : entry.Answer.IsCompleted ? NearbyHandshake.Refuse("Failed.") : NearbyHandshake.Working();
    }

    void INearbyResponder.Forget(string sender) => _answers.TryRemove(sender, out _);

    private async Task<byte[]> AnswerAsync(byte[] request)
    {
        DirectNetwork? network = null;
        try
        {
            using var timeout = new CancellationTokenSource(HostTimeout);
            network = await _direct.HostForNearbyAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn($"Nearby: starting the network for a sender failed: {ex.Message}");
        }

        var code = _pairingCode(network != null);
        if (network != null)
        {
            code.Ssid = network.Ssid; // what is actually on (it may differ from the saved one)
            code.Passphrase = network.Passphrase;
        }

        if (network == null && code.Addresses.Count == 0)
            return NearbyHandshake.Refuse(_settings.Current.AllowDirectSenders
                ? L.T("{0} couldn't start its own Wi-Fi. Connect both devices to the same Wi-Fi.", _settings.Current.DeviceName)
                : L.T("{0} doesn't let senders connect directly. Connect both devices to the same Wi-Fi.", _settings.Current.DeviceName));

        // A sender that isn't trusted here gets the passphrase only until Receive closes: then the network gets a new one.
        var senderHash = NearbyHandshake.SenderHash(request);
        var trusted = _settings.Current.TrustedDevices.Any(t => NearbyBeacon.HashOf(t.DeviceId).AsSpan().SequenceEqual(senderHash));
        if (network != null && !trusted) _direct.RotateWhenOff();
        Log.Info($"Nearby: answered a sender over Bluetooth{(network != null ? $" with {network.Ssid}" : "")}{(trusted ? " (paired)" : "")}");
        return NearbyHandshake.Answer(request, code.ToJson());
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
                DirectRoles = DirectRoles.Host | DirectRoles.Receiving,
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
                DirectRoles = DirectRoles.Host | DirectRoles.Receiving,
            });
        }

        if (isNew) Changed?.Invoke();
    }

    private static string KindOf(NearbyBeacon beacon) => beacon.Flags.HasFlag(NearbyBeaconFlags.Computer) ? DeviceKinds.Desktop : DeviceKinds.Phone;

    /// <summary>
    /// Turns a receiver heard over Bluetooth into a device Beam can send to: runs the handshake, remembers its network,
    /// and returns it under its real id. Devices that aren't placeholders come back unchanged.
    /// </summary>
    public async Task<DeviceInfo> ResolveAsync(DeviceInfo device, CancellationToken cancellationToken)
    {
        if (!IsPlaceholder(device)) return device;
        var hash = device.Id[PlaceholderPrefix.Length..];
        var code = await HandshakeAsync(hash, device.Name, cancellationToken).ConfigureAwait(false);
        var resolved = _direct.AcceptNearby(code);
        _discovery.Forget(device.Id);
        Changed?.Invoke();
        return resolved;
    }

    /// <summary>Joining a remembered network failed: ask the receiver for its current details (if it is in range).</summary>
    private async Task<bool> RefreshAsync(DeviceInfo device, CancellationToken cancellationToken)
    {
        var hash = HashText(device.Id);
        if (!_sightings.TryGetValue(hash, out var s) || !Fresh(s.At)) return false;
        var before = _direct.KnownNetworkFor(device.Id);
        var code = await HandshakeAsync(hash, device.Name, cancellationToken).ConfigureAwait(false);
        if (code.DeviceId != device.Id) return false;
        _direct.AcceptNearby(code);
        var after = _direct.KnownNetworkFor(device.Id);
        return after != null && (before == null || before.Passphrase != after.Passphrase || before.Ssid != after.Ssid);
    }

    private async Task<PairingCode> HandshakeAsync(string hash, string name, CancellationToken cancellationToken)
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
        Log.Info($"Nearby: handshake with {name} over Bluetooth ({string.Join(", ", candidates)})");
        Exception? last = null;
        try
        {
            foreach (var address in candidates)
            {
                try
                {
                    var code = await HandshakeAtAsync(address, name, token).ConfigureAwait(false);
                    if (HashText(code.DeviceId) == hash) return code;
                    Log.Info($"Nearby: {address} is {code.Name}, not {name}; trying the next one");
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

        Log.Warn($"Nearby: handshake with {name} failed: {last?.Message}");
        throw new DirectLinkException(L.T("{0} didn't answer over Bluetooth. Move closer and try again.", name), last);
    }

    /// <summary>One handshake at one address (connects with a time limit of its own, so a dead address costs little).</summary>
    private async Task<PairingCode> HandshakeAtAsync(string address, string name, CancellationToken token)
    {
        using var connecting = CancellationTokenSource.CreateLinkedTokenSource(token);
        connecting.CancelAfter(ConnectTimeout);
        INearbyConnection connection;
        try
        {
            connection = await _radio!.ConnectAsync(address, connecting.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (connecting.IsCancellationRequested && !token.IsCancellationRequested)
        {
            throw new TimeoutException("No connection.");
        }

        await using (connection.ConfigureAwait(false))
        {
            using var sender = new NearbyHandshake.Sender(NearbyBeacon.HashOf(_selfId()));
            await connection.WriteAsync(sender.Request(), token).ConfigureAwait(false);
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
                if (status == NearbyHandshake.Status.Refused || text == null)
                    throw new DirectLinkException(text ?? L.T("{0} didn't answer.", name));
                var code = PairingCode.FromJson(text) ?? throw new IOException("Not a pairing code.");
                // A device seen before must still have the same certificate: otherwise someone is pretending to be it.
                var expected = _settings.Current.TrustedDevices.FirstOrDefault(t => t.DeviceId == code.DeviceId)?.Fingerprint
                               ?? _direct.KnownNetworkFor(code.DeviceId)?.Fingerprint;
                if (!string.IsNullOrEmpty(expected) && !string.Equals(expected, code.Fingerprint, StringComparison.OrdinalIgnoreCase))
                {
                    Log.Warn($"Nearby: {code.Name} answered with a different certificate than before; not connecting");
                    throw new DirectLinkException(L.T("This isn't the {0} you connected to before. Beam didn't connect, to keep your files safe.", code.Name));
                }

                Log.Info($"Nearby: {code.Name} answered{(code.Network != null ? $" with its network {code.Network.Ssid}" : "")}");
                return code;
            }
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
