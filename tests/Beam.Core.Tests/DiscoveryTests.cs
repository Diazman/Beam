using System.Net;
using Beam.Core.Discovery;

namespace Beam.Core.Tests;

public class DiscoveryTests
{
    private static void Link(TestNode a, TestNode b)
    {
        a.Node.Discovery.AddUnicastTarget(IPAddress.Loopback, b.Node.Discovery.LocalPort);
        b.Node.Discovery.AddUnicastTarget(IPAddress.Loopback, a.Node.Discovery.LocalPort);
    }

    [Fact]
    public async Task DevicesFindEachOtherAndSeeNameChanges()
    {
        await using var a = new TestNode("Alpha", startDiscovery: true);
        await using var b = new TestNode("Bravo", startDiscovery: true);
        Link(a, b);

        await Wait.UntilAsync(() => a.Node.Discovery.Devices.Any(d => d.Name == "Bravo") && b.Node.Discovery.Devices.Any(d => d.Name == "Alpha"));
        var bravo = a.Node.Discovery.Devices.Single();
        Assert.Equal(b.Node.Identity.DeviceId, bravo.Id);
        Assert.Equal(b.Node.Identity.Fingerprint, bravo.Fingerprint);
        Assert.Equal(b.Node.Transfers.Port, bravo.Endpoints[0].Port);

        b.Node.Settings.Update(s => s.DeviceName = "Bravo Laptop");
        await Wait.UntilAsync(() => a.Node.Discovery.Devices.Any(d => d.Name == "Bravo Laptop"));

        // The discovered device can be used directly to send files.
        var file = a.CreateFile("hello.txt", 1234);
        var session = a.Node.Send(a.Node.Discovery.Devices.Single(), new[] { file });
        await Wait.ForFinishAsync(session);
        Assert.Equal(Transfer.TransferState.Completed, session.State);
    }

    [Fact]
    public async Task DeviceDisappearsWhenClosedOrHidden()
    {
        await using var a = new TestNode("Alpha", startDiscovery: true);
        var b = new TestNode("Bravo", startDiscovery: true);
        await using var c = new TestNode("Charlie", startDiscovery: true);
        Link(a, b);
        Link(a, c);
        await Wait.UntilAsync(() => a.Node.Discovery.Devices.Count == 2);

        var lost = new List<string>();
        a.Node.Discovery.DeviceLost += (_, e) => { lock (lost) lost.Add(e.Device.Name); };

        // Closing sends a goodbye: removal is immediate.
        await b.DisposeAsync();
        await Wait.UntilAsync(() => a.Node.Discovery.Devices.Count == 1, TimeSpan.FromSeconds(1.5));

        // Hiding stops announcements.
        c.Node.Settings.Update(s => s.Discoverable = false);
        await Wait.UntilAsync(() => a.Node.Discovery.Devices.Count == 0, TimeSpan.FromSeconds(5));
        Assert.Contains("Bravo", lost);
        Assert.Contains("Charlie", lost);
    }

    [Fact]
    public async Task SilentDeviceExpires()
    {
        await using var a = new TestNode("Alpha", startDiscovery: true);
        var b = new TestNode("Bravo", startDiscovery: true);
        Link(a, b);
        await Wait.UntilAsync(() => a.Node.Discovery.Devices.Count == 1);

        // Simulate a crash/unplugged cable: stop answering without saying goodbye.
        b.Node.Discovery.Discoverable = false; // sends bye...
        await Wait.UntilAsync(() => a.Node.Discovery.Devices.Count == 0);
        b.Node.Discovery.Discoverable = true;
        await Wait.UntilAsync(() => a.Node.Discovery.Devices.Count == 1);
        a.Node.Discovery.RemoveUnicastTarget(IPAddress.Loopback);
        b.Node.Discovery.RemoveUnicastTarget(IPAddress.Loopback);
        await Wait.UntilAsync(() => a.Node.Discovery.Devices.Count == 0, TimeSpan.FromSeconds(6), "device should expire after the timeout");
        await b.DisposeAsync();
    }

    [Fact]
    public async Task ManualAddressConnectsAndIsRemembered()
    {
        await using var a = new TestNode("Alpha", startDiscovery: true);
        await using var b = new TestNode("Bravo", startDiscovery: true);

        var device = await a.Node.AddDeviceByAddressAsync($"127.0.0.1:{b.Node.Transfers.Port}", CancellationToken.None);

        Assert.Equal("Bravo", device.Name);
        Assert.Equal(b.Node.Identity.Fingerprint, device.Fingerprint);
        Assert.Contains(a.Node.Discovery.Devices, d => d.Id == b.Node.Identity.DeviceId);
        Assert.Contains($"127.0.0.1:{b.Node.Transfers.Port}", a.Node.Settings.Current.ManualAddresses);
    }

    [Fact]
    public async Task ManualAddressErrorsAreFriendly()
    {
        await using var a = new TestNode("Alpha");
        var ex = await Assert.ThrowsAsync<Transfer.TransferException>(() => a.Node.AddDeviceByAddressAsync("127.0.0.1:1", CancellationToken.None));
        Assert.StartsWith("Couldn't connect to 127.0.0.1:1", ex.Error.Message);

        ex = await Assert.ThrowsAsync<Transfer.TransferException>(() => a.Node.AddDeviceByAddressAsync("  ", CancellationToken.None));
        Assert.Contains("address", ex.Error.Message);

        ex = await Assert.ThrowsAsync<Transfer.TransferException>(() =>
            a.Node.AddDeviceByAddressAsync($"127.0.0.1:{a.Node.Transfers.Port}", CancellationToken.None));
        Assert.Contains("this computer", ex.Error.Message);
    }

    [Fact]
    public void RejectsMalformedPackets()
    {
        Assert.Null(DiscoveryPacket.TryParse("not json"u8));
        Assert.Null(DiscoveryPacket.TryParse("""{"magic":"other","id":"x","type":"announce","port":1,"fp":"ab"}"""u8));
        Assert.Null(DiscoveryPacket.TryParse("""{"magic":"beam-discovery","id":"x","type":"announce","port":0,"fp":"ab"}"""u8));
        var ok = DiscoveryPacket.TryParse("""{"magic":"beam-discovery","id":"x","type":"announce","port":5,"fp":"ab","name":"  Diaz's\u0007 PC  "}"""u8);
        Assert.NotNull(ok);
        Assert.Equal("Diaz's PC", ok!.Name);
    }
}
