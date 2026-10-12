using System.Security.Cryptography;
using Beam.Core.Direct;
using Beam.Core.Discovery;
using Beam.Core.Nearby;
using Beam.Core.Settings;
using Beam.Core.Transfer;

namespace Beam.Core.Tests;

/// <summary>Receive / Send with no shared network: found over (fake) Bluetooth, connected over the receiver's own network.</summary>
public class NearbyTests
{
    [Fact]
    public void BeaconCarriesIdHashFlagsAndAsMuchOfTheNameAsFits()
    {
        var beacon = new NearbyBeacon(NearbyBeacon.HashOf("abc"), "Dilshodjon's ноутбук", NearbyBeaconFlags.Computer | NearbyBeaconFlags.Hosting);
        var bytes = beacon.ToBytes();
        Assert.True(bytes.Length <= NearbyBeacon.MaxPayload);
        var read = NearbyBeacon.TryParse(bytes)!;
        Assert.True(read.Matches("abc"));
        Assert.False(read.Matches("abd"));
        Assert.Equal(beacon.Flags, read.Flags);
        Assert.StartsWith("Dilshodjon", read.Name);
        Assert.Null(NearbyBeacon.TryParse(new byte[] { 1, 2, 3 }));
    }

    [Fact]
    public void HandshakeOnlyTheSenderCanReadAndAlteringItIsNoticed()
    {
        using var sender = new NearbyHandshake.Sender(NearbyBeacon.HashOf("me"));
        var request = sender.Request();
        Assert.True(NearbyHandshake.IsRequest(request));
        Assert.Equal(NearbyBeacon.HashOf("me"), NearbyHandshake.SenderHash(request));

        var answer = NearbyHandshake.Answer(request, "secret network details");
        Assert.DoesNotContain("secret", System.Text.Encoding.UTF8.GetString(answer));
        Assert.Equal((NearbyHandshake.Status.Ready, "secret network details"), sender.Open(answer));

        using var someoneElse = new NearbyHandshake.Sender(NearbyBeacon.HashOf("other"));
        Assert.Throws<CryptographicException>(() => someoneElse.Open(answer));
        var altered = (byte[])answer.Clone();
        altered[^40] ^= 1;
        Assert.Throws<CryptographicException>(() => sender.Open(altered));
        Assert.Equal(NearbyHandshake.Status.Working, sender.Open(NearbyHandshake.Working()).Status);
        Assert.Equal((NearbyHandshake.Status.Refused, "closed"), sender.Open(NearbyHandshake.Refuse("closed")));
    }

    [Fact]
    public void JoinRequestTravelsEncryptedWithTheHandshakesKeysAndItsLengthIsKnownFromItsStart()
    {
        using var sender = new NearbyHandshake.Sender(NearbyBeacon.HashOf("phone"), NearbyHandshake.RequestFlags.SenderHosts);
        var request = sender.Request();
        Assert.Equal(NearbyHandshake.RequestLength, NearbyHandshake.ExpectedLength(request.AsSpan(0, 2)));
        Assert.Equal(NearbyHandshake.RequestFlags.SenderHosts, NearbyHandshake.FlagsOf(request));
        var answer = NearbyHandshake.Answer(request, "pc code", out var keys);
        Assert.Equal(NearbyHandshake.Status.Ready, sender.Open(answer).Status);

        var join = sender.JoinRequest("phone code with DIRECT-xy-Beam-Galaxy / passphrase");
        Assert.True(NearbyHandshake.IsJoinRequest(join));
        Assert.Null(NearbyHandshake.ExpectedLength(join.AsSpan(0, 3)));
        Assert.Equal(join.Length, NearbyHandshake.ExpectedLength(join.AsSpan(0, 4)));
        Assert.DoesNotContain("passphrase", System.Text.Encoding.UTF8.GetString(join));
        Assert.Equal("phone code with DIRECT-xy-Beam-Galaxy / passphrase", NearbyHandshake.OpenJoinRequest(join, keys!));
        join[^1] ^= 1;
        Assert.Throws<CryptographicException>(() => NearbyHandshake.OpenJoinRequest(join, keys!));
        Assert.Equal((NearbyHandshake.Status.Joined, "192.168.49.37"), sender.Open(NearbyHandshake.Joined("192.168.49.37")));
    }

    [Fact]
    public async Task PhoneSendsToAPcWithReceiveOpenAndNoSharedNetwork()
    {
        var wifi = new FakeDirectAir();
        var bluetooth = new FakeNearbyAir();
        var pcRoles = DirectRoles.Host | DirectRoles.JoinComputers;
        await using var pc = new TestNode("Laptop", directLink: wifi.Link(pcRoles), nearbyRadio: bluetooth.Radio());
        await using var phone = new TestNode("Galaxy", directLink: wifi.Link(DirectRoles.Host | DirectRoles.Join), kind: DeviceKinds.Phone,
            nearbyRadio: bluetooth.Radio());
        wifi.ConnectDirectOnly(pc.Node, () => pc.AsDevice(), phone.Node, () => phone.AsDevice());
        var leave = DirectManager.LeaveReceiverAfter;
        DirectManager.LeaveReceiverAfter = TimeSpan.FromMilliseconds(300);
        try
        {
            pc.Node.Nearby.SetReceiving(true);
            phone.Node.Nearby.SetLooking(true);
            DeviceInfo? listed = null;
            await Wait.UntilAsync(() => (listed = phone.Node.Discovery.Devices.FirstOrDefault(NearbyService.IsPlaceholder)) != null, because: "PC heard over Bluetooth");
            Assert.Equal("Laptop", listed!.Name);

            var device = await phone.Node.Nearby.ResolveAsync(listed, CancellationToken.None);
            Assert.Equal(pc.Node.Identity.DeviceId, device.Id);
            Assert.Equal(pc.Node.Identity.Fingerprint, device.Fingerprint);
            Assert.Equal(0, wifi.Hosted); // the phone will host: the PC doesn't start its network
            Assert.DoesNotContain(phone.Node.Discovery.Devices, NearbyService.IsPlaceholder);
            Assert.DoesNotContain(phone.Node.Settings.Current.TrustedDevices, t => t.DeviceId == device.Id); // found, not trusted

            // Sending: the phone starts its network and asks the PC over Bluetooth to join it.
            var file = phone.CreateFile("photo.jpg", 300_000);
            var session = phone.Node.Send(device, new[] { file });
            await Wait.ForFinishAsync(session, TimeSpan.FromSeconds(30));
            Assert.Equal(TransferState.Completed, session.State);
            Assert.Equal(1, wifi.Hosted);
            Assert.Equal(1, wifi.Joined);
            Assert.Equal(pcRoles, wifi.LastJoiner);
            Assert.False(wifi.LastJoin!.HostIsComputer);
            Assert.Equal(TestFiles.Hash(file), TestFiles.Hash(Path.Combine(pc.ReceiveFolder, "photo.jpg")));

            // Received: the PC leaves the phone's network again.
            await Wait.UntilAsync(() => wifi.Stopped > 0, because: "PC left the phone's network");
        }
        finally
        {
            DirectManager.LeaveReceiverAfter = leave;
        }
    }

    [Fact]
    public async Task PcSendsToAPhoneWithReceiveOpenThenGoesBackToItsWifi()
    {
        var wifi = new FakeDirectAir();
        var bluetooth = new FakeNearbyAir();
        await using var pc = new TestNode("Laptop", directLink: wifi.Link(DirectRoles.Host | DirectRoles.JoinComputers), nearbyRadio: bluetooth.Radio());
        await using var phone = new TestNode("Galaxy", directLink: wifi.Link(DirectRoles.Host | DirectRoles.Join), kind: DeviceKinds.Phone,
            nearbyRadio: bluetooth.Radio());
        wifi.ConnectDirectOnly(pc.Node, () => pc.AsDevice(), phone.Node, () => phone.AsDevice());
        var leave = DirectManager.LeaveReceiverAfter;
        DirectManager.LeaveReceiverAfter = TimeSpan.FromMilliseconds(300);
        try
        {
            // Receive open on the phone: visible over Bluetooth; its network starts when the PC asks for it.
            phone.Node.Nearby.SetReceiving(true);
            Assert.True(phone.Node.Direct.AnnouncedRoles.HasFlag(DirectRoles.Receiving));
            Assert.Equal(0, wifi.Hosted);

            pc.Node.Nearby.SetLooking(true);
            DeviceInfo? listed = null;
            await Wait.UntilAsync(() => (listed = pc.Node.Discovery.Devices.FirstOrDefault(NearbyService.IsPlaceholder)) != null, because: "phone heard");
            Assert.Equal(DeviceKinds.Phone, listed!.Kind);
            var device = await pc.Node.Nearby.ResolveAsync(listed, CancellationToken.None);
            Assert.Equal(1, wifi.Hosted);

            var session = pc.Node.Send(device, new[] { pc.CreateFile("report.pdf", 200_000) });
            await Wait.ForFinishAsync(session, TimeSpan.FromSeconds(30));
            Assert.Equal(TransferState.Completed, session.State);
            Assert.Equal(1, wifi.Joined);
            Assert.False(wifi.LastJoin!.HostIsComputer);

            // Done sending: the PC leaves the phone's network (back to its own Wi-Fi).
            await Wait.UntilAsync(() => wifi.Stopped > 0, because: "PC left the phone's network");
        }
        finally
        {
            DirectManager.LeaveReceiverAfter = leave;
        }
    }

    [Fact]
    public async Task OnTheSameWifiASenderPreferringDirectJoinsTheReceiversNetworkWithoutAPrompt()
    {
        var wifi = new FakeDirectAir();
        await using var receiver = new TestNode("Desk PC", directLink: wifi.Link(DirectRoles.Host | DirectRoles.JoinComputers));
        await using var sender = new TestNode("Laptop", directLink: wifi.Link(DirectRoles.Host | DirectRoles.JoinComputers), kind: DeviceKinds.Laptop);
        receiver.Node.Nearby.SetReceiving(true);
        sender.Node.Settings.Update(s => s.Connection = ConnectionMethod.Direct);
        wifi.Connect(sender.Node, () => sender.AsDevice(), receiver.Node, () => receiver.AsDevice());
        Assert.True(sender.Node.Direct.CanConnectDirectly(receiver.AsDevice()), "Receive open: the computer hosts for any sender");

        var session = sender.Node.Send(receiver.AsDevice(), new[] { sender.CreateFile("a.bin", 100_000) });
        await Wait.ForFinishAsync(session, TimeSpan.FromSeconds(30));
        Assert.Equal(TransferState.Completed, session.State);
        Assert.Equal(1, wifi.Joined);
    }

    [Fact]
    public async Task AnImpostorWithADifferentCertificateIsNotTrusted()
    {
        var wifi = new FakeDirectAir();
        var bluetooth = new FakeNearbyAir();
        await using var pc = new TestNode("Laptop", directLink: wifi.Link(DirectRoles.Host | DirectRoles.JoinComputers), nearbyRadio: bluetooth.Radio());
        await using var phone = new TestNode("Galaxy", directLink: wifi.Link(DirectRoles.Host | DirectRoles.Join), kind: DeviceKinds.Phone,
            nearbyRadio: bluetooth.Radio());
        // The phone knew this PC before, with another certificate.
        phone.Node.Direct.Remember(pc.Node.Identity.DeviceId, "Laptop", new string('a', 64), DeviceKinds.Laptop, new DirectNetwork("DIRECT-xy-Old", "oldpassphrase"));

        pc.Node.Nearby.SetReceiving(true);
        phone.Node.Nearby.SetLooking(true);
        DeviceInfo? listed = null;
        await Wait.UntilAsync(() => (listed = phone.Node.Discovery.Find(pc.Node.Identity.DeviceId)) != null, because: "known PC heard");
        Assert.Empty(listed!.Endpoints);

        // Joining the remembered network fails; asking again over Bluetooth gets an answer from a different certificate.
        var session = phone.Node.Send(listed, new[] { phone.CreateFile("x.bin", 1000) });
        await Wait.ForFinishAsync(session, TimeSpan.FromSeconds(60));
        Assert.NotEqual(TransferState.Completed, session.State);
        Assert.True(bluetooth.Connections > 0, "asked over Bluetooth after the remembered network failed");
        Assert.Equal(0, wifi.Joined);
        Assert.Equal("oldpassphrase", phone.Node.Direct.KnownNetworkFor(pc.Node.Identity.DeviceId)!.Passphrase);
    }

    [Fact]
    public async Task AfterLeavingReceiveItStaysReceivableForAWhileUnlessReopened()
    {
        var bluetooth = new FakeNearbyAir();
        await using var pc = new TestNode("Laptop", directLink: new FakeDirectAir().Link(DirectRoles.Host | DirectRoles.JoinComputers), nearbyRadio: bluetooth.Radio());
        await using var phone = new TestNode("Galaxy", kind: DeviceKinds.Phone, nearbyRadio: bluetooth.Radio());
        pc.Node.Nearby.ReceiveGrace = TimeSpan.FromMilliseconds(600);
        phone.Node.Nearby.SetLooking(true);

        pc.Node.Nearby.SetReceiving(true);
        pc.Node.Nearby.SetReceiving(false); // left Receive
        Assert.True(pc.Node.Nearby.IsReceiving);
        pc.Node.Nearby.SetReceiving(true); // came back: nothing stops
        pc.Node.Nearby.SetReceiving(false);
        await Task.Delay(300);
        Assert.True(pc.Node.Nearby.IsReceiving);
        await Wait.UntilAsync(() => !pc.Node.Nearby.IsReceiving, because: "stopped after the grace period");
        Assert.False(pc.Node.Direct.IsReceiving);
    }
}
