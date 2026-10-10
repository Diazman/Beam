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
    private readonly object _sendGate = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _wake = new(0);
    private Socket? _socket;
    private List<LocalInterface> _interfaces = new();
    private long _interfacesRefreshedAt;
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
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.EnableBroadcast = true;
            socket.MulticastLoopback = true;
            if (OperatingSystem.IsWindows())
            {
                // Stop ICMP "port unreachable" replies from surfacing as receive errors.
                const int SioUdpConnReset = unchecked((int)0x9800000C);
                socket.IOControl(SioUdpConnReset, new byte[] { 0, 0, 0, 0 }, null);
            }

            socket.Bind(new IPEndPoint(IPAddress.Any, _options.Port));
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

    /// <summary>Re-announce right away, e.g. after the device name changed.</summary>
    public void AnnounceNow() => Wake();

    /// <summary>Re-reads the network adapters (a direct link came up or went away) and asks everyone on them.</summary>
    public void RescanNetworks()
    {
        if (_socket == null) return;
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
        while (!token.IsCancellationRequested && _socket != null)
        {
            try
            {
                var result = await _socket.ReceiveFromAsync(buffer, SocketFlags.None, any, token).ConfigureAwait(false);
                if (result.RemoteEndPoint is IPEndPoint remote)
                    Handle(buffer.AsSpan(0, result.ReceivedBytes), remote);
            }
            catch (OperationCanceledException)
            {
                break;
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
                if (!packet.Hidden) Upsert(ToDevice(packet, remote), new IPEndPoint(remote.Address, packet.Port));
                if (_discoverable && ShouldReply(packet.Id))
                    SendTo(CreatePacket(DiscoveryPacket.TypeAnnounce), remote);
                return;

            case DiscoveryPacket.TypeAnnounce:
                var isNew = !_devices.ContainsKey(packet.Id);
                Upsert(ToDevice(packet, remote), new IPEndPoint(remote.Address, packet.Port));

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

    private static DeviceInfo ToDevice(DiscoveryPacket packet, IPEndPoint remote) => new()
    {
        Id = packet.Id,
        Name = packet.Name,
        Fingerprint = packet.Fingerprint.ToLowerInvariant(),
        Platform = packet.Platform,
        Kind = packet.Kind,
        AppVersion = packet.AppVersion,
        DirectRoles = Direct.DirectRolesText.Parse(packet.Direct),
        Endpoints = new[] { new IPEndPoint(remote.Address, packet.Port) },
    };

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
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"Multicast on {nic.Address} failed: {ex.Message}");
                    }

                    try
                    {
                        if (nic.Broadcast != null) socket.SendTo(data, new IPEndPoint(nic.Broadcast, _options.Port));
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"Broadcast on {nic.Address} failed: {ex.Message}");
                    }
                }
            }
        }

        foreach (var target in _unicastTargets.Keys) SendTo(data, target);
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
            }
            catch (Exception ex)
            {
                Log.Warn($"Discovery unicast to {target} failed: {ex.Message}");
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
