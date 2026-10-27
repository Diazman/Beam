using Beam.App.Platform;
using Beam.Core.Diagnostics;
using Foundation;
using UIKit;

namespace Beam.iOS;

/// <summary>iOS integration: opening received files and links, notifications, sharing the log.</summary>
internal sealed class IosPlatformServices : IPlatformServices
{
    private readonly IosNotifications _notifications = new();

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

    public void ShowNotification(string title, string message, Action? onActivated = null) => _notifications.Post(null, title, message, onActivated);

    public void ShowQuestionNotification(string tag, string title, string message, Action onActivated, IReadOnlyList<NotificationButton> buttons) =>
        _notifications.Post(tag, title, message, onActivated, buttons);

    public void CancelNotification(string tag) => _notifications.Cancel(tag);

    /// <summary>Notifications need the user's permission; the rest (local network) iOS asks for by itself when used.</summary>
    public bool HasPermission(AppPermission permission) => permission != AppPermission.Notifications || _notifications.Allowed;

    public Task<bool> RequestPermissionAsync(AppPermission permission) =>
        permission == AppPermission.Notifications ? _notifications.RequestAsync() : Task.FromResult(true);

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
                popover.SourceView = controller.View!;
                popover.SourceRect = controller.View!.Bounds;
            }

            controller.PresentViewController(share, true, null);
        });
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
