using System.Diagnostics.CodeAnalysis;

namespace Beam.Core.Files;

/// <summary>A file the platform hands over as a stream rather than a path (Android content:// URIs, iOS security-scoped URLs).</summary>
public sealed record ExternalFile(string Name, long Size, DateTime ModifiedUtc, Func<Stream> OpenRead);

/// <summary>
/// Resolves addresses such as <c>content://…</c> to files Beam can send. The send list and the transfer engine
/// pass these addresses around like paths; only this provider knows how to open them. Desktop hosts have none.
/// </summary>
public interface IExternalFiles
{
    bool TryGet(string address, [NotNullWhen(true)] out ExternalFile? file);
}
