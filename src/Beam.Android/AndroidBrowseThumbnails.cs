using Android.Graphics;
using Android.Media;
using Beam.Core.Browse;
using AndroidEnvironment = Android.OS.Environment;

namespace Beam.Droid;

/// <summary>Previews of photos and videos in shared folders, for trusted devices browsing this phone.</summary>
internal sealed class AndroidBrowseThumbnails : IBrowseThumbnails
{
    private static readonly HashSet<string> Videos = new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".mov", ".m4v", ".3gp", ".mkv", ".webm" };

    private static readonly HashSet<string> Pictures = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".heic", ".heif", ".bmp",
    };

    public byte[]? Create(string path, int max)
    {
        var extension = System.IO.Path.GetExtension(path);
        var isVideo = Videos.Contains(extension);
        if (!isVideo && !Pictures.Contains(extension)) return null;
        using var bitmap = Load(path, max, isVideo);
        if (bitmap == null) return null;
        // Small enough to send in one message: lower the quality until it fits.
        foreach (var quality in new[] { 75, 55, 35 })
        {
            using var stream = new MemoryStream();
            bitmap.Compress(Bitmap.CompressFormat.Jpeg!, quality, stream);
            if (stream.Length < 46 * 1024) return stream.ToArray();
        }

        return null;
    }

    private static Bitmap? Load(string path, int max, bool isVideo)
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(29))
        {
            var file = new Java.IO.File(path);
            var size = new global::Android.Util.Size(max, max);
            return isVideo ? ThumbnailUtils.CreateVideoThumbnail(file, size, null) : ThumbnailUtils.CreateImageThumbnail(file, size, null);
        }

        if (isVideo) return null;
        // Older Android: decode a smaller copy of the picture.
        var bounds = new BitmapFactory.Options { InJustDecodeBounds = true };
        BitmapFactory.DecodeFile(path, bounds);
        var sample = 1;
        while (Math.Max(bounds.OutWidth, bounds.OutHeight) / (sample * 2) >= max) sample *= 2;
        return BitmapFactory.DecodeFile(path, new BitmapFactory.Options { InSampleSize = sample });
    }

    /// <summary>The usual folders of a phone, shared when browsing is first switched on (the user changes them in Settings).</summary>
    public static IReadOnlyList<(string Name, string Path)> DefaultFolders()
    {
        var list = new List<(string, string)>();
        void Add(string name, string? type)
        {
            var folder = type == null ? null : AndroidEnvironment.GetExternalStoragePublicDirectory(type)?.AbsolutePath;
            if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder)) list.Add((name, folder));
        }

        Add("Camera", AndroidEnvironment.DirectoryDcim);
        Add("Pictures", AndroidEnvironment.DirectoryPictures);
        Add("Movies", AndroidEnvironment.DirectoryMovies);
        Add("Music", AndroidEnvironment.DirectoryMusic);
        Add("Download", AndroidEnvironment.DirectoryDownloads);
        Add("Documents", AndroidEnvironment.DirectoryDocuments);
        return list;
    }
}
