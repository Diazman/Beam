using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Beam.Core.Diagnostics;

namespace Beam.App.Services;

/// <summary>
/// Keeps one Beam per user (per data folder). A second launch hands its command line to the
/// running instance — e.g. to bring the window forward or add files — and exits.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _cts = new();

    private SingleInstance(Mutex mutex, string pipeName)
    {
        _mutex = mutex;
        _pipeName = pipeName;
    }

    /// <summary>Raised on a background thread with the arguments of a later launch.</summary>
    public event Action<string[]>? ArgumentsReceived;

    public static string KeyFor(string dataFolder)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dataFolder.ToLowerInvariant())))[..16];
        return $"Beam-{Environment.UserName}-{hash}";
    }

    /// <summary>Returns null if another instance already owns <paramref name="key"/>.</summary>
    public static SingleInstance? TryAcquire(string key)
    {
        // Windows: per logon session. Elsewhere .NET scopes unprefixed names to the terminal session,
        // so use Global\ (the key already contains the user name).
        var mutex = new Mutex(initiallyOwned: true, (OperatingSystem.IsWindows() ? @"Local\" : @"Global\") + key, out var created);
        if (!created)
        {
            mutex.Dispose();
            return null;
        }

        var instance = new SingleInstance(mutex, key);
        _ = Task.Run(instance.ListenAsync);
        return instance;
    }

    public static bool SendToPrimary(string key, string[] args)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", key, PipeDirection.Out);
            client.Connect(3000);
            var payload = JsonSerializer.SerializeToUtf8Bytes(args, ArgsJson.Default.StringArray);
            client.Write(payload);
            client.Flush();
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not reach the running instance: {ex.Message}");
            return false;
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _mutex.ReleaseMutex(); } catch { /* not owned */ }
        _mutex.Dispose();
    }

    private async Task ListenAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(_pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(_cts.Token);
                using var buffer = new MemoryStream();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                var chunk = new byte[4096];
                int read;
                while ((read = await server.ReadAsync(chunk, timeout.Token)) > 0 && buffer.Length < 1024 * 1024)
                    buffer.Write(chunk, 0, read);

                var args = JsonSerializer.Deserialize(buffer.ToArray(), ArgsJson.Default.StringArray) ?? Array.Empty<string>();
                ArgumentsReceived?.Invoke(args);
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Warn($"Single-instance pipe error: {ex.Message}");
                await Task.Delay(500);
            }
        }
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(string[]))]
internal sealed partial class ArgsJson : System.Text.Json.Serialization.JsonSerializerContext
{
}
