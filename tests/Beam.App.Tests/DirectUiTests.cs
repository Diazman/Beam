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
        await using var laptop = new UiHarness("Laptop", directLink: air.Link(DirectRoles.Host | DirectRoles.JoinComputers));
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
        await UiHarness.WaitForAsync(() => card.PathText == "Over the Wi-Fi network", "path shown");
        Assert.Equal(ConnectionMethod.SameNetwork, card.Session.Mode);
        Assert.True(card.CanSwitchToDirect); // the user may move it; nothing moves by itself
        Assert.False(card.CanSwitchToNetwork);
        phone.Screenshot("direct-01-over-wifi");

        // Choosing Direct on the Send page applies to the next Send, not to this running transfer.
        home.PreferDirect = true;
        await UiHarness.PumpAsync(500);
        Assert.Equal("Over the Wi-Fi network", card.PathText);

        // "Connect directly" on the card: set up while it keeps going over Wi-Fi, then moved over once verified.
        card.SwitchToDirectCommand.Execute(null);
        await UiHarness.WaitForAsync(() => card.PathText == "Direct connection (verified)", "switched to direct", 25000);
        Assert.True(card.IsDirectPath);
        Assert.Equal(ConnectionMethod.Direct, card.Session.Mode);
        Assert.False(card.CanSwitchToDirect);
        Assert.True(card.CanSwitchToNetwork);
        await UiHarness.PumpAsync(300);
        Assert.Contains("Switch to Wi-Fi", phone.VisibleTexts());
        Assert.Empty(phone.TextOutsideWindow());
        phone.Screenshot("direct-02-direct");

        // The laptop's card shows the direct connection too, but only the sender switches.
        var received = laptop.ViewModel.Transfers.Single();
        await UiHarness.WaitForAsync(() => received.PathText == "Direct connection (verified)", "receiver on direct");
        Assert.False(received.CanSwitchToNetwork);
        card.SwitchToNetworkCommand.Execute(null);
        await UiHarness.WaitForAsync(() => card.PathText == "Over the Wi-Fi network", "back on Wi-Fi", 25000);
        Assert.Equal(ConnectionMethod.SameNetwork, card.Session.Mode); // shown as such

        card.Session.Cancel();
        await UiHarness.WaitForAsync(() => card.IsFinished, "cancelled");
    }

    [AvaloniaFact]
    public async Task ADirectSendShowsItsStagesAsksTheReceiverAndOffersWiFiOnlyWhenTheUserChooses()
    {
        var air = new FakeDirectAir();
        var laptopLink = (FakeDirectAir.FakeLink)air.Link(DirectRoles.Host | DirectRoles.JoinComputers);
        var phoneLink = (FakeDirectAir.FakeLink)air.Link(DirectRoles.Host | DirectRoles.Join);
        await using var laptop = new UiHarness("Laptop", directLink: laptopLink);
        await using var phone = new UiHarness("Diaz's Galaxy", width: 400, height: 860, phone: true, directLink: phoneLink);
        await laptop.InitializeAsync();
        await phone.InitializeAsync();
        air.Connect(laptop.Node, laptop.AsDevice, phone.Node, phone.AsDevice);
        phone.ViewModel.IsReceivePage = true; // not paired: Receive open, and its user is asked
        var home = laptop.ViewModel.Home;
        home.PreferDirect = true;
        await UiHarness.WaitForAsync(() => home.SelectedDevice != null, "phone selected");
        Assert.Equal("Searching over the local network…", home.DiscoveryStatus);
        Assert.Equal(0, air.Hosted); // Send and Receive are open: nothing started

        phoneLink.NextJoins = FakeDirectAir.JoinOutcome.PromptDenied; // then Android's own prompt is declined
        home.AddPaths(new[] { laptop.CreateFile("Report.pdf", 300_000) });
        home.SendCommand.Execute(null);
        await UiHarness.WaitForAsync(() => laptop.ViewModel.Transfers.Count == 1, "card");
        var card = laptop.ViewModel.Transfers[0];
        await UiHarness.WaitForAsync(() => card.StatusText == "Asking Diaz's Galaxy to accept a direct connection…", "asking");

        // The phone's user is asked, with the security code, what joining means.
        await UiHarness.WaitForAsync(() => phone.ViewModel.Dialog is ConfirmViewModel { Title: "Connect directly to Laptop?" }, "phone asked");
        var question = (ConfirmViewModel)phone.ViewModel.Dialog!;
        Assert.Contains(Beam.Core.Identity.DeviceIdentity.ShortCode(laptop.Node.Identity.Fingerprint), question.Message);
        phone.Screenshot("direct-03-receiver-asked");
        question.PrimaryCommand.Execute(null);

        await UiHarness.WaitForAsync(() => card.IsFinished, "failed", 30000);
        Assert.StartsWith("Direct connection failed at receiver join:", card.StatusText);
        Assert.True(card.CanSendOverNetwork);
        Assert.Equal("", card.PathText);
        laptop.ViewModel.IsTransfersPage = true;
        await UiHarness.PumpAsync(300);
        Assert.Contains("Send over Wi-Fi instead", laptop.VisibleTexts());
        Assert.Empty(laptop.TextOutsideWindow());
        laptop.Screenshot("direct-04-failed-at-join");
        Assert.Empty(phone.ViewModel.Transfers); // nothing went to it

        // Only when the user picks it: a separate transfer over Wi-Fi, shown as such.
        card.SendOverNetworkCommand.Execute(null);
        await UiHarness.WaitForAsync(() => laptop.ViewModel.Transfers.Count == 2, "second card");
        var overWiFi = laptop.ViewModel.Transfers.First(t => t != card);
        Assert.Equal(ConnectionMethod.SameNetwork, overWiFi.Session.Mode);
        await UiHarness.WaitForAsync(() => phone.ViewModel.Dialog is IncomingRequestViewModel, "the phone is asked about the files");
        ((IncomingRequestViewModel)phone.ViewModel.Dialog!).AcceptCommand.Execute(null);
        await UiHarness.WaitForAsync(() => overWiFi.IsFinished, "sent over Wi-Fi", 30000);
        Assert.Equal(TransferState.Completed, overWiFi.State);
        Assert.Equal(1, phoneLink.JoinCalls);
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
        Assert.Contains("Send directly", phone.VisibleTexts());

        // A phone that can't connect directly doesn't offer it.
        await using var oldPhone = new UiHarness("Old phone", width: 400, height: 860, phone: true);
        await oldPhone.InitializeAsync();
        await UiHarness.PumpAsync(100);
        Assert.False(oldPhone.ViewModel.Home.CanConnectDirectly);
        Assert.DoesNotContain("Send directly", oldPhone.VisibleTexts());
    }

    [AvaloniaFact]
    public async Task ReceiveStartsNoNetworkAndSaysHowDirectSendersConnect()
    {
        var air = new FakeDirectAir();
        await using var app = new UiHarness("Laptop", directLink: air.Link(DirectRoles.Host | DirectRoles.JoinComputers), nearbyRadio: new FakeNearbyAir().Radio());
        await app.InitializeAsync();
        var receive = app.ViewModel.Receive;

        // Receive open with "Let senders connect directly": visible, and nothing started.
        app.ViewModel.IsReceivePage = true;
        await UiHarness.PumpAsync(500);
        Assert.True(app.Node.Nearby.IsReceiving);
        Assert.True(receive.AllowDirect);
        Assert.False(receive.NetworkOn);
        Assert.Equal(0, air.Hosted);
        Assert.Contains("Ready to receive", app.VisibleTexts());
        Assert.Contains("Let senders connect directly", app.VisibleTexts());
        Assert.Contains("Devices nearby can find it over Bluetooth, even without a shared Wi-Fi.", app.VisibleTexts());
        Assert.Contains("Senders that send directly start their own Wi-Fi when they send, and this device joins it.", app.VisibleTexts());
        Assert.DoesNotContain("Sending from a phone? On the phone, forget saved Wi-Fi networks nearby that have internet.", app.VisibleTexts());
        Assert.Empty(app.TextOutsideWindow());
        app.Screenshot("receive-01-direct");

        receive.ToggleCodeCommand.Execute(null);
        await UiHarness.PumpAsync(100);
        Assert.NotNull(receive.Code);

        // Turned off: senders aren't offered a direct connection.
        receive.AllowDirect = false;
        await UiHarness.PumpAsync(100);
        Assert.Equal(DirectRoles.None, app.Node.Direct.AnnouncedRoles);
        Assert.Contains("Senders connect over the shared Wi-Fi.", app.VisibleTexts());
        Assert.Equal(0, air.Hosted);
    }

    [AvaloniaFact]
    public async Task FirewallIsOnlyAskedAboutWhenThisComputerFirstConnectsDirectly()
    {
        var air = new FakeDirectAir();
        await using var app = new UiHarness("Laptop", directLink: air.Link(DirectRoles.Host));
        app.Platform.FirewallBlocks = true;
        await app.InitializeAsync();
        Assert.Null(app.ViewModel.Dialog); // nothing at startup

        // Before this computer starts its own network (or joins one), Beam asks first, with the reason, then goes ahead.
        var hosting = app.Node.Direct.BeforeHosting!();
        await UiHarness.WaitForAsync(() => app.ViewModel.Dialog is ViewModels.Dialogs.ConfirmViewModel, "firewall question");
        var dialog = (ViewModels.Dialogs.ConfirmViewModel)app.ViewModel.Dialog!;
        Assert.Equal("Allow direct connections?", dialog.Title);
        app.Screenshot("direct-05-firewall");
        dialog.PrimaryCommand.Execute(null);
        await hosting;
        Assert.Contains("allow-firewall", app.Platform.Calls);

        // Only once per run.
        app.Platform.FirewallBlocks = true;
        await app.Node.Direct.BeforeHosting!();
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
        // Its own name (never one Windows saved for that network earlier), for the network's SSID.
        Assert.Equal("Beam DIRECT-ab-Beam-Tom & Jerry's <PC>", doc.Root!.Element(ns + "name")!.Value);
        Assert.Equal("DIRECT-ab-Beam-Tom & Jerry's <PC>", doc.Descendants(ns + "SSID").Single().Element(ns + "name")!.Value);
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
    public async Task SendDirectlyIsChosenOnTheSendPage()
    {
        var air = new FakeDirectAir();
        await using var laptop = new UiHarness("Laptop", directLink: air.Link(DirectRoles.Host | DirectRoles.JoinComputers));
        await laptop.InitializeAsync();
        var home = laptop.ViewModel.Home;
        await UiHarness.PumpAsync();
        Assert.Contains("Send directly", laptop.VisibleTexts());
        Assert.DoesNotContain("Connect directly", laptop.VisibleTexts());
        Assert.False(home.PreferDirect);
        Assert.False(home.HasDiscoveryStatus);

        home.PreferDirect = true;
        await UiHarness.PumpAsync(100);
        Assert.Equal(ConnectionMethod.Direct, laptop.Node.Settings.Current.Connection);
        Assert.Equal(ConnectionMethod.Direct, laptop.ViewModel.SendMode);
        Assert.Contains("Searching over the local network…", laptop.VisibleTexts());
        Assert.False(home.SendCommand.CanExecute(null)); // no files, no device chosen
        Assert.Equal(0, air.Hosted); // choosing Direct starts nothing
        laptop.Screenshot("send-01-send-directly");
    }

}
