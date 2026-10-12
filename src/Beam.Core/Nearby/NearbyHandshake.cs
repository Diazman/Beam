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
    }

    /// <summary>The sender's side: its key, and the request to write.</summary>
    public sealed class Sender : IDisposable
    {
        private readonly ECDiffieHellman _key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        private readonly byte[] _senderHash;

        /// <param name="senderHash">The sender's <see cref="NearbyBeacon.HashOf"/> (lets a receiver tell paired senders).</param>
        public Sender(byte[] senderHash) => _senderHash = senderHash;

        public byte[] Request()
        {
            var request = new byte[RequestLength];
            request[0] = Version;
            PublicPoint(_key).CopyTo(request, 1);
            _senderHash.AsSpan(0, 6).CopyTo(request.AsSpan(1 + PointLength));
            return request;
        }

        /// <summary>Reads the receiver's answer. Throws <see cref="CryptographicException"/> when it was tampered with.</summary>
        public (Status Status, string? Text) Open(byte[] response)
        {
            if (response.Length < 2 || response[0] != Version) throw new CryptographicException("Not a Beam handshake answer.");
            var status = (Status)response[1];
            if (status != Status.Ready) return (status, response.Length > 2 ? Encoding.UTF8.GetString(response, 2, response.Length - 2) : null);
            if (response.Length < 2 + PointLength + IvLength + 16 + MacLength) throw new CryptographicException("Answer too short.");
            var theirs = response.AsSpan(2, PointLength).ToArray();
            var (encryption, mac) = Keys(_key, theirs, PublicPoint(_key), theirs);
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
    public static byte[] Answer(byte[] request, string text)
    {
        if (!IsRequest(request)) return Refuse("Not a Beam request.");
        using var key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var mine = PublicPoint(key);
        var theirs = request.AsSpan(1, PointLength).ToArray();
        var (encryption, macKey) = Keys(key, theirs, theirs, mine);
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
    public const int RequestLength = 1 + PointLength + 6;

    public static bool IsRequest(byte[] data) => data.Length == RequestLength && data[0] == Version && data[1] == 4;

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

    private static (byte[] Encryption, byte[] Mac) Keys(ECDiffieHellman mine, byte[] theirPoint, byte[] senderPoint, byte[] receiverPoint)
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
