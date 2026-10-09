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
    private static readonly IPAddress DirectAddress = IPAddress.Parse("127.0.0.2");

    [Fact]
    public async Task SwitchingDuringATransferMovesItToTheDirectLinkAndBack()
    {
        var air = new FakeAir();
        await using var pc = new TestNode("Laptop", directLink: air.Link(DirectRoles.Host));
        await using var phone = new TestNode("Galaxy", pro: false, directLink: air.Link(DirectRoles.Host | DirectRoles.Join)); // 5 MB/s: the transfer takes a while
        air.Connect(phone, pc);
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
        var air = new FakeAir();
        await using var pc = new TestNode("Laptop", directLink: air.Link(DirectRoles.Host));
        await using var phone = new TestNode("Galaxy", directLink: air.Link(DirectRoles.Host | DirectRoles.Join));
        air.Connect(phone, pc);
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
        var air = new FakeAir();
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
        var air = new FakeAir { HostError = "This computer's Wi-Fi can't create a direct connection." };
        await using var pc = new TestNode("Desktop", directLink: air.Link(DirectRoles.Host));
        await using var phone = new TestNode("Galaxy", directLink: air.Link(DirectRoles.Host | DirectRoles.Join));
        air.Connect(phone, pc);
        var problems = new List<string>();
        phone.Node.Direct.Problem += problems.Add;

        Assert.False(await phone.Node.Direct.SwitchAsync(pc.Node.Identity.DeviceId, ConnectionMethod.Direct));
        Assert.Equal(new[] { "This computer's Wi-Fi can't create a direct connection." }, problems);
        Assert.Equal(ConnectionMethod.SameNetwork, phone.Node.Direct.MethodFor(pc.Node.Identity.DeviceId));
    }

    [Fact]
    public async Task AddressesOfThePreferredPathComeFirst()
    {
        var air = new FakeAir();
        await using var pc = new TestNode("Laptop", directLink: air.Link(DirectRoles.Host));
        await using var phone = new TestNode("Galaxy", directLink: air.Link(DirectRoles.Host | DirectRoles.Join));
        air.Connect(phone, pc);
        var both = pc.AsDevice() with { Endpoints = new[] { new IPEndPoint(DirectAddress, 1), new IPEndPoint(IPAddress.Loopback, 1) } };

        Assert.Equal(IPAddress.Loopback, phone.Node.Direct.Order(both).Endpoints[0].Address);
        phone.Node.Settings.Update(s => s.Connection = ConnectionMethod.Direct);
        Assert.Equal(DirectAddress, phone.Node.Direct.Order(both).Endpoints[0].Address);
        Assert.Equal(2, phone.Node.Direct.Order(both).Endpoints.Count); // the other path stays as a fallback
    }

    [Fact]
    public void RolesAndDirectAdaptersAreRecognized()
    {
        Assert.Equal(DirectRoles.Host | DirectRoles.Join, DirectRolesText.Parse(DirectRolesText.Format(DirectRoles.Host | DirectRoles.Join)));
        Assert.Equal(DirectRoles.None, DirectRolesText.Parse("teleport"));
        Assert.True(DirectAddresses.IsDirectAdapter("p2p-wlan0-3", ""));
        Assert.True(DirectAddresses.IsDirectAdapter("Local Area Connection* 12", "Microsoft Wi-Fi Direct Virtual Adapter #2"));
        Assert.False(DirectAddresses.IsDirectAdapter("Wi-Fi", "Intel(R) Wi-Fi 6 AX201 160MHz"));
    }

    /// <summary>Stand-in for the radio: hosting hands out a network, joining it makes the host reachable on 127.0.0.2.</summary>
    private sealed class FakeAir
    {
        private DirectNetwork? _network;
        private Action? _onJoined;
        private int _hosted, _joined, _stopped;

        public string? HostError { get; init; }

        public int Hosted => Volatile.Read(ref _hosted);

        public int Joined => Volatile.Read(ref _joined);

        public int Stopped => Volatile.Read(ref _stopped);

        public IDirectLink Link(DirectRoles roles) => new FakeLink(this, roles);

        /// <summary>Both devices see each other on the Wi-Fi network (127.0.0.1); joining adds the direct address.</summary>
        public void Connect(TestNode joiner, TestNode host)
        {
            joiner.Node.Transfers.IsDirectAddress = a => a.Equals(DirectAddress);
            host.Node.Transfers.IsDirectAddress = a => a.Equals(DirectAddress);
            joiner.Node.Discovery.ReportReachable(host.AsDevice());
            host.Node.Discovery.ReportReachable(joiner.AsDevice());
            _onJoined = () =>
            {
                joiner.Node.Discovery.ReportReachable(host.AsDevice() with { Endpoints = new[] { new IPEndPoint(DirectAddress, host.Node.Transfers.Port) } });
                host.Node.Discovery.ReportReachable(joiner.AsDevice() with { Endpoints = new[] { new IPEndPoint(DirectAddress, joiner.Node.Transfers.Port) } });
            };
        }

        private sealed class FakeLink : IDirectLink
        {
            private readonly FakeAir _air;

            public FakeLink(FakeAir air, DirectRoles roles)
            {
                _air = air;
                Roles = roles;
            }

            public DirectRoles Roles { get; }

            public Task<DirectNetwork> HostAsync(CancellationToken cancellationToken)
            {
                if (_air.HostError != null) throw new DirectLinkException(_air.HostError);
                Interlocked.Increment(ref _air._hosted);
                return Task.FromResult(_air._network ??= new DirectNetwork("DIRECT-Bm-Laptop", Guid.NewGuid().ToString("N")[..12]));
            }

            public async Task<IPAddress?> JoinAsync(DirectNetwork network, CancellationToken cancellationToken)
            {
                await Task.Delay(100, cancellationToken);
                if (network != _air._network) throw new DirectLinkException("Wrong passphrase");
                Interlocked.Increment(ref _air._joined);
                _air._onJoined?.Invoke();
                return DirectAddress;
            }

            public void Stop()
            {
                Interlocked.Increment(ref _air._stopped);
                _air._network = null;
            }
        }
    }
}
