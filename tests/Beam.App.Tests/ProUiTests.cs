using Avalonia.Headless.XUnit;
using Beam.App.Services;
using Beam.App.ViewModels;
using Beam.App.ViewModels.Dialogs;
using Beam.Core.Licensing;
using Beam.Core.Tests;
using Beam.Core.Transfer;

namespace Beam.App.Tests;

public class ProUiTests
{
    [AvaloniaFact]
    public async Task FreeEditionPicksOneComputerAndShowsItsLimits()
    {
        await using var app = new UiHarness(pro: false);
        await app.InitializeAsync();
        app.AddFakeDevice("Laptop");
        app.AddFakeDevice("John's PC");
        await UiHarness.WaitForAsync(() => app.ViewModel.Home.Devices.Count == 2, "devices");
        var home = app.ViewModel.Home;

        Assert.True(home.IsFree);
        Assert.True(home.ShowMultiSendUpsell);
        Assert.Equal("Free · up to 5 MB/s · 10 of 10 free sends left today", home.PlanText);

        home.Devices[0].SelectCommand.Execute(null);
        home.Devices[1].SelectCommand.Execute(null);
        Assert.Single(home.SelectedDevices);
        Assert.Equal("Laptop", home.SelectedDevice!.Name);
        Assert.False(home.Devices[0].IsSelected);

        home.AddPaths(new[] { app.CreateFile("report.pdf", 1000) });
        await UiHarness.PumpAsync();
        app.Screenshot("pro-01-home-free");

        app.ViewModel.ShowSettingsCommand.Execute(null);
        await UiHarness.PumpAsync();
        Assert.Equal("Beam Free", app.ViewModel.Settings.PlanTitle);
        Assert.Contains("Today you've used 0 of 10", app.ViewModel.Settings.PlanDescription);
    }

    [AvaloniaFact]
    public async Task UpgradingUnlocksSendingToSeveralComputers()
    {
        await using var app = new UiHarness(pro: false);
        await app.InitializeAsync();
        await using var laptop = new TestNode("Laptop");
        await using var desktop = new TestNode("Office PC");
        app.Node.Discovery.ReportReachable(laptop.AsDevice());
        app.Node.Discovery.ReportReachable(desktop.AsDevice());
        var home = app.ViewModel.Home;
        await UiHarness.WaitForAsync(() => home.Devices.Count == 2, "devices");

        home.UpgradeCommand.Execute(null);
        await UiHarness.WaitForAsync(() => app.ViewModel.Dialog is UpgradeViewModel, "upgrade dialog");
        var dialog = (UpgradeViewModel)app.ViewModel.Dialog!;
        Assert.Equal("Upgrade · $4.99", dialog.BuyText);
        Assert.Equal(4, dialog.Benefits.Count);
        app.Screenshot("pro-02-upgrade-dialog");

        dialog.BuyCommand.Execute(null);
        await UiHarness.WaitForAsync(() => !app.ViewModel.HasDialog, "dialog closes after purchase");
        Assert.Equal(1, app.Store.Purchases);
        Assert.True(app.Node.Edition.IsPro);
        Assert.False(home.ShowMultiSendUpsell);
        Assert.Equal("Beam Pro", app.ViewModel.Settings.PlanTitle);

        home.Devices[0].SelectCommand.Execute(null);
        home.Devices[1].SelectCommand.Execute(null);
        Assert.Equal(2, home.SelectedDevices.Count);
        var file = app.CreateFile("Slides.pptx", 3L * 1024 * 1024, 5);
        home.AddPaths(new[] { file });
        await UiHarness.PumpAsync();
        Assert.Equal("Send “Slides.pptx” (3 MB) to Laptop and Office PC", home.SendSummary);
        app.Screenshot("pro-03-home-several-selected");

        home.SendCommand.Execute(null);
        await UiHarness.WaitForAsync(() => app.ViewModel.Transfers.Count == 2 && app.ViewModel.Transfers.All(t => t.IsFinished), "both transfers", 60000);
        Assert.All(app.ViewModel.Transfers, t => Assert.Equal(TransferState.Completed, t.State));
        Assert.Equal(TestFiles.Hash(file), TestFiles.Hash(Path.Combine(laptop.ReceiveFolder, "Slides.pptx")));
        Assert.Equal(TestFiles.Hash(file), TestFiles.Hash(Path.Combine(desktop.ReceiveFolder, "Slides.pptx")));
    }

    [AvaloniaFact]
    public async Task DailyLimitOffersUpgradeInsteadOfSending()
    {
        await using var app = new UiHarness(pro: false);
        await app.InitializeAsync();
        for (var i = 0; i < FreeLimits.SendsPerDay; i++) Assert.True(app.Node.Quota.TryUse());
        app.AddFakeDevice("Laptop");
        var home = app.ViewModel.Home;
        await UiHarness.WaitForAsync(() => home.SelectedDevice != null, "auto-selected device");
        await UiHarness.WaitForAsync(() => home.PlanText.Contains("no free sends left"), "plan text");
        home.AddPaths(new[] { app.CreateFile("a.txt", 10) });
        await UiHarness.PumpAsync();

        home.SendCommand.Execute(null);
        await UiHarness.WaitForAsync(() => app.ViewModel.Dialog is UpgradeViewModel, "upgrade dialog");
        var dialog = (UpgradeViewModel)app.ViewModel.Dialog!;
        Assert.Contains("today's 10 free sends", dialog.Reason);
        app.Screenshot("pro-04-limit-reached");

        dialog.NotNowCommand.Execute(null);
        await UiHarness.WaitForAsync(() => !app.ViewModel.HasDialog, "dialog closed");
        Assert.Empty(app.ViewModel.Transfers);
        Assert.True(home.HasItems); // nothing lost: the files are still there to send later
    }

    [AvaloniaFact]
    public async Task BuildsOutsideTheStoreLinkToTheStore()
    {
        await using var app = new UiHarness(pro: false);
        app.Store.CanPurchase = false;
        await app.InitializeAsync();

        app.ViewModel.Settings.UpgradeCommand.Execute(null);
        await UiHarness.WaitForAsync(() => app.ViewModel.Dialog is UpgradeViewModel, "upgrade dialog");
        var dialog = (UpgradeViewModel)app.ViewModel.Dialog!;
        Assert.Equal("Get Beam from the Microsoft Store", dialog.BuyText);
        dialog.BuyCommand.Execute(null);
        await UiHarness.PumpAsync();
        Assert.Contains("open-url:" + ProService.StorePageUrl, app.Platform.Calls);
        Assert.Equal(0, app.Store.Purchases);
        Assert.False(app.Node.Edition.IsPro);
    }

    [AvaloniaFact]
    public async Task LaunchPeriodIsUnlimitedAndOffersFreeProToKeep()
    {
        await using var app = new UiHarness(pro: false, launchPeriod: true);
        app.Store.Price = "Free";
        await app.InitializeAsync();
        app.AddFakeDevice("Laptop");
        app.AddFakeDevice("John's PC");
        var home = app.ViewModel.Home;
        await UiHarness.WaitForAsync(() => home.Devices.Count == 2, "devices");

        Assert.False(home.ShowFreeLimits);
        Assert.False(home.ShowMultiSendUpsell);
        home.Devices[0].SelectCommand.Execute(null);
        home.Devices[1].SelectCommand.Execute(null);
        Assert.Equal(2, home.SelectedDevices.Count); // Pro feature, free during launch
        for (var i = 0; i < FreeLimits.SendsPerDay * 2; i++) Assert.True(app.Node.Quota.TryUse());

        var settings = app.ViewModel.Settings;
        Assert.Equal("Beam Pro is free while Beam is new", settings.PlanTitle);
        Assert.Equal("Claim Beam Pro free", settings.UpgradeButtonText);
        app.ViewModel.ShowSettingsCommand.Execute(null);
        await UiHarness.PumpAsync();
        app.Screenshot("pro-05-launch-settings");

        settings.UpgradeCommand.Execute(null);
        await UiHarness.WaitForAsync(() => app.ViewModel.Dialog is UpgradeViewModel, "claim dialog");
        var dialog = (UpgradeViewModel)app.ViewModel.Dialog!;
        Assert.Equal("Claim Beam Pro", dialog.Title);
        Assert.Equal("Claim for free", dialog.BuyText);
        Assert.Equal("Yours to keep", dialog.Benefits[0].Title);
        app.Screenshot("pro-06-claim-dialog");

        dialog.BuyCommand.Execute(null);
        await UiHarness.WaitForAsync(() => !app.ViewModel.HasDialog, "claimed");
        Assert.True(app.Node.Edition.IsPro);
        Assert.Equal("Beam Pro", settings.PlanTitle);
    }

    [AvaloniaFact]
    public async Task PaidPriceIsNeverShownAsFree()
    {
        await using var app = new UiHarness(pro: false, launchPeriod: true);
        app.Store.Price = "₺149,99";
        await app.InitializeAsync();
        app.ViewModel.Settings.UpgradeCommand.Execute(null);
        await UiHarness.WaitForAsync(() => app.ViewModel.Dialog is UpgradeViewModel, "dialog");
        Assert.Equal("Upgrade · ₺149,99", ((UpgradeViewModel)app.ViewModel.Dialog!).BuyText);
    }

    [AvaloniaFact]
    public async Task AsksForARatingOnceAfterThreeCompletedTransfers()
    {
        MainViewModel.RatingPromptDelay = TimeSpan.FromMilliseconds(50);
        await using var app = new UiHarness();
        await app.InitializeAsync();
        await using var laptop = new TestNode("Laptop");
        var file = app.CreateFile("a.txt", 100);
        for (var i = 1; i <= 4; i++)
        {
            await app.ViewModel.StartSendAsync(laptop.AsDevice(), new[] { file });
            await UiHarness.WaitForAsync(() => app.ViewModel.Transfers.Count(t => t.IsFinished) == i, $"transfer {i}");
            if (i == 3) await UiHarness.WaitForAsync(() => app.Store.ReviewRequests == 1, "rating prompt");
            else await UiHarness.PumpAsync(300);
            Assert.Equal(i >= 3 ? 1 : 0, app.Store.ReviewRequests);
        }

        Assert.True(app.Node.Settings.Current.RatingRequested);
        Assert.Equal(4, app.Node.Settings.Current.CompletedTransfers);
    }

    [AvaloniaFact]
    public async Task ExistingPurchaseIsPickedUpAtStartup()
    {
        await using var app = new UiHarness(pro: false);
        app.Store.Owns = true;
        await app.InitializeAsync();
        Assert.True(app.Node.Edition.IsPro);
        Assert.True(app.ViewModel.Home.IsPro);
    }
}
