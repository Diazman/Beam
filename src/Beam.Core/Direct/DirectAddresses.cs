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
    private static List<(uint Address, uint Mask, bool Direct)> _local = new();
    private static long _refreshedAt = long.MinValue;

    /// <summary>True when <paramref name="remote"/> is on the same subnet as one of this device's direct-link adapters.</summary>
    public static bool IsDirect(IPAddress remote)
    {
        if (remote.AddressFamily != AddressFamily.InterNetwork) return false;
        var value = ToUInt(remote);
        foreach (var (address, mask, direct) in LocalAdapters())
        {
            if ((value & mask) != (address & mask)) continue;
            if (direct || KnownDirectSubnets.Any(s => (value & s.Mask) == s.Network)) return true;
        }

        return false;
    }

    /// <summary>Re-reads the adapters on the next check (after joining or leaving a direct network).</summary>
    public static void Invalidate()
    {
        lock (Gate) _refreshedAt = long.MinValue;
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
            if (Environment.TickCount64 - _refreshedAt < 3000) return _local;
            var list = new List<(uint, uint, bool)>();
            try
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
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
            return _local;
        }
    }

    private static uint ToUInt(IPAddress address)
    {
        var b = address.GetAddressBytes();
        return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
    }
}
