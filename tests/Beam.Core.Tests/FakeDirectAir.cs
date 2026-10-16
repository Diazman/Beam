using System.Net;
using Beam.Core.Direct;
using Beam.Core.Discovery;

namespace Beam.Core.Tests;

/// <summary>
/// Stand-in for the radio, NOT real Wi-Fi: each link that hosts has its own network (name, passphrase); joining checks
/// them like a WPA2 network would, and can be told to fail the way phones and PCs do (prompt denied, never answers, out
/// of range). On the loopback interface 127.0.0.1 plays the shared Wi-Fi network, 127.0.0.2 the host's address on the
/// direct network and 127.0.0.3 the joiner's: <see cref="Wire"/> makes both nodes treat .2/.3 as the direct network and
/// bind their direct connections to their own address there, so the route checks of a direct session run for real.
/// </summary>
public sealed class FakeDirectAir
{
    public static readonly IPAddress DirectAddress = IPAddress.Parse("127.0.0.2"); // the host's address on the direct network
    public static readonly IPAddress JoinerAddress = IPAddress.Parse("127.0.0.3"); // the joiner's

    private readonly object _gate = new();
    private readonly List<FakeLink> _links = new();
    private int _hosted, _joined, _stopped;

    public string? HostError { get; init; }

    /// <summary>Joining fails from now on (e.g. the network went out of range).</summary>
    public bool FailJoins { get; set; }

    /// <summary>The platform says the network started, but no address comes up on it (a driver that half-works).</summary>
    public bool HostUsable { get; set; } = true;

    /// <summary>Networks don't show up in Wi-Fi scans (scan throttled or not allowed); joining by name still works.</summary>
    public bool HiddenFromScans { get; set; }

    public int Hosted => Volatile.Read(ref _hosted);

    public int Joined => Volatile.Read(ref _joined);

    public int Stopped => Volatile.Read(ref _stopped);

    public IDirectLink Link(DirectRoles roles, bool sharesInternet = false, string? joinError = null, bool canRunHotspot = false, bool reportsNoHost = false)
    {
        var link = new FakeLink(this, roles)
        {
            CanShareInternet = sharesInternet, JoinError = joinError, CanRunHotspot = canRunHotspot, ReportsNoHost = reportsNoHost,
        };
        lock (_gate) _links.Add(link);
        return link;
    }

    /// <summary>The roles of the device whose link started the last network (who hosted).</summary>
    public DirectRoles? LastHost { get; private set; }

    public IDirectLink? LastHostLink { get; private set; }

    public IDirectLink? LastJoinLink { get; private set; }

    /// <summary>The roles of the device whose link joined last (who joined).</summary>
    public DirectRoles? LastJoiner { get; private set; }

    /// <summary>The network details the last join was given.</summary>
    public DirectNetwork? LastJoin { get; private set; }

    /// <summary>Whether any joiner is on a host's network right now.</summary>
    public bool LinkUp
    {
        get
        {
            lock (_gate) return _links.Any(l => l.JoinedTo != null);
        }
    }

    public bool InRange { get; set; } = true;

    /// <summary>Joiners fall off their host's network (e.g. the phone went back to its normal Wi-Fi).</summary>
    public void LoseLink()
    {
        List<FakeLink> lost;
        lock (_gate)
        {
            lost = _links.Where(l => l.JoinedTo != null).ToList();
            foreach (var link in lost) link.JoinedTo = null;
        }

        foreach (var link in lost) link.RaiseLost();
    }

    /// <summary>
    /// Makes 127.0.0.2/127.0.0.3 these nodes' direct network: each treats them as direct addresses, its own address there
    /// is the other one, and its hosted network is usable when <see cref="HostUsable"/>.
    /// </summary>
    public void Wire(params BeamNode[] nodes)
    {
        foreach (var node in nodes)
        {
            node.Transfers.IsDirectAddress = a => a.Equals(DirectAddress) || a.Equals(JoinerAddress);
            node.Direct.OwnAddressNear = a => a.Equals(DirectAddress) ? JoinerAddress : a.Equals(JoinerAddress) ? DirectAddress : null;
            node.Direct.HostNetworkReady = () => HostUsable;
            node.Direct.OwnHostedAddress = () => DirectAddress;
        }
    }

    /// <summary>Both devices see each other on the shared Wi-Fi network (127.0.0.1), and are wired for direct links.</summary>
    public void Connect(BeamNode a, Func<DeviceInfo> aDevice, BeamNode b, Func<DeviceInfo> bDevice)
    {
        Wire(a, b);
        a.Discovery.ReportReachable(bDevice());
        b.Discovery.ReportReachable(aDevice());
    }

    /// <summary>The two devices share no network at all (found over Bluetooth only).</summary>
    public void ConnectDirectOnly(BeamNode a, BeamNode b) => Wire(a, b);

    public FakeLink? HostOf(string ssid)
    {
        lock (_gate) return _links.FirstOrDefault(l => l.Hosting?.Ssid == ssid);
    }

    public sealed class FakeLink : IDirectLink, IDirectNetworkScanner, IDirectLinkEvents, IInternetSharing, IDirectRadio, IAccessPointHosting
    {
        /// <summary>A phone that can run its own hotspot beside its Wi-Fi (Android 11+ with that support).</summary>
        public bool CanRunHotspot { get; init; }

        public bool PreferAccessPoint { get; set; }

        /// <summary>Joining can't tell the host's address (iOS doesn't show the network's gateway to apps).</summary>
        public bool ReportsNoHost { get; init; }

        /// <summary>This PC's join requests to a phone's hotspot fail.</summary>
        public bool CantJoinAccessPoints { get; set; }

        /// <summary>This device's Wi-Fi switch.</summary>
        public bool WifiOn { get; set; } = true;

        /// <summary>When Beam asks (the system's Wi-Fi panel), the user turns Wi-Fi on.</summary>
        public bool TurnsOnWhenAsked { get; set; }

        public int WifiAsks => Volatile.Read(ref _wifiAsks);

        private int _wifiAsks;

        public bool? IsWifiEnabled => WifiOn;

        public Task<bool> RequestWifiOnAsync(TimeSpan wait, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _wifiAsks);
            if (TurnsOnWhenAsked) WifiOn = true;
            return Task.FromResult(WifiOn);
        }

        public bool CanShareInternet { get; init; }

        public bool PreferInternetSharing { get; set; }

        /// <summary>Its hotspot doesn't start: the network it starts doesn't share its internet (Windows' Wi-Fi Direct instead).</summary>
        public bool WontShare { get; set; }

        /// <summary>This phone's join requests to an internet-sharing network (Windows' hotspot) go unanswered.</summary>
        public bool CantJoinSharedNetworks { get; set; }

        /// <summary>This device can't join (e.g. a desktop without Wi-Fi).</summary>
        public string? JoinError { get; init; }

        /// <summary>What joining does on this device (the system's prompt, a radio that never answers…).</summary>
        public JoinOutcome NextJoins { get; set; } = JoinOutcome.Joins;

        /// <summary>How long joining takes.</summary>
        public TimeSpan JoinDelay { get; set; } = TimeSpan.FromMilliseconds(100);

        /// <summary>The network actually started with another passphrase than the one reported (the joiner gets wrong credentials).</summary>
        public bool StartsWithOtherPassphrase { get; set; }

        /// <summary>Starting the network is held until this completes (a slow or stuck radio).</summary>
        public TaskCompletionSource? HoldHost { get; set; }

        private readonly FakeDirectAir _air;
        private int _hostCalls, _joinCalls, _stopCalls;

        public FakeLink(FakeDirectAir air, DirectRoles roles)
        {
            _air = air;
            Roles = roles;
        }

        public DirectRoles Roles { get; }

        /// <summary>The network this device hosts right now.</summary>
        public DirectNetwork? Hosting { get; private set; }

        /// <summary>The host whose network this device is on right now.</summary>
        public FakeLink? JoinedTo { get; internal set; }

        public int HostCalls => Volatile.Read(ref _hostCalls);

        public int JoinCalls => Volatile.Read(ref _joinCalls);

        public int StopCalls => Volatile.Read(ref _stopCalls);

        /// <summary>Every network this device was asked to start (to check fresh passphrases).</summary>
        public List<DirectNetwork> HostedNetworks { get; } = new();

        public async Task<DirectNetwork> HostAsync(DirectNetwork? preferred, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _hostCalls);
            if (HoldHost is { } hold) await hold.Task.WaitAsync(cancellationToken);
            if (_air.HostError != null) throw new DirectLinkException(_air.HostError);
            if (!WifiOn) throw new DirectLinkException("Wi-Fi is off.");
            lock (_air._gate)
            {
                if (Hosting != null && (preferred == null || preferred == Hosting) && !StartsWithOtherPassphrase) return Hosting;
                LeaveLocked(); // one adapter: hosting ends a join
                var reported = (preferred ?? new DirectNetwork("DIRECT-Bm-Laptop", Guid.NewGuid().ToString("N")[..12]))
                    with { SharesInternet = PreferInternetSharing && CanShareInternet && !WontShare, IsAccessPoint = PreferAccessPoint && CanRunHotspot };
                Hosting = StartsWithOtherPassphrase ? reported with { Passphrase = "not-" + reported.Passphrase } : reported;
                HostedNetworks.Add(reported);
                Interlocked.Increment(ref _air._hosted);
                _air.LastHost = Roles;
                _air.LastHostLink = this;
                return reported;
            }
        }

        public async Task<IPAddress?> JoinAsync(DirectNetwork network, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _joinCalls);
            await Task.Delay(JoinDelay, cancellationToken);
            if (JoinError != null) throw new DirectLinkException(JoinError);
            if (!WifiOn) throw new DirectLinkException("Wi-Fi is off.");
            switch (NextJoins)
            {
                case JoinOutcome.PromptDenied:
                    throw new DirectLinkException("The phone's user didn't allow connecting to " + network.Ssid);
                case JoinOutcome.NeverAnswers:
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                    break;
            }

            if (_air.FailJoins || !_air.InRange) throw new DirectLinkException("The network isn't in range.");
            lock (_air._gate)
            {
                var host = _air._links.FirstOrDefault(l => l.Hosting?.Ssid == network.Ssid);
                if (host == null) throw new DirectLinkException("The network isn't in range.");
                if (host.Hosting!.Passphrase != network.Passphrase) throw new DirectLinkException("Wrong passphrase");
                if (CantJoinSharedNetworks && host.Hosting.SharesInternet) throw new DirectLinkException("Android didn't answer the join request.");
                if (CantJoinAccessPoints && host.Hosting.IsAccessPoint) throw new DirectLinkException("Windows couldn't connect to the hotspot.");
                if (Hosting != null) Hosting = null; // one adapter: joining ends this device's own network
                JoinedTo = host;
                _air.LastJoin = network;
                Interlocked.Increment(ref _air._joined);
                _air.LastJoiner = Roles;
                _air.LastJoinLink = this;
            }

            return ReportsNoHost ? null : DirectAddress;
        }

        public event Action? Lost;

        public void RaiseLost() => Lost?.Invoke();

        public IReadOnlyCollection<string> VisibleNetworks()
        {
            lock (_air._gate)
            {
                var visible = new List<string> { "Home Wi-Fi" };
                if (_air.InRange && !_air.HiddenFromScans) visible.AddRange(_air._links.Where(l => l.Hosting != null).Select(l => l.Hosting!.Ssid));
                return visible;
            }
        }

        /// <summary>Stops this device's own network (its joiners fall off) and leaves the one it joined; nobody else's.</summary>
        public void Stop()
        {
            Interlocked.Increment(ref _stopCalls);
            Interlocked.Increment(ref _air._stopped);
            List<FakeLink> dropped;
            lock (_air._gate)
            {
                LeaveLocked();
                dropped = Hosting == null ? new List<FakeLink>() : _air._links.Where(l => l.JoinedTo == this).ToList();
                foreach (var joiner in dropped) joiner.JoinedTo = null;
                Hosting = null;
            }

            foreach (var joiner in dropped) joiner.RaiseLost();
        }

        private void LeaveLocked() => JoinedTo = null;
    }

    public enum JoinOutcome
    {
        Joins,

        /// <summary>The user tapped "Cancel" on the system's "Connect to DIRECT-…?" prompt.</summary>
        PromptDenied,

        /// <summary>The OS never answers (until the join is given up).</summary>
        NeverAnswers,
    }
}
