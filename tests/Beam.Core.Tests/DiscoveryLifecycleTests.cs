using System.Net;
using Beam.Core.Discovery;

namespace Beam.Core.Tests;

/// <summary>
/// Discovery across network changes: multicast memberships per adapter, unicast targets it added itself, and addresses
/// that stop answering. Adapters are stand-ins (the machine's own aren't changed); the bookkeeping is the real code.
/// </summary>
public class DiscoveryLifecycleTests
{
    private const int Wlan0 = 5;

    private sealed class Harness : IDisposable
    {
        public readonly List<(int Index, IPAddress Address, bool Join)> Calls = new();
        public List<DiscoveryService.LocalInterface> Adapters = new();
        public long Now = 1_000_000;
        public readonly DiscoveryService Service;

        public Harness(bool start = true, bool multicast = true)
        {
            Service = new DiscoveryService(new DiscoveryOptions
            {
                Port = 0,
                UseMulticastAndBroadcast = multicast,
                AnnounceInterval = TimeSpan.FromHours(1), // the test drives every step
                DeviceTimeout = TimeSpan.FromSeconds(16),
            }, () => new LocalAnnouncement("self", "Self", 1, new string('a', 64), DeviceKinds.Desktop))
            {
                InterfacesForTests = () => Adapters.ToList(),
                MembershipForTests = (nic, join) =>
                {
                    lock (Calls) Calls.Add((nic.Index, nic.Address, join));
                },
                Clock = () => Now,
            };
            if (start) Service.Start();
        }

        public void On(params (int Index, string Address)[] adapters)
        {
            Adapters = adapters.Select(a => new DiscoveryService.LocalInterface(IPAddress.Parse(a.Address), null, a.Index)).ToList();
            Service.RefreshInterfacesForTests();
        }

        public int Joins(int index) => Calls.Count(c => c.Index == index && c.Join);

        public int Leaves(int index) => Calls.Count(c => c.Index == index && !c.Join);

        public void Dispose() => Service.Dispose();
    }

    private static byte[] Announce(string id, string name, int port = 47821) => new DiscoveryPacket
    {
        Type = DiscoveryPacket.TypeAnnounce,
        Id = id,
        Name = name,
        Port = port,
        Fingerprint = new string('b', 64),
        Platform = "windows",
    }.Serialize();

    [Fact]
    public void AnAdapterThatChangesItsAddressStaysInTheGroupInsteadOfJoiningTwice()
    {
        // The phone's wlan0: home Wi-Fi, then the PC's direct network (same adapter, new address), then back.
        using var h = new Harness();
        h.On((Wlan0, "192.168.0.110"));
        h.On((Wlan0, "192.168.137.129"));
        h.On((Wlan0, "192.168.137.150"));
        h.On((Wlan0, "192.168.0.110"));

        Assert.Equal(1, h.Joins(Wlan0)); // before: once per new address ("Address already in use" from the second on)
        Assert.Equal(0, h.Leaves(Wlan0));
        Assert.Equal(new[] { "if" + Wlan0 }, h.Service.MembershipsForTests);
    }

    [Fact]
    public void AnAdapterThatGoesAwayLeavesTheGroupAndJoinsAgainWhenItComesBack()
    {
        using var h = new Harness();
        h.On((Wlan0, "192.168.0.110"), (7, "192.168.137.1"));
        h.On((Wlan0, "192.168.0.110")); // the hosted network's virtual adapter went down
        h.On((Wlan0, "192.168.0.110"), (7, "192.168.137.1")); // and up again

        Assert.Equal(2, h.Joins(7));
        Assert.Equal(1, h.Leaves(7));
        Assert.Equal(1, h.Joins(Wlan0));
        Assert.Equal(2, h.Service.MembershipsForTests.Count);
    }

    [Fact]
    public async Task RefreshesRunningAtTheSameTimeJoinOnlyOnce()
    {
        using var h = new Harness();
        h.Adapters = new() { new(IPAddress.Parse("192.168.0.110"), null, Wlan0) };
        await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(h.Service.RefreshInterfacesForTests)));

        Assert.Equal(1, h.Joins(Wlan0));
    }

    [Fact]
    public void ANewSocketJoinsAgainAndRepeatedReopeningStaysConsistent()
    {
        using var h = new Harness();
        h.On((Wlan0, "192.168.0.110"));
        for (var i = 0; i < 3; i++)
        {
            h.Service.ReopenForTests(); // memberships belonged to the old socket
            Assert.Empty(h.Service.MembershipsForTests);
            h.On((Wlan0, "192.168.0.110"));
        }

        Assert.Equal(4, h.Joins(Wlan0));
        Assert.Single(h.Service.MembershipsForTests);
    }

    [Fact]
    public void TheHostOfADirectNetworkIsAskedOnlyWhileThisDeviceIsOnIt()
    {
        using var h = new Harness();
        h.Service.AddUnicastTarget(IPAddress.Parse("10.0.0.9")); // added by the user: never removed by this
        h.On((Wlan0, "192.168.137.129")); // joined a PC's direct network
        var host = new IPEndPoint(IPAddress.Parse("192.168.137.1"), h.Service.LocalPort == 0 ? 0 : AppInfo.DiscoveryPort);
        Assert.Contains(h.Service.UnicastTargetsForTests, t => t.Address.Equals(host.Address));

        h.On((Wlan0, "192.168.0.110")); // back on the home Wi-Fi
        Assert.DoesNotContain(h.Service.UnicastTargetsForTests, t => t.Address.Equals(host.Address)); // no "Network is unreachable" every few seconds
        Assert.Contains(h.Service.UnicastTargetsForTests, t => t.Address.Equals(IPAddress.Parse("10.0.0.9")));

        h.On((Wlan0, "192.168.49.37")); // a phone's group this time
        Assert.Contains(h.Service.UnicastTargetsForTests, t => t.Address.Equals(IPAddress.Parse("192.168.49.1")));
    }

    [Fact]
    public void AnAddressThatStopsAnsweringAgesOutWhileTheDeviceIsStillHeardAtAnother()
    {
        using var h = new Harness(start: false, multicast: false);
        var pc = DirectSessionTokens.NewId();
        h.Service.HandleForTests(Announce(pc, "PC"), new IPEndPoint(IPAddress.Parse("192.168.0.109"), 47820));
        h.Now += 1000;
        h.Service.HandleForTests(Announce(pc, "PC"), new IPEndPoint(IPAddress.Parse("192.168.137.1"), 47820));
        Assert.Equal(2, h.Service.Find(pc)!.Endpoints.Count);

        // From now on only heard on the direct network (the home Wi-Fi went away).
        for (var i = 0; i < 20; i++)
        {
            h.Now += 1000;
            h.Service.HandleForTests(Announce(pc, "PC"), new IPEndPoint(IPAddress.Parse("192.168.137.1"), 47820));
            h.Service.ExpireForTests();
        }

        var endpoints = h.Service.Find(pc)!.Endpoints;
        Assert.Single(endpoints);
        Assert.Equal(IPAddress.Parse("192.168.137.1"), endpoints[0].Address);
    }

    [Fact]
    public void SameNetworkDevicesKeepEveryAddressTheyAreHeardAtAndGoAsBeforeWhenSilent()
    {
        // Regression: a PC on the home network (heard at two addresses, e.g. cable and Wi-Fi) keeps both; a device
        // that goes silent disappears after the same timeout as always.
        using var h = new Harness(start: false, multicast: false);
        var pc = DirectSessionTokens.NewId();
        var quiet = DirectSessionTokens.NewId();
        h.Service.HandleForTests(Announce(quiet, "Quiet"), new IPEndPoint(IPAddress.Parse("192.168.0.50"), 47820));
        for (var i = 0; i < 30; i++)
        {
            h.Now += 1000;
            h.Service.HandleForTests(Announce(pc, "PC"), new IPEndPoint(IPAddress.Parse("192.168.0.109"), 47820));
            h.Service.HandleForTests(Announce(pc, "PC"), new IPEndPoint(IPAddress.Parse("192.168.0.111"), 47820));
            h.Service.ExpireForTests();
            if (i == 10) Assert.NotNull(h.Service.Find(quiet)); // not before its time
        }

        Assert.Equal(2, h.Service.Find(pc)!.Endpoints.Count);
        Assert.Null(h.Service.Find(quiet));
    }
}

internal static class DirectSessionTokens
{
    public static string NewId() => Guid.NewGuid().ToString("N");
}
