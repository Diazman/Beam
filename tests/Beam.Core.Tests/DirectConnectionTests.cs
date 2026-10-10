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
        await using var phone = new TestNode("Galaxy", pro: false, directLink: air.Link(DirectRoles.Host | DirectRoles.Join)); // 5 MB/s: the transfer takes a while
        air.Connect(phone.Node, () => phone.AsDevice(), pc.Node, () => pc.AsDevice());
        var file = phone.CreateFile("video.mp4", 30L * 1024 * 1024, 4);

        var session = phone.Node.Send(pc.AsDevice(), new[] { file });
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
