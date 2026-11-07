using Avalonia.Headless.XUnit;
using Beam.App.Platform;
using Beam.App.ViewModels;
using Beam.App.ViewModels.Dialogs;
using Beam.Core.Tests;
using Beam.Core.Transfer;

namespace Beam.App.Tests;

public class BrowseUiTests
{
    /// <summary>The phone adds the PC as a trusted device by scanning its code (Settings → Trusted devices on both).</summary>
    private static async Task TrustAsync(UiHarness pc, UiHarness phone)
    {
        pc.ViewModel.IsSettingsPage = true;
        pc.ViewModel.Settings.AddTrustedCommand.Execute(null);
        await UiHarness.WaitForAsync(() => pc.ViewModel.Dialog is TrustCodeViewModel, "the PC's code");
        var dialog = (TrustCodeViewModel)pc.ViewModel.Dialog!;
        await UiHarness.PumpAsync(100);
        pc.Screenshot("trust-code");

        var code = dialog.TrustCode!;
        code.Addresses = new List<string> { $"127.0.0.1:{pc.Node.Transfers.Port}" };
        phone.Platform.ScannedCode = code.AppendTo(null);
        phone.ViewModel.IsSettingsPage = true;
        phone.ViewModel.Settings.AddTrustedCommand.Execute(null);
        await UiHarness.WaitForAsync(() => phone.ViewModel.Dialog is TrustCodeViewModel, "the phone's own code");
        var phoneDialog = (TrustCodeViewModel)phone.ViewModel.Dialog!;
        Assert.True(phoneDialog.ShowsScan);
        phoneDialog.ScanCommand.Execute(null);
        await UiHarness.WaitForAsync(() => phone.ViewModel.Dialog is ConfirmViewModel { Title: "Diaz's PC is a trusted device now" }, "added");
        ((ConfirmViewModel)phone.ViewModel.Dialog!).PrimaryCommand.Execute(null);

        await UiHarness.WaitForAsync(() => dialog.IsAdded, "the PC saw the phone");
        Assert.Contains("Diaz's Galaxy", dialog.AddedText);
        dialog.CloseCommand.Execute(null);
        await UiHarness.WaitForAsync(() => pc.ViewModel.Settings.TrustedDevices.Count == 1, "listed on the PC");
        Assert.Single(phone.ViewModel.Settings.TrustedDevices);
        phone.Node.Discovery.ReportReachable(pc.AsDevice()); // found on the Wi-Fi (no discovery in tests)
    }

    [AvaloniaFact]
    public async Task ThePhoneBrowsesThePcAfterAddingItWithACode()
    {
        await using var pc = new UiHarness("Diaz's PC");
        await using var phone = new UiHarness("Diaz's Galaxy", width: 400, height: 860, phone: true);
        await pc.InitializeAsync();
        await phone.InitializeAsync();
        await TrustAsync(pc, phone);
        Assert.Equal(1, phone.Platform.OwnerChecks); // the phone's owner confirmed once

        // The PC lets trusted devices browse: its usual folders are shared to start with.
        pc.ViewModel.Settings.BrowsingEnabled = true;
        await UiHarness.WaitForAsync(() => pc.ViewModel.Settings.SharedFolders.Count == 2, "the default folders");
        TestFiles.Write(Path.Combine(pc.SharedFolder("Pictures"), "beach.jpg"), 200_000, 3);
        Directory.CreateDirectory(Path.Combine(pc.SharedFolder("Pictures"), "Trip"));
        await UiHarness.PumpAsync(100);
        pc.Screenshot("browse-settings");

        // The phone opens Browse from Settings → Trusted devices; the PC asks.
        phone.ViewModel.Settings.TrustedDevices.Single().BrowseCommand.Execute(null);
        await UiHarness.WaitForAsync(() => pc.ViewModel.Dialog is BrowseRequestViewModel, "the PC asks");
        var request = (BrowseRequestViewModel)pc.ViewModel.Dialog!;
        Assert.Equal("Diaz's Galaxy wants to browse your files", request.Title);
        Assert.Equal("Always allow Diaz's Galaxy to browse my files", request.AlwaysText);
        await UiHarness.PumpAsync(100);
        pc.Screenshot("browse-request");
        phone.Screenshot("browse-connecting");
        request.Always = true;
        request.AllowCommand.Execute(null);

        var browse = phone.ViewModel.Browse!;
        Assert.Equal(Page.Browse, phone.ViewModel.CurrentPageKind);
        await UiHarness.WaitForAsync(() => browse.IsReady && browse.Entries.Count == 2, "the shared folders");
        Assert.Equal(new[] { "Pictures", "Documents" }, browse.Entries.Select(e => e.Name));
        Assert.True(pc.Node.Settings.Current.TrustedDevices.Single().AlwaysBrowse);
        // The PC shows who is browsing, with Stop.
        await UiHarness.WaitForAsync(() => pc.ViewModel.HasVisits, "the bar on the PC");
        Assert.Contains("Diaz's Galaxy is browsing your shared folders", pc.VisibleTexts());

        browse.Entries[0].OpenCommand.Execute(null);
        await UiHarness.WaitForAsync(() => browse.Entries.Any(e => e.Name == "beach.jpg"), "Pictures opened");
        Assert.Equal(new[] { "Shared folders", "Pictures" }, browse.Crumbs.Select(c => c.Name));
        Assert.Equal(new[] { "Trip", "beach.jpg" }, browse.Entries.Select(e => e.Name));
        Assert.False(browse.CanAddHere); // adding files is off
        browse.Entries[1].IsChecked = true;
        await UiHarness.PumpAsync(100);
        Assert.True(browse.ShowsBottomBar);
        phone.Screenshot("browse-folder");
        pc.Screenshot("browse-being-browsed");

        browse.DownloadCommand.Execute(null);
        await UiHarness.WaitForAsync(() => File.Exists(Path.Combine(phone.ReceiveFolder, "beach.jpg")), "downloaded");
        Assert.Equal(TestFiles.Hash(Path.Combine(pc.SharedFolder("Pictures"), "beach.jpg")), TestFiles.Hash(Path.Combine(phone.ReceiveFolder, "beach.jpg")));
        Assert.Contains(phone.ViewModel.Transfers, t => t.Session.Direction == TransferDirection.Receive);

        // Back goes up a folder, then closes Browse.
        Assert.True(phone.ViewModel.GoBack());
        await UiHarness.WaitForAsync(() => browse.Crumbs.Count == 1, "back at the shared folders");
        Assert.True(phone.ViewModel.GoBack());
        Assert.Equal(Page.Settings, phone.ViewModel.CurrentPageKind);
        Assert.Null(phone.ViewModel.Browse);
        await UiHarness.WaitForAsync(() => !pc.ViewModel.HasVisits, "the visit ended");
    }

    [AvaloniaFact]
    public async Task StopEndsAVisitAndAddingFilesWorksWhenAllowed()
    {
        await using var pc = new UiHarness("Diaz's PC");
        await using var phone = new UiHarness("Diaz's Galaxy", width: 400, height: 860, phone: true);
        await pc.InitializeAsync();
        await phone.InitializeAsync();
        await TrustAsync(pc, phone);
        pc.ViewModel.Settings.BrowsingEnabled = true;
        pc.ViewModel.Settings.BrowseAllowAdding = true;
        await UiHarness.WaitForAsync(() => pc.Node.Settings.Current.BrowseAllowAdding, "adding allowed");

        phone.ViewModel.Settings.TrustedDevices.Single().BrowseCommand.Execute(null);
        await UiHarness.WaitForAsync(() => pc.ViewModel.Dialog is BrowseRequestViewModel, "the PC asks");
        ((BrowseRequestViewModel)pc.ViewModel.Dialog!).AllowCommand.Execute(null);
        var browse = phone.ViewModel.Browse!;
        await UiHarness.WaitForAsync(() => browse.IsReady && browse.Entries.Count == 2, "the shared folders");
        browse.Entries.Single(e => e.Name == "Documents").OpenCommand.Execute(null);
        await UiHarness.WaitForAsync(() => browse.CanAddHere, "in Documents");

        var file = phone.CreateFile("notes.txt", 5000);
        phone.Ui.FilesToPick.Add(file);
        browse.AddFilesCommand.Execute(null);
        await UiHarness.WaitForAsync(() => browse.Entries.Any(e => e.Name == "notes.txt"), "added and listed");
        Assert.Equal(TestFiles.Hash(file), TestFiles.Hash(Path.Combine(pc.SharedFolder("Documents"), "notes.txt")));

        // Stop on the PC: the phone can't carry on.
        await UiHarness.WaitForAsync(() => pc.ViewModel.HasVisits, "the bar on the PC");
        pc.ViewModel.Visits.Single().StopCommand.Execute(null);
        await UiHarness.WaitForAsync(() => !pc.ViewModel.HasVisits, "stopped");
        browse.Entries.Single(e => e.Name == "notes.txt"); // still shown
        browse.BackCommand.Execute(null); // back to the shared folders: no request needed
        await UiHarness.PumpAsync(100);
        Assert.Single(pc.Node.Settings.Current.TrustedDevices); // stopping doesn't remove the trust
    }

    [AvaloniaFact]
    public async Task UntrustedDevicesCantBeBrowsed()
    {
        await using var pc = new UiHarness("Diaz's PC");
        await using var phone = new UiHarness("Diaz's Galaxy", width: 400, height: 860, phone: true);
        await pc.InitializeAsync();
        await phone.InitializeAsync();
        pc.ViewModel.Settings.BrowsingEnabled = true;

        // Not trusted: Send offers no Browse, and opening it anyway says why.
        phone.Node.Discovery.ReportReachable(pc.AsDevice());
        await UiHarness.PumpAsync(200);
        Assert.False(phone.ViewModel.Home.CanBrowse);
        var task = phone.ViewModel.OpenBrowseAsync(pc.AsDevice());
        await UiHarness.WaitForAsync(() => task.IsCompleted, "refused");
        Assert.True(phone.ViewModel.Browse!.HasError);
        Assert.Contains("trusted device", phone.ViewModel.Browse.Error);
        Assert.Null(pc.ViewModel.Dialog);
        phone.Screenshot("browse-not-trusted");
    }

    [AvaloniaFact]
    public async Task TurningBrowsingOnNeedsTheOwnerAndAllFilesAccessOnAPhone()
    {
        await using var phone = new UiHarness("Diaz's Galaxy", width: 400, height: 860, phone: true);
        await phone.InitializeAsync();
        phone.ViewModel.IsSettingsPage = true;

        phone.Platform.OwnerAnswer = OwnerCheck.Refused;
        phone.ViewModel.Settings.BrowsingEnabled = true;
        await UiHarness.PumpAsync(100);
        Assert.False(phone.Node.Settings.Current.BrowsingEnabled);
        Assert.True(phone.ViewModel.Settings.HasTrustMessage);

        phone.Platform.OwnerAnswer = OwnerCheck.Confirmed;
        phone.Platform.GrantWhenAsked = false;
        phone.ViewModel.Settings.BrowsingEnabled = true;
        await UiHarness.PumpAsync(100);
        Assert.Contains("permission:AllFiles", phone.Platform.Calls);
        Assert.False(phone.Node.Settings.Current.BrowsingEnabled);

        phone.Platform.GrantWhenAsked = true;
        phone.ViewModel.Settings.BrowsingEnabled = true;
        await UiHarness.WaitForAsync(() => phone.Node.Settings.Current.BrowsingEnabled, "on");
        await UiHarness.PumpAsync(100);
        phone.Screenshot("browse-settings-phone");
        Assert.Empty(phone.TextOutsideWindow());
    }

    [AvaloniaFact]
    public async Task APhoneSharesItsStorageOrDownloadByName()
    {
        await using var phone = new UiHarness("Diaz's Galaxy", width: 400, height: 860, phone: true);
        await phone.InitializeAsync();
        phone.ViewModel.IsSettingsPage = true;
        phone.ViewModel.Settings.BrowsingEnabled = true;
        await UiHarness.WaitForAsync(() => phone.Node.Settings.Current.BrowsingEnabled, "on");
        var storage = phone.SharedFolder("storage");
        var download = phone.SharedFolder("storage/Download");
        phone.Platform.Shareable.Add(("Whole phone storage", storage));
        phone.Platform.Shareable.Add(("Download", download));
        phone.Platform.Shareable.Add(("Pictures", phone.SharedFolder("Pictures"))); // shared already (the defaults)

        phone.ViewModel.Settings.AddSharedFolderCommand.Execute(null);
        await UiHarness.WaitForAsync(() => phone.ViewModel.Dialog is ShareFolderViewModel, "the list");
        var dialog = (ShareFolderViewModel)phone.ViewModel.Dialog!;
        Assert.Equal(new[] { "Whole phone storage", "Download", "Pictures" }, dialog.Folders.Select(f => f.Name));
        Assert.False(dialog.Folders[2].ChooseCommand.CanExecute(null));
        await UiHarness.PumpAsync(100);
        phone.Screenshot("browse-share-folder-phone");
        Assert.Empty(phone.TextOutsideWindow());

        dialog.Folders[0].ChooseCommand.Execute(null);
        await UiHarness.WaitForAsync(() => phone.Node.Settings.Current.SharedFolders.Any(f => f.Path == storage), "storage shared");
        Assert.Equal("Whole phone storage", phone.Node.Settings.Current.SharedFolders.Single(f => f.Path == storage).Name);

        // "Another folder…": the system's picker, as before.
        phone.Ui.FolderToPick = phone.SharedFolder("Other");
        phone.ViewModel.Settings.AddSharedFolderCommand.Execute(null);
        await UiHarness.WaitForAsync(() => phone.ViewModel.Dialog is ShareFolderViewModel, "the list again");
        ((ShareFolderViewModel)phone.ViewModel.Dialog!).OtherCommand.Execute(null);
        await UiHarness.WaitForAsync(() => phone.Node.Settings.Current.SharedFolders.Any(f => f.Name == "Other"), "picked folder shared");
    }

    [AvaloniaFact]
    public async Task APhoneWithoutAllFilesAccessSaysSoAndOffersIt()
    {
        await using var phone = new UiHarness("Diaz's Galaxy", width: 400, height: 860, phone: true);
        await phone.InitializeAsync();
        phone.ViewModel.IsSettingsPage = true;
        phone.ViewModel.Settings.BrowsingEnabled = true;
        await UiHarness.WaitForAsync(() => phone.Node.Settings.Current.BrowsingEnabled, "on");
        Assert.False(phone.ViewModel.Settings.NeedsAllFiles);

        // Turned off in Android's settings since.
        phone.Platform.Granted.Remove(AppPermission.AllFiles);
        phone.ViewModel.IsHomePage = true;
        phone.ViewModel.IsSettingsPage = true;
        Assert.True(phone.ViewModel.Settings.NeedsAllFiles);

        phone.ViewModel.Settings.AllowAllFilesCommand.Execute(null);
        await UiHarness.WaitForAsync(() => !phone.ViewModel.Settings.NeedsAllFiles, "allowed again");
    }
}
