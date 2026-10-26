using System.Security.Cryptography;
using System.Text;

namespace Beam.Core.Nearby;

/// <summary>
/// What a device with Receive open says over Bluetooth LE, in a legacy advertisement's manufacturer data (20 bytes at
/// most, which every Windows and Android Bluetooth stack accepts next to the flags it adds): "Bm", version, flags, a short hash of the device id
/// (so a sender can tell a device it already knows), a checksum (a garbled read is dropped, not listed as a strange
/// device) and the start of its name. No secrets: anyone nearby can read it.
/// </summary>
public sealed record NearbyBeacon(byte[] IdHash, string Name, NearbyBeaconFlags Flags)
{
    /// <summary>Bluetooth company identifier for the manufacturer data (0xFFFF: no company, for tests and apps).</summary>
    public const ushort CompanyId = 0xFFFF;

    public const int MaxPayload = 20;
    private const int HeaderLength = 11;
    private const byte Version = 2;

    /// <summary>The first 6 bytes of SHA-256 of the device id.</summary>
    public static byte[] HashOf(string deviceId) => SHA256.HashData(Encoding.UTF8.GetBytes(deviceId))[..6];

    public string IdHashText => Convert.ToHexString(IdHash).ToLowerInvariant();

    public bool Matches(string deviceId) => HashOf(deviceId).AsSpan().SequenceEqual(IdHash);

    public byte[] ToBytes()
    {
        var name = Encoding.UTF8.GetBytes(Name);
        var length = Math.Min(name.Length, MaxPayload - HeaderLength);
        while (length > 0 && length < name.Length && (name[length] & 0xC0) == 0x80) length--; // don't split a character
        var bytes = new byte[HeaderLength + length];
        bytes[0] = (byte)'B';
        bytes[1] = (byte)'m';
        bytes[2] = Version;
        bytes[3] = (byte)Flags;
        IdHash.AsSpan(0, 6).CopyTo(bytes.AsSpan(4));
        name.AsSpan(0, length).CopyTo(bytes.AsSpan(HeaderLength));
        bytes[10] = Checksum(bytes);
        return bytes;
    }

    /// <summary>Reads manufacturer data; null when it isn't a Beam beacon this version understands.</summary>
    public static NearbyBeacon? TryParse(ReadOnlySpan<byte> data)
    {
        if (data.Length <= HeaderLength || data.Length > MaxPayload || data[0] != 'B' || data[1] != 'm' || data[2] != Version) return null;
        if (data[10] != Checksum(data)) return null;
        string name;
        try
        {
            name = new UTF8Encoding(false, true).GetString(data[HeaderLength..]);
        }
        catch (DecoderFallbackException)
        {
            name = "";
        }

        if (string.IsNullOrWhiteSpace(name)) return null;
        return new NearbyBeacon(data.Slice(4, 6).ToArray(), name, (NearbyBeaconFlags)data[3]);
    }

    /// <summary>CRC-8 (polynomial 0x07) of everything but the checksum byte itself.</summary>
    private static byte Checksum(ReadOnlySpan<byte> data)
    {
        byte crc = 0;
        for (var i = 0; i < data.Length; i++)
        {
            if (i == 10) continue;
            crc ^= data[i];
            for (var bit = 0; bit < 8; bit++) crc = (byte)((crc & 0x80) != 0 ? (crc << 1) ^ 0x07 : crc << 1);
        }

        return crc;
    }
}

[Flags]
public enum NearbyBeaconFlags : byte
{
    None = 0,

    /// <summary>Its own network was on (for senders up to Beam 1.5.3, which joined it). No longer sent; others ignore it.</summary>
    Hosting = 1,

    /// <summary>A computer (otherwise a phone).</summary>
    Computer = 2,

    /// <summary>Can start its own network (otherwise a sender needs a shared network with it).</summary>
    CanHost = 4,

    /// <summary>Takes sender-hosted direct sessions (joins the sender's network when it sends); older versions don't.</summary>
    SenderHosted = 8,
}
