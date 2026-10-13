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

        // Turning on "Prefer direct connection" (Send page) moves the running transfer onto a direct link.
        home.PreferDirect = true;
        await UiHarness.WaitForAsync(() => card.PathText == "Direct connection", "switched to direct", 25000);
        Assert.True(card.IsDirectPath);
        Assert.False(card.CanSwitchToDirect);
        Assert.True(card.CanSwitchToNetwork);
        await UiHarness.PumpAsync(300);
        Assert.Contains("Switch to Wi-Fi", phone.VisibleTexts());
        Assert.Empty(phone.TextOutsideWindow());
        phone.Screenshot("direct-02-direct");

        // The laptop's card shows the direct connection too, but only the sender switches.
        var received = laptop.ViewModel.Transfers.Single();
        await UiHarness.WaitForAsync(() => received.PathText == "Direct connection", "receiver on direct");
        Assert.False(received.CanSwitchToNetwork);
        card.SwitchToNetworkCommand.Execute(null);
        await UiHarness.WaitForAsync(() => card.PathText == "Over Wi-Fi", "back on Wi-Fi", 25000);
        Assert.Equal(ConnectionMethod.SameNetwork, phone.Node.Direct.MethodFor(laptop.Node.Identity.DeviceId));

        card.Session.Cancel();
        await UiHarness.WaitForAsync(() => card.IsFinished, "cancelled");
    }

    [AvaloniaFact]
    public async Task TheConnectionChoiceIsOnSendAndReceiveNotInSettings()
    {
        await using var phone = new UiHarness("Diaz's Galaxy", width: 400, height: 860, phone: true, directLink: new FakeDirectAir().Link(DirectRoles.Host | DirectRoles.Join));
        await phone.InitializeAsync();
        phone.ViewModel.IsSettingsPage = true;
        await UiHarness.PumpAsync(100);
        Assert.DoesNotContain("How devices connect", phone.VisibleTexts());
        phone.ViewModel.IsHomePage = true;
        await UiHarness.PumpAsync(100);
        Assert.Contains("Prefer direct connection", phone.VisibleTexts());

        // A phone that can't connect directly doesn't offer it.
        await using var oldPhone = new UiHarness("Old phone", width: 400, height: 860, phone: true);
        await oldPhone.InitializeAsync();
        await UiHarness.PumpAsync(100);
        Assert.False(oldPhone.ViewModel.Home.CanConnectDirectly);
        Assert.DoesNotContain("Prefer direct connection", oldPhone.VisibleTexts());
    }

    [AvaloniaFact]
    public async Task ReceiveKeepsThisComputersWiFiOnWhileSendersMayConnectDirectly()
    {
        var air = new FakeDirectAir();
        await using var app = new UiHarness("Laptop", directLink: air.Link(DirectRoles.Host | DirectRoles.JoinComputers), nearbyRadio: new FakeNearbyAir().Radio());
        await app.InitializeAsync();
        var receive = app.ViewModel.Receive;

        // Receive open, "Let senders connect directly" on: its own Wi-Fi goes on right away, and phones are warned it
        // has no internet.
        app.ViewModel.IsReceivePage = true;
        await UiHarness.WaitForAsync(() => receive.NetworkOn, "own Wi-Fi on");
        Assert.True(app.Node.Nearby.IsReceiving);
        Assert.True(receive.AllowDirect);
        await UiHarness.PumpAsync(100);
        Assert.Contains("Ready to receive", app.VisibleTexts());
        Assert.Contains("Let senders connect directly", app.VisibleTexts());
        Assert.Contains("Devices nearby can find it over Bluetooth, even without a shared Wi-Fi.", app.VisibleTexts());
        Assert.Contains("Sending from a phone? On the phone, forget saved Wi-Fi networks nearby that have internet.", app.VisibleTexts());
        Assert.Empty(app.TextOutsideWindow());
        app.Screenshot("receive-01-direct");

        receive.ToggleCodeCommand.Execute(null);
        await UiHarness.PumpAsync(100);
        Assert.NotNull(receive.Code);

        // Turning "Let senders connect directly" off turns it off too; back on, it goes on again.
        receive.AllowDirect = false;
        await UiHarness.WaitForAsync(() => air.Stopped > 0 && !receive.NetworkOn, "own Wi-Fi off with the switch");
        Assert.Null(await app.Node.Direct.HostForNearbyAsync(CancellationToken.None));
        receive.AllowDirect = true;
        await UiHarness.WaitForAsync(() => receive.NetworkOn, "own Wi-Fi on again");

        // Leaving Receive turns it off (at once here; after a few minutes in the app).
        app.ViewModel.ShowHomeCommand.Execute(null);
        await UiHarness.WaitForAsync(() => air.Stopped > 1, "own Wi-Fi off");
        Assert.False(app.Node.Nearby.IsReceiving);

        // Turned off: senders aren't offered a direct connection.
        receive.AllowDirect = false;
        await UiHarness.PumpAsync(100);
        Assert.Equal(DirectRoles.None, app.Node.Direct.AnnouncedRoles);
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
    public async Task WhenWindowsBlocksTheLocalNetworkBeamOffersToAllowItAtStart()
    {
        // The home Wi-Fi is a "public" network in Windows: nothing nearby could send to this computer.
        await using var app = new UiHarness("Laptop");
        app.Platform.FirewallBlocksLocalNetwork = true;
        await app.InitializeAsync();
        await UiHarness.WaitForAsync(() => app.ViewModel.Dialog is ViewModels.Dialogs.ConfirmViewModel, "firewall question");
        var dialog = (ViewModels.Dialogs.ConfirmViewModel)app.ViewModel.Dialog!;
        Assert.Equal("Let nearby devices send to this computer?", dialog.Title);
        await UiHarness.PumpAsync();
        Assert.Empty(app.TextOutsideWindow());
        app.Screenshot("firewall-local-network");
        dialog.PrimaryCommand.Execute(null);
        await UiHarness.WaitForAsync(() => app.Platform.Calls.Contains("allow-firewall"), "allowed");
    }

    [Fact]
    public void TheWiFiProfileForJoiningByNameIsValidXml()
    {
        var xml = Beam.App.Platform.WlanProfiles.ProfileXml("DIRECT-ab-Beam-Tom & Jerry's <PC>", "p<a>ss&\"word'");
        var doc = System.Xml.Linq.XDocument.Parse(xml);
        System.Xml.Linq.XNamespace ns = "http://www.microsoft.com/networking/WLAN/profile/v1";
        Assert.Equal("DIRECT-ab-Beam-Tom & Jerry's <PC>", doc.Root!.Element(ns + "name")!.Value);
        Assert.Equal("p<a>ss&\"word'", doc.Descendants(ns + "keyMaterial").Single().Value);
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
    public async Task PreferDirectConnectionOnTheSendPage()
    {
        var air = new FakeDirectAir();
        await using var laptop = new UiHarness("Laptop", directLink: air.Link(DirectRoles.Host | DirectRoles.JoinComputers));
        await laptop.InitializeAsync();
        var home = laptop.ViewModel.Home;
        await UiHarness.PumpAsync();
        Assert.Contains("Prefer direct connection", laptop.VisibleTexts());
        Assert.DoesNotContain("Connect directly", laptop.VisibleTexts());
        Assert.False(home.PreferDirect);

        home.PreferDirect = true;
        await UiHarness.PumpAsync(100);
        Assert.Equal(ConnectionMethod.Direct, laptop.Node.Settings.Current.Connection);
        laptop.Screenshot("send-01-prefer-direct");
    }
}
