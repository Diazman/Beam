using System.Text.Json.Serialization;

namespace Beam.Core.Phone;

public sealed class PhoneInfo
{
    public string Device { get; set; } = "";

    public string Version { get; set; } = "";
}

public sealed class PhoneFileInfo
{
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public long Size { get; set; }
}

public sealed class PhoneFileList
{
    public List<PhoneFileInfo> Files { get; set; } = new();
}

public sealed class PhoneOfferFile
{
    public string Name { get; set; } = "";

    public long Size { get; set; }
}

public sealed class PhoneOffer
{
    public List<PhoneOfferFile> Files { get; set; } = new();
}

public sealed class PhoneReply
{
    public bool Ok { get; set; }

    public string? OfferId { get; set; }

    public string? Message { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(PhoneInfo))]
[JsonSerializable(typeof(PhoneFileList))]
[JsonSerializable(typeof(PhoneOffer))]
[JsonSerializable(typeof(PhoneReply))]
internal sealed partial class PhoneJson : JsonSerializerContext
{
}
