using Android.Content;
using Android.Database;
using Android.Provider;
using Beam.Core.Backup;
using Beam.Core.Diagnostics;
using Uri = Android.Net.Uri;

namespace Beam.Droid;

/// <summary>
/// The phone's photos and videos for backups, from Android's media library (camera, screenshots, downloaded
/// pictures…), each with its folder on the phone ("DCIM/Camera/IMG_1.jpg"). Folders the user chose are listed by
/// <see cref="ContentFiles"/>.
/// </summary>
internal sealed class AndroidBackupSources : IBackupSources
{
    private readonly ContentResolver _resolver;

    public AndroidBackupSources(ContentResolver resolver) => _resolver = resolver;

    public IReadOnlyList<BackupItem>? List(BackupSource source, CancellationToken cancellationToken) => source.Kind switch
    {
        BackupSourceKinds.Photos => Query(MediaStore.Images.Media.ExternalContentUri!, cancellationToken),
        BackupSourceKinds.Videos => Query(MediaStore.Video.Media.ExternalContentUri!, cancellationToken),
        _ => null,
    };

    private List<BackupItem> Query(Uri collection, CancellationToken cancellationToken)
    {
        var withFolders = OperatingSystem.IsAndroidVersionAtLeast(29);
        string[] columns = withFolders
            ? new[] { IBaseColumns.Id, IMediaColumns.DisplayName, IMediaColumns.Size, IMediaColumns.DateModified, IMediaColumns.RelativePath }
            : new[] { IBaseColumns.Id, IMediaColumns.DisplayName, IMediaColumns.Size, IMediaColumns.DateModified, IMediaColumns.Data };
        var items = new List<BackupItem>();
        // Files still being written (a video being recorded) are left for the next backup.
        var selection = withFolders ? IMediaColumns.IsPending + " = 0" : null;
        using ICursor? cursor = _resolver.Query(collection, columns, selection, null, null);
        if (cursor == null)
        {
            Log.Warn($"Backup: the media library returned nothing for {collection}");
            return items;
        }

        while (cursor.MoveToNext())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var id = cursor.GetLong(0);
            var name = cursor.IsNull(1) ? null : cursor.GetString(1);
            if (string.IsNullOrWhiteSpace(name)) continue;
            var size = cursor.IsNull(2) ? -1 : cursor.GetLong(2);
            if (size <= 0) continue; // not there (yet), or unreadable
            var modified = cursor.IsNull(3) ? DateTime.UtcNow : DateTimeOffset.FromUnixTimeSeconds(cursor.GetLong(3)).UtcDateTime;
            var folder = cursor.IsNull(4) ? "" : cursor.GetString(4) ?? "";
            if (!withFolders) folder = FolderOf(folder);
            var uri = ContentUris.WithAppendedId(collection, id)!;
            items.Add(new BackupItem(folder.Trim('/') is { Length: > 0 } f ? f + "/" + name : name, size, modified, uri.ToString()!,
                () => _resolver.OpenInputStream(uri) ?? throw new IOException("The file can no longer be opened.")));
        }

        return items;
    }

    /// <summary>"/storage/emulated/0/DCIM/Camera/IMG_1.jpg" → "DCIM/Camera" (Android 9 and older).</summary>
    private static string FolderOf(string path)
    {
        var directory = Path.GetDirectoryName(path) ?? "";
        var root = global::Android.OS.Environment.ExternalStorageDirectory?.AbsolutePath;
        return root != null && directory.StartsWith(root, StringComparison.Ordinal) ? directory[root.Length..] : Path.GetFileName(directory);
    }
}
