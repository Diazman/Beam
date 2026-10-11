using System.Text.Json.Serialization;
using Beam.Core.History;
using Beam.Core.Identity;
using Beam.Core.Settings;
using Beam.Core.Transfer;

namespace Beam.Core.Storage;

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(List<HistoryEntry>))]
[JsonSerializable(typeof(StoredIdentity))]
[JsonSerializable(typeof(ResumeRecord))]
internal sealed partial class StorageJson : JsonSerializerContext
{
}
