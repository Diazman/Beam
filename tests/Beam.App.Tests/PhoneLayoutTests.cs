using Avalonia.Headless.XUnit;
using Beam.App.ViewModels;
using Beam.Core.Discovery;
using Beam.Core.Tests;
using Beam.Core.Transfer;

namespace Beam.App.Tests;

/// <summary>The phone layout (MobileView): bottom tabs, every page at phone size, transfers tab. Screenshots: artifacts/screenshots/phone-*.png.</summary>
public class PhoneLayoutTests
{
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
        Assert.Contains("Send files", app.VisibleTexts());
        app.Screenshot("phone-1-send");

        app.ViewModel.IsTransfersPage = true;
        await UiHarness.PumpAsync(100);
        Assert.Contains("No transfers yet", app.VisibleTexts());
        Assert.DoesNotContain("History", app.VisibleTexts()); // one tab for running and past transfers
        app.Screenshot("phone-2-transfers-empty");

        app.ViewModel.IsSettingsPage = true;
        await UiHarness.PumpAsync(100);
        app.Screenshot("phone-4-settings");
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
        app.ViewModel.IsTransfersPage = true;
        await UiHarness.PumpAsync(100);
        Assert.Equal(TransferState.Completed, app.ViewModel.Transfers[0].State);
        await UiHarness.WaitForAsync(() => app.ViewModel.History.Items.Count == 1, "in the history");
        await UiHarness.PumpAsync(100);
        Assert.Contains("History", app.VisibleTexts());
        Assert.Contains("Diaz's Laptop", string.Join(" ", app.VisibleTexts()));
        app.Screenshot("phone-5-transfers");
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
