using Beam.Core.Direct;

namespace Beam.Core.Tests;

/// <summary>
/// The rules every direct link's radio work follows: one operation at a time, bounded waits, and an attempt that was
/// given up never runs alongside (or after) the next one.
/// </summary>
public class RadioOperationsTests
{
    private static Exception TimedOut() => new DirectLinkException("timed out");

    [Fact]
    public async Task ARetryAfterATimeoutWaitsForTheStuckAttemptAndItsLateResultIsUndone()
    {
        var radio = new RadioOperations("test");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var events = new List<string>();
        var undone = new List<string>();

        // Like a Windows Wi-Fi call that ignores cancellation: it only finishes when released.
        var first = radio.RunAsync("start", TimeSpan.FromMilliseconds(200), async _ =>
            {
                await release.Task;
                lock (events) events.Add("first finished");
                return "network-1";
            }, TimedOut, result =>
            {
                lock (events) events.Add("undo " + result);
                undone.Add(result);
                return Task.CompletedTask;
            }, CancellationToken.None);

        var error = await Assert.ThrowsAsync<DirectLinkException>(() => first);
        Assert.Equal("timed out", error.Message); // the caller got its answer at the limit, not when the call ended
        Assert.Equal(1, radio.Abandoned);

        var retry = radio.RunAsync("start again", TimeSpan.FromSeconds(10), _ =>
        {
            lock (events) events.Add("retry ran");
            return Task.FromResult("network-2");
        }, TimedOut, null, CancellationToken.None);
        await Task.Delay(300);
        Assert.False(retry.IsCompleted, "the retry ran while the earlier attempt could still change the radio");

        release.SetResult();
        Assert.Equal("network-2", await retry);
        Assert.Equal(new[] { "first finished", "undo network-1", "retry ran" }, events);
        Assert.Equal(new[] { "network-1" }, undone);
        Assert.Equal(0, radio.Abandoned);
    }

    [Fact]
    public async Task CancellingStopsTheOperationAndFreesTheRadio()
    {
        var radio = new RadioOperations("test");
        using var cancel = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sawCancellation = false;

        var running = radio.RunAsync("join", TimeSpan.FromSeconds(30), async token =>
        {
            started.SetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, token);
            }
            catch (OperationCanceledException)
            {
                sawCancellation = true;
                throw;
            }

            return 1;
        }, TimedOut, _ => throw new InvalidOperationException("nothing to undo"), cancel.Token);

        await started.Task;
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        await Wait.UntilAsync(() => sawCancellation, because: "the operation's own token was cancelled");
        Assert.Equal(0, radio.Abandoned);

        // Free at once for the next one.
        Assert.Equal(2, await radio.RunAsync("next", TimeSpan.FromSeconds(1), _ => Task.FromResult(2), TimedOut, null, CancellationToken.None));
    }

    [Fact]
    public async Task ATimeoutCancelsTheOperationsOwnToken()
    {
        var radio = new RadioOperations("test");
        var observed = CancellationToken.None;
        var error = await Assert.ThrowsAsync<DirectLinkException>(() => radio.RunAsync<int>("host", TimeSpan.FromMilliseconds(100), async token =>
        {
            observed = token;
            await Task.Delay(Timeout.Infinite, token);
            return 0;
        }, TimedOut, null, CancellationToken.None));

        Assert.Equal("timed out", error.Message);
        Assert.True(observed.IsCancellationRequested, "the operation wasn't told to stop");
        Assert.Equal(0, radio.Abandoned); // it stopped, so nothing is left running
    }

    [Fact]
    public async Task CancellingAStuckOperationKeepsTheRadioUntilItEnds()
    {
        var radio = new RadioOperations("test");
        using var cancel = new CancellationTokenSource();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stuck = radio.RunAsync("join", TimeSpan.FromSeconds(30), async _ =>
        {
            await release.Task;
            return 1;
        }, TimedOut, null, cancel.Token);

        cancel.CancelAfter(100);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stuck);
        Assert.Equal(1, radio.Abandoned);
        Assert.False(await radio.RunExclusiveAsync("stop", TimeSpan.FromMilliseconds(200), () => Task.CompletedTask), "stopped while the join still ran");

        release.SetResult();
        Assert.True(await radio.RunExclusiveAsync("stop", TimeSpan.FromSeconds(5), () => Task.CompletedTask));
    }

    [Fact]
    public async Task ALateFailureIsNotUndone()
    {
        var radio = new RadioOperations("test");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var undone = false;
        await Assert.ThrowsAsync<DirectLinkException>(() => radio.RunAsync<string>("start", TimeSpan.FromMilliseconds(100), async _ =>
        {
            await release.Task;
            throw new InvalidOperationException("Wi-Fi off");
        }, TimedOut, _ =>
        {
            undone = true;
            return Task.CompletedTask;
        }, CancellationToken.None));

        release.SetResult();
        Assert.Equal(5, await radio.RunAsync("next", TimeSpan.FromSeconds(5), _ => Task.FromResult(5), TimedOut, null, CancellationToken.None));
        Assert.False(undone);
    }

    [Fact]
    public async Task ARetryThatCantGetTheRadioInTimeGivesUpWithoutRunning()
    {
        var radio = new RadioOperations("test");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var busy = radio.RunAsync("start", TimeSpan.FromSeconds(30), async _ =>
        {
            await release.Task;
            return 1;
        }, TimedOut, null, CancellationToken.None);

        var ran = false;
        var error = await Assert.ThrowsAsync<DirectLinkException>(() => radio.RunAsync("join", TimeSpan.FromMilliseconds(150), _ =>
        {
            ran = true;
            return Task.FromResult(2);
        }, TimedOut, null, CancellationToken.None));
        Assert.Equal("timed out", error.Message);
        Assert.False(ran);

        release.SetResult();
        Assert.Equal(1, await busy);
    }

    [Fact]
    public async Task OperationsNeverOverlap()
    {
        var radio = new RadioOperations("test");
        var inside = 0;
        var most = 0;
        var tasks = Enumerable.Range(0, 20).Select(i => radio.RunAsync($"op {i}", TimeSpan.FromSeconds(30), async _ =>
        {
            var now = Interlocked.Increment(ref inside);
            lock (radio) most = Math.Max(most, now);
            await Task.Delay(5);
            Interlocked.Decrement(ref inside);
            return i;
        }, TimedOut, null, CancellationToken.None)).ToList();

        Assert.Equal(Enumerable.Range(0, 20), await Task.WhenAll(tasks));
        Assert.Equal(1, most);
    }

    [Fact]
    public async Task AnOperationsOwnErrorReachesTheCaller()
    {
        var radio = new RadioOperations("test");
        var error = await Assert.ThrowsAsync<DirectLinkException>(() => radio.RunAsync<int>("host", TimeSpan.FromSeconds(5),
            _ => throw new DirectLinkException("This computer's Wi-Fi is busy."), TimedOut, null, CancellationToken.None));
        Assert.Equal("This computer's Wi-Fi is busy.", error.Message);
        Assert.Equal(3, await radio.RunAsync("next", TimeSpan.FromSeconds(1), _ => Task.FromResult(3), TimedOut, null, CancellationToken.None));
    }

    [Fact]
    public async Task ClosingGoesAheadEvenWhileAnOperationHangs()
    {
        var radio = new RadioOperations("test");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = radio.RunAsync("join", TimeSpan.FromSeconds(30), async _ =>
        {
            await release.Task;
            return 1;
        }, TimedOut, null, CancellationToken.None);

        var stopped = false;
        Assert.True(await radio.RunExclusiveAsync("closing", TimeSpan.FromMilliseconds(100), () =>
        {
            stopped = true;
            return Task.CompletedTask;
        }, runAnyway: true));
        Assert.True(stopped);
        release.SetResult();
    }

    [Fact]
    public async Task SucceedingJustAsTheCallerGivesUpIsUndone()
    {
        var radio = new RadioOperations("test");
        using var cancel = new CancellationTokenSource();
        var undone = new List<string>();
        var running = radio.RunAsync("start", TimeSpan.FromSeconds(30), async token =>
        {
            cancel.Cancel(); // the caller gives up while Windows finishes anyway
            await Task.Delay(20, CancellationToken.None);
            return "network";
        }, TimedOut, result =>
        {
            undone.Add(result);
            return Task.CompletedTask;
        }, cancel.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.Equal(new[] { "network" }, undone);
    }

    [Fact]
    public async Task AnOperationCancelledBySomethingElseReportsACancellation()
    {
        // A cancelled task carries no exception: this used to end in an ArgumentNullException.
        var radio = new RadioOperations("test");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => radio.RunAsync("host", TimeSpan.FromSeconds(5), async _ =>
        {
            using var other = new CancellationTokenSource();
            other.Cancel();
            await Task.Delay(10, other.Token);
            return 0;
        }, TimedOut, null, CancellationToken.None));
        Assert.Equal(4, await radio.RunAsync("next", TimeSpan.FromSeconds(1), _ => Task.FromResult(4), TimedOut, null, CancellationToken.None));
    }

    [Fact]
    public async Task ASlowUndoKeepsTheRadioUntilItIsDone()
    {
        var warning = RadioOperations.UndoWarning;
        RadioOperations.UndoWarning = TimeSpan.FromMilliseconds(50);
        try
        {
            var radio = new RadioOperations("test");
            var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var undoDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var events = new List<string>();
            await Assert.ThrowsAsync<DirectLinkException>(() => radio.RunAsync("start", TimeSpan.FromMilliseconds(100), async _ =>
            {
                await finish.Task;
                return "network-1";
            }, TimedOut, async _ =>
            {
                await undoDone.Task; // e.g. stopping the hotspot takes long
                lock (events) events.Add("undone");
            }, CancellationToken.None));

            finish.SetResult();
            var next = radio.RunAsync("start again", TimeSpan.FromSeconds(10), _ =>
            {
                lock (events) events.Add("next ran");
                return Task.FromResult(1);
            }, TimedOut, null, CancellationToken.None);
            await Task.Delay(300); // well past the warning
            Assert.False(next.IsCompleted, "the next start ran while the undo could still stop what it starts");

            undoDone.SetResult();
            await next;
            Assert.Equal(new[] { "undone", "next ran" }, events);
        }
        finally
        {
            RadioOperations.UndoWarning = warning;
        }
    }

    // ---- Android's callbacks ----

    [Fact]
    public async Task AnUnansweredCallbackIsWithdrawnBeforeTheCallerHearsOfIt()
    {
        var answer = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var withdrawn = false;
        await Assert.ThrowsAsync<TimeoutException>(() => RadioCallbacks.WaitAsync(answer.Task, TimeSpan.FromMilliseconds(100), async () =>
        {
            await Task.Delay(50);
            withdrawn = true;
        }, CancellationToken.None));
        Assert.True(withdrawn, "the next attempt could start before the request was withdrawn");

        answer.SetResult(null); // Android answers late: nobody is waiting, nothing breaks
        Assert.True(answer.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task GivingUpOnACallbackWithdrawsTheRequest()
    {
        var answer = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancel = new CancellationTokenSource(100);
        var withdrawn = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RadioCallbacks.WaitAsync(answer.Task, TimeSpan.FromSeconds(30), () =>
        {
            withdrawn++;
            return Task.CompletedTask;
        }, cancel.Token));
        Assert.Equal(1, withdrawn);
    }

    [Fact]
    public async Task ACallbackInTimeIsNotWithdrawnAndAFailingWithdrawalDoesntHideTheTimeout()
    {
        var answered = Task.FromResult<string?>("busy");
        Assert.Equal("busy", await RadioCallbacks.WaitAsync(answered, TimeSpan.FromSeconds(1), () => throw new InvalidOperationException("not called"), CancellationToken.None));

        var never = new TaskCompletionSource<string?>().Task;
        await Assert.ThrowsAsync<TimeoutException>(() => RadioCallbacks.WaitAsync(never, TimeSpan.FromMilliseconds(50),
            () => throw new InvalidOperationException("Wi-Fi Direct gone"), CancellationToken.None));
    }
}
