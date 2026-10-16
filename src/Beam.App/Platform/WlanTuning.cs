using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Beam.Core.Diagnostics;

namespace Beam.App.Platform;

/// <summary>
/// Windows' Wi-Fi API (wlanapi.dll) for two things during a direct link:
/// <list type="bullet">
/// <item>For the log: which channel this PC's own Wi-Fi is on, and its rates. A PC that hosts while staying on its
/// Wi-Fi shares one radio between both networks; on different channels it takes turns, which halves the speed.</item>
/// <item>While the link is up: "media streaming mode" on and background scans off, so Windows doesn't take the radio
/// away every minute or so to look for other networks (each time the transfer stalls for a moment). Both are settings
/// of this handle only: Windows puts them back when it is closed (<see cref="Dispose"/>), or when Beam exits.</item>
/// </list>
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WlanTuning : IDisposable
{
    private IntPtr _handle;

    private WlanTuning(IntPtr handle) => _handle = handle;

    /// <summary>Opens the Wi-Fi API and applies the settings to every Wi-Fi adapter; null when there's no Wi-Fi API.</summary>
    public static WlanTuning? Begin(string why)
    {
        try
        {
            if (WlanOpenHandle(2, IntPtr.Zero, out _, out var handle) != 0) return null;
            var tuning = new WlanTuning(handle);
            foreach (var (id, name) in tuning.Interfaces())
            {
                tuning.LogConnection(id, name);
                var streaming = tuning.SetBool(id, Opcode.MediaStreamingMode, true);
                var noScan = tuning.SetBool(id, Opcode.BackgroundScanEnabled, false);
                Log.Info($"Wi-Fi: {name}: media streaming mode {(streaming == 0 ? "on" : $"not set ({streaming})")}, " +
                         $"background scans {(noScan == 0 ? "off" : $"not changed ({noScan})")} while {why}");
            }

            return tuning;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or SEHException)
        {
            Log.Info($"Wi-Fi: Windows' Wi-Fi API unavailable ({ex.Message})");
            return null;
        }
    }

    /// <summary>For the log only: this PC's Wi-Fi connections (channel, band, rates).</summary>
    public static void LogConnections()
    {
        using var tuning = WlanOpenHandle(2, IntPtr.Zero, out _, out var handle) == 0 ? new WlanTuning(handle) : null;
        if (tuning == null) return;
        foreach (var (id, name) in tuning.Interfaces()) tuning.LogConnection(id, name);
    }

    /// <summary>The band this PC's Wi-Fi is connected on (2 or 5 GHz, 6 for 6 GHz), or 0 when it isn't connected.</summary>
    public static int ConnectedBand()
    {
        using var tuning = WlanOpenHandle(2, IntPtr.Zero, out _, out var handle) == 0 ? new WlanTuning(handle) : null;
        if (tuning == null) return 0;
        foreach (var (id, _) in tuning.Interfaces())
        {
            var channel = tuning.QueryInt(id, Opcode.ChannelNumber);
            if (channel is > 0 and <= 14) return 2;
            if (channel > 14) return 5; // 6 GHz channels overlap these numbers; 5 is the safe answer for a hotspot
        }

        return 0;
    }

    private void LogConnection(Guid id, string name)
    {
        if (!TryQuery(id, Opcode.CurrentConnection, out var data, out var size)) return;
        try
        {
            if (size < 588) return;
            var state = Marshal.ReadInt32(data, 0);
            if (state != 1) // wlan_interface_state_connected
            {
                Log.Info($"Wi-Fi: {name}: not connected to a network");
                return;
            }

            var ssidLength = Math.Min(32, Marshal.ReadInt32(data, 520));
            var ssid = new byte[ssidLength];
            Marshal.Copy(data + 524, ssid, 0, ssidLength);
            var phy = Marshal.ReadInt32(data, 568);
            var rx = Marshal.ReadInt32(data, 580) / 1000;
            var tx = Marshal.ReadInt32(data, 584) / 1000;
            var channel = QueryInt(id, Opcode.ChannelNumber);
            var band = channel is > 0 and <= 14 ? "2.4 GHz" : channel > 14 ? "5/6 GHz" : "?";
            Log.Info($"Wi-Fi: {name} is on {System.Text.Encoding.UTF8.GetString(ssid)}: channel {channel} ({band}), " +
                     $"receive {rx} Mbps, send {tx} Mbps (PHY {phy})");
        }
        finally
        {
            WlanFreeMemory(data);
        }
    }

    private List<(Guid Id, string Name)> Interfaces()
    {
        var list = new List<(Guid, string)>();
        if (WlanEnumInterfaces(_handle, IntPtr.Zero, out var data) != 0) return list;
        try
        {
            var count = Marshal.ReadInt32(data, 0);
            const int itemSize = 16 + 512 + 4; // GUID, WCHAR[256] description, state
            for (var i = 0; i < count; i++)
            {
                var item = data + 8 + i * itemSize;
                var id = Marshal.PtrToStructure<Guid>(item);
                var name = Marshal.PtrToStringUni(item + 16) ?? "Wi-Fi";
                list.Add((id, name));
            }
        }
        finally
        {
            WlanFreeMemory(data);
        }

        return list;
    }

    private int SetBool(Guid id, Opcode opcode, bool value)
    {
        var data = Marshal.AllocHGlobal(4);
        try
        {
            Marshal.WriteInt32(data, value ? 1 : 0);
            return WlanSetInterface(_handle, ref id, opcode, 4, data, IntPtr.Zero);
        }
        finally
        {
            Marshal.FreeHGlobal(data);
        }
    }

    private int QueryInt(Guid id, Opcode opcode)
    {
        if (!TryQuery(id, opcode, out var data, out var size)) return 0;
        try
        {
            return size >= 4 ? Marshal.ReadInt32(data) : 0;
        }
        finally
        {
            WlanFreeMemory(data);
        }
    }

    private bool TryQuery(Guid id, Opcode opcode, out IntPtr data, out int size) =>
        WlanQueryInterface(_handle, ref id, opcode, IntPtr.Zero, out size, out data, out _) == 0 && data != IntPtr.Zero;

    /// <summary>Closes the handle: Windows restores media streaming mode and background scans.</summary>
    public void Dispose()
    {
        var handle = Interlocked.Exchange(ref _handle, IntPtr.Zero);
        if (handle != IntPtr.Zero) WlanCloseHandle(handle, IntPtr.Zero);
    }

    private enum Opcode
    {
        BackgroundScanEnabled = 2,
        MediaStreamingMode = 3,
        CurrentConnection = 7,
        ChannelNumber = 8,
    }

    [DllImport("wlanapi.dll")]
    private static extern int WlanOpenHandle(int clientVersion, IntPtr reserved, out int negotiatedVersion, out IntPtr clientHandle);

    [DllImport("wlanapi.dll")]
    private static extern int WlanCloseHandle(IntPtr clientHandle, IntPtr reserved);

    [DllImport("wlanapi.dll")]
    private static extern int WlanEnumInterfaces(IntPtr clientHandle, IntPtr reserved, out IntPtr interfaceList);

    [DllImport("wlanapi.dll")]
    private static extern int WlanQueryInterface(IntPtr clientHandle, ref Guid interfaceGuid, Opcode opCode, IntPtr reserved,
        out int dataSize, out IntPtr data, out int opcodeValueType);

    [DllImport("wlanapi.dll")]
    private static extern int WlanSetInterface(IntPtr clientHandle, ref Guid interfaceGuid, Opcode opCode, int dataSize, IntPtr data, IntPtr reserved);

    [DllImport("wlanapi.dll")]
    private static extern void WlanFreeMemory(IntPtr memory);
}
