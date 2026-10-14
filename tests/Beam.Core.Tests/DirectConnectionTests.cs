using Beam.Core;
using System.Net;
using Beam.Core.Direct;
using Beam.Core.Discovery;
using Beam.Core.Nearby;
using Beam.Core.Protocol;
using Beam.Core.Settings;
using Beam.Core.Transfer;

namespace Beam.Core.Tests;

/// <summary>
/// Same Network and Direct sending with a stand-in for the radio (<see cref="FakeDirectAir"/>, <see cref="FakeNearbyAir"/>):
/// deterministic tests of Beam's own logic, NOT of real Wi-Fi or Bluetooth hardware. On loopback 127.0.0.1 plays the
/// shared Wi-Fi network, 127.0.0.2 the sender's address on its direct network and 127.0.0.3 the receiver's, so a direct
/// session's route checks (who connects from where) run for real.
/// </summary>
public class DirectConnectionTests
{
    private static readonly IPAddress DirectAddress = FakeDirectAir.DirectAddress;

    private const DirectRoles Pc = DirectRoles.Host | DirectRoles.JoinComputers; // what WindowsDirectLink announces
    private const DirectRoles Phone = DirectRoles.Host | DirectRoles.Join; // what AndroidDirectLink announces

    private static FakeDirectAir.FakeLink Fake(IDirectLink link) => (FakeDirectAir.FakeLink)link;

    /// <summary>A sender and a receiver that see each other on the shared Wi-Fi (127.0.0.1) and can link directly.</summary>
    private static async Task<(TestNode Sender, TestNode Receiver, FakeDirectAir.FakeLink SenderLink, FakeDirectAir.FakeLink ReceiverLink)> PairAsync(
        FakeDirectAir air, DirectRoles senderRoles = Pc, string senderKind = DeviceKinds.Laptop, DirectRoles receiverRoles = Phone,
        string receiverKind = DeviceKinds.Phone, bool trusted = true, SlowFiles? files = null)
    {
        var senderLink = air.Link(senderRoles);
        var receiverLink = air.Link(receiverRoles);
        var sender = new TestNode("Sender", directLink: senderLink, kind: senderKind, externalFiles: files);
        var receiver = new TestNode("Receiver", directLink: receiverLink, kind: receiverKind);
        air.Connect(sender.Node, () => sender.AsDevice(), receiver.Node, () => receiver.AsDevice());
        if (trusted) receiver.Trust(sender);
        await Task.CompletedTask;
        return (sender, receiver, Fake(senderLink), Fake(receiverLink));
    }

    /// <summary>Nothing the receiver got: no request was shown, nothing saved.</summary>
    private static void AssertNothingArrived(TestNode receiver)
    {
        Assert.Empty(receiver.Handler.Requests);
        Assert.Empty(Directory.GetFiles(receiver.ReceiveFolder, "*", SearchOption.AllDirectories));
        Assert.DoesNotContain(receiver.Node.Transfers.Sessions, s => s.Direction == TransferDirection.Receive);
    }

    // ---- Same Network is unchanged ----

    [Fact]
    public async Task SameNetworkSendsOverTheRouterAndNeverTouchesTheRadio()
    {
        var air = new FakeDirectAir();
        var (sender, receiver, senderLink, receiverLink) = await PairAsync(air);
        await using var _s = sender;
        await using var _r = receiver;
        var file = sender.CreateFile("photo.jpg", 500_000);

        var session = sender.Node.Send(receiver.AsDevice(), new[] { file });
        await Wait.ForFinishAsync(session);

        Assert.Equal(TransferState.Completed, session.State);
        Assert.Equal(ConnectionMethod.SameNetwork, session.Mode);
        Assert.Null(session.DirectSession);
        Assert.Equal(TransferPath.Network, session.Path);
        Assert.Equal(0, senderLink.HostCalls + senderLink.JoinCalls + receiverLink.HostCalls + receiverLink.JoinCalls);
        Assert.Equal(TestFiles.Hash(file), TestFiles.Hash(Path.Combine(receiver.ReceiveFolder, "photo.jpg")));
    }

    // ---- Opening Receive or Send starts nothing ----

    [Fact]
    public async Task OpeningReceiveOnlyAdvertisesAndStartsNoNetwork()
    {
        var air = new FakeDirectAir();
        var bluetooth = new FakeNearbyAir();
        var link = air.Link(Pc);
        await using var pc = new TestNode("Laptop", directLink: link, nearbyRadio: bluetooth.Radio());
        await using var phone = new TestNode("Galaxy", kind: DeviceKinds.Phone, nearbyRadio: bluetooth.Radio());

        pc.Node.Nearby.SetReceiving(true);
        phone.Node.Nearby.SetLooking(true);
        await Wait.UntilAsync(() => phone.Node.Discovery.Devices.Any(NearbyService.IsPlaceholder), because: "heard over Bluetooth");
        await Task.Delay(500);

        Assert.True(pc.Node.Direct.AnnouncedRoles.HasFlag(DirectRoles.Receiving));
        Assert.True(pc.Node.Direct.AnnouncedRoles.HasFlag(DirectRoles.SenderHosted));
        Assert.Equal(0, Fake(link).HostCalls);
        Assert.Equal(0, air.Hosted);
        Assert.Null(pc.Node.Direct.HostedNetwork);
        // The beacon carries no secrets: just "Bm", flags, an id hash and the name.
        Assert.DoesNotContain(phone.Node.Discovery.Devices, d => d.Endpoints.Count > 0);
    }

    [Fact]
    public async Task OpeningSendWithDirectChosenOnlyLooksAndStartsNoNetwork()
    {
        var air = new FakeDirectAir();
        var bluetooth = new FakeNearbyAir();
        var link = air.Link(Phone);
        await using var phone = new TestNode("Galaxy", directLink: link, kind: DeviceKinds.Phone, nearbyRadio: bluetooth.Radio());
        await using var pc = new TestNode("Laptop", directLink: air.Link(Pc), nearbyRadio: bluetooth.Radio());
        air.Connect(phone.Node, () => phone.AsDevice(), pc.Node, () => pc.AsDevice());
        pc.Node.Nearby.SetReceiving(true);

        phone.Node.Settings.Update(s => s.Connection = ConnectionMethod.Direct);
        phone.Node.Nearby.SetLooking(true);
        await Wait.UntilAsync(() => phone.Node.Nearby.IsNearby(pc.Node.Identity.DeviceId), because: "heard over Bluetooth");
        await Task.Delay(500);

        Assert.Equal(0, air.Hosted);
        Assert.Equal(0, Fake(link).HostCalls + Fake(link).JoinCalls);
    }

    [Fact]
    public async Task TheSendersNetworkStartsOnlyAfterSendAndOnlyOnceTheReceiverAccepted()
    {
        var air = new FakeDirectAir();
        var (sender, receiver, senderLink, receiverLink) = await PairAsync(air, trusted: false);
        await using var _s = sender;
        await using var _r = receiver;
        receiver.Node.Nearby.SetReceiving(true);
        receiver.Handler.ApproveDirect = _ => false; // its user says no
        await Task.Delay(300);
        Assert.Equal(0, senderLink.HostCalls); // nothing before Send

        var declined = sender.Node.Send(receiver.AsDevice(), new[] { sender.CreateFile("a.bin", 10_000) }, ConnectionMethod.Direct);
        await Wait.ForFinishAsync(declined);
        Assert.Equal(TransferState.Failed, declined.State);
        Assert.Equal(DirectSessionState.Negotiating, declined.DirectSession!.FailedAt);
        Assert.Contains("didn't accept", declined.Error!.Message);
        Assert.Equal(0, senderLink.HostCalls); // asked first; it said no; nothing was started
        Assert.Equal(0, receiverLink.JoinCalls + receiverLink.StopCalls); // and the receiver's Wi-Fi was never touched
        Assert.Single(receiver.Handler.DirectRequests);
        AssertNothingArrived(receiver);

        receiver.Handler.ApproveDirect = _ => true;
        var accepted = sender.Node.Send(receiver.AsDevice(), new[] { sender.CreateFile("b.bin", 10_000) }, ConnectionMethod.Direct);
        await Wait.ForFinishAsync(accepted);
        Assert.Equal(TransferState.Completed, accepted.State);
        Assert.Equal(1, senderLink.HostCalls);
    }

    // ---- Direct mode on the same router still goes direct ----

    [Fact]
    public async Task OnTheSameRouterDirectModeStillSetsUpAVerifiedDirectLinkOverTheLanControlChannel()
    {
        var air = new FakeDirectAir();
        var (sender, receiver, senderLink, receiverLink) = await PairAsync(air);
        await using var _s = sender;
        await using var _r = receiver;
        var file = sender.CreateFile("video.mp4", 3_000_000, 2);
        var states = new List<DirectSessionState>();

        var session = sender.Node.Send(receiver.AsDevice(), new[] { file }, ConnectionMethod.Direct);
        session.DirectChanged += s => { lock (states) states.Add(s.DirectSession!.State); };
        await Wait.ForFinishAsync(session);

        Assert.Equal(TransferState.Completed, session.State);
        Assert.Equal(ConnectionMethod.Direct, session.Mode);
        Assert.Equal(TransferPath.Direct, session.Path);
        var direct = session.DirectSession!;
        Assert.Equal(DirectControlChannel.Lan, direct.Channel);
        Assert.Equal(DirectSessionState.Completed, direct.State);
        Assert.Equal(receiver.Node.Identity.Fingerprint, direct.PeerFingerprint);
        // The sender hosted (on demand, with a fresh passphrase) and the receiver joined.
        Assert.Same(senderLink, air.LastHostLink);
        Assert.Same(receiverLink, air.LastJoinLink);
        Assert.NotEqual(sender.Node.Direct.OwnNetwork().Passphrase, senderLink.HostedNetworks.Single().Passphrase);
        Assert.Equal(sender.Node.Direct.OwnNetwork().Ssid, senderLink.HostedNetworks.Single().Ssid);
        Assert.Equal(20, senderLink.HostedNetworks.Single().Passphrase.Length);
        // Each stage in order; "started" was never taken for "joined", nor "joined" for "verified".
        lock (states)
        {
            var order = new[] { DirectSessionState.Hosting, DirectSessionState.OfferingCredentials, DirectSessionState.Joining, DirectSessionState.Verifying,
                DirectSessionState.Verified, DirectSessionState.Transferring, DirectSessionState.Completed };
            Assert.Equal(order, states.Distinct().Where(order.Contains).ToArray());
        }

        // The receiver got it over the direct network, and both let go afterwards.
        var received = receiver.Node.Transfers.Sessions.Single(s => s.Direction == TransferDirection.Receive);
        Assert.Equal(TransferPath.Direct, received.Path);
        Assert.Equal(TestFiles.Hash(file), TestFiles.Hash(Path.Combine(receiver.ReceiveFolder, "video.mp4")));
        await Wait.UntilAsync(() => senderLink.Hosting == null && receiverLink.JoinedTo == null, because: "network off, receiver left");
        Assert.Null(sender.Node.Direct.HostedNetwork);
    }

    [Fact]
    public async Task AWindowsSenderHostsAndAnAndroidReceiverJoinsAsOrdinaryWiFi()
    {
        var air = new FakeDirectAir();
        var (sender, receiver, senderLink, receiverLink) = await PairAsync(air, Pc, DeviceKinds.Laptop, Phone, DeviceKinds.Phone);
        await using var _s = sender;
        await using var _r = receiver;

        var session = sender.Node.Send(receiver.AsDevice(), new[] { sender.CreateFile("report.pdf", 200_000) }, ConnectionMethod.Direct);
        await Wait.ForFinishAsync(session);

        Assert.Equal(TransferState.Completed, session.State);
        Assert.Equal(Pc, air.LastHost);
        Assert.Equal(Phone, air.LastJoiner);
        Assert.True(air.LastJoin!.HostIsComputer); // the phone joins a computer's network as ordinary Wi-Fi straight away
    }

    [Fact]
    public async Task AnAndroidSenderHostsAndAWindowsReceiverJoinsItsGroupAsAWiFiClient()
    {
        var air = new FakeDirectAir();
        var (sender, receiver, senderLink, receiverLink) = await PairAsync(air, Phone, DeviceKinds.Phone, Pc, DeviceKinds.Laptop);
        await using var _s = sender;
        await using var _r = receiver;

        var session = sender.Node.Send(receiver.AsDevice(), new[] { sender.CreateFile("photo.jpg", 200_000) }, ConnectionMethod.Direct);
        await Wait.ForFinishAsync(session);

        Assert.Equal(TransferState.Completed, session.State);
        Assert.Equal(Phone, air.LastHost);
        Assert.Equal(Pc, air.LastJoiner); // the PC's "joinpc" role: it joins the phone's group by name and passphrase
        Assert.False(air.LastJoin!.HostIsComputer);
        await Wait.UntilAsync(() => receiverLink.JoinedTo == null, because: "the PC left the phone's network (back to its own Wi-Fi)");
    }

    [Fact]
    public async Task AReceiverWithAnOlderBeamIsReportedAndNothingStarts()
    {
        var air = new FakeDirectAir();
        var (sender, receiver, senderLink, receiverLink) = await PairAsync(air);
        await using var _s = sender;
        await using var _r = receiver;
        // Beam 1.5.3 announced its roles without "shost": it can't take a sender-hosted session.
        sender.Node.Discovery.ReportReachable(receiver.AsDevice() with { DirectRoles = Phone });

        var session = sender.Node.Send(receiver.AsDevice() with { DirectRoles = Phone }, new[] { sender.CreateFile("a.bin", 1000) }, ConnectionMethod.Direct);
        await Wait.ForFinishAsync(session);

        Assert.Equal(TransferState.Failed, session.State);
        Assert.Contains("Update Beam", session.Error!.Message);
        Assert.Equal(0, senderLink.HostCalls);
        AssertNothingArrived(receiver);
    }

    // ---- Control channels ----

    [Fact]
    public async Task WithTheLanIsolatedTheSessionIsSetUpOverBluetoothAndTheFilesStillGoDirect()
    {
        var air = new FakeDirectAir();
        var bluetooth = new FakeNearbyAir();
        var phoneLink = air.Link(Phone);
        var pcLink = air.Link(Pc);
        await using var phone = new TestNode("Galaxy", directLink: phoneLink, kind: DeviceKinds.Phone, nearbyRadio: bluetooth.Radio());
        await using var pc = new TestNode("Laptop", directLink: pcLink, kind: DeviceKinds.Laptop, nearbyRadio: bluetooth.Radio());
        air.ConnectDirectOnly(phone.Node, pc.Node);
        pc.Trust(phone);
        pc.Node.Nearby.SetReceiving(true);
        phone.Node.Nearby.SetLooking(true);
        await Wait.UntilAsync(() => phone.Node.Nearby.IsNearby(pc.Node.Identity.DeviceId), because: "heard over Bluetooth");
        // Listed on the "same Wi-Fi", but the router isolates clients: its address doesn't answer.
        using var dead = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        dead.Start();
        var deadPort = ((IPEndPoint)dead.LocalEndpoint).Port;
        dead.Stop();
        var device = pc.AsDevice() with { Endpoints = new[] { new IPEndPoint(IPAddress.Loopback, deadPort) } };
        phone.Node.Discovery.ReportReachable(device);

        var file = phone.CreateFile("photo.jpg", 300_000);
        var session = phone.Node.Send(device, new[] { file }, ConnectionMethod.Direct);
        await Wait.ForFinishAsync(session);

        Assert.Equal(TransferState.Completed, session.State);
        Assert.Equal(DirectControlChannel.Bluetooth, session.DirectSession!.Channel);
        Assert.Equal(TransferPath.Direct, session.Path);
        Assert.Same(phoneLink, air.LastHostLink);
        Assert.Same(pcLink, air.LastJoinLink);
        // Over Bluetooth the sender's identity is only claimed: the PC asked its user even though it is paired, and
        // checked the claim on the direct network's TLS connection before any file came.
        Assert.Single(pc.Handler.DirectRequests);
        Assert.True(pc.Handler.DirectRequests[0].ClaimedOnly);
        Assert.Equal(TestFiles.Hash(file), TestFiles.Hash(Path.Combine(pc.ReceiveFolder, "photo.jpg")));
    }

    [Fact]
    public async Task ReceiversFoundOverBluetoothOnlyAreReachedOverBluetoothAndTheReceiverAsksItsUser()
    {
        var air = new FakeDirectAir();
        var bluetooth = new FakeNearbyAir();
        var phoneLink = Fake(air.Link(Phone));
        var pcLink = air.Link(Pc);
        await using var pc = new TestNode("Laptop", directLink: pcLink, nearbyRadio: bluetooth.Radio());
        await using var phone = new TestNode("Galaxy", directLink: phoneLink, kind: DeviceKinds.Phone, nearbyRadio: bluetooth.Radio());
        air.ConnectDirectOnly(phone.Node, pc.Node);
        pc.Node.Nearby.SetReceiving(true);
        phone.Node.Nearby.SetLooking(true);
        DeviceInfo? listed = null;
        await Wait.UntilAsync(() => (listed = phone.Node.Discovery.Devices.FirstOrDefault(NearbyService.IsPlaceholder)) != null, because: "PC heard");
        Assert.True(listed!.DirectRoles.HasFlag(DirectRoles.SenderHosted));
        var device = await phone.Node.Nearby.ResolveAsync(listed, CancellationToken.None);
        Assert.Equal(pc.Node.Identity.DeviceId, device.Id);
        Assert.Equal(0, air.Hosted); // finding out who it is started nothing on either side

        pc.Handler.ApproveDirect = _ => false;
        var refused = phone.Node.Send(device, new[] { phone.CreateFile("a.jpg", 1000) }, ConnectionMethod.Direct);
        await Wait.ForFinishAsync(refused);
        Assert.Equal(TransferState.Failed, refused.State);
        Assert.Equal(0, phoneLink.HostCalls);

        pc.Handler.ApproveDirect = _ => true;
        var session = phone.Node.Send(device, new[] { phone.CreateFile("b.jpg", 200_000) }, ConnectionMethod.Direct);
        await Wait.ForFinishAsync(session);
        Assert.Equal(TransferState.Completed, session.State);
        Assert.Equal(DirectControlChannel.Bluetooth, session.DirectSession!.Channel);
    }

    [Fact]
    public async Task ADeviceHeardOnBothChannelsIsListedOnceUnderItsOwnId()
    {
        var air = new FakeDirectAir();
        var bluetooth = new FakeNearbyAir();
        await using var pc = new TestNode("Laptop", directLink: air.Link(Pc), nearbyRadio: bluetooth.Radio());
        await using var phone = new TestNode("Galaxy", directLink: air.Link(Phone), kind: DeviceKinds.Phone, nearbyRadio: bluetooth.Radio());
        air.Connect(phone.Node, () => phone.AsDevice(), pc.Node, () => pc.AsDevice());
        pc.Node.Nearby.SetReceiving(true);
        phone.Node.Nearby.SetLooking(true);
        await Wait.UntilAsync(() => phone.Node.Nearby.IsNearby(pc.Node.Identity.DeviceId), because: "heard over Bluetooth");
        await Task.Delay(400);

        var entries = phone.Node.Discovery.Devices.Where(d => d.Name == "Laptop").ToList();
        Assert.Single(entries);
        Assert.Equal(pc.Node.Identity.DeviceId, entries[0].Id);
        Assert.DoesNotContain(phone.Node.Discovery.Devices, NearbyService.IsPlaceholder);
        var channels = phone.Node.ChannelsOf(entries[0]);
        Assert.True(channels.LocalNetwork);
        Assert.True(channels.Bluetooth);
    }

    // ---- Failures: reported at their stage, nothing sent, nothing sent another way ----

    [Fact]
    public async Task WhenTheNetworkCantStartTheFailureNamesThatStageAndNothingIsSent()
    {
        var air = new FakeDirectAir { HostError = "Wi-Fi Direct isn't supported by this adapter." };
        var (sender, receiver, senderLink, receiverLink) = await PairAsync(air);
        await using var _s = sender;
        await using var _r = receiver;
        var paths = new List<TransferPath>();

        var session = sender.Node.Send(receiver.AsDevice(), new[] { sender.CreateFile("a.bin", 100_000) }, ConnectionMethod.Direct);
        session.PathChanged += s => { lock (paths) paths.Add(s.Path); };
        await Wait.ForFinishAsync(session);

        Assert.Equal(TransferState.Failed, session.State);
        Assert.Equal(TransferErrorKind.DirectFailed, session.Error!.Kind);
        Assert.Equal(DirectSessionState.Hosting, session.DirectSession!.FailedAt);
        Assert.Contains("starting the direct network", session.Error.Message);
        Assert.Contains("isn't supported", session.Error.Message);
        Assert.Equal(0, receiverLink.JoinCalls);
        Assert.DoesNotContain(TransferPath.Network, paths); // never quietly over the router
        AssertNothingArrived(receiver);
        await Wait.UntilAsync(() => receiver.Node.Direct.IsIdleForTests, because: "the receiver let go of the proposal");
    }

    [Fact]
    public async Task ANetworkThatSaysItStartedButIsntUsableFailsAtHostingAndIsTurnedOffAgain()
    {
        var air = new FakeDirectAir { HostUsable = false };
        var (sender, receiver, senderLink, receiverLink) = await PairAsync(air);
        await using var _s = sender;
        await using var _r = receiver;
        sender.Node.Direct.HostReadyTimeout = TimeSpan.FromMilliseconds(600);

        var session = sender.Node.Send(receiver.AsDevice(), new[] { sender.CreateFile("a.bin", 1000) }, ConnectionMethod.Direct);
        await Wait.ForFinishAsync(session);

        Assert.Equal(TransferState.Failed, session.State);
        Assert.Equal(DirectSessionState.Hosting, session.DirectSession!.FailedAt);
        Assert.Contains("isn't usable", session.Error!.Message);
        Assert.Null(senderLink.Hosting); // turned off again
        Assert.Equal(0, receiverLink.JoinCalls);
        AssertNothingArrived(receiver);
    }

    [Fact]
    public async Task ANetworkHiddenFromWiFiScansIsStillJoinedWithTheDetailsItWasSent()
    {
        var air = new FakeDirectAir { HiddenFromScans = true };
        var (sender, receiver, _, receiverLink) = await PairAsync(air);
        await using var _s = sender;
        await using var _r = receiver;

        var session = sender.Node.Send(receiver.AsDevice(), new[] { sender.CreateFile("a.bin", 50_000) }, ConnectionMethod.Direct);
        await Wait.ForFinishAsync(session);

        Assert.Equal(TransferState.Completed, session.State);
        Assert.DoesNotContain(air.LastJoin!.Ssid, receiverLink.VisibleNetworks());
    }

    [Fact]
    public async Task WhenThePhonesUserDeniesTheWiFiPromptTheFailureIsAtReceiverJoin()
    {
        var air = new FakeDirectAir();
        var (sender, receiver, senderLink, receiverLink) = await PairAsync(air);
        await using var _s = sender;
        await using var _r = receiver;
        receiverLink.NextJoins = FakeDirectAir.JoinOutcome.PromptDenied;

        var session = sender.Node.Send(receiver.AsDevice(), new[] { sender.CreateFile("a.bin", 1000) }, ConnectionMethod.Direct);
        await Wait.ForFinishAsync(session);

        Assert.Equal(TransferState.Failed, session.State);
        Assert.Equal(DirectSessionState.Joining, session.DirectSession!.FailedAt);
        Assert.Contains("receiver join", session.Error!.Message);
        Assert.Contains("didn't allow", session.Error.Message); // the receiver's own reason, sent back over the LAN
        await Wait.UntilAsync(() => senderLink.Hosting == null, because: "the sender's network goes off");
        AssertNothingArrived(receiver);
    }

    [Fact]
    public async Task AJoinThatNeverFinishesTimesOutAtReceiverJoinAndTheReceiverGivesUp()
    {
        var air = new FakeDirectAir();
        var (sender, receiver, senderLink, receiverLink) = await PairAsync(air);
        await using var _s = sender;
        await using var _r = receiver;
        receiverLink.NextJoins = FakeDirectAir.JoinOutcome.NeverAnswers;
        sender.Node.Direct.JoinWait = TimeSpan.FromSeconds(2);

        var session = sender.Node.Send(receiver.AsDevice(), new[] { sender.CreateFile("a.bin", 1000) }, ConnectionMethod.Direct);
        await Wait.ForFinishAsync(session);

        Assert.Equal(TransferState.Failed, session.State);
        Assert.Equal(DirectSessionState.Joining, session.DirectSession!.FailedAt);
        Assert.Contains("didn't join the direct network in time", session.Error!.Message);
        await Wait.UntilAsync(() => receiver.Node.Direct.IsIdleForTests, because: "the receiver withdrew its join when told the session ended");
        Assert.Null(receiverLink.JoinedTo);
        AssertNothingArrived(receiver);
    }

    [Fact]
    public async Task WrongNetworkDetailsFailAtReceiverJoin()
    {
        var air = new FakeDirectAir();
        var (sender, receiver, senderLink, receiverLink) = await PairAsync(air);
        await using var _s = sender;
        await using var _r = receiver;
        senderLink.StartsWithOtherPassphrase = true;

        var session = sender.Node.Send(receiver.AsDevice(), new[] { sender.CreateFile("a.bin", 1000) }, ConnectionMethod.Direct);
        await Wait.ForFinishAsync(session);

        Assert.Equal(DirectSessionState.Joining, session.DirectSession!.FailedAt);
        Assert.Contains("Wrong passphrase", session.Error!.Message);
        AssertNothingArrived(receiver);
    }

    [Fact]
    public async Task WhenTheRouteCantBeVerifiedNoFileDataIsSent()
    {
        var air = new FakeDirectAir();
        var (sender, receiver, senderLink, receiverLink) = await PairAsync(air);
        await using var _s = sender;
        await using var _r = receiver;
        // The sender's connection to the receiver would leave over another route (not from its address on the direct
        // network): the receiver sees it come from outside and doesn't confirm.
        sender.Node.Direct.OwnAddressNear = _ => null;

        var session = sender.Node.Send(receiver.AsDevice(), new[] { sender.CreateFile("a.bin", 100_000) }, ConnectionMethod.Direct);
        await Wait.ForFinishAsync(session);

        Assert.Equal(TransferState.Failed, session.State);
        Assert.Equal(DirectSessionState.Verifying, session.DirectSession!.FailedAt);
        Assert.Contains("verifying the direct link", session.Error!.Message);
        Assert.Null(session.DirectSession.VerifiedEndpoint);
        Assert.Equal(1, receiverLink.JoinCalls); // it did join; that alone is not a verified link
        AssertNothingArrived(receiver);
        await Wait.UntilAsync(() => receiverLink.JoinedTo == null && senderLink.Hosting == null, because: "both let go");
    }

    [Fact]
    public async Task AnImpostorAnsweringAtTheReceiversAddressIsRefusedBeforeAnythingStarts()
    {
        var air = new FakeDirectAir();
        var senderLink = Fake(air.Link(Pc));
        await using var sender = new TestNode("Sender", directLink: senderLink, kind: DeviceKinds.Laptop);
        await using var receiver = new TestNode("Receiver", directLink: air.Link(Phone), kind: DeviceKinds.Phone);
        await using var impostor = new TestNode("Receiver", directLink: air.Link(Phone), kind: DeviceKinds.Phone);
        air.Wire(sender.Node, receiver.Node, impostor.Node);
        // Announced on the network with the receiver's id and certificate fingerprint, but at the impostor's address.
        var spoofed = receiver.AsDevice() with { Endpoints = impostor.AsDevice().Endpoints };
        sender.Node.Discovery.ReportReachable(spoofed);

        var session = sender.Node.Send(spoofed, new[] { sender.CreateFile("a.bin", 1000) }, ConnectionMethod.Direct);
        await Wait.ForFinishAsync(session);

        Assert.Equal(TransferState.Failed, session.State);
        Assert.Equal(DirectSessionState.Negotiating, session.DirectSession!.FailedAt);
        Assert.Contains("couldn't confirm", session.Error!.Message);
        Assert.Equal(0, senderLink.HostCalls);
        Assert.Empty(impostor.Handler.DirectRequests);
        AssertNothingArrived(impostor);
        AssertNothingArrived(receiver);
    }

    // ---- Fallback only when the user chooses it ----

    [Fact]
    public async Task AfterADirectFailureTheUserCanChooseToSendOverTheNetworkAsASeparateTransfer()
    {
        var air = new FakeDirectAir { HostError = "busy" };
        var (sender, receiver, _, _) = await PairAsync(air);
        await using var _s = sender;
        await using var _r = receiver;
        var file = sender.CreateFile("a.bin", 300_000);
        var failed = sender.Node.Send(receiver.AsDevice(), new[] { file }, ConnectionMethod.Direct);
        await Wait.ForFinishAsync(failed);
        Assert.Equal(TransferState.Failed, failed.State);
        await Task.Delay(300);
        AssertNothingArrived(receiver); // waited: nothing went over the network by itself

        var overNetwork = sender.Node.SendOverNetworkInstead(failed);

        Assert.NotSame(failed, overNetwork);
        Assert.NotEqual(failed.Id, overNetwork.Id);
        Assert.Equal(ConnectionMethod.SameNetwork, overNetwork.Mode);
        Assert.Equal(ConnectionMethod.Direct, failed.Mode); // the failed one stays what it was
        await Wait.ForFinishAsync(overNetwork);
        Assert.Equal(TransferState.Completed, overNetwork.State);
        Assert.Equal(TransferPath.Network, overNetwork.Path);
        Assert.Equal(TestFiles.Hash(file), TestFiles.Hash(Path.Combine(receiver.ReceiveFolder, "a.bin")));
    }

    // ---- Several receivers ----

    [Fact]
    public async Task SeveralReceiversGetIndependentSessionsOnOneNetworkWithHonestMixedResults()
    {
        var air = new FakeDirectAir();
        var slow = new SlowFiles();
        var senderLink = Fake(air.Link(Pc));
        await using var sender = new TestNode("Sender", directLink: senderLink, kind: DeviceKinds.Laptop, externalFiles: slow);
        var completes = new TestNode("Completes", directLink: air.Link(Phone), kind: DeviceKinds.Phone);
        var declines = new TestNode("Declines", directLink: air.Link(Phone), kind: DeviceKinds.Phone);
        var timesOut = new TestNode("TimesOut", directLink: air.Link(Phone), kind: DeviceKinds.Phone);
        var cancelled = new TestNode("Cancelled", directLink: air.Link(Phone), kind: DeviceKinds.Phone);
        var receivers = new[] { completes, declines, timesOut, cancelled };
        try
        {
            foreach (var r in receivers)
            {
                air.Connect(sender.Node, () => sender.AsDevice(), r.Node, () => r.AsDevice());
                if (r != declines) r.Trust(sender);
            }

            declines.Node.Nearby.SetReceiving(true);
            declines.Handler.ApproveDirect = _ => false;
            Fake(timesOut.Node.Direct.LinkForTests!).NextJoins = FakeDirectAir.JoinOutcome.NeverAnswers;
            sender.Node.Direct.JoinWait = TimeSpan.FromSeconds(3);
            var big = slow.Add(sender.CreateFile("video.mp4", 25L * 1024 * 1024, 7));

            var sessions = receivers.ToDictionary(r => r, r => sender.Node.Send(r.AsDevice(), new[] { big }, ConnectionMethod.Direct));
            Assert.Equal(4, sessions.Values.Select(s => s.Id).Distinct().Count());

            await Wait.UntilAsync(() => sessions[cancelled].Path == TransferPath.Direct && sessions[completes].Path == TransferPath.Direct,
                because: "two transferring directly");
            Assert.Equal(4, sessions.Values.Select(s => s.DirectSession!.Id).Distinct().Count());
            sessions[cancelled].Cancel(); // only that one

            foreach (var s in sessions.Values) await Wait.ForFinishAsync(s, TimeSpan.FromSeconds(90));
            Assert.Equal(TransferState.Completed, sessions[completes].State);
            Assert.Equal(TransferState.Failed, sessions[declines].State);
            Assert.Equal(DirectSessionState.Negotiating, sessions[declines].DirectSession!.FailedAt);
            Assert.Equal(TransferState.Failed, sessions[timesOut].State);
            Assert.Equal(DirectSessionState.Joining, sessions[timesOut].DirectSession!.FailedAt);
            Assert.Equal(TransferState.Cancelled, sessions[cancelled].State);
            Assert.Equal(DirectSessionState.Cancelled, sessions[cancelled].DirectSession!.State);

            // One network for all of them, kept until the last one was done; then off.
            Assert.Equal(1, senderLink.HostCalls);
            Assert.Equal(TestFiles.Hash(sender.SourcePath("video.mp4")), TestFiles.Hash(Path.Combine(completes.ReceiveFolder, "video.mp4")));
            await Wait.UntilAsync(() => senderLink.Hosting == null, because: "network off after the last receiver");
            AssertNothingArrived(declines);
            AssertNothingArrived(timesOut);
        }
        finally
        {
            foreach (var r in receivers) await r.DisposeAsync();
        }
    }

    [Fact]
    public async Task MoreReceiversThanTheNetworkServesAreToldToWait()
    {
        var air = new FakeDirectAir();
        var senderLink = Fake(air.Link(Pc));
        await using var sender = new TestNode("Sender", directLink: senderLink, kind: DeviceKinds.Laptop);
        var receivers = Enumerable.Range(1, DirectManager.MaxDirectReceivers + 1)
            .Select(i => new TestNode("Phone " + i, directLink: air.Link(Phone), kind: DeviceKinds.Phone)).ToList();
        try
        {
            foreach (var r in receivers)
            {
                air.Connect(sender.Node, () => sender.AsDevice(), r.Node, () => r.AsDevice());
                r.Trust(sender);
                Fake(r.Node.Direct.LinkForTests!).NextJoins = FakeDirectAir.JoinOutcome.NeverAnswers; // they stay in setup
            }

            sender.Node.Direct.JoinWait = TimeSpan.FromSeconds(4);
            var file = sender.CreateFile("a.bin", 1000);
            var first = receivers.Take(DirectManager.MaxDirectReceivers).Select(r => sender.Node.Send(r.AsDevice(), new[] { file }, ConnectionMethod.Direct)).ToList();
            await Wait.UntilAsync(() => first.All(s => s.DirectSession!.State == DirectSessionState.Joining), because: "all joining");

            var extra = sender.Node.Send(receivers[^1].AsDevice(), new[] { file }, ConnectionMethod.Direct);
            await Wait.ForFinishAsync(extra);
            Assert.Equal(TransferState.Failed, extra.State);
            Assert.Contains($"at most {DirectManager.MaxDirectReceivers}", extra.Error!.Message);
            Assert.All(first, s => Assert.False(s.IsFinished)); // the others go on
            foreach (var s in first) s.Cancel();
        }
        finally
        {
            foreach (var r in receivers) await r.DisposeAsync();
        }
    }

    // ---- The direct path stays put ----

    [Fact]
    public async Task LosingTheRouterDoesntMatterOnceTheDirectLinkIsVerified()
    {
        var air = new FakeDirectAir();
        var slow = new SlowFiles();
        var (sender, receiver, _, _) = await PairAsync(air, files: slow);
        await using var _s = sender;
        await using var _r = receiver;
        using var router = new FlakyProxy(new IPEndPoint(IPAddress.Loopback, receiver.Node.Transfers.Port));
        var viaRouter = receiver.AsDevice(router.Port);
        sender.Node.Discovery.ReportReachable(viaRouter);
        const long size = 20L * 1024 * 1024;
        var file = sender.CreateFile("video.mp4", size, 5);

        var session = sender.Node.Send(viaRouter, new[] { slow.Add(file) }, ConnectionMethod.Direct);
        await Wait.UntilAsync(() => session.Path == TransferPath.Direct && session.GetSnapshot().TransferredBytes > 2_000_000, because: "sending directly");
        var setupConnections = router.Connections; // the control channel's (propose, offer)

        router.Unreachable = true; // the router (and internet) is gone
        router.CutAll();
        await Wait.ForFinishAsync(session, TimeSpan.FromSeconds(60));

        Assert.Equal(TransferState.Completed, session.State);
        Assert.Equal(TransferPath.Direct, session.Path);
        Assert.Equal(setupConnections, router.Connections); // no file data ever went through the router
        Assert.Equal(TestFiles.Hash(file), TestFiles.Hash(Path.Combine(receiver.ReceiveFolder, "video.mp4")));
    }

    // ---- Cancelling, late answers, and what is let go ----

    [Fact]
    public async Task CancellingWhileTheNetworkStartsTurnsItOffWhenItComesUpLateAndNeverAsksTheReceiverToJoin()
    {
        var air = new FakeDirectAir();
        var (sender, receiver, senderLink, receiverLink) = await PairAsync(air);
        await using var _s = sender;
        await using var _r = receiver;
        senderLink.HoldHost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var session = sender.Node.Send(receiver.AsDevice(), new[] { sender.CreateFile("a.bin", 1000) }, ConnectionMethod.Direct);
        await Wait.UntilAsync(() => senderLink.HostCalls == 1, because: "starting the network");
        session.Cancel();
        await Wait.ForFinishAsync(session);
        Assert.Equal(TransferState.Cancelled, session.State);
        Assert.Equal(DirectSessionState.Cancelled, session.DirectSession!.State);

        senderLink.HoldHost.SetResult(); // the radio answers after all
        await Wait.UntilAsync(() => senderLink.HostedNetworks.Count == 1 && senderLink.Hosting == null, because: "the late network was turned off again");
        Assert.Equal(0, receiverLink.JoinCalls);
        await Wait.UntilAsync(() => receiver.Node.Direct.IsIdleForTests, because: "the receiver dropped the proposal");
    }

    [Fact]
    public async Task AJoinThatFinishesAfterItsSessionWasCancelledIsLeftAgainWithoutDisturbingAnotherReceiver()
    {
        var air = new FakeDirectAir();
        var slow = new SlowFiles();
        var senderLink = Fake(air.Link(Pc));
        await using var sender = new TestNode("Sender", directLink: senderLink, kind: DeviceKinds.Laptop, externalFiles: slow);
        var lateLink = Fake(air.Link(Phone));
        var otherLink = Fake(air.Link(Phone));
        await using var late = new TestNode("Late", directLink: lateLink, kind: DeviceKinds.Phone);
        await using var other = new TestNode("Other", directLink: otherLink, kind: DeviceKinds.Phone);
        air.Connect(sender.Node, () => sender.AsDevice(), late.Node, () => late.AsDevice());
        air.Connect(sender.Node, () => sender.AsDevice(), other.Node, () => other.AsDevice());
        late.Trust(sender);
        other.Trust(sender);
        lateLink.JoinDelay = TimeSpan.FromSeconds(1.5); // the OS answers slowly
        var file = sender.CreateFile("video.mp4", 25L * 1024 * 1024, 6);

        var keeps = sender.Node.Send(other.AsDevice(), new[] { slow.Add(file) }, ConnectionMethod.Direct);
        await Wait.UntilAsync(() => keeps.Path == TransferPath.Direct, because: "the other receiver transfers directly");
        var cancelled = sender.Node.Send(late.AsDevice(), new[] { slow.Add(file) }, ConnectionMethod.Direct);
        await Wait.UntilAsync(() => lateLink.JoinCalls == 1, because: "the late receiver is joining");
        cancelled.Cancel();
        await Wait.ForFinishAsync(cancelled);

        // Its join still succeeds (the network is on for the other receiver), after the session ended: it leaves again.
        await Wait.UntilAsync(() => late.Node.Direct.IsIdleForTests && lateLink.StopCalls > 0 && lateLink.JoinedTo == null,
            because: "the late join was undone", timeout: TimeSpan.FromSeconds(10));
        Assert.NotNull(senderLink.Hosting); // the sender's network stays: the other receiver still uses it
        Assert.Same(senderLink, otherLink.JoinedTo);
        await Wait.ForFinishAsync(keeps, TimeSpan.FromSeconds(60));
        Assert.Equal(TransferState.Completed, keeps.State);
        Assert.Equal(1, senderLink.HostCalls);
        AssertNothingArrived(late);
    }

    [Fact]
    public async Task ADirectOfferNeverTakesTheRadioFromThePhonePageOrTouchesItsNetwork()
    {
        var air = new FakeDirectAir();
        var (sender, receiver, _, receiverLink) = await PairAsync(air, Phone, DeviceKinds.Phone, Pc, DeviceKinds.Laptop);
        await using var _s = sender;
        await using var _r = receiver;
        var browserNetwork = await receiver.Node.Direct.HostForBrowserAsync(CancellationToken.None); // the PC's Phone page is on

        var session = sender.Node.Send(receiver.AsDevice(), new[] { sender.CreateFile("a.bin", 1000) }, ConnectionMethod.Direct);
        await Wait.ForFinishAsync(session);

        Assert.Equal(TransferState.Failed, session.State);
        Assert.Contains("in use", session.Error!.Message);
        Assert.Equal(browserNetwork, receiverLink.Hosting); // still on
        Assert.Equal(0, receiverLink.JoinCalls);
        Assert.Equal(0, receiverLink.StopCalls);
    }

    [Fact]
    public async Task ASenderThatIsOnAnotherDevicesNetworkDoesntStartItsOwnOverIt()
    {
        var air = new FakeDirectAir();
        var (sender, receiver, senderLink, _) = await PairAsync(air);
        await using var _s = sender;
        await using var _r = receiver;
        var slow = new SlowFiles();
        await using var other = new TestNode("Other", directLink: air.Link(Pc), kind: DeviceKinds.Laptop, externalFiles: slow);
        air.Connect(other.Node, () => other.AsDevice(), sender.Node, () => sender.AsDevice());
        sender.Trust(other);
        // The sender is receiving from another device over that device's network right now (for a while).
        var incoming = other.Node.Send(sender.AsDevice(), new[] { slow.Add(other.CreateFile("x.bin", 20L * 1024 * 1024, 3)) }, ConnectionMethod.Direct);
        await Wait.UntilAsync(() => senderLink.JoinedTo != null, because: "the sender joined the other device's network");

        var session = sender.Node.Send(receiver.AsDevice(), new[] { sender.CreateFile("a.bin", 1000) }, ConnectionMethod.Direct);
        await Wait.ForFinishAsync(session);

        Assert.Equal(TransferState.Failed, session.State);
        Assert.Contains("in use", session.Error!.Message);
        Assert.Equal(0, senderLink.HostCalls);
        Assert.NotNull(senderLink.JoinedTo); // still on the other device's network, undisturbed
        await Wait.ForFinishAsync(incoming, TimeSpan.FromSeconds(60));
        Assert.Equal(TransferState.Completed, incoming.State);
    }

    // ---- The session protocol, message by message ----

    /// <summary>Sends one direct request the way another Beam does, over the shared network (or from <paramref name="from"/>'s address).</summary>
    private static async Task<DirectResponseMessage> AskAsync(TestNode from, TestNode to, DirectRequestMessage request, IPEndPoint? at = null)
    {
        await using var connection = await SecureTransport.ConnectAsync(from.Node.Transfers.Identity, from.Node.Transfers.CreateHello(ConnectionPurpose.Direct),
            new[] { at ?? to.AsDevice().Endpoints[0] }, null, "peer", CancellationToken.None);
        await connection.Channel.SendAsync(FrameType.DirectRequest, request, CancellationToken.None);
        return await connection.Channel.ReadMessageAsync<DirectResponseMessage>(FrameType.DirectResponse, TimeSpan.FromSeconds(30), CancellationToken.None);
    }

    private static DirectRequestMessage Propose(TestNode from, TestNode to, string? sessionId = null) => new()
    {
        Action = DirectActions.Propose,
        SessionId = sessionId ?? DirectSession.NewToken(),
        ReceiverId = to.Node.Identity.DeviceId,
        SenderId = from.Node.Identity.DeviceId,
    };

    private static DirectRequestMessage Offer(string sessionId, string? passphrase = "abcdefgh12345678") => new()
    {
        Action = DirectActions.Offer,
        SessionId = sessionId,
        Nonce = DirectSession.NewToken(),
        Ssid = "DIRECT-xy-Beam-Evil",
        Passphrase = passphrase,
        TtlSeconds = 60,
        Port = 1234,
    };

    [Fact]
    public async Task ProposalsMeantForAnotherDeviceMalformedOrFromUnpairedSendersWithReceiveClosedAreRefused()
    {
        var air = new FakeDirectAir();
        var (sender, receiver, _, receiverLink) = await PairAsync(air, trusted: false);
        await using var _s = sender;
        await using var _r = receiver;
        await using var bystander = new TestNode("Bystander");

        var forOther = await AskAsync(sender, receiver, Propose(sender, bystander));
        var malformed = await AskAsync(sender, receiver, Propose(sender, receiver, sessionId: "../../etc"));
        var receiveClosed = await AskAsync(sender, receiver, Propose(sender, receiver));

        Assert.False(forOther.Ok);
        Assert.Contains("another device", forOther.Message);
        Assert.False(malformed.Ok);
        Assert.False(receiveClosed.Ok);
        Assert.True(receiveClosed.Declined);
        Assert.Empty(receiver.Handler.DirectRequests); // its user wasn't even asked
        Assert.Equal(0, receiverLink.JoinCalls);
    }

    [Fact]
    public async Task OffersAreOnlyTakenForAnAcceptedSessionFromTheSameDeviceAndNeverTwice()
    {
        var air = new FakeDirectAir();
        var (sender, receiver, _, receiverLink) = await PairAsync(air);
        await using var _s = sender;
        await using var _r = receiver;
        await using var stranger = new TestNode("Stranger");

        // No proposal first: refused.
        Assert.False((await AskAsync(sender, receiver, Offer(DirectSession.NewToken()))).Ok);

        var proposal = Propose(sender, receiver);
        Assert.True((await AskAsync(sender, receiver, proposal)).Ok);
        Assert.True((await AskAsync(sender, receiver, proposal)).Ok); // a repeat is harmless
        Assert.False((await AskAsync(stranger, receiver, proposal)).Ok); // the same session from another certificate
        Assert.False((await AskAsync(stranger, receiver, Offer(proposal.SessionId!))).Ok);
        Assert.False((await AskAsync(sender, receiver, Offer(proposal.SessionId!, passphrase: "short"))).Ok); // malformed
        Assert.Equal(0, receiverLink.JoinCalls);

        Assert.True((await AskAsync(sender, receiver, new DirectRequestMessage { Action = DirectActions.End, SessionId = proposal.SessionId })).Ok);
        // Ended: neither the proposal nor an offer for it comes back (replayed).
        var replay = await AskAsync(sender, receiver, proposal);
        Assert.False(replay.Ok);
        Assert.Contains("already ended", replay.Message);
        Assert.False((await AskAsync(sender, receiver, Offer(proposal.SessionId!))).Ok);
        Assert.Equal(0, receiverLink.JoinCalls);
    }

    [Fact]
    public async Task AnAcceptedProposalExpiresWhenNoOfferFollows()
    {
        var air = new FakeDirectAir();
        var (sender, receiver, _, receiverLink) = await PairAsync(air);
        await using var _s = sender;
        await using var _r = receiver;
        receiver.Node.Direct.ProposalLifetime = TimeSpan.FromMilliseconds(500);

        var proposal = Propose(sender, receiver);
        Assert.True((await AskAsync(sender, receiver, proposal)).Ok);
        await Wait.UntilAsync(() => receiver.Node.Direct.IsIdleForTests, because: "expired");

        Assert.False((await AskAsync(sender, receiver, Offer(proposal.SessionId!))).Ok);
        Assert.Equal(0, receiverLink.JoinCalls);
    }

    [Fact]
    public async Task ReadyAndVerifyOnlyCountFromTheSessionsPeerWithItsNonceOverTheDirectNetwork()
    {
        var air = new FakeDirectAir();
        var (sender, receiver, _, receiverLink) = await PairAsync(air);
        await using var _s = sender;
        await using var _r = receiver;
        await using var stranger = new TestNode("Stranger");
        receiverLink.NextJoins = FakeDirectAir.JoinOutcome.NeverAnswers; // the real receiver never reports in

        var session = sender.Node.Send(receiver.AsDevice(), new[] { sender.CreateFile("a.bin", 1000) }, ConnectionMethod.Direct);
        await Wait.UntilAsync(() => session.DirectSession!.State == DirectSessionState.Joining, because: "waiting for the receiver to join");
        var direct = session.DirectSession!;
        var senderOnDirect = new IPEndPoint(FakeDirectAir.DirectAddress, sender.Node.Transfers.Port);
        var ready = new DirectRequestMessage { Action = DirectActions.Ready, SessionId = direct.Id, Nonce = direct.Nonce, Port = stranger.Node.Transfers.Port };

        // Someone else who learned the session's id and nonce still has the wrong certificate.
        var wrongPeer = await AskAsync(stranger, sender, ready, senderOnDirect);
        var wrongNonce = await AskAsync(receiver, sender, new DirectRequestMessage { Action = DirectActions.Ready, SessionId = direct.Id, Nonce = DirectSession.NewToken(),
            Port = receiver.Node.Transfers.Port }, senderOnDirect);
        // And the right peer, but over the router instead of the direct network.
        var overRouter = await AskAsync(receiver, sender, ready);

        Assert.False(wrongPeer.Ok);
        Assert.False(wrongNonce.Ok);
        Assert.False(overRouter.Ok);
        Assert.Equal(DirectSessionState.Joining, direct.State); // none of that moved it on
        Assert.Null(direct.VerifiedEndpoint);
        session.Cancel();
        await Wait.ForFinishAsync(session);
    }

    // ---- The state model ----

    [Fact]
    public void SessionStatesOnlyMoveForwardAndFinalStatesStayFinal()
    {
        var session = new DirectSession("peer", "Peer", TimeSpan.FromMinutes(1));
        Assert.Equal(DirectSessionState.Negotiating, session.State);
        Assert.False(session.TryMove(DirectSessionState.Joining)); // can't skip hosting and the offer
        Assert.False(session.TryMove(DirectSessionState.Verified)); // nor verification
        Assert.False(session.TryMove(DirectSessionState.Transferring));
        Assert.True(session.TryMove(DirectSessionState.Hosting));
        Assert.True(session.TryMove(DirectSessionState.OfferingCredentials));
        Assert.True(session.TryMove(DirectSessionState.Joining));
        Assert.False(session.MarkVerified(new IPEndPoint(DirectAddress, 1))); // joined isn't verified
        Assert.True(session.TryMove(DirectSessionState.Verifying));
        Assert.True(session.MarkVerified(new IPEndPoint(DirectAddress, 1)));
        Assert.True(session.TryMove(DirectSessionState.Transferring));
        Assert.True(session.TryMove(DirectSessionState.Interrupted));
        var id = session.Id;
        var nonce = session.Nonce;
        Assert.True(session.Restart());
        Assert.NotEqual(id, session.Id);
        Assert.NotEqual(nonce, session.Nonce);
        Assert.Null(session.VerifiedEndpoint);
        Assert.True(session.Fail("gone"));
        Assert.Equal(DirectSessionState.Negotiating, session.FailedAt);
        Assert.False(session.Fail("again")); // first failure wins
        Assert.False(session.Cancel());
        Assert.False(session.TryMove(DirectSessionState.Hosting)); // a late callback can't bring it back
        Assert.True(session.CancellationToken.IsCancellationRequested);
        Assert.Equal("gone", session.Error);
    }

    // ---- Unchanged helpers ----


    [Fact]
    public async Task AddressesOfThePreferredPathComeFirst()
    {
        var air = new FakeDirectAir();
        await using var pc = new TestNode("Laptop", directLink: air.Link(DirectRoles.Host));
        await using var phone = new TestNode("Galaxy", directLink: air.Link(DirectRoles.Host | DirectRoles.Join), kind: DeviceKinds.Phone);
        air.Connect(phone.Node, () => phone.AsDevice(), pc.Node, () => pc.AsDevice());
        var both = pc.AsDevice() with { Endpoints = new[] { new IPEndPoint(DirectAddress, 1), new IPEndPoint(IPAddress.Loopback, 1) } };

        Assert.Equal(IPAddress.Loopback, phone.Node.Direct.Order(both).Endpoints[0].Address);
        phone.Node.Settings.Update(s => s.Connection = ConnectionMethod.Direct);
        Assert.Equal(DirectAddress, phone.Node.Direct.Order(both).Endpoints[0].Address);
        Assert.Equal(2, phone.Node.Direct.Order(both).Endpoints.Count); // the other path stays as a fallback
    }

    [Fact]
    public void LocalAdaptersAreReadOnTheFirstCheck()
    {
        // Regression: the adapter cache's "never read" marker overflowed, so direct links were never recognized.
        DirectAddresses.Invalidate();
        Assert.False(DirectAddresses.IsDirect(IPAddress.Parse("203.0.113.9")));
        Assert.True(DirectAddresses.LocalAddressCount > 0);
    }

    [Fact]
    public void FreeSpaceIsMeasuredOnTheFoldersOwnStorage()
    {
        // Regression: on Android "/" (the read-only system partition, 0 bytes free) was measured instead.
        using var dir = new TempDir();
        Assert.True(Transfer.DiskSpace.GetAvailableBytes(dir.Combine("not", "created", "yet")) > 0);
    }

    [Fact]
    public void RolesAndDirectAdaptersAreRecognized()
    {
        Assert.Equal(DirectRoles.Host | DirectRoles.Join, DirectRolesText.Parse(DirectRolesText.Format(DirectRoles.Host | DirectRoles.Join)));
        Assert.Equal(DirectRoles.None, DirectRolesText.Parse("teleport"));
        Assert.True(DirectAddresses.IsDirectAdapter("p2p-wlan0-3", ""));
        Assert.True(DirectAddresses.IsDirectAdapter("Local Area Connection* 12", "Microsoft Wi-Fi Direct Virtual Adapter #2"));
        Assert.False(DirectAddresses.IsDirectAdapter("Wi-Fi", "Intel(R) Wi-Fi 6 AX201 160MHz"));
        Assert.Equal("WIFI:T:WPA;S:DIRECT-ab-Beam-Diaz\\;s PC;P:pa\\:ss\\\\word;;", new DirectNetwork("DIRECT-ab-Beam-Diaz;s PC", "pa:ss\\word").ToWifiQrText());
    }

}

public class PairingTests
{
    [Fact]
    public void PairingCodeTravelsInTheLinksFragment()
    {
        var code = new PairingCode
        {
            DeviceId = "25e3dae905a447668a7b90283ce5fb79",
            Name = "DILSHODJON",
            Fingerprint = new string('a', 64),
            Kind = DeviceKinds.Laptop,
            Addresses = new() { "192.168.0.109", "10.0.0.5:47822", "not-an-address" },
            Ssid = "DIRECT-ab-Beam-DILSHODJON",
            Passphrase = "k3Zp9qWx2mNb",
        };
        var link = code.AppendTo("http://192.168.0.109:47831/0123abcd/");
        Assert.StartsWith("http://192.168.0.109:47831/0123abcd/#beam=", link);

        var parsed = PairingCode.TryParse(link)!;
        Assert.Equal(code.DeviceId, parsed.DeviceId);
        Assert.Equal("DILSHODJON", parsed.Name);
        Assert.Equal(new DirectNetwork("DIRECT-ab-Beam-DILSHODJON", "k3Zp9qWx2mNb"), parsed.Network);
        Assert.Equal(new[] { "192.168.0.109", "10.0.0.5:47822" }, parsed.Addresses);
        Assert.Equal(new[] { new IPEndPoint(IPAddress.Parse("192.168.0.109"), AppInfo.TransferPort), new IPEndPoint(IPAddress.Parse("10.0.0.5"), 47822) },
            parsed.ToDevice().Endpoints);
        Assert.Equal(code.DeviceId, PairingCode.TryParse(code.AppendTo(null))!.DeviceId);

        Assert.Null(PairingCode.TryParse("http://192.168.0.109:47831/0123abcd/"));
        Assert.Null(PairingCode.TryParse("WIFI:T:WPA;S:x;P:y;;"));
        Assert.Null(PairingCode.TryParse("beam:%%%"));
        Assert.Null(PairingCode.TryParse("beam:" + Convert.ToBase64String("{\"i\":\"\"}"u8.ToArray())));
    }
}
