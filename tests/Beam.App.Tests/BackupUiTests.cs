using Avalonia.Headless.XUnit;
using Beam.App.Platform;
using Beam.App.ViewModels.Dialogs;
using Beam.Core.Backup;

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

        // A folder with only PDFs.
        phone.Ui.FolderToPick = Path.Combine(phone.Root, "Documents");
        Directory.CreateDirectory(phone.Ui.FolderToPick);
        backup.AddFolderCommand.Execute(null);
        await UiHarness.WaitForAsync(() => backup.Sources.Count == 3, "folder added");
        var folder = backup.Sources[2];
        folder.SelectedFileType = folder.FileTypeOptions.Single(o => o.Value == BackupFileTypes.Custom);
        await UiHarness.PumpAsync(50);
        backup.Sources[2].Extensions = "pdf";
        await UiHarness.WaitForAsync(() => phone.Node.Settings.Current.Backups.Single().Sources.ElementAtOrDefault(2)?.Extensions == "pdf", "saved", 5000);
        Assert.Equal(BackupFileTypes.Custom, phone.Node.Settings.Current.Backups.Single().Sources[2].FileTypes);
        await UiHarness.PumpAsync(100);
        Assert.Empty(phone.TextOutsideWindow());
        var heading = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(phone.Window).OfType<Avalonia.Controls.TextBlock>()
            .First(t => t.Text == "What is backed up");
        Avalonia.Controls.ControlExtensions.BringIntoView(heading, new Avalonia.Rect(0, -200, 10, 700));
        await UiHarness.PumpAsync(100);
        phone.Screenshot("backup-phone");

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

    private static IEnumerable<string> Buttons(UiHarness h) =>
        Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(h.Window).OfType<Avalonia.Controls.Button>()
            .Where(b => b.IsEffectivelyVisible).Select(b => b.Content as string).OfType<string>();
}
