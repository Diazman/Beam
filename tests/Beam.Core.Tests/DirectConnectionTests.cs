using Beam.Core;
using System.Net;
using Beam.Core.Direct;
using Beam.Core.Discovery;
using Beam.Core.Settings;
using Beam.Core.Transfer;

namespace Beam.Core.Tests;

/// <summary>
/// Direct links with a stand-in for Wi-Fi Direct: 127.0.0.1 plays the Wi-Fi network and 127.0.0.2 the direct
/// link (both reach the same listener), so the whole switch — asking the other device to host, joining,
/// finding it on the new path, dropping the connection and resuming — runs for real.
/// </summary>
public class DirectConnectionTests
{
    private static readonly IPAddress DirectAddress = FakeDirectAir.DirectAddress;

    [Fact]
    public async Task SwitchingDuringATransferMovesItToTheDirectLinkAndBack()
    {
        var air = new FakeDirectAir();
        await using var pc = new TestNode("Laptop", directLink: air.Link(DirectRoles.Host));
        var slow = new SlowFiles(); // the transfer takes a while
        await using var phone = new TestNode("Galaxy", externalFiles: slow, directLink: air.Link(DirectRoles.Host | DirectRoles.Join));
        air.Connect(phone.Node, () => phone.AsDevice(), pc.Node, () => pc.AsDevice());
        var file = phone.CreateFile("video.mp4", 30L * 1024 * 1024, 4);

        var session = phone.Node.Send(pc.AsDevice(), new[] { slow.Add(file) });
        await Wait.UntilAsync(() => session.State == TransferState.Transferring && session.GetSnapshot().TransferredBytes > 2_000_000, because: "transferring over Wi-Fi");
        Assert.Equal(TransferPath.Network, session.Path);
        Assert.Equal(ConnectionMethod.SameNetwork, phone.Node.Direct.MethodFor(pc.Node.Identity.DeviceId));

        // The phone (which can join) asks the laptop (which can host) to start a direct network, joins it,
        // and the transfer continues over it.
        Assert.True(await phone.Node.Direct.SwitchAsync(pc.Node.Identity.DeviceId, ConnectionMethod.Direct));
        Assert.Equal(1, air.Hosted);
        Assert.Equal(1, air.Joined);
        await Wait.UntilAsync(() => session.Path == TransferPath.Direct, because: "reconnected directly");
        var before = session.GetSnapshot().TransferredBytes;
        Assert.True(before > 2_000_000, "continued, not restarted");
        Assert.Equal(ConnectionMethod.Direct, pc.Node.Direct.MethodFor(phone.Node.Identity.DeviceId)); // the laptop was told
        var received = pc.Node.Transfers.Sessions.Single(s => s.Direction == TransferDirection.Receive);
        await Wait.UntilAsync(() => received.State == TransferState.Transferring, because: "receiver resumed");

        // Back to the Wi-Fi network (e.g. the phone needs internet): the direct link is stopped on both sides.
        await Wait.UntilAsync(() => session.GetSnapshot().TransferredBytes > before + 2_000_000, because: "progress on the direct link");
        Assert.True(await phone.Node.Direct.SwitchAsync(pc.Node.Identity.DeviceId, ConnectionMethod.SameNetwork));
        await Wait.UntilAsync(() => session.Path == TransferPath.Network, because: "back on Wi-Fi");
        Assert.Equal(2, air.Stopped);

        await Wait.ForFinishAsync(session, TimeSpan.FromSeconds(60));
        Assert.Equal(TransferState.Completed, session.State);
        Assert.Equal(TestFiles.Hash(file), TestFiles.Hash(Path.Combine(pc.ReceiveFolder, "video.mp4")));
        Assert.Single(pc.Node.History.Entries, e => e.Direction == TransferDirection.Receive);
    }

    [Fact]
    public async Task ChoosingDirectBeforeSendingConnectsDirectlyFromTheStart()
    {
        var air = new FakeDirectAir();
        await using var pc = new TestNode("Laptop", directLink: air.Link(DirectRoles.Host));
        await using var phone = new TestNode("Galaxy", directLink: air.Link(DirectRoles.Host | DirectRoles.Join));
        air.Connect(phone.Node, () => phone.AsDevice(), pc.Node, () => pc.AsDevice());
        phone.Node.Settings.Update(s => s.Connection = ConnectionMethod.Direct);
        var paths = new List<TransferPath>();

        var session = phone.Node.Send(pc.AsDevice(), new[] { phone.CreateFile("notes.pdf", 300_000) });
        session.PathChanged += s => { lock (paths) paths.Add(s.Path); };
        await Wait.ForFinishAsync(session);

        Assert.Equal(TransferState.Completed, session.State);
        Assert.Equal(1, air.Joined);
        Assert.Equal(TransferPath.Direct, paths.First(p => p != TransferPath.Unknown));
    }

    [Fact]
    public async Task WhenTheOtherDeviceCantConnectDirectlyTheNetworkIsUsed()
    {
        var air = new FakeDirectAir();
        await using var pc = new TestNode("Old laptop"); // no direct link (older Beam, or no Wi-Fi Direct)
        await using var phone = new TestNode("Galaxy", directLink: air.Link(DirectRoles.Host | DirectRoles.Join));
        phone.Node.Settings.Update(s => s.Connection = ConnectionMethod.Direct);
        var problems = new List<string>();
        phone.Node.Direct.Problem += problems.Add;

        Assert.False(phone.Node.Direct.CanConnectDirectly(pc.AsDevice()));
        var session = phone.Node.Send(pc.AsDevice(), new[] { phone.CreateFile("a.txt", 1000) });
        await Wait.ForFinishAsync(session);
        Assert.Equal(TransferState.Completed, session.State);
        Assert.Equal(0, air.Joined);

        phone.Node.Discovery.ReportReachable(pc.AsDevice());
        Assert.False(await phone.Node.Direct.SwitchAsync(pc.Node.Identity.DeviceId, ConnectionMethod.Direct));
        Assert.Contains(problems, p => p.Contains("can't connect directly"));
        Assert.Equal(ConnectionMethod.Direct, phone.Node.Direct.MethodFor(pc.Node.Identity.DeviceId)); // still the setting; sends use the network
    }

    [Fact]
    public async Task HostFailureIsReportedAndTheTransferStaysOnTheNetwork()
    {
        var air = new FakeDirectAir { HostError = "This computer's Wi-Fi can't create a direct connection." };
        await using var pc = new TestNode("Desktop", directLink: air.Link(DirectRoles.Host));
        await using var phone = new TestNode("Galaxy", directLink: air.Link(DirectRoles.Host | DirectRoles.Join));
        air.Connect(phone.Node, () => phone.AsDevice(), pc.Node, () => pc.AsDevice());
        var problems = new List<string>();
        phone.Node.Direct.Problem += problems.Add;

        Assert.False(await phone.Node.Direct.SwitchAsync(pc.Node.Identity.DeviceId, ConnectionMethod.Direct));
        Assert.Equal(new[] { "This computer's Wi-Fi can't create a direct connection." }, problems);
        Assert.Equal(ConnectionMethod.SameNetwork, phone.Node.Direct.MethodFor(pc.Node.Identity.DeviceId));
    }

    [Fact]
    public async Task AddressesOfThePreferredPathComeFirst()
    {
        var air = new FakeDirectAir();
        await using var pc = new TestNode("Laptop", directLink: air.Link(DirectRoles.Host));
        await using var phone = new TestNode("Galaxy", directLink: air.Link(DirectRoles.Host | DirectRoles.Join));
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

    [Fact]
    public async Task PairedPhoneSendsToThePcDirectlyWithoutAnySharedNetwork()
    {
        var air = new FakeDirectAir();
        var phoneLink = air.Link(DirectRoles.Host | DirectRoles.Join);
        await using var pc = new TestNode("Laptop", directLink: air.Link(DirectRoles.Host));
        await using var phone = new TestNode("Galaxy", directLink: phoneLink);
        air.ConnectDirectOnly(phone.Node, () => phone.AsDevice(), pc.Node, () => pc.AsDevice());

        // The PC is set to Direct: it keeps its own network on for paired phones, with the same name every time.
        pc.Node.Settings.Update(s => s.Connection = ConnectionMethod.Direct);
        pc.Node.Direct.Start();
        await Wait.UntilAsync(() => air.Hosted > 0, because: "PC's network on");
        var own = pc.Node.Direct.OwnNetwork();
        Assert.Equal(own, pc.Node.Direct.OwnNetwork());

        // The phone scans the PC's code once: it trusts the PC and remembers its network.
        var code = PairingCode.TryParse(pc.Node.CreatePairingCode().AppendTo("http://192.168.0.109:47831/t/"))!;
        Assert.Equal(own.Ssid, code.Ssid);
        phone.Node.Pair(code);
        Assert.Contains(phone.Node.Settings.Current.TrustedDevices, t => t.DeviceId == pc.Node.Identity.DeviceId);
        Assert.Equal(own.Ssid, phone.Node.Direct.KnownNetworkFor(pc.Node.Identity.DeviceId)!.Ssid);

        // With Same Wi-Fi chosen on the phone, the PC's own network is never used: not listed, not joined.
        phone.Node.Direct.CheckKnownNetworks((IDirectNetworkScanner)phoneLink);
        Assert.Null(phone.Node.Discovery.Find(pc.Node.Identity.DeviceId));
        Assert.False(phone.Node.Direct.IsNearbyOnlyDirectly(pc.Node.Identity.DeviceId));
        Assert.Equal(0, air.Joined);

        // Direct chosen, no shared network: the phone notices the PC's network in a Wi-Fi scan and lists the PC.
        phone.Node.Settings.Update(s => s.Connection = ConnectionMethod.Direct);
        phone.Node.Direct.CheckKnownNetworks((IDirectNetworkScanner)phoneLink);
        var listed = phone.Node.Discovery.Find(pc.Node.Identity.DeviceId)!;
        Assert.Empty(listed.Endpoints);
        Assert.True(phone.Node.Direct.IsNearbyOnlyDirectly(listed.Id));

        // Sending joins the PC's network by itself and goes over it.
        var file = phone.CreateFile("photo.jpg", 400_000);
        var session = phone.Node.Send(listed, new[] { file });
        var paths = new List<TransferPath>();
        session.PathChanged += s => { lock (paths) paths.Add(s.Path); };
        await Wait.ForFinishAsync(session, TimeSpan.FromSeconds(30));
        Assert.Equal(TransferState.Completed, session.State);
        Assert.Equal(1, air.Joined);
        Assert.Contains(TransferPath.Direct, paths);
        Assert.Equal(TestFiles.Hash(file), TestFiles.Hash(Path.Combine(pc.ReceiveFolder, "photo.jpg")));

        // Out of range: no longer listed as reachable directly.
        air.InRange = false;
        phone.Node.Direct.CheckKnownNetworks((IDirectNetworkScanner)phoneLink);
        Assert.False(phone.Node.Direct.IsNearbyOnlyDirectly(listed.Id));
    }

    [Fact]
    public async Task ScanningThePcsCodeJoinsItsNetworkWhenThereIsNoSharedWifi()
    {
        var air = new FakeDirectAir();
        var phoneLink = air.Link(DirectRoles.Host | DirectRoles.Join);
        await using var pc = new TestNode("Laptop", directLink: air.Link(DirectRoles.Host));
        await using var phone = new TestNode("Galaxy", directLink: phoneLink);
        air.ConnectDirectOnly(phone.Node, () => phone.AsDevice(), pc.Node, () => pc.AsDevice());

        // The PC's Phone page runs its direct network (Connect directly); its code carries that network.
        pc.Node.Settings.Update(s => s.Connection = ConnectionMethod.Direct);
        pc.Node.Direct.Start();
        await Wait.UntilAsync(() => air.Hosted > 0, because: "PC's network on");

        // One scan: paired, then joined the PC's network without asking for anything else.
        var device = phone.Node.Pair(PairingCode.TryParse(pc.Node.CreatePairingCode().AppendTo("http://192.168.137.1:47831/t/"))!);
        Assert.True(await phone.Node.Direct.ConnectAfterPairingAsync(device, connectDirectly: false, CancellationToken.None));
        Assert.Equal(1, air.Joined);
        Assert.True(air.LastJoin!.HostIsComputer, "a PC's network is joined as ordinary Wi-Fi right away");
        Assert.NotEmpty(phone.Node.Discovery.Find(pc.Node.Identity.DeviceId)!.Endpoints);

        // Already linked: scanning again changes nothing.
        Assert.False(await phone.Node.Direct.ConnectAfterPairingAsync(device, connectDirectly: false, CancellationToken.None));
        Assert.Equal(1, air.Joined);
    }

    [Fact]
    public async Task TheCodeShownForConnectingDirectlyJoinsRightAwayAndSwitchesBothSides()
    {
        var air = new FakeDirectAir();
        await using var pc = new TestNode("Laptop", directLink: air.Link(DirectRoles.Host));
        await using var phone = new TestNode("Galaxy", directLink: air.Link(DirectRoles.Host | DirectRoles.Join));
        air.ConnectDirectOnly(phone.Node, () => phone.AsDevice(), pc.Node, () => pc.AsDevice());
        pc.Node.Settings.Update(s => s.Connection = ConnectionMethod.Direct);
        pc.Node.Direct.Start();
        await Wait.UntilAsync(() => air.Hosted > 0, because: "PC's network on");

        var code = PairingCode.TryParse(pc.Node.CreatePairingCode(connectDirectly: true).AppendTo("http://192.168.137.1:47831/t/"))!;
        Assert.True(code.ConnectDirectly);
        var device = phone.Node.Pair(code);
        var started = Environment.TickCount64;
        Assert.True(await phone.Node.Direct.ConnectAfterPairingAsync(device, code.ConnectDirectly, CancellationToken.None));
        Assert.True(Environment.TickCount64 - started < 3000, "no waiting for the shared Wi-Fi first");
        Assert.Equal(1, air.Joined);
        Assert.Equal(ConnectionMethod.Direct, phone.Node.Direct.MethodFor(pc.Node.Identity.DeviceId));
        await Wait.UntilAsync(() => pc.Node.Direct.MethodFor(phone.Node.Identity.DeviceId) == ConnectionMethod.Direct, because: "the PC was told");
    }

    [Fact]
    public async Task WithSameWiFiChosenSendingNeverJoinsThePcsNetwork()
    {
        var air = new FakeDirectAir();
        await using var pc = new TestNode("Laptop", directLink: air.Link(DirectRoles.Host));
        await using var phone = new TestNode("Galaxy", directLink: air.Link(DirectRoles.Host | DirectRoles.Join));
        air.Connect(phone.Node, () => phone.AsDevice(), pc.Node, () => pc.AsDevice());
        pc.Node.Settings.Update(s => s.Connection = ConnectionMethod.Direct);
        pc.Node.Direct.Start();
        await Wait.UntilAsync(() => air.Hosted > 0, because: "PC's network on");
        phone.Node.Pair(PairingCode.TryParse(pc.Node.CreatePairingCode().AppendTo(null))!); // the phone knows the PC's network
        Assert.Equal(ConnectionMethod.SameNetwork, phone.Node.Direct.MethodFor(pc.Node.Identity.DeviceId));

        var session = phone.Node.Send(pc.AsDevice(), new[] { phone.CreateFile("photo.jpg", 400_000) });
        await Wait.ForFinishAsync(session, TimeSpan.FromSeconds(30));
        Assert.Equal(TransferState.Completed, session.State);
        Assert.Equal(0, air.Joined);
    }

    [Fact]
    public async Task WithDirectChosenButThePcsNetworkOutOfRangeItSendsOverWiFiAndSwitchesWhenItAppears()
    {
        var (quick, retry, check) = (DirectManager.QuickDirectAttempt, DirectManager.RetryDirectAfter, DirectManager.UpgradeCheckInterval);
        DirectManager.RetryDirectAfter = TimeSpan.FromMilliseconds(500);
        DirectManager.UpgradeCheckInterval = TimeSpan.FromMilliseconds(200);
        try
        {
            var air = new FakeDirectAir();
            await using var pc = new TestNode("Laptop", directLink: air.Link(DirectRoles.Host));
            var slow = new SlowFiles();
            await using var phone = new TestNode("Galaxy", externalFiles: slow, directLink: air.Link(DirectRoles.Host | DirectRoles.Join));
            air.Connect(phone.Node, () => phone.AsDevice(), pc.Node, () => pc.AsDevice());
            pc.Node.Settings.Update(s => s.Connection = ConnectionMethod.Direct);
            pc.Node.Direct.Start();
            await Wait.UntilAsync(() => air.Hosted > 0, because: "PC's network on");
            phone.Node.Pair(PairingCode.TryParse(pc.Node.CreatePairingCode().AppendTo(null))!);
            phone.Node.Settings.Update(s => s.Connection = ConnectionMethod.Direct);
            air.InRange = false;

            var file = phone.CreateFile("video.mp4", 30L * 1024 * 1024, 5);
            var session = phone.Node.Send(pc.AsDevice(), new[] { slow.Add(file) });
            await Wait.UntilAsync(() => session.State == TransferState.Transferring, because: "sending over Wi-Fi without waiting", timeout: TimeSpan.FromSeconds(5));
            Assert.Equal(TransferPath.Network, session.Path);
            Assert.Equal(0, air.Joined);

            air.InRange = true;
            await Wait.UntilAsync(() => session.Path == TransferPath.Direct, because: "moved to the direct link once it was in range", timeout: TimeSpan.FromSeconds(20));
            Assert.Equal(1, air.Joined);
            await Wait.ForFinishAsync(session, TimeSpan.FromSeconds(60));
            Assert.Equal(TransferState.Completed, session.State);
            Assert.Equal(TestFiles.Hash(file), TestFiles.Hash(Path.Combine(pc.ReceiveFolder, "video.mp4")));
        }
        finally
        {
            (DirectManager.QuickDirectAttempt, DirectManager.RetryDirectAfter, DirectManager.UpgradeCheckInterval) = (quick, retry, check);
        }
    }

    [Fact]
    public async Task AfterTheDirectNetworkIsLostThePhoneRejoinsItWithoutAskingThePcFirst()
    {
        // The phone fell back to its normal Wi-Fi mid-transfer: asking the PC over that network fails or is slow,
        // while the PC's network is still there to rejoin.
        var air = new FakeDirectAir();
        await using var pc = new TestNode("Laptop", directLink: air.Link(DirectRoles.Host));
        await using var phone = new TestNode("Galaxy", directLink: air.Link(DirectRoles.Host | DirectRoles.Join));
        air.Connect(phone.Node, () => phone.AsDevice(), pc.Node, () => pc.AsDevice());
        phone.Node.Transfers.IsDirectAddress = a => a.Equals(FakeDirectAir.DirectAddress) && air.LinkUp; // like a real adapter
        Assert.True(await phone.Node.Direct.SwitchAsync(pc.Node.Identity.DeviceId, ConnectionMethod.Direct));
        Assert.Equal(1, air.Hosted);
        Assert.Equal(1, air.Joined);

        air.LoseLink();
        await phone.Node.Direct.ConnectAsync(pc.AsDevice(), CancellationToken.None);
        Assert.Equal(2, air.Joined);
        Assert.Equal(1, air.Hosted); // rejoined its known network; didn't ask the PC to start one
    }
}
