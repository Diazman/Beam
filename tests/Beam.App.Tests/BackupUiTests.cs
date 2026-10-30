using Avalonia.Headless.XUnit;
using Beam.App.Platform;
using Beam.App.ViewModels.Dialogs;
using Beam.App.Services;
using Beam.Core.Backup;
using Beam.Core.Tests;

namespace Beam.App.Tests;

public class BackupUiTests
{
    [AvaloniaFact]
    public async Task PhoneSetsUpABackupByScanningTheComputersCode()
    {
        await using var pc = new UiHarness("Diaz's PC");
        await using var phone = new UiHarness("Diaz's Galaxy", width: 400, height: 860, phone: true);
        phone.Platform.SupportsBackup = true;
        pc.Node.Settings.Update(s => s.BackupFolder = Path.Combine(pc.Root, "Beam Backups"));
        await pc.InitializeAsync();
        await phone.InitializeAsync();

        // The computer: Settings → Backup → Add a phone shows a one-time code.
        pc.ViewModel.IsSettingsPage = true;
        await UiHarness.PumpAsync(100);
        Assert.Contains("Back up phones to this computer", pc.VisibleTexts());
        pc.ViewModel.Settings.Backup.AddPhoneCommand.Execute(null);
        await UiHarness.WaitForAsync(() => pc.ViewModel.Dialog is BackupCodeViewModel, "the code");
        var dialog = (BackupCodeViewModel)pc.ViewModel.Dialog!;
        Assert.True(dialog.ShowsCode);
        await UiHarness.PumpAsync(100);
        pc.Screenshot("backup-code");

        // The phone scans it (here: the code's text, reachable on loopback).
        var code = dialog.SetupCode!;
        code.Addresses = new List<string> { $"127.0.0.1:{pc.Node.Transfers.Port}" };
        phone.Platform.ScannedCode = code.AppendTo(null);
        phone.ViewModel.IsSettingsPage = true;
        await UiHarness.PumpAsync(100);
        Assert.Contains("Set up backup", phone.VisibleTexts().Concat(Buttons(phone)));
        phone.ViewModel.Settings.Backup.SetUpCommand.Execute(null);

        // Photos and videos need Android's permission: explained first.
        await UiHarness.WaitForAsync(() => phone.ViewModel.Dialog is ConfirmViewModel { Title: "Allow Beam to back up your photos and videos" }, "the explanation");
        ((ConfirmViewModel)phone.ViewModel.Dialog!).PrimaryCommand.Execute(null);
        await UiHarness.WaitForAsync(() => phone.ViewModel.Dialog is ConfirmViewModel { Title: "Backup is set up" }, "set up");
        ((ConfirmViewModel)phone.ViewModel.Dialog!).PrimaryCommand.Execute(null);
        Assert.Contains("permission:MediaLibrary", phone.Platform.Calls);
        Assert.Equal(1, phone.Platform.OwnerChecks); // setting up and the first choices: one fingerprint
        Assert.Contains(phone.Platform.Calls, c => c.StartsWith("question:This phone now backs up to Diaz's PC|"));

        // Both sides say who: the computer names the phone, the phone the computer.
        await UiHarness.WaitForAsync(() => dialog.IsAdded, "the computer saw the phone");
        Assert.Contains("Diaz's Galaxy", dialog.AddedText);
        dialog.CloseCommand.Execute(null);
        await UiHarness.WaitForAsync(() => pc.ViewModel.Settings.Backup.HasPartners, "partner listed");
        Assert.Equal("Diaz's Galaxy", pc.ViewModel.Settings.Backup.Partners.Single().Name);

        var backup = phone.ViewModel.Settings.Backup;
        await UiHarness.WaitForAsync(() => backup.HasPlan, "plan shown");
        Assert.Equal("Backing up to Diaz's PC", backup.TargetText);
        Assert.Equal(new[] { BackupSourceKinds.Photos, BackupSourceKinds.Videos }, backup.Sources.Select(s => s.Source.Kind));
        Assert.True(backup.Automatic);
        Assert.True(phone.Platform.BackupsScheduled);

        // A folder: every kind ticked to start with; keep only PDF, Word and PowerPoint.
        phone.Ui.FolderToPick = Path.Combine(phone.Root, "Download");
        Directory.CreateDirectory(phone.Ui.FolderToPick);
        backup.AddFolderCommand.Execute(null);
        await UiHarness.WaitForAsync(() => backup.Sources.Count == 3, "folder added");
        var kinds = backup.Sources[2].FileKinds;
        Assert.False(backup.Sources[2].ShowsKinds); // closed: a summary and a button
        Assert.Equal("All file types", backup.Sources[2].KindsSummary);
        await UiHarness.PumpAsync(100);
        Assert.Contains("Choose file types", Buttons(phone));
        Assert.DoesNotContain("E-books", phone.VisibleTexts());
        backup.Sources[2].ToggleKindsCommand.Execute(null);
        await UiHarness.PumpAsync(100);
        Assert.Contains("E-books", phone.VisibleTexts());
        Assert.Contains("Hide file types", Buttons(phone));
        Assert.Equal(new[] { "Photos", "Videos", "Documents", "PDF", "Word", "Excel", "PowerPoint", "Text", "E-books", "Music", "Archives", "Other" },
            kinds.Select(k => k.Label));
        Assert.All(kinds, k => Assert.True(k.IsChecked));
        KindViewModel(kinds, "Documents").IsChecked = false; // clears all documents
        Assert.All(kinds.Where(k => k.IsChild), k => Assert.False(k.IsChecked));
        foreach (var label in new[] { "Photos", "Videos", "Music", "Archives", "Other" }) KindViewModel(kinds, label).IsChecked = false;
        Assert.True(backup.Sources[2].HasNoKinds);
        foreach (var label in new[] { "PDF", "Word", "PowerPoint" }) KindViewModel(kinds, label).IsChecked = true;
        Assert.Null(KindViewModel(kinds, "Documents").IsChecked); // some documents
        Assert.Equal("File types: PDF, Word, PowerPoint", backup.Sources[2].KindsSummary);
        await UiHarness.WaitForAsync(() => phone.Node.Settings.Current.Backups.Single().Sources.ElementAtOrDefault(2)?.Types?.Count == 3, "saved", 5000);
        Assert.Equal(new[] { BackupFileTypes.Pdf, BackupFileTypes.Word, BackupFileTypes.PowerPoint },
            phone.Node.Settings.Current.Backups.Single().Sources[2].Types);

        // The computer sorts files into Photos, Videos, Documents… unless the phone's folder names are kept.
        var checks = phone.Platform.OwnerChecks;
        Assert.False(backup.KeepFolderNames);
        backup.KeepFolderNames = true;
        await UiHarness.WaitForAsync(() => phone.Node.Settings.Current.Backups.Single().KeepFolderNames, "saved");
        Assert.Equal(checks, phone.Platform.OwnerChecks); // the same files: nothing to confirm
        await UiHarness.PumpAsync(100);
        Assert.Empty(phone.TextOutsideWindow());
        var heading = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(phone.Window).OfType<Avalonia.Controls.TextBlock>()
            .First(t => t.Text == "Download");
        Avalonia.Controls.ControlExtensions.BringIntoView(heading, new Avalonia.Rect(0, -200, 10, 700));
        await UiHarness.PumpAsync(100);
        phone.Screenshot("backup-phone");
        backup.Sources[2].ToggleKindsCommand.Execute(null); // closed again: the summary
        await UiHarness.PumpAsync(100);
        Avalonia.Controls.ControlExtensions.BringIntoView(heading, new Avalonia.Rect(0, -200, 10, 700));
        await UiHarness.PumpAsync(100);
        phone.Screenshot("backup-phone-closed");

        // Stopping asks first, and keeps the computer's copy.
        backup.StopCommand.Execute(null);
        await UiHarness.WaitForAsync(() => phone.ViewModel.Dialog is ConfirmViewModel { IsDestructive: true }, "confirm stop");
        ((ConfirmViewModel)phone.ViewModel.Dialog!).PrimaryCommand.Execute(null);
        await UiHarness.WaitForAsync(() => backup.HasNoPlan, "stopped");
        Assert.Empty(phone.Node.Settings.Current.Backups);
        Assert.False(phone.Platform.BackupsScheduled);
    }

    [AvaloniaFact]
    public async Task AnOrdinaryCodeIsNotABackupCode()
    {
        await using var pc = new UiHarness("Diaz's PC");
        await using var phone = new UiHarness("Diaz's Galaxy", width: 400, height: 860, phone: true);
        phone.Platform.SupportsBackup = true;
        await phone.InitializeAsync();
        phone.Platform.ScannedCode = pc.Node.CreatePairingCode().AppendTo(null);
        phone.ViewModel.Settings.Backup.SetUpCommand.Execute(null);
        await UiHarness.WaitForAsync(() => phone.ViewModel.Settings.Backup.HasMessage, "explained");
        Assert.Contains("not a backup code", phone.ViewModel.Settings.Backup.Message);
        Assert.Empty(phone.Node.Settings.Current.Backups);
        Assert.Empty(pc.Node.Settings.Current.BackupPartners);
    }

    [AvaloniaFact]
    public async Task ClosingTheDialogEndsTheCode()
    {
        await using var pc = new UiHarness("Diaz's PC");
        await using var phone = new UiHarness("Diaz's Galaxy", width: 400, height: 860, phone: true);
        phone.Platform.SupportsBackup = true;
        await pc.InitializeAsync();
        await phone.InitializeAsync();
        pc.ViewModel.Settings.Backup.AddPhoneCommand.Execute(null);
        await UiHarness.WaitForAsync(() => pc.ViewModel.Dialog is BackupCodeViewModel, "the code");
        var code = ((BackupCodeViewModel)pc.ViewModel.Dialog!).SetupCode!;
        code.Addresses = new List<string> { $"127.0.0.1:{pc.Node.Transfers.Port}" };
        ((BackupCodeViewModel)pc.ViewModel.Dialog!).CloseCommand.Execute(null);
        await UiHarness.WaitForAsync(() => pc.ViewModel.Dialog == null, "closed");

        // A photo of the code taken before it closed doesn't work.
        phone.Platform.ScannedCode = code.AppendTo(null);
        phone.ViewModel.Settings.Backup.SetUpCommand.Execute(null);
        await UiHarness.WaitForAsync(() => phone.ViewModel.Settings.Backup.HasMessage, "refused");
        Assert.Empty(pc.Node.Settings.Current.BackupPartners);
        Assert.Empty(phone.Node.Settings.Current.Backups);
    }

    [AvaloniaFact]
    public async Task IPhonesDontShowBackupYet()
    {
        await using var phone = new UiHarness("Diaz's iPhone", width: 400, height: 860, phone: true);
        await phone.InitializeAsync();
        Assert.False(phone.ViewModel.Settings.Backup.IsAvailable);
        phone.ViewModel.IsSettingsPage = true;
        await UiHarness.PumpAsync(100);
        Assert.DoesNotContain("Backup", phone.VisibleTexts());
    }

    /// <summary>A phone and a computer with the computer's backup code on screen; the phone scans it.</summary>
    private static async Task<(UiHarness Pc, UiHarness Phone)> ScanTheCodeAsync()
    {
        var pc = new UiHarness("Diaz's PC");
        var phone = new UiHarness("Diaz's Galaxy", width: 400, height: 860, phone: true);
        phone.Platform.SupportsBackup = true;
        await pc.InitializeAsync();
        await phone.InitializeAsync();
        var code = pc.Node.Backup.CreateSetupCode();
        code.Addresses = new List<string> { $"127.0.0.1:{pc.Node.Transfers.Port}" };
        phone.Platform.ScannedCode = code.AppendTo(null);
        return (pc, phone);
    }

    [AvaloniaFact]
    public async Task SomeoneWithoutTheScreenLockCantSetUpABackup()
    {
        var (pc, phone) = await ScanTheCodeAsync();
        await using var _ = pc;
        await using var __ = phone;

        // Handed the unlocked phone, someone scans their own computer's code: the phone's lock is asked, and fails.
        phone.Platform.OwnerAnswer = OwnerCheck.Refused;
        phone.ViewModel.Settings.Backup.SetUpCommand.Execute(null);
        await UiHarness.WaitForAsync(() => phone.ViewModel.Settings.Backup.HasMessage, "refused");
        Assert.Equal(1, phone.Platform.OwnerChecks);
        Assert.Contains("Nothing was changed", phone.ViewModel.Settings.Backup.Message);
        await UiHarness.PumpAsync(200);
        Assert.Empty(phone.Node.Settings.Current.Backups);
        Assert.Empty(pc.Node.Settings.Current.BackupPartners); // the computer never even heard from the phone
        Assert.DoesNotContain(phone.Platform.Calls, c => c.StartsWith("permission:"));

        // The owner, with their fingerprint, can.
        phone.Platform.OwnerAnswer = OwnerCheck.Confirmed;
        phone.ViewModel.Settings.Backup.SetUpCommand.Execute(null);
        await UiHarness.WaitForAsync(() => phone.ViewModel.Dialog is ConfirmViewModel, "a question");
        while (phone.ViewModel.Dialog is ConfirmViewModel question)
        {
            question.PrimaryCommand.Execute(null);
            await UiHarness.PumpAsync(100);
        }

        await UiHarness.WaitForAsync(() => phone.Node.Settings.Current.Backups.Count == 1, "set up");
        Assert.Single(pc.Node.Settings.Current.BackupPartners);
    }

    [AvaloniaFact]
    public async Task APhoneWithoutAScreenLockWarnsFirst()
    {
        var (pc, phone) = await ScanTheCodeAsync();
        await using var _ = pc;
        await using var __ = phone;
        phone.Platform.OwnerAnswer = OwnerCheck.NoScreenLock;
        phone.ViewModel.Settings.Backup.SetUpCommand.Execute(null);
        await UiHarness.WaitForAsync(() => phone.ViewModel.Dialog is ConfirmViewModel { Title: "This phone has no screen lock" }, "the warning");
        ((ConfirmViewModel)phone.ViewModel.Dialog!).SecondaryCommand.Execute(null);
        await UiHarness.PumpAsync(200);
        Assert.Empty(phone.Node.Settings.Current.Backups);
        Assert.Empty(pc.Node.Settings.Current.BackupPartners);
    }

    [AvaloniaFact]
    public async Task BackingUpMoreAsksTheOwnerAgain()
    {
        var grace = Beam.App.ViewModels.BackupViewModel.OwnerGrace;
        Beam.App.ViewModels.BackupViewModel.OwnerGrace = TimeSpan.Zero; // every change asks (normally not for 2 minutes)
        try
        {
            var (pc, phone) = await ScanTheCodeAsync();
            await using var _ = pc;
            await using var __ = phone;
            phone.Platform.Granted.Add(AppPermission.MediaLibrary);
            var backup = phone.ViewModel.Settings.Backup;
            backup.SetUpCommand.Execute(null);
            await UiHarness.WaitForAsync(() => phone.ViewModel.Dialog is ConfirmViewModel { Title: "Backup is set up" }, "set up");
            ((ConfirmViewModel)phone.ViewModel.Dialog!).PrimaryCommand.Execute(null);
            var checks = phone.Platform.OwnerChecks;

            // Someone else adds a folder, or switches automatic backups back on: refused without the owner's lock.
            phone.Platform.OwnerAnswer = OwnerCheck.Refused;
            phone.Ui.FolderToPick = Path.Combine(phone.Root, "Private");
            Directory.CreateDirectory(phone.Ui.FolderToPick);
            backup.AddFolderCommand.Execute(null);
            await UiHarness.WaitForAsync(() => phone.Platform.OwnerChecks == checks + 1, "asked");
            await UiHarness.PumpAsync(100);
            Assert.Equal(2, phone.Node.Settings.Current.Backups.Single().Sources.Count);
            Assert.Equal(2, backup.Sources.Count);
            Assert.True(backup.HasMessage);

            backup.Automatic = false; // backing up less never asks
            await UiHarness.WaitForAsync(() => !phone.Node.Settings.Current.Backups.Single().Automatic, "turned off");
            Assert.Equal(checks + 1, phone.Platform.OwnerChecks);
            backup.Automatic = true;
            await UiHarness.WaitForAsync(() => phone.Platform.OwnerChecks == checks + 2, "asked again");
            await UiHarness.PumpAsync(100);
            Assert.False(phone.Node.Settings.Current.Backups.Single().Automatic);
            Assert.False(backup.Automatic);

            backup.Sources[0].RemoveCommand.Execute(null);
            await UiHarness.WaitForAsync(() => phone.Node.Settings.Current.Backups.Single().Sources.Count == 1, "removed");
            Assert.Equal(checks + 2, phone.Platform.OwnerChecks);

            // The owner can.
            phone.Platform.OwnerAnswer = OwnerCheck.Confirmed;
            backup.AddFolderCommand.Execute(null);
            await UiHarness.WaitForAsync(() => phone.Node.Settings.Current.Backups.Single().Sources.Count == 2, "folder added");
        }
        finally
        {
            Beam.App.ViewModels.BackupViewModel.OwnerGrace = grace;
        }
    }

    [AvaloniaFact]
    public async Task EveryBackupThatSendsFilesIsAnnouncedWithStop()
    {
        // As in the background on Android: no screen, only the notices.
        await using var pc = new TestNode("Anna's laptop", startDiscovery: true);
        await using var phone = new TestNode("Diaz's Galaxy", startDiscovery: true, kind: Beam.Core.Discovery.DeviceKinds.Phone);
        pc.Node.Settings.Update(s => s.BackupFolder = Path.Combine(pc.Root, "backups"));
        var platform = new FakePlatform { IsPhone = true, SupportsBackup = true };
        BackupNotices.Attach(phone.Node, platform);
        BackupNotices.Attach(phone.Node, platform); // once is enough

        var code = pc.Node.Backup.CreateSetupCode();
        code.Addresses = new List<string> { $"127.0.0.1:{pc.Node.Transfers.Port}" };
        var plan = await phone.Node.Backup.SetUpAsync(code, CancellationToken.None);
        var tag = "backup-" + pc.Node.Identity.DeviceId;
        Assert.Single(platform.Calls, c => c.StartsWith("question:This phone now backs up to Anna's laptop|"));
        Assert.Equal("Stop backup", Assert.Single(platform.Questions[tag]).Label);

        var source = Path.Combine(phone.Root, "photos");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "a.jpg"), "a");
        File.WriteAllText(Path.Combine(source, "b.jpg"), "b");
        plan.Sources.Add(new BackupSource { Kind = BackupSourceKinds.Folder, Address = source, Name = "Photos" });
        plan.Automatic = false;
        phone.Node.Backup.Save(plan);
        phone.Node.Discovery.AddUnicastTarget(System.Net.IPAddress.Loopback, pc.Node.Discovery.LocalPort);
        pc.Node.Discovery.AddUnicastTarget(System.Net.IPAddress.Loopback, phone.Node.Discovery.LocalPort);
        await Wait.UntilAsync(() => phone.Node.Backup.IsReachable(plan));

        var session = phone.Node.Backup.BackUpNow(plan.TargetId);
        await Wait.ForFinishAsync(session);
        await Wait.UntilAsync(() => platform.Calls.Any(c => c.StartsWith("question:2 files backed up to Anna's laptop|")));

        // Nothing new: no notice.
        var count = platform.Calls.Count;
        await Wait.UntilAsync(() => phone.Node.Backup.Running(plan.TargetId) == null);
        session = phone.Node.Backup.BackUpNow(plan.TargetId);
        await Wait.ForFinishAsync(session);
        await Task.Delay(200);
        Assert.Equal(count, platform.Calls.Count);

        // "Stop backup" on the notice: no more backups to that computer.
        platform.Questions[tag].Single().OnClick();
        Assert.Empty(phone.Node.Settings.Current.Backups);
    }

    private static Beam.App.ViewModels.FileKindViewModel KindViewModel(IEnumerable<Beam.App.ViewModels.FileKindViewModel> kinds, string label) =>
        kinds.Single(k => k.Label == label);

    private static IEnumerable<string> Buttons(UiHarness h) =>
        Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(h.Window).OfType<Avalonia.Controls.Button>()
            .Where(b => b.IsEffectivelyVisible).Select(b => b.Content as string).OfType<string>();
}
