using Beam.Core;
using Beam.Core.Direct;
using Beam.Core.Discovery;
using Beam.Core.Protocol;
using Beam.Core.Settings;
using Beam.Core.Transfer;

namespace Beam.Core.Tests;

/// <summary>
/// A phone that sends to a PC whose network shares its internet (Windows' Mobile hotspot) joins the PC's network
/// instead of the PC joining the phone's: the phone stays on a network with internet, and the PC is the host rather
/// than a guest (an Intel adapter on a phone's network dropped every 166 s). Stand-ins for the radios, real Beam logic.
/// </summary>
public class DirectReceiverHostTests
{
    private const DirectRoles Pc = DirectRoles.Host | DirectRoles.JoinComputers;
    private const DirectRoles Phone = DirectRoles.Host | DirectRoles.Join;

    private static FakeDirectAir.FakeLink Fake(IDirectLink link) => (FakeDirectAir.FakeLink)link;

    private static (TestNode Phone, TestNode Pc, FakeDirectAir.FakeLink PhoneLink, FakeDirectAir.FakeLink PcLink) PhoneAndPc(
        FakeDirectAir air, bool pcOnline = true, SlowFiles? files = null, string pcName = "Laptop")
    {
        var phoneLink = Fake(air.Link(Phone));
        var pcLink = Fake(air.Link(Pc, sharesInternet: pcOnline));
        var phone = new TestNode("Redmi", externalFiles: files, directLink: phoneLink, kind: DeviceKinds.Phone);
        var pc = new TestNode(pcName, directLink: pcLink, kind: DeviceKinds.Laptop);
        pc.Node.Direct.ReceiverHostGrace = TimeSpan.FromMilliseconds(500);
        air.Connect(phone.Node, () => phone.AsDevice(), pc.Node, () => pc.AsDevice());
        pc.Trust(phone);
        return (phone, pc, phoneLink, pcLink);
    }

    [Fact]
    public async Task APhoneSendingToAnOnlinePcJoinsThePcsHotspotAndLeavesItWhenDone()
    {
        var air = new FakeDirectAir();
        var (phone, pc, phoneLink, pcLink) = PhoneAndPc(air);
        await using var _a = phone;
        await using var _b = pc;
        Assert.True(pc.AsDevice().DirectRoles.HasFlag(DirectRoles.HostsForSenders));

        var file = phone.CreateFile("video.mp4", 3_000_000, 1);
        var session = phone.Node.Send(pc.AsDevice(), new[] { file }, ConnectionMethod.Direct);
        await Wait.ForFinishAsync(session);

        Assert.True(session.State == TransferState.Completed, session.Error?.Message);
        Assert.Equal(TransferPath.Direct, session.Path);
        Assert.True(session.DirectSession!.OnReceiversNetwork);
        Assert.Equal(0, phoneLink.HostCalls); // the phone started nothing
        Assert.Equal(1, pcLink.HostCalls);
        Assert.True(pcLink.HostedNetworks.Single().SharesInternet);
        Assert.Same(phoneLink, air.LastJoinLink);
        Assert.Equal(TestFiles.Hash(file), TestFiles.Hash(Path.Combine(pc.ReceiveFolder, "video.mp4")));
        await Wait.UntilAsync(() => pcLink.Hosting == null && phoneLink.JoinedTo == null, because: "the hotspot went off and the phone left it");
        await Wait.UntilAsync(() => pc.Node.Direct.IsIdleForTests && phone.Node.Direct.IsIdleForTests, because: "nothing is held any more");
    }

    [Theory]
    [InlineData(false, DeviceKinds.Laptop)] // the PC has no internet to share: the phone's network, as before
    [InlineData(true, DeviceKinds.Phone)] // phone to phone: as before
    public async Task OtherwiseTheSenderStartsItsOwnNetworkAsBefore(bool pcOnline, string receiverKind)
    {
        var air = new FakeDirectAir();
        var phoneLink = Fake(air.Link(Phone));
        var receiverLink = Fake(air.Link(receiverKind == DeviceKinds.Phone ? Phone : Pc, sharesInternet: pcOnline));
        await using var phone = new TestNode("Redmi", directLink: phoneLink, kind: DeviceKinds.Phone);
        await using var receiver = new TestNode("Other", directLink: receiverLink, kind: receiverKind);
        air.Connect(phone.Node, () => phone.AsDevice(), receiver.Node, () => receiver.AsDevice());
        receiver.Trust(phone);

        var session = phone.Node.Send(receiver.AsDevice(), new[] { phone.CreateFile("a.jpg", 200_000) }, ConnectionMethod.Direct);
        await Wait.ForFinishAsync(session);

        Assert.True(session.State == TransferState.Completed, session.Error?.Message);
        Assert.False(session.DirectSession!.OnReceiversNetwork);
        Assert.Equal(1, phoneLink.HostCalls);
        Assert.Equal(0, receiverLink.HostCalls);
    }

    [Fact]
    public async Task APcSendingToAnotherOnlinePcStillHostsItself()
    {
        // Only phones ask: PC to PC stays as it was.
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
    public async Task WhenThePhoneCantJoinThePcsNetworkItStartsItsOwnInTheSameSendAndRemembersThat()
    {
        var air = new FakeDirectAir();
        var (phone, pc, phoneLink, pcLink) = PhoneAndPc(air);
        await using var _a = phone;
        await using var _b = pc;
        phoneLink.CantJoinSharedNetworks = true;

        var first = phone.Node.Send(pc.AsDevice(), new[] { phone.CreateFile("a.jpg", 200_000) }, ConnectionMethod.Direct);
        await Wait.ForFinishAsync(first);
        Assert.True(first.State == TransferState.Completed, first.Error?.Message); // no failure for the user to see
        Assert.False(first.DirectSession!.OnReceiversNetwork);
        Assert.Equal(1, pcLink.HostCalls);
        Assert.Equal(1, phoneLink.HostCalls);
        await Wait.UntilAsync(() => pcLink.Hosting == null, because: "the PC's hotspot went off once nobody used it");

        var second = phone.Node.Send(pc.AsDevice(), new[] { phone.CreateFile("b.jpg", 200_000) }, ConnectionMethod.Direct);
        await Wait.ForFinishAsync(second);
        Assert.True(second.State == TransferState.Completed, second.Error?.Message);
        Assert.Equal(1, pcLink.HostCalls); // not asked again for a while
        Assert.Equal(2, phoneLink.HostCalls);
    }

    [Fact]
    public async Task WhenThePcsHotspotWontStartThePhoneStartsItsOwnNetwork()
    {
        var air = new FakeDirectAir();
        var (phone, pc, phoneLink, pcLink) = PhoneAndPc(air);
        await using var _a = phone;
        await using var _b = pc;
        pcLink.WontShare = true; // the hotspot fails: the PC would fall back to Wi-Fi Direct, without internet

        var session = phone.Node.Send(pc.AsDevice(), new[] { phone.CreateFile("a.jpg", 200_000) }, ConnectionMethod.Direct);
        await Wait.ForFinishAsync(session);

        Assert.True(session.State == TransferState.Completed, session.Error?.Message);
        Assert.False(session.DirectSession!.OnReceiversNetwork);
        Assert.Equal(1, phoneLink.HostCalls);
        Assert.Same(pcLink, air.LastJoinLink); // the PC joined the phone's network
    }

    [Fact]
    public async Task ALostLinkIsSetUpAgainOnTheSameHotspotWithoutRestartingItAndResumes()
    {
        var air = new FakeDirectAir();
        var slow = new SlowFiles();
        var (phone, pc, phoneLink, pcLink) = PhoneAndPc(air, files: slow);
        await using var _a = phone;
        await using var _b = pc;
        pc.Node.Direct.ReceiverHostGrace = TimeSpan.FromSeconds(30);
        const long size = 30L * 1024 * 1024;
        var file = phone.CreateFile("video.mp4", size, 7);

        var session = phone.Node.Send(pc.AsDevice(), new[] { slow.Add(file) }, ConnectionMethod.Direct);
        await Wait.UntilAsync(() => session.Path == TransferPath.Direct && session.GetSnapshot().TransferredBytes > 4_000_000, because: "sending directly");
        var firstSession = session.DirectSession!.Id;
        Assert.True(session.DirectSession.OnReceiversNetwork);

        air.LoseLink(); // the phone fell off the PC's hotspot
        await Wait.UntilAsync(() => session.DirectSession!.Id != firstSession && air.Joined == 2, timeout: TimeSpan.FromSeconds(20),
            because: "the phone noticed and set the link up again");
        await Wait.ForFinishAsync(session, TimeSpan.FromSeconds(90));

        Assert.Equal(TransferState.Completed, session.State);
        Assert.Equal(TransferPath.Direct, session.Path);
        Assert.True(session.DirectSession!.OnReceiversNetwork);
        Assert.Equal(1, pcLink.HostCalls); // the hotspot stayed on in between
        Assert.Equal(0, phoneLink.HostCalls);
        Assert.Equal(TestFiles.Hash(file), TestFiles.Hash(Path.Combine(pc.ReceiveFolder, "video.mp4")));
        Assert.True(slow.BytesRead < size + 4L * 1024 * 1024, $"read {slow.BytesRead} bytes of {size}: the transfer started over");
        Assert.Single(pc.Node.Transfers.Sessions, s => s.Direction == TransferDirection.Receive);
    }

    [Fact]
    public async Task ASecondPcWhileOnTheFirstPcsHotspotMovesBothToThePhonesNetwork()
    {
        // The phone can't be a guest on one PC's network and host another's at once: it leaves the first PC's network,
        // starts its own, and both transfers go on there.
        var air = new FakeDirectAir();
        var slow = new SlowFiles();
        var (phone, pc1, phoneLink, pc1Link) = PhoneAndPc(air, files: slow);
        await using var _a = phone;
        await using var _b = pc1;
        var pc2Link = Fake(air.Link(Pc, sharesInternet: true));
        await using var pc2 = new TestNode("Desk", directLink: pc2Link, kind: DeviceKinds.Desktop);
        air.Connect(phone.Node, () => phone.AsDevice(), pc2.Node, () => pc2.AsDevice());
        pc2.Trust(phone);
        const long size = 30L * 1024 * 1024;
        var big = phone.CreateFile("video.mp4", size, 8);

        var toPc1 = phone.Node.Send(pc1.AsDevice(), new[] { slow.Add(big) }, ConnectionMethod.Direct);
        await Wait.UntilAsync(() => toPc1.Path == TransferPath.Direct && toPc1.GetSnapshot().TransferredBytes > 4_000_000, because: "sending to the first PC");
        Assert.True(toPc1.DirectSession!.OnReceiversNetwork);

        // Big enough to still be running when the first PC's transfer is set up again (then both share the phone's network).
        var toPc2 = phone.Node.Send(pc2.AsDevice(), new[] { slow.Add(phone.CreateFile("other.mp4", 20L * 1024 * 1024, 9)) }, ConnectionMethod.Direct);
        await Wait.ForFinishAsync(toPc2, TimeSpan.FromSeconds(60));
        await Wait.ForFinishAsync(toPc1, TimeSpan.FromSeconds(90));

        Assert.True(toPc2.State == TransferState.Completed, toPc2.Error?.Message);
        Assert.True(toPc1.State == TransferState.Completed, toPc1.Error?.Message);
        Assert.False(toPc2.DirectSession!.OnReceiversNetwork);
        Assert.False(toPc1.DirectSession!.OnReceiversNetwork); // moved to the phone's network
        Assert.Equal(1, phoneLink.HostCalls);
        Assert.Equal(0, pc2Link.HostCalls);
        Assert.Equal(TestFiles.Hash(big), TestFiles.Hash(Path.Combine(pc1.ReceiveFolder, "video.mp4")));
        Assert.True(slow.BytesRead < size + 20L * 1024 * 1024 + 8L * 1024 * 1024, $"read {slow.BytesRead} bytes: a transfer started over");
        await Wait.UntilAsync(() => pc1Link.Hosting == null, because: "the first PC's hotspot went off once the phone left");
    }

    [Fact]
    public async Task ThePcStartsItsNetworkOnlyForASessionItAcceptedAndOnlyForThatSender()
    {
        // Nobody gets the PC's network without a proposal it accepted, from that same certificate, over TLS.
        var air = new FakeDirectAir();
        var (phone, pc, _, pcLink) = PhoneAndPc(air);
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
        Assert.True(mine.SharesInternet);
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
