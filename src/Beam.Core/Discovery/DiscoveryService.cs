using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Beam.Core.Diagnostics;
using Beam.Core.Localization;

namespace Beam.Core.Discovery;

public sealed class DiscoveryOptions
{
    public int Port { get; init; } = AppInfo.DiscoveryPort;

    /// <summary>Administratively-scoped multicast group (never routed beyond the local network).</summary>
    public IPAddress MulticastGroup { get; init; } = IPAddress.Parse("239.255.73.37");

    /// <summary>Send multicast and subnet broadcasts on every network adapter. Disabled in tests.</summary>
    public bool UseMulticastAndBroadcast { get; init; } = true;

    public TimeSpan AnnounceInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>A device that has not been heard from for this long is considered gone.</summary>
    public TimeSpan DeviceTimeout { get; init; } = TimeSpan.FromSeconds(16);

    /// <summary>Extra discovery endpoints that always receive unicast queries (tests, fixed peers).</summary>
    public IReadOnlyList<IPEndPoint> StaticTargets { get; init; } = Array.Empty<IPEndPoint>();
}

/// <summary>What this device tells others about itself.</summary>
public sealed record LocalAnnouncement(string Id, string Name, int TcpPort, string Fingerprint, string Kind, Direct.DirectRoles DirectRoles = Direct.DirectRoles.None);

/// <summary>
/// Finds other Beam instances on the local network using small UDP datagrams:
/// multicast plus subnet broadcast (some Wi-Fi routers drop one or the other),
/// and unicast queries to addresses the user entered by hand.
/// Traffic is tiny: one ~200 byte packet per adapter every few seconds.
/// </summary>
public sealed class DiscoveryService : IDisposable
{
    private readonly DiscoveryOptions _options;
    private readonly Func<LocalAnnouncement> _self;
    private readonly ConcurrentDictionary<string, Entry> _devices = new();
    private readonly ConcurrentDictionary<string, long> _lastReplyTo = new();
    private readonly ConcurrentDictionary<IPEndPoint, byte> _unicastTargets = new();
    private readonly ConcurrentDictionary<IPEndPoint, string> _failingTargets = new();
    private readonly ConcurrentDictionary<string, string> _failingSends = new();
    private readonly object _sendGate = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _wake = new(0);
    private Socket? _socket;
    private List<LocalInterface> _interfaces = new();
    private long _interfacesRefreshedAt;
    private long _reopenedAt;
    private bool _discoverable = true;
    private Task? _receiveTask;
    private Task? _timerTask;

    public DiscoveryService(DiscoveryOptions options, Func<LocalAnnouncement> self)
    {
        _options = options;
        _self = self;
        foreach (var target in options.StaticTargets) _unicastTargets[target] = 0;
    }

    public event EventHandler<DeviceEventArgs>? DeviceFound;

    public event EventHandler<DeviceEventArgs>? DeviceChanged;

    public event EventHandler<DeviceEventArgs>? DeviceLost;

    /// <summary>False when the discovery port could not be opened; manual connection still works.</summary>
    public bool IsRunning { get; private set; }

    public string? Problem { get; private set; }

    /// <summary>The UDP port actually bound (differs from the option when it was 0).</summary>
    public int LocalPort { get; private set; }

    public IReadOnlyList<DeviceInfo> Devices =>
        _devices.Values.Select(e => e.Device).OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

    public bool Discoverable
    {
        get => _discoverable;
        set
        {
            if (_discoverable == value) return;
            _discoverable = value;
            if (!value) SendToAll(CreatePacket(DiscoveryPacket.TypeBye));
            else Wake();
        }
    }

    public DeviceInfo? Find(string deviceId) => _devices.TryGetValue(deviceId, out var e) ? e.Device : null;

    public void Start()
    {
        try
        {
            var socket = OpenSocket(_options.Port);
            _socket = socket;
            LocalPort = ((IPEndPoint)socket.LocalEndPoint!).Port;
            IsRunning = true;
            Problem = null;
        }
        catch (Exception ex)
        {
            IsRunning = false;
            Problem = L.T("Automatic discovery is unavailable because another program is using the network port it needs.");
            Log.Error($"Discovery could not bind UDP port {_options.Port}", ex);
            _socket?.Dispose();
            _socket = null;
            return;
        }

        RefreshInterfaces();
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        _receiveTask = Task.Run(ReceiveLoopAsync);
        _timerTask = Task.Run(TimerLoopAsync);
        SendToAll(CreatePacket(DiscoveryPacket.TypeQuery));
        Log.Info($"Discovery started on UDP {_options.Port} ({_interfaces.Count} network adapters)");
    }

    private static Socket OpenSocket(int port)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.EnableBroadcast = true;
            socket.MulticastLoopback = true;
            if (OperatingSystem.IsWindows())
            {
                // Stop ICMP "port unreachable" replies from surfacing as receive errors.
                const int SioUdpConnReset = unchecked((int)0x9800000C);
                socket.IOControl(SioUdpConnReset, new byte[] { 0, 0, 0, 0 }, null);
            }

            socket.Bind(new IPEndPoint(IPAddress.Any, port));
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>
    /// On Android a socket stays on the network Beam used when it was opened, so after joining or leaving a direct
    /// network every send failed with "Network is unreachable": open a new one on the network Beam uses now.
    /// </summary>
    private void ReopenSocket()
    {
        lock (_sendGate)
        {
            _reopenedAt = Environment.TickCount64;
            var old = _socket;
            if (old == null || _cts.IsCancellationRequested) return;
            old.Dispose();
            try
            {
                _socket = OpenSocket(LocalPort);
                _interfaces = new List<LocalInterface>(); // the new socket joins the multicast group on each one again
            }
            catch (Exception ex)
            {
                _socket = null;
                IsRunning = false;
                Log.Error($"Discovery could not reopen UDP port {LocalPort}", ex);
            }
        }
    }

    /// <summary>Re-announce right away, e.g. after the device name changed.</summary>
    public void AnnounceNow() => Wake();

    /// <summary>Re-reads the network adapters (a direct link came up or went away) and asks everyone on them.</summary>
    public void RescanNetworks()
    {
        if (_socket == null) return;
        if (OperatingSystem.IsAndroid()) ReopenSocket();
        RefreshInterfaces();
        SendToAll(CreatePacket(DiscoveryPacket.TypeQuery));
    }

    /// <summary>Ask everyone nearby to announce themselves now.</summary>
    public void Refresh()
    {
        SendToAll(CreatePacket(DiscoveryPacket.TypeQuery));
    }

    public void AddUnicastTarget(IPAddress address, int port = 0)
    {
        var endpoint = new IPEndPoint(address, port == 0 ? _options.Port : port);
        _unicastTargets[endpoint] = 0;
        SendTo(CreatePacket(DiscoveryPacket.TypeQuery), endpoint);
    }

    public void RemoveUnicastTarget(IPAddress address)
    {
        foreach (var key in _unicastTargets.Keys.Where(k => k.Address.Equals(address)).ToList())
            _unicastTargets.TryRemove(key, out _);
    }

    /// <summary>Records a device that was reached by a direct connection (manual address).</summary>
    public void ReportReachable(DeviceInfo device)
    {
        Upsert(device with { IsManual = true }, device.Endpoints.FirstOrDefault());
    }

    /// <summary>Drops a device from the list (e.g. a Bluetooth placeholder now known by its real id).</summary>
    public void Forget(string deviceId)
    {
        if (_devices.TryRemove(deviceId, out var removed)) DeviceLost?.Invoke(this, new DeviceEventArgs(removed.Device));
    }

    public void Dispose()
    {
        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        if (_socket != null && _discoverable)
        {
            try { SendToAll(CreatePacket(DiscoveryPacket.TypeBye)); } catch { /* shutting down */ }
        }

        _cts.Cancel();
        _socket?.Dispose();
        try { Task.WaitAll(new[] { _receiveTask, _timerTask }.OfType<Task>().ToArray(), TimeSpan.FromSeconds(2)); } catch { /* ignore */ }
        _cts.Dispose();
    }

    private void Wake()
    {
        if (_wake.CurrentCount == 0) _wake.Release();
    }

    private void OnNetworkChanged(object? sender, EventArgs e)
    {
        _interfacesRefreshedAt = 0;
        Wake();
    }

    private async Task TimerLoopAsync()
    {
        var token = _cts.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                if (Environment.TickCount64 - _interfacesRefreshedAt > 30_000)
                {
                    var before = _interfaces.Count;
                    RefreshInterfaces();
                    if (_interfaces.Count != before) SendToAll(CreatePacket(DiscoveryPacket.TypeQuery));
                }

                if (_discoverable) SendToAll(CreatePacket(DiscoveryPacket.TypeAnnounce));
                else if (!_unicastTargets.IsEmpty) SendToAll(CreatePacket(DiscoveryPacket.TypeQuery));
                ExpireDevices();
            }
            catch (Exception ex)
            {
                Log.Warn("Discovery tick failed", ex);
            }

            try
            {
                await _wake.WaitAsync(_options.AnnounceInterval, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[DiscoveryPacket.MaxSize + 1];
        var token = _cts.Token;
        EndPoint any = new IPEndPoint(IPAddress.Any, 0);
        while (!token.IsCancellationRequested && _socket is { } socket)
        {
            try
            {
                var result = await socket.ReceiveFromAsync(buffer, SocketFlags.None, any, token).ConfigureAwait(false);
                if (result.RemoteEndPoint is IPEndPoint remote)
                    Handle(buffer.AsSpan(0, result.ReceivedBytes), remote);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex) when ((ex is ObjectDisposedException or SocketException && !ReferenceEquals(socket, _socket))
                                       || ex is SocketException { SocketErrorCode: SocketError.OperationAborted } && !token.IsCancellationRequested)
            {
                // Reopened on another network (the old socket's pending receive is cancelled): listen on the new socket.
                await Task.Delay(50, CancellationToken.None).ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionReset or SocketError.MessageSize)
            {
                // Harmless: an earlier datagram bounced or an oversized packet arrived.
            }
            catch (Exception ex)
            {
                Log.Warn("Discovery receive failed", ex);
                try { await Task.Delay(500, token).ConfigureAwait(false); } catch { break; }
            }
        }
    }

    private void Handle(ReadOnlySpan<byte> data, IPEndPoint remote)
    {
        var packet = DiscoveryPacket.TryParse(data);
        if (packet == null) return;
        var self = _self();
        if (packet.Id == self.Id) return;

        switch (packet.Type)
        {
            case DiscoveryPacket.TypeBye:
                if (_devices.TryRemove(packet.Id, out var gone))
                    DeviceLost?.Invoke(this, new DeviceEventArgs(gone.Device));
                return;

            case DiscoveryPacket.TypeQuery:
                if (!packet.Hidden) Upsert(ToDevice(packet, remote), EndpointOf(remote, packet.Port));
                if (_discoverable && ShouldReply(packet.Id))
                    SendTo(CreatePacket(DiscoveryPacket.TypeAnnounce), remote);
                return;

            case DiscoveryPacket.TypeAnnounce:
                var isNew = !_devices.ContainsKey(packet.Id);
                Upsert(ToDevice(packet, remote), EndpointOf(remote, packet.Port));

                // Answer newcomers directly so both sides see each other immediately.
                if (isNew && _discoverable && ShouldReply(packet.Id))
                    SendTo(CreatePacket(DiscoveryPacket.TypeAnnounce), remote);
                return;
        }
    }

    private bool ShouldReply(string deviceId)
    {
        var now = Environment.TickCount64;
        var last = _lastReplyTo.GetOrAdd(deviceId, 0);
        if (now - last < 1000) return false;
        _lastReplyTo[deviceId] = now;
        return true;
    }

    private DeviceInfo ToDevice(DiscoveryPacket packet, IPEndPoint remote) => new()
    {
        Id = packet.Id,
        Name = packet.Name,
        Fingerprint = packet.Fingerprint.ToLowerInvariant(),
        Platform = packet.Platform,
        Kind = packet.Kind,
        AppVersion = packet.AppVersion,
        DirectRoles = Direct.DirectRolesText.Parse(packet.Direct),
        Endpoints = EndpointOf(remote, packet.Port) is { } endpoint ? new[] { endpoint } : Array.Empty<IPEndPoint>(),
    };

    /// <summary>
    /// Where the sender can be reached; null when the packet came from one of this device's own addresses (another
    /// device's traffic passed on by this PC's hotspot): connecting there would reach this device itself.
    /// </summary>
    private IPEndPoint? EndpointOf(IPEndPoint remote, int port) =>
        _options.UseMulticastAndBroadcast && Direct.DirectAddresses.IsOwnAddress(remote.Address) ? null : new IPEndPoint(remote.Address, port);

    private void Upsert(DeviceInfo incoming, IPEndPoint? endpoint)
    {
        var now = Environment.TickCount64;
        DeviceInfo? found = null, changed = null;
        _devices.AddOrUpdate(
            incoming.Id,
            _ =>
            {
                found = incoming;
                return new Entry(incoming, now);
            },
            (_, existing) =>
            {
                var endpoints = existing.Device.Endpoints.ToList();
                if (endpoint != null)
                {
                    endpoints.RemoveAll(e => e.Equals(endpoint) || e.Address.Equals(endpoint.Address));
                    endpoints.Insert(0, endpoint);
                }

                var merged = incoming with
                {
                    Endpoints = endpoints.Take(4).ToList(),
                    IsManual = existing.Device.IsManual || incoming.IsManual,
                };
                if (merged.Name != existing.Device.Name || merged.Fingerprint != existing.Device.Fingerprint || merged.DirectRoles != existing.Device.DirectRoles
                    || !merged.Endpoints.SequenceEqual(existing.Device.Endpoints))
                    changed = merged;
                return new Entry(merged, now);
            });

        if (found != null) DeviceFound?.Invoke(this, new DeviceEventArgs(found));
        else if (changed != null) DeviceChanged?.Invoke(this, new DeviceEventArgs(changed));
    }

    private void ExpireDevices()
    {
        var cutoff = Environment.TickCount64 - (long)_options.DeviceTimeout.TotalMilliseconds;
        foreach (var (id, entry) in _devices)
        {
            if (entry.LastSeen < cutoff && _devices.TryRemove(id, out var removed))
                DeviceLost?.Invoke(this, new DeviceEventArgs(removed.Device));
        }
    }

    private DiscoveryPacket CreatePacket(string type)
    {
        var self = _self();
        var packet = new DiscoveryPacket
        {
            Type = type,
            Id = self.Id,
            Name = self.Name,
            Port = self.TcpPort,
            Fingerprint = self.Fingerprint,
            Platform = AppInfo.Platform,
            Kind = self.Kind,
            AppVersion = AppInfo.Version,
            Direct = Direct.DirectRolesText.Format(self.DirectRoles),
        };
        if (type == DiscoveryPacket.TypeQuery && !_discoverable)
        {
            packet.Hidden = true;
            packet.Name = "";
            packet.Fingerprint = "";
        }

        return packet;
    }

    private void SendToAll(DiscoveryPacket packet)
    {
        var socket = _socket;
        if (socket == null) return;
        var data = packet.Serialize();
        var reopen = false;

        if (_options.UseMulticastAndBroadcast)
        {
            foreach (var nic in _interfaces)
            {
                lock (_sendGate)
                {
                    try
                    {
                        socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, nic.Address.GetAddressBytes());
                        socket.SendTo(data, new IPEndPoint(_options.MulticastGroup, _options.Port));
                        _failingSends.TryRemove($"Multicast on {nic.Address}", out _);
                    }
                    catch (Exception ex)
                    {
                        WarnOnce($"Multicast on {nic.Address}", ex.Message);
                    }

                    try
                    {
                        if (nic.Broadcast != null) socket.SendTo(data, new IPEndPoint(nic.Broadcast, _options.Port));
                        _failingSends.TryRemove($"Broadcast on {nic.Address}", out _);
                    }
                    catch (Exception ex)
                    {
                        WarnOnce($"Broadcast on {nic.Address}", ex.Message);
                        // The phone's own network is "unreachable": the socket is stuck on a network Beam left.
                        if (ex is SocketException { SocketErrorCode: SocketError.NetworkUnreachable } && OperatingSystem.IsAndroid()) reopen = true;
                    }
                }
            }
        }

        foreach (var target in _unicastTargets.Keys) SendTo(data, target);
        if (reopen && Environment.TickCount64 - _reopenedAt > 30_000)
        {
            Log.Info("Discovery: reopening on the current network");
            ReopenSocket();
            _interfacesRefreshedAt = 0; // join the multicast group again on the next tick
        }
    }

    /// <summary>Logs a failure once until it changes or the send works again (e.g. Android blocking a background app's network).</summary>
    private void WarnOnce(string what, string error)
    {
        if (_failingSends.TryGetValue(what, out var known) && known == error) return;
        _failingSends[what] = error;
        Log.Warn($"{what} failed: {error}");
    }

    private void SendTo(DiscoveryPacket packet, IPEndPoint target) => SendTo(packet.Serialize(), target);

    private void SendTo(byte[] data, IPEndPoint target)
    {
        var socket = _socket;
        if (socket == null) return;
        lock (_sendGate)
        {
            try
            {
                socket.SendTo(data, target);
                _failingTargets.TryRemove(target, out _);
            }
            catch (Exception ex)
            {
                // Once per target and error: an address on another network fails every few seconds.
                if (!_failingTargets.TryGetValue(target, out var known) || known != ex.Message)
                {
                    _failingTargets[target] = ex.Message;
                    Log.Warn($"Discovery unicast to {target} failed: {ex.Message}");
                }
            }
        }
    }

    private void RefreshInterfaces()
    {
        _interfacesRefreshedAt = Environment.TickCount64;
        var list = new List<LocalInterface>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (!Direct.DirectAddresses.IsUsable(nic)) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                IPInterfaceProperties props;
                try { props = nic.GetIPProperties(); } catch { continue; }
                foreach (var unicast in props.UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (IPAddress.IsLoopback(unicast.Address)) continue;
                    list.Add(new LocalInterface(unicast.Address, ComputeBroadcast(unicast.Address, unicast.IPv4Mask)));
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Could not enumerate network adapters", ex);
        }

        var socket = _socket;
        if (socket != null && _options.UseMulticastAndBroadcast)
        {
            foreach (var nic in list.Where(n => !_interfaces.Any(o => o.Address.Equals(n.Address))))
            {
                try
                {
                    socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(_options.MulticastGroup, nic.Address));
                }
                catch (Exception ex)
                {
                    Log.Warn($"Could not join discovery group on {nic.Address}: {ex.Message}");
                }
            }
        }

        _interfaces = list;

        // A guest on another device's direct network (e.g. a phone that joined a PC's DIRECT-… Wi-Fi from a QR code):
        // ask its host directly instead of relying on broadcasts.
        if (_options.UseMulticastAndBroadcast)
        {
            Direct.DirectAddresses.Invalidate();
            foreach (var host in Direct.DirectAddresses.DirectHostsOfLocalNetworks())
            {
                var target = new IPEndPoint(host, _options.Port);
                if (_unicastTargets.TryAdd(target, 0)) Log.Info($"Discovery: on {host}'s direct network; asking it directly");
            }
        }
    }

    private static IPAddress? ComputeBroadcast(IPAddress address, IPAddress? mask)
    {
        if (mask == null) return null;
        var a = address.GetAddressBytes();
        var m = mask.GetAddressBytes();
        if (a.Length != 4 || m.Length != 4) return null;
        if (m.All(b => b == 255)) return null; // point-to-point / VPN adapters
        var result = new byte[4];
        for (var i = 0; i < 4; i++) result[i] = (byte)(a[i] | ~m[i]);
        return new IPAddress(result);
    }

    private sealed record Entry(DeviceInfo Device, long LastSeen);

    private sealed record LocalInterface(IPAddress Address, IPAddress? Broadcast);
}
