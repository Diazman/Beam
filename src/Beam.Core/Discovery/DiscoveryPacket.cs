using System.Text.Json;
using System.Text.Json.Serialization;

namespace Beam.Core.Discovery;

/// <summary>Small UDP datagram used to find nearby devices. Never carries file data.</summary>
internal sealed class DiscoveryPacket
{
    public const string MagicValue = "beam-discovery";
    public const int MaxSize = 2048;

    public const string TypeAnnounce = "announce";
    public const string TypeQuery = "query";
    public const string TypeBye = "bye";

    [JsonPropertyName("magic")]
    public string Magic { get; set; } = MagicValue;

    [JsonPropertyName("v")]
    public int Version { get; set; } = AppInfo.ProtocolVersion;

    [JsonPropertyName("type")]
    public string Type { get; set; } = TypeAnnounce;

    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("port")]
    public int Port { get; set; }

    [JsonPropertyName("fp")]
    public string Fingerprint { get; set; } = "";

    [JsonPropertyName("platform")]
    public string Platform { get; set; } = "";

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = DeviceKinds.Desktop;

    [JsonPropertyName("ver")]
    public string AppVersion { get; set; } = "";

    /// <summary>Direct Wi-Fi link support: "host", "join" or "host,join"; empty when unsupported.</summary>
    [JsonPropertyName("direct")]
    public string Direct { get; set; } = "";

    /// <summary>Set on queries from devices that are hidden: peers answer but do not list them.</summary>
    [JsonPropertyName("hidden")]
    public bool Hidden { get; set; }

    public byte[] Serialize() => JsonSerializer.SerializeToUtf8Bytes(this, DiscoveryJson.Default.DiscoveryPacket);

    public static DiscoveryPacket? TryParse(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0 || data.Length > MaxSize) return null;
        try
        {
            var packet = JsonSerializer.Deserialize(data, DiscoveryJson.Default.DiscoveryPacket);
            if (packet == null || packet.Magic != MagicValue) return null;
            if (string.IsNullOrWhiteSpace(packet.Id) || packet.Id.Length > 64) return null;
            if (packet.Type != TypeAnnounce && packet.Type != TypeQuery && packet.Type != TypeBye) return null;
            if (packet.Type != TypeBye && !packet.Hidden)
            {
                if (packet.Port is <= 0 or > 65535) return null;
                if (string.IsNullOrWhiteSpace(packet.Fingerprint) || packet.Fingerprint.Length > 128) return null;
            }

            packet.Name = Settings.AppSettings.NormalizeDeviceName(packet.Name);
            packet.Platform = Truncate(packet.Platform, 16);
            packet.Kind = Truncate(packet.Kind, 16);
            packet.AppVersion = Truncate(packet.AppVersion, 32);
            packet.Direct = Truncate(packet.Direct, 16);
            return packet;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Truncate(string? value, int max) =>
        string.IsNullOrEmpty(value) ? "" : value.Length <= max ? value : value[..max];
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault)]
[JsonSerializable(typeof(DiscoveryPacket))]
internal sealed partial class DiscoveryJson : JsonSerializerContext
{
}
