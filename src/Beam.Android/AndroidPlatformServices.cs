using Android.App;
using Android.Content;
using Android.Media;
using Android.OS;
using Android.Provider;
using Beam.Core.Localization;
using Beam.App.Platform;
using Beam.Core.Diagnostics;
using AndroidEnvironment = Android.OS.Environment;
using AndroidUri = Android.Net.Uri;

namespace Beam.Droid;

/// <summary>Android integration: opening received files, links and notifications.</summary>
internal sealed class AndroidPlatformServices : IPlatformServices
{
    private const string ChannelId = "transfers";
    private static int _notificationId = 100;

    private static Context Context => global::Android.App.Application.Context;

    public bool IsPhone => true;

    public bool SupportsStartWithSystem => false;

    public bool GetStartWithSystem() => false;

    public void SetStartWithSystem(bool enabled)
    {
    }

    /// <summary>Shows the Downloads screen (where Downloads/Beam is).</summary>
    public void OpenFolder(string path) => Start(new Intent(DownloadManager.ActionViewDownloads));

    public void RevealInFolder(string path) => OpenFolder(path);

    public void OpenFile(string path)
    {
        if (Directory.Exists(path))
        {
            OpenFolder(path);
            return;
        }

        // The media scanner hands back a content:// address other apps are allowed to open.
        MediaScannerConnection.ScanFile(Context, new[] { path }, null, new ScanCallback(uri =>
        {
            if (uri == null) return;
            var intent = new Intent(Intent.ActionView);
            intent.SetDataAndType(uri, Context.ContentResolver!.GetType(uri));
            intent.AddFlags(ActivityFlags.GrantReadUriPermission);
            Start(Intent.CreateChooser(intent, (string?)null)!);
        }));
    }

    public void OpenUrl(string url) => Start(new Intent(Intent.ActionView, AndroidUri.Parse(url)));

    public void ShowNotification(string title, string message, Action? onActivated = null)
    {
        try
        {
            var manager = (NotificationManager?)Context.GetSystemService(Context.NotificationService);
            if (manager == null) return;
            if (Build.VERSION.SdkInt >= BuildVersionCodes.O && manager.GetNotificationChannel(ChannelId) == null)
                manager.CreateNotificationChannel(new NotificationChannel(ChannelId, "Transfers", NotificationImportance.High));

            var open = Context.PackageManager!.GetLaunchIntentForPackage(Context.PackageName!)!;
            open.SetFlags(ActivityFlags.SingleTop | ActivityFlags.ReorderToFront);
            var pending = PendingIntent.GetActivity(Context, 0, open, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
#pragma warning disable CA1422 // Notification.Builder(Context) is only for Android 7 and older
            var builder = Build.VERSION.SdkInt >= BuildVersionCodes.O ? new Notification.Builder(Context, ChannelId) : new Notification.Builder(Context);
#pragma warning restore CA1422
            builder.SetContentTitle(title)
                .SetContentText(message)
                .SetStyle(new Notification.BigTextStyle().BigText(message))
                .SetSmallIcon(global::Android.Resource.Drawable.StatSysDownloadDone)
                .SetContentIntent(pending)
                .SetAutoCancel(true);
            manager.Notify(Interlocked.Increment(ref _notificationId), builder.Build());
        }
        catch (Exception ex)
        {
            Log.Warn("Could not show a notification", ex);
        }
    }

    public string? DefaultReceiveFolder => AndroidHost.ReceiveFolder;

    /// <summary>
    /// The folder picker returns a content:// address ("…/tree/primary:Download/Projects"). Beam saves with normal
    /// file access, which Android allows in the shared Download and Documents folders (and their subfolders).
    /// </summary>
    public ReceiveFolderChoice CheckReceiveFolder(string picked)
    {
        var path = picked.StartsWith('/') ? picked : PathFromTreeUri(picked);
        var problem = L.T("Beam can't save files in that folder. Choose Download or Documents on the phone, or a folder inside them.");
        if (path == null) return new ReceiveFolderChoice(null, problem);
        path = path.TrimEnd('/');
        var storageRoot = AndroidEnvironment.ExternalStorageDirectory?.AbsolutePath?.TrimEnd('/');
        if (path == storageRoot) return new ReceiveFolderChoice(null, problem);

        try
        {
            // Android decides what Beam may write where; try it instead of guessing.
            Directory.CreateDirectory(path);
            var probe = Path.Combine(path, ".beam-write-check");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return new ReceiveFolderChoice(path);
        }
        catch (Exception ex)
        {
            Log.Warn($"Can't save into {path}", ex);
            return new ReceiveFolderChoice(null, problem);
        }
    }

    private static string? PathFromTreeUri(string address)
    {
        try
        {
            var uri = AndroidUri.Parse(address)!;
            var id = DocumentsContract.GetTreeDocumentId(uri);
            if (string.IsNullOrEmpty(id)) return null;
            if (id.StartsWith("raw:", StringComparison.Ordinal)) return id[4..];
            if (uri.Authority == "com.android.providers.downloads.documents" && id == "downloads")
                return AndroidEnvironment.GetExternalStoragePublicDirectory(AndroidEnvironment.DirectoryDownloads)!.AbsolutePath;
            if (uri.Authority != "com.android.externalstorage.documents") return null;
            var colon = id.IndexOf(':');
            if (colon < 0) return null;
            var volume = id[..colon];
            var relative = id[(colon + 1)..];
            var root = volume == "primary" ? AndroidEnvironment.ExternalStorageDirectory!.AbsolutePath : "/storage/" + volume;
            return relative.Length == 0 ? root : Path.Combine(root, relative);
        }
        catch (Exception ex)
        {
            Log.Warn($"Unknown folder address {address}", ex);
            return null;
        }
    }

    /// <summary>Shares the end of today's log as text (Telegram, email…), so it can be sent for troubleshooting.</summary>
    public void ShareLog(string logDirectory)
    {
        try
        {
            var file = new DirectoryInfo(logDirectory).GetFiles("beam-*.log").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
            if (file == null) return;
            string text;
            using (var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                const int MaxBytes = 60_000; // big enough for the interesting part, small enough for any share target
                if (stream.Length > MaxBytes) stream.Seek(-MaxBytes, SeekOrigin.End);
                text = new StreamReader(stream).ReadToEnd();
            }

            var send = new Intent(Intent.ActionSend);
            send.SetType("text/plain");
            send.PutExtra(Intent.ExtraSubject, $"Beam log ({Build.Manufacturer} {Build.Model}, Android {Build.VERSION.Release}, Beam {Core.AppInfo.Version})");
            send.PutExtra(Intent.ExtraText, $"Beam {Core.AppInfo.Version} · {Build.Manufacturer} {Build.Model} · Android {Build.VERSION.Release}\n\n{text}");
            Start(Intent.CreateChooser(send, (string?)null)!);
        }
        catch (Exception ex)
        {
            Log.Warn("Could not share the log", ex);
        }
    }

    private static void Start(Intent intent)
    {
        try
        {
            intent.AddFlags(ActivityFlags.NewTask);
            Context.StartActivity(intent);
        }
        catch (Exception ex)
        {
            Log.Warn("No app can open this", ex);
        }
    }

    private sealed class ScanCallback : Java.Lang.Object, MediaScannerConnection.IOnScanCompletedListener
    {
        private readonly Action<AndroidUri?> _done;

        public ScanCallback(Action<AndroidUri?> done) => _done = done;

        public void OnScanCompleted(string? path, AndroidUri? uri) => _done(uri);
    }
}
