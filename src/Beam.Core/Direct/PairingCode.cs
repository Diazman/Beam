using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Beam.Core.Direct;

/// <summary>
/// What a PC's QR code tells the Beam app on a phone, so the two can find each other from then on: who the PC is
/// (id, name, certificate fingerprint for the encrypted connection), where it is on the network, and the name and
/// passphrase of its own direct network. The same code also works with a phone's camera: it is the Phone page's
/// link with this information after "#beam=" (a URL fragment, which browsers never send anywhere).
/// </summary>
public sealed class PairingCode
{
    private const string Marker = "#beam=";

    [JsonPropertyName("i")]
    public string DeviceId { get; set; } = "";

    [JsonPropertyName("n")]
    public string Name { get; set; } = "";

    [JsonPropertyName("f")]
    public string Fingerprint { get; set; } = "";

    [JsonPropertyName("k")]
    public string Kind { get; set; } = "";

    /// <summary>"address" or "address:port" of the PC's Beam on its networks.</summary>
    [JsonPropertyName("a")]
    public List<string> Addresses { get; set; } = new();

    [JsonPropertyName("s")]
    public string? Ssid { get; set; }

    [JsonPropertyName("p")]
    public string? Passphrase { get; set; }

    /// <summary>Appends the pairing information to a link (the Phone page's), or returns it as a "beam:" text.</summary>
    public string AppendTo(string? link)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(this, PairingJson.Default.PairingCode);
        var payload = Convert.ToBase64String(json).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return string.IsNullOrEmpty(link) ? "beam:" + payload : link + Marker + payload;
    }

    /// <summary>Reads a scanned code; null when it isn't a Beam pairing code (or is damaged).</summary>
    public static PairingCode? TryParse(string? scanned)
    {
        if (string.IsNullOrWhiteSpace(scanned)) return null;
        var text = scanned.Trim();
        var at = text.IndexOf(Marker, StringComparison.Ordinal);
        string payload;
        if (at >= 0) payload = text[(at + Marker.Length)..];
        else if (text.StartsWith("beam:", StringComparison.Ordinal)) payload = text[5..];
        else return null;

        try
        {
            payload = payload.Replace('-', '+').Replace('_', '/');
            payload += new string('=', (4 - payload.Length % 4) % 4);
            var code = JsonSerializer.Deserialize(Convert.FromBase64String(payload), PairingJson.Default.PairingCode);
            if (code == null || string.IsNullOrWhiteSpace(code.DeviceId) || code.DeviceId.Length > 64) return null;
            if (string.IsNullOrWhiteSpace(code.Fingerprint) || code.Fingerprint.Length > 128) return null;
            code.Name = Settings.AppSettings.NormalizeDeviceName(code.Name);
            code.Fingerprint = code.Fingerprint.ToLowerInvariant();
            code.Addresses = code.Addresses.Where(a => TryParseEndPoint(a, out _)).Take(8).ToList();
            if (string.IsNullOrEmpty(code.Ssid) || string.IsNullOrEmpty(code.Passphrase) || code.Ssid.Length > 32 || code.Passphrase.Length is < 8 or > 63)
            {
                code.Ssid = null;
                code.Passphrase = null;
            }

            return code;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return null;
        }
    }

    public DirectNetwork? Network => Ssid != null && Passphrase != null ? new DirectNetwork(Ssid, Passphrase) : null;

    /// <summary>The PC as a device this phone can send to (addresses with Beam's port).</summary>
    public Discovery.DeviceInfo ToDevice() => new()
    {
        Id = DeviceId,
        Name = Name,
        Fingerprint = Fingerprint,
        Kind = string.IsNullOrEmpty(Kind) ? Discovery.DeviceKinds.Desktop : Kind,
        DirectRoles = Network != null ? DirectRoles.Host : DirectRoles.None,
        Endpoints = Addresses.Select(a => TryParseEndPoint(a, out var e) ? e : null).OfType<IPEndPoint>().ToList(),
    };

    private static bool TryParseEndPoint(string text, out IPEndPoint? endpoint)
    {
        endpoint = null;
        var parts = text.Split(':');
        if (parts.Length is < 1 or > 2 || !IPAddress.TryParse(parts[0], out var address)) return false;
        var port = AppInfo.TransferPort;
        if (parts.Length == 2 && (!int.TryParse(parts[1], out port) || port is <= 0 or > 65535)) return false;
        endpoint = new IPEndPoint(address, port);
        return true;
    }
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(PairingCode))]
internal sealed partial class PairingJson : JsonSerializerContext
{
}
