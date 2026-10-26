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
        Assert.StartsWith("Dilshodjo", read.Name); // as much of the name as fits
        Assert.Null(NearbyBeacon.TryParse(new byte[] { 1, 2, 3 }));
        // A garbled read (one byte off) is dropped, not listed as some other device.
        for (var i = 3; i < bytes.Length; i++)
        {
            var garbled = (byte[])bytes.Clone();
            garbled[i] ^= 0x10;
            Assert.Null(NearbyBeacon.TryParse(garbled));
        }
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
        var phoneRoles = DirectRoles.Host | DirectRoles.Join;
        var pcLink = (FakeDirectAir.FakeLink)wifi.Link(DirectRoles.Host | DirectRoles.JoinComputers);
        var phoneLink = (FakeDirectAir.FakeLink)wifi.Link(phoneRoles);
        await using var pc = new TestNode("Laptop", directLink: pcLink, nearbyRadio: bluetooth.Radio());
        await using var phone = new TestNode("Galaxy", directLink: phoneLink, kind: DeviceKinds.Phone, nearbyRadio: bluetooth.Radio());
        wifi.ConnectDirectOnly(phone.Node, pc.Node);

        // Receive open: visible over Bluetooth, and nothing else (no network for senders).
        pc.Node.Nearby.SetReceiving(true);
        phone.Node.Nearby.SetLooking(true);
        DeviceInfo? listed = null;
        await Wait.UntilAsync(() => (listed = phone.Node.Discovery.Devices.FirstOrDefault(NearbyService.IsPlaceholder)) != null, because: "PC heard over Bluetooth");
        Assert.Equal("Laptop", listed!.Name);
        Assert.Equal(0, wifi.Hosted);

        var device = await phone.Node.Nearby.ResolveAsync(listed, CancellationToken.None);
        Assert.Equal(pc.Node.Identity.DeviceId, device.Id);
        Assert.Equal(pc.Node.Identity.Fingerprint, device.Fingerprint);
        Assert.DoesNotContain(phone.Node.Discovery.Devices, NearbyService.IsPlaceholder);
        await Task.Delay(500); // several more sightings of its beacon: still listed once, by its real id
        Assert.DoesNotContain(phone.Node.Discovery.Devices, NearbyService.IsPlaceholder);
        Assert.Single(phone.Node.Discovery.Devices, d => d.Id == device.Id);
        Assert.DoesNotContain(phone.Node.Settings.Current.TrustedDevices, t => t.DeviceId == device.Id); // found, not trusted
        Assert.Null(phone.Node.Direct.KnownNetworkFor(device.Id)); // and the handshake handed over no network details
        Assert.Equal(0, wifi.Hosted);

        // Sending directly: the phone (the sender) starts its network; the PC joins it once its user agreed.
        var file = phone.CreateFile("photo.jpg", 300_000);
        var session = phone.Node.Send(device, new[] { file }, ConnectionMethod.Direct);
        await Wait.ForFinishAsync(session, TimeSpan.FromSeconds(30));
        Assert.Equal(TransferState.Completed, session.State);
        Assert.Equal(phoneRoles, wifi.LastHost);
        Assert.Same(pcLink, wifi.LastJoinLink);
        Assert.False(wifi.LastJoin!.HostIsComputer);
        Assert.Single(pc.Handler.DirectRequests);
        Assert.Equal(TestFiles.Hash(file), TestFiles.Hash(Path.Combine(pc.ReceiveFolder, "photo.jpg")));

        // Sent: the phone's network goes off and the PC is back on its own Wi-Fi.
        await Wait.UntilAsync(() => phoneLink.Hosting == null && pcLink.JoinedTo == null, because: "network off, PC left it");
    }

    [Fact]
    public async Task PcSendsToAPhoneWithReceiveOpenThenTurnsItsNetworkOff()
    {
        var wifi = new FakeDirectAir();
        var bluetooth = new FakeNearbyAir();
        var pcLink = (FakeDirectAir.FakeLink)wifi.Link(DirectRoles.Host | DirectRoles.JoinComputers);
        var phoneLink = (FakeDirectAir.FakeLink)wifi.Link(DirectRoles.Host | DirectRoles.Join);
        await using var pc = new TestNode("Laptop", directLink: pcLink, nearbyRadio: bluetooth.Radio());
        await using var phone = new TestNode("Galaxy", directLink: phoneLink, kind: DeviceKinds.Phone, nearbyRadio: bluetooth.Radio());
        wifi.ConnectDirectOnly(pc.Node, phone.Node);

        phone.Node.Nearby.SetReceiving(true);
        Assert.True(phone.Node.Direct.AnnouncedRoles.HasFlag(DirectRoles.Receiving));
        pc.Node.Nearby.SetLooking(true);
        DeviceInfo? listed = null;
        await Wait.UntilAsync(() => (listed = pc.Node.Discovery.Devices.FirstOrDefault(NearbyService.IsPlaceholder)) != null, because: "phone heard");
        Assert.Equal(DeviceKinds.Phone, listed!.Kind);
        var device = await pc.Node.Nearby.ResolveAsync(listed, CancellationToken.None);

        var session = pc.Node.Send(device, new[] { pc.CreateFile("report.pdf", 200_000) }, ConnectionMethod.Direct);
        await Wait.ForFinishAsync(session, TimeSpan.FromSeconds(30));
        Assert.Equal(TransferState.Completed, session.State);
        Assert.Equal(1, wifi.Joined);
        Assert.Same(pcLink, wifi.LastHostLink);
        Assert.True(wifi.LastJoin!.HostIsComputer);
        await Wait.UntilAsync(() => pcLink.Hosting == null && phoneLink.JoinedTo == null, because: "the PC's network went off");
    }

    [Fact]
    public async Task AnImpostorWithADifferentCertificateIsNotTrusted()
    {
        var wifi = new FakeDirectAir();
        var bluetooth = new FakeNearbyAir();
        var phoneLink = (FakeDirectAir.FakeLink)wifi.Link(DirectRoles.Host | DirectRoles.Join);
        await using var pc = new TestNode("Laptop", directLink: wifi.Link(DirectRoles.Host | DirectRoles.JoinComputers), nearbyRadio: bluetooth.Radio());
        await using var phone = new TestNode("Galaxy", directLink: phoneLink, kind: DeviceKinds.Phone, nearbyRadio: bluetooth.Radio());
        wifi.ConnectDirectOnly(phone.Node, pc.Node);
        // The phone knew this PC before, with another certificate.
        phone.Node.Direct.Remember(pc.Node.Identity.DeviceId, "Laptop", new string('a', 64), DeviceKinds.Laptop);

        pc.Node.Nearby.SetReceiving(true);
        phone.Node.Nearby.SetLooking(true);
        DeviceInfo? listed = null;
        await Wait.UntilAsync(() => (listed = phone.Node.Discovery.Find(pc.Node.Identity.DeviceId)) != null, because: "known PC heard");
        Assert.Empty(listed!.Endpoints);

        // Over Bluetooth it answers with a different certificate: refused before this phone starts anything.
        var session = phone.Node.Send(listed, new[] { phone.CreateFile("x.bin", 1000) }, ConnectionMethod.Direct);
        await Wait.ForFinishAsync(session, TimeSpan.FromSeconds(60));
        Assert.Equal(TransferState.Failed, session.State);
        Assert.Equal(DirectSessionState.Negotiating, session.DirectSession!.FailedAt);
        Assert.Contains("isn't the Laptop you connected to before", session.Error!.Message);
        Assert.True(bluetooth.Connections > 0);
        Assert.Equal(0, phoneLink.HostCalls);
        Assert.Equal(0, wifi.Joined);
        Assert.Empty(pc.Handler.DirectRequests);
        Assert.Equal(new string('a', 64), phone.Node.Direct.KnownNetworkFor(pc.Node.Identity.DeviceId)!.Fingerprint);
    }

    [Fact]
    public async Task AnOlderBluetoothSenderThatAsksForTheReceiversNetworkIsRefused()
    {
        // Beam 1.5.3 and older didn't host: it asked the receiver to start its network over Bluetooth.
        var bluetooth = new FakeNearbyAir();
        var pcLink = (FakeDirectAir.FakeLink)new FakeDirectAir().Link(DirectRoles.Host | DirectRoles.JoinComputers);
        await using var pc = new TestNode("Laptop", directLink: pcLink, nearbyRadio: bluetooth.Radio());
        pc.Node.Nearby.SetReceiving(true);
        var radio = bluetooth.Radio();

        await using var connection = await radio.ConnectAsync(radio.RecentServiceAddresses().Single(), CancellationToken.None);
        using var sender = new NearbyHandshake.Sender(NearbyBeacon.HashOf("old-phone"), NearbyHandshake.RequestFlags.None);
        await connection.WriteAsync(sender.Request(), CancellationToken.None);
        (NearbyHandshake.Status Status, string? Text) answer;
        do
        {
            await Task.Delay(50);
            answer = sender.Open(await connection.ReadAsync(CancellationToken.None));
        }
        while (answer.Status == NearbyHandshake.Status.Working);

        Assert.Equal(NearbyHandshake.Status.Refused, answer.Status);
        Assert.Contains("needs a newer Beam", answer.Text);
        Assert.Equal(0, pcLink.HostCalls);
        Assert.Null(pc.Node.Direct.HostedNetwork);
    }

    [Fact]
    public async Task ABluetoothHandshakeNeverHandsOutTheReceiversOwnNetworkDetailsToThisVersionsSenders()
    {
        var bluetooth = new FakeNearbyAir();
        var pcLink = (FakeDirectAir.FakeLink)new FakeDirectAir().Link(DirectRoles.Host | DirectRoles.JoinComputers);
        await using var pc = new TestNode("Laptop", directLink: pcLink, nearbyRadio: bluetooth.Radio());
        pc.Node.Nearby.SetReceiving(true);
        var radio = bluetooth.Radio(); // anyone nearby
        var address = radio.RecentServiceAddresses().Single();

        await using var connection = await radio.ConnectAsync(address, CancellationToken.None);
        using var sender = new NearbyHandshake.Sender(NearbyBeacon.HashOf("someone"), NearbyHandshake.RequestFlags.SenderHosts);
        await connection.WriteAsync(sender.Request(), CancellationToken.None);
        (NearbyHandshake.Status Status, string? Text) answer;
        do
        {
            await Task.Delay(50);
            answer = sender.Open(await connection.ReadAsync(CancellationToken.None));
        }
        while (answer.Status == NearbyHandshake.Status.Working);

        Assert.Equal(NearbyHandshake.Status.Ready, answer.Status);
        var code = PairingCode.FromJson(answer.Text!)!;
        Assert.Equal(pc.Node.Identity.DeviceId, code.DeviceId);
        Assert.DoesNotContain("\"s\"", answer.Text); // no network details: the sender hosts
        Assert.Equal(0, pcLink.HostCalls); // and nothing was started for it
    }

    [Fact]
    public async Task AfterLeavingReceiveItStaysReceivableForAWhileUnlessReopened()
    {
        var bluetooth = new FakeNearbyAir();
        await using var pc = new TestNode("Laptop", directLink: new FakeDirectAir().Link(DirectRoles.Host | DirectRoles.JoinComputers), nearbyRadio: bluetooth.Radio());
        await using var phone = new TestNode("Galaxy", kind: DeviceKinds.Phone, nearbyRadio: bluetooth.Radio());
        // Long against the 300 ms below: a slow CI machine once took over 600 ms for that wait.
        pc.Node.Nearby.ReceiveGrace = TimeSpan.FromSeconds(3);
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
