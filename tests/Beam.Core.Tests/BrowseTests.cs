using Beam.Core.Browse;
using Beam.Core.Direct;
using Beam.Core.Discovery;
using Beam.Core.Nearby;
using Beam.Core.Settings;
using Beam.Core.Transfer;

namespace Beam.Core.Tests;

/// <summary>Adding trusted devices with a code, and browsing each other's shared folders: who may, what they see, what they may do.</summary>
[Collection(nameof(BrowseTests))] // changes BrowseService's and TrustService's timings
public class BrowseTests
{
    private static TestNode Pc(string name = "Office PC") => new(name);

    private static TestNode Phone(string name = "Pixel") => new(name, kind: DeviceKinds.Phone);

    /// <summary>The code as the other device reads it from the QR code (reachable on loopback in tests).</summary>
    private static PairingCode Scan(TestNode shower, PairingCode code)
    {
        code.Addresses = new List<string> { $"127.0.0.1:{shower.Node.Transfers.Port}" };
        return PairingCode.TryParse(code.AppendTo(null)) ?? throw new InvalidOperationException("The code doesn't parse.");
    }

    /// <summary>Both trust each other: <paramref name="scanner"/> scans <paramref name="shower"/>'s code.</summary>
    private static Task<TrustedDevice> TrustAsync(TestNode scanner, TestNode shower) =>
        scanner.Node.Trust.AddAsync(Scan(shower, shower.Node.Trust.CreateCode()), CancellationToken.None);

    /// <summary>Shares a new folder on <paramref name="node"/> with a few files in it, and turns browsing on.</summary>
    private static (string Folder, string RootId) Share(TestNode node, string name = "Photos")
    {
        var folder = Path.Combine(node.Root, "shared-" + name);
        Directory.CreateDirectory(Path.Combine(folder, "Trip"));
        TestFiles.Write(Path.Combine(folder, "a.jpg"), 300_000, 1);
        TestFiles.Write(Path.Combine(folder, "Trip", "b.jpg"), 1_200_000, 2);
        File.WriteAllText(Path.Combine(folder, ".secret"), "hidden");
        node.Node.Browse.SetEnabled(true);
        node.Node.Browse.AddFolder(folder, name);
        return (folder, node.Node.Settings.Current.SharedFolders.Single(f => f.Path == folder).Id);
    }

    private static async Task<TransferException> RefusedAsync(TestNode visitor, TestNode host) =>
        await Assert.ThrowsAsync<TransferException>(() => visitor.Node.Browse.OpenAsync(host.AsDevice(), CancellationToken.None));

    // ---------------------------------------------------------------- trusted devices

    [Fact]
    public async Task ScanningTheCodeMakesBothTrustEachOther()
    {
        await using var pc = Pc();
        await using var phone = Phone();
        TrustedDevice? added = null;
        pc.Node.Trust.Added += d => added = d;

        var device = await TrustAsync(phone, pc);

        Assert.Equal(pc.Node.Identity.DeviceId, device.DeviceId);
        Assert.Equal("Office PC", device.Name);
        var onPhone = Assert.Single(phone.Node.Settings.Current.TrustedDevices);
        Assert.Equal(pc.Node.Identity.Fingerprint, onPhone.Fingerprint, ignoreCase: true);
        var onPc = Assert.Single(pc.Node.Settings.Current.TrustedDevices);
        Assert.Equal(phone.Node.Identity.Fingerprint, onPc.Fingerprint, ignoreCase: true);
        Assert.Equal("Pixel", onPc.Name);
        Assert.NotNull(added);
        Assert.False(onPc.AlwaysBrowse);
    }

    [Fact]
    public async Task TheCodeWorksOnceAndOnlyWhileShown()
    {
        await using var pc = Pc();
        await using var phone = Phone();
        await using var guest = Phone("Guest");
        var code = pc.Node.Trust.CreateCode();
        await phone.Node.Trust.AddAsync(Scan(pc, code), CancellationToken.None);

        // Photographed afterwards: used up.
        var ex = await Assert.ThrowsAsync<TransferException>(() => guest.Node.Trust.AddAsync(Scan(pc, code), CancellationToken.None));
        Assert.Equal(TransferErrorKind.Declined, ex.Error.Kind);

        // Closed before it was used.
        var closed = pc.Node.Trust.CreateCode();
        pc.Node.Trust.CancelCode();
        await Assert.ThrowsAsync<TransferException>(() => guest.Node.Trust.AddAsync(Scan(pc, closed), CancellationToken.None));

        // A guessed token while a real code is shown.
        pc.Node.Trust.CreateCode();
        var guessed = pc.Node.CreatePairingCode();
        guessed.TrustToken = "guessed";
        await Assert.ThrowsAsync<TransferException>(() => guest.Node.Trust.AddAsync(Scan(pc, guessed), CancellationToken.None));

        Assert.Single(pc.Node.Settings.Current.TrustedDevices); // only the phone
        Assert.Empty(guest.Node.Settings.Current.TrustedDevices);
    }

    [Fact]
    public async Task GuessingClosesTheCode()
    {
        await using var pc = Pc();
        await using var phone = Phone();
        await using var guest = Phone("Guest");
        var real = pc.Node.Trust.CreateCode();
        for (var i = 0; i < 5; i++)
        {
            var guessed = pc.Node.CreatePairingCode();
            guessed.TrustToken = "guess-" + i;
            await Assert.ThrowsAsync<TransferException>(() => guest.Node.Trust.AddAsync(Scan(pc, guessed), CancellationToken.None));
        }

        // Even the real code doesn't work any more: a new one has to be shown.
        await Assert.ThrowsAsync<TransferException>(() => phone.Node.Trust.AddAsync(Scan(pc, real), CancellationToken.None));
        Assert.Empty(pc.Node.Settings.Current.TrustedDevices);
    }

    [Fact]
    public async Task AnImpostorAtTheCodesAddressIsRefused()
    {
        await using var pc = Pc();
        await using var impostor = Pc("Office PC");
        await using var phone = Phone();
        // The code says the PC's certificate; the address leads to another device.
        var code = Scan(impostor, pc.Node.Trust.CreateCode());
        impostor.Node.Trust.CreateCode();

        await Assert.ThrowsAsync<TransferException>(() => phone.Node.Trust.AddAsync(code, CancellationToken.None));
        Assert.Empty(phone.Node.Settings.Current.TrustedDevices);
        Assert.Empty(impostor.Node.Settings.Current.TrustedDevices);
    }

    [Fact]
    public async Task OtherCodesAndOwnCodeAreRefused()
    {
        await using var pc = Pc();
        await using var phone = Phone();
        await Assert.ThrowsAsync<TransferException>(() => phone.Node.Trust.AddAsync(Scan(pc, pc.Node.CreatePairingCode()), CancellationToken.None));
        await Assert.ThrowsAsync<TransferException>(() => pc.Node.Trust.AddAsync(Scan(pc, pc.Node.Trust.CreateCode()), CancellationToken.None));
    }

    // ---------------------------------------------------------------- who may browse

    [Fact]
    public async Task OnlyTrustedDevicesMayBrowse()
    {
        await using var pc = Pc();
        await using var phone = Phone();
        Share(pc);

        // The phone doesn't trust the PC: it doesn't even try.
        await RefusedAsync(phone, pc);

        // The phone trusts the PC (one-sided, "Always accept"), but the PC doesn't trust the phone.
        phone.Trust(pc);
        var ex = await RefusedAsync(phone, pc);
        Assert.Equal(TransferErrorKind.Declined, ex.Error.Kind);
        Assert.Empty(pc.Handler.BrowseRequests); // nobody was even asked
        Assert.Empty(pc.Node.Browse.Visits);
    }

    [Fact]
    public async Task BrowsingOffRefusesWithoutAsking()
    {
        await using var pc = Pc();
        await using var phone = Phone();
        await TrustAsync(phone, pc);

        var ex = await RefusedAsync(phone, pc);
        Assert.Contains("off", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(pc.Handler.BrowseRequests);
    }

    [Fact]
    public async Task EachVisitIsAskedAboutUnlessAlwaysAllowed()
    {
        await using var pc = Pc();
        await using var phone = Phone();
        await TrustAsync(phone, pc);
        Share(pc);

        // Declined.
        pc.Handler.DecideBrowse = _ => BrowseDecision.Deny;
        await RefusedAsync(phone, pc);
        Assert.Single(pc.Handler.BrowseRequests);
        Assert.Equal("Pixel", pc.Handler.BrowseRequests[0].DeviceName);

        // Allowed this once: asked again next time.
        pc.Handler.DecideBrowse = _ => BrowseDecision.Allow;
        await using (var visit = await phone.Node.Browse.OpenAsync(pc.AsDevice(), CancellationToken.None)) Assert.Single(visit.Roots);
        Assert.Equal(2, pc.Handler.BrowseRequests.Count);

        // "Always allow Pixel to browse my files".
        pc.Handler.DecideBrowse = _ => BrowseDecision.AllowAlways;
        await using (await phone.Node.Browse.OpenAsync(pc.AsDevice(), CancellationToken.None)) { }
        Assert.True(pc.Node.Settings.Current.TrustedDevices.Single().AlwaysBrowse);
        await using (await phone.Node.Browse.OpenAsync(pc.AsDevice(), CancellationToken.None)) { }
        Assert.Equal(3, pc.Handler.BrowseRequests.Count); // not asked the last time

        // "Ask again".
        pc.Node.Browse.Forget(phone.Node.Identity.Fingerprint);
        pc.Handler.DecideBrowse = _ => BrowseDecision.Deny;
        await RefusedAsync(phone, pc);
        Assert.Equal(4, pc.Handler.BrowseRequests.Count);
    }

    [Fact]
    public async Task NobodyAnsweringMeansNo()
    {
        var before = BrowseService.ApprovalTimeout;
        BrowseService.ApprovalTimeout = TimeSpan.FromMilliseconds(500);
        try
        {
            await using var pc = Pc();
            await using var phone = Phone();
            await TrustAsync(phone, pc);
            Share(pc);
            pc.Handler.NeverAnswerBrowse = true;

            await RefusedAsync(phone, pc);
            Assert.Empty(pc.Node.Browse.Visits);
        }
        finally
        {
            BrowseService.ApprovalTimeout = before;
        }
    }

    [Fact]
    public async Task NoLongerTrustedEndsTheVisit()
    {
        await using var pc = Pc();
        await using var phone = Phone();
        await TrustAsync(phone, pc);
        var (_, root) = Share(pc);
        await using var visit = await phone.Node.Browse.OpenAsync(pc.AsDevice(), CancellationToken.None);
        Assert.NotEmpty(await visit.ListAsync(root, "", CancellationToken.None));

        pc.Node.Trust.Remove(phone.Node.Identity.Fingerprint);

        await Assert.ThrowsAnyAsync<Exception>(() => visit.ListAsync(root, "", CancellationToken.None));
        await Wait.UntilAsync(() => pc.Node.Browse.Visits.Count == 0);
    }

    [Fact]
    public async Task StoppingAVisitKeepsItOut()
    {
        await using var pc = Pc();
        await using var phone = Phone();
        await TrustAsync(phone, pc);
        var (_, root) = Share(pc);
        pc.Handler.DecideBrowse = _ => BrowseDecision.AllowAlways;
        await using var visit = await phone.Node.Browse.OpenAsync(pc.AsDevice(), CancellationToken.None);
        await Wait.UntilAsync(() => pc.Node.Browse.Visits.Count == 1);

        pc.Node.Browse.Stop(pc.Node.Browse.Visits.Single());

        // Even "always allowed", reconnecting by itself doesn't get back in.
        var ex = await Assert.ThrowsAsync<TransferException>(() => visit.ListAsync(root, "", CancellationToken.None));
        Assert.Contains("ended", ex.Message);
        await Wait.UntilAsync(() => pc.Node.Browse.Visits.Count == 0);
    }

    [Fact]
    public async Task TurningBrowsingOffEndsVisits()
    {
        await using var pc = Pc();
        await using var phone = Phone();
        await TrustAsync(phone, pc);
        var (_, root) = Share(pc);
        await using var visit = await phone.Node.Browse.OpenAsync(pc.AsDevice(), CancellationToken.None);

        pc.Node.Browse.SetEnabled(false);

        await Wait.UntilAsync(() => pc.Node.Browse.Visits.Count == 0);
        await Assert.ThrowsAsync<TransferException>(() => visit.ListAsync(root, "", CancellationToken.None));
    }

    // ---------------------------------------------------------------- what can be seen

    [Fact]
    public async Task OnlySharedFoldersWithoutHiddenFiles()
    {
        await using var pc = Pc();
        await using var phone = Phone();
        await TrustAsync(phone, pc);
        var (folder, root) = Share(pc);
        File.WriteAllText(Path.Combine(folder, ".photo.jpg.1234.beam-part"), "unfinished");
        Directory.CreateDirectory(Path.Combine(folder, ".thumbnails"));
        await using var visit = await phone.Node.Browse.OpenAsync(pc.AsDevice(), CancellationToken.None);

        var root1 = Assert.Single(visit.Roots);
        Assert.Equal("Photos", root1.Name);
        Assert.DoesNotContain(pc.Root, root1.Id); // the path itself is never told
        var items = await visit.ListAsync(root, "", CancellationToken.None);
        Assert.Equal(new[] { "Trip", "a.jpg" }, items.Select(i => i.Name));
        Assert.True(items[0].IsFolder);
        Assert.Equal(300_000, items[1].Size);
        var inside = await visit.ListAsync(root, "Trip", CancellationToken.None);
        Assert.Equal("Trip/b.jpg", Assert.Single(inside).Path);
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../..")]
    [InlineData("Trip/../..")]
    [InlineData("/etc")]
    [InlineData("C:\\Windows")]
    [InlineData("Trip\\..\\..")]
    [InlineData(".secret")]
    [InlineData("Trip//b.jpg")]
    [InlineData("./Trip")]
    [InlineData("nope")]
    public async Task PathsOutsideTheSharedFolderAreRefused(string path)
    {
        await using var pc = Pc();
        await using var phone = Phone();
        await TrustAsync(phone, pc);
        var (_, root) = Share(pc);
        await using var visit = await phone.Node.Browse.OpenAsync(pc.AsDevice(), CancellationToken.None);

        await Assert.ThrowsAsync<TransferException>(() => visit.ListAsync(root, path, CancellationToken.None));
        // The visit carries on.
        Assert.NotEmpty(await visit.ListAsync(root, "", CancellationToken.None));
    }

    [Fact]
    public void ResolveAcceptsOnlyPlainPathsInsideSharedFolders()
    {
        using var dir = new TempDir();
        var shared = dir.Combine("shared");
        Directory.CreateDirectory(Path.Combine(shared, "Trip"));
        File.WriteAllText(Path.Combine(shared, "Trip", "b.jpg"), "x");
        File.WriteAllText(dir.Combine("outside.txt"), "x");
        var settings = new AppSettings { SharedFolders = { new SharedFolder { Id = "r1", Name = "Shared", Path = shared } } };

        Assert.Equal(Path.Combine(shared, "Trip", "b.jpg"), BrowseService.Resolve(settings, "r1", "Trip/b.jpg"));
        Assert.Equal(Path.GetFullPath(shared), BrowseService.Resolve(settings, "r1", ""));
        Assert.Null(BrowseService.Resolve(settings, "r2", "Trip")); // not shared
        Assert.Null(BrowseService.Resolve(settings, null, ""));
        Assert.Null(BrowseService.Resolve(settings, "r1", "../outside.txt"));
        Assert.Null(BrowseService.Resolve(settings, "r1", "Trip/\0"));
        Assert.Null(BrowseService.Resolve(settings, "r1", "Trip/b.jpg\n"));
        Assert.Null(BrowseService.Resolve(settings, "r1", string.Join('/', Enumerable.Repeat("a", 500))));
    }

    [Fact]
    public async Task LinksLeadingOutAreNeitherListedNorFollowed()
    {
        if (OperatingSystem.IsWindows()) return; // links need extra rights there
        await using var pc = Pc();
        await using var phone = Phone();
        await TrustAsync(phone, pc);
        var (folder, root) = Share(pc);
        var outside = Path.Combine(pc.Root, "private");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "passwords.txt"), "secret");
        Directory.CreateSymbolicLink(Path.Combine(folder, "Escape"), outside);
        File.CreateSymbolicLink(Path.Combine(folder, "passwords.txt"), Path.Combine(outside, "passwords.txt"));
        await using var visit = await phone.Node.Browse.OpenAsync(pc.AsDevice(), CancellationToken.None);

        var items = await visit.ListAsync(root, "", CancellationToken.None);
        Assert.DoesNotContain(items, i => i.Name is "Escape" or "passwords.txt");
        await Assert.ThrowsAsync<TransferException>(() => visit.ListAsync(root, "Escape", CancellationToken.None));

        // Asked for by name anyway: not downloaded.
        var forged = new BrowseItem(root, "passwords.txt", "passwords.txt", false, 6, DateTimeOffset.UtcNow);
        var session = visit.Download(new[] { forged });
        await Wait.ForFinishAsync(session);
        Assert.NotEqual(TransferState.Completed, session.State);
        Assert.False(File.Exists(Path.Combine(phone.ReceiveFolder, "passwords.txt")));
    }

    [Fact]
    public async Task AnUnsharedFolderIsGoneAtOnce()
    {
        await using var pc = Pc();
        await using var phone = Phone();
        await TrustAsync(phone, pc);
        var (_, root) = Share(pc);
        await using var visit = await phone.Node.Browse.OpenAsync(pc.AsDevice(), CancellationToken.None);
        Assert.NotEmpty(await visit.ListAsync(root, "", CancellationToken.None));

        pc.Node.Browse.RemoveFolder(root);

        await Assert.ThrowsAsync<TransferException>(() => visit.ListAsync(root, "", CancellationToken.None));
    }

    [Fact]
    public async Task TheFirstTimeOnSharesTheUsualFolders()
    {
        await using var pc = Pc();
        var pictures = Path.Combine(pc.Root, "Pictures");
        Directory.CreateDirectory(pictures);
        pc.DefaultSharedFolders.Add(("Pictures", pictures));
        pc.DefaultSharedFolders.Add(("Gone", Path.Combine(pc.Root, "missing"))); // not there: not shared
        Assert.False(pc.Node.Settings.Current.BrowsingEnabled);
        pc.Node.Browse.SetEnabled(true);
        Assert.True(pc.Node.Settings.Current.SharedFoldersSetUp);
        var first = Assert.Single(pc.Node.Settings.Current.SharedFolders);
        Assert.Equal("Pictures", first.Name);
        Assert.Equal(pictures, first.Path);

        // Removed by the user: not added back when switched off and on.
        foreach (var folder in pc.Node.Settings.Current.SharedFolders.ToList()) pc.Node.Browse.RemoveFolder(folder.Id);
        pc.Node.Browse.SetEnabled(false);
        pc.Node.Browse.SetEnabled(true);
        Assert.Empty(pc.Node.Settings.Current.SharedFolders);
    }

    // ---------------------------------------------------------------- downloading and adding files

    [Fact]
    public async Task DownloadsFilesAndFoldersIntact()
    {
        await using var pc = Pc();
        await using var phone = Phone();
        await TrustAsync(phone, pc);
        var (folder, root) = Share(pc);
        pc.Handler.DecideBrowse = _ => BrowseDecision.Allow;
        await using var visit = await phone.Node.Browse.OpenAsync(pc.AsDevice(), CancellationToken.None);
        var items = await visit.ListAsync(root, "", CancellationToken.None);

        var session = visit.Download(items);
        await Wait.ForFinishAsync(session);

        Assert.Equal(TransferState.Completed, session.State);
        Assert.Equal(TestFiles.Hash(Path.Combine(folder, "a.jpg")), TestFiles.Hash(Path.Combine(phone.ReceiveFolder, "a.jpg")));
        Assert.Equal(TestFiles.Hash(Path.Combine(folder, "Trip", "b.jpg")), TestFiles.Hash(Path.Combine(phone.ReceiveFolder, "Trip", "b.jpg")));
        Assert.Empty(Directory.GetFiles(phone.ReceiveFolder, "*.beam-part", SearchOption.AllDirectories));
        Assert.Single(pc.Handler.BrowseRequests); // the download's own connection carried on the visit: not asked again
        Assert.Contains(phone.Node.Transfers.Sessions, s => s.Id == session.Id);

        // Again: kept side by side, nothing overwritten.
        var again = visit.Download(new[] { items.Single(i => i.Name == "a.jpg") });
        await Wait.ForFinishAsync(again);
        Assert.True(File.Exists(Path.Combine(phone.ReceiveFolder, "a (1).jpg")));
    }

    [Fact]
    public async Task ADroppedDownloadCarriesOnWhereItWas()
    {
        await using var pc = Pc();
        await using var phone = Phone();
        await TrustAsync(phone, pc);
        var (folder, root) = Share(pc);
        await using var visit = await phone.Node.Browse.OpenAsync(pc.AsDevice(), CancellationToken.None);
        var item = (await visit.ListAsync(root, "Trip", CancellationToken.None)).Single();
        BrowseService.ReadPause = TimeSpan.FromMilliseconds(100);
        try
        {
            var session = visit.Download(new[] { item });
            await Wait.UntilAsync(() => session.GetSnapshot().TransferredBytes > 0);
            pc.Node.Browse.DropAll(); // the Wi-Fi went
            BrowseService.ReadPause = TimeSpan.Zero;
            await Wait.ForFinishAsync(session);

            Assert.Equal(TransferState.Completed, session.State);
            Assert.Equal(TestFiles.Hash(Path.Combine(folder, "Trip", "b.jpg")), TestFiles.Hash(Path.Combine(phone.ReceiveFolder, "b.jpg")));
            Assert.Equal(item.Size, session.GetSnapshot().TransferredBytes); // carried on: nothing fetched twice
            Assert.Single(pc.Handler.BrowseRequests); // and not asked again
        }
        finally
        {
            BrowseService.ReadPause = TimeSpan.Zero;
        }
    }

    [Fact]
    public async Task ClosingBrowsingLetsStartedDownloadsFinish()
    {
        await using var pc = Pc();
        await using var phone = Phone();
        await TrustAsync(phone, pc);
        var (folder, root) = Share(pc);
        var visit = await phone.Node.Browse.OpenAsync(pc.AsDevice(), CancellationToken.None);
        var items = await visit.ListAsync(root, "", CancellationToken.None);
        BrowseService.ReadPause = TimeSpan.FromMilliseconds(50);
        try
        {
            var session = visit.Download(items); // a folder too: listed by the download itself
            await visit.DisposeAsync(); // left the Browse page at once

            await Wait.ForFinishAsync(session);
            Assert.Equal(TransferState.Completed, session.State);
            Assert.Equal(TestFiles.Hash(Path.Combine(folder, "Trip", "b.jpg")), TestFiles.Hash(Path.Combine(phone.ReceiveFolder, "Trip", "b.jpg")));
            await Wait.UntilAsync(() => pc.Node.Browse.Visits.Count == 0, because: "everything closed afterwards");
        }
        finally
        {
            BrowseService.ReadPause = TimeSpan.Zero;
        }
    }

    [Fact]
    public async Task ACancelledDownloadLeavesNothing()
    {
        await using var pc = Pc();
        await using var phone = Phone();
        await TrustAsync(phone, pc);
        var (_, root) = Share(pc);
        await using var visit = await phone.Node.Browse.OpenAsync(pc.AsDevice(), CancellationToken.None);
        var item = (await visit.ListAsync(root, "Trip", CancellationToken.None)).Single();
        BrowseService.ReadPause = TimeSpan.FromMilliseconds(100);
        try
        {
            var session = visit.Download(new[] { item });
            await Wait.UntilAsync(() => session.GetSnapshot().TransferredBytes > 0);
            session.Cancel();
            await Wait.ForFinishAsync(session);

            Assert.Equal(TransferState.Cancelled, session.State);
            await Wait.UntilAsync(() => Directory.GetFiles(phone.ReceiveFolder, "*", SearchOption.AllDirectories).Length == 0,
                because: "the part file is removed");
        }
        finally
        {
            BrowseService.ReadPause = TimeSpan.Zero;
        }
    }

    [Fact]
    public async Task AddingFilesNeedsPermissionAndNeverOverwrites()
    {
        await using var pc = Pc();
        await using var phone = Phone();
        await TrustAsync(phone, pc);
        var (folder, root) = Share(pc);
        var original = TestFiles.Hash(Path.Combine(folder, "a.jpg"));
        var local = Path.Combine(phone.Root, "a.jpg");
        TestFiles.Write(local, 500_000, 9);

        // Not allowed: nothing written.
        await using (var visit = await phone.Node.Browse.OpenAsync(pc.AsDevice(), CancellationToken.None))
        {
            Assert.False(visit.CanAdd);
            var refused = visit.Upload(root, "", new[] { local });
            await Wait.ForFinishAsync(refused);
            Assert.NotEqual(TransferState.Completed, refused.State);
        }

        Assert.Equal(original, TestFiles.Hash(Path.Combine(folder, "a.jpg")));
        Assert.False(File.Exists(Path.Combine(folder, "a (1).jpg")));

        // Allowed: added next to the one already there.
        pc.Node.Settings.Update(s => s.BrowseAllowAdding = true);
        string? added = null;
        pc.Node.Browse.FileAdded += (_, path) => added = path;
        await using (var visit = await phone.Node.Browse.OpenAsync(pc.AsDevice(), CancellationToken.None))
        {
            Assert.True(visit.CanAdd);
            var upload = visit.Upload(root, "Trip", new[] { local });
            await Wait.ForFinishAsync(upload);
            Assert.Equal(TransferState.Completed, upload.State);
            var again = visit.Upload(root, "", new[] { local });
            await Wait.ForFinishAsync(again);
            Assert.Equal(TransferState.Completed, again.State);
        }

        Assert.Equal(TestFiles.Hash(local), TestFiles.Hash(Path.Combine(folder, "Trip", "a.jpg")));
        Assert.Equal(original, TestFiles.Hash(Path.Combine(folder, "a.jpg")));
        Assert.Equal(TestFiles.Hash(local), TestFiles.Hash(Path.Combine(folder, "a (1).jpg")));
        Assert.Equal(Path.Combine(folder, "a (1).jpg"), added);
        Assert.Empty(Directory.GetFiles(folder, "*.beam-part", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task AddingOutsideTheSharedFolderIsRefused()
    {
        await using var pc = Pc();
        await using var phone = Phone();
        await TrustAsync(phone, pc);
        var (_, root) = Share(pc);
        pc.Node.Settings.Update(s => s.BrowseAllowAdding = true);
        var local = Path.Combine(phone.Root, "evil.txt");
        File.WriteAllText(local, "x");
        await using var visit = await phone.Node.Browse.OpenAsync(pc.AsDevice(), CancellationToken.None);

        var upload = visit.Upload(root, "..", new[] { local });
        await Wait.ForFinishAsync(upload);

        Assert.NotEqual(TransferState.Completed, upload.State);
        Assert.False(File.Exists(Path.Combine(pc.Root, "evil.txt")));
    }

    [Fact]
    public async Task ThumbnailsComeFromThePlatformOnly()
    {
        await using var pc = Pc();
        await using var phone = Phone();
        await TrustAsync(phone, pc);
        var (_, root) = Share(pc);
        await using var visit = await phone.Node.Browse.OpenAsync(pc.AsDevice(), CancellationToken.None);
        var item = (await visit.ListAsync(root, "", CancellationToken.None)).Single(i => i.Name == "a.jpg");

        Assert.Null(await visit.ThumbnailAsync(item, 128, CancellationToken.None)); // no previews in tests' nodes
        Assert.NotEmpty(await visit.ListAsync(root, "", CancellationToken.None)); // and the visit carries on
    }

    // ---------------------------------------------------------------- without a shared Wi-Fi

    [Fact]
    public async Task WithoutASharedWiFiBrowsingGoesOverTheDirectLink()
    {
        var air = new FakeDirectAir();
        var bluetooth = new FakeNearbyAir();
        var phoneLink = air.Link(DirectRoles.Host | DirectRoles.Join);
        var pcLink = air.Link(DirectRoles.Host | DirectRoles.JoinComputers);
        await using var phone = new TestNode("Galaxy", directLink: phoneLink, kind: DeviceKinds.Phone, nearbyRadio: bluetooth.Radio());
        await using var pc = new TestNode("Laptop", directLink: pcLink, kind: DeviceKinds.Laptop, nearbyRadio: bluetooth.Radio());
        air.ConnectDirectOnly(phone.Node, pc.Node);
        phone.Trust(pc);
        pc.Trust(phone);
        var (folder, root) = Share(pc);
        pc.Node.Nearby.SetReceiving(true);
        phone.Node.Nearby.SetLooking(true);
        await Wait.UntilAsync(() => phone.Node.Nearby.IsNearby(pc.Node.Identity.DeviceId), because: "heard over Bluetooth");
        // Known, but its Wi-Fi address doesn't answer (not on the same network).
        using var dead = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        dead.Start();
        var deadPort = ((System.Net.IPEndPoint)dead.LocalEndpoint).Port;
        dead.Stop();
        var device = pc.AsDevice(deadPort);

        await using var visit = await phone.Node.Browse.OpenAsync(device, CancellationToken.None);
        Assert.Equal(TransferPath.Direct, visit.Path);
        var items = await visit.ListAsync(root, "", CancellationToken.None);
        var session = visit.Download(items.Where(i => !i.IsFolder).ToList());
        await Wait.ForFinishAsync(session);

        Assert.Equal(TransferState.Completed, session.State);
        Assert.Equal(TransferPath.Direct, session.Path);
        Assert.Equal(TestFiles.Hash(Path.Combine(folder, "a.jpg")), TestFiles.Hash(Path.Combine(phone.ReceiveFolder, "a.jpg")));
        Assert.Single(pc.Handler.BrowseRequests); // asked once, also for the download's connection
    }
}
