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

    [AvaloniaFact]
    public async Task PhonePageCanMakeTheComputersOwnWiFiForAnIPhone()
    {
        var air = new FakeDirectAir();
        await using var app = new UiHarness("Laptop", directLink: air.Link(DirectRoles.Host));
        await app.InitializeAsync();
        // Pretend this computer's network address is on its direct network once that is up.
        app.Node.Transfers.IsDirectAddress = ip => air.Hosted > 0 && !System.Net.IPAddress.IsLoopback(ip);
        var phone = app.ViewModel.Phone;
        app.ViewModel.IsPhonePage = true;
        phone.TurnOnCommand.Execute(null);
        await UiHarness.PumpAsync(100);
        Assert.True(phone.OffersDirect);
        Assert.Contains("No Wi-Fi network? Connect directly", app.VisibleTexts());

        phone.ConnectDirectlyCommand.Execute(null);
        await UiHarness.WaitForAsync(() => phone.IsDirect, "direct network started");
        Assert.Equal(1, air.Hosted);
        Assert.NotNull(phone.WifiQr);
        Assert.StartsWith("DIRECT-", phone.DirectNetworkName);
        Assert.False(phone.OffersDirect);
        await UiHarness.WaitForAsync(() => phone.Link.Length > 0, "page address on the direct network");
        await UiHarness.PumpAsync(100);
        Assert.Contains("First, join this computer's Wi-Fi", app.VisibleTexts());
        app.Screenshot("direct-04-iphone");

        phone.StopDirectCommand.Execute(null);
        Assert.False(phone.IsDirect);
        Assert.Equal(1, air.Stopped);
        Assert.True(phone.OffersDirect);
        phone.TurnOffCommand.Execute(null);
        await UiHarness.WaitForAsync(() => phone.IsOff, "turned off");
    }

    [AvaloniaFact]
    public async Task StartupOffersToAllowDirectConnectionsThroughTheFirewall()
    {
        await using var app = new UiHarness("Laptop", directLink: new FakeDirectAir().Link(DirectRoles.Host));
        app.Platform.FirewallBlocks = true;
        var init = app.InitializeAsync();
        await UiHarness.WaitForAsync(() => app.ViewModel.Dialog is ViewModels.Dialogs.ConfirmViewModel, "firewall question");
        var dialog = (ViewModels.Dialogs.ConfirmViewModel)app.ViewModel.Dialog!;
        Assert.Equal("Allow direct connections?", dialog.Title);
        app.Screenshot("direct-05-firewall");
        dialog.PrimaryCommand.Execute(null);
        await init;
        Assert.Contains("allow-firewall", app.Platform.Calls);

        // "Not now" is remembered: no question at the next start.
        await using var other = new UiHarness("Desktop", directLink: new FakeDirectAir().Link(DirectRoles.Host));
        other.Platform.FirewallBlocks = true;
        var second = other.InitializeAsync();
        await UiHarness.WaitForAsync(() => other.ViewModel.Dialog is ViewModels.Dialogs.ConfirmViewModel, "firewall question");
        ((ViewModels.Dialogs.ConfirmViewModel)other.ViewModel.Dialog!).SecondaryCommand.Execute(null);
        await second;
        Assert.True(other.Node.Settings.Current.FirewallPromptDeclined);
    }

    [AvaloniaFact]
    public async Task PhoneScansTheComputersCodeToPair()
    {
        await using var laptop = new UiHarness("Laptop", directLink: new FakeDirectAir().Link(DirectRoles.Host));
        await laptop.InitializeAsync();
        await using var phone = new UiHarness("Diaz's Galaxy", width: 400, height: 860, phone: true,
            directLink: new FakeDirectAir().Link(DirectRoles.Host | DirectRoles.Join));
        await phone.InitializeAsync();
        await UiHarness.PumpAsync(100);
        Assert.Contains("Scan the computer's code", phone.VisibleTexts());
        phone.Screenshot("pair-01-scan-button");

        // Something that isn't a Beam code.
        phone.Platform.ScannedCode = "https://example.com";
        phone.ViewModel.Home.ScanCodeCommand.Execute(null);
        await UiHarness.WaitForAsync(() => phone.ViewModel.Dialog is ViewModels.Dialogs.ConfirmViewModel, "not a Beam code");
        Assert.Equal("That's not a Beam code", ((ViewModels.Dialogs.ConfirmViewModel)phone.ViewModel.Dialog!).Title);
        phone.ViewModel.Dialog!.Dismiss();
        await UiHarness.WaitForAsync(() => phone.ViewModel.Home.ScanCodeCommand.CanExecute(null), "scan finished");

        // The code the laptop's Phone page shows.
        phone.Platform.ScannedCode = laptop.Node.CreatePairingCode().AppendTo("http://192.168.0.109:47831/t/");
        phone.ViewModel.Home.ScanCodeCommand.Execute(null);
        await UiHarness.WaitForAsync(() => phone.ViewModel.Dialog is ViewModels.Dialogs.ConfirmViewModel { Title: "Paired with Laptop" }, "paired");
        await UiHarness.PumpAsync(100);
        Assert.Empty(phone.TextOutsideWindow());
        phone.Screenshot("pair-02-paired");
        Assert.Contains(phone.Node.Settings.Current.TrustedDevices, t => t.DeviceId == laptop.Node.Identity.DeviceId);
        Assert.NotNull(phone.Node.Direct.KnownNetworkFor(laptop.Node.Identity.DeviceId));
    }
}
