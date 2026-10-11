using Android.App;
using Android.Content;
using Android.Media;
using Android.OS;
using Android.Provider;
using Android.Runtime;
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

    /// <summary>
    /// Opens the folder in the phone's Files app. Android has no standard way to do that for every folder: the
    /// Files app's own address works on most phones, else the Downloads screen.
    /// </summary>
    public void OpenFolder(string path)
    {
        var storage = AndroidEnvironment.ExternalStorageDirectory?.AbsolutePath?.TrimEnd('/');
        if (storage != null && path.StartsWith(storage + "/", StringComparison.Ordinal))
        {
            var relative = path[(storage.Length + 1)..].TrimEnd('/');
            var folder = DocumentsContract.BuildDocumentUri("com.android.externalstorage.documents", "primary:" + relative);
            var view = new Intent(Intent.ActionView);
            view.SetDataAndType(folder, DocumentsContract.Document.MimeTypeDir);
            view.AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.NewTask);
            try
            {
                Context.StartActivity(view);
                return;
            }
            catch (Exception ex)
            {
                Log.Info($"No app opens {path} directly ({ex.Message}); showing Downloads");
            }
        }

        Start(new Intent(DownloadManager.ActionViewDownloads));
    }

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

    public void ShowNotification(string title, string message, Action? onActivated = null) =>
        Post(null, title, message, onActivated, Array.Empty<NotificationButton>(), global::Android.Resource.Drawable.StatSysDownloadDone);

    public void ShowQuestionNotification(string tag, string title, string message, Action onActivated, IReadOnlyList<NotificationButton> buttons) =>
        Post(tag, title, message, onActivated, buttons, global::Android.Resource.Drawable.StatSysDownload);

    public void CancelNotification(string tag)
    {
        try
        {
            ((NotificationManager?)Context.GetSystemService(Context.NotificationService))?.Cancel(tag, 1);
        }
        catch (Exception ex)
        {
            Log.Warn("Could not remove a notification", ex);
        }
    }

    public bool HasPermission(AppPermission permission) =>
        AndroidPermissionFor(permission) is not { } name || Context.CheckSelfPermission(name) == global::Android.Content.PM.Permission.Granted;

    public Task<bool> RequestPermissionAsync(AppPermission permission) =>
        AndroidPermissionFor(permission) is { } name ? AndroidHost.RequestPermissionAsync(name) : Task.FromResult(true);

    public bool NearbyDevicesIsLocation => !OperatingSystem.IsAndroidVersionAtLeast(33);

    private static string? AndroidPermissionFor(AppPermission permission) => permission switch
    {
        AppPermission.Notifications => OperatingSystem.IsAndroidVersionAtLeast(33) ? global::Android.Manifest.Permission.PostNotifications : null,
        AppPermission.NearbyDevices => OperatingSystem.IsAndroidVersionAtLeast(33) ? global::Android.Manifest.Permission.NearbyWifiDevices
            : OperatingSystem.IsAndroidVersionAtLeast(29) ? global::Android.Manifest.Permission.AccessFineLocation : null,
        _ => null,
    };

    /// <summary>
    /// Posts a notification. Tapping it opens Beam and runs <paramref name="onActivated"/> there (Android lets only the
    /// app on screen open other apps, e.g. to show a received file); buttons run their action without opening Beam.
    /// </summary>
    private static void Post(string? tag, string title, string message, Action? onActivated, IReadOnlyList<NotificationButton> buttons, int icon)
    {
        try
        {
            var manager = (NotificationManager?)Context.GetSystemService(Context.NotificationService);
            if (manager == null) return;
            if (Build.VERSION.SdkInt >= BuildVersionCodes.O && manager.GetNotificationChannel(ChannelId) == null)
                manager.CreateNotificationChannel(new NotificationChannel(ChannelId, L.T("Transfers"), NotificationImportance.High));

            var open = Context.PackageManager!.GetLaunchIntentForPackage(Context.PackageName!)!;
            open.SetFlags(ActivityFlags.SingleTop | ActivityFlags.ReorderToFront);
            if (onActivated != null) open.PutExtra(NotificationActions.Extra, NotificationActions.Register(onActivated));
            var pending = PendingIntent.GetActivity(Context, NotificationActions.NextRequestCode(), open, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
#pragma warning disable CA1422 // Notification.Builder(Context) is only for Android 7 and older
            var builder = Build.VERSION.SdkInt >= BuildVersionCodes.O ? new Notification.Builder(Context, ChannelId) : new Notification.Builder(Context);
#pragma warning restore CA1422
            builder.SetContentTitle(title)
                .SetContentText(message)
                .SetStyle(new Notification.BigTextStyle().BigText(message))
                .SetSmallIcon(icon)
                .SetContentIntent(pending)
                .SetAutoCancel(true);
            foreach (var button in buttons)
            {
                var click = new Intent(Context, typeof(NotificationActionReceiver));
                click.PutExtra(NotificationActions.Extra, NotificationActions.Register(button.OnClick));
                if (tag != null) click.PutExtra(NotificationActions.TagExtra, tag);
                var action = PendingIntent.GetBroadcast(Context, NotificationActions.NextRequestCode(), click, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
                builder.AddAction(new Notification.Action.Builder(
                    global::Android.Graphics.Drawables.Icon.CreateWithResource(Context, global::Android.Resource.Drawable.IcMenuSend), button.Label, action).Build());
            }

            if (tag != null) manager.Notify(tag, 1, builder.Build());
            else manager.Notify(Interlocked.Increment(ref _notificationId), builder.Build());
        }
        catch (Exception ex)
        {
            Log.Warn("Could not show a notification", ex);
        }
    }

    public string? DefaultReceiveFolder => AndroidHost.ReceiveFolder;

    public bool CanScanCodes => true;

    /// <summary>Google's code scanner: a full-screen camera view from Google Play services (Beam needs no camera permission).</summary>
    public Task<string?> ScanCodeAsync()
    {
        var done = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var context = AndroidHost.CurrentActivity ?? Context;
            var options = new Xamarin.Google.MLKit.Vision.CodeScanner.GmsBarcodeScannerOptions.Builder()
                .SetBarcodeFormats(Xamarin.Google.MLKit.Vision.Barcode.Common.Barcode.FormatQrCode)!
                .Build();
            var scanner = Xamarin.Google.MLKit.Vision.CodeScanner.GmsBarcodeScanning.GetClient(context, options);
            var listener = new ScanListener(done);
            scanner.StartScan()
                .AddOnSuccessListener(listener)
                .AddOnFailureListener(listener)
                .AddOnCanceledListener(listener);
        }
        catch (Exception ex)
        {
            Log.Warn("Could not start the code scanner", ex);
            done.TrySetResult(null);
        }

        return done.Task;
    }

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

    private sealed class ScanListener : Java.Lang.Object, Android.Gms.Tasks.IOnSuccessListener, Android.Gms.Tasks.IOnFailureListener, Android.Gms.Tasks.IOnCanceledListener
    {
        private readonly TaskCompletionSource<string?> _done;

        public ScanListener(TaskCompletionSource<string?> done) => _done = done;

        public void OnSuccess(Java.Lang.Object? result)
        {
            var barcode = result?.JavaCast<Xamarin.Google.MLKit.Vision.Barcode.Common.Barcode>();
            _done.TrySetResult(barcode?.RawValue);
        }

        public void OnFailure(Java.Lang.Exception e)
        {
            Log.Warn($"Code scanner failed: {e.Message}");
            _done.TrySetResult(null);
        }

        public void OnCanceled() => _done.TrySetResult(null);
    }

    private sealed class ScanCallback : Java.Lang.Object, MediaScannerConnection.IOnScanCompletedListener
    {
        private readonly Action<AndroidUri?> _done;

        public ScanCallback(Action<AndroidUri?> done) => _done = done;

        public void OnScanCompleted(string? path, AndroidUri? uri) => _done(uri);
    }
}
