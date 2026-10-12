using Beam.App.ViewModels.Dialogs;
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
        var slow = new Beam.Core.Tests.SlowFiles(); // keeps the transfer running
        await using var phone = new UiHarness("Diaz's Galaxy", width: 400, height: 860, phone: true,
            directLink: air.Link(DirectRoles.Host | DirectRoles.Join), externalFiles: slow);
        await using var laptop = new UiHarness("Laptop", directLink: air.Link(DirectRoles.Host));
        await phone.InitializeAsync();
        await laptop.InitializeAsync();
        air.Connect(phone.Node, phone.AsDevice, laptop.Node, laptop.AsDevice);
        laptop.Node.Settings.Update(s => s.TrustedDevices.Add(new TrustedDevice { Fingerprint = phone.Node.Identity.Fingerprint, DeviceId = phone.Node.Identity.DeviceId, Name = "Diaz's Galaxy" }));
        var home = phone.ViewModel.Home;
        await UiHarness.WaitForAsync(() => home.SelectedDevice != null, "laptop selected");

        home.AddPaths(new[] { slow.Add(phone.CreateFile("Holiday.mp4", 40L * 1024 * 1024, 3)) });
        home.SendCommand.Execute(null);
        await UiHarness.WaitForAsync(() => phone.ViewModel.Transfers.Count == 1 && phone.ViewModel.Transfers[0].State == TransferState.Transferring, "sending");
        var card = phone.ViewModel.Transfers[0];
        phone.ViewModel.IsTransfersPage = true;
        await UiHarness.WaitForAsync(() => card.PathText == "Over Wi-Fi", "path shown");
        Assert.False(card.CanSwitchToDirect); // Direct connection is a beta people choose in Settings
        Assert.False(card.CanSwitchToNetwork);
        Assert.DoesNotContain("Connect directly", phone.VisibleTexts());
        phone.Screenshot("direct-01-over-wifi");

        // Choosing Direct connection in Settings moves the running transfer onto a direct link.
        phone.ViewModel.Settings.UseDirect = true;
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
    public async Task PhonePageStartsThisComputersWiFiForTheAppCode()
    {
        var air = new FakeDirectAir();
        await using var app = new UiHarness("Laptop", directLink: air.Link(DirectRoles.Host));
        await app.InitializeAsync();
        var phone = app.ViewModel.Phone;

        // Opening the Phone page turns this computer's own Wi-Fi on; the app's code then says to join it.
        app.ViewModel.IsPhonePage = true;
        await UiHarness.WaitForAsync(() => phone.AppCodeConnectsDirectly, "own Wi-Fi on");
        Assert.Equal(1, air.Hosted);
        Assert.True(app.Node.CreatePairingCode(connectDirectly: true).ConnectDirectly);
        Assert.Contains("The phone has no internet while it's connected to this computer's Wi-Fi.", app.VisibleTexts());
        app.Screenshot("direct-04-app-code");

        // The browser code is for the shared Wi-Fi only: this computer's own Wi-Fi goes off.
        phone.ShowBrowserCommand.Execute(null);
        await UiHarness.PumpAsync(100);
        Assert.Equal(1, air.Stopped);
        Assert.DoesNotContain("192.168.137.", phone.Link);

        // Leaving the page turns it off too (unless a phone connected over it).
        phone.ShowAppCodeCommand.Execute(null);
        await UiHarness.WaitForAsync(() => phone.AppCodeConnectsDirectly, "own Wi-Fi on again");
        app.ViewModel.ShowHomeCommand.Execute(null);
        await UiHarness.WaitForAsync(() => air.Stopped == 2, "own Wi-Fi off");
    }

    [AvaloniaFact]
    public async Task FirewallIsOnlyAskedAboutWhenThisComputerFirstConnectsDirectly()
    {
        var air = new FakeDirectAir();
        await using var app = new UiHarness("Laptop", directLink: air.Link(DirectRoles.Host));
        app.Platform.FirewallBlocks = true;
        await app.InitializeAsync();
        Assert.Null(app.ViewModel.Dialog); // nothing at startup

        // Starting this computer's own network asks first, with the reason, then goes ahead.
        var hosting = app.Node.Direct.HostForBrowserAsync(CancellationToken.None);
        await UiHarness.WaitForAsync(() => app.ViewModel.Dialog is ViewModels.Dialogs.ConfirmViewModel, "firewall question");
        var dialog = (ViewModels.Dialogs.ConfirmViewModel)app.ViewModel.Dialog!;
        Assert.Equal("Allow direct connections?", dialog.Title);
        app.Screenshot("direct-05-firewall");
        dialog.PrimaryCommand.Execute(null);
        await hosting;
        Assert.Contains("allow-firewall", app.Platform.Calls);
        Assert.Equal(1, air.Hosted);

        // Only once per run.
        app.Platform.FirewallBlocks = true;
        app.Node.Direct.StopForBrowser();
        await app.Node.Direct.HostForBrowserAsync(CancellationToken.None);
        Assert.Null(app.ViewModel.Dialog);
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

    [AvaloniaFact]
    public async Task ConnectDirectlyOnAnotherComputersCard()
    {
        var air = new FakeDirectAir();
        var pcRoles = DirectRoles.Host | DirectRoles.JoinComputers;
        await using var laptop = new UiHarness("Laptop", directLink: air.Link(pcRoles));
        await using var desk = new UiHarness("Office desktop", directLink: air.Link(pcRoles));
        await laptop.InitializeAsync();
        await desk.InitializeAsync();
        air.Connect(laptop.Node, laptop.AsDevice, desk.Node, desk.AsDevice);
        var home = laptop.ViewModel.Home;
        await UiHarness.WaitForAsync(() => home.Devices.Count == 1, "desk listed");
        var card = home.Devices[0];
        Assert.True(card.ShowsDirectButton);
        Assert.Equal("Connect directly", card.DirectButtonText);
        await UiHarness.PumpAsync();
        Assert.Contains("Connect directly", laptop.VisibleTexts());
        laptop.Screenshot("direct-pc-01-card");

        // The desk can't share its internet: the laptop is told it goes offline meanwhile, and goes ahead.
        card.DirectCommand.Execute(null);
        await UiHarness.WaitForAsync(() => laptop.ViewModel.Dialog is ConfirmViewModel, "internet warning");
        Assert.Contains("no internet until you disconnect", ((ConfirmViewModel)laptop.ViewModel.Dialog!).Message);
        laptop.Screenshot("direct-pc-02-warning");
        ((ConfirmViewModel)laptop.ViewModel.Dialog!).PrimaryCommand.Execute(null);

        // The desk (which doesn't trust the laptop yet) is asked before starting its network.
        await UiHarness.WaitForAsync(() => desk.ViewModel.Dialog is ConfirmViewModel, "desk asked");
        var ask = (ConfirmViewModel)desk.ViewModel.Dialog!;
        Assert.Equal("Laptop wants to connect directly", ask.Title);
        Assert.Contains("Your Wi-Fi stays connected", ask.Message);
        await UiHarness.PumpAsync();
        Assert.Empty(desk.TextOutsideWindow());
        desk.Screenshot("direct-pc-03-asked");
        ask.PrimaryCommand.Execute(null);

        await UiHarness.WaitForAsync(() => card.IsDirect, "connected directly", 20000);
        Assert.Equal("Disconnect", card.DirectButtonText);
        await UiHarness.PumpAsync();
        Assert.EndsWith("· Direct", card.Subtitle);
        Assert.Contains(card.Subtitle, laptop.VisibleTexts());
        laptop.Screenshot("direct-pc-04-connected");

        card.DirectCommand.Execute(null);
        await UiHarness.WaitForAsync(() => !card.IsDirect && card.DirectButtonText == "Connect directly", "disconnected");
    }
}
