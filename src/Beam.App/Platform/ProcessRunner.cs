using System.Diagnostics;

namespace Beam.App.Platform;

/// <summary>Runs a helper program (netsh, PowerShell) with a time limit that covers its output too.</summary>
internal static class ProcessRunner
{
    /// <summary>
    /// Starts <paramref name="start"/> and waits for it to exit, reading its output (when redirected) meanwhile. Past
    /// <paramref name="timeout"/>, or when cancelled, the program is ended and this throws.
    /// </summary>
    public static async Task<(int Exit, string Output)> RunAsync(ProcessStartInfo start, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"{start.FileName} didn't start.");
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        try
        {
            // Both streams at once: a program blocks when a redirected stream it writes to is never read.
            var output = start.RedirectStandardOutput ? process.StandardOutput.ReadToEndAsync(limit.Token) : Task.FromResult("");
            var errors = start.RedirectStandardError ? process.StandardError.ReadToEndAsync(limit.Token) : Task.FromResult("");
            await Task.WhenAll(output, errors, process.WaitForExitAsync(limit.Token)).ConfigureAwait(false);
            return (process.ExitCode, await output.ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Stop(process);
            throw new TimeoutException($"{Path.GetFileName(start.FileName)} didn't finish within {timeout.TotalSeconds:0} seconds.");
        }
        catch (OperationCanceledException)
        {
            Stop(process);
            throw;
        }
    }

    private static void Stop(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
            // gone already, or not ours to end (an elevated process)
        }
    }
}
