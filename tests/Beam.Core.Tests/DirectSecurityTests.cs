using System.Net;
using Beam.Core.Direct;
using Beam.Core.Discovery;
using Beam.Core.Protocol;
using Beam.Core.Settings;
using Beam.Core.Transfer;

namespace Beam.Core.Tests;

/// <summary>
/// Who may make this device start its network, join another, or stop its link. Any device on the network can open a
/// connection with a self-signed certificate and claim any device id in its hello, so neither is enough on its own.
/// </summary>
public class DirectSecurityTests
{
    /// <summary>Sends one direct request the way another Beam does (optionally claiming someone else's device id).</summary>
    private static async Task<DirectResponseMessage> AskAsync(TestNode from, TestNode to, DirectRequestMessage request, string? claimedId = null)
    {
        var hello = from.Node.Transfers.CreateHello(ConnectionPurpose.Direct);
        if (claimedId != null) hello.DeviceId = claimedId;
        await using var connection = await SecureTransport.ConnectAsync(from.Node.Transfers.Identity, hello, to.AsDevice().Endpoints, null,
            to.Node.Settings.Current.DeviceName, CancellationToken.None);
        await connection.Channel.SendAsync(FrameType.DirectRequest, request, CancellationToken.None);
        return await connection.Channel.ReadMessageAsync<DirectResponseMessage>(FrameType.DirectResponse, TimeSpan.FromSeconds(30), CancellationToken.None);
    }

    private static FakeDirectAir.FakeLink Fake(IDirectLink link) => (FakeDirectAir.FakeLink)link;

    [Fact]
    public async Task AnUnpairedDeviceCantMakeThisDeviceJoinItsNetworkEvenWithReceiveOpen()
    {
        var air = new FakeDirectAir();
        var pcLink = air.Link(DirectRoles.Host | DirectRoles.JoinComputers);
        await using var pc = new TestNode("Laptop", directLink: pcLink, kind: DeviceKinds.Laptop);
        await using var stranger = new TestNode("Stranger");
        pc.Node.Nearby.SetReceiving(true); // senders may connect directly (the default)
        Assert.True(pc.Node.Settings.Current.AllowDirectSenders);

        var reply = await AskAsync(stranger, pc, new DirectRequestMessage { Action = DirectActions.Join, Ssid = "DIRECT-xx-Evil", Passphrase = "passphrase123" });

        Assert.False(reply.Ok);
        Assert.True(reply.Declined);
        Assert.Equal(0, Fake(pcLink).JoinCalls);
        Assert.Null(pc.Node.Direct.KnownNetworkFor(stranger.Node.Identity.DeviceId)); // nothing remembered to join later
    }

    [Fact]
    public async Task AnUnpairedDeviceCantStartTheNetworkOfADeviceWithReceiveClosed()
    {
        var air = new FakeDirectAir();
        var pcLink = air.Link(DirectRoles.Host | DirectRoles.JoinComputers);
        await using var pc = new TestNode("Laptop", directLink: pcLink, kind: DeviceKinds.Laptop);
        await using var stranger = new TestNode("Stranger");

        var reply = await AskAsync(stranger, pc, new DirectRequestMessage { Action = DirectActions.Host });

        Assert.False(reply.Ok);
        Assert.True(reply.Declined);
        Assert.Null(reply.Passphrase);
        Assert.Equal(0, Fake(pcLink).HostCalls);
        Assert.Null(pc.Node.Direct.HostedNetwork);
    }

    [Fact]
    public async Task WithReceiveOpenAnUnpairedSenderGetsTheNetworkOnlyUntilItGoesOff()
    {
        var air = new FakeDirectAir();
        var pcLink = air.Link(DirectRoles.Host | DirectRoles.JoinComputers);
        await using var pc = new TestNode("Laptop", directLink: pcLink, kind: DeviceKinds.Laptop);
        await using var sender = new TestNode("Galaxy", kind: DeviceKinds.Phone);
        pc.Node.Nearby.SetReceiving(true);

        var reply = await AskAsync(sender, pc, new DirectRequestMessage { Action = DirectActions.Host });
        Assert.True(reply.Ok);
        Assert.Equal(pc.Node.Direct.OwnNetwork().Passphrase, reply.Passphrase);

        // Receive closes and the network goes off: it gets a new passphrase, so that sender can't join it later unasked.
        await AskAsync(sender, pc, new DirectRequestMessage { Action = DirectActions.Stop });
        pc.Node.Nearby.SetReceiving(false);
        await Wait.UntilAsync(() => pc.Node.Direct.HostedNetwork == null, because: "the network went off");
        Assert.NotEqual(reply.Passphrase, pc.Node.Direct.OwnNetwork().Passphrase);
    }

    [Fact]
    public async Task APairedDeviceMayAskThisDeviceToJoinItsNetwork()
    {
        var air = new FakeDirectAir();
        var phoneLink = air.Link(DirectRoles.Host | DirectRoles.Join);
        var pcLink = air.Link(DirectRoles.Host | DirectRoles.JoinComputers);
        await using var phone = new TestNode("Galaxy", directLink: phoneLink, kind: DeviceKinds.Phone);
        await using var pc = new TestNode("Laptop", directLink: pcLink, kind: DeviceKinds.Laptop);
        pc.Trust(phone);
        var network = await phoneLink.HostAsync(null, CancellationToken.None);

        var reply = await AskAsync(phone, pc, new DirectRequestMessage { Action = DirectActions.Join, Ssid = network.Ssid, Passphrase = network.Passphrase });

        Assert.True(reply.Ok);
        Assert.Equal(1, Fake(pcLink).JoinCalls);
        Assert.Equal(network.Ssid, pc.Node.Direct.KnownNetworkFor(phone.Node.Identity.DeviceId)!.Ssid);
    }

    [Fact]
    public async Task ADeviceClaimingAPairedDevicesIdIsRefused()
    {
        var air = new FakeDirectAir();
        var pcLink = air.Link(DirectRoles.Host | DirectRoles.JoinComputers);
        await using var pc = new TestNode("Laptop", directLink: pcLink, kind: DeviceKinds.Laptop);
        await using var phone = new TestNode("Galaxy", kind: DeviceKinds.Phone);
        await using var impostor = new TestNode("Galaxy");
        pc.Trust(phone);
        var phoneId = phone.Node.Identity.DeviceId;

        // Same id in its hello, but its own certificate: not the paired phone.
        var join = await AskAsync(impostor, pc, new DirectRequestMessage { Action = DirectActions.Join, Ssid = "DIRECT-xx-Evil", Passphrase = "passphrase123" }, claimedId: phoneId);
        var host = await AskAsync(impostor, pc, new DirectRequestMessage { Action = DirectActions.Host }, claimedId: phoneId);
        var prefer = await AskAsync(impostor, pc, new DirectRequestMessage { Action = DirectActions.Prefer, Method = "direct" }, claimedId: phoneId);

        Assert.All(new[] { join, host, prefer }, r => Assert.False(r.Ok));
        Assert.Equal(0, Fake(pcLink).JoinCalls);
        Assert.Equal(0, Fake(pcLink).HostCalls);
        Assert.Null(pc.Node.Direct.KnownNetworkFor(phoneId));
        Assert.Equal(ConnectionMethod.SameNetwork, pc.Node.Direct.MethodFor(phoneId));
    }

    [Fact]
    public async Task OnlyTheDeviceALinkWasStartedForCanStopIt()
    {
        var air = new FakeDirectAir();
        var pcLink = air.Link(DirectRoles.Host | DirectRoles.JoinComputers);
        await using var pc = new TestNode("Laptop", directLink: pcLink, kind: DeviceKinds.Laptop);
        await using var phone = new TestNode("Galaxy", kind: DeviceKinds.Phone);
        await using var stranger = new TestNode("Stranger");
        pc.Trust(phone);
        Assert.True((await AskAsync(phone, pc, new DirectRequestMessage { Action = DirectActions.Host })).Ok);

        var reply = await AskAsync(stranger, pc, new DirectRequestMessage { Action = DirectActions.Stop });
        Assert.True(reply.Ok); // nothing to stop for it
        Assert.Equal(0, air.Stopped);
        Assert.NotNull(pc.Node.Direct.HostedNetwork);

        await AskAsync(phone, pc, new DirectRequestMessage { Action = DirectActions.Stop });
        Assert.Equal(1, air.Stopped);
    }

    [Fact]
    public async Task AcceptingATransferLetsItsSenderMoveItToADirectLink()
    {
        // Mid-transfer "Switch to direct" works without pairing or Receive: the user already accepted these files.
        var air = new FakeDirectAir();
        var slow = new SlowFiles();
        var pcLink = air.Link(DirectRoles.Host | DirectRoles.JoinComputers);
        await using var pc = new TestNode("Laptop", directLink: pcLink, kind: DeviceKinds.Laptop);
        await using var phone = new TestNode("Galaxy", externalFiles: slow, directLink: air.Link(DirectRoles.Host | DirectRoles.Join), kind: DeviceKinds.Phone);
        air.Connect(phone.Node, () => phone.AsDevice(), pc.Node, () => pc.AsDevice());

        // Before any transfer: refused.
        Assert.False(await phone.Node.Direct.SwitchAsync(pc.Node.Identity.DeviceId, ConnectionMethod.Direct));
        Assert.Equal(0, Fake(pcLink).HostCalls);

        var session = phone.Node.Send(pc.AsDevice(), new[] { slow.Add(phone.CreateFile("video.mp4", 20L * 1024 * 1024, 3)) });
        await Wait.UntilAsync(() => session.State == TransferState.Transferring, because: "accepted");
        Assert.True(await phone.Node.Direct.SwitchAsync(pc.Node.Identity.DeviceId, ConnectionMethod.Direct));
        Assert.Equal(1, Fake(pcLink).HostCalls);
        await Wait.UntilAsync(() => session.Path == TransferPath.Direct, because: "moved to the direct link");
        await Wait.ForFinishAsync(session, TimeSpan.FromSeconds(60));
        Assert.Equal(TransferState.Completed, session.State);

        // Its files are in: that unpaired phone no longer keeps the PC's network on.
        await Wait.UntilAsync(() => pc.Node.Direct.HostedNetwork == null, because: "the network went off after the transfer");
    }

    [Fact]
    public async Task AnUnpairedSenderDoesntKeepTheNetworkOnAfterReceiveCloses()
    {
        // Receive left (and its grace time over): the network goes off, whoever asked for it while Receive was open.
        var air = new FakeDirectAir();
        await using var pc = new TestNode("Laptop", directLink: air.Link(DirectRoles.Host | DirectRoles.JoinComputers), kind: DeviceKinds.Laptop);
        await using var stranger = new TestNode("Stranger");
        await using var browser = new TestNode("Other");
        pc.Node.Nearby.SetReceiving(true);
        Assert.True((await AskAsync(stranger, pc, new DirectRequestMessage { Action = DirectActions.Host })).Ok);
        await Wait.UntilAsync(() => pc.Node.Direct.HostedNetwork != null, because: "on");
        Assert.True((await AskAsync(browser, pc, new DirectRequestMessage { Action = DirectActions.Prefer, Method = "direct" })).Ok);

        pc.Node.Nearby.SetReceiving(false);
        await Wait.UntilAsync(() => pc.Node.Direct.HostedNetwork == null, because: "the network went off when Receive closed");
        Assert.True(air.Stopped >= 1);
    }

    [Fact]
    public async Task APairedDeviceStillKeepsTheNetworkAfterReceiveCloses()
    {
        var air = new FakeDirectAir();
        await using var pc = new TestNode("Laptop", directLink: air.Link(DirectRoles.Host | DirectRoles.JoinComputers), kind: DeviceKinds.Laptop);
        await using var phone = new TestNode("Galaxy", kind: DeviceKinds.Phone);
        pc.Trust(phone);
        pc.Node.Nearby.SetReceiving(true);
        Assert.True((await AskAsync(phone, pc, new DirectRequestMessage { Action = DirectActions.Host })).Ok);

        pc.Node.Nearby.SetReceiving(false);
        await Task.Delay(300);
        Assert.NotNull(pc.Node.Direct.HostedNetwork); // until the phone says it is done
        await AskAsync(phone, pc, new DirectRequestMessage { Action = DirectActions.Stop });
        Assert.Null(pc.Node.Direct.HostedNetwork);
    }

    [Fact]
    public async Task AFakedWiFiAnnouncementDoesntLetAnotherDeviceStopALinkOrLockOutAPairedDevice()
    {
        var air = new FakeDirectAir();
        await using var pc = new TestNode("Laptop", directLink: air.Link(DirectRoles.Host | DirectRoles.JoinComputers), kind: DeviceKinds.Laptop);
        await using var phone = new TestNode("Galaxy", kind: DeviceKinds.Phone);
        await using var paired = new TestNode("Pixel", kind: DeviceKinds.Phone);
        await using var attacker = new TestNode("Galaxy");
        pc.Trust(paired);
        pc.Node.Nearby.SetReceiving(true);
        Assert.True((await AskAsync(phone, pc, new DirectRequestMessage { Action = DirectActions.Host })).Ok);

        // Anyone can announce any device id with their own certificate's fingerprint (the last announcement wins).
        pc.Node.Discovery.ReportReachable(phone.AsDevice() with { Fingerprint = attacker.Node.Identity.Fingerprint });
        pc.Node.Discovery.ReportReachable(paired.AsDevice() with { Fingerprint = attacker.Node.Identity.Fingerprint });

        var stop = await AskAsync(attacker, pc, new DirectRequestMessage { Action = DirectActions.Stop }, claimedId: phone.Node.Identity.DeviceId);
        Assert.False(stop.Ok);
        Assert.NotNull(pc.Node.Direct.HostedNetwork);
        Assert.Equal(ConnectionMethod.Direct, pc.Node.Direct.MethodFor(phone.Node.Identity.DeviceId));

        // The paired phone is still recognised by its own certificate.
        Assert.True((await AskAsync(paired, pc, new DirectRequestMessage { Action = DirectActions.Host })).Ok);
    }

    [Fact]
    public async Task AnUnpairedDeviceCantPullThisDeviceOffANetworkItJoinedToReceive()
    {
        var air = new FakeDirectAir();
        var pcLink = air.Link(DirectRoles.Host | DirectRoles.JoinComputers);
        var phoneLink = air.Link(DirectRoles.Host | DirectRoles.Join);
        await using var pc = new TestNode("Laptop", directLink: pcLink, kind: DeviceKinds.Laptop);
        await using var phone = new TestNode("Galaxy", directLink: phoneLink, kind: DeviceKinds.Phone);
        await using var stranger = new TestNode("Stranger");
        pc.Trust(phone);
        pc.Node.Settings.Update(s => s.AllowDirectSenders = true);
        var network = await phoneLink.HostAsync(null, CancellationToken.None);
        Assert.True((await AskAsync(phone, pc, new DirectRequestMessage { Action = DirectActions.Join, Ssid = network.Ssid, Passphrase = network.Passphrase })).Ok);
        pc.Node.Nearby.SetReceiving(true); // Receive open, but on the phone's network now
        var hosted = Fake(pcLink).HostCalls;

        var reply = await AskAsync(stranger, pc, new DirectRequestMessage { Action = DirectActions.Host });

        Assert.False(reply.Ok);
        Assert.Equal(hosted, Fake(pcLink).HostCalls);
    }

    [Fact]
    public async Task AnAcceptedButUnpairedSenderCantMakeThisDeviceJoinItsNetworkWhileAPairedDeviceUsesTheLink()
    {
        var air = new FakeDirectAir();
        var slow = new SlowFiles();
        var pcLink = air.Link(DirectRoles.Host | DirectRoles.JoinComputers);
        await using var pc = new TestNode("Laptop", directLink: pcLink, kind: DeviceKinds.Laptop);
        await using var paired = new TestNode("Galaxy", kind: DeviceKinds.Phone);
        await using var sender = new TestNode("Stranger", externalFiles: slow);
        pc.Trust(paired);
        Assert.True((await AskAsync(paired, pc, new DirectRequestMessage { Action = DirectActions.Host })).Ok); // the paired phone's link

        // The user accepted files from the stranger, and they are still coming.
        var session = sender.Node.Send(pc.AsDevice(), new[] { slow.Add(sender.CreateFile("video.mp4", 20L * 1024 * 1024, 4)) });
        await Wait.UntilAsync(() => session.State == TransferState.Transferring, because: "accepted");

        var reply = await AskAsync(sender, pc, new DirectRequestMessage { Action = DirectActions.Join, Ssid = "DIRECT-xx-Other", Passphrase = "passphrase123" });

        Assert.False(reply.Ok);
        Assert.Equal(0, Fake(pcLink).JoinCalls);
        Assert.NotNull(pc.Node.Direct.HostedNetwork); // the paired phone's network stays on
        session.Cancel();
    }

    [Fact]
    public async Task AnAcceptedButUnpairedSenderCantPullThisDeviceOffAPairedDevicesNetwork()
    {
        var air = new FakeDirectAir();
        var slow = new SlowFiles();
        var pcLink = air.Link(DirectRoles.Host | DirectRoles.JoinComputers);
        var phoneLink = air.Link(DirectRoles.Host | DirectRoles.Join);
        await using var pc = new TestNode("Laptop", directLink: pcLink, kind: DeviceKinds.Laptop);
        await using var paired = new TestNode("Galaxy", directLink: phoneLink, kind: DeviceKinds.Phone);
        await using var sender = new TestNode("Stranger", externalFiles: slow);
        pc.Trust(paired);
        var network = await phoneLink.HostAsync(null, CancellationToken.None);
        Assert.True((await AskAsync(paired, pc, new DirectRequestMessage { Action = DirectActions.Join, Ssid = network.Ssid, Passphrase = network.Passphrase })).Ok);

        var session = sender.Node.Send(pc.AsDevice(), new[] { slow.Add(sender.CreateFile("video.mp4", 20L * 1024 * 1024, 5)) });
        await Wait.UntilAsync(() => session.State == TransferState.Transferring, because: "accepted");
        var hosted = Fake(pcLink).HostCalls;

        var reply = await AskAsync(sender, pc, new DirectRequestMessage { Action = DirectActions.Host });

        Assert.False(reply.Ok);
        Assert.Equal(hosted, Fake(pcLink).HostCalls);
        session.Cancel();
    }

    // ---- Same Wi-Fi stays same Wi-Fi ----

    [Fact]
    public async Task WithSameWiFiChosenASenderNeverStartsOrJoinsANetwork()
    {
        // Everything that could tempt it: the receiver has Receive open with its network on, the sender is paired and
        // knows that network, and it is in range.
        var air = new FakeDirectAir();
        var phoneLink = air.Link(DirectRoles.Host | DirectRoles.Join);
        await using var pc = new TestNode("Laptop", directLink: air.Link(DirectRoles.Host | DirectRoles.JoinComputers), kind: DeviceKinds.Laptop);
        await using var phone = new TestNode("Galaxy", directLink: phoneLink, kind: DeviceKinds.Phone);
        air.Connect(phone.Node, () => phone.AsDevice(), pc.Node, () => pc.AsDevice());
        pc.Node.Nearby.SetReceiving(true);
        await Wait.UntilAsync(() => pc.Node.Direct.HostedNetwork != null, because: "the receiver's network is on");
        phone.Node.Pair(PairingCode.TryParse(pc.Node.CreatePairingCode().AppendTo(null))!);
        Assert.NotNull(phone.Node.Direct.KnownNetworkFor(pc.Node.Identity.DeviceId));
        Assert.Equal(ConnectionMethod.SameNetwork, phone.Node.Direct.MethodFor(pc.Node.Identity.DeviceId));
        var hostedBefore = air.Hosted;

        var paths = new List<TransferPath>();
        var session = phone.Node.Send(pc.AsDevice(), new[] { phone.CreateFile("photo.jpg", 400_000) });
        session.PathChanged += s => { lock (paths) paths.Add(s.Path); };
        await Wait.ForFinishAsync(session, TimeSpan.FromSeconds(30));

        Assert.Equal(TransferState.Completed, session.State);
        Assert.All(paths.Where(p => p != TransferPath.Unknown), p => Assert.Equal(TransferPath.Network, p));
        Assert.Equal(0, Fake(phoneLink).HostCalls);
        Assert.Equal(0, Fake(phoneLink).JoinCalls);
        Assert.Equal(hostedBefore, air.Hosted); // the sender didn't ask the receiver to start anything either
    }

    // ---- A direct link lost mid-transfer ----

    [Fact]
    public async Task ALostDirectLinkIsRejoinedAndTheTransferResumesWithoutStartingOver()
    {
        var air = new FakeDirectAir();
        var slow = new SlowFiles();
        await using var pc = new TestNode("Laptop", directLink: air.Link(DirectRoles.Host | DirectRoles.JoinComputers), kind: DeviceKinds.Laptop);
        await using var phone = new TestNode("Galaxy", externalFiles: slow, directLink: air.Link(DirectRoles.Host | DirectRoles.Join), kind: DeviceKinds.Phone);
        air.Connect(phone.Node, () => phone.AsDevice(), pc.Node, () => pc.AsDevice());
        phone.Node.Transfers.IsDirectAddress = a => a.Equals(FakeDirectAir.DirectAddress) && air.LinkUp; // like a real adapter
        const long size = 30L * 1024 * 1024;
        var file = phone.CreateFile("video.mp4", size, 8);

        var session = phone.Node.Send(pc.AsDevice(), new[] { slow.Add(file) });
        await Wait.UntilAsync(() => session.State == TransferState.Transferring, because: "accepted");
        Assert.True(await phone.Node.Direct.SwitchAsync(pc.Node.Identity.DeviceId, ConnectionMethod.Direct));
        await Wait.UntilAsync(() => session.Path == TransferPath.Direct && session.GetSnapshot().TransferredBytes > 4_000_000, because: "sending directly");

        // The phone falls off the PC's network, and the connection over it dies.
        air.LoseLink();
        Assert.True(session.InterruptConnection(switchingPath: false));

        await Wait.UntilAsync(() => air.Joined == 2 && session.Path == TransferPath.Direct, because: "rejoined and resumed directly", timeout: TimeSpan.FromSeconds(30));
        await Wait.ForFinishAsync(session, TimeSpan.FromSeconds(90));
        Assert.Equal(TransferState.Completed, session.State);
        Assert.Equal(TestFiles.Hash(file), TestFiles.Hash(Path.Combine(pc.ReceiveFolder, "video.mp4")));
        Assert.True(slow.BytesRead < size + 3L * 1024 * 1024, $"read {slow.BytesRead} bytes of {size}: the transfer started over");
        Assert.Single(pc.Handler.Requests); // resuming didn't ask the user again
        Assert.Empty(Directory.GetFiles(pc.ReceiveFolder, "*.beampart"));
        Assert.Single(Directory.GetFiles(pc.ReceiveFolder)); // no second copy
    }

    [Fact]
    public async Task WhenTheLostDirectLinkCantBeRejoinedTheTransferFinishesOverWiFi()
    {
        var air = new FakeDirectAir();
        var slow = new SlowFiles();
        await using var pc = new TestNode("Laptop", directLink: air.Link(DirectRoles.Host | DirectRoles.JoinComputers), kind: DeviceKinds.Laptop);
        await using var phone = new TestNode("Galaxy", externalFiles: slow, directLink: air.Link(DirectRoles.Host | DirectRoles.Join), kind: DeviceKinds.Phone);
        air.Connect(phone.Node, () => phone.AsDevice(), pc.Node, () => pc.AsDevice());
        phone.Node.Transfers.IsDirectAddress = a => a.Equals(FakeDirectAir.DirectAddress) && air.LinkUp;
        var problems = new List<string>();
        phone.Node.Direct.Problem += problems.Add;
        const long size = 30L * 1024 * 1024;
        var file = phone.CreateFile("video.mp4", size, 9);

        var session = phone.Node.Send(pc.AsDevice(), new[] { slow.Add(file) });
        await Wait.UntilAsync(() => session.State == TransferState.Transferring, because: "accepted");
        Assert.True(await phone.Node.Direct.SwitchAsync(pc.Node.Identity.DeviceId, ConnectionMethod.Direct));
        await Wait.UntilAsync(() => session.Path == TransferPath.Direct && session.GetSnapshot().TransferredBytes > 4_000_000, because: "sending directly");

        air.FailJoins = true; // out of range now
        air.LoseLink();
        Assert.True(session.InterruptConnection(switchingPath: false));

        await Wait.UntilAsync(() => session.Path == TransferPath.Network && session.State == TransferState.Transferring, because: "continued over Wi-Fi",
            timeout: TimeSpan.FromSeconds(30));
        await Wait.ForFinishAsync(session, TimeSpan.FromSeconds(90));
        Assert.Equal(TransferState.Completed, session.State);
        Assert.Equal(TestFiles.Hash(file), TestFiles.Hash(Path.Combine(pc.ReceiveFolder, "video.mp4")));
        Assert.True(slow.BytesRead < size + 3L * 1024 * 1024, $"read {slow.BytesRead} bytes of {size}: the transfer started over");
        Assert.Single(pc.Handler.Requests);
        Assert.Single(Directory.GetFiles(pc.ReceiveFolder));
    }
}
