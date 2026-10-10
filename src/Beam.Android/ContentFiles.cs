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

    public bool TryGetFolderName(string address, [NotNullWhen(true)] out string? name)
    {
        name = null;
        if (!address.StartsWith("content://", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            var tree = Uri.Parse(address)!;
            if (!DocumentsContract.IsTreeUri(tree)) return false;
            var id = DocumentsContract.GetTreeDocumentId(tree);
            var document = DocumentsContract.BuildDocumentUriUsingTree(tree, id)!;
            using ICursor? cursor = _resolver.Query(document, new[] { IOpenableColumns.DisplayName }, null, null, null);
            if (cursor != null && cursor.MoveToFirst() && !cursor.IsNull(0)) name = cursor.GetString(0);
            // "primary:DCIM/Camera" → "Camera" when the provider doesn't say.
            if (string.IsNullOrWhiteSpace(name)) name = id?.Split(':', '/').LastOrDefault(p => p.Length > 0) ?? "Folder";
            return true;
        }
        catch (Exception ex)
        {
            Core.Diagnostics.Log.Warn($"Could not open the folder {address}", ex);
            return false;
        }
    }

    public IReadOnlyList<ExternalFolderItem> ListFolder(string address, CancellationToken cancellationToken)
    {
        var tree = Uri.Parse(address)!;
        var items = new List<ExternalFolderItem>();
        var pending = new Stack<(string Id, string Relative)>();
        pending.Push((DocumentsContract.GetTreeDocumentId(tree)!, ""));
        string[] columns =
        {
            DocumentsContract.Document.ColumnDocumentId, DocumentsContract.Document.ColumnDisplayName, DocumentsContract.Document.ColumnMimeType,
            DocumentsContract.Document.ColumnSize, DocumentsContract.Document.ColumnLastModified,
        };
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (parent, relative) = pending.Pop();
            var children = DocumentsContract.BuildChildDocumentsUriUsingTree(tree, parent)!;
            var found = new List<(string Id, string Name, bool IsFolder, long Size, DateTime Modified)>();
            try
            {
                using ICursor? cursor = _resolver.Query(children, columns, null, null, null);
                while (cursor != null && cursor.MoveToNext())
                {
                    var id = cursor.GetString(0);
                    var name = cursor.GetString(1);
                    if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name)) continue;
                    var modified = cursor.IsNull(4) ? DateTime.UtcNow : DateTimeOffset.FromUnixTimeMilliseconds(cursor.GetLong(4)).UtcDateTime;
                    found.Add((id, name, cursor.GetString(2) == DocumentsContract.Document.MimeTypeDir, cursor.IsNull(3) ? 0 : cursor.GetLong(3), modified));
                }
            }
            catch (Exception ex)
            {
                Core.Diagnostics.Log.Warn($"Could not list {relative} in {address}", ex);
                continue;
            }

            found.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
            var folders = new List<(string, string)>();
            foreach (var child in found)
            {
                var path = relative.Length == 0 ? child.Name : relative + "/" + child.Name;
                if (child.IsFolder)
                {
                    items.Add(new ExternalFolderItem(path, null, child.Modified));
                    folders.Add((child.Id, path));
                    continue;
                }

                var document = DocumentsContract.BuildDocumentUriUsingTree(tree, child.Id)!;
                items.Add(new ExternalFolderItem(path, new ExternalFile(child.Name, child.Size, child.Modified,
                    () => _resolver.OpenInputStream(document) ?? throw new IOException("The file can no longer be opened.")), child.Modified));
            }

            for (var i = folders.Count - 1; i >= 0; i--) pending.Push(folders[i]);
        }

        return items;
    }
}
