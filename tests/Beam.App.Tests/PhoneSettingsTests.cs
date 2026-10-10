using Avalonia.Headless.XUnit;
using Beam.App.Platform;
using Beam.App.ViewModels.Dialogs;

namespace Beam.App.Tests;

public class PhoneSettingsTests
{
    [AvaloniaFact]
    public async Task PhoneCanChangeWhereReceivedFilesAreSaved()
    {
        await using var phone = new UiHarness("Diaz's Galaxy", width: 400, height: 860, phone: true);
        const string defaultFolder = "/storage/emulated/0/Download/Beam";
        phone.Platform.DefaultReceiveFolder = defaultFolder;
        phone.Node.Settings.Update(s => s.ReceiveFolder = defaultFolder);
        await phone.InitializeAsync();
        var settings = phone.ViewModel.Settings;
        phone.ViewModel.IsSettingsPage = true;
        await UiHarness.PumpAsync(100);
        Assert.Contains("Change…", phone.VisibleTexts());
        Assert.True(settings.UsesDefaultFolder);

        // The picker hands back a content:// address; the platform turns it into a folder Beam can save into.
        phone.Ui.FolderToPick = "content://com.android.externalstorage.documents/tree/primary%3ADocuments%2FWork";
        phone.Platform.FolderCheck = _ => new ReceiveFolderChoice("/storage/emulated/0/Documents/Work");
        settings.ChangeFolderCommand.Execute(null);
        await UiHarness.WaitForAsync(() => phone.Node.Settings.Current.ReceiveFolder == "/storage/emulated/0/Documents/Work", "folder changed");
        Assert.False(settings.UsesDefaultFolder);
        await UiHarness.PumpAsync(100);
        Assert.Contains("Use Download/Beam", phone.VisibleTexts());
        Assert.Empty(phone.TextOutsideWindow());
        phone.Screenshot("phone-settings-folder");

        settings.ResetFolderCommand.Execute(null);
        Assert.Equal(defaultFolder, phone.Node.Settings.Current.ReceiveFolder);

        // A folder Android won't let Beam write to is refused with an explanation.
        phone.Platform.FolderCheck = _ => new ReceiveFolderChoice(null, "Beam can't save files in that folder. Choose Download or Documents on the phone, or a folder inside them.");
        settings.ChangeFolderCommand.Execute(null);
        await UiHarness.WaitForAsync(() => phone.ViewModel.Dialog is ConfirmViewModel, "explanation");
        Assert.Equal(defaultFolder, phone.Node.Settings.Current.ReceiveFolder);
        phone.ViewModel.Dialog!.Dismiss();

        // The log can be sent for troubleshooting.
        settings.OpenLogsCommand.Execute(null);
        Assert.Contains(phone.Platform.Calls, c => c.StartsWith("share-log:"));
    }
}
