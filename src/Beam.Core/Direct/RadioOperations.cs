using Beam.Core.Diagnostics;

namespace Beam.Core.Direct;

/// <summary>
/// Runs a direct link's radio operations (starting a network, joining one, stopping) one at a time, each with a time
/// limit. When the limit passes or the caller gives up, the operation's token is cancelled and the caller gets its
/// answer right away; but an operation that doesn't stop (Windows' Wi-Fi calls can hang) keeps the gate until it has
/// really finished, and what it did late is undone first. So a retry never runs alongside an earlier attempt that can
/// still change the radio.
/// </summary>
public sealed class RadioOperations
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _name;
    private int _abandoned;

    public RadioOperations(string name) => _name = name;

    /// <summary>Operations given up on that are still running (each holds the gate until it finishes).</summary>
    public int Abandoned => Volatile.Read(ref _abandoned);

    /// <summary>How long a cancelled operation gets to stop by itself before it counts as stuck.</summary>
    internal static TimeSpan StopGrace { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>After this long, a slow undo is logged (the gate stays held until it is done).</summary>
    internal static TimeSpan UndoWarning { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Runs <paramref name="operation"/> once the earlier ones are done. <paramref name="timeout"/> covers the wait for
    /// the gate and the operation itself; when it passes, the exception from <paramref name="timedOut"/> is thrown (the
    /// operation's own failure, if it failed by then). When <paramref name="cancellationToken"/> is cancelled,
    /// <see cref="OperationCanceledException"/> is thrown. Either way an operation still running is left to finish while
    /// holding the gate, and <paramref name="undoLateResult"/> (if any) then runs on what it returned.
    /// </summary>
    public async Task<T> RunAsync<T>(string what, TimeSpan timeout, Func<CancellationToken, Task<T>> operation,
        Func<Exception> timedOut, Func<T, Task>? undoLateResult, CancellationToken cancellationToken)
    {
        var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        try
        {
            await _gate.WaitAsync(limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            limit.Dispose();
            cancellationToken.ThrowIfCancellationRequested();
            Log.Warn($"{_name}: {what}: an earlier attempt is still running; gave up after {timeout.TotalSeconds:0} s");
            throw timedOut();
        }

        // On a pool thread (not the caller's, which may be the window's), so a call that blocks can't hold the caller.
        var task = Task.Run(() => operation(limit.Token), CancellationToken.None);
        try
        {
            await task.WaitAsync(limit.Token).ConfigureAwait(false);
        }
        catch
        {
            // Finished, failed, or the limit passed (or the caller gave up): looked at below.
        }

        // Its token is cancelled now: most operations stop right away.
        if (!task.IsCompleted) await Task.WhenAny(task, Task.Delay(StopGrace)).ConfigureAwait(false);
        if (!task.IsCompleted)
        {
            // Still running: it keeps the gate (and the token source it uses) until it is done.
            Interlocked.Increment(ref _abandoned);
            Log.Warn($"{_name}: {what} is still running after it was {(cancellationToken.IsCancellationRequested ? "cancelled" : "given up")}; " +
                     "later operations wait for it");
            FinishAbandonedAsync(what, task, undoLateResult, limit).Forget($"Direct: finishing {what}");
            cancellationToken.ThrowIfCancellationRequested();
            throw timedOut();
        }

        try
        {
            if (task.IsCompletedSuccessfully)
            {
                if (!cancellationToken.IsCancellationRequested) return task.Result;
                // Done just as the caller gave up: it won't use the result, so undo it before the next operation.
                if (undoLateResult != null) await UndoAsync(what, task.Result, undoLateResult).ConfigureAwait(false);
                throw new OperationCanceledException(cancellationToken);
            }

            if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException(cancellationToken);
            var error = task.Exception?.GetBaseException(); // null when the task was cancelled
            if (error is null or OperationCanceledException)
            {
                if (limit.IsCancellationRequested) throw timedOut();
                throw error ?? new OperationCanceledException(); // cancelled by something else it waited on
            }

            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(error);
            throw error; // not reached
        }
        finally
        {
            limit.Dispose();
            _gate.Release();
        }
    }

    /// <summary>
    /// Waits for the gate (up to <paramref name="wait"/>) and runs <paramref name="action"/> under it; runs it without the
    /// gate when <paramref name="runAnyway"/> and the wait ran out (closing Beam must stop the radio even if something hangs).
    /// False when it didn't run.
    /// </summary>
    public async Task<bool> RunExclusiveAsync(string what, TimeSpan wait, Func<Task> action, bool runAnyway = false)
    {
        var locked = await _gate.WaitAsync(wait).ConfigureAwait(false);
        if (!locked)
        {
            Log.Warn($"{_name}: {what}: an earlier operation is still running{(runAnyway ? "; going ahead anyway" : "; skipped")}");
            if (!runAnyway) return false;
        }

        try
        {
            await action().ConfigureAwait(false);
            return true;
        }
        finally
        {
            if (locked) _gate.Release();
        }
    }

    /// <summary>
    /// Undoes a result nobody will use. Waited for in full while holding the gate: an undo still running could otherwise
    /// stop what the next operation starts.
    /// </summary>
    private async Task UndoAsync<T>(string what, T result, Func<T, Task> undo)
    {
        var undoing = Task.Run(() => undo(result));
        try
        {
            if (await Task.WhenAny(undoing, Task.Delay(UndoWarning)).ConfigureAwait(false) != undoing)
                Log.Warn($"{_name}: undoing {what} is taking long; later operations keep waiting for it");
            await undoing.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn($"{_name}: undoing {what} failed: {ex.Message}");
        }
    }

    private async Task FinishAbandonedAsync<T>(string what, Task<T> task, Func<T, Task>? undoLateResult, CancellationTokenSource limit)
    {
        try
        {
            T result;
            try
            {
                result = await task.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Info($"{_name}: {what} ended after it was given up ({ex.GetType().Name}: {ex.Message})");
                return;
            }

            if (undoLateResult == null)
            {
                Log.Info($"{_name}: {what} finished after it was given up");
                return;
            }

            Log.Warn($"{_name}: {what} finished after it was given up; undoing it");
            await UndoAsync(what, result, undoLateResult).ConfigureAwait(false);
        }
        finally
        {
            limit.Dispose();
            Interlocked.Decrement(ref _abandoned);
            _gate.Release();
        }
    }
}

/// <summary>Waiting for a platform callback (Android's Wi-Fi Direct listeners) without letting it hold anything forever.</summary>
public static class RadioCallbacks
{
    /// <summary>
    /// Waits for <paramref name="callback"/> at most <paramref name="timeout"/>. When it doesn't come in time, or the caller
    /// gives up, <paramref name="cleanup"/> withdraws the request (best effort, awaited so it is done before the next
    /// operation) and <see cref="TimeoutException"/> or <see cref="OperationCanceledException"/> is thrown. A callback
    /// arriving after that is ignored.
    /// </summary>
    public static async Task<T> WaitAsync<T>(Task<T> callback, TimeSpan timeout, Func<Task>? cleanup, CancellationToken cancellationToken)
    {
        try
        {
            return await callback.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            // Answered at the very moment the wait ended: still wanted unless the caller gave up.
            if (callback.IsCompletedSuccessfully && !cancellationToken.IsCancellationRequested) return callback.Result;
            if (cleanup != null)
            {
                try
                {
                    await cleanup().ConfigureAwait(false);
                }
                catch (Exception again)
                {
                    Log.Info($"Withdrawing an unanswered radio request failed: {again.Message}");
                }
            }

            throw;
        }
    }
}
