using System.Net;
using Beam.Core.Direct;

namespace Beam.Core.Tests;

/// <summary>
/// Tests that change which networks count as direct for the whole process (marked networks, the adapter cache): they
/// run alone, so a test running alongside doesn't lose the networks it marked (this failed now and then).
/// </summary>
[CollectionDefinition(nameof(DirectAddressStateTests), DisableParallelization = true)]
public sealed class DirectAddressStateCollection;

[Collection(nameof(DirectAddressStateTests))]
public class DirectAddressStateTests
{
    [Fact]
    public void AMarkedNetworkHasItsHostAndIsForgottenWhenLeft()
    {
        // A phone's hotspot picks its own subnet: the joined network is marked direct, and forgotten on leaving.
        var own = IPAddress.Parse("10.213.77.20");
        var host = IPAddress.Parse("10.213.77.1");
        Assert.Null(DirectAddresses.DirectHostOf(own));
        DirectAddresses.MarkDirectNetwork(own, host);
        try
        {
            Assert.Equal(host, DirectAddresses.DirectHostOf(own));
        }
        finally
        {
            DirectAddresses.ForgetDirectNetworks(hosted: false);
        }

        Assert.Null(DirectAddresses.DirectHostOf(own));
    }

    [Fact]
    public void LocalAdaptersAreReadOnTheFirstCheck()
    {
        // Regression: the adapter cache's "never read" marker overflowed, so direct links were never recognized.
        DirectAddresses.Invalidate();
        Assert.False(DirectAddresses.IsDirect(IPAddress.Parse("203.0.113.9")));
        Assert.True(DirectAddresses.LocalAddressCount > 0);
    }
}
