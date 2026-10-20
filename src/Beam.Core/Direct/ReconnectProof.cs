using System.Security.Cryptography;
using System.Text;
using Beam.Core.Protocol;

namespace Beam.Core.Direct;

/// <summary>
/// Lets a sender set a lost direct link up again over Bluetooth without the receiver's user accepting it again.
/// <para>
/// Over Bluetooth a sender's identity is only claimed, so the receiver's user is asked. Once a session is verified on
/// the direct network (TLS with the sender's certificate), the receiver gives the sender a random key over that TLS
/// connection. A later proposal carries an HMAC with that key over the session, both devices and the sender's
/// certificate; its offer one over the session, nonce and network details. Only the device the key was given to can
/// make them, a relayed message can't be changed or used for another session, and the direct network's TLS check still
/// follows as for any session.
/// </para>
/// </summary>
internal static class ReconnectProof
{
    /// <summary>A new key (256 random bits).</summary>
    public static string NewKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    public static bool IsKey(string? key)
    {
        if (string.IsNullOrEmpty(key) || key.Length > 64) return false;
        try
        {
            return Convert.FromBase64String(key).Length == 32;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static string ForPropose(string key, DirectRequestMessage m) =>
        Mac(key, "beam-reconnect-propose-1", m.SessionId, m.ReceiverId, m.SenderId, m.SenderFingerprint?.ToLowerInvariant());

    public static string ForOffer(string key, DirectRequestMessage m) =>
        Mac(key, "beam-reconnect-offer-1", m.SessionId, m.Nonce, m.Ssid, m.Passphrase, m.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            m.TtlSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture), m.HostIsComputer ? "1" : "0");

    /// <summary>Whether <paramref name="given"/> is the proof <paramref name="expected"/> (in constant time).</summary>
    public static bool Matches(string expected, string? given)
    {
        if (string.IsNullOrEmpty(given)) return false;
        var a = Encoding.ASCII.GetBytes(expected);
        var b = Encoding.ASCII.GetBytes(given);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    private static string Mac(string key, params string?[] parts)
    {
        using var hmac = new HMACSHA256(Convert.FromBase64String(key));
        var text = new StringBuilder();
        foreach (var part in parts)
        {
            var value = part ?? "";
            text.Append(value.Length).Append(':').Append(value).Append('\n'); // length-prefixed: no two field lists read alike
        }

        // 128 bits are plenty for a proof checked online, and keep the message within one Bluetooth value.
        return Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(text.ToString())), 0, 16);
    }
}
