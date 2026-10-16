using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Net.Wifi;
using Android.OS;
using Beam.Core.Diagnostics;
using Beam.Core.Localization;
using Beam.Core.Transfer;

namespace Beam.Droid;

/// <summary>
/// Keeps Beam running while files are being sent or received, also when the user switches to another app or turns
/// the screen off: Android would otherwise pause the app and the transfer would stall. Shows an ongoing
/// notification (required by Android) and keeps Wi-Fi and the processor awake until the last transfer ends.
/// </summary>
[Service(Exported = false, ForegroundServiceType = ForegroundService.TypeDataSync)]
public sealed class TransferForegroundService : Service
{
    private const string ChannelId = "running";
    private const int NotificationId = 1;

    public override IBinder? OnBind(Intent? intent) => null;

    /// <summary>Intent extra: true while files transfer, false while Beam only stays on a computer's network.</summary>
    internal const string TransferringExtra = "transferring";

    // Android kills the app ("startForegroundService() did not then call startForeground()") when a service started
    // that way is stopped before it called StartForeground. A send that fails at once (Wi-Fi off) did exactly that, so
    // it is never stopped from outside before then: it goes to the foreground first, then stops itself if no longer wanted.
    private static readonly object StateGate = new();
    private static bool _wanted;
    private static bool _inForeground;

    /// <summary>Asks for the service (true) or lets it go (false); it is stopped only once it is in the foreground.</summary>
    internal static void Want(Context context, bool wanted, bool transferring)
    {
        var intent = new Intent(context, typeof(TransferForegroundService)).PutExtra(TransferringExtra, transferring);
        lock (StateGate)
        {
            _wanted = wanted;
            if (wanted)
            {
                if (OperatingSystem.IsAndroidVersionAtLeast(26)) context.StartForegroundService(intent);
                else context.StartService(intent);
            }
            else if (_inForeground)
            {
                _inForeground = false;
                context.StopService(intent);
            }
            // else: still starting; it stops itself right after StartForeground (OnStartCommand)
        }
    }

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        try
        {
            var notification = BuildNotification(this, intent?.GetBooleanExtra(TransferringExtra, true) ?? true);
            if (OperatingSystem.IsAndroidVersionAtLeast(29)) StartForeground(NotificationId, notification, ForegroundService.TypeDataSync);
            else StartForeground(NotificationId, notification);
        }
        catch (Exception ex)
        {
            Log.Warn("Could not keep Beam running in the background", ex);
            lock (StateGate) _inForeground = false;
            StopSelf();
            return StartCommandResult.NotSticky;
        }

        lock (StateGate)
        {
            if (_wanted)
            {
                _inForeground = true;
                return StartCommandResult.NotSticky;
            }

            _inForeground = false;
        }

        // Let go while it was starting (the transfer already ended).
        Log.Info("Beam no longer needs to keep running; stopping the transfer service");
        if (OperatingSystem.IsAndroidVersionAtLeast(24)) StopForeground(StopForegroundFlags.Remove);
#pragma warning disable CA1422
        else StopForeground(true);
#pragma warning restore CA1422
        StopSelf();
        return StartCommandResult.NotSticky;
    }

    internal static void Update(Context context, bool transferring)
    {
        try
        {
            ((NotificationManager?)context.GetSystemService(NotificationService))?.Notify(NotificationId, BuildNotification(context, transferring));
        }
        catch (Exception ex)
        {
            Log.Warn("Could not update Beam's notification", ex);
        }
    }

    private static Notification BuildNotification(Context context, bool transferring)
    {
        var manager = (NotificationManager?)context.GetSystemService(NotificationService);
        if (OperatingSystem.IsAndroidVersionAtLeast(26) && manager?.GetNotificationChannel(ChannelId) == null)
            manager?.CreateNotificationChannel(new NotificationChannel(ChannelId, L.T("Transfers in progress"), NotificationImportance.Low));
        var open = context.PackageManager!.GetLaunchIntentForPackage(context.PackageName!)!;
        open.SetFlags(ActivityFlags.SingleTop | ActivityFlags.ReorderToFront);
        var pending = PendingIntent.GetActivity(context, 0, open, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
#pragma warning disable CA1422 // Notification.Builder(Context) is only for Android 7 and older
        var builder = OperatingSystem.IsAndroidVersionAtLeast(26) ? new Notification.Builder(context, ChannelId) : new Notification.Builder(context);
#pragma warning restore CA1422
        return builder
            .SetContentTitle(transferring ? L.T("Beam is transferring files") : L.T("Beam is connected to a computer"))
            .SetContentText(transferring
                ? L.T("Keep Wi-Fi on until it's done. You can use other apps meanwhile.")
                : L.T("On the computer's own Wi-Fi, ready to send and receive files."))
            .SetSmallIcon(transferring ? global::Android.Resource.Drawable.StatSysUpload : global::Android.Resource.Drawable.StatNotifySync)
            .SetContentIntent(pending)
            .SetOngoing(true)
            .Build()!;
    }
}

/// <summary>
/// Starts <see cref="TransferForegroundService"/> while any transfer runs (or waits for an answer), and while Beam is on a
/// computer's own Wi-Fi, so a transfer the computer starts later still runs with Beam in the background (Android doesn't
/// let a hidden app start this service). Stops it after.
/// </summary>
internal sealed class TransferKeepAlive
{
    private readonly Context _context;
    private readonly TransferService _transfers;
    private readonly AndroidDirectLink? _link;
    private readonly object _gate = new();
    private bool _running;
    private bool _transferring;
    private WifiManager.WifiLock? _wifiLock;
    private WifiManager.WifiLock? _lowLatencyLock;
    private PowerManager.WakeLock? _wakeLock;

    public TransferKeepAlive(Context context, TransferService transfers, AndroidDirectLink? link)
    {
        _context = context;
        _transfers = transfers;
        _link = link;
        transfers.SessionStarted += _ => Update();
        transfers.SessionFinished += _ => Update();
        if (link != null) link.JoinedChanged += Update;
    }

    /// <summary>Beam came to the screen: start the service now if Android refused it while Beam was hidden.</summary>
    public void Retry() => Update();

    private void Update()
    {
        lock (_gate)
        {
            var transferring = _transfers.Sessions.Any(s => !s.IsFinished);
            var active = transferring || _link?.IsJoined == true;
            if (active && _running)
            {
                if (transferring != _transferring) TransferForegroundService.Update(_context, transferring);
                _transferring = transferring;
                return;
            }

            if (active == _running) return;
            try
            {
                if (active)
                {
                    TransferForegroundService.Want(_context, true, transferring);
                    _running = true;
                    _transferring = transferring;
                    AcquireLocks();
                    Log.Info(transferring ? "Keeping Beam running while files transfer" : "Keeping Beam running while it's on a computer's Wi-Fi");
                }
                else
                {
                    _running = false;
                    TransferForegroundService.Want(_context, false, transferring);
                    ReleaseLocks();
                }
            }
            catch (Exception ex)
            {
                // E.g. Android doesn't let an app in the background start this (a transfer that began while Beam was
                // hidden): keep Wi-Fi awake anyway and try again when Beam is opened.
                Log.Warn($"Could not start or stop the transfer service: {ex.Message}");
                if (active) AcquireLocks();
            }
        }
    }

    private void AcquireLocks()
    {
        try
        {
            if (_wifiLock == null && _context.GetSystemService(Context.WifiService) is WifiManager wifi)
            {
                // High performance keeps Wi-Fi's power saving off, also with the screen off (low latency, below, only
                // counts while Beam is on screen); power saving slows a transfer down a lot on some phones.
#pragma warning disable CA1422 // deprecated in Android 14 (where it does nothing), still what works with the screen off before that
                _wifiLock = wifi.CreateWifiLock(global::Android.Net.WifiMode.FullHighPerf, "Beam transfer");
#pragma warning restore CA1422
                _wifiLock?.SetReferenceCounted(false);
                _wifiLock?.Acquire();
                if (OperatingSystem.IsAndroidVersionAtLeast(29))
                {
                    _lowLatencyLock = wifi.CreateWifiLock(global::Android.Net.WifiMode.FullLowLatency, "Beam transfer (on screen)");
                    _lowLatencyLock?.SetReferenceCounted(false);
                    _lowLatencyLock?.Acquire();
                }
            }

            if (_wakeLock == null && _context.GetSystemService(Context.PowerService) is PowerManager power)
            {
                _wakeLock = power.NewWakeLock(WakeLockFlags.Partial, "Beam:transfer");
                _wakeLock?.SetReferenceCounted(false);
                _wakeLock?.Acquire((long)TimeSpan.FromHours(6).TotalMilliseconds); // a safety limit; released when the last transfer ends
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Could not keep Wi-Fi awake for the transfer", ex);
        }
    }

    private void ReleaseLocks()
    {
        try
        {
            if (_wifiLock?.IsHeld == true) _wifiLock.Release();
            if (_lowLatencyLock?.IsHeld == true) _lowLatencyLock.Release();
            if (_wakeLock?.IsHeld == true) _wakeLock.Release();
        }
        catch (Exception ex)
        {
            Log.Warn("Could not release the transfer locks", ex);
        }

        _wifiLock = null;
        _lowLatencyLock = null;
        _wakeLock = null;
    }
}
