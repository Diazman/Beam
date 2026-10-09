using Android.App;
using Android.Content;
using Android.Media;
using Android.OS;
using Beam.App.Platform;
using Beam.Core.Diagnostics;
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
