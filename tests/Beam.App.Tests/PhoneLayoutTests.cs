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
        Assert.Contains("Nothing being sent or received", app.VisibleTexts());
        app.Screenshot("phone-2-transfers-empty");

        app.ViewModel.IsHistoryPage = true;
        await UiHarness.PumpAsync(100);
        app.Screenshot("phone-3-history");

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
        Assert.Contains("Diaz's Laptop", string.Join(" ", app.VisibleTexts()));
        app.Screenshot("phone-5-transfers");
    }
}
