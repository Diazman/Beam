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

    /// <summary>
    /// Joins a direct network as an ordinary Wi-Fi client (Windows: its Wi-Fi adapter leaves the current Wi-Fi and joins
    /// the WPA2 network by name and passphrase). That works for another PC's network and for a phone's Wi-Fi Direct group,
    /// which accepts such "legacy" clients (Android 10+ groups created with a name and passphrase). Announced as its own
    /// word ("joinpc") so Beam 1.5.3 and older on phones keep their old behavior.
    /// </summary>
    JoinComputers = 4,

    // What the device tells others about its situation (announced with the roles):

    /// <summary>Has internet over a cable: its Wi-Fi can join another network without losing internet.</summary>
    Wired = 8,

    /// <summary>Its network shares its internet (Windows' Mobile hotspot): whoever joins it keeps internet.</summary>
    SharesInternet = 16,

    /// <summary>
    /// Receive is open on it: it hosts the direct link (its own network) for anyone sending to it, so a sender joins
    /// it whatever both devices are.
    /// </summary>
    Receiving = 32,

    /// <summary>
    /// Speaks the sender-hosted session protocol (propose, offer, ready, verify, end): the sender starts its network when
    /// Send is pressed and this device joins it. Devices without it only know the older receiver-hosted requests.
    /// </summary>
    SenderHosted = 64,
}

/// <summary>A direct link that can tell whether its network would share this device's internet (Windows' hotspot).</summary>
public interface IInternetSharing
{
    bool CanShareInternet { get; }

    /// <summary>
    /// Set before a network is started: the next one should share this device's internet when it can (Windows' Mobile
    /// hotspot), because a phone will join it. A phone that can be on only one Wi-Fi network leaves a network without
    /// internet after a while; one with internet it keeps. Otherwise the usual network (Wi-Fi Direct).
    /// </summary>
    bool PreferInternetSharing { get; set; }
}

public static class DirectRolesText
{
    public static string Format(DirectRoles roles)
    {
        var parts = new List<string>(7);
        if (roles.HasFlag(DirectRoles.Host)) parts.Add("host");
        if (roles.HasFlag(DirectRoles.Join)) parts.Add("join");
        if (roles.HasFlag(DirectRoles.JoinComputers)) parts.Add("joinpc");
        if (roles.HasFlag(DirectRoles.Wired)) parts.Add("wired");
        if (roles.HasFlag(DirectRoles.SharesInternet)) parts.Add("share");
        if (roles.HasFlag(DirectRoles.Receiving)) parts.Add("rx");
        if (roles.HasFlag(DirectRoles.SenderHosted)) parts.Add("shost");
        return string.Join(",", parts);
    }

    public static DirectRoles Parse(string? text)
    {
        var roles = DirectRoles.None;
        foreach (var part in (text ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == "host") roles |= DirectRoles.Host;
            else if (part == "join") roles |= DirectRoles.Join;
            else if (part == "joinpc") roles |= DirectRoles.JoinComputers;
            else if (part == "wired") roles |= DirectRoles.Wired;
            else if (part == "share") roles |= DirectRoles.SharesInternet;
            else if (part == "rx") roles |= DirectRoles.Receiving;
            else if (part == "shost") roles |= DirectRoles.SenderHosted;
        }

        return roles;
    }
}

/// <summary>The name and passphrase of a direct network (WPA2), shared only with the device that should join.</summary>
public sealed record DirectNetwork(string Ssid, string Passphrase)
{
    /// <summary>
    /// Hosted by a computer (Windows' Wi-Fi Direct network): phones join it as an ordinary Wi-Fi network right away,
    /// since joining it the Wi-Fi Direct way fails on many phones. Not part of the network's identity.
    /// </summary>
    public bool HostIsComputer { get; init; }

    /// <summary>
    /// The network shares the host's internet (Windows' Mobile hotspot), so phones on it have no reason to switch to
    /// another Wi-Fi. Not part of the network's identity.
    /// </summary>
    public bool SharesInternet { get; init; }

    public bool Equals(DirectNetwork? other) => other is not null && Ssid == other.Ssid && Passphrase == other.Passphrase;

    public override int GetHashCode() => HashCode.Combine(Ssid, Passphrase);

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

    /// <summary>
    /// The network for a send's direct sessions: this device's usual name (a phone that approved joining it before may
    /// connect without asking again), with a passphrase made just for it (about 115 bits), useless once it goes off.
    /// </summary>
    public static DirectNetwork ForSession(string ssid) => new(ssid, Random(20));

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

/// <summary>A link that says when a network it joined went away (stopped by the other device, or out of range).</summary>
public interface IDirectLinkEvents
{
    /// <summary>Raised (on any thread) when the joined network was lost; this device is back on its normal network.</summary>
    event Action? Lost;
}

/// <summary>
/// A link whose Wi-Fi the user may have switched off, and that can't switch it on by itself (Android 10+ doesn't let
/// apps): it can only show the system's Wi-Fi panel and wait for the user.
/// </summary>
public interface IDirectRadio
{
    /// <summary>False when Wi-Fi is switched off; null when that can't be told.</summary>
    bool? IsWifiEnabled { get; }

    /// <summary>
    /// Shows the system's Wi-Fi panel (when Beam is in front) and waits up to <paramref name="wait"/> for Wi-Fi to be on
    /// and ready. True when it is.
    /// </summary>
    Task<bool> RequestWifiOnAsync(TimeSpan wait, CancellationToken cancellationToken);
}

/// <summary>A link that can also list the Wi-Fi networks in range (phones: to notice a paired PC's direct network).</summary>
public interface IDirectNetworkScanner
{
    /// <summary>Names of the Wi-Fi networks seen recently (may be empty when scanning isn't allowed).</summary>
    IReadOnlyCollection<string> VisibleNetworks();
}

public sealed class DirectLinkException : Exception
{
    public DirectLinkException(string message, Exception? inner = null, bool declined = false, bool transient = false)
        : base(message, inner)
    {
        Declined = declined;
        Transient = transient;
    }

    /// <summary>The other device's user said no (so don't try another way that would ask again).</summary>
    public bool Declined { get; }

    /// <summary>
    /// The other device couldn't be reached just now (e.g. a phone between networks after its direct link dropped):
    /// worth trying again shortly when recovering a running transfer. Never set for refusals or identity problems.
    /// </summary>
    public bool Transient { get; }

    /// <summary>This device's Wi-Fi is switched off (the user has to turn it on; Beam may open the system's Wi-Fi panel).</summary>
    public bool WifiOff { get; init; }
}
