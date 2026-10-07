using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Beam.App.ViewModels.Dialogs;
using Beam.Core.Discovery;
using Beam.Core.Tests;

namespace Beam.App.Tests;

/// <summary>Renders Microsoft Store listing screenshots (1600x900) to artifacts/store-screenshots.</summary>
public class StoreScreenshotTests
{
    private const string Folder = "store-screenshots";

    [AvaloniaFact]
    public async Task RenderStoreScreenshots()
    {
        await using var app = new UiHarness("Diaz's PC", width: 1600, height: 900);
        await app.InitializeAsync();
        app.AddFakeDevice("Living room PC");
        app.AddFakeDevice("Diaz's Laptop", DeviceKinds.Laptop);
        app.AddFakeDevice("Office desktop");
        await UiHarness.WaitForAsync(() => app.ViewModel.Home.Devices.Count == 3, "devices");
        app.ViewModel.Home.Devices.First(d => d.Name == "Diaz's Laptop").SelectCommand.Execute(null);
        var report = app.CreateFile("Project proposal.pdf", 3_400_000, 1);
        app.CreateFile("Holiday photos/Beach/IMG_2041.jpg", 4_100_000, 2);
        app.CreateFile("Holiday photos/Beach/IMG_2042.jpg", 3_900_000, 3);
        app.CreateFile("Holiday photos/Mountains/IMG_2107.jpg", 4_400_000, 4);
        var video = app.CreateFile("Family video.mp4", 48_000_000, 5);
        app.ViewModel.Home.AddPaths(new[] { Path.Combine(app.Root, "source", "Holiday photos"), report, video });
        await UiHarness.WaitForAsync(() => !app.ViewModel.Home.IsMeasuring, "sizes");
        await UiHarness.PumpAsync(100);
        app.Screenshot("1-send-files", Folder);

        Avalonia.Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
        await UiHarness.PumpAsync(100);
        app.Screenshot("5-dark-mode", Folder);
        Avalonia.Application.Current!.RequestedThemeVariant = ThemeVariant.Default;

        // Incoming request from a real second device.
        await using var sender = new TestNode("Diaz's Laptop");
        sender.Handler.DecisionDelay = TimeSpan.Zero;
        sender.CreateFile("Wedding album/Ceremony/DSC_0001.jpg", 6_000_000, 6);
        sender.CreateFile("Wedding album/Ceremony/DSC_0002.jpg", 6_000_000, 7);
        sender.CreateFile("Wedding album/Party/DSC_0140.jpg", 6_000_000, 8);
        var notes = sender.CreateFile("Guest list.xlsx", 80_000, 9);
        var outgoing = sender.Node.Send(new DeviceInfo
        {
            Id = app.Node.Identity.DeviceId,
            Name = "Diaz's PC",
            Fingerprint = app.Node.Identity.Fingerprint,
            Endpoints = new[] { new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, app.Node.Transfers.Port) },
        }, new[] { sender.SourcePath("Wedding album"), notes });
        await UiHarness.WaitForAsync(() => app.ViewModel.Dialog is IncomingRequestViewModel, "request");
        ((IncomingRequestViewModel)app.ViewModel.Dialog!).AcceptCommand.Execute(null);
        await UiHarness.WaitForAsync(() => outgoing.IsFinished, "transfer");

        // The request dialog as a customer would see it (Windows paths and a LAN address).
        var showcase = new IncomingRequestViewModel(new Core.Transfer.IncomingRequest
        {
            TransferId = "showcase",
            SenderName = "Diaz's Laptop",
            SenderId = "laptop",
            SenderFingerprint = sender.Node.Identity.Fingerprint,
            SenderAddress = "192.168.1.152",
            FileCount = 214,
            FolderCount = 3,
            TotalBytes = 1_610_612_736,
            Items = new[]
            {
                new Core.Transfer.IncomingItem("Wedding album", true, 1_590_000_000, 213),
                new Core.Transfer.IncomingItem("Guest list.xlsx", false, 82_000, 1),
            },
            DefaultFolder = @"C:\Users\Diaz\Downloads",
        }, app.Ui, trustedDevicesAvailable: true);
        _ = app.ViewModel.ShowDialogAsync(showcase);
        await UiHarness.PumpAsync(150);
        app.Screenshot("2-incoming-request", Folder);
        showcase.DeclineCommand.Execute(null);
        await UiHarness.PumpAsync(50);

        // Live progress of a large send.
        await using var receiver = new TestNode("Office desktop 2");
        receiver.Handler.DecisionDelay = TimeSpan.Zero;
        var big = app.CreateFile("Project backup.zip", 600L * 1024 * 1024, 10);
        app.ViewModel.StartSend(receiver.AsDevice(), new[] { big });
        await UiHarness.WaitForAsync(() => app.ViewModel.Transfers.Any(t => t.Progress is > 25 and < 75 && t.SpeedText.Length > 0), "progress", 60000);
        app.Screenshot("3-transfer-progress", Folder);
        await UiHarness.WaitForAsync(() => app.ViewModel.Transfers.All(t => t.IsFinished), "finish", 120000);

        app.ViewModel.ShowHistoryCommand.Execute(null);
        await UiHarness.PumpAsync(100);
        app.Screenshot("4-history", Folder);
        app.ViewModel.ShowSettingsCommand.Execute(null);
        await UiHarness.PumpAsync(100);
        app.Screenshot("6-settings", Folder);
    }
}
