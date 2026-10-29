using System.Collections.Concurrent;
using Android.App;
using Android.Content;
using Beam.Core.Diagnostics;

namespace Beam.Droid;

/// <summary>
/// What notification taps and buttons do. Android only carries numbers in the notification; the actions stay here
/// (they need the running app, which the transfer service keeps alive while something is going on).
/// </summary>
internal static class NotificationActions
{
    public const string Extra = "beam.notification.action";
    public const string TagExtra = "beam.notification.tag";

    /// <summary>"Stop backup" on a backup notification: the computer's id (works after Beam was closed, too).</summary>
    public const string BackupStopExtra = "beam.backup.stop";

    /// <summary>On the intent sent when a notification is swiped away: its actions are forgotten.</summary>
    public const string ForgetExtra = "beam.notification.forget";

    // Each action belongs to one notification (its group): running one, or the notification going away, drops them all.
    private static readonly ConcurrentDictionary<int, (string Group, Action Action)> Actions = new();
    private static int _next;
    private static int _requestCode = 1000;

    public static int Register(Action action, string group)
    {
        var id = Interlocked.Increment(ref _next);
        Actions[id] = (group, action);
        return id;
    }

    /// <summary>The notification was dismissed, answered or replaced: its actions can't run any more.</summary>
    public static void Forget(string group)
    {
        foreach (var entry in Actions)
            if (entry.Value.Group == group) Actions.TryRemove(entry.Key, out _);
    }

    public static int NextRequestCode() => Interlocked.Increment(ref _requestCode);

    /// <summary>Runs the action an intent from a notification points to (once).</summary>
    public static void Run(Intent? intent)
    {
        var id = intent?.GetIntExtra(Extra, 0) ?? 0;
        if (id == 0 || !Actions.TryRemove(id, out var entry)) return;
        Forget(entry.Group); // the other buttons of the same notification
        try
        {
            entry.Action();
        }
        catch (Exception ex)
        {
            Log.Warn("Notification action failed", ex);
        }
    }
}

/// <summary>Notification buttons (Accept / Decline): handled in the background, without opening Beam.</summary>
[BroadcastReceiver(Enabled = true, Exported = false)]
public sealed class NotificationActionReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        if (intent?.GetStringExtra(NotificationActions.ForgetExtra) is { } dismissed)
        {
            NotificationActions.Forget(dismissed);
            return;
        }

        if (intent?.GetStringExtra(NotificationActions.TagExtra) is { } tag)
            ((NotificationManager?)context?.GetSystemService(Context.NotificationService))?.Cancel(tag, 1);
        if (intent?.GetStringExtra(NotificationActions.BackupStopExtra) is { } target)
        {
            // Only Beam can send this (the receiver isn't exported); stopping a backup never needs the owner's check.
            Log.Info("Backup: stopped from its notification");
            AndroidHost.EnsureNode(start: false, create: false)?.Backup.Remove(target);
            return;
        }

        NotificationActions.Run(intent);
    }
}
