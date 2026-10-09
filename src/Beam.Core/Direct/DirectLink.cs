using System.Net;

namespace Beam.Core.Direct;

/// <summary>What a device can do with a direct Wi-Fi link.</summary>
[Flags]
public enum DirectRoles
{
    None = 0,

    /// <summary>Can start a direct network others join (Windows: Wi-Fi Direct group owner; Android: P2P group).</summary>
    Host = 1,

    /// <summary>Can join another device's direct network (Android 10+).</summary>
    Join = 2,
}

public static class DirectRolesText
{
    public static string Format(DirectRoles roles) => roles switch
    {
        DirectRoles.Host | DirectRoles.Join => "host,join",
        DirectRoles.Host => "host",
        DirectRoles.Join => "join",
        _ => "",
    };

    public static DirectRoles Parse(string? text)
    {
        var roles = DirectRoles.None;
        foreach (var part in (text ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == "host") roles |= DirectRoles.Host;
            else if (part == "join") roles |= DirectRoles.Join;
        }

        return roles;
    }
}

/// <summary>The name and passphrase of a direct network (WPA2), shared only with the device that should join.</summary>
public sealed record DirectNetwork(string Ssid, string Passphrase);

/// <summary>
/// The platform's direct Wi-Fi link (Wi-Fi Direct on Windows and Android). Beam.Core decides when to use it;
/// the platform only starts, joins and stops the network. Methods may take several seconds and throw
/// <see cref="DirectLinkException"/> with a message for the user when the device can't do it.
/// </summary>
public interface IDirectLink
{
    DirectRoles Roles { get; }

    /// <summary>Starts this device's direct network, or returns the one already running.</summary>
    Task<DirectNetwork> HostAsync(CancellationToken cancellationToken);

    /// <summary>Joins another device's direct network. Returns the host's address on it, if known.</summary>
    Task<IPAddress?> JoinAsync(DirectNetwork network, CancellationToken cancellationToken);

    /// <summary>Stops hosting and leaves any joined network.</summary>
    void Stop();
}

public sealed class DirectLinkException : Exception
{
    public DirectLinkException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}
