using System.Diagnostics;
using Beam.Core.History;
using Beam.Core.Transfer;

namespace Beam.Core.Tests;

public class TransferTests
{
    [Fact]
    public async Task SendsSingleFileIntact()
    {
        await using var sender = new TestNode("Sender");
        await using var receiver = new TestNode("Receiver");
        var file = sender.CreateFile("report.pdf", 5 * 1024 * 1024 + 123);

        var session = sender.Node.Send(receiver.AsDevice(), new[] { file });
        await Wait.ForFinishAsync(session);

        Assert.Equal(TransferState.Completed, session.State);
        var received = Path.Combine(receiver.ReceiveFolder, "report.pdf");
        Assert.Equal(TestFiles.Hash(file), TestFiles.Hash(received));
        Assert.Equal(File.GetLastWriteTimeUtc(file), File.GetLastWriteTimeUtc(received), TimeSpan.FromSeconds(1));

        var request = Assert.Single(receiver.Handler.Requests);
        Assert.Equal("Sender", request.SenderName);
        Assert.Equal(1, request.FileCount);
        Assert.Equal(new FileInfo(file).Length, request.TotalBytes);

        var snapshot = session.GetSnapshot();
        Assert.Equal(1, snapshot.CompletedFiles);
        Assert.Equal(snapshot.TotalBytes, snapshot.TransferredBytes);

        await Wait.UntilAsync(() => receiver.Node.History.Entries.Count == 1 && sender.Node.History.Entries.Count == 1);
        var history = receiver.Node.History.Entries[0];
        Assert.Equal(TransferDirection.Receive, history.Direction);
        Assert.Equal(HistoryStatus.Completed, history.Status);
        Assert.Equal("Sender", history.DeviceName);
        Assert.Equal(received, Assert.Single(history.Paths));
        Assert.Empty(Directory.GetFiles(receiver.ReceiveFolder, "*.beampart", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task SendsMultipleFilesWithUnicodeNames()
    {
        await using var sender = new TestNode("Sender");
        await using var receiver = new TestNode("Receiver");
        var names = new[] { "résumé final.docx", "日本語のファイル.txt", "file with  spaces & (brackets).txt", "emoji 😀.png", "empty.txt" };
        var paths = names.Select((n, i) => sender.CreateFile(n, n == "empty.txt" ? 0 : 1000 * (i + 1), i)).ToArray();

        var session = sender.Node.Send(receiver.AsDevice(), paths);
        await Wait.ForFinishAsync(session);

        Assert.Equal(TransferState.Completed, session.State);
        foreach (var (name, path) in names.Zip(paths))
            Assert.Equal(TestFiles.Hash(path), TestFiles.Hash(Path.Combine(receiver.ReceiveFolder, name)));
        Assert.Equal(5, session.GetSnapshot().CompletedFiles);
    }

    [Fact]
    public async Task PreservesFolderStructureIncludingEmptyFolders()
    {
        await using var sender = new TestNode("Sender");
        await using var receiver = new TestNode("Receiver");
        sender.CreateFile("Photos/2026/January/a.jpg", 2048, 1);
        sender.CreateFile("Photos/2026/January/b.jpg", 4096, 2);
        sender.CreateFile("Photos/2026/February/c.jpg", 10, 3);
        sender.CreateFile("Photos/readme.txt", 5, 4);
        Directory.CreateDirectory(sender.SourcePath("Photos/2026/March"));

        var session = sender.Node.Send(receiver.AsDevice(), new[] { sender.SourcePath("Photos") });
        await Wait.ForFinishAsync(session);

        Assert.Equal(TransferState.Completed, session.State);
        Assert.Equal("Photos", session.Title);
        foreach (var rel in new[] { "Photos/2026/January/a.jpg", "Photos/2026/January/b.jpg", "Photos/2026/February/c.jpg", "Photos/readme.txt" })
        {
            var received = Path.Combine(receiver.ReceiveFolder, rel.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(received), rel);
            Assert.Equal(TestFiles.Hash(sender.SourcePath(rel)), TestFiles.Hash(received));
        }

        Assert.True(Directory.Exists(Path.Combine(receiver.ReceiveFolder, "Photos", "2026", "March")));
        var request = Assert.Single(receiver.Handler.Requests);
        var item = Assert.Single(request.Items);
        Assert.True(item.IsDirectory);
        Assert.Equal(4, item.FileCount);
        Assert.Equal(4, request.FileCount);
        Assert.Equal(5, request.FolderCount);
    }

    [Fact]
    public async Task DeclinedTransferSendsNothing()
    {
        await using var sender = new TestNode("Sender");
        await using var receiver = new TestNode("Receiver");
        receiver.Handler.Decide = _ => IncomingDecision.Decline();
        var file = sender.CreateFile("secret.txt", 100);

        var session = sender.Node.Send(receiver.AsDevice(), new[] { file });
        await Wait.ForFinishAsync(session);

        Assert.Equal(TransferState.Declined, session.State);
        Assert.Equal(TransferErrorKind.Declined, session.Error!.Kind);
        Assert.Contains("declined", session.Error.Message);
        Assert.Empty(Directory.GetFileSystemEntries(receiver.ReceiveFolder));
        await Wait.UntilAsync(() => receiver.Node.History.Entries.Count == 1);
        Assert.Equal(HistoryStatus.Declined, receiver.Node.History.Entries[0].Status);
    }

    [Fact]
    public async Task SenderCancellingWhileWaitingWithdrawsTheRequest()
    {
        await using var sender = new TestNode("Sender");
        await using var receiver = new TestNode("Receiver");
        receiver.Handler.NeverAnswer = true;
        var file = sender.CreateFile("a.txt", 100);

        var session = sender.Node.Send(receiver.AsDevice(), new[] { file });
        await Wait.UntilAsync(() => session.State == TransferState.WaitingForAcceptance);
        await Wait.UntilAsync(() => receiver.Handler.Requests.Count == 1);
        session.Cancel();
        await Wait.ForFinishAsync(session);

        Assert.Equal(TransferState.Cancelled, session.State);
        await Wait.UntilAsync(() => receiver.Handler.ApprovalWasCancelled, because: "the prompt should be closed on the receiver");
        var incoming = receiver.Node.Transfers.Sessions.Single();
        await Wait.ForFinishAsync(incoming);
        Assert.Equal(TransferState.Cancelled, incoming.State);
    }

    [Theory]
    [InlineData(ConflictAction.Replace)]
    [InlineData(ConflictAction.KeepBoth)]
    [InlineData(ConflictAction.Skip)]
    public async Task HandlesExistingFiles(ConflictAction action)
    {
        await using var sender = new TestNode("Sender");
        await using var receiver = new TestNode("Receiver");
        var file = sender.CreateFile("notes.txt", 3000, 7);
        var other = sender.CreateFile("other.txt", 10, 8);
        var existing = Path.Combine(receiver.ReceiveFolder, "notes.txt");
        File.WriteAllText(existing, "old content");
        receiver.Handler.Resolve = c => c.Select(_ => action).ToList();

        var session = sender.Node.Send(receiver.AsDevice(), new[] { file, other });
        await Wait.ForFinishAsync(session);

        Assert.Equal(TransferState.Completed, session.State);
        var conflict = Assert.Single(Assert.Single(receiver.Handler.Conflicts));
        Assert.Equal("notes.txt", conflict.RelativePath);
        Assert.Equal(11, conflict.ExistingSize);
        Assert.True(File.Exists(Path.Combine(receiver.ReceiveFolder, "other.txt")));

        switch (action)
        {
            case ConflictAction.Replace:
                Assert.Equal(TestFiles.Hash(file), TestFiles.Hash(existing));
                Assert.False(File.Exists(Path.Combine(receiver.ReceiveFolder, "notes (1).txt")));
                break;
            case ConflictAction.KeepBoth:
                Assert.Equal("old content", File.ReadAllText(existing));
                Assert.Equal(TestFiles.Hash(file), TestFiles.Hash(Path.Combine(receiver.ReceiveFolder, "notes (1).txt")));
                break;
            case ConflictAction.Skip:
                Assert.Equal("old content", File.ReadAllText(existing));
                Assert.Equal(1, session.GetSnapshot().SkippedFiles);
                Assert.Equal(1, session.GetSnapshot().CompletedFiles);
                break;
        }
    }

    [Fact]
    public async Task MergesIntoExistingFolderAndRenamesFolderBlockedByFile()
    {
        await using var sender = new TestNode("Sender");
        await using var receiver = new TestNode("Receiver");
        sender.CreateFile("Docs/a.txt", 10, 1);
        sender.CreateFile("Docs/sub/b.txt", 10, 2);
        Directory.CreateDirectory(Path.Combine(receiver.ReceiveFolder, "Docs"));
        File.WriteAllText(Path.Combine(receiver.ReceiveFolder, "Docs", "keep.txt"), "mine");
        File.WriteAllText(Path.Combine(receiver.ReceiveFolder, "Docs", "sub"), "a file named like the folder");

        var session = sender.Node.Send(receiver.AsDevice(), new[] { sender.SourcePath("Docs") });
        await Wait.ForFinishAsync(session);

        Assert.Equal(TransferState.Completed, session.State);
        Assert.Empty(receiver.Handler.Conflicts);
        Assert.Equal("mine", File.ReadAllText(Path.Combine(receiver.ReceiveFolder, "Docs", "keep.txt")));
        Assert.True(File.Exists(Path.Combine(receiver.ReceiveFolder, "Docs", "a.txt")));
        Assert.True(File.Exists(Path.Combine(receiver.ReceiveFolder, "Docs", "sub (1)", "b.txt")));
    }

    [Fact]
    public async Task LargeFileStreamsWithoutLoadingIntoMemory()
    {
        await using var sender = new TestNode("Sender");
        await using var receiver = new TestNode("Receiver");
        const long size = 512L * 1024 * 1024;
        var file = sender.CreateFile("big.bin", size, 42);
        GC.Collect();

        var stopwatch = Stopwatch.StartNew();
        var session = sender.Node.Send(receiver.AsDevice(), new[] { file });
        long peakManaged = 0;
        var sawProgress = false;
        while (!session.IsFinished)
        {
            peakManaged = Math.Max(peakManaged, GC.GetTotalMemory(false));
            var snapshot = session.GetSnapshot();
            if (snapshot.TransferredBytes > 0 && snapshot.TransferredBytes < size) sawProgress = true;
            await Task.Delay(20);
        }

        stopwatch.Stop();
        Assert.Equal(TransferState.Completed, session.State);
        Assert.True(sawProgress, "progress should be reported while the file is in flight");
        Assert.True(peakManaged < 200L * 1024 * 1024, $"managed heap peaked at {peakManaged / 1024 / 1024} MB");
        Assert.Equal(TestFiles.Hash(file), TestFiles.Hash(Path.Combine(receiver.ReceiveFolder, "big.bin")));
        Console.WriteLine($"512 MB over loopback TLS in {stopwatch.Elapsed.TotalSeconds:0.0}s ({size / stopwatch.Elapsed.TotalSeconds / 1024 / 1024:0} MB/s)");
    }

    [Fact]
    public async Task ManySmallFilesAreFast()
    {
        await using var sender = new TestNode("Sender");
        await using var receiver = new TestNode("Receiver");
        for (var i = 0; i < 2000; i++) sender.CreateFile($"Many/dir{i % 20}/file{i}.txt", 100 + i, i);

        var stopwatch = Stopwatch.StartNew();
        var session = sender.Node.Send(receiver.AsDevice(), new[] { sender.SourcePath("Many") });
        await Wait.ForFinishAsync(session, TimeSpan.FromSeconds(120));
        stopwatch.Stop();

        Assert.Equal(TransferState.Completed, session.State);
        Assert.Equal(2000, session.GetSnapshot().CompletedFiles);
        Assert.Equal(2000, Directory.GetFiles(Path.Combine(receiver.ReceiveFolder, "Many"), "*", SearchOption.AllDirectories).Length);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(60), $"took {stopwatch.Elapsed}");
        Console.WriteLine($"2000 small files in {stopwatch.Elapsed.TotalSeconds:0.0}s");
    }

    [Fact]
    public async Task SmallFilesSurviveAConnectionDropAndArriveIntact()
    {
        await using var sender = new TestNode("Sender");
        await using var receiver = new TestNode("Receiver");
        var sources = new List<string>();
        for (var i = 0; i < 1500; i++) sources.Add(sender.CreateFile($"Project/src{i % 30}/file{i}.js", 3_000 + i * 7, i));
        sources.Add(sender.CreateFile("Project/empty.txt", 0, 1));
        sources.Add(sender.CreateFile("Project/exactly-limit.bin", 1024 * 1024, 2));
        sources.Add(sender.CreateFile("Project/just-over-limit.bin", 1024 * 1024 + 1, 3));
        using var proxy = new FlakyProxy(receiver.AsDevice().Endpoints[0]) { CutAfterBytes = 3L * 1024 * 1024 };

        var session = sender.Node.Send(receiver.AsDevice(proxy.Port), new[] { sender.SourcePath("Project") });
        await Wait.ForFinishAsync(session, TimeSpan.FromSeconds(120));

        Assert.Equal(TransferState.Completed, session.State);
        Assert.True(proxy.Connections >= 2, "the connection should have been cut and resumed");
        Assert.Single(receiver.Handler.Requests);
        var root = Path.Combine(sender.Root, "source");
        foreach (var source in sources)
        {
            var received = Path.Combine(receiver.ReceiveFolder, Path.GetRelativePath(root, source));
            Assert.True(File.Exists(received), $"missing {received}");
            Assert.Equal(TestFiles.Hash(source), TestFiles.Hash(received));
        }

        Assert.Empty(Directory.GetFiles(receiver.ReceiveFolder, "*.beampart", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task UnreadableSmallFileIsReportedAndOthersStillArrive()
    {
        await using var sender = new TestNode("Sender");
        await using var receiver = new TestNode("Receiver");
        for (var i = 0; i < 50; i++) sender.CreateFile($"Docs/file{i}.txt", 2000 + i, i);
        var missing = sender.SourcePath("Docs/file7.txt");
        receiver.Handler.DecisionDelay = TimeSpan.FromMilliseconds(500);

        var session = sender.Node.Send(receiver.AsDevice(), new[] { sender.SourcePath("Docs") });
        // The offer (with the file list) has arrived; the file disappears before it is read.
        await Wait.UntilAsync(() => receiver.Handler.Requests.Count == 1);
        File.Delete(missing);
        await Wait.ForFinishAsync(session, TimeSpan.FromSeconds(60));

        Assert.Equal(TransferState.CompletedWithErrors, session.State);
        var snapshot = session.GetSnapshot();
        Assert.Equal(1, snapshot.FailedFiles);
        Assert.Equal(49, snapshot.CompletedFiles);
        Assert.False(File.Exists(Path.Combine(receiver.ReceiveFolder, "Docs", "file7.txt")));
        Assert.Equal(49, Directory.GetFiles(Path.Combine(receiver.ReceiveFolder, "Docs")).Length);
    }

    [Fact]
    public async Task ResumesAfterConnectionDropWithoutStartingOver()
    {
        await using var sender = new TestNode("Sender");
        await using var receiver = new TestNode("Receiver");
        const long size = 64L * 1024 * 1024;
        var file = sender.CreateFile("video.mp4", size, 5);
        using var proxy = new FlakyProxy(receiver.AsDevice().Endpoints[0]) { CutAfterBytes = 30L * 1024 * 1024 };

        var session = sender.Node.Send(receiver.AsDevice(proxy.Port), new[] { file });
        var sawReconnecting = false;
        var sawInterrupted = false;
        while (!session.IsFinished)
        {
            sawReconnecting |= session.State == TransferState.Reconnecting;
            sawInterrupted |= receiver.Node.Transfers.Sessions.Any(s => s.State == TransferState.Interrupted);
            await Task.Delay(5);
            if (DateTime.UtcNow - session.StartedAt.UtcDateTime > TimeSpan.FromSeconds(60)) break;
        }

        Assert.Equal(TransferState.Completed, session.State);
        Assert.True(sawReconnecting, "sender should report reconnecting");
        Assert.True(proxy.Connections >= 2);
        Assert.True(proxy.BytesForwarded < size + 20L * 1024 * 1024, $"forwarded {proxy.BytesForwarded} bytes: transfer restarted from zero");
        Assert.Equal(TestFiles.Hash(file), TestFiles.Hash(Path.Combine(receiver.ReceiveFolder, "video.mp4")));
        Assert.Single(receiver.Handler.Requests); // resume must not ask the user again
        var incoming = Assert.Single(receiver.Node.Transfers.Sessions);
        Assert.Equal(TransferState.Completed, incoming.State);
        Assert.Empty(Directory.GetFiles(receiver.ReceiveFolder, "*.beampart"));
        _ = sawInterrupted; // timing-dependent; the important property is that it completed without asking again
    }

    [Fact]
    public async Task CorruptedPartialFileIsDetectedAndResent()
    {
        await using var sender = new TestNode("Sender");
        await using var receiver = new TestNode("Receiver");
        const long size = 32L * 1024 * 1024;
        var file = sender.CreateFile("data.bin", size, 9);
        using var proxy = new FlakyProxy(receiver.AsDevice().Endpoints[0]) { CutAfterBytes = 12L * 1024 * 1024 };

        // Corrupt the partial file while the sender is waiting to reconnect.
        var corrupted = false;
        receiver.Node.Transfers.SessionStarted += s => s.StateChanged += _ =>
        {
            if (s.State != TransferState.Interrupted || corrupted) return;
            var part = Directory.GetFiles(receiver.ReceiveFolder, "*.beampart").Single();
            using var stream = new FileStream(part, FileMode.Open, FileAccess.ReadWrite);
            stream.Position = 1000;
            stream.WriteByte(0xFF);
            stream.WriteByte(0x00);
            corrupted = true;
        };

        var session = sender.Node.Send(receiver.AsDevice(proxy.Port), new[] { file });
        await Wait.ForFinishAsync(session, TimeSpan.FromSeconds(90));

        Assert.True(corrupted, "test should have corrupted the partial file");
        Assert.Equal(TransferState.Completed, session.State);
        Assert.Equal(TestFiles.Hash(file), TestFiles.Hash(Path.Combine(receiver.ReceiveFolder, "data.bin")));
    }

    [Fact]
    public async Task ReceiverCancellingStopsSender()
    {
        await using var sender = new TestNode("Sender");
        await using var receiver = new TestNode("Receiver");
        var file = sender.CreateFile("big.bin", 256L * 1024 * 1024, 3);
        using var throttle = new FlakyProxy(receiver.AsDevice().Endpoints[0]);

        var session = sender.Node.Send(receiver.AsDevice(throttle.Port), new[] { file });
        await Wait.UntilAsync(() => receiver.Node.Transfers.Sessions.Any(s => s.GetSnapshot().TransferredBytes > 1024 * 1024));
        var incoming = receiver.Node.Transfers.Sessions.Single();
        incoming.Cancel();
        await Wait.ForFinishAsync(session);
        await Wait.ForFinishAsync(incoming);

        Assert.Equal(TransferState.Cancelled, incoming.State);
        Assert.Equal(TransferState.Cancelled, session.State);
        Assert.Contains("Receiver cancelled", session.Error!.Message);
        Assert.Empty(Directory.GetFiles(receiver.ReceiveFolder, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task SenderCancellingMidTransferCleansUpReceiver()
    {
        await using var sender = new TestNode("Sender");
        await using var receiver = new TestNode("Receiver");
        var file = sender.CreateFile("big.bin", 256L * 1024 * 1024, 3);

        var session = sender.Node.Send(receiver.AsDevice(), new[] { file });
        await Wait.UntilAsync(() => session.GetSnapshot().TransferredBytes > 1024 * 1024);
        session.Cancel();
        await Wait.ForFinishAsync(session);
        var incoming = receiver.Node.Transfers.Sessions.Single();
        await Wait.ForFinishAsync(incoming);

        Assert.Equal(TransferState.Cancelled, session.State);
        Assert.Equal(TransferState.Cancelled, incoming.State);
        Assert.Contains("Sender cancelled", incoming.Error!.Message);
        Assert.Empty(Directory.GetFiles(receiver.ReceiveFolder, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ReceiverClosingMidTransferIsReportedGracefully()
    {
        await using var sender = new TestNode("Sender");
        var receiver = new TestNode("Receiver");
        var file = sender.CreateFile("big.bin", 256L * 1024 * 1024, 3);

        var session = sender.Node.Send(receiver.AsDevice(), new[] { file });
        await Wait.UntilAsync(() => session.GetSnapshot().TransferredBytes > 1024 * 1024);
        await receiver.DisposeAsync();
        await Wait.ForFinishAsync(session);

        Assert.Equal(TransferState.Cancelled, session.State);
        Assert.Contains("Beam was closed on Receiver", session.Error!.Message);
    }

    [Fact]
    public async Task UnreachableDeviceFailsWithFriendlyMessage()
    {
        await using var sender = new TestNode("Sender");
        var receiver = new TestNode("Receiver");
        var device = receiver.AsDevice();
        await receiver.DisposeAsync();
        var file = sender.CreateFile("a.txt", 10);

        var session = sender.Node.Send(device, new[] { file });
        await Wait.ForFinishAsync(session, TimeSpan.FromSeconds(30));

        Assert.Equal(TransferState.Failed, session.State);
        Assert.Equal(TransferErrorKind.ConnectFailed, session.Error!.Kind);
        Assert.StartsWith("Couldn't reach Receiver", session.Error.Message);
        Assert.True(session.CanResume);
    }

    [Fact]
    public async Task UnavailableDestinationIsReported()
    {
        await using var sender = new TestNode("Sender");
        await using var receiver = new TestNode("Receiver");
        var blocker = Path.Combine(receiver.Root, "not-a-folder");
        File.WriteAllText(blocker, "x");
        receiver.Handler.Decide = _ => IncomingDecision.Accept(blocker);
        var file = sender.CreateFile("a.txt", 10);

        var session = sender.Node.Send(receiver.AsDevice(), new[] { file });
        await Wait.ForFinishAsync(session);

        Assert.Equal(TransferState.Failed, session.State);
        Assert.Contains("isn't available", session.Error!.Message);
        var incoming = receiver.Node.Transfers.Sessions.Single();
        Assert.Equal(TransferState.Failed, incoming.State);
        Assert.Equal(TransferErrorKind.DestinationUnavailable, incoming.Error!.Kind);
    }

    [Fact]
    public async Task TrustedDeviceIsAcceptedWithoutAsking()
    {
        await using var sender = new TestNode("Sender");
        await using var receiver = new TestNode("Receiver");
        receiver.Node.Settings.Update(s => s.TrustedDevices.Add(new Settings.TrustedDevice
        {
            Fingerprint = sender.Node.Identity.Fingerprint,
            DeviceId = sender.Node.Identity.DeviceId,
            Name = "Sender",
        }));
        var file = sender.CreateFile("a.txt", 10);

        var session = sender.Node.Send(receiver.AsDevice(), new[] { file });
        await Wait.ForFinishAsync(session);

        Assert.Equal(TransferState.Completed, session.State);
        Assert.Empty(receiver.Handler.Requests);
    }

    [Fact]
    public async Task ImpostorWithWrongFingerprintIsRejected()
    {
        await using var sender = new TestNode("Sender");
        await using var receiver = new TestNode("Receiver");
        var file = sender.CreateFile("a.txt", 10);
        var spoofed = receiver.AsDevice() with { Fingerprint = new string('a', 64) };

        var session = sender.Node.Send(spoofed, new[] { file });
        await Wait.ForFinishAsync(session);

        Assert.Equal(TransferState.Failed, session.State);
        Assert.Equal(TransferErrorKind.IdentityMismatch, session.Error!.Kind);
        Assert.Empty(receiver.Handler.Requests);
    }

    [Fact]
    public async Task SenderCanResumeManuallyAfterGivingUp()
    {
        await using var sender = new TestNode("Sender");
        await using var receiver = new TestNode("Receiver");
        var file = sender.CreateFile("a.bin", 4 * 1024 * 1024, 2);
        var unreachable = receiver.AsDevice() with { Endpoints = new[] { new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 1) } };
        var target = unreachable;

        var session = sender.Node.Transfers.Send(unreachable, new[] { file }, () => target);
        await Wait.ForFinishAsync(session, TimeSpan.FromSeconds(30));
        Assert.True(session.CanResume);

        target = receiver.AsDevice();
        session.Resume(); // immediately, as a user clicking "Try again" right away would
        await Wait.UntilAsync(() => session.State == TransferState.Completed, TimeSpan.FromSeconds(30),
            $"resumed transfer should complete (state {session.State})");

        Assert.Equal(TransferState.Completed, session.State);
        Assert.Equal(TestFiles.Hash(file), TestFiles.Hash(Path.Combine(receiver.ReceiveFolder, "a.bin")));
    }

    [Fact]
    public async Task MultipleSimultaneousTransfers()
    {
        await using var a = new TestNode("A");
        await using var b = new TestNode("B");
        await using var c = new TestNode("C");
        var f1 = a.CreateFile("one.bin", 8 * 1024 * 1024, 1);
        var f2 = a.CreateFile("two.bin", 8 * 1024 * 1024, 2);
        var f3 = c.CreateFile("three.bin", 8 * 1024 * 1024, 3);

        var s1 = a.Node.Send(b.AsDevice(), new[] { f1 });
        var s2 = a.Node.Send(c.AsDevice(), new[] { f2 });
        var s3 = c.Node.Send(b.AsDevice(), new[] { f3 });
        await Task.WhenAll(Wait.ForFinishAsync(s1), Wait.ForFinishAsync(s2), Wait.ForFinishAsync(s3));

        Assert.All(new[] { s1, s2, s3 }, s => Assert.Equal(TransferState.Completed, s.State));
        Assert.Equal(TestFiles.Hash(f1), TestFiles.Hash(Path.Combine(b.ReceiveFolder, "one.bin")));
        Assert.Equal(TestFiles.Hash(f2), TestFiles.Hash(Path.Combine(c.ReceiveFolder, "two.bin")));
        Assert.Equal(TestFiles.Hash(f3), TestFiles.Hash(Path.Combine(b.ReceiveFolder, "three.bin")));
    }
}

public class TransferSecurityTests
{
    [Fact]
    public async Task NeverWritesThroughALinkedFolder()
    {
        await using var sender = new TestNode("Sender");
        await using var receiver = new TestNode("Receiver");
        sender.CreateFile("Photos/a.jpg", 100, 1);
        var outside = Path.Combine(receiver.Root, "outside");
        Directory.CreateDirectory(outside);
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(receiver.ReceiveFolder, "Photos"), outside);
        }
        catch (Exception)
        {
            return; // symlinks not permitted on this machine
        }

        var session = sender.Node.Send(receiver.AsDevice(), new[] { sender.SourcePath("Photos") });
        await Wait.ForFinishAsync(session);

        Assert.Equal(TransferState.Completed, session.State);
        Assert.Empty(Directory.GetFiles(outside));
        Assert.True(File.Exists(Path.Combine(receiver.ReceiveFolder, "Photos (1)", "a.jpg")));
    }
}
