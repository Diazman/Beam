using System.Net;
using Beam.Core.Backup;
using Beam.Core.Direct;
using Beam.Core.Discovery;
using Beam.Core.Files;
using Beam.Core.Transfer;

namespace Beam.Core.Tests;

/// <summary>Automatic backups from a phone to a computer: the one-time setup, who may back up, and what is sent.</summary>
[Collection(nameof(BackupTests))] // changes BackupService's timings
public class BackupTests
{
    /// <summary>A computer that takes backups into its own temporary folder.</summary>
    private static TestNode Computer(string name = "Office PC")
    {
        var pc = new TestNode(name, startDiscovery: true);
        pc.Node.Settings.Update(s => s.BackupFolder = Path.Combine(pc.Root, "backups"));
        return pc;
    }

    private static TestNode Phone(string name = "Pixel") => new(name, startDiscovery: true, kind: DeviceKinds.Phone);

    /// <summary>The setup code as the phone reads it from the QR code (reachable on loopback in tests).</summary>
    private static PairingCode Scan(TestNode pc, PairingCode code)
    {
        code.Addresses = new List<string> { $"127.0.0.1:{pc.Node.Transfers.Port}" };
        return PairingCode.TryParse(code.AppendTo(null)) ?? throw new InvalidOperationException("The code doesn't parse.");
    }

    private static void Link(TestNode a, TestNode b)
    {
        a.Node.Discovery.AddUnicastTarget(IPAddress.Loopback, b.Node.Discovery.LocalPort);
        b.Node.Discovery.AddUnicastTarget(IPAddress.Loopback, a.Node.Discovery.LocalPort);
    }

    private static async Task<BackupPlan> SetUpAsync(TestNode phone, TestNode pc, string folder, string fileTypes = BackupFileTypes.All)
    {
        var plan = await phone.Node.Backup.SetUpAsync(Scan(pc, pc.Node.Backup.CreateSetupCode()), CancellationToken.None);
        plan.Sources.Add(new BackupSource { Kind = BackupSourceKinds.Folder, Address = folder, Name = "Documents", FileTypes = fileTypes });
        plan.Automatic = false;
        phone.Node.Backup.Save(plan);
        return plan;
    }

    private static string PartnerFolder(TestNode pc) => pc.Node.Settings.Current.BackupPartners.Single().Folder;

    private static async Task<TransferSession> BackUpAsync(TestNode phone, TestNode pc)
    {
        await Wait.UntilAsync(() => phone.Node.Backup.IsReachable(phone.Node.Settings.Current.Backups.Single()), because: "the computer is found");
        var session = phone.Node.Backup.BackUpNow(pc.Node.Identity.DeviceId);
        await Wait.ForFinishAsync(session);
        await Wait.UntilAsync(() => phone.Node.Backup.Running(pc.Node.Identity.DeviceId) == null);
        return session;
    }

    [Fact]
    public async Task SetupWithTheCodePinsBothSides()
    {
        await using var pc = Computer();
        await using var phone = Phone();
        BackupPartner? added = null;
        pc.Node.Backup.PartnerAdded += p => added = p;

        var plan = await phone.Node.Backup.SetUpAsync(Scan(pc, pc.Node.Backup.CreateSetupCode()), CancellationToken.None);

        Assert.Equal(pc.Node.Identity.DeviceId, plan.TargetId);
        Assert.Equal("Office PC", plan.TargetName);
        Assert.Equal(pc.Node.Identity.Fingerprint, plan.TargetFingerprint, ignoreCase: true);
        Assert.Single(phone.Node.Settings.Current.Backups);

        var partner = Assert.Single(pc.Node.Settings.Current.BackupPartners);
        Assert.Equal(phone.Node.Identity.Fingerprint, partner.Fingerprint, ignoreCase: true);
        Assert.Equal("Pixel", partner.Name);
        Assert.StartsWith(Path.Combine(pc.Root, "backups"), partner.Folder);
        Assert.True(Directory.Exists(partner.Folder));
        Assert.NotNull(added);
        Assert.Empty(pc.Handler.Requests); // nobody had to accept anything
    }

    [Fact]
    public async Task TheCodeWorksOnceForOnePhone()
    {
        await using var pc = Computer();
        await using var phone = Phone();
        await using var guest = Phone("Guest phone");
        var code = pc.Node.Backup.CreateSetupCode();

        await phone.Node.Backup.SetUpAsync(Scan(pc, code), CancellationToken.None);
        // Someone who photographed the screen afterwards: the code is used up.
        var ex = await Assert.ThrowsAsync<TransferException>(() => guest.Node.Backup.SetUpAsync(Scan(pc, code), CancellationToken.None));
        Assert.Equal(TransferErrorKind.Declined, ex.Error.Kind);
        Assert.Single(pc.Node.Settings.Current.BackupPartners);
        Assert.Empty(guest.Node.Settings.Current.Backups);
    }

    [Fact]
    public async Task AWrongOrClosedCodeSetsNothingUp()
    {
        await using var pc = Computer();
        await using var guest = Phone("Guest phone");

        // No code on screen: a guessed token goes nowhere.
        var guessed = pc.Node.CreatePairingCode();
        guessed.BackupToken = "guessed-token";
        await Assert.ThrowsAsync<TransferException>(() => guest.Node.Backup.SetUpAsync(Scan(pc, guessed), CancellationToken.None));

        // A code on screen, but the wrong token.
        var real = pc.Node.Backup.CreateSetupCode();
        await Assert.ThrowsAsync<TransferException>(() => guest.Node.Backup.SetUpAsync(Scan(pc, guessed), CancellationToken.None));

        // Closed before it was used.
        pc.Node.Backup.CancelSetupCode();
        await Assert.ThrowsAsync<TransferException>(() => guest.Node.Backup.SetUpAsync(Scan(pc, real), CancellationToken.None));

        // An ordinary pairing code (no token) is not a backup code.
        await Assert.ThrowsAsync<TransferException>(() => guest.Node.Backup.SetUpAsync(Scan(pc, pc.Node.CreatePairingCode()), CancellationToken.None));

        Assert.Empty(pc.Node.Settings.Current.BackupPartners);
        Assert.Empty(guest.Node.Settings.Current.Backups);
    }

    [Fact]
    public async Task GuessingEndsTheCode()
    {
        await using var pc = Computer();
        await using var guest = Phone("Guest phone");
        var real = pc.Node.Backup.CreateSetupCode();
        var wrong = pc.Node.CreatePairingCode();
        wrong.BackupToken = "wrong";
        for (var i = 0; i < 5; i++)
            await Assert.ThrowsAsync<TransferException>(() => guest.Node.Backup.SetUpAsync(Scan(pc, wrong), CancellationToken.None));

        // After 5 wrong tries the shown code no longer works either: a new one has to be shown.
        await Assert.ThrowsAsync<TransferException>(() => guest.Node.Backup.SetUpAsync(Scan(pc, real), CancellationToken.None));
        Assert.Empty(pc.Node.Settings.Current.BackupPartners);
    }

    [Fact]
    public async Task AnExpiredCodeSetsNothingUp()
    {
        var lifetime = BackupService.InviteLifetime;
        BackupService.InviteLifetime = TimeSpan.FromMilliseconds(200);
        try
        {
            await using var pc = Computer();
            await using var phone = Phone();
            var code = pc.Node.Backup.CreateSetupCode();
            await Task.Delay(400);
            await Assert.ThrowsAsync<TransferException>(() => phone.Node.Backup.SetUpAsync(Scan(pc, code), CancellationToken.None));
            Assert.Empty(pc.Node.Settings.Current.BackupPartners);
        }
        finally
        {
            BackupService.InviteLifetime = lifetime;
        }
    }

    [Fact]
    public async Task AnImpostorComputerCantTakeTheSetup()
    {
        await using var pc = Computer();
        await using var impostor = Computer("Office PC");
        await using var phone = Phone();

        // The impostor shows a copy of the real computer's code, but it can't prove the real computer's certificate.
        var code = Scan(impostor, pc.Node.Backup.CreateSetupCode());
        var ex = await Assert.ThrowsAsync<TransferException>(() => phone.Node.Backup.SetUpAsync(code, CancellationToken.None));
        Assert.NotEqual(TransferErrorKind.Declined, ex.Error.Kind);
        Assert.Empty(phone.Node.Settings.Current.Backups);
        Assert.Empty(impostor.Node.Settings.Current.BackupPartners);
    }

    [Fact]
    public async Task BacksUpOnlyWhatIsMissingAndNeverDeletes()
    {
        await using var pc = Computer();
        await using var phone = Phone();
        Link(phone, pc);
        var source = Path.Combine(phone.Root, "phone-docs");
        Directory.CreateDirectory(Path.Combine(source, "Work"));
        File.WriteAllText(Path.Combine(source, "a.pdf"), "first");
        File.WriteAllText(Path.Combine(source, "Work", "b.txt"), "second");
        await SetUpAsync(phone, pc, source);

        var first = await BackUpAsync(phone, pc);
        Assert.Equal(TransferState.Completed, first.State);
        var target = Path.Combine(PartnerFolder(pc), "Documents");
        Assert.Equal("first", File.ReadAllText(Path.Combine(target, "a.pdf")));
        Assert.Equal("second", File.ReadAllText(Path.Combine(target, "Work", "b.txt")));
        Assert.Empty(pc.Handler.Requests); // taken without asking
        Assert.Empty(pc.Handler.Conflicts);

        // Nothing new: nothing is sent, and history doesn't fill up with empty backups.
        var historyBefore = phone.Node.History.Entries.Count;
        var second = await BackUpAsync(phone, pc);
        Assert.Equal(TransferState.Completed, second.State);
        Assert.Equal(0, second.GetSnapshot().CompletedFiles);
        Assert.Equal(2, second.GetSnapshot().SkippedFiles);
        Assert.Equal(historyBefore, phone.Node.History.Entries.Count);

        // A changed file replaces the old copy; a new file is added; one deleted on the phone stays on the computer.
        File.WriteAllText(Path.Combine(source, "a.pdf"), "changed!");
        File.SetLastWriteTimeUtc(Path.Combine(source, "a.pdf"), DateTime.UtcNow.AddMinutes(1));
        File.WriteAllText(Path.Combine(source, "c.docx"), "third");
        File.Delete(Path.Combine(source, "Work", "b.txt"));
        var third = await BackUpAsync(phone, pc);
        Assert.Equal(2, third.GetSnapshot().CompletedFiles);
        Assert.Equal("changed!", File.ReadAllText(Path.Combine(target, "a.pdf")));
        Assert.Equal("third", File.ReadAllText(Path.Combine(target, "c.docx")));
        Assert.Equal("second", File.ReadAllText(Path.Combine(target, "Work", "b.txt")));
        Assert.Single(Directory.GetFiles(target, "a*")); // replaced, not "a (1).pdf"

        var plan = phone.Node.Settings.Current.Backups.Single();
        Assert.NotNull(plan.LastCompletedAt);
        Assert.Null(plan.LastError);
        Assert.Equal(2, plan.LastFileCount);
        Assert.Equal(2, pc.Node.Settings.Current.BackupPartners.Single().LastFileCount);
    }

    [Fact]
    public async Task OnlyTheChosenKindsOfFiles()
    {
        await using var pc = Computer();
        await using var phone = Phone();
        Link(phone, pc);
        var source = Path.Combine(phone.Root, "downloads");
        Directory.CreateDirectory(source);
        foreach (var name in new[] { "report.pdf", "Book.EPUB", "photo.jpg", "clip.mp4", "notes.txt" })
            File.WriteAllText(Path.Combine(source, name), name);
        var plan = await SetUpAsync(phone, pc, source, BackupFileTypes.Custom);
        plan.Sources[0].Extensions = "pdf, .epub";
        phone.Node.Backup.Save(plan);

        await BackUpAsync(phone, pc);

        var names = Directory.GetFiles(Path.Combine(PartnerFolder(pc), "Documents")).Select(Path.GetFileName).OrderBy(n => n).ToList();
        Assert.Equal(new[] { "Book.EPUB", "report.pdf" }, names);
    }

    [Fact]
    public void FileTypeFilters()
    {
        BackupSource Of(string types) => new() { FileTypes = types };
        Assert.True(BackupFileTypes.Includes(Of(BackupFileTypes.Photos), "IMG_1.HEIC"));
        Assert.False(BackupFileTypes.Includes(Of(BackupFileTypes.Photos), "clip.mp4"));
        Assert.True(BackupFileTypes.Includes(Of(BackupFileTypes.Videos), "clip.MOV"));
        Assert.True(BackupFileTypes.Includes(Of(BackupFileTypes.Documents), "contract.pdf"));
        Assert.False(BackupFileTypes.Includes(Of(BackupFileTypes.Documents), "song.mp3"));
        Assert.True(BackupFileTypes.Includes(Of(BackupFileTypes.All), "anything.bin"));
        Assert.Equal(new HashSet<string> { ".pdf", ".epub", ".docx" }, BackupFileTypes.ParseExtensions("pdf, .EPUB *.docx ;"));
        Assert.False(BackupFileTypes.Includes(new BackupSource { FileTypes = BackupFileTypes.Custom, Extensions = "" }, "a.pdf"));
    }

    [Fact]
    public async Task AStrangerCantBackUp()
    {
        await using var pc = Computer();
        await using var stranger = Phone("Stranger");
        var file = stranger.CreateFile("secret.pdf", 1000);

        // Not set up (even if it knows the computer's certificate): refused, and nothing is written.
        var session = stranger.Node.Transfers.SendBackup(pc.AsDevice(), () => pc.AsDevice(),
            _ => new Manifest
            {
                Entries = new List<ManifestEntry> { new() { RelativePath = "Docs/secret.pdf", SourcePath = file, Size = 1000, ModifiedUtc = DateTime.UtcNow } },
                RootNames = new List<string> { "Docs" },
                Warnings = new List<string>(),
            }, "Backup");
        await Wait.ForFinishAsync(session);
        Assert.Equal(TransferState.Declined, session.State);
        Assert.False(Directory.Exists(Path.Combine(pc.Root, "backups")) && Directory.EnumerateFiles(Path.Combine(pc.Root, "backups"), "*", SearchOption.AllDirectories).Any());
        Assert.Empty(pc.Handler.Requests);
        Assert.Empty(Directory.EnumerateFiles(pc.ReceiveFolder, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ARemovedPhoneCantBackUpAnyMore()
    {
        await using var pc = Computer();
        await using var phone = Phone();
        Link(phone, pc);
        var source = Path.Combine(phone.Root, "docs");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "a.pdf"), "a");
        await SetUpAsync(phone, pc, source);

        pc.Node.Backup.RemovePartner(phone.Node.Identity.Fingerprint);
        var session = await BackUpAsync(phone, pc);

        Assert.Equal(TransferState.Declined, session.State);
        Assert.NotNull(phone.Node.Settings.Current.Backups.Single().LastError);
        Assert.Empty(pc.Handler.Requests);
    }

    [Fact]
    public async Task NeverToADeviceWithAnotherCertificate()
    {
        await using var pc = Computer();
        await using var other = Computer("Office PC");
        await using var phone = Phone();
        Link(phone, other);
        await SetUpAsync(phone, pc, phone.Root);

        // Another device announcing itself under the computer's id: it doesn't have the computer's certificate.
        var plan = phone.Node.Settings.Current.Backups.Single();
        plan.TargetId = other.Node.Identity.DeviceId;
        phone.Node.Settings.Update(s => s.Backups[0] = plan);
        await Wait.UntilAsync(() => phone.Node.Discovery.Find(other.Node.Identity.DeviceId) != null);

        Assert.False(phone.Node.Backup.IsReachable(plan));
        var ex = Assert.Throws<TransferException>(() => phone.Node.Backup.BackUpNow(plan.TargetId));
        Assert.Equal(TransferErrorKind.ConnectFailed, ex.Error.Kind);

        // Even sent straight to it (pinned to the computer's certificate), the connection fails before any file.
        var session = phone.Node.Transfers.SendBackup(other.AsDevice() with { Fingerprint = plan.TargetFingerprint },
            () => other.AsDevice() with { Fingerprint = plan.TargetFingerprint },
            _ => BackupManifest.Build(plan, null, null, CancellationToken.None), "Backup");
        await Wait.ForFinishAsync(session, TimeSpan.FromSeconds(240));
        Assert.NotEqual(TransferState.Completed, session.State);
        Assert.False(Directory.Exists(Path.Combine(other.Root, "backups")) && Directory.EnumerateFiles(Path.Combine(other.Root, "backups"), "*", SearchOption.AllDirectories).Any());
    }

    [Fact]
    public async Task NeverOverADirectLink()
    {
        await using var pc = Computer();
        await using var phone = Phone();
        Link(phone, pc);
        var source = Path.Combine(phone.Root, "docs");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "a.pdf"), "a");
        await SetUpAsync(phone, pc, source);
        await Wait.UntilAsync(() => phone.Node.Discovery.Find(pc.Node.Identity.DeviceId) != null);

        // The computer is reachable only over a direct link: no backup.
        phone.Node.Transfers.IsDirectAddress = a => IPAddress.IsLoopback(a);
        Assert.False(phone.Node.Backup.IsReachable(phone.Node.Settings.Current.Backups.Single()));
        Assert.Throws<TransferException>(() => phone.Node.Backup.BackUpNow(pc.Node.Identity.DeviceId));

        // And the computer doesn't take a backup arriving over one.
        phone.Node.Transfers.IsDirectAddress = _ => false;
        pc.Node.Transfers.IsDirectAddress = a => IPAddress.IsLoopback(a);
        var session = phone.Node.Backup.BackUpNow(pc.Node.Identity.DeviceId);
        await Wait.ForFinishAsync(session);
        Assert.Equal(TransferState.Declined, session.State);
        Assert.False(File.Exists(Path.Combine(PartnerFolder(pc), "Documents", "a.pdf")));
    }

    [Fact]
    public async Task BacksUpByItselfWhenTheComputerShowsUp()
    {
        await using var pc = Computer();
        await using var phone = Phone();
        var source = Path.Combine(phone.Root, "docs");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "a.pdf"), "a");
        var plan = await SetUpAsync(phone, pc, source);
        plan.Automatic = true;
        phone.Node.Backup.Save(plan);
        Assert.Null(phone.Node.Backup.Running(pc.Node.Identity.DeviceId)); // not on the same network yet

        // Home again: both on the same network. Nobody presses anything on either side.
        Link(phone, pc);
        var target = Path.Combine(pc.Root, "backups");
        await Wait.UntilAsync(() => Directory.Exists(target) && Directory.EnumerateFiles(target, "a.pdf", SearchOption.AllDirectories).Any(),
            because: "the backup runs by itself");
        Assert.Empty(pc.Handler.Requests);
        await Wait.UntilAsync(() => phone.Node.Settings.Current.Backups.Single().LastCompletedAt != null);
    }

    [Fact]
    public void BackupPathsStayInsideTheirSource()
    {
        using var dir = new TempDir();
        var plan = new BackupPlan
        {
            Sources =
            {
                new BackupSource { Kind = BackupSourceKinds.Photos, Name = "Photos" },
                new BackupSource { Kind = BackupSourceKinds.Photos, Name = "Photos" },
                new BackupSource { Kind = BackupSourceKinds.Videos, Name = "../Videos" },
            },
        };
        var platform = new FakeSources(new BackupItem("../../escape.jpg", 1, DateTime.UtcNow, "content://1", () => new MemoryStream(new byte[1])),
            new BackupItem("DCIM/Camera/IMG_1.jpg", 1, DateTime.UtcNow, "content://2", () => new MemoryStream(new byte[1])));
        var manifest = BackupManifest.Build(plan, platform, null, CancellationToken.None);

        Assert.Equal(3, manifest.RootNames.Count);
        Assert.Equal(manifest.RootNames.Count, manifest.RootNames.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(manifest.Entries, e =>
        {
            Assert.DoesNotContain("..", e.RelativePath.Split('/'));
            Assert.Contains(manifest.RootNames, r => e.RelativePath.StartsWith(r + "/", StringComparison.Ordinal));
        });
    }

    private sealed class FakeSources(params BackupItem[] items) : IBackupSources
    {
        public IReadOnlyList<BackupItem>? List(BackupSource source, CancellationToken cancellationToken) =>
            source.Kind == BackupSourceKinds.Folder ? null : items;
    }
}
