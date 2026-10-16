using Beam.App.Platform;
using Beam.Core.Diagnostics;
using Foundation;
using UIKit;
using UserNotifications;

namespace Beam.iOS;

/// <summary>iOS integration: opening received files and links, notifications, sharing the log.</summary>
internal sealed class IosPlatformServices : IPlatformServices
{
    private static int _notificationId;

    public bool IsPhone => true;

    public bool SupportsStartWithSystem => false;

    public bool GetStartWithSystem() => false;

    public void SetStartWithSystem(bool enabled)
    {
    }

    public string? DefaultReceiveFolder => IosHost.ReceiveFolder;

    /// <summary>Opens the folder in the Files app (shareddocuments:// shows an app's own folder there).</summary>
    public void OpenFolder(string path) => Open("shareddocuments://" + Uri.EscapeDataString(path).Replace("%2F", "/"));

    public void RevealInFolder(string path) => OpenFolder(Directory.Exists(path) ? path : Path.GetDirectoryName(path) ?? path);

    /// <summary>Shows the file with iOS's own viewer (Quick Look), which also offers Share and Save.</summary>
    public void OpenFile(string path)
    {
        if (Directory.Exists(path))
        {
            OpenFolder(path);
            return;
        }

        OnMain(controller =>
        {
            var preview = UIDocumentInteractionController.FromUrl(NSUrl.FromFilename(path));
            preview.ViewControllerForPreview = _ => controller;
            if (!preview.PresentPreview(true)) preview.PresentOptionsMenu(controller.View!.Bounds, controller.View, true);
        });
    }

    public void OpenUrl(string url) => Open(url);

    public void ShowNotification(string title, string message, Action? onActivated = null) => Post(null, title, message);

    public void ShowQuestionNotification(string tag, string title, string message, Action onActivated, IReadOnlyList<NotificationButton> buttons) =>
        Post(tag, title, message);

    public void CancelNotification(string tag)
    {
        try
        {
            UNUserNotificationCenter.Current.RemoveDeliveredNotifications(new[] { tag });
            UNUserNotificationCenter.Current.RemovePendingNotificationRequests(new[] { tag });
        }
        catch (Exception ex)
        {
            Log.Warn("Could not remove a notification", ex);
        }
    }

    public bool HasPermission(AppPermission permission) => true;

    public async Task<bool> RequestPermissionAsync(AppPermission permission)
    {
        if (permission != AppPermission.Notifications) return true;
        try
        {
            var (granted, _) = await UNUserNotificationCenter.Current.RequestAuthorizationAsync(
                UNAuthorizationOptions.Alert | UNAuthorizationOptions.Sound | UNAuthorizationOptions.Badge);
            return granted;
        }
        catch (Exception ex)
        {
            Log.Warn("Could not ask for notifications", ex);
            return false;
        }
    }

    /// <summary>Hands the newest log to the share sheet (Mail, Messages, AirDrop, Save to Files…).</summary>
    public void ShareLog(string logDirectory)
    {
        var newest = Directory.Exists(logDirectory)
            ? new DirectoryInfo(logDirectory).GetFiles("*.log").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()
            : null;
        if (newest == null) return;
        OnMain(controller =>
        {
            var share = new UIActivityViewController(new NSObject[] { NSUrl.FromFilename(newest.FullName) }, null);
            if (share.PopoverPresentationController is { } popover)
            {
                popover.SourceView = controller.View;
                popover.SourceRect = controller.View!.Bounds;
            }

            controller.PresentViewController(share, true, null);
        });
    }

    private static void Post(string? tag, string title, string message)
    {
        try
        {
            var content = new UNMutableNotificationContent { Title = title, Body = message, Sound = UNNotificationSound.Default };
            var id = tag ?? "beam-" + Interlocked.Increment(ref _notificationId);
            UNUserNotificationCenter.Current.AddNotificationRequest(UNNotificationRequest.FromIdentifier(id, content, null), error =>
            {
                if (error != null) Log.Info($"Notification not shown: {error.LocalizedDescription}");
            });
        }
        catch (Exception ex)
        {
            Log.Warn("Could not show a notification", ex);
        }
    }

    private static void Open(string url) => UIApplication.SharedApplication.InvokeOnMainThread(() =>
    {
        try
        {
            UIApplication.SharedApplication.OpenUrl(new NSUrl(url), new UIApplicationOpenUrlOptions(), opened =>
            {
                if (!opened) Log.Info($"iOS didn't open {url}");
            });
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not open {url}", ex);
        }
    });

    private static void OnMain(Action<UIViewController> action) => UIApplication.SharedApplication.InvokeOnMainThread(() =>
    {
        try
        {
            var controller = UIApplication.SharedApplication.ConnectedScenes.OfType<UIWindowScene>()
                .SelectMany(s => s.Windows).FirstOrDefault(w => w.IsKeyWindow)?.RootViewController;
            while (controller?.PresentedViewController != null) controller = controller.PresentedViewController;
            if (controller == null) return;
            action(controller);
        }
        catch (Exception ex)
        {
            Log.Warn("Could not show that", ex);
        }
    });
}
