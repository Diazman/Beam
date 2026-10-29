using Android.App;
using Android.App.Job;
using Android.Content;
using Beam.Core.Diagnostics;

namespace Beam.Droid;

/// <summary>
/// Runs the automatic backups in the background: Android starts it about every 15 minutes while the phone is on a
/// Wi-Fi (an unmetered network; never on mobile data). It looks for the computer for a short while and backs up
/// whatever is new; when the computer isn't there, it ends at once.
/// </summary>
[Service(Permission = "android.permission.BIND_JOB_SERVICE", Exported = false)]
public sealed class BackupJobService : JobService
{
    private const int JobId = 4711;
    private CancellationTokenSource? _cts;

    public override bool OnStartJob(JobParameters? parameters)
    {
        _cts = new CancellationTokenSource(TimeSpan.FromMinutes(9)); // Android stops jobs after 10 minutes
        var token = _cts.Token;
        Task.Run(async () =>
        {
            try
            {
                var node = AndroidHost.EnsureNode();
                if (node == null) return; // Beam hasn't been opened yet
                await node.Backup.RunDueAsync(TimeSpan.FromSeconds(20), token);
            }
            catch (OperationCanceledException)
            {
                // Android stopped the job; the next one continues where this one stopped
            }
            catch (Exception ex)
            {
                Log.Warn("Backup: the background check failed", ex);
            }
            finally
            {
                JobFinished(parameters, false);
            }
        });
        return true;
    }

    public override bool OnStopJob(JobParameters? parameters)
    {
        _cts?.Cancel();
        return true; // try again later
    }

    /// <summary>Starts or stops the periodic background check.</summary>
    internal static void Schedule(Context context, bool enabled)
    {
        try
        {
            var scheduler = (JobScheduler?)context.GetSystemService(JobSchedulerService);
            if (scheduler == null) return;
            if (!enabled)
            {
                scheduler.Cancel(JobId);
                return;
            }

            if (scheduler.GetPendingJob(JobId) != null) return;
            var job = new JobInfo.Builder(JobId, new ComponentName(context, Java.Lang.Class.FromType(typeof(BackupJobService))))
                .SetRequiredNetworkType(NetworkType.Unmetered)!
                .SetPeriodic(15 * 60 * 1000)!
                .SetPersisted(true)! // after a restart too (needs RECEIVE_BOOT_COMPLETED)
                .Build();
            var result = scheduler.Schedule(job!);
            Log.Info(result == JobScheduler.ResultSuccess ? "Backup: background checks on Wi-Fi scheduled" : "Backup: couldn't schedule background checks");
        }
        catch (Exception ex)
        {
            Log.Warn("Backup: couldn't schedule background checks", ex);
        }
    }
}
