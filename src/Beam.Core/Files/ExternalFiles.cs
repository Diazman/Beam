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

    /// <summary>A folder address (Android's content://…/tree/…), with the folder's name. Quick: doesn't list it.</summary>
    bool TryGetFolderName(string address, [NotNullWhen(true)] out string? name)
    {
        name = null;
        return false;
    }

    /// <summary>Everything in a folder address, with paths relative to the folder ("Sub/a.jpg"). May take a while.</summary>
    IReadOnlyList<ExternalFolderItem> ListFolder(string address, CancellationToken cancellationToken) => Array.Empty<ExternalFolderItem>();
}

/// <summary>A file (or, when <see cref="File"/> is null, a subfolder) inside an external folder.</summary>
public sealed record ExternalFolderItem(string RelativePath, ExternalFile? File, DateTime ModifiedUtc);
