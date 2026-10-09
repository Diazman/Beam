using System.Diagnostics.CodeAnalysis;
using Android.Content;
using Android.Database;
using Android.Provider;
using Beam.Core.Files;
using Uri = Android.Net.Uri;

namespace Beam.Droid;

/// <summary>Opens content:// addresses from the file picker or the Share sheet so Beam can send them.</summary>
internal sealed class ContentFiles : IExternalFiles
{
    private readonly ContentResolver _resolver;

    public ContentFiles(ContentResolver resolver) => _resolver = resolver;

    public bool TryGet(string address, [NotNullWhen(true)] out ExternalFile? file)
    {
        file = null;
        if (!address.StartsWith("content://", StringComparison.OrdinalIgnoreCase)) return false;
        var uri = Uri.Parse(address)!;
        string? name = null;
        long size = -1;
        var modified = DateTime.UtcNow;
        try
        {
            using ICursor? cursor = _resolver.Query(uri, null, null, null, null);
            if (cursor != null && cursor.MoveToFirst())
            {
                var nameColumn = cursor.GetColumnIndex(IOpenableColumns.DisplayName);
                var sizeColumn = cursor.GetColumnIndex(IOpenableColumns.Size);
                var modifiedColumn = cursor.GetColumnIndex(DocumentsContract.Document.ColumnLastModified);
                if (nameColumn >= 0 && !cursor.IsNull(nameColumn)) name = cursor.GetString(nameColumn);
                if (sizeColumn >= 0 && !cursor.IsNull(sizeColumn)) size = cursor.GetLong(sizeColumn);
                if (modifiedColumn >= 0 && !cursor.IsNull(modifiedColumn))
                    modified = DateTimeOffset.FromUnixTimeMilliseconds(cursor.GetLong(modifiedColumn)).UtcDateTime;
            }
        }
        catch (Exception ex)
        {
            Core.Diagnostics.Log.Warn($"Could not read details of {address}", ex);
            return false;
        }

        if (size < 0)
        {
            // Some providers don't report a size: ask the file descriptor.
            try
            {
                using var descriptor = _resolver.OpenAssetFileDescriptor(uri, "r");
                size = descriptor?.Length ?? -1;
            }
            catch
            {
                // unreadable: left out below
            }
        }

        if (size < 0) return false;
        file = new ExternalFile(string.IsNullOrWhiteSpace(name) ? "file" : name, size, modified,
            () => _resolver.OpenInputStream(uri) ?? throw new IOException("The file can no longer be opened."));
        return true;
    }
}
