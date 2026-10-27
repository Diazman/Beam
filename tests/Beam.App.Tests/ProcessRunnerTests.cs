using System.Diagnostics;
using Beam.App.Platform;

namespace Beam.App.Tests;

public class ProcessRunnerTests
{
    /// <summary>A helper that keeps writing and never ends (a stuck netsh or PowerShell).</summary>
    private static ProcessStartInfo Stuck() => OperatingSystem.IsWindows()
        ? new ProcessStartInfo("ping", "-n 60 127.0.0.1") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true }
        : new ProcessStartInfo("sh", "-c \"while true; do echo still here; sleep 1; done\"") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };

    [Fact]
    public async Task AHelperThatNeverEndsIsStoppedAtItsTimeLimit()
    {
        var watch = Stopwatch.StartNew();
        await Assert.ThrowsAsync<TimeoutException>(() => ProcessRunner.RunAsync(Stuck(), TimeSpan.FromSeconds(1)));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), $"took {watch.Elapsed}"); // not waiting for it to finish by itself
    }

    [Fact]
    public async Task ItsOutputIsReturned()
    {
        var start = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd", "/c echo hello") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true }
            : new ProcessStartInfo("sh", "-c \"echo hello\"") { RedirectStandardOutput = true, UseShellExecute = false };
        var (exit, output) = await ProcessRunner.RunAsync(start, TimeSpan.FromSeconds(30));
        Assert.Equal(0, exit);
        Assert.Equal("hello", output.Trim());
    }
}
