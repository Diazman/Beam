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

    private readonly List<FakeLink> _links = new();

    public IDirectLink Link(DirectRoles roles)
    {
        var link = new FakeLink(this, roles);
        _links.Add(link);
        return link;
    }

    /// <summary>The roles of the device whose link started the last network (who hosted).</summary>
    public DirectRoles? LastHost { get; private set; }

    /// <summary>The link that started the last network.</summary>
    public IDirectLink? LastHostLink { get; private set; }

    /// <summary>The link that joined last.</summary>
    public IDirectLink? LastJoinLink { get; private set; }

    /// <summary>The roles of the device whose link joined last (who joined).</summary>
    public DirectRoles? LastJoiner { get; private set; }

    /// <summary>Whether a joiner is on the host's network right now.</summary>
    public bool LinkUp { get; private set; }

    /// <summary>The joiner fell off the host's network (e.g. the phone went back to its normal Wi-Fi).</summary>
    public void LoseLink()
    {
        LinkUp = false;
        foreach (var link in _links.Where(l => l.Roles.HasFlag(DirectRoles.Join))) link.RaiseLost();
    }

    /// <summary>Whether the host's direct network shows up in Wi-Fi scans (phones notice paired PCs this way).</summary>
    /// <summary>The network details the last join was given (including how to join it).</summary>
    public DirectNetwork? LastJoin { get; private set; }

    public bool InRange { get; set; } = true;

    /// <summary>
    /// The two devices share no network at all: the joiner only reaches the host after joining its direct network.
    /// </summary>
    public void ConnectDirectOnly(BeamNode joiner, Func<DeviceInfo> joinerDevice, BeamNode host, Func<DeviceInfo> hostDevice)
    {
        joiner.Transfers.IsDirectAddress = a => a.Equals(DirectAddress);
        host.Transfers.IsDirectAddress = a => a.Equals(DirectAddress);
        _onJoined = () =>
        {
            joiner.Discovery.ReportReachable(hostDevice() with { Endpoints = new[] { new IPEndPoint(DirectAddress, host.Transfers.Port) } });
            host.Discovery.ReportReachable(joinerDevice() with { Endpoints = new[] { new IPEndPoint(DirectAddress, joiner.Transfers.Port) } });
        };
    }

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

    private sealed class FakeLink : IDirectLink, IDirectNetworkScanner, IDirectLinkEvents
    {
        private readonly FakeDirectAir _air;

        public FakeLink(FakeDirectAir air, DirectRoles roles)
        {
            _air = air;
            Roles = roles;
        }

        public DirectRoles Roles { get; }

        public Task<DirectNetwork> HostAsync(DirectNetwork? preferred, CancellationToken cancellationToken)
        {
            if (_air.HostError != null) throw new DirectLinkException(_air.HostError);
            Interlocked.Increment(ref _air._hosted);
            _air.LastHost = Roles;
            _air.LastHostLink = this;
            return Task.FromResult(_air._network ??= preferred ?? new DirectNetwork("DIRECT-Bm-Laptop", Guid.NewGuid().ToString("N")[..12]));
        }

        public async Task<IPAddress?> JoinAsync(DirectNetwork network, CancellationToken cancellationToken)
        {
            await Task.Delay(100, cancellationToken);
            if (network != _air._network) throw new DirectLinkException("Wrong passphrase");
            _air.LastJoin = network;
            Interlocked.Increment(ref _air._joined);
            _air.LastJoiner = Roles;
            _air.LastJoinLink = this;
            _air.LinkUp = true;
            _air._onJoined?.Invoke();
            return DirectAddress;
        }

        public event Action? Lost;

        public void RaiseLost() => Lost?.Invoke();

        public IReadOnlyCollection<string> VisibleNetworks() =>
            _air.InRange && _air._network != null ? new[] { "Home Wi-Fi", _air._network.Ssid } : new[] { "Home Wi-Fi" };

        public void Stop()
        {
            Interlocked.Increment(ref _air._stopped);
            _air._network = null;
        }
    }
}
