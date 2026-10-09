using System.Net;
using Beam.Core.Direct;
using Beam.Core.Discovery;

namespace Beam.Core.Tests;

/// <summary>Stand-in for the radio: hosting hands out a network, joining it makes the host reachable on 127.0.0.2.</summary>
public sealed class FakeDirectAir
{
    public static readonly IPAddress DirectAddress = IPAddress.Parse("127.0.0.2");

    private DirectNetwork? _network;
    private Action? _onJoined;
    private int _hosted, _joined, _stopped;

    public string? HostError { get; init; }

    public int Hosted => Volatile.Read(ref _hosted);

    public int Joined => Volatile.Read(ref _joined);

    public int Stopped => Volatile.Read(ref _stopped);

    public IDirectLink Link(DirectRoles roles) => new FakeLink(this, roles);

    /// <summary>Both devices see each other on the Wi-Fi network (127.0.0.1); joining adds the direct address.</summary>
    public void Connect(BeamNode joiner, Func<DeviceInfo> joinerDevice, BeamNode host, Func<DeviceInfo> hostDevice)
    {
        joiner.Transfers.IsDirectAddress = a => a.Equals(DirectAddress);
        host.Transfers.IsDirectAddress = a => a.Equals(DirectAddress);
        joiner.Discovery.ReportReachable(hostDevice());
        host.Discovery.ReportReachable(joinerDevice());
        _onJoined = () =>
        {
            joiner.Discovery.ReportReachable(hostDevice() with { Endpoints = new[] { new IPEndPoint(DirectAddress, host.Transfers.Port) } });
            host.Discovery.ReportReachable(joinerDevice() with { Endpoints = new[] { new IPEndPoint(DirectAddress, joiner.Transfers.Port) } });
        };
    }

    private sealed class FakeLink : IDirectLink
    {
        private readonly FakeDirectAir _air;

        public FakeLink(FakeDirectAir air, DirectRoles roles)
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
