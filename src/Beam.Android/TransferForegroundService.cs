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

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        try
        {
            var notification = BuildNotification(this);
            if (OperatingSystem.IsAndroidVersionAtLeast(29)) StartForeground(NotificationId, notification, ForegroundService.TypeDataSync);
            else StartForeground(NotificationId, notification);
        }
        catch (Exception ex)
        {
            Log.Warn("Could not keep Beam running in the background", ex);
            StopSelf();
        }

        return StartCommandResult.NotSticky;
    }

    private static Notification BuildNotification(Context context)
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
            .SetContentTitle(L.T("Beam is transferring files"))
            .SetContentText(L.T("Keep Wi-Fi on until it's done. You can use other apps meanwhile."))
            .SetSmallIcon(global::Android.Resource.Drawable.StatSysUpload)
            .SetContentIntent(pending)
            .SetOngoing(true)
            .Build()!;
    }
}

/// <summary>Starts <see cref="TransferForegroundService"/> while any transfer runs (or waits for an answer) and stops it after.</summary>
internal sealed class TransferKeepAlive
{
    private readonly Context _context;
    private readonly TransferService _transfers;
    private readonly object _gate = new();
    private bool _running;
    private WifiManager.WifiLock? _wifiLock;
    private PowerManager.WakeLock? _wakeLock;

    public TransferKeepAlive(Context context, TransferService transfers)
    {
        _context = context;
        _transfers = transfers;
        transfers.SessionStarted += _ => Update();
        transfers.SessionFinished += _ => Update();
    }

    private void Update()
    {
        lock (_gate)
        {
            var active = _transfers.Sessions.Any(s => !s.IsFinished);
            if (active == _running) return;
            _running = active;
            var intent = new Intent(_context, typeof(TransferForegroundService));
            try
            {
                if (active)
                {
                    if (OperatingSystem.IsAndroidVersionAtLeast(26)) _context.StartForegroundService(intent);
                    else _context.StartService(intent);
                    AcquireLocks();
                    Log.Info("Keeping Beam running while files transfer");
                }
                else
                {
                    _context.StopService(intent);
                    ReleaseLocks();
                }
            }
            catch (Exception ex)
            {
                // E.g. Android doesn't let an app in the background start this (a transfer that began while Beam was hidden).
                Log.Warn("Could not start or stop the transfer service", ex);
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
#pragma warning disable CA1422 // the high-performance lock is the one that still works on older phones
                _wifiLock = wifi.CreateWifiLock(OperatingSystem.IsAndroidVersionAtLeast(29) ? WifiMode.FullLowLatency : WifiMode.FullHighPerf, "Beam transfer");
#pragma warning restore CA1422
                _wifiLock?.SetReferenceCounted(false);
                _wifiLock?.Acquire();
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
            if (_wakeLock?.IsHeld == true) _wakeLock.Release();
        }
        catch (Exception ex)
        {
            Log.Warn("Could not release the transfer locks", ex);
        }

        _wifiLock = null;
        _wakeLock = null;
    }
}
