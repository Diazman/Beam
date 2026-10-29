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

    /// <summary>Protocol 2: a piece of text or a link (connection purpose <see cref="ConnectionPurpose.Text"/>).</summary>
    Text = 13,

    /// <summary>Setting up a direct Wi-Fi link (connection purpose <see cref="ConnectionPurpose.Direct"/>).</summary>
    DirectRequest = 14,

    DirectResponse = 15,

    /// <summary>Setting up a backup (connection purpose <see cref="ConnectionPurpose.Backup"/>), answered with <see cref="BackupResponse"/>.</summary>
    BackupRequest = 16,

    BackupResponse = 17,
}

public static class ConnectionPurpose
{
    public const string Transfer = "transfer";
    public const string Probe = "probe";

    /// <summary>Protocol 2: the connection carries one <see cref="TextMessage"/>, answered with Result or Cancel.</summary>
    public const string Text = "text";

    /// <summary>One <see cref="DirectRequestMessage"/>, answered with a <see cref="DirectResponseMessage"/>. Only sent to devices that announce direct support.</summary>
    public const string Direct = "direct";

    /// <summary>One <see cref="BackupRequestMessage"/> (backup setup), answered with a <see cref="BackupResponseMessage"/>.</summary>
    public const string Backup = "backup";
}

public static class DirectActions
{
    // "host", "join", "prefer" and "stop" were Beam 1.5.3 and older's (receiver-hosted) requests: refused now.

    // Sender-hosted sessions (one per receiver, identified by SessionId; later messages carry its Nonce):

    /// <summary>Sender → receiver (control channel): "will you take a direct session from me?" Nothing is started yet.</summary>
    public const string Propose = "propose";

    /// <summary>Sender → receiver (control channel): this session's network name and passphrase; join it.</summary>
    public const string Offer = "offer";

    /// <summary>Receiver → sender, over the direct network: "I joined; my Beam is at this port."</summary>
    public const string Ready = "ready";

    /// <summary>Sender → receiver, over the direct network: confirms both ends of the route before any file data.</summary>
    public const string Verify = "verify";

    /// <summary>Either side: the session is over (done, cancelled or failed); leave or stop what was set up for it.</summary>
    public const string End = "end";

    /// <summary>Receiver → sender (control channel): joining failed at <see cref="DirectRequestMessage.Stage"/>.</summary>
    public const string Failed = "failed";

    /// <summary>
    /// Sender that can't start a network (an iPhone) → receiver (a computer announcing
    /// <see cref="Direct.DirectRoles.HostsForSenders"/>), over Beam's TLS connection after it accepted the proposal:
    /// "start your network for this session and tell me how to join it". Answered with its name, passphrase and Beam port; the sender joins it and verifies
    /// the receiver there as usual (<see cref="Verify"/>).
    /// </summary>
    public const string HostForSender = "hostforsender";
}

public sealed class DirectRequestMessage
{
    public string Action { get; set; } = "";

    public string? Ssid { get; set; }

    public string? Passphrase { get; set; }

    /// <summary>Sender-hosted sessions: which session (128 random bits, hex).</summary>
    public string? SessionId { get; set; }

    /// <summary>Sender-hosted sessions: proves a message belongs to the current setup of the session.</summary>
    public string? Nonce { get; set; }

    /// <summary>Propose/Offer: the device this is meant for (a receiver refuses one meant for another device).</summary>
    public string? ReceiverId { get; set; }

    /// <summary>Over Bluetooth (no TLS): who the sender says it is; checked later on the direct network's TLS connection.</summary>
    public string? SenderId { get; set; }

    public string? SenderName { get; set; }

    public string? SenderFingerprint { get; set; }

    /// <summary>Offer: how long the details are good for, in seconds (relative, so the devices' clocks don't matter).</summary>
    public int TtlSeconds { get; set; }

    /// <summary>Offer: the sender is a computer (phones join its network as ordinary Wi-Fi).</summary>
    public bool HostIsComputer { get; set; }

    /// <summary>Offer: the sender's Beam port; Ready: the receiver's Beam port.</summary>
    public int Port { get; set; }

    /// <summary>
    /// Offer: the sender's address on its network, for a receiver that can't read the network's gateway (iOS). Not
    /// trusted for anything else: the receiver reports in there over TLS, checked against the sender's certificate.
    /// </summary>
    public string? HostAddress { get; set; }

    /// <summary>Failed: where it failed (e.g. "join").</summary>
    public string? Stage { get; set; }

    /// <summary>Failed/End: why, for the other device's user.</summary>
    public string? Message { get; set; }

    /// <summary>
    /// Propose/Offer setting a lost link up again: made with the key the receiver gave on the last verified session
    /// (<see cref="Direct.ReconnectProof"/>), so the receiver needn't ask its user again.
    /// </summary>
    public string? Proof { get; set; }
}

public sealed class DirectResponseMessage
{
    public bool Ok { get; set; }

    public string? Ssid { get; set; }

    public string? Passphrase { get; set; }

    /// <summary>Why it didn't work (shown to the user on the other device).</summary>
    public string? Message { get; set; }

    /// <summary>The user of the device that was asked said no.</summary>
    public bool Declined { get; set; }

    /// <summary>For a join: the joined device's own address on the direct network, so the host can reach it right away.</summary>
    public string? Address { get; set; }

    /// <summary>Verify: the receiver's key for setting a lost link up again (<see cref="Direct.ReconnectProof"/>); only over TLS.</summary>
    public string? ReconnectKey { get; set; }

    /// <summary>HostForSender: the receiver's Beam port on its network.</summary>
    public int Port { get; set; }

    /// <summary>HostForSender: the network shares the receiver's internet (a phone stays on it).</summary>
    public bool SharesInternet { get; set; }
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

    /// <summary>A backup from a device that isn't (or no longer is) set up to back up here.</summary>
    public const string NotBackupPartner = "not_backup_partner";
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

public static class BackupActions
{
    /// <summary>"Let me back up to you": carries the one-time token from the computer's setup code.</summary>
    public const string Setup = "setup";
}

public sealed class BackupRequestMessage
{
    public string Action { get; set; } = "";

    /// <summary>The one-time setup token shown in the computer's QR code (only someone who saw its screen has it).</summary>
    public string? Token { get; set; }
}

public sealed class BackupResponseMessage
{
    public bool Ok { get; set; }

    /// <summary>Why not (for the user).</summary>
    public string? Message { get; set; }

    /// <summary>The computer's name, as its user calls it.</summary>
    public string? Name { get; set; }
}

public sealed class TextMessage
{
    public string TransferId { get; set; } = "";

    public string Text { get; set; } = "";
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

    /// <summary>
    /// A backup: everything the device backs up. The receiver takes it only from a device set up to back up there
    /// (by its certificate), into that device's backup folder, and asks only for the files it doesn't have yet.
    /// </summary>
    public bool Backup { get; set; }
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
[JsonSerializable(typeof(TextMessage))]
[JsonSerializable(typeof(DirectRequestMessage))]
[JsonSerializable(typeof(DirectResponseMessage))]
[JsonSerializable(typeof(BackupRequestMessage))]
[JsonSerializable(typeof(BackupResponseMessage))]
internal sealed partial class ProtocolJson : JsonSerializerContext
{
}
