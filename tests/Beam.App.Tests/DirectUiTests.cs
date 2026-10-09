using Avalonia.Headless.XUnit;
using Beam.Core.Direct;
using Beam.Core.Settings;
using Beam.Core.Tests;
using Beam.Core.Transfer;

namespace Beam.App.Tests;

public class DirectUiTests
{
    [AvaloniaFact]
    public async Task TransferCardSwitchesBetweenWiFiAndDirectConnection()
    {
        var air = new FakeDirectAir();
        await using var phone = new UiHarness("Diaz's Galaxy", width: 400, height: 860, phone: true, pro: false,
            directLink: air.Link(DirectRoles.Host | DirectRoles.Join)); // free edition: 5 MB/s keeps the transfer running
        await using var laptop = new UiHarness("Laptop", directLink: air.Link(DirectRoles.Host));
        await phone.InitializeAsync();
        await laptop.InitializeAsync();
        air.Connect(phone.Node, phone.AsDevice, laptop.Node, laptop.AsDevice);
        laptop.Node.Settings.Update(s => s.TrustedDevices.Add(new TrustedDevice { Fingerprint = phone.Node.Identity.Fingerprint, DeviceId = phone.Node.Identity.DeviceId, Name = "Diaz's Galaxy" }));
        var home = phone.ViewModel.Home;
        await UiHarness.WaitForAsync(() => home.SelectedDevice != null, "laptop selected");

        home.AddPaths(new[] { phone.CreateFile("Holiday.mp4", 40L * 1024 * 1024, 3) });
        home.SendCommand.Execute(null);
        await UiHarness.WaitForAsync(() => phone.ViewModel.Transfers.Count == 1 && phone.ViewModel.Transfers[0].State == TransferState.Transferring, "sending");
        var card = phone.ViewModel.Transfers[0];
        phone.ViewModel.IsTransfersPage = true;
        await UiHarness.WaitForAsync(() => card.PathText == "Over Wi-Fi", "path shown");
        Assert.True(card.CanSwitchToDirect);
        Assert.False(card.CanSwitchToNetwork);
        Assert.Contains("Connect directly", phone.VisibleTexts());
        phone.Screenshot("direct-01-over-wifi");

        card.SwitchToDirectCommand.Execute(null);
        await UiHarness.WaitForAsync(() => card.PathText == "Direct connection", "switched to direct", 25000);
        Assert.True(card.IsDirectPath);
        Assert.False(card.CanSwitchToDirect);
        Assert.True(card.CanSwitchToNetwork);
        await UiHarness.PumpAsync(300);
        Assert.Contains("Switch to Wi-Fi", phone.VisibleTexts());
        Assert.Empty(phone.TextOutsideWindow());
        phone.Screenshot("direct-02-direct");

        // The laptop's card shows the direct connection too, and it can switch back to Wi-Fi.
        var received = laptop.ViewModel.Transfers.Single();
        await UiHarness.WaitForAsync(() => received.PathText == "Direct connection", "receiver on direct");
        Assert.True(received.CanSwitchToNetwork);
        received.SwitchToNetworkCommand.Execute(null);
        await UiHarness.WaitForAsync(() => card.PathText == "Over Wi-Fi", "back on Wi-Fi", 25000);
        Assert.Equal(ConnectionMethod.SameNetwork, phone.Node.Direct.MethodFor(laptop.Node.Identity.DeviceId));

        card.Session.Cancel();
        await UiHarness.WaitForAsync(() => card.IsFinished, "cancelled");
    }

    [AvaloniaFact]
    public async Task SettingsOfferTheConnectionMethod()
    {
        await using var phone = new UiHarness("Diaz's Galaxy", width: 400, height: 860, phone: true, directLink: new FakeDirectAir().Link(DirectRoles.Host | DirectRoles.Join));
        await phone.InitializeAsync();
        var settings = phone.ViewModel.Settings;
        phone.ViewModel.IsSettingsPage = true;
        await UiHarness.PumpAsync(100);
        Assert.True(settings.UseSameNetwork);
        Assert.True(settings.DirectSupported);

        settings.UseDirect = true;
        Assert.Equal(ConnectionMethod.Direct, phone.Node.Settings.Current.Connection);
        Assert.False(settings.UseSameNetwork);
        Assert.Contains("Direct connection (Beta)", phone.VisibleTexts());
        Assert.Empty(phone.TextOutsideWindow());
        phone.Screenshot("direct-03-settings");

        await using var oldPhone = new UiHarness("Old phone", width: 400, height: 860, phone: true);
        await oldPhone.InitializeAsync();
        Assert.False(oldPhone.ViewModel.Settings.DirectSupported);
        oldPhone.ViewModel.IsSettingsPage = true;
        await UiHarness.PumpAsync(100);
        Assert.Contains("This phone can't connect directly. Direct connections need Android 10 or newer.", oldPhone.VisibleTexts());
    }
}
