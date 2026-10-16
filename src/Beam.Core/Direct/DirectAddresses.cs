using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Beam.Core.Direct;

/// <summary>Tells whether an address is reached over a direct Wi-Fi link rather than the normal network.</summary>
public static class DirectAddresses
{
    // Wi-Fi Direct networks always use these: Windows group owners hand out 192.168.137.x,
    // Android group owners 192.168.49.x.
    private static readonly (uint Network, uint Mask)[] KnownDirectSubnets =
    {
        (ToUInt(IPAddress.Parse("192.168.137.0")), 0xFFFFFF00),
        (ToUInt(IPAddress.Parse("192.168.49.0")), 0xFFFFFF00),
    };

    private static readonly object Gate = new();

    // Direct networks on other subnets (a phone's hotspot picks its own): this device's address on each, and the host's
    // (null when this device hosts it). Marked when started or joined, forgotten when left.
    private static readonly object MarkGate = new();
    private static readonly Dictionary<uint, uint?> Marked = new();

    /// <summary>
    /// <paramref name="own"/> (this device's address) is on a direct network whose subnet isn't one Wi-Fi Direct always
    /// uses: hosted by this device (<paramref name="host"/> null) or by <paramref name="host"/>.
    /// </summary>
    public static void MarkDirectNetwork(IPAddress own, IPAddress? host)
    {
        if (own.AddressFamily != AddressFamily.InterNetwork) return;
        lock (MarkGate) Marked[ToUInt(own)] = host is { AddressFamily: AddressFamily.InterNetwork } ? ToUInt(host) : null;
        Diagnostics.Log.Info($"Direct: {own}'s network is a direct network{(host != null ? $" (host {host})" : " (this device hosts it)")}");
    }

    /// <summary>Forgets the marked networks this device hosts (<paramref name="hosted"/>) or joined.</summary>
    public static void ForgetDirectNetworks(bool hosted)
    {
        lock (MarkGate)
            foreach (var own in Marked.Where(m => m.Value == null == hosted).Select(m => m.Key).ToList())
                Marked.Remove(own);
    }

    private static bool IsMarked(uint own, out uint? host)
    {
        lock (MarkGate) return Marked.TryGetValue(own, out host);
    }
    private static List<(uint Address, uint Mask, bool Direct)> _local = new();
    private static bool _fresh;
    private static long _refreshedAt;

    /// <summary>True when <paramref name="remote"/> is on the same subnet as one of this device's direct-link adapters.</summary>
    public static bool IsDirect(IPAddress remote)
    {
        if (remote.AddressFamily != AddressFamily.InterNetwork) return false;
        var value = ToUInt(remote);
        foreach (var (address, mask, direct) in LocalAdapters())
        {
            if ((value & mask) != (address & mask)) continue;
            if (direct || KnownDirectSubnets.Any(s => (value & s.Mask) == s.Network) || IsMarked(address, out _)) return true;
        }

        return false;
    }

    /// <summary>How many local IPv4 addresses the last check saw (tests).</summary>
    internal static int LocalAddressCount => LocalAdapters().Count;

    /// <summary>
    /// The address of the device hosting the direct network this device is on (Windows: 192.168.137.1, Android:
    /// 192.168.49.1), when one of this device's addresses is on such a network as a guest; else null.
    /// </summary>
    public static IEnumerable<IPAddress> DirectHostsOfLocalNetworks()
    {
        foreach (var (address, _, _) in LocalAdapters())
        {
            if (IsMarked(address, out var marked))
            {
                if (marked is { } markedHost) yield return FromUInt(markedHost);
                continue;
            }

            if (!KnownDirectSubnets.Any(s => (address & s.Mask) == s.Network) || (address & 0xFF) == 1) continue;
            var host = (address & 0xFFFFFF00) | 1;
            yield return new IPAddress(new[] { (byte)(host >> 24), (byte)(host >> 16), (byte)(host >> 8), (byte)host });
        }
    }

    /// <summary>
    /// This device hosts a usable direct network: it has the host's address (.1) on a Wi-Fi Direct subnet (Windows
    /// 192.168.137.1, Android 192.168.49.1), or an address on a Wi-Fi Direct adapter. A platform reporting "started"
    /// before the adapter has an address is not usable yet.
    /// </summary>
    public static bool HasHostedDirectNetwork()
    {
        foreach (var (address, _, direct) in LocalAdapters())
        {
            if (direct && (address >> 16) != 0xA9FE) return true; // not 169.254.x (no address handed out yet)
            if (IsMarked(address, out var host) && host == null) return true;
            if ((address & 0xFF) == 1 && KnownDirectSubnets.Any(s => (address & s.Mask) == s.Network)) return true;
        }

        return false;
    }

    /// <summary>
    /// The host of the direct network <paramref name="own"/> is on (.1 of a Wi-Fi Direct subnet), when this device is a
    /// guest there; null otherwise (including when this device is the host).
    /// </summary>
    public static IPAddress? DirectHostOf(IPAddress own)
    {
        if (own.AddressFamily != AddressFamily.InterNetwork) return null;
        var address = ToUInt(own);
        if (IsMarked(address, out var marked)) return marked is { } markedHost ? FromUInt(markedHost) : null;
        if (!KnownDirectSubnets.Any(s => (address & s.Mask) == s.Network) || (address & 0xFF) == 1) return null;
        return FromUInt((address & 0xFFFFFF00) | 1);
    }

    /// <summary>This device's own address (not loopback): connecting to it would reach this device itself.</summary>
    public static bool IsOwnAddress(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(address)) return false;
        var value = ToUInt(address);
        return LocalAdapters().Any(a => a.Address == value);
    }

    /// <summary>This device's address on the network that <paramref name="other"/> is on (e.g. on a direct network just joined).</summary>
    public static IPAddress? OwnAddressNear(IPAddress other)
    {
        if (other.AddressFamily != AddressFamily.InterNetwork) return null;
        var value = ToUInt(other);
        foreach (var (address, mask, _) in LocalAdapters())
        {
            if ((value & mask) == (address & mask) && address != value) return FromUInt(address);
        }

        return null;
    }

    /// <summary>Re-reads the adapters on the next check (after joining or leaving a direct network).</summary>
    public static void Invalidate()
    {
        lock (Gate) _fresh = false;
    }

    /// <summary>
    /// Adapters that can carry traffic. Windows doesn't always report its Wi-Fi Direct adapter as "Up" while it hosts
    /// a direct network, so anything that isn't clearly down (and has an address) counts.
    /// </summary>
    public static bool IsUsable(NetworkInterface nic) =>
        nic.NetworkInterfaceType != NetworkInterfaceType.Loopback
        && nic.OperationalStatus is not (OperationalStatus.Down or OperationalStatus.NotPresent or OperationalStatus.LowerLayerDown);

    /// <summary>One line per adapter with an IPv4 address, for the log (helps when a direct connection doesn't work).</summary>
    public static string Describe()
    {
        try
        {
            return string.Join("; ", NetworkInterface.GetAllNetworkInterfaces()
                .Select(nic => (nic, ips: nic.GetIPProperties().UnicastAddresses.Where(u => u.Address.AddressFamily == AddressFamily.InterNetwork)
                    .Select(u => $"{u.Address}/{u.PrefixLength}").ToList()))
                .Where(x => x.ips.Count > 0)
                .Select(x => $"{x.nic.Name} [{x.nic.Description}] {x.nic.OperationalStatus}{(IsDirectAdapter(x.nic.Name, x.nic.Description) ? " direct" : "")}: {string.Join(", ", x.ips)}"));
        }
        catch (Exception ex)
        {
            return "adapters unavailable: " + ex.Message;
        }
    }

    /// <summary>Wi-Fi Direct adapters: "p2p-wlan0-0" on Android, "Microsoft Wi-Fi Direct Virtual Adapter" on Windows.</summary>
    internal static bool IsDirectAdapter(string name, string description) =>
        name.StartsWith("p2p", StringComparison.OrdinalIgnoreCase)
        || description.Contains("Wi-Fi Direct", StringComparison.OrdinalIgnoreCase)
        || description.Contains("WiFi Direct", StringComparison.OrdinalIgnoreCase);

    private static List<(uint Address, uint Mask, bool Direct)> LocalAdapters()
    {
        lock (Gate)
        {
            // (Not "now - long.MinValue": that overflows, and the adapters were never read on real devices.)
            if (_fresh && Environment.TickCount64 - _refreshedAt < 3000) return _local;
            var list = new List<(uint, uint, bool)>();
            try
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (!IsUsable(nic)) continue;
                    var direct = IsDirectAdapter(nic.Name, nic.Description);
                    foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                    {
                        if (unicast.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        var mask = unicast.IPv4Mask is { } m && !m.Equals(IPAddress.Any) ? ToUInt(m) : 0xFFFFFF00;
                        list.Add((ToUInt(unicast.Address), mask, direct));
                    }
                }
            }
            catch (Exception ex)
            {
                Diagnostics.Log.Warn("Could not read network adapters", ex);
            }

            _local = list;
            _refreshedAt = Environment.TickCount64;
            _fresh = true;
            return _local;
        }
    }

    private static IPAddress FromUInt(uint value) =>
        new(new[] { (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value });

    private static uint ToUInt(IPAddress address)
    {
        var b = address.GetAddressBytes();
        return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
    }
}
