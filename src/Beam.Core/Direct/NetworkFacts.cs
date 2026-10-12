using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Beam.Core.Direct;

/// <summary>Facts about this device's own networks that decide how two computers connect directly.</summary>
public static class NetworkFacts
{
    private static readonly string[] VirtualAdapterWords = { "virtual", "hyper-v", "vmware", "virtualbox", "vpn", "tap-", "tunnel", "loopback", "bluetooth" };
    private static readonly object Gate = new();
    private static bool _wired;
    private static long _checkedAt = long.MinValue / 2;

    /// <summary>
    /// A real network cable with a gateway (internet) is connected: this computer's Wi-Fi can join another computer's
    /// network without the computer losing its internet. Checked at most every 30 seconds.
    /// </summary>
    public static bool HasWiredInternet()
    {
        lock (Gate)
        {
            if (Environment.TickCount64 - _checkedAt < 30_000) return _wired;
            _checkedAt = Environment.TickCount64;
            try
            {
                _wired = NetworkInterface.GetAllNetworkInterfaces().Any(IsWiredWithGateway);
            }
            catch
            {
                _wired = false;
            }

            return _wired;
        }
    }

    private static bool IsWiredWithGateway(NetworkInterface nic)
    {
        if (nic.OperationalStatus != OperationalStatus.Up) return false;
        if (nic.NetworkInterfaceType is not (NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT
            or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.Ethernet3Megabit)) return false;
        var text = (nic.Description + " " + nic.Name).ToLowerInvariant();
        if (VirtualAdapterWords.Any(text.Contains)) return false;
        return nic.GetIPProperties().GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
    }
}
