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

    private static readonly ConcurrentDictionary<int, Action> Actions = new();
    private static int _next;
    private static int _requestCode = 1000;

    public static int Register(Action action)
    {
        var id = Interlocked.Increment(ref _next);
        Actions[id] = action;
        return id;
    }

    public static int NextRequestCode() => Interlocked.Increment(ref _requestCode);

    /// <summary>Runs the action an intent from a notification points to (once).</summary>
    public static void Run(Intent? intent)
    {
        var id = intent?.GetIntExtra(Extra, 0) ?? 0;
        if (id == 0 || !Actions.TryRemove(id, out var action)) return;
        try
        {
            action();
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
        if (intent?.GetStringExtra(NotificationActions.TagExtra) is { } tag)
            ((NotificationManager?)context?.GetSystemService(Context.NotificationService))?.Cancel(tag, 1);
        NotificationActions.Run(intent);
    }
}
