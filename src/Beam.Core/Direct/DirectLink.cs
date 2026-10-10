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
public sealed record DirectNetwork(string Ssid, string Passphrase)
{
    private const string Alphabet = "abcdefghijkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    /// <summary>
    /// A new network for this device: "DIRECT-" plus two random characters is required for Wi-Fi Direct groups
    /// (Android checks it), then the device's name so people know whose it is. At most 32 bytes.
    /// </summary>
    public static DirectNetwork Create(string deviceName)
    {
        var name = new System.Text.StringBuilder("DIRECT-").Append(Random(2)).Append("-Beam-");
        foreach (var c in deviceName)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is ' ' or '-' or '_')) continue;
            if (name.Length >= 32) break;
            name.Append(c);
        }

        return new DirectNetwork(name.ToString().TrimEnd(' ', '-'), Random(12));
    }

    private static string Random(int length)
    {
        var chars = new char[length];
        for (var i = 0; i < length; i++) chars[i] = Alphabet[System.Security.Cryptography.RandomNumberGenerator.GetInt32(Alphabet.Length)];
        return new string(chars);
    }

    /// <summary>The text of a "join this Wi-Fi" QR code, which iPhone and Android cameras understand.</summary>
    public string ToWifiQrText() => $"WIFI:T:WPA;S:{Escape(Ssid)};P:{Escape(Passphrase)};;";

    private static string Escape(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (c is '\\' or ';' or ',' or ':' or '"') builder.Append('\\');
            builder.Append(c);
        }

        return builder.ToString();
    }
}

/// <summary>
/// The platform's direct Wi-Fi link (Wi-Fi Direct on Windows and Android). Beam.Core decides when to use it;
/// the platform only starts, joins and stops the network. Methods may take several seconds and throw
/// <see cref="DirectLinkException"/> with a message for the user when the device can't do it.
/// </summary>
public interface IDirectLink
{
    DirectRoles Roles { get; }

    /// <summary>
    /// Starts this device's direct network, or returns the one already running. <paramref name="preferred"/> is the
    /// name and passphrase to use (the same every time, so paired devices can find it again).
    /// </summary>
    Task<DirectNetwork> HostAsync(DirectNetwork? preferred, CancellationToken cancellationToken);

    /// <summary>Joins another device's direct network. Returns the host's address on it, if known.</summary>
    Task<IPAddress?> JoinAsync(DirectNetwork network, CancellationToken cancellationToken);

    /// <summary>Stops hosting and leaves any joined network.</summary>
    void Stop();
}

/// <summary>A link that can also list the Wi-Fi networks in range (phones: to notice a paired PC's direct network).</summary>
public interface IDirectNetworkScanner
{
    /// <summary>Names of the Wi-Fi networks seen recently (may be empty when scanning isn't allowed).</summary>
    IReadOnlyCollection<string> VisibleNetworks();
}

public sealed class DirectLinkException : Exception
{
    public DirectLinkException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}
