using System.Collections.Concurrent;
using Beam.App.Platform;
using Beam.Core.Diagnostics;
using Foundation;
using UIKit;
using UserNotifications;

namespace Beam.iOS;

/// <summary>
/// Beam's notifications on iOS: whether they are allowed (checked again whenever Beam comes back to the front, since
/// the user can change it in Settings), what a tap does, and the buttons of a question (Accept / Decline).
/// </summary>
internal sealed class IosNotifications : UNUserNotificationCenterDelegate
{
    private readonly ConcurrentDictionary<string, Handlers> _handlers = new();
    private readonly ConcurrentDictionary<string, UNNotificationCategory> _categories = new();
    private readonly NSObject _foregroundObserver;
    private Task _refresh;
    private volatile bool _allowed;
    private static int _nextId;

    private sealed record Handlers(Action? OnActivated, IReadOnlyList<NotificationButton> Buttons);

    public IosNotifications()
    {
        UNUserNotificationCenter.Current.Delegate = this;
        _refresh = RefreshAsync();
        _foregroundObserver = UIApplication.Notifications.ObserveWillEnterForeground((_, _) => _refresh = RefreshAsync());
    }

    /// <summary>The user allowed Beam's notifications (waits briefly for the first check after launch).</summary>
    public bool Allowed
    {
        get
        {
            try
            {
                _refresh.Wait(TimeSpan.FromSeconds(1));
            }
            catch (Exception)
            {
                // logged by RefreshAsync
            }

            return _allowed;
        }
    }

    public async Task<bool> RequestAsync()
    {
        try
        {
            var (granted, _) = await UNUserNotificationCenter.Current.RequestAuthorizationAsync(
                UNAuthorizationOptions.Alert | UNAuthorizationOptions.Sound | UNAuthorizationOptions.Badge).ConfigureAwait(false);
            _allowed = granted;
            return granted;
        }
        catch (Exception ex)
        {
            Log.Warn("Could not ask for notifications", ex);
            return false;
        }
    }

    private async Task RefreshAsync()
    {
        try
        {
            var settings = await UNUserNotificationCenter.Current.GetNotificationSettingsAsync().ConfigureAwait(false);
            _allowed = settings.AuthorizationStatus is UNAuthorizationStatus.Authorized or UNAuthorizationStatus.Provisional
                or UNAuthorizationStatus.Ephemeral;
        }
        catch (Exception ex)
        {
            Log.Warn("Could not read the notification settings", ex);
        }
    }

    public void Post(string? tag, string title, string message, Action? onActivated, IReadOnlyList<NotificationButton>? buttons = null)
    {
        try
        {
            var id = tag ?? "beam-" + Interlocked.Increment(ref _nextId);
            var content = new UNMutableNotificationContent { Title = title, Body = message, Sound = UNNotificationSound.Default };
            if (buttons is { Count: > 0 }) content.CategoryIdentifier = CategoryFor(buttons);
            if (onActivated != null || buttons is { Count: > 0 }) _handlers[id] = new Handlers(onActivated, buttons ?? Array.Empty<NotificationButton>());
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

    public void Cancel(string tag)
    {
        _handlers.TryRemove(tag, out _);
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

    /// <summary>A category per set of button labels (iOS needs them registered before a notification uses them).</summary>
    private string CategoryFor(IReadOnlyList<NotificationButton> buttons)
    {
        var id = "beam." + string.Join("|", buttons.Select(b => b.Label));
        if (_categories.ContainsKey(id)) return id;
        var actions = buttons.Select((b, i) => UNNotificationAction.FromIdentifier("b" + i, b.Label, UNNotificationActionOptions.Foreground)).ToArray();
        _categories[id] = UNNotificationCategory.FromIdentifier(id, actions, Array.Empty<string>(), UNNotificationCategoryOptions.None);
        UNUserNotificationCenter.Current.SetNotificationCategories(new NSSet<UNNotificationCategory>(_categories.Values.ToArray()));
        return id;
    }

    /// <summary>The user tapped the notification or one of its buttons.</summary>
    public override void DidReceiveNotificationResponse(UNUserNotificationCenter center, UNNotificationResponse response, Action completionHandler)
    {
        try
        {
            if (_handlers.TryRemove(response.Notification.Request.Identifier, out var handlers))
            {
                var action = response.ActionIdentifier;
                Action? run = action == UNNotificationAction.DefaultActionIdentifier ? handlers.OnActivated
                    : action.StartsWith('b') && int.TryParse(action.AsSpan(1), out var index) && index < handlers.Buttons.Count ? handlers.Buttons[index].OnClick
                    : null;
                run?.Invoke();
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Notification action failed", ex);
        }
        finally
        {
            completionHandler();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _foregroundObserver.Dispose();
        base.Dispose(disposing);
    }
}
