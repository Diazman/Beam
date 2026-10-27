namespace Beam.Core.Diagnostics;

public static class BackgroundTask
{
    /// <summary>
    /// Lets <paramref name="task"/> run on its own; if it fails, that is logged (as "<paramref name="what"/> failed")
    /// instead of disappearing unseen.
    /// </summary>
    public static void Forget(this Task task, string what)
    {
        if (task.IsCompletedSuccessfully) return;
        task.ContinueWith(t =>
        {
            if (t.Exception?.GetBaseException() is { } ex and not OperationCanceledException) Log.Warn($"{what} failed", ex);
        }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
}
