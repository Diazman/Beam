using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Beam.Core;
using Beam.Core.Discovery;
using Beam.Core.Storage;
using Beam.Core.Transfer;

namespace Beam.Core.Tests;

/// <summary>A temporary folder deleted at the end of the test.</summary>
public sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "beam-tests", Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(params string[] parts) => System.IO.Path.Combine(new[] { Path }.Concat(parts).ToArray());

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch { /* best effort */ }
    }
}

/// <summary>Programmable stand-in for the user on the receiving side.</summary>
public sealed class TestHandler : IIncomingTransferHandler
{
    public Func<IncomingRequest, IncomingDecision> Decide { get; set; } = _ => IncomingDecision.Accept();

    public Func<IReadOnlyList<FileConflict>, IReadOnlyList<ConflictAction>> Resolve { get; set; } =
        c => c.Select(_ => ConflictAction.KeepBoth).ToList();

    public TimeSpan DecisionDelay { get; set; }

    /// <summary>When set, the request is never answered (until cancelled).</summary>
    public bool NeverAnswer { get; set; }

    public List<IncomingRequest> Requests { get; } = new();

    public List<IReadOnlyList<FileConflict>> Conflicts { get; } = new();

    public bool ApprovalWasCancelled { get; private set; }

    public async Task<IncomingDecision> RequestApprovalAsync(IncomingRequest request, TransferSession session, CancellationToken cancellationToken)
    {
        lock (Requests) Requests.Add(request);
        try
        {
            if (NeverAnswer) await Task.Delay(Timeout.Infinite, cancellationToken);
            if (DecisionDelay > TimeSpan.Zero) await Task.Delay(DecisionDelay, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            ApprovalWasCancelled = true;
            throw;
        }

        return Decide(request);
    }

    public Task<IReadOnlyList<ConflictAction>> ResolveConflictsAsync(IncomingRequest request, IReadOnlyList<FileConflict> conflicts, CancellationToken cancellationToken)
    {
        lock (Conflicts) Conflicts.Add(conflicts);
        return Task.FromResult(Resolve(conflicts));
    }
}

/// <summary>A complete Beam device running in-process with its own data and receive folders.</summary>
public sealed class TestNode : IAsyncDisposable
{
    private readonly TempDir _dir = new();

    /// <param name="pro">Most tests run as Pro so the free edition's speed and daily limits don't apply.</param>
    public TestNode(string name, bool startDiscovery = false, bool pro = true, bool launchPeriod = false)
    {
        Edition = new Licensing.Edition(pro, launchPeriod);
        Handler = new TestHandler();
        ReceiveFolder = _dir.Combine("received");
        Directory.CreateDirectory(ReceiveFolder);
        Node = BeamNode.Create(new BeamNodeOptions
        {
            Paths = new AppDataPaths(_dir.Combine("data")),
            TransferPort = 0,
            Handler = Handler,
            Edition = Edition,
            Discovery = new DiscoveryOptions
            {
                Port = 0,
                UseMulticastAndBroadcast = false,
                AnnounceInterval = TimeSpan.FromMilliseconds(300),
                DeviceTimeout = TimeSpan.FromSeconds(2),
            },
        });
        Node.Settings.Update(s =>
        {
            s.DeviceName = name;
            s.ReceiveFolder = ReceiveFolder;
            s.FirstRunCompleted = true;
        });
        Node.Transfers.Start(0);
        if (startDiscovery) Node.Discovery.Start();
    }

    public BeamNode Node { get; }

    public TestHandler Handler { get; }

    public Licensing.Edition Edition { get; }

    public string ReceiveFolder { get; }

    public string Root => _dir.Path;

    /// <summary>How other nodes reach this one directly (bypassing discovery).</summary>
    public DeviceInfo AsDevice(int? viaPort = null) => new()
    {
        Id = Node.Identity.DeviceId,
        Name = Node.Settings.Current.DeviceName,
        Fingerprint = Node.Identity.Fingerprint,
        Endpoints = new[] { new IPEndPoint(IPAddress.Loopback, viaPort ?? Node.Transfers.Port) },
    };

    public string CreateFile(string relativePath, long size, int seed = 1)
    {
        var path = Path.Combine(Root, "source", relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        TestFiles.Write(path, size, seed);
        return path;
    }

    public string SourcePath(string relativePath) => Path.Combine(Root, "source", relativePath.Replace('/', Path.DirectorySeparatorChar));

    public async ValueTask DisposeAsync()
    {
        await Node.DisposeAsync();
        _dir.Dispose();
    }
}

public static class TestFiles
{
    /// <summary>Writes deterministic pseudo-random content without holding it all in memory.</summary>
    public static void Write(string path, long size, int seed)
    {
        var random = new Random(seed);
        var buffer = new byte[1024 * 1024];
        using var stream = File.Create(path);
        var remaining = size;
        while (remaining > 0)
        {
            random.NextBytes(buffer);
            var count = (int)Math.Min(buffer.Length, remaining);
            stream.Write(buffer, 0, count);
            remaining -= count;
        }
    }

    public static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}

public static class Wait
{
    public static async Task UntilAsync(Func<bool> condition, TimeSpan? timeout = null, string? because = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(20));
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Timed out waiting" + (because != null ? ": " + because : "."));
            await Task.Delay(25);
        }
    }

    public static Task ForFinishAsync(TransferSession session, TimeSpan? timeout = null) =>
        UntilAsync(() => session.IsFinished, timeout ?? TimeSpan.FromSeconds(60), $"session {session.Id} state {session.State}");
}

/// <summary>
/// TCP relay used to simulate network failures: it forwards traffic and can cut the
/// connection after a number of bytes, or refuse connections for a while.
/// </summary>
public sealed class FlakyProxy : IDisposable
{
    private readonly TcpListener _listener;
    private readonly IPEndPoint _target;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Socket> _sockets = new();

    public FlakyProxy(IPEndPoint target)
    {
        _target = target;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(AcceptLoopAsync);
    }

    public int Port { get; }

    /// <summary>Cut the next connection after this many bytes from client to server (once).</summary>
    public long CutAfterBytes { get; set; } = -1;

    /// <summary>Limits client-to-server throughput (bytes per second, per connection); 0 = unlimited.</summary>
    public long BytesPerSecond { get; set; }

    /// <summary>Number of connections accepted so far.</summary>
    public int Connections;

    public long BytesForwarded;

    public void CutAll()
    {
        lock (_sockets)
        {
            foreach (var socket in _sockets) socket.Dispose();
            _sockets.Clear();
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
        CutAll();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            Socket client;
            try
            {
                client = await _listener.AcceptSocketAsync(_cts.Token);
            }
            catch
            {
                return;
            }

            Interlocked.Increment(ref Connections);
            var server = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await server.ConnectAsync(_target);
            }
            catch
            {
                client.Dispose();
                server.Dispose();
                continue;
            }

            lock (_sockets)
            {
                _sockets.Add(client);
                _sockets.Add(server);
            }

            var cut = CutAfterBytes;
            CutAfterBytes = -1;
            _ = PumpAsync(client, server, cut);
            _ = PumpAsync(server, client, -2);
        }
    }

    private async Task PumpAsync(Socket from, Socket to, long cutAfter)
    {
        var buffer = new byte[64 * 1024];
        long total = 0;
        var limit = cutAfter == -2 ? 0 : BytesPerSecond;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            while (true)
            {
                var read = await from.ReceiveAsync(buffer, SocketFlags.None);
                if (read == 0) break;
                if (cutAfter >= 0 && total + read > cutAfter)
                {
                    from.Dispose();
                    to.Dispose();
                    return;
                }

                await to.SendAsync(buffer.AsMemory(0, read), SocketFlags.None);
                total += read;
                Interlocked.Add(ref BytesForwarded, read);
                if (limit > 0)
                {
                    var due = TimeSpan.FromSeconds((double)total / limit) - clock.Elapsed;
                    if (due > TimeSpan.Zero) await Task.Delay(due);
                }
            }
        }
        catch
        {
            // connection torn down
        }

        try { to.Shutdown(SocketShutdown.Send); } catch { /* ignore */ }
    }
}
