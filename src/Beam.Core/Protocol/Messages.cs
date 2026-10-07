using System.Text.Json.Serialization;

namespace Beam.Core.Protocol;

/// <summary>
/// Every frame on a transfer connection is <c>[type:1][length:4, big-endian][payload]</c>.
/// Control frames carry UTF-8 JSON; <see cref="FileData"/> frames carry raw file bytes.
/// The whole connection runs inside mutually-authenticated TLS.
/// </summary>
public enum FrameType : byte
{
    Hello = 1,
    HelloReply = 2,
    Offer = 3,
    OfferResponse = 4,
    FileHeader = 5,
    FileData = 6,
    FileFooter = 7,
    FileResult = 8,
    SourceFileError = 9,
    Done = 10,
    Result = 11,
    Cancel = 12,
}

public static class ConnectionPurpose
{
    public const string Transfer = "transfer";
    public const string Probe = "probe";
}

/// <summary>Reasons sent in <see cref="OfferResponseMessage"/> and <see cref="CancelMessage"/>.</summary>
public static class Reasons
{
    public const string Declined = "declined";
    public const string TimedOut = "timeout";
    public const string Busy = "busy";
    public const string NoSpace = "no_space";
    public const string DestinationUnavailable = "destination_unavailable";
    public const string WriteFailed = "write_failed";
    public const string PermissionDenied = "permission_denied";
    public const string Cancelled = "cancelled";
    public const string Protocol = "protocol";
    public const string Shutdown = "shutdown";
}

public sealed class HelloMessage
{
    public int Protocol { get; set; } = AppInfo.ProtocolVersion;

    public int MinProtocol { get; set; } = AppInfo.MinimumProtocolVersion;

    public string DeviceId { get; set; } = "";

    public string DeviceName { get; set; } = "";

    public string Platform { get; set; } = "";

    public string Kind { get; set; } = "";

    public string AppVersion { get; set; } = "";

    public string Purpose { get; set; } = ConnectionPurpose.Transfer;

    /// <summary>TCP port the sender itself listens on, so the receiver can show/offer it.</summary>
    public int Port { get; set; }
}

public sealed class OfferEntry
{
    /// <summary>Relative path using '/' separators, e.g. "Photos/2026/January/a.jpg".</summary>
    [JsonPropertyName("p")]
    public string Path { get; set; } = "";

    [JsonPropertyName("s")]
    public long Size { get; set; }

    [JsonPropertyName("d")]
    public bool IsDirectory { get; set; }

    /// <summary>Last write time, Unix milliseconds (UTC). 0 when unknown.</summary>
    [JsonPropertyName("m")]
    public long Modified { get; set; }
}

public sealed class OfferMessage
{
    public string TransferId { get; set; } = "";

    public List<OfferEntry> Entries { get; set; } = new();

    /// <summary>True when the sender is reconnecting to continue a transfer that was already accepted.</summary>
    public bool Resume { get; set; }
}

public sealed class PlanItem
{
    [JsonPropertyName("i")]
    public int Index { get; set; }

    /// <summary>Bytes the receiver already has; the sender continues from here.</summary>
    [JsonPropertyName("o")]
    public long Offset { get; set; }

    /// <summary>Already received and verified in an earlier attempt.</summary>
    [JsonPropertyName("done")]
    public bool Done { get; set; }
}

public sealed class OfferResponseMessage
{
    public bool Accepted { get; set; }

    public string? Reason { get; set; }

    /// <summary>Files the receiver wants. Entries not listed (directories, skipped conflicts) are not sent.</summary>
    public List<PlanItem> Plan { get; set; } = new();
}

public sealed class FileHeaderMessage
{
    public int Index { get; set; }

    public long Offset { get; set; }
}

public sealed class FileFooterMessage
{
    public int Index { get; set; }

    /// <summary>Total length of the file as sent (from byte 0).</summary>
    public long Length { get; set; }

    /// <summary>Lower-case hex SHA-256 of the whole file.</summary>
    public string Sha256 { get; set; } = "";
}

public sealed class FileResultMessage
{
    public int Index { get; set; }

    public bool Ok { get; set; }

    /// <summary>The file arrived damaged; the sender should send it again from the start.</summary>
    public bool Retry { get; set; }

    public string? Error { get; set; }
}

public sealed class SourceFileErrorMessage
{
    public int Index { get; set; }

    public string Error { get; set; } = "";
}

public sealed class DoneMessage
{
}

public sealed class ResultMessage
{
    public int Completed { get; set; }

    public int Failed { get; set; }

    public int Skipped { get; set; }
}

public sealed class CancelMessage
{
    public string Reason { get; set; } = Reasons.Cancelled;

    public string? Detail { get; set; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(HelloMessage))]
[JsonSerializable(typeof(OfferMessage))]
[JsonSerializable(typeof(OfferResponseMessage))]
[JsonSerializable(typeof(FileHeaderMessage))]
[JsonSerializable(typeof(FileFooterMessage))]
[JsonSerializable(typeof(FileResultMessage))]
[JsonSerializable(typeof(SourceFileErrorMessage))]
[JsonSerializable(typeof(DoneMessage))]
[JsonSerializable(typeof(ResultMessage))]
[JsonSerializable(typeof(CancelMessage))]
internal sealed partial class ProtocolJson : JsonSerializerContext
{
}
