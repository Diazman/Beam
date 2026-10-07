using System.Runtime.Versioning;
using System.Security;
using Beam.Core.Diagnostics;
#if WINDOWS_TOASTS
using Microsoft.Win32;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;
#endif

namespace Beam.App.Platform;

/// <summary>
/// Native Windows 10/11 toast notifications for an unpackaged desktop app. The app registers its
/// AppUserModelID under HKCU so Windows knows the display name and icon to show.
/// Builds without the Windows SDK projection (the plain net8.0 target) simply don't show toasts.
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
internal sealed class ToastNotifier
{
#if WINDOWS_TOASTS
    private readonly Windows.UI.Notifications.ToastNotifier? _notifier;
#endif

    public ToastNotifier()
    {
#if WINDOWS_TOASTS
        try
        {
            var iconPath = ExtractIcon();
            using (var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\AppUserModelId\{WindowsPlatformServices.AppUserModelId}"))
            {
                key.SetValue("DisplayName", Core.AppInfo.ProductName);
                if (iconPath != null) key.SetValue("IconUri", iconPath);
            }

            _notifier = ToastNotificationManager.CreateToastNotifier(WindowsPlatformServices.AppUserModelId);
        }
        catch (Exception ex)
        {
            Log.Warn("Toast notifications unavailable", ex);
        }
#endif
    }

    public void Show(string title, string message, Action? onActivated)
    {
#if WINDOWS_TOASTS
        if (_notifier == null) return;
        try
        {
            var xml = new XmlDocument();
            xml.LoadXml(
                "<toast><visual><binding template=\"ToastGeneric\">" +
                $"<text>{SecurityElement.Escape(title)}</text><text>{SecurityElement.Escape(message)}</text>" +
                "</binding></visual></toast>");
            var toast = new ToastNotification(xml) { ExpirationTime = DateTimeOffset.Now.AddHours(1) };
            if (onActivated != null) toast.Activated += (_, _) => onActivated();
            _notifier.Show(toast);
        }
        catch (Exception ex)
        {
            Log.Warn("Could not show notification", ex);
        }
#else
        _ = title;
        _ = message;
        _ = onActivated;
#endif
    }

#if WINDOWS_TOASTS
    private static string? ExtractIcon()
    {
        try
        {
            var directory = Core.Storage.AppDataPaths.Default().Root;
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "notification-icon.png");
            using var source = Avalonia.Platform.AssetLoader.Open(new Uri("avares://Beam/Assets/beam.png"));
            using var target = File.Create(path);
            source.CopyTo(target);
            return path;
        }
        catch (Exception ex)
        {
            Log.Warn("Could not extract notification icon", ex);
            return null;
        }
    }
#endif
}
