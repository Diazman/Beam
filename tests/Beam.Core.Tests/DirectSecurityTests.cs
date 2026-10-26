using System.Net;
using Beam.Core.Direct;
using Beam.Core.Discovery;
using Beam.Core.Protocol;
using Beam.Core.Settings;
using Beam.Core.Transfer;

namespace Beam.Core.Tests;

/// <summary>
/// What may make this device start its network or join another: only a direct session the user chose (or accepted
/// files moving to). Older Beams' receiver-hosted requests are refused, and Same Wi-Fi never starts anything.
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

    [Theory]
    [InlineData("host")]
    [InlineData("join")]
    [InlineData("prefer")]
    [InlineData("stop")]
    public async Task AnOlderBeamsReceiverHostedRequestIsRefusedAndStartsNothing(string action)
    {
        // Beam 1.5.3 and older asked the receiver to start its network ("host") or to join the sender's ("join"). Even a
        // paired device, with Receive open, gets a refusal that says to update; nothing is started or joined.
        var air = new FakeDirectAir();
        var pcLink = air.Link(DirectRoles.Host | DirectRoles.JoinComputers);
        await using var pc = new TestNode("Laptop", directLink: pcLink, kind: DeviceKinds.Laptop);
        await using var phone = new TestNode("Galaxy", directLink: air.Link(DirectRoles.Host | DirectRoles.Join), kind: DeviceKinds.Phone);
        pc.Trust(phone);
        pc.Node.Nearby.SetReceiving(true);

        var reply = await AskAsync(phone, pc, new DirectRequestMessage { Action = action, Ssid = "DIRECT-xx-Old", Passphrase = "passphrase123" });

        Assert.False(reply.Ok);
        Assert.Contains("needs a newer Beam", reply.Message);
        Assert.Null(reply.Ssid);
        Assert.Null(reply.Passphrase);
        Assert.Equal(0, Fake(pcLink).HostCalls);
        Assert.Equal(0, Fake(pcLink).JoinCalls);
        Assert.Null(pc.Node.Direct.HostedNetwork);
    }

    [Fact]
    public async Task AcceptingATransferLetsItsSenderMoveItToADirectLink()
    {
        // Mid-transfer "Switch to direct" works without pairing or Receive: the user already accepted these files.
        // (The sender hosts now: the PC, the receiver, joins the phone's network.)
        var air = new FakeDirectAir();
        var slow = new SlowFiles();
        var pcLink = air.Link(DirectRoles.Host | DirectRoles.JoinComputers);
        var phoneLink = air.Link(DirectRoles.Host | DirectRoles.Join);
        await using var pc = new TestNode("Laptop", directLink: pcLink, kind: DeviceKinds.Laptop);
        await using var phone = new TestNode("Galaxy", externalFiles: slow, directLink: phoneLink, kind: DeviceKinds.Phone);
        air.Connect(phone.Node, () => phone.AsDevice(), pc.Node, () => pc.AsDevice());
        pc.Handler.ApproveDirect = _ => false; // its user would say no if asked: accepting the files is what counts

        // Before any transfer: refused (not paired, Receive closed), and nothing was started.
        var before = phone.Node.Send(pc.AsDevice(), new[] { phone.CreateFile("a.txt", 1000) }, ConnectionMethod.Direct);
        await Wait.ForFinishAsync(before);
        Assert.Equal(TransferState.Failed, before.State);
        Assert.Equal(0, Fake(phoneLink).HostCalls);
        Assert.Equal(0, Fake(pcLink).JoinCalls);

        var session = phone.Node.Send(pc.AsDevice(), new[] { slow.Add(phone.CreateFile("video.mp4", 20L * 1024 * 1024, 3)) });
        await Wait.UntilAsync(() => session.State == TransferState.Transferring, because: "accepted");
        Assert.True(await phone.Node.Direct.SwitchAsync(session, ConnectionMethod.Direct));
        Assert.Equal(1, Fake(phoneLink).HostCalls);
        Assert.Equal(1, Fake(pcLink).JoinCalls);
        await Wait.UntilAsync(() => session.Path == TransferPath.Direct, because: "moved to the direct link");
        await Wait.ForFinishAsync(session, TimeSpan.FromSeconds(60));
        Assert.Equal(TransferState.Completed, session.State);

        // Its files are in: the phone's network goes off and the PC leaves it.
        await Wait.UntilAsync(() => phone.Node.Direct.HostedNetwork == null && Fake(pcLink).JoinedTo == null, because: "the network went off after the transfer");
    }

    [Fact]
    public async Task WithSameWiFiChosenASenderNeverStartsOrJoinsANetwork()
    {
        // Everything that could tempt it: the receiver has Receive open, and the sender is paired with it.
        var air = new FakeDirectAir();
        var phoneLink = air.Link(DirectRoles.Host | DirectRoles.Join);
        await using var pc = new TestNode("Laptop", directLink: air.Link(DirectRoles.Host | DirectRoles.JoinComputers), kind: DeviceKinds.Laptop);
        await using var phone = new TestNode("Galaxy", directLink: phoneLink, kind: DeviceKinds.Phone);
        air.Connect(phone.Node, () => phone.AsDevice(), pc.Node, () => pc.AsDevice());
        pc.Node.Nearby.SetReceiving(true);
        phone.Node.Pair(PairingCode.TryParse(pc.Node.CreatePairingCode().AppendTo(null))!);
        Assert.NotNull(phone.Node.Direct.KnownNetworkFor(pc.Node.Identity.DeviceId));

        var paths = new List<TransferPath>();
        var session = phone.Node.Send(pc.AsDevice(), new[] { phone.CreateFile("photo.jpg", 400_000) });
        session.PathChanged += s => { lock (paths) paths.Add(s.Path); };
        await Wait.ForFinishAsync(session, TimeSpan.FromSeconds(30));

        Assert.Equal(TransferState.Completed, session.State);
        Assert.Equal(ConnectionMethod.SameNetwork, session.Mode);
        Assert.Null(session.DirectSession);
        Assert.All(paths.Where(p => p != TransferPath.Unknown), p => Assert.Equal(TransferPath.Network, p));
        Assert.Equal(0, Fake(phoneLink).HostCalls);
        Assert.Equal(0, Fake(phoneLink).JoinCalls);
        Assert.Equal(0, air.Hosted); // nobody started anything: not the sender, and not the receiver for having Receive open
    }

    // ---- A direct link lost mid-transfer ----

    [Fact]
    public async Task ALostDirectLinkIsRejoinedAndTheTransferResumesWithoutStartingOver()
    {
        var air = new FakeDirectAir();
        var slow = new SlowFiles();
        var pcLink = air.Link(DirectRoles.Host | DirectRoles.JoinComputers);
        await using var pc = new TestNode("Laptop", directLink: pcLink, kind: DeviceKinds.Laptop);
        await using var phone = new TestNode("Galaxy", externalFiles: slow, directLink: air.Link(DirectRoles.Host | DirectRoles.Join), kind: DeviceKinds.Phone);
        air.Connect(phone.Node, () => phone.AsDevice(), pc.Node, () => pc.AsDevice());
        pc.Trust(phone);
        const long size = 30L * 1024 * 1024;
        var file = phone.CreateFile("video.mp4", size, 8);

        var session = phone.Node.Send(pc.AsDevice(), new[] { slow.Add(file) }, ConnectionMethod.Direct);
        await Wait.UntilAsync(() => session.Path == TransferPath.Direct && session.GetSnapshot().TransferredBytes > 4_000_000, because: "sending directly");
        var first = session.DirectSession!.Id;

        // The PC falls off the phone's network, and the connection over it dies.
        air.LoseLink();
        Assert.True(session.InterruptConnection(switchingPath: false));

        await Wait.UntilAsync(() => air.Joined == 2 && session.Path == TransferPath.Direct && session.State == TransferState.Transferring,
            because: "set up again and resumed directly", timeout: TimeSpan.FromSeconds(30));
        Assert.NotEqual(first, session.DirectSession!.Id); // a new session id for the new setup
        Assert.Equal(1, session.DirectSession.Reconnects);
        await Wait.ForFinishAsync(session, TimeSpan.FromSeconds(90));
        Assert.Equal(TransferState.Completed, session.State);
        Assert.Equal(TestFiles.Hash(file), TestFiles.Hash(Path.Combine(pc.ReceiveFolder, "video.mp4")));
        Assert.True(slow.BytesRead < size + 3L * 1024 * 1024, $"read {slow.BytesRead} bytes of {size}: the transfer started over");
        Assert.Single(pc.Node.Transfers.Sessions, s => s.Direction == TransferDirection.Receive); // resumed, not a second transfer
        Assert.Empty(Directory.GetFiles(pc.ReceiveFolder, "*.beampart"));
        Assert.Single(Directory.GetFiles(pc.ReceiveFolder)); // no second copy
    }

    [Fact]
    public async Task WhenTheLostDirectLinkCantBeSetUpAgainTheTransferFailsAndNothingGoesOverWiFi()
    {
        // (Until this version it quietly finished over the shared Wi-Fi. Direct now means direct: the user decides.)
        var air = new FakeDirectAir();
        var slow = new SlowFiles();
        await using var pc = new TestNode("Laptop", directLink: air.Link(DirectRoles.Host | DirectRoles.JoinComputers), kind: DeviceKinds.Laptop);
        await using var phone = new TestNode("Galaxy", externalFiles: slow, directLink: air.Link(DirectRoles.Host | DirectRoles.Join), kind: DeviceKinds.Phone);
        air.Connect(phone.Node, () => phone.AsDevice(), pc.Node, () => pc.AsDevice());
        pc.Trust(phone);
        const long size = 30L * 1024 * 1024;
        var file = phone.CreateFile("video.mp4", size, 9);
        var paths = new List<TransferPath>();

        var session = phone.Node.Send(pc.AsDevice(), new[] { slow.Add(file) }, ConnectionMethod.Direct);
        session.PathChanged += s => { lock (paths) paths.Add(s.Path); };
        await Wait.UntilAsync(() => session.Path == TransferPath.Direct && session.GetSnapshot().TransferredBytes > 4_000_000, because: "sending directly");

        air.FailJoins = true; // out of range now
        air.LoseLink();
        Assert.True(session.InterruptConnection(switchingPath: false));

        await Wait.ForFinishAsync(session, TimeSpan.FromSeconds(60));
        Assert.Equal(TransferState.Failed, session.State);
        Assert.Equal(TransferErrorKind.DirectFailed, session.Error!.Kind);
        Assert.Contains("receiver join", session.Error.Message);
        Assert.True(session.CanResume);
        Assert.DoesNotContain(TransferPath.Network, paths);
        Assert.Equal(ConnectionMethod.Direct, session.Mode);

        // Only when the user asks: a separate transfer over the Wi-Fi network, shown as such; it finishes the file.
        var overWiFi = phone.Node.SendOverNetworkInstead(session);
        Assert.Equal(ConnectionMethod.SameNetwork, overWiFi.Mode);
        await Wait.ForFinishAsync(overWiFi, TimeSpan.FromSeconds(90));
        Assert.Equal(TransferState.Completed, overWiFi.State);
        Assert.Equal(TransferPath.Network, overWiFi.Path);
        Assert.Equal(TestFiles.Hash(file), TestFiles.Hash(Path.Combine(pc.ReceiveFolder, "video.mp4")));
    }
}
