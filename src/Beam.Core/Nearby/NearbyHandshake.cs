using System.Security.Cryptography;
using System.Text;

namespace Beam.Core.Nearby;

/// <summary>
/// The Bluetooth handshake that hands a sender what a QR code would: the receiver's pairing code (who it is, its
/// certificate fingerprint, its addresses, and its own network's name and passphrase). Both sides make a fresh P-256
/// key; the code travels encrypted (AES-256-CBC, then HMAC-SHA256 over everything) with keys from ECDH + HKDF, so
/// someone listening to the radio learns nothing. The transfer itself still uses Beam's TLS connection, checked
/// against the fingerprint in the code, and still needs the receiver's approval.
/// </summary>
public static class NearbyHandshake
{
    private const byte Version = 1;
    private const int PointLength = 65; // uncompressed P-256 point
    private const int IvLength = 16;
    private const int MacLength = 32;
    private static readonly byte[] Info = Encoding.ASCII.GetBytes("beam-nearby-v1");

    public enum Status : byte
    {
        /// <summary>The receiver is still getting ready (starting its network): read again shortly.</summary>
        Working = 0,
        Ready = 1,

        /// <summary>The receiver closed Receive, or can't help this sender (the message says why).</summary>
        Refused = 2,

        /// <summary>The receiver joined the sender's network; the text is its address there (may be empty).</summary>
        Joined = 3,

        /// <summary>The receiver accepts the proposed direct session (sender-hosted); the text is empty.</summary>
        Accepted = 4,
    }

    /// <summary>What the sender asks for with its request.</summary>
    [Flags]
    public enum RequestFlags : byte
    {
        None = 0,

        /// <summary>Don't start your network: this device hosts, and asks you to join its network next.</summary>
        SenderHosts = 1,
    }

    /// <summary>The keys both sides derived in one handshake (the receiver keeps them for the sender's next message).</summary>
    public sealed record Keys(byte[] Encryption, byte[] Mac);

    private const byte JoinMarker = 0x20;

    /// <summary>The sender's side: its key, and the request to write.</summary>
    public sealed class Sender : IDisposable
    {
        private readonly ECDiffieHellman _key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        private readonly byte[] _senderHash;
        private readonly RequestFlags _flags;
        private Keys? _keys;

        /// <param name="senderHash">The sender's <see cref="NearbyBeacon.HashOf"/> (lets a receiver tell paired senders).</param>
        public Sender(byte[] senderHash, RequestFlags flags = RequestFlags.None)
        {
            _senderHash = senderHash;
            _flags = flags;
        }

        public byte[] Request()
        {
            var request = new byte[RequestLength];
            request[0] = Version;
            PublicPoint(_key).CopyTo(request, 1);
            _senderHash.AsSpan(0, 6).CopyTo(request.AsSpan(1 + PointLength));
            request[^1] = (byte)_flags;
            return request;
        }

        /// <summary>After a ready answer: asks the receiver to join this device's network (<paramref name="text"/>: this device's code).</summary>
        public byte[] JoinRequest(string text)
        {
            var keys = _keys ?? throw new InvalidOperationException("No answer yet.");
            return Seal(JoinMarker, Encoding.UTF8.GetBytes(text), keys);
        }

        /// <summary>Reads the receiver's answer. Throws <see cref="CryptographicException"/> when it was tampered with.</summary>
        public (Status Status, string? Text) Open(byte[] response)
        {
            if (response.Length < 2 || response[0] != Version) throw new CryptographicException("Not a Beam handshake answer.");
            var status = (Status)response[1];
            if (status is Status.Joined or Status.Accepted) return (status, Encoding.UTF8.GetString(response, 2, response.Length - 2));
            if (status != Status.Ready) return (status, response.Length > 2 ? Encoding.UTF8.GetString(response, 2, response.Length - 2) : null);
            if (response.Length < 2 + PointLength + IvLength + 16 + MacLength) throw new CryptographicException("Answer too short.");
            var theirs = response.AsSpan(2, PointLength).ToArray();
            var (encryption, mac) = Derive(_key, theirs, PublicPoint(_key), theirs);
            _keys = new Keys(encryption, mac);
            var macStart = response.Length - MacLength;
            using (var hmac = new HMACSHA256(mac))
            {
                var expected = hmac.ComputeHash(response, 0, macStart);
                if (!CryptographicOperations.FixedTimeEquals(expected, response.AsSpan(macStart))) throw new CryptographicException("Answer was altered.");
            }

            using var aes = Aes.Create();
            aes.Key = encryption;
            var iv = response.AsSpan(2 + PointLength, IvLength).ToArray();
            var cipher = response.AsSpan(2 + PointLength + IvLength, macStart - (2 + PointLength + IvLength));
            return (status, Encoding.UTF8.GetString(aes.DecryptCbc(cipher, iv)));
        }

        public void Dispose() => _key.Dispose();
    }

    /// <summary>The receiver's answer to <paramref name="request"/>: <paramref name="text"/> encrypted for that sender only.</summary>
    public static byte[] Answer(byte[] request, string text) => Answer(request, text, out _);

    /// <inheritdoc cref="Answer(byte[], string)"/>
    /// <param name="keys">The keys of this handshake, for the sender's join request that may follow.</param>
    public static byte[] Answer(byte[] request, string text, out Keys? keys)
    {
        keys = null;
        if (!IsRequest(request)) return Refuse("Not a Beam request.");
        using var key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var mine = PublicPoint(key);
        var theirs = request.AsSpan(1, PointLength).ToArray();
        var (encryption, macKey) = Derive(key, theirs, theirs, mine);
        keys = new Keys(encryption, macKey);
        using var aes = Aes.Create();
        aes.Key = encryption;
        var iv = RandomNumberGenerator.GetBytes(IvLength);
        var cipher = aes.EncryptCbc(Encoding.UTF8.GetBytes(text), iv);
        var answer = new byte[2 + PointLength + IvLength + cipher.Length + MacLength];
        answer[0] = Version;
        answer[1] = (byte)Status.Ready;
        mine.CopyTo(answer, 2);
        iv.CopyTo(answer, 2 + PointLength);
        cipher.CopyTo(answer, 2 + PointLength + IvLength);
        using var hmac = new HMACSHA256(macKey);
        hmac.ComputeHash(answer, 0, answer.Length - MacLength).CopyTo(answer, answer.Length - MacLength);
        return answer;
    }

    public static byte[] Working() => new[] { Version, (byte)Status.Working };

    /// <summary>The receiver joined the sender's network and is at <paramref name="address"/> there.</summary>
    public static byte[] Joined(string address) => Plain(Status.Joined, address);

    /// <summary>The receiver accepts the proposed sender-hosted session.</summary>
    public static byte[] Accepted() => Plain(Status.Accepted, "");

    private static byte[] Plain(Status status, string value)
    {
        var text = Encoding.UTF8.GetBytes(value);
        var answer = new byte[2 + text.Length];
        answer[0] = Version;
        answer[1] = (byte)status;
        text.CopyTo(answer, 2);
        return answer;
    }

    public static bool IsJoinRequest(byte[] data) => data.Length > 4 && data[0] == Version && data[1] == JoinMarker;

    /// <summary>The sender's code in a join request. Throws <see cref="CryptographicException"/> when it was altered.</summary>
    public static string OpenJoinRequest(byte[] data, Keys keys) => Encoding.UTF8.GetString(Unseal(JoinMarker, data, keys));

    /// <summary>
    /// How long the message that starts with <paramref name="partial"/> is (a GATT server collects a long write until it
    /// has all of it); null while that can't be told yet.
    /// </summary>
    public static int? ExpectedLength(ReadOnlySpan<byte> partial)
    {
        if (partial.Length < 2) return null;
        if (partial[1] == 4) return RequestLength;
        if (partial[1] != JoinMarker) return partial.Length; // unknown: take it as it is
        return partial.Length < 4 ? null : 4 + (partial[2] << 8 | partial[3]);
    }

    /// <summary>[version, marker, length (2 bytes), IV, AES-CBC ciphertext, HMAC over all before it].</summary>
    private static byte[] Seal(byte marker, byte[] plain, Keys keys)
    {
        using var aes = Aes.Create();
        aes.Key = keys.Encryption;
        var iv = RandomNumberGenerator.GetBytes(IvLength);
        var cipher = aes.EncryptCbc(plain, iv);
        var length = IvLength + cipher.Length + MacLength;
        if (length > 0xFFFF) throw new ArgumentException("Too long.");
        var message = new byte[4 + length];
        message[0] = Version;
        message[1] = marker;
        message[2] = (byte)(length >> 8);
        message[3] = (byte)length;
        iv.CopyTo(message, 4);
        cipher.CopyTo(message, 4 + IvLength);
        using var hmac = new HMACSHA256(keys.Mac);
        hmac.ComputeHash(message, 0, message.Length - MacLength).CopyTo(message, message.Length - MacLength);
        return message;
    }

    private static byte[] Unseal(byte marker, byte[] message, Keys keys)
    {
        if (message.Length < 4 + IvLength + 16 + MacLength || message[0] != Version || message[1] != marker
            || message.Length != 4 + (message[2] << 8 | message[3])) throw new CryptographicException("Not a Beam message.");
        var macStart = message.Length - MacLength;
        using (var hmac = new HMACSHA256(keys.Mac))
        {
            if (!CryptographicOperations.FixedTimeEquals(hmac.ComputeHash(message, 0, macStart), message.AsSpan(macStart)))
                throw new CryptographicException("Message was altered.");
        }

        using var aes = Aes.Create();
        aes.Key = keys.Encryption;
        return aes.DecryptCbc(message.AsSpan(4 + IvLength, macStart - 4 - IvLength), message.AsSpan(4, IvLength));
    }

    public static byte[] Refuse(string message)
    {
        var text = Encoding.UTF8.GetBytes(message.Length > 120 ? message[..120] : message);
        var answer = new byte[2 + text.Length];
        answer[0] = Version;
        answer[1] = (byte)Status.Refused;
        text.CopyTo(answer, 2);
        return answer;
    }

    /// <summary>A request's exact length (a GATT server knows a long write is complete when it has this many bytes).</summary>
    public const int RequestLength = 1 + PointLength + 6 + 1;

    public static bool IsRequest(byte[] data) => data.Length == RequestLength && data[0] == Version && data[1] == 4;

    public static RequestFlags FlagsOf(byte[] request) => (RequestFlags)request[^1];

    /// <summary>Who the sender says it is (its id hash; not proof, only a hint).</summary>
    public static byte[] SenderHash(byte[] request) => request.AsSpan(1 + PointLength, 6).ToArray();

    private static byte[] PublicPoint(ECDiffieHellman key)
    {
        var p = key.ExportParameters(false).Q;
        var point = new byte[PointLength];
        point[0] = 4;
        p.X!.CopyTo(point, 1);
        p.Y!.CopyTo(point, 33);
        return point;
    }

    private static (byte[] Encryption, byte[] Mac) Derive(ECDiffieHellman mine, byte[] theirPoint, byte[] senderPoint, byte[] receiverPoint)
    {
        using var theirs = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = theirPoint[1..33], Y = theirPoint[33..65] },
        });
        var secret = mine.DeriveKeyFromHash(theirs.PublicKey, HashAlgorithmName.SHA256);
        var salt = senderPoint.Concat(receiverPoint).ToArray();
        var keys = HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, 64, salt, Info);
        return (keys[..32], keys[32..]);
    }
}
