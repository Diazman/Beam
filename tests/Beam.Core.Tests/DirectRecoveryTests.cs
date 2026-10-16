using System.Net;
using Beam.Core;
using Beam.Core.Direct;
using Beam.Core.Discovery;
using Beam.Core.Nearby;
using Beam.Core.Protocol;
using Beam.Core.Settings;
using Beam.Core.Transfer;

namespace Beam.Core.Tests;

/// <summary>
/// What happens when a verified direct link goes away mid-transfer, when the control channel is Bluetooth, and when
/// Wi-Fi is off. Stand-ins for the radios (<see cref="FakeDirectAir"/>, <see cref="FakeNearbyAir"/>), real Beam logic.
/// </summary>
public class DirectRecoveryTests
{
    private const DirectRoles Pc = DirectRoles.Host | DirectRoles.JoinComputers;
    private const DirectRoles Phone = DirectRoles.Host | DirectRoles.Join;

    private static FakeDirectAir.FakeLink Fake(IDirectLink link) => (FakeDirectAir.FakeLink)link;

    private static void AssertResumedOnce(TestNode receiver, string file, string name, SlowFiles slow, long size)
    {
        Assert.Equal(TestFiles.Hash(file), TestFiles.Hash(Path.Combine(receiver.ReceiveFolder, name)));
        // The receiver said where to go on (its plan's offsets): nothing was read twice beyond the chunks in flight.
        Assert.True(slow.BytesRead < size + 4L * 1024 * 1024, $"read {slow.BytesRead} bytes of {size}: the transfer started over");
        Assert.Single(receiver.Node.Transfers.Sessions, s => s.Direction == TransferDirection.Receive);
        Assert.Empty(Directory.GetFiles(receiver.ReceiveFolder, "*.beampart"));
        Assert.Single(Directory.GetFiles(receiver.ReceiveFolder));
    }

    [Fact]
    public async Task AReceiverThatLosesTheDirectNetworkTellsTheSenderWhichSetsItUpAgainWithoutWaitingForATimeout()
    {
        // The Android log: the phone lost the PC's network, and the PC noticed only a minute later (I/O timeout) and
        // then didn't recover. Here nobody interrupts the connection by hand: the receiver's notice is all there is.
        var air = new FakeDirectAir();
        var slow = new SlowFiles();
        var phoneLink = air.Link(Phone);
        await using var pc = new TestNode("Laptop", externalFiles: slow, directLink: air.Link(Pc), kind: DeviceKinds.Laptop);
        await using var phone = new TestNode("Redmi", directLink: phoneLink, kind: DeviceKinds.Phone);
        air.Connect(pc.Node, () => pc.AsDevice(), phone.Node, () => phone.AsDevice());
        phone.Trust(pc);
        const long size = 30L * 1024 * 1024;
        var file = pc.CreateFile("video.mp4", size, 3);
        var paths = new List<TransferPath>();

        var session = pc.Node.Send(phone.AsDevice(), new[] { slow.Add(file) }, ConnectionMethod.Direct);
        session.PathChanged += s => { lock (paths) paths.Add(s.Path); };
        await Wait.UntilAsync(() => session.Path == TransferPath.Direct && session.GetSnapshot().TransferredBytes > 4_000_000, because: "sending directly");
        var first = session.DirectSession!.Id;

        air.LoseLink(); // the phone's Wi-Fi dropped the PC's network
        await Wait.UntilAsync(() => air.Joined == 2 && session.DirectSession!.Id != first, timeout: TimeSpan.FromSeconds(20),
            because: "set up again as soon as the phone said so");
        await Wait.ForFinishAsync(session, TimeSpan.FromSeconds(90));

        Assert.Equal(TransferState.Completed, session.State);
        Assert.Equal(ConnectionMethod.Direct, session.Mode);
        Assert.Equal(TransferPath.Direct, session.Path);
        Assert.DoesNotContain(TransferPath.Network, paths); // never quietly over the router
        Assert.True(session.DirectSession!.Reconnects >= 1);
        AssertResumedOnce(phone, file, "video.mp4", slow, size);
    }

    [Fact]
    public async Task RecoveryKeepsTryingWhileTheReceiverCantBeReachedForAWhileAndResumesDirectly()
    {
        // Between networks (Wi-Fi switching back, the router slow to answer) the receiver isn't reachable for some
        // seconds: that is not a reason to give up the transfer.
        var air = new FakeDirectAir();
        var slow = new SlowFiles();
        await using var pc = new TestNode("Laptop", externalFiles: slow, directLink: air.Link(Pc), kind: DeviceKinds.Laptop);
        await using var phone = new TestNode("Redmi", directLink: air.Link(Phone), kind: DeviceKinds.Phone);
        air.Wire(pc.Node, phone.Node);
        phone.Trust(pc);
        pc.Node.Direct.RecoveryRetryDelay = TimeSpan.FromMilliseconds(300);
        using var router = new FlakyProxy(new IPEndPoint(IPAddress.Loopback, phone.Node.Transfers.Port));
        var viaRouter = phone.AsDevice(router.Port);
        pc.Node.Discovery.ReportReachable(viaRouter);
        phone.Node.Discovery.ReportReachable(pc.AsDevice());
        const long size = 30L * 1024 * 1024;
        var file = pc.CreateFile("video.mp4", size, 4);

        var session = pc.Node.Send(viaRouter, new[] { slow.Add(file) }, ConnectionMethod.Direct);
        await Wait.UntilAsync(() => session.Path == TransferPath.Direct && session.GetSnapshot().TransferredBytes > 4_000_000, because: "sending directly");

        router.Unreachable = true;
        router.CutAll();
        air.LoseLink();
        Assert.True(session.InterruptConnection(switchingPath: false));
        await Wait.UntilAsync(() => router.Connections > 1 || session.IsFinished, because: "tried to reach it again");
        await Task.Delay(2000); // several attempts fail meanwhile
        Assert.False(session.IsFinished, session.Error?.Message);
        router.Unreachable = false;

        await Wait.ForFinishAsync(session, TimeSpan.FromSeconds(90));
        Assert.Equal(TransferState.Completed, session.State);
        Assert.Equal(TransferPath.Direct, session.Path);
        Assert.Equal(2, air.Joined);
        AssertResumedOnce(phone, file, "video.mp4", slow, size);
    }

    [Fact]
    public async Task ALinkThatKeepsDroppingWhileDataStillMovesIsSetUpAgainEachTime()
    {
        // The Android log: the phone's network dropped every two minutes. Each link moved data, so each drop gets a new
        // link (the fixed limit of two reconnects per transfer failed the third).
        var air = new FakeDirectAir();
        var slow = new SlowFiles();
        await using var pc = new TestNode("Laptop", externalFiles: slow, directLink: air.Link(Pc), kind: DeviceKinds.Laptop);
        await using var phone = new TestNode("Redmi", directLink: air.Link(Phone), kind: DeviceKinds.Phone);
        air.Connect(pc.Node, () => pc.AsDevice(), phone.Node, () => phone.AsDevice());
        phone.Trust(pc);
        const long size = 40L * 1024 * 1024;
        var file = pc.CreateFile("video.mp4", size, 5);

        var session = pc.Node.Send(phone.AsDevice(), new[] { slow.Add(file) }, ConnectionMethod.Direct);
        for (var drop = 1; drop <= 4; drop++)
        {
            var at = session.GetSnapshot().TransferredBytes;
            await Wait.UntilAsync(() => session.Path == TransferPath.Direct && session.State == TransferState.Transferring
                                        && session.GetSnapshot().TransferredBytes > at + 2_000_000 && air.LinkUp,
                because: $"moving data before drop {drop}", timeout: TimeSpan.FromSeconds(60));
            air.LoseLink();
            Assert.True(session.InterruptConnection(switchingPath: false));
        }

        await Wait.ForFinishAsync(session, TimeSpan.FromSeconds(120));
        Assert.Equal(TransferState.Completed, session.State);
        Assert.True(session.DirectSession!.Reconnects >= 4, $"reconnects: {session.DirectSession.Reconnects}");
        Assert.Equal(TestFiles.Hash(file), TestFiles.Hash(Path.Combine(phone.ReceiveFolder, "video.mp4")));
        Assert.Single(phone.Node.Transfers.Sessions, s => s.Direction == TransferDirection.Receive);
    }

    [Fact]
    public async Task CancellingWhileTheLinkIsBeingSetUpAgainStopsAtOnceAndLetsTheNetworkGo()
    {
        var air = new FakeDirectAir();
        var slow = new SlowFiles();
        var pcLink = Fake(air.Link(Pc));
        await using var pc = new TestNode("Laptop", externalFiles: slow, directLink: pcLink, kind: DeviceKinds.Laptop);
        await using var phone = new TestNode("Redmi", directLink: air.Link(Phone), kind: DeviceKinds.Phone);
        air.Wire(pc.Node, phone.Node);
        phone.Trust(pc);
        pc.Node.Direct.RecoveryRetryDelay = TimeSpan.FromMilliseconds(300);
        using var router = new FlakyProxy(new IPEndPoint(IPAddress.Loopback, phone.Node.Transfers.Port));
        var viaRouter = phone.AsDevice(router.Port);
        pc.Node.Discovery.ReportReachable(viaRouter);
        phone.Node.Discovery.ReportReachable(pc.AsDevice());
        var file = pc.CreateFile("video.mp4", 30L * 1024 * 1024, 6);

        var session = pc.Node.Send(viaRouter, new[] { slow.Add(file) }, ConnectionMethod.Direct);
        await Wait.UntilAsync(() => session.Path == TransferPath.Direct && session.GetSnapshot().TransferredBytes > 2_000_000, because: "sending directly");
        router.Unreachable = true;
        router.CutAll();
        air.LoseLink();
        session.InterruptConnection(switchingPath: false);
        await Wait.UntilAsync(() => router.Connections > 1, because: "recovering");

        session.Cancel();
        await Wait.ForFinishAsync(session, TimeSpan.FromSeconds(10)); // not after the recovery window
        Assert.Equal(TransferState.Cancelled, session.State);
        await Wait.UntilAsync(() => pcLink.Hosting == null, because: "the network is let go once nothing uses it");
        Assert.Equal(1, air.Joined);
    }

    [Fact]
    public async Task TryAgainAfterADirectFailureKeepsTheNewSessionAndFinishesOnIt()
    {
        // From a real log: after "Try again" the transfer still read "failed" until data flowed, so the moment it
        // reconnected the sender ended the brand-new session and turned its network off again.
        var air = new FakeDirectAir();
        var slow = new SlowFiles();
        var pcLink = Fake(air.Link(Pc));
        await using var pc = new TestNode("Laptop", externalFiles: slow, directLink: pcLink, kind: DeviceKinds.Laptop);
        await using var phone = new TestNode("Redmi", directLink: air.Link(Phone), kind: DeviceKinds.Phone);
        air.Connect(pc.Node, () => pc.AsDevice(), phone.Node, () => phone.AsDevice());
        phone.Trust(pc);
        const long size = 30L * 1024 * 1024;
        var file = pc.CreateFile("video.mp4", size, 7);
        var session = pc.Node.Send(phone.AsDevice(), new[] { slow.Add(file) }, ConnectionMethod.Direct);
        await Wait.UntilAsync(() => session.Path == TransferPath.Direct && session.GetSnapshot().TransferredBytes > 4_000_000, because: "sending directly");

        air.FailJoins = true; // gone for now
        air.LoseLink();
        session.InterruptConnection(switchingPath: false);
        await Wait.ForFinishAsync(session, TimeSpan.FromSeconds(60));
        Assert.Equal(TransferState.Failed, session.State);
        Assert.True(session.CanResume);
        // As in the log: the last connection had died on its own (not dropped by Beam), which leaves no path behind.
        session.SetPath(TransferPath.Unknown);
        await Wait.UntilAsync(() => pcLink.Hosting == null, because: "the failed session let the network go");
        var joined = air.Joined;
        var hosted = pcLink.HostCalls;

        air.FailJoins = false;
        session.Resume();
        Assert.False(session.IsFinished); // running again, not "failed" until data flows
        await Wait.ForFinishAsync(session, TimeSpan.FromSeconds(90));
        Assert.Equal(TransferState.Completed, session.State);
        await Wait.UntilAsync(() => session.DirectSession!.IsFinished, because: "the session followed the transfer");
        Assert.Equal(DirectSessionState.Completed, session.DirectSession!.State); // not ended as "failed" when it connected
        Assert.Equal(joined + 1, air.Joined); // one new session, kept until the end
        Assert.Equal(hosted + 1, pcLink.HostCalls); // started once, not turned off and started again in between
        Assert.Equal(TestFiles.Hash(file), TestFiles.Hash(Path.Combine(phone.ReceiveFolder, "video.mp4")));
    }

    // ---- Which network the sender starts ----

    private static async Task<(TestNode Pc, TestNode Receiver, FakeDirectAir.FakeLink PcLink, FakeDirectAir.FakeLink ReceiverLink)> PcAndAsync(
        FakeDirectAir air, bool pcOnline, DirectRoles receiverRoles, string receiverKind)
    {
        var pcLink = Fake(air.Link(Pc, sharesInternet: pcOnline));
        var receiverLink = Fake(air.Link(receiverRoles));
        var pc = new TestNode("Laptop", directLink: pcLink, kind: DeviceKinds.Laptop);
        var receiver = new TestNode("Other", directLink: receiverLink, kind: receiverKind);
        air.Connect(pc.Node, () => pc.AsDevice(), receiver.Node, () => receiver.AsDevice());
        receiver.Trust(pc);
        await Task.CompletedTask;
        return (pc, receiver, pcLink, receiverLink);
    }

    [Theory]
    [InlineData(true, true, true)] // a phone, the PC online: its Mobile hotspot (the phone stays on a network with internet)
    [InlineData(false, true, false)] // no internet to share: the usual Wi-Fi Direct network
    [InlineData(true, false, false)] // another PC: the usual network
    public async Task APcSharesItsInternetOnlyWithPhonesAndOnlyWhenItIsOnline(bool pcOnline, bool toPhone, bool sharing)
    {
        var air = new FakeDirectAir();
        var (pc, receiver, pcLink, _) = await PcAndAsync(air, pcOnline, toPhone ? Phone : Pc, toPhone ? DeviceKinds.Phone : DeviceKinds.Laptop);
        await using var _p = pc;
        await using var _r = receiver;

        var session = pc.Node.Send(receiver.AsDevice(), new[] { pc.CreateFile("a.jpg", 200_000) }, ConnectionMethod.Direct);
        await Wait.ForFinishAsync(session);
        Assert.True(session.State == TransferState.Completed, session.Error?.Message);
        Assert.Equal(TransferPath.Direct, session.Path);
        Assert.Equal(sharing, pcLink.HostedNetworks.Last().SharesInternet);
    }

    [Fact]
    public async Task APhoneThatCantJoinTheHotspotGetsTheUsualNetworkOnTryAgain()
    {
        var air = new FakeDirectAir();
        var (pc, phone, pcLink, phoneLink) = await PcAndAsync(air, pcOnline: true, Phone, DeviceKinds.Phone);
        await using var _p = pc;
        await using var _r = phone;
        phoneLink.CantJoinSharedNetworks = true;

        var session = pc.Node.Send(phone.AsDevice(), new[] { pc.CreateFile("a.jpg", 200_000) }, ConnectionMethod.Direct);
        await Wait.ForFinishAsync(session);
        Assert.Equal(TransferState.Failed, session.State);
        Assert.Contains("receiver join", session.Error!.Message);
        Assert.True(pcLink.HostedNetworks.Last().SharesInternet);
        await Wait.UntilAsync(() => pcLink.Hosting == null, because: "the hotspot went off");

        session.Resume();
        await Wait.ForFinishAsync(session);
        Assert.True(session.State == TransferState.Completed, session.Error?.Message);
        Assert.False(pcLink.HostedNetworks.Last().SharesInternet);
    }

    // ---- Bluetooth as the control channel ----

    private static async Task<(TestNode Pc, TestNode Phone, FakeNearbyAir Bluetooth, DeviceInfo Resolved)> OverBluetoothAsync(
        FakeDirectAir air, string pcName = "Laptop", string phoneName = "Redmi", SlowFiles? files = null)
    {
        var bluetooth = new FakeNearbyAir();
        var pc = new TestNode(pcName, externalFiles: files, directLink: air.Link(Pc), kind: DeviceKinds.Laptop, nearbyRadio: bluetooth.Radio());
        var phone = new TestNode(phoneName, directLink: air.Link(Phone), kind: DeviceKinds.Phone, nearbyRadio: bluetooth.Radio());
        air.ConnectDirectOnly(pc.Node, phone.Node); // no shared network: Bluetooth only
        phone.Node.Nearby.SetReceiving(true);
        pc.Node.Nearby.SetLooking(true);
        DeviceInfo? listed = null;
        await Wait.UntilAsync(() => (listed = pc.Node.Discovery.Devices.FirstOrDefault(NearbyService.IsPlaceholder)) != null, because: "phone heard");
        var device = await pc.Node.Nearby.ResolveAsync(listed!, CancellationToken.None);
        return (pc, phone, bluetooth, device);
    }

    [Fact]
    public async Task FindingOutWhoItIsAndSendingRightAfterUseOneBluetoothConnection()
    {
        // The Android log: the second connection, moments after the first, timed out on the phone.
        var air = new FakeDirectAir();
        var (pc, phone, bluetooth, device) = await OverBluetoothAsync(air);
        await using var _p = pc;
        await using var _q = phone;
        Assert.Equal(1, bluetooth.Connections);

        var session = pc.Node.Send(device, new[] { pc.CreateFile("a.jpg", 200_000) }, ConnectionMethod.Direct);
        await Wait.ForFinishAsync(session);
        Assert.Equal(TransferState.Completed, session.State);
        Assert.Equal(DirectControlChannel.Bluetooth, session.DirectSession!.Channel);
        Assert.Equal(1, bluetooth.Connections);
    }

    [Fact]
    public async Task ASendStartedWhenBluetoothScanningHadStoppedScansAgainInsteadOfFailingAtOnce()
    {
        // The Android log: "isn't reachable" within a millisecond, because nothing was scanning any more.
        var air = new FakeDirectAir();
        var (pc, phone, bluetooth, device) = await OverBluetoothAsync(air);
        await using var _p = pc;
        await using var _q = phone;
        pc.Node.Nearby.SetLooking(false); // the Send page was left
        await pc.Node.Nearby.CloseOpenHandshakesForTests(); // the connection kept open after finding it out timed out
        pc.Node.Nearby.AgeSightingsForTests(TimeSpan.FromMinutes(1)); // and the beacon was last heard long ago
        Assert.False(pc.Node.Nearby.IsNearby(device.Id));
        var starts = bluetooth.ScanStarts;

        var session = pc.Node.Send(device, new[] { pc.CreateFile("a.jpg", 200_000) }, ConnectionMethod.Direct);
        await Wait.ForFinishAsync(session, TimeSpan.FromSeconds(60));

        Assert.True(session.State == TransferState.Completed, session.Error?.Message);
        Assert.Equal(DirectControlChannel.Bluetooth, session.DirectSession!.Channel);
        Assert.True(bluetooth.ScanStarts > starts, "scanned for the receiver");
    }

    [Fact]
    public async Task AReceiverWhoseUserTakesAWhileToAcceptIsWaitedFor()
    {
        // From a real log: the sender gave up ("didn't answer over Bluetooth. Move closer") at the same moment the
        // phone was still showing its Accept prompt.
        var air = new FakeDirectAir();
        var (pc, phone, _, device) = await OverBluetoothAsync(air);
        await using var _p = pc;
        await using var _q = phone;
        pc.Node.Nearby.HandshakeTimeout = TimeSpan.FromSeconds(1.5); // shorter than the user takes
        phone.Handler.ApproveDirectAfter = TimeSpan.FromSeconds(3);

        var session = pc.Node.Send(device, new[] { pc.CreateFile("a.jpg", 100_000) }, ConnectionMethod.Direct);
        await Wait.ForFinishAsync(session, TimeSpan.FromSeconds(60));
        Assert.True(session.State == TransferState.Completed, session.Error?.Message);
    }

    [Fact]
    public async Task WhenNobodyAcceptsOnTheReceiverTheSenderSaysSoInsteadOfBlamingBluetooth()
    {
        var air = new FakeDirectAir();
        var (pc, phone, _, device) = await OverBluetoothAsync(air);
        await using var _p = pc;
        await using var _q = phone;
        pc.Node.Nearby.ProposalWait = TimeSpan.FromSeconds(2);
        phone.Handler.ApproveDirectAfter = TimeSpan.FromSeconds(20); // nobody taps

        var session = pc.Node.Send(device, new[] { pc.CreateFile("a.jpg", 100_000) }, ConnectionMethod.Direct);
        await Wait.ForFinishAsync(session, TimeSpan.FromSeconds(30));
        Assert.Equal(TransferState.Failed, session.State);
        Assert.Contains("didn't accept the direct connection in time", session.Error!.Message);
        Assert.DoesNotContain("Bluetooth", session.Error.Message);
    }

    [Theory]
    [InlineData("Ж")] // 2 bytes in UTF-8, 6 as a JSON escape
    [InlineData("電")] // 3 bytes
    [InlineData("😀")] // 4 bytes (two UTF-16 chars)
    public async Task EveryBluetoothMessageFitsOneGattValueEvenWithLongNames(string letter)
    {
        // The Android log: the offer was 548 bytes, over the 512 a GATT attribute holds. Names of 40 characters, a long
        // network name, and the receiver's longest answer: all must fit (the fake radio fails anything longer).
        var air = new FakeDirectAir();
        var longName = string.Concat(Enumerable.Repeat(letter, 40 / letter.Length)); // the longest name allowed
        var (pc, phone, bluetooth, device) = await OverBluetoothAsync(air, pcName: longName, phoneName: longName);
        await using var _p = pc;
        await using var _q = phone;

        var session = pc.Node.Send(device, new[] { pc.CreateFile("a.jpg", 100_000) }, ConnectionMethod.Direct);
        await Wait.ForFinishAsync(session);
        Assert.True(session.State == TransferState.Completed, session.Error?.Message);
        Assert.InRange(bluetooth.Longest, 1, NearbyHandshake.MaxValueLength);
    }

    // ---- Setting a link up again over Bluetooth without asking again ----

    [Fact]
    public async Task OverBluetoothALostLinkIsSetUpAgainWithoutTheReceiversUserAcceptingAgain()
    {
        // From a real log: every reconnect over Bluetooth showed the phone's Accept prompt again; nobody tapped it within
        // a minute (the phone was lying there), so the transfer failed.
        var air = new FakeDirectAir();
        var slow = new SlowFiles();
        var (pc, phone, _, device) = await OverBluetoothAsync(air, files: slow);
        await using var _p = pc;
        await using var _q = phone;
        var asks = 0;
        phone.Handler.ApproveDirect = _ => Interlocked.Increment(ref asks) == 1; // yes the first time only
        const long size = 30L * 1024 * 1024;
        var file = pc.CreateFile("video.mp4", size, 9);

        var session = pc.Node.Send(device, new[] { slow.Add(file) }, ConnectionMethod.Direct);
        for (var drop = 1; drop <= 2; drop++)
        {
            var at = session.GetSnapshot().TransferredBytes;
            await Wait.UntilAsync(() => session.Path == TransferPath.Direct && session.State == TransferState.Transferring
                                        && session.GetSnapshot().TransferredBytes > at + 3_000_000 && air.LinkUp,
                because: $"sending directly before drop {drop}", timeout: TimeSpan.FromSeconds(60));
            air.LoseLink();
            session.InterruptConnection(switchingPath: false);
        }

        await Wait.ForFinishAsync(session, TimeSpan.FromSeconds(120));
        Assert.True(session.State == TransferState.Completed, session.Error?.Message);
        Assert.Equal(DirectControlChannel.Bluetooth, session.DirectSession!.Channel);
        Assert.Equal(1, Volatile.Read(ref asks)); // asked once, for the first session only
        Assert.Equal(3, air.Joined);
        Assert.Equal(TestFiles.Hash(file), TestFiles.Hash(Path.Combine(phone.ReceiveFolder, "video.mp4")));
    }

    [Fact]
    public async Task OnlyAValidFreshReconnectProofSkipsTheQuestionAndTheOfferMustBeProvenToo()
    {
        var air = new FakeDirectAir();
        await using var pc = new TestNode("Laptop", directLink: air.Link(Pc), kind: DeviceKinds.Laptop);
        await using var phone = new TestNode("Redmi", directLink: air.Link(Phone), kind: DeviceKinds.Phone);
        air.Wire(pc.Node, phone.Node);
        var direct = phone.Node.Direct;
        var fingerprint = pc.Node.Identity.Fingerprint;
        direct.SetReceiving(true);
        phone.Handler.ApproveDirect = _ => false; // whenever it asks, the answer is no

        DirectRequestMessage Propose(string? proofKey, string? sessionId = null)
        {
            var m = new DirectRequestMessage
            {
                Action = DirectActions.Propose, SessionId = sessionId ?? DirectSession.NewToken(), ReceiverId = phone.Node.Identity.DeviceId,
                SenderId = pc.Node.Identity.DeviceId, SenderName = "Laptop", SenderFingerprint = fingerprint,
            };
            if (proofKey != null) m.Proof = ReconnectProof.ForPropose(proofKey, m);
            return m;
        }

        Task<DirectResponseMessage> Ask(DirectRequestMessage m) => direct.ProposeIncomingAsync(m, m.SenderId!, m.SenderName!, m.SenderFingerprint!,
            claimedOnly: true, DirectControlChannel.Bluetooth, Array.Empty<IPEndPoint>(), CancellationToken.None);

        static async Task<bool> Accepted(Task<DirectResponseMessage> answer)
        {
            try
            {
                return (await answer).Ok;
            }
            catch (DirectLinkException)
            {
                return false;
            }
        }

        var key = direct.IssueReconnectKeyForTests(fingerprint);
        // A proof made with another key, or one taken from another session: asked (and refused).
        Assert.False(await Accepted(Ask(Propose(ReconnectProof.NewKey()))));
        var replayed = Propose(key);
        replayed.SessionId = DirectSession.NewToken(); // the proof was for another session id
        Assert.False(await Accepted(Ask(replayed)));
        // For another sender's certificate: asked.
        var other = Propose(key);
        other.SenderFingerprint = new string('c', 64);
        other.Proof = ReconnectProof.ForPropose(key, other);
        direct.IssueReconnectKeyForTests(new string('d', 64));
        Assert.False(await Accepted(Ask(other)));
        Assert.Equal(3, phone.Handler.DirectRequests.Count);

        // An expired key: asked.
        direct.ReconnectKeyLifetime = TimeSpan.Zero;
        var expired = direct.IssueReconnectKeyForTests(fingerprint);
        Assert.False(await Accepted(Ask(Propose(expired))));
        Assert.Equal(4, phone.Handler.DirectRequests.Count);

        // The right, fresh key: accepted without asking, even with Receive closed.
        direct.ReconnectKeyLifetime = TimeSpan.FromMinutes(30);
        key = direct.IssueReconnectKeyForTests(fingerprint);
        direct.SetReceiving(false);
        var good = Propose(key);
        Assert.True(await Accepted(Ask(good)));
        Assert.Equal(4, phone.Handler.DirectRequests.Count);

        // Its offer must be proven with the same key, over exactly these network details.
        var offer = new DirectRequestMessage
        {
            Action = DirectActions.Offer, SessionId = good.SessionId, Nonce = DirectSession.NewToken(), Ssid = "DIRECT-EL-Beam-Laptop",
            Passphrase = "abcdefgh12345678", TtlSeconds = 120, HostIsComputer = true, Port = 47821,
        };
        Assert.False(direct.OfferIncoming(offer, fingerprint, out _).Ok); // no proof
        offer.Proof = ReconnectProof.ForOffer(key, offer);
        offer.Passphrase = "someone-elses-pass"; // changed on the way
        Assert.False(direct.OfferIncoming(offer, fingerprint, out _).Ok);
        offer.Passphrase = "abcdefgh12345678";
        Assert.True(direct.OfferIncoming(offer, fingerprint, out var join).Ok);
        Assert.NotNull(join);
    }

    // ---- Wi-Fi off ----

    [Fact]
    public async Task WithWiFiOffOnTheSenderNothingStartsAndTheMessageSaysSo()
    {
        var air = new FakeDirectAir();
        var pcLink = Fake(air.Link(Pc));
        pcLink.WifiOn = false;
        await using var pc = new TestNode("Laptop", directLink: pcLink, kind: DeviceKinds.Laptop);
        await using var phone = new TestNode("Redmi", directLink: air.Link(Phone), kind: DeviceKinds.Phone);
        air.Connect(pc.Node, () => pc.AsDevice(), phone.Node, () => phone.AsDevice());
        phone.Trust(pc);
        Assert.True(pc.Node.Direct.IsWifiOff);

        var session = pc.Node.Send(phone.AsDevice(), new[] { pc.CreateFile("a.jpg", 1000) }, ConnectionMethod.Direct);
        await Wait.ForFinishAsync(session);
        Assert.Equal(TransferState.Failed, session.State);
        Assert.Equal(TransferErrorKind.DirectFailed, session.Error!.Kind);
        Assert.Contains("Wi-Fi is off", session.Error.Message);
        Assert.Equal(0, pcLink.HostCalls);
        Assert.Empty(phone.Handler.DirectRequests); // the receiver wasn't bothered
        Assert.Equal(ConnectionMethod.Direct, session.Mode); // and nothing went over the router instead

        // Asking turns it on (the Wi-Fi panel on a phone): the next attempt works.
        pcLink.TurnsOnWhenAsked = true;
        Assert.True(await pc.Node.Direct.RequestWifiOnAsync(TimeSpan.FromSeconds(1)));
        var again = pc.Node.Send(phone.AsDevice(), new[] { pc.CreateFile("b.jpg", 1000) }, ConnectionMethod.Direct);
        await Wait.ForFinishAsync(again);
        Assert.Equal(TransferState.Completed, again.State);
    }

    [Fact]
    public async Task WithWiFiOffOnTheReceiverItIsAskedToTurnItOnAndOtherwiseTheFailureSaysWhere()
    {
        var air = new FakeDirectAir();
        var phoneLink = Fake(air.Link(Phone));
        phoneLink.WifiOn = false;
        await using var pc = new TestNode("Laptop", directLink: air.Link(Pc), kind: DeviceKinds.Laptop);
        await using var phone = new TestNode("Redmi", directLink: phoneLink, kind: DeviceKinds.Phone);
        air.Connect(pc.Node, () => pc.AsDevice(), phone.Node, () => phone.AsDevice());
        phone.Trust(pc);

        var refused = pc.Node.Send(phone.AsDevice(), new[] { pc.CreateFile("a.jpg", 1000) }, ConnectionMethod.Direct);
        await Wait.ForFinishAsync(refused);
        Assert.Equal(TransferState.Failed, refused.State);
        Assert.Contains("receiver join", refused.Error!.Message);
        Assert.Contains("Wi-Fi is off on Redmi", refused.Error.Message);
        Assert.Equal(1, phoneLink.WifiAsks);
        Assert.Equal(0, phoneLink.JoinCalls);

        phoneLink.TurnsOnWhenAsked = true; // this time the user turns it on from the panel
        var session = pc.Node.Send(phone.AsDevice(), new[] { pc.CreateFile("b.jpg", 1000) }, ConnectionMethod.Direct);
        await Wait.ForFinishAsync(session);
        Assert.Equal(TransferState.Completed, session.State);
        Assert.Equal(TransferPath.Direct, session.Path);
    }

    [Fact]
    public async Task SameNetworkIgnoresTheWiFiSwitchAndTheRadio()
    {
        // Regression: none of the Direct checks apply to Same Network (a PC on a cable has no Wi-Fi on at all).
        var air = new FakeDirectAir();
        var pcLink = Fake(air.Link(Pc));
        pcLink.WifiOn = false;
        await using var pc = new TestNode("Laptop", directLink: pcLink, kind: DeviceKinds.Laptop);
        await using var phone = new TestNode("Redmi", directLink: air.Link(Phone), kind: DeviceKinds.Phone);
        air.Connect(pc.Node, () => pc.AsDevice(), phone.Node, () => phone.AsDevice());
        phone.Trust(pc);
        var file = pc.CreateFile("a.jpg", 500_000);

        var session = pc.Node.Send(phone.AsDevice(), new[] { file }, ConnectionMethod.SameNetwork);
        await Wait.ForFinishAsync(session);
        Assert.Equal(TransferState.Completed, session.State);
        Assert.Equal(TransferPath.Network, session.Path);
        Assert.Equal(0, pcLink.HostCalls);
        Assert.Equal(0, pcLink.WifiAsks);
        Assert.Equal(0, air.Joined);
        Assert.Null(session.DirectSession);
        Assert.Equal(TestFiles.Hash(file), TestFiles.Hash(Path.Combine(phone.ReceiveFolder, "a.jpg")));
    }
}
