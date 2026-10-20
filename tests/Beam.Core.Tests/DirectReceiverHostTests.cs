using Beam.Core;
using Beam.Core.Direct;
using Beam.Core.Discovery;
using Beam.Core.Protocol;
using Beam.Core.Settings;
using Beam.Core.Transfer;

namespace Beam.Core.Tests;

/// <summary>
/// An iPhone can't start a Wi-Fi network (iOS doesn't let apps), so when it sends directly to a PC, the PC starts its
/// network (its Mobile hotspot when it can share its internet) and the iPhone joins it. Android phones still host.
/// Stand-ins for the radios, real Beam logic.
/// </summary>
public class DirectReceiverHostTests
{
    private const DirectRoles Pc = DirectRoles.Host | DirectRoles.JoinComputers;
    private const DirectRoles IPhone = DirectRoles.Join; // joins only

    private static FakeDirectAir.FakeLink Fake(IDirectLink link) => (FakeDirectAir.FakeLink)link;

    private static (TestNode Phone, TestNode Pc, FakeDirectAir.FakeLink PhoneLink, FakeDirectAir.FakeLink PcLink) IPhoneAndPc(
        FakeDirectAir air, bool pcOnline = true, SlowFiles? files = null, string pcName = "Laptop")
    {
        var phoneLink = Fake(air.Link(IPhone));
        var pcLink = Fake(air.Link(Pc, sharesInternet: pcOnline));
        var phone = new TestNode("iPhone", externalFiles: files, directLink: phoneLink, kind: DeviceKinds.Phone);
        var pc = new TestNode(pcName, directLink: pcLink, kind: DeviceKinds.Laptop);
        pc.Node.Direct.ReceiverHostGrace = TimeSpan.FromMilliseconds(500);
        air.Connect(phone.Node, () => phone.AsDevice(), pc.Node, () => pc.AsDevice());
        pc.Trust(phone);
        return (phone, pc, phoneLink, pcLink);
    }

    [Theory]
    [InlineData(true)] // the PC is online: its hotspot
    [InlineData(false)] // it isn't: its Wi-Fi Direct network
    public async Task AnIPhoneSendingToAPcJoinsThePcsNetworkAndLeavesItWhenDone(bool pcOnline)
    {
        var air = new FakeDirectAir();
        var (phone, pc, phoneLink, pcLink) = IPhoneAndPc(air, pcOnline);
        await using var _a = phone;
        await using var _b = pc;
        Assert.True(pc.AsDevice().DirectRoles.HasFlag(DirectRoles.HostsForSenders));
        Assert.True(phone.Node.Direct.CanSendDirectlyTo(pc.AsDevice()));

        var file = phone.CreateFile("video.mp4", 3_000_000, 1);
        var session = phone.Node.Send(pc.AsDevice(), new[] { file }, ConnectionMethod.Direct);
        await Wait.ForFinishAsync(session);

        Assert.True(session.State == TransferState.Completed, session.Error?.Message);
        Assert.Equal(TransferPath.Direct, session.Path);
        Assert.True(session.DirectSession!.OnReceiversNetwork);
        Assert.Equal(0, phoneLink.HostCalls);
        Assert.Equal(1, pcLink.HostCalls);
        Assert.Equal(pcOnline, pcLink.HostedNetworks.Single().SharesInternet);
        Assert.Same(phoneLink, air.LastJoinLink);
        Assert.Equal(TestFiles.Hash(file), TestFiles.Hash(Path.Combine(pc.ReceiveFolder, "video.mp4")));
        await Wait.UntilAsync(() => pcLink.Hosting == null && phoneLink.JoinedTo == null, because: "the PC's network went off and the iPhone left it");
        await Wait.UntilAsync(() => pc.Node.Direct.IsIdleForTests && phone.Node.Direct.IsIdleForTests, because: "nothing is held any more");
    }

    [Theory]
    [InlineData(true)] // the iPhone sends: the PC's network
    [InlineData(false)] // the PC sends: its network as always
    public async Task AnIPhoneThatCantReadTheGatewayFindsTheHostByTheAddressItWasGiven(bool iPhoneSends)
    {
        var air = new FakeDirectAir();
        var phoneLink = Fake(air.Link(IPhone, reportsNoHost: true));
        var pcLink = Fake(air.Link(Pc, sharesInternet: true));
        await using var phone = new TestNode("iPhone", directLink: phoneLink, kind: DeviceKinds.Phone);
        await using var pc = new TestNode("Laptop", directLink: pcLink, kind: DeviceKinds.Laptop);
        air.Connect(phone.Node, () => phone.AsDevice(), pc.Node, () => pc.AsDevice());
        pc.Trust(phone);
        phone.Trust(pc);
        var (from, to) = iPhoneSends ? (phone, pc) : (pc, phone);

        var session = from.Node.Send(to.AsDevice(), new[] { from.CreateFile("a.jpg", 200_000) }, ConnectionMethod.Direct);
        await Wait.ForFinishAsync(session);

        Assert.True(session.State == TransferState.Completed, session.Error?.Message);
        Assert.Equal(TransferPath.Direct, session.Path);
        Assert.Same(phoneLink, air.LastJoinLink);
    }

    [Fact]
    public async Task AnAndroidPhoneStillHostsItself()
    {
        // Only senders that can't host ask: an Android phone keeps starting its own network.
        var air = new FakeDirectAir();
        var phoneLink = Fake(air.Link(DirectRoles.Host | DirectRoles.Join));
        var pcLink = Fake(air.Link(Pc, sharesInternet: true));
        await using var phone = new TestNode("Redmi", directLink: phoneLink, kind: DeviceKinds.Phone);
        await using var pc = new TestNode("Laptop", directLink: pcLink, kind: DeviceKinds.Laptop);
        air.Connect(phone.Node, () => phone.AsDevice(), pc.Node, () => pc.AsDevice());
        pc.Trust(phone);

        var session = phone.Node.Send(pc.AsDevice(), new[] { phone.CreateFile("a.jpg", 200_000) }, ConnectionMethod.Direct);
        await Wait.ForFinishAsync(session);

        Assert.True(session.State == TransferState.Completed, session.Error?.Message);
        Assert.False(session.DirectSession!.OnReceiversNetwork);
        Assert.Equal(1, phoneLink.HostCalls);
        Assert.Equal(0, pcLink.HostCalls);
    }

    [Fact]
    public async Task APcSendingToAnotherPcStillHostsItself()
    {
        var air = new FakeDirectAir();
        var senderLink = Fake(air.Link(Pc, sharesInternet: true));
        var receiverLink = Fake(air.Link(Pc, sharesInternet: true));
        await using var sender = new TestNode("Desk", directLink: senderLink, kind: DeviceKinds.Desktop);
        await using var receiver = new TestNode("Laptop", directLink: receiverLink, kind: DeviceKinds.Laptop);
        air.Connect(sender.Node, () => sender.AsDevice(), receiver.Node, () => receiver.AsDevice());
        receiver.Trust(sender);

        var session = sender.Node.Send(receiver.AsDevice(), new[] { sender.CreateFile("a.jpg", 200_000) }, ConnectionMethod.Direct);
        await Wait.ForFinishAsync(session);

        Assert.True(session.State == TransferState.Completed, session.Error?.Message);
        Assert.False(session.DirectSession!.OnReceiversNetwork);
        Assert.Equal(1, senderLink.HostCalls);
        Assert.Equal(0, receiverLink.HostCalls);
    }

    [Fact]
    public async Task WhenTheIPhoneCantJoinThePcsNetworkTheSendFailsSayingWhereAndThePcsNetworkGoesOff()
    {
        var air = new FakeDirectAir();
        var (phone, pc, phoneLink, pcLink) = IPhoneAndPc(air);
        await using var _a = phone;
        await using var _b = pc;
        phoneLink.NextJoins = FakeDirectAir.JoinOutcome.PromptDenied; // "Join network?" → Cancel

        var session = phone.Node.Send(pc.AsDevice(), new[] { phone.CreateFile("a.jpg", 200_000) }, ConnectionMethod.Direct);
        await Wait.ForFinishAsync(session);

        Assert.Equal(TransferState.Failed, session.State);
        Assert.Contains("receiver join", session.Error!.Message);
        Assert.Equal(0, phoneLink.HostCalls);
        await Wait.UntilAsync(() => pcLink.Hosting == null, because: "the PC's network went off");
    }

    [Fact]
    public async Task ALostLinkIsSetUpAgainOnTheSameNetworkWithoutRestartingItAndResumes()
    {
        var air = new FakeDirectAir();
        var slow = new SlowFiles();
        var (phone, pc, phoneLink, pcLink) = IPhoneAndPc(air, files: slow);
        await using var _a = phone;
        await using var _b = pc;
        pc.Node.Direct.ReceiverHostGrace = TimeSpan.FromSeconds(30);
        const long size = 30L * 1024 * 1024;
        var file = phone.CreateFile("video.mp4", size, 7);

        var session = phone.Node.Send(pc.AsDevice(), new[] { slow.Add(file) }, ConnectionMethod.Direct);
        await Wait.UntilAsync(() => session.Path == TransferPath.Direct && session.GetSnapshot().TransferredBytes > 4_000_000, because: "sending directly");
        var firstSession = session.DirectSession!.Id;
        Assert.True(session.DirectSession.OnReceiversNetwork);

        air.LoseLink(); // the iPhone fell off the PC's network
        await Wait.UntilAsync(() => session.DirectSession!.Id != firstSession && air.Joined == 2, timeout: TimeSpan.FromSeconds(20),
            because: "the iPhone noticed and set the link up again");
        await Wait.ForFinishAsync(session, TimeSpan.FromSeconds(90));

        Assert.Equal(TransferState.Completed, session.State);
        Assert.Equal(TransferPath.Direct, session.Path);
        Assert.True(session.DirectSession!.OnReceiversNetwork);
        Assert.Equal(1, pcLink.HostCalls); // the network stayed on in between
        Assert.Equal(0, phoneLink.HostCalls);
        Assert.Equal(TestFiles.Hash(file), TestFiles.Hash(Path.Combine(pc.ReceiveFolder, "video.mp4")));
        Assert.True(slow.BytesRead < size + 4L * 1024 * 1024, $"read {slow.BytesRead} bytes of {size}: the transfer started over");
        Assert.Single(pc.Node.Transfers.Sessions, s => s.Direction == TransferDirection.Receive);
    }

    [Fact]
    public async Task ASecondDirectSendFromTheIPhoneSaysOneAtATimeAndTheFirstGoesOn()
    {
        var air = new FakeDirectAir();
        var slow = new SlowFiles();
        var (phone, pc1, _, pc1Link) = IPhoneAndPc(air, files: slow);
        await using var _a = phone;
        await using var _b = pc1;
        var pc2Link = Fake(air.Link(Pc, sharesInternet: true));
        await using var pc2 = new TestNode("Desk", directLink: pc2Link, kind: DeviceKinds.Desktop);
        air.Connect(phone.Node, () => phone.AsDevice(), pc2.Node, () => pc2.AsDevice());
        pc2.Trust(phone);
        var big = phone.CreateFile("video.mp4", 20L * 1024 * 1024, 8);

        var toPc1 = phone.Node.Send(pc1.AsDevice(), new[] { slow.Add(big) }, ConnectionMethod.Direct);
        await Wait.UntilAsync(() => toPc1.Path == TransferPath.Direct && toPc1.GetSnapshot().TransferredBytes > 2_000_000, because: "sending to the first PC");

        var toPc2 = phone.Node.Send(pc2.AsDevice(), new[] { phone.CreateFile("b.jpg", 200_000, 9) }, ConnectionMethod.Direct);
        await Wait.ForFinishAsync(toPc2);
        Assert.Equal(TransferState.Failed, toPc2.State);
        Assert.Contains("one device at a time", toPc2.Error!.Message);
        Assert.Equal(0, pc2Link.HostCalls);

        await Wait.ForFinishAsync(toPc1, TimeSpan.FromSeconds(90));
        Assert.True(toPc1.State == TransferState.Completed, toPc1.Error?.Message);
        Assert.True(toPc1.DirectSession!.OnReceiversNetwork);
        Assert.Equal(1, pc1Link.HostCalls);
    }

    [Fact]
    public async Task ThePcStartsItsNetworkOnlyForASessionItAcceptedAndOnlyForThatSender()
    {
        // Nobody gets the PC's network without a proposal it accepted, from that same certificate, over TLS.
        var air = new FakeDirectAir();
        var (phone, pc, _, pcLink) = IPhoneAndPc(air);
        await using var _a = phone;
        await using var _b = pc;
        await using var stranger = new TestNode("Stranger", kind: DeviceKinds.Phone);
        var sessionId = DirectSession.NewToken();
        DirectRequestMessage Ask(string id) => new()
        {
            Action = DirectActions.HostForSender,
            SessionId = id,
            Nonce = DirectSession.NewToken(),
            TtlSeconds = 60,
        };

        var unknown = await AskAsync(phone, pc, Ask(DirectSession.NewToken()));
        Assert.False(unknown.Ok); // no such session
        Assert.True((await AskAsync(phone, pc, new DirectRequestMessage
        {
            Action = DirectActions.Propose,
            SessionId = sessionId,
            ReceiverId = pc.Node.Identity.DeviceId,
            SenderId = phone.Node.Identity.DeviceId,
        })).Ok);
        var other = await AskAsync(stranger, pc, Ask(sessionId));
        Assert.False(other.Ok); // that session is the phone's
        Assert.Null(other.Passphrase);
        Assert.Equal(0, pcLink.HostCalls);

        var mine = await AskAsync(phone, pc, Ask(sessionId));
        Assert.True(mine.Ok, mine.Message);
        Assert.Equal(pcLink.Hosting!.Passphrase, mine.Passphrase);
        Assert.True(mine.SharesInternet); // the PC is online
        Assert.Equal(1, pcLink.HostCalls);
        var again = await AskAsync(phone, pc, Ask(sessionId));
        Assert.False(again.Ok); // once per session
    }

    private static async Task<DirectResponseMessage> AskAsync(TestNode from, TestNode to, DirectRequestMessage request)
    {
        await using var connection = await SecureTransport.ConnectAsync(from.Node.Transfers.Identity, from.Node.Transfers.CreateHello(ConnectionPurpose.Direct),
            new[] { to.AsDevice().Endpoints[0] }, null, "peer", CancellationToken.None);
        await connection.Channel.SendAsync(FrameType.DirectRequest, request, CancellationToken.None);
        return await connection.Channel.ReadMessageAsync<DirectResponseMessage>(FrameType.DirectResponse, TimeSpan.FromSeconds(90), CancellationToken.None);
    }
}
