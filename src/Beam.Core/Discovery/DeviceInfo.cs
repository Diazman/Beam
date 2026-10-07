using System.Net;

namespace Beam.Core.Discovery;

/// <summary>Kinds of devices. Only desktops exist today; phones will announce themselves as "phone".</summary>
public static class DeviceKinds
{
    public const string Desktop = "desktop";
    public const string Laptop = "laptop";
    public const string Phone = "phone";
}

/// <summary>A nearby device running Beam. Instances are immutable snapshots.</summary>
public sealed record DeviceInfo
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    /// <summary>Certificate fingerprint the device advertised; the TLS connection must present the same one.</summary>
    public required string Fingerprint { get; init; }

    public string Platform { get; init; } = "unknown";

    public string Kind { get; init; } = DeviceKinds.Desktop;

    public string AppVersion { get; init; } = "";

    /// <summary>Addresses the device can be reached at, most recently seen first.</summary>
    public IReadOnlyList<IPEndPoint> Endpoints { get; init; } = Array.Empty<IPEndPoint>();

    /// <summary>True when the user added the device by address instead of it being discovered.</summary>
    public bool IsManual { get; init; }

    public string AddressText => Endpoints.Count > 0 ? Endpoints[0].Address.ToString() : "";
}

public sealed class DeviceEventArgs : EventArgs
{
    public DeviceEventArgs(DeviceInfo device) => Device = device;

    public DeviceInfo Device { get; }
}
