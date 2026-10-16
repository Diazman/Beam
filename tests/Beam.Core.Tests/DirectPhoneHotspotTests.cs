using System.Net;
using Beam.Core;
using Beam.Core.Direct;
using Beam.Core.Discovery;
using Beam.Core.Settings;
using Beam.Core.Transfer;

namespace Beam.Core.Tests;

/// <summary>
/// A phone that sends to a computer hosts with its own hotspot (an ordinary access point) rather than a Wi-Fi Direct
/// group, when it can run one beside its Wi-Fi: a PC's Intel adapter dropped off the phone's group every 166 s. The phone
/// still hosts; the computer joins. Stand-ins for the radios, real Beam logic.
/// </summary>
public class DirectPhoneHotspotTests
{
    private const DirectRoles Pc = DirectRoles.Host | DirectRoles.JoinComputers;
    private const DirectRoles Phone = DirectRoles.Host | DirectRoles.Join;

    private static FakeDirectAir.FakeLink Fake(IDirectLink link) => (FakeDirectAir.FakeLink)link;

    [Theory]
    [InlineData(true, true, true)] // to a PC, the phone can: its hotspot
    [InlineData(false, true, false)] // the phone can't run one beside its Wi-Fi: its Wi-Fi Direct group, as before
    [InlineData(true, false, false)] // to another phone: the group (that phone stays on its own Wi-Fi beside it)
    public async Task APhoneSendingToAComputerHostsItsHotspotWhenItCan(bool canRunHotspot, bool toComputer, bool hotspot)
    {
        var air = new FakeDirectAir();
        var phoneLink = Fake(air.Link(Phone, canRunHotspot: canRunHotspot));
        var receiverLink = Fake(air.Link(toComputer ? Pc : Phone, sharesInternet: true));
        await using var phone = new TestNode("Redmi", directLink: phoneLink, kind: DeviceKinds.Phone);
        await using var receiver = new TestNode("Other", directLink: receiverLink, kind: toComputer ? DeviceKinds.Laptop : DeviceKinds.Phone);
        air.Connect(phone.Node, () => phone.AsDevice(), receiver.Node, () => receiver.AsDevice());
        receiver.Trust(phone);
        var file = phone.CreateFile("video.mp4", 2_000_000, 1);

        var session = phone.Node.Send(receiver.AsDevice(), new[] { file }, ConnectionMethod.Direct);
        await Wait.ForFinishAsync(session);

        Assert.True(session.State == TransferState.Completed, session.Error?.Message);
        Assert.Equal(TransferPath.Direct, session.Path);
        Assert.Equal(1, phoneLink.HostCalls); // the phone hosts either way
        Assert.Equal(0, receiverLink.HostCalls);
        Assert.Equal(hotspot, phoneLink.HostedNetworks.Single().IsAccessPoint);
        Assert.Same(receiverLink, air.LastJoinLink);
        // Receivers are told to join a hotspot as ordinary Wi-Fi (a phone would try the Wi-Fi Direct way first otherwise).
        Assert.Equal(hotspot, air.LastJoin!.HostIsComputer);
        Assert.Equal(TestFiles.Hash(file), TestFiles.Hash(Path.Combine(receiver.ReceiveFolder, "video.mp4")));
    }

    [Fact]
    public async Task APcThatCantJoinThePhonesHotspotGetsTheGroupOnTryAgain()
    {
        var air = new FakeDirectAir();
        var phoneLink = Fake(air.Link(Phone, canRunHotspot: true));
        var pcLink = Fake(air.Link(Pc));
        await using var phone = new TestNode("Redmi", directLink: phoneLink, kind: DeviceKinds.Phone);
        await using var pc = new TestNode("Laptop", directLink: pcLink, kind: DeviceKinds.Laptop);
        air.Connect(phone.Node, () => phone.AsDevice(), pc.Node, () => pc.AsDevice());
        pc.Trust(phone);
        pcLink.CantJoinAccessPoints = true;

        var session = phone.Node.Send(pc.AsDevice(), new[] { phone.CreateFile("a.jpg", 200_000) }, ConnectionMethod.Direct);
        await Wait.ForFinishAsync(session);
        Assert.Equal(TransferState.Failed, session.State);
        Assert.Contains("receiver join", session.Error!.Message);
        Assert.True(phoneLink.HostedNetworks.Last().IsAccessPoint);
        await Wait.UntilAsync(() => phoneLink.Hosting == null, because: "the hotspot went off");

        session.Resume();
        await Wait.ForFinishAsync(session);
        Assert.True(session.State == TransferState.Completed, session.Error?.Message);
        Assert.False(phoneLink.HostedNetworks.Last().IsAccessPoint);
    }

    [Fact]
    public async Task TwoPcsJoinTheSameHotspot()
    {
        var air = new FakeDirectAir();
        var phoneLink = Fake(air.Link(Phone, canRunHotspot: true));
        var aLink = Fake(air.Link(Pc));
        var bLink = Fake(air.Link(Pc));
        await using var phone = new TestNode("Redmi", directLink: phoneLink, kind: DeviceKinds.Phone);
        await using var a = new TestNode("Laptop", directLink: aLink, kind: DeviceKinds.Laptop);
        await using var b = new TestNode("Desk", directLink: bLink, kind: DeviceKinds.Desktop);
        air.Connect(phone.Node, () => phone.AsDevice(), a.Node, () => a.AsDevice());
        air.Connect(phone.Node, () => phone.AsDevice(), b.Node, () => b.AsDevice());
        a.Trust(phone);
        b.Trust(phone);

        var toA = phone.Node.Send(a.AsDevice(), new[] { phone.CreateFile("a.mp4", 3_000_000, 2) }, ConnectionMethod.Direct);
        var toB = phone.Node.Send(b.AsDevice(), new[] { phone.CreateFile("b.mp4", 3_000_000, 3) }, ConnectionMethod.Direct);
        await Wait.ForFinishAsync(toA);
        await Wait.ForFinishAsync(toB);

        Assert.True(toA.State == TransferState.Completed, toA.Error?.Message);
        Assert.True(toB.State == TransferState.Completed, toB.Error?.Message);
        Assert.Single(phoneLink.HostedNetworks); // one hotspot for both
        Assert.True(phoneLink.HostedNetworks[0].IsAccessPoint);
    }

    [Fact]
    public void AMarkedNetworkHasItsHostAndIsForgottenWhenLeft()
    {
        // A phone's hotspot picks its own subnet: the joined network is marked direct, and forgotten on leaving.
        var own = IPAddress.Parse("10.213.77.20");
        var host = IPAddress.Parse("10.213.77.1");
        Assert.Null(DirectAddresses.DirectHostOf(own));
        DirectAddresses.MarkDirectNetwork(own, host);
        try
        {
            Assert.Equal(host, DirectAddresses.DirectHostOf(own));
        }
        finally
        {
            DirectAddresses.ForgetDirectNetworks(hosted: false);
        }

        Assert.Null(DirectAddresses.DirectHostOf(own));
    }
}
