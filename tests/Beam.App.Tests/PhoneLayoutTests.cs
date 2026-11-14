using Avalonia.Headless.XUnit;
using Beam.App.ViewModels;
using Beam.Core.Discovery;
using Beam.Core.Tests;
using Beam.Core.Transfer;

namespace Beam.App.Tests;

/// <summary>The phone layout (MobileView): bottom tabs, every page at phone size, transfers tab. Screenshots: artifacts/screenshots/phone-*.png.</summary>
public class PhoneLayoutTests
{
    private static IEnumerable<string> Buttons(UiHarness h) =>
        Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(h.Window).OfType<Avalonia.Controls.Button>()
            .Where(b => b.IsEffectivelyVisible).Select(b => b.Content as string).OfType<string>();

    [AvaloniaFact]
    public async Task EveryTabFitsAPhoneScreen()
    {
        await using var app = new UiHarness("Diaz's Phone", width: 412, height: 892, phone: true);
        await app.InitializeAsync();
        app.AddFakeDevice("Diaz's Laptop", DeviceKinds.Laptop);
        app.AddFakeDevice("Office desktop");
        await UiHarness.WaitForAsync(() => app.ViewModel.Home.Devices.Count == 2, "devices");
        app.ViewModel.Home.Devices[0].SelectCommand.Execute(null);
        app.ViewModel.Home.AddPaths(new[] { app.CreateFile("IMG_2041.jpg", 2_400_000), app.CreateFile("Notes.pdf", 300_000, 2) });
        await UiHarness.PumpAsync(100);
        Assert.Contains("Send", app.VisibleTexts());
        Assert.Empty(app.TextOutsideWindow());
        app.Screenshot("phone-1-send");

        app.ViewModel.IsReceivePage = true;
        await UiHarness.PumpAsync(100);
        Assert.Contains("Ready to receive", app.VisibleTexts());
        Assert.Empty(app.TextOutsideWindow());
        app.Screenshot("phone-2-receive");

        app.ViewModel.IsFilesPage = true;
        await UiHarness.PumpAsync(100);
        Assert.Contains("Browse your other devices' files", app.VisibleTexts()); // no trusted device yet
        Assert.Contains("Add a trusted device", app.VisibleTexts());
        Assert.Contains("WHAT YOU SHARE", app.VisibleTexts());
        Assert.DoesNotContain("No Beam app on the other device?", app.VisibleTexts()); // computers only
        Assert.Empty(app.TextOutsideWindow());
        app.Screenshot("phone-3-files");

        app.Platform.SupportsBackup = true;
        app.ViewModel.IsBackupPage = true;
        await UiHarness.PumpAsync(100);
        Assert.Contains("Set up backup", app.VisibleTexts().Concat(Buttons(app)));
        Assert.Empty(app.TextOutsideWindow());
        app.Screenshot("phone-3-backup");

        app.ViewModel.IsActivityPage = true;
        await UiHarness.PumpAsync(100);
        Assert.Contains("No transfers yet", app.VisibleTexts());
        Assert.DoesNotContain("HISTORY", app.VisibleTexts()); // one tab for running and past transfers
        Assert.Empty(app.TextOutsideWindow());
        app.Screenshot("phone-4-activity-empty");

        // Settings: the gear on the main pages, the list of sections, then one section; Back returns to the list.
        Assert.True(app.ViewModel.ShowsSettingsButton);
        app.ViewModel.OpenSettingsCommand.Execute(null);
        Assert.True(app.ViewModel.IsSettingsPage);
        Assert.False(app.ViewModel.ShowsSettingsButton);
        await UiHarness.PumpAsync(100);
        Assert.Contains("Connection", app.VisibleTexts());
        Assert.Single(app.VisibleTexts(), t => t == "Backup"); // the tab only: no longer a Settings section
        Assert.DoesNotContain("Shared folders", app.VisibleTexts()); // on Files now
        Assert.DoesNotContain("Appearance", app.VisibleTexts());
        Assert.Empty(app.TextOutsideWindow());
        app.Screenshot("phone-5-settings");
        app.ViewModel.Settings.Section = SettingsSection.General;
        await UiHarness.PumpAsync(100);
        Assert.Contains("Appearance", app.VisibleTexts());
        Assert.Empty(app.TextOutsideWindow());
        app.Screenshot("phone-6-settings-general");
        Assert.True(app.ViewModel.GoBack());
        Assert.Equal(SettingsSection.None, app.ViewModel.Settings.Section);
        Assert.True(app.ViewModel.IsSettingsPage);
    }

    [AvaloniaFact]
    public async Task TransfersTabShowsProgressAndResults()
    {
        await using var app = new UiHarness("Diaz's Phone", width: 412, height: 892, phone: true);
        await app.InitializeAsync();
        await using var laptop = new TestNode("Diaz's Laptop");
        var file = app.CreateFile("Holiday.mp4", 3_000_000);
        await app.ViewModel.StartSendAsync(laptop.AsDevice(), new[] { file });
        await UiHarness.WaitForAsync(() => app.ViewModel.Transfers.Any(t => t.IsFinished), "transfer");
        app.ViewModel.IsActivityPage = true;
        await UiHarness.PumpAsync(100);
        Assert.Equal(TransferState.Completed, app.ViewModel.Transfers[0].State);
        await UiHarness.WaitForAsync(() => app.ViewModel.History.Items.Count == 1, "in the history");
        await UiHarness.PumpAsync(100);
        Assert.Contains("HISTORY", app.VisibleTexts());
        Assert.Contains("Diaz's Laptop", string.Join(" ", app.VisibleTexts()));
        app.Screenshot("phone-7-activity");
    }

    [AvaloniaFact]
    public async Task PhoneWordingAndNothingSticksOutInUzbek()
    {
        try
        {
            await using var app = new UiHarness("Diyor's Note20 Ultra", firstRunDone: false, width: 412, height: 892, language: "uz", phone: true);
            _ = app.InitializeAsync();
            await UiHarness.WaitForAsync(() => app.ViewModel.HasDialog, "welcome");
            await UiHarness.PumpAsync(100);
            Assert.Contains(Core.Localization.L.T("Name this phone"), app.VisibleTexts());
            Assert.Empty(app.TextOutsideWindow());
            app.Screenshot("phone-uz-welcome");
            ((Beam.App.ViewModels.Dialogs.WelcomeViewModel)app.ViewModel.Dialog!).StartCommand.Execute(null);
            await UiHarness.WaitForAsync(() => !app.ViewModel.HasDialog, "welcome closed");

            app.AddFakeDevice("DILSHODJON");
            await UiHarness.WaitForAsync(() => app.ViewModel.Home.Devices.Count == 1, "device");
            await UiHarness.PumpAsync(100);
            Assert.Empty(app.TextOutsideWindow());
            app.Screenshot("phone-uz-send");

            app.ViewModel.IsSettingsPage = true;
            app.ViewModel.Settings.Section = SettingsSection.General;
            await UiHarness.PumpAsync(100);
            var texts = app.VisibleTexts();
            Assert.Contains(Core.Localization.L.T("Let nearby devices find this phone"), texts);
            Assert.Empty(app.TextOutsideWindow());
            app.Screenshot("phone-uz-settings");

            app.ViewModel.Home.ConnectByAddressCommand.Execute(null);
            await UiHarness.WaitForAsync(() => app.ViewModel.HasDialog, "add device");
            await UiHarness.PumpAsync(100);
            Assert.Empty(app.TextOutsideWindow());
            app.Screenshot("phone-uz-add-device");
        }
        finally
        {
            Core.Localization.L.IsPhone = false;
        }
    }
}

public class PhoneBehaviourTests
{
    [AvaloniaFact]
    public async Task BackGoesToSendThenLeavesAndClosesDialogsFirst()
    {
        await using var app = new UiHarness("Diaz's Galaxy", width: 400, height: 860, phone: true);
        await app.InitializeAsync();
        app.ViewModel.IsSettingsPage = true;
        Assert.True(app.ViewModel.GoBack());
        Assert.True(app.ViewModel.IsHomePage);

        // A page under Devices goes back to Devices; a Settings section to the list of sections.
        app.ViewModel.IsSettingsPage = true;
        app.ViewModel.Settings.Section = SettingsSection.About;
        Assert.True(app.ViewModel.GoBack());
        Assert.True(app.ViewModel.IsSettingsPage);
        Assert.Equal(SettingsSection.None, app.ViewModel.Settings.Section);
        Assert.True(app.ViewModel.GoBack());
        Assert.True(app.ViewModel.IsHomePage);

        app.ViewModel.Home.ConnectByAddressCommand.Execute(null);
        await UiHarness.WaitForAsync(() => app.ViewModel.HasDialog, "dialog");
        Assert.True(app.ViewModel.GoBack());
        await UiHarness.WaitForAsync(() => !app.ViewModel.HasDialog, "dialog closed");

        Assert.False(app.ViewModel.GoBack()); // on Send: the phone leaves Beam as usual
    }

    [AvaloniaFact]
    public async Task NotificationsAreExplainedBeforeAndroidAsks()
    {
        await using var app = new UiHarness("Diaz's Galaxy", width: 400, height: 860, phone: true);
        app.Platform.Granted.Clear();
        var init = app.InitializeAsync();
        await UiHarness.WaitForAsync(() => app.ViewModel.Dialog is ViewModels.Dialogs.ConfirmViewModel, "explanation");
        var dialog = (ViewModels.Dialogs.ConfirmViewModel)app.ViewModel.Dialog!;
        Assert.Equal("Get notified about incoming files", dialog.Title);
        Assert.DoesNotContain("permission:Notifications", app.Platform.Calls); // not asked before the explanation
        app.Screenshot("phone-6-permission");
        dialog.PrimaryCommand.Execute(null);
        await init;
        Assert.Contains("permission:Notifications", app.Platform.Calls);
        Assert.DoesNotContain("permission:NearbyDevices", app.Platform.Calls); // asked when connecting directly, not at start
    }

    [AvaloniaFact]
    public async Task IncomingFilesCanBeAcceptedFromTheNotification()
    {
        await using var phone = new UiHarness("Diaz's Galaxy", width: 400, height: 860, phone: true);
        await using var laptop = new UiHarness("Laptop");
        await phone.InitializeAsync();
        await laptop.InitializeAsync();
        phone.Ui.IsWindowActive = false; // Beam is in the background
        var file = laptop.CreateFile("Slides.pdf", 50_000);
        await laptop.ViewModel.StartSendAsync(phone.AsDevice(), new[] { file });

        await UiHarness.WaitForAsync(() => phone.Platform.Questions.Count == 1, "notification with buttons");
        var buttons = phone.Platform.Questions.Values.Single();
        Assert.Equal(new[] { "Accept", "Decline" }, buttons.Select(b => b.Label));
        buttons[0].OnClick();
        await UiHarness.WaitForAsync(() => File.Exists(Path.Combine(phone.ReceiveFolder, "Slides.pdf")), "received");
        Assert.Empty(phone.Platform.Questions); // answered: the notification is gone
    }
}
