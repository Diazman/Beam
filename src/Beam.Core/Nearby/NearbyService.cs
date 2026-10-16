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
    /// <summary>How long connecting and the handshake may take, together.</summary>
    internal TimeSpan HandshakeTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How long the sender waits for the answer to a proposal. An unpaired sender (and every sender over Bluetooth) is
    /// accepted only once the receiver's user taps Accept, which may take up to the receiver's own limit
    /// (<see cref="DirectManager.DirectApprovalTimeout"/>); this waits a little longer, so the answer (yes, or "didn't
    /// accept") arrives instead of a timeout of our own that blames Bluetooth.
    /// </summary>
    internal TimeSpan ProposalWait { get; set; } = DirectManager.DirectApprovalTimeout + TimeSpan.FromSeconds(15);
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
            if ((_looking || Volatile.Read(ref _directLooks) > 0) && _radio.State == NearbyRadioState.Ready) _radio.StartScanning(OnSighting);
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
        _claims.TryRemove(sender, out _);
    }

    /// <summary>Who each Bluetooth sender said it is in its handshake (its id hash), to check its later messages against.</summary>
    private readonly ConcurrentDictionary<string, byte[]> _senderHashes = new();

    /// <summary>The certificate fingerprint each Bluetooth sender claimed when its proposal was accepted (per connection).</summary>
    private readonly ConcurrentDictionary<string, string> _claims = new();

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
        // At most 512 bytes over Bluetooth: leave out addresses (the least needed part) until it fits.
        var answer = NearbyHandshake.Answer(request, code.ToJson(), out var keys);
        while (answer.Length > NearbyHandshake.MaxValueLength && code.Addresses.Count > 0)
        {
            code.Addresses.RemoveAt(code.Addresses.Count - 1);
            answer = NearbyHandshake.Answer(request, code.ToJson(), out keys);
        }

        // Then the name shown to the sender (an emoji takes 12 bytes in JSON); never the id, key or fingerprint.
        while (answer.Length > NearbyHandshake.MaxValueLength && Shorter(code.Name) is { } shorter)
        {
            code.Name = shorter;
            answer = NearbyHandshake.Answer(request, code.ToJson(), out keys);
        }

        if (answer.Length > NearbyHandshake.MaxValueLength) Log.Warn($"Nearby: the answer is {answer.Length} bytes, more than Bluetooth carries");
        return (answer, keys);
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

            switch (request.Action)
            {
                case DirectActions.Propose:
                {
                    // Its claims must match its handshake (still only claims: the direct network's TLS connection checks them).
                    if (string.IsNullOrEmpty(request.SenderId) || string.IsNullOrEmpty(request.SenderFingerprint) || !_senderHashes.TryGetValue(sender, out var hash)
                        || !NearbyBeacon.HashOf(request.SenderId).AsSpan(0, 6).SequenceEqual(hash))
                        return NearbyHandshake.Refuse("Not the device that started this handshake.");
                    var reply = await _direct.ProposeIncomingAsync(request, request.SenderId, request.SenderName ?? "Beam", request.SenderFingerprint,
                        claimedOnly: true, DirectControlChannel.Bluetooth, Array.Empty<IPEndPoint>(), CancellationToken.None).ConfigureAwait(false);
                    if (reply.Ok) _claims[sender] = request.SenderFingerprint;
                    return reply.Ok ? NearbyHandshake.Accepted() : NearbyHandshake.Refuse(reply.Message ?? L.T("{0} didn't accept the direct connection.", _settings.Current.DeviceName));
                }

                // Later messages on the same connection (sealed with its keys) are from whoever proposed on it: they
                // don't repeat its identity, so they fit Bluetooth's 512-byte limit.
                case DirectActions.Offer:
                {
                    if (!_claims.TryGetValue(sender, out var claimed)) return NearbyHandshake.Refuse("Propose first.");
                    var reply = _direct.OfferIncoming(request, claimed, out var join);
                    if (!reply.Ok || join == null) return NearbyHandshake.Refuse(reply.Message ?? "Missing network details.");
                    return NearbyHandshake.Joined(await join.ConfigureAwait(false)); // the sender reads "working" meanwhile
                }

                default:
                    if (_claims.TryGetValue(sender, out var proposer)) _direct.EndFromSenderOverBluetooth(request, proposer);
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

        // Already listed by its real id (found out over Bluetooth, so no address): kept, and never listed twice.
        if (listed != null)
        {
            _discovery.Forget(PlaceholderPrefix + hash);
            _discovery.ReportReachable(listed);
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
        // The connection stays open a little while: a Direct send right after uses it instead of connecting again (a
        // second Bluetooth connection to the same device moments after the first one often doesn't come up).
        var (handshake, code) = await HandshakeAsync(hash, device.Name, null,
            (connection, sender, code, _) => Task.FromResult(new OpenHandshake(connection, sender, code, Environment.TickCount64)),
            cancellationToken, keepOpen: true).ConfigureAwait(false);
        KeepOpen(code.DeviceId, handshake);
        var resolved = _direct.AcceptNearby(code);
        _discovery.Forget(device.Id);
        Changed?.Invoke();
        return resolved;
    }

    /// <summary>A handshake whose connection is still open, for the direct session that usually follows.</summary>
    private sealed record OpenHandshake(INearbyConnection Connection, NearbyHandshake.Sender Sender, PairingCode Code, long At)
    {
        public async Task CloseAsync()
        {
            try
            {
                await Connection.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Info($"Nearby: closing an unused Bluetooth connection: {ex.Message}");
            }

            Sender.Dispose();
        }
    }

    private readonly ConcurrentDictionary<string, OpenHandshake> _open = new(); // by device id

    /// <summary>How long a resolved handshake's connection waits for the direct session that may follow.</summary>
    internal static TimeSpan KeepHandshakeOpen { get; set; } = TimeSpan.FromSeconds(30);

    private void KeepOpen(string deviceId, OpenHandshake handshake)
    {
        if (_open.TryGetValue(deviceId, out var older) && _open.TryRemove(new KeyValuePair<string, OpenHandshake>(deviceId, older))) _ = older.CloseAsync();
        _open[deviceId] = handshake;
        _ = Task.Delay(KeepHandshakeOpen).ContinueWith(async _ =>
        {
            if (_open.TryRemove(new KeyValuePair<string, OpenHandshake>(deviceId, handshake))) await handshake.CloseAsync().ConfigureAwait(false);
        }, TaskScheduler.Default);
    }

    /// <summary>
    /// Whether the device can be reached over Bluetooth now: its beacon heard just now, or (scanning while a direct
    /// session is set up, even when the Send page isn't open) within <paramref name="wait"/>. Also true while a
    /// handshake with it is still open.
    /// </summary>
    async Task<bool> IBluetoothDirectControl.CanReachAsync(string deviceId, TimeSpan wait, CancellationToken cancellationToken)
    {
        if (_radio?.State != NearbyRadioState.Ready) return false;
        if (_open.ContainsKey(deviceId)) return true;
        var hash = HashText(deviceId);
        bool Heard() => _sightings.TryGetValue(hash, out var s) && Fresh(s.At);
        if (Heard()) return true;
        Interlocked.Increment(ref _directLooks);
        UpdateScanning();
        try
        {
            var deadline = Environment.TickCount64 + (long)wait.TotalMilliseconds;
            while (!Heard() && Environment.TickCount64 < deadline) await Task.Delay(200, cancellationToken).ConfigureAwait(false);
            return Heard();
        }
        finally
        {
            // Scanning stays on for a while (the session's handshake needs the sighting); stopped then unless Send is open.
            _ = Task.Delay(HandshakeTimeout).ContinueWith(_ =>
            {
                Interlocked.Decrement(ref _directLooks);
                UpdateScanning();
            }, TaskScheduler.Default);
        }
    }

    private int _directLooks;

    /// <summary>
    /// A direct session's control channel over Bluetooth: the handshake (or the one still open from finding out who the
    /// device is), then the proposal, on one connection kept open.
    /// </summary>
    async Task<IDirectControl> IBluetoothDirectControl.OpenAsync(DeviceInfo peer, string? expectedFingerprint, DirectRequestMessage propose,
        CancellationToken cancellationToken)
    {
        async Task<IDirectControl> ProposeAsync(INearbyConnection connection, NearbyHandshake.Sender sender, PairingCode code, CancellationToken _)
        {
            if (code.DeviceId != peer.Id) throw new DirectLinkException(L.T("This isn't the {0} you connected to before. Beam didn't connect, to keep your files safe.", peer.Name));
            // Its own time limit, not the handshake's: the receiver may be asking its user.
            using var answer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            answer.CancelAfter(ProposalWait);
            try
            {
                await connection.WriteAsync(Sealed(sender, propose), answer.Token).ConfigureAwait(false);
                await ReadUntilDoneAsync(connection, sender, peer.Name, NearbyHandshake.Status.Accepted, answer.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (answer.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                Log.Info($"Nearby: {peer.Name} didn't answer the proposal within {ProposalWait.TotalSeconds:0} s (its user didn't decide)");
                throw new DirectLinkException(L.T("{0} didn't accept the direct connection in time. Tap Accept on {0} when it asks, then try again.", peer.Name));
            }

            return new BluetoothControl(connection, sender, code.Fingerprint, peer.Name);
        }

        if (_open.TryRemove(peer.Id, out var open) && Environment.TickCount64 - open.At < KeepHandshakeOpen.TotalMilliseconds)
        {
            var known = expectedFingerprint ?? peer.Fingerprint;
            if (!string.IsNullOrEmpty(known) && !string.Equals(known, open.Code.Fingerprint, StringComparison.OrdinalIgnoreCase))
            {
                await open.CloseAsync().ConfigureAwait(false);
                throw new DirectLinkException(L.T("This isn't the {0} you connected to before. Beam didn't connect, to keep your files safe.", peer.Name));
            }

            try
            {
                Log.Info($"Nearby: proposing a direct session to {peer.Name} on the Bluetooth connection already open");
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(HandshakeTimeout);
                return await ProposeAsync(open.Connection, open.Sender, open.Code, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or CryptographicException or TimeoutException
                                           || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
            {
                // The open connection dropped meanwhile: a new handshake.
                Log.Info($"Nearby: the open Bluetooth connection to {peer.Name} didn't work any more ({ex.Message}); connecting again");
                await open.CloseAsync().ConfigureAwait(false);
            }
        }
        else if (open != null)
        {
            await open.CloseAsync().ConfigureAwait(false);
        }

        var (control, _) = await HandshakeAsync<IDirectControl>(HashText(peer.Id), peer.Name, expectedFingerprint, ProposeAsync,
            cancellationToken, keepOpen: true).ConfigureAwait(false);
        return control;
    }

    /// <summary>
    /// A message after the proposal, without the identity fields the proposal carried (the receiver knows them from the
    /// proposal on this connection): Bluetooth carries at most <see cref="NearbyHandshake.MaxValueLength"/> bytes.
    /// </summary>
    internal static DirectRequestMessage Compact(DirectRequestMessage message) => new()
    {
        Action = message.Action,
        SessionId = message.SessionId,
        Nonce = message.Nonce,
        Ssid = message.Ssid,
        Passphrase = message.Passphrase,
        TtlSeconds = message.TtlSeconds,
        HostIsComputer = message.HostIsComputer,
        Port = message.Port,
        Stage = message.Stage,
        Message = message.Message is { Length: > 80 } text ? text[..80] : message.Message,
        Proof = message.Proof,
    };

    /// <summary>
    /// <paramref name="message"/> sealed for Bluetooth. If it doesn't fit <see cref="NearbyHandshake.MaxValueLength"/>
    /// (long names of emoji), the text only shown to people (the sender's name, then a message) is shortened until it
    /// does; ids, nonces, fingerprints and network details never are (if those don't fit, sealing fails).
    /// </summary>
    internal static byte[] Sealed(NearbyHandshake.Sender sender, DirectRequestMessage message)
    {
        var text = Json(message);
        if (!NearbyHandshake.Fits(text))
        {
            var copy = System.Text.Json.JsonSerializer.Deserialize(text, ProtocolJson.Default.DirectRequestMessage)!;
            while (!NearbyHandshake.Fits(text = Json(copy)))
            {
                if (Shorter(copy.SenderName) is { } name) copy.SenderName = name;
                else if (Shorter(copy.Message) is { } shorter) copy.Message = shorter;
                else break;
            }

            Log.Info($"Nearby: shortened the text of a {message.Action} message to fit Bluetooth");
        }

        return sender.JoinRequest(text);
    }

    /// <summary>The text one letter (text element) shorter, ending in "…"; null when there's nothing left to cut.</summary>
    internal static string? Shorter(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var body = text.EndsWith('…') ? text[..^1] : text;
        var letters = new System.Globalization.StringInfo(body);
        return letters.LengthInTextElements <= 1 ? null : letters.SubstringByTextElements(0, letters.LengthInTextElements - 1) + "…";
    }

    /// <summary>
    /// A direct session message as JSON for Bluetooth: non-ASCII text (names, messages) as UTF-8 rather than six-byte
    /// escapes, so a 40-character Cyrillic name still fits <see cref="NearbyHandshake.MaxValueLength"/>. (Messages over
    /// the network are unchanged.)
    /// </summary>
    internal static string Json(DirectRequestMessage message) =>
        System.Text.Json.JsonSerializer.Serialize(message, CompactJson.DirectRequestMessage);

    private static readonly ProtocolJson CompactJson = new(new System.Text.Json.JsonSerializerOptions(ProtocolJson.Default.Options)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });

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
            await _connection.WriteAsync(Sealed(_sender, Compact(offer)), cancellationToken).ConfigureAwait(false);
            delivered();
            // "Working" while it joins; then joined (it also reports in over the direct network) or why it couldn't.
            try
            {
                await ReadUntilDoneAsync(_connection, _sender, _name, NearbyHandshake.Status.Joined, cancellationToken).ConfigureAwait(false);
            }
            catch (DirectLinkException ex)
            {
                throw new DirectLinkException(ex.Message, ex) { JoinFailed = true }; // the receiver's answer: it couldn't join
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested
                                       && ex is IOException or ObjectDisposedException or OperationCanceledException or TimeoutException
                                           or CryptographicException)
            {
                // The Bluetooth connection went away (phones often drop it while switching Wi-Fi). The receiver has the
                // network details: whether it joined shows when it reports in over the direct network.
                Log.Info($"Nearby: the Bluetooth connection to {_name} dropped after the offer ({ex.GetType().Name}: {ex.Message}); " +
                         "waiting for it to report in over the direct network");
            }
        }

        public async Task TellAsync(DirectRequestMessage message)
        {
            if (Volatile.Read(ref _disposed) == 1) return;
            try
            {
                using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await _connection.WriteAsync(Sealed(_sender, Compact(message)), limit.Token).ConfigureAwait(false);
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
            throw new DirectLinkException(L.T("{0} isn't nearby anymore. Open Receive on it and try again.", name), transient: true);

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
            throw new DirectLinkException(L.T("{0} didn't answer over Bluetooth. Move closer and try again.", name), transient: true);
        }

        Log.Warn($"Nearby: handshake with {name} failed: {last?.Message ?? "no device answered"}");
        throw new DirectLinkException(L.T("{0} didn't answer over Bluetooth. Move closer and try again.", name), last, transient: true);
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

    /// <summary>Tests: the connections kept open after finding out who a device is are closed (as when they time out).</summary>
    internal async Task CloseOpenHandshakesForTests()
    {
        foreach (var id in _open.Keys.ToList())
            if (_open.TryRemove(id, out var open)) await open.CloseAsync().ConfigureAwait(false);
    }

    /// <summary>Tests: every sighting so far is <paramref name="by"/> older (as if scanning had stopped that long ago).</summary>
    internal void AgeSightingsForTests(TimeSpan by)
    {
        foreach (var (hash, seen) in _sightings) _sightings[hash] = (seen.Sighting, seen.At - (long)by.TotalMilliseconds);
    }

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
