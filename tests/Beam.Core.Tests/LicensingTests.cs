using System.Diagnostics;
using Beam.Core.Licensing;
using Beam.Core.Transfer;
using Beam.Core.Util;

namespace Beam.Core.Tests;

public class LicensingTests
{
    [Fact]
    public void FreeEditionLocksOnlyProFeatures()
    {
        var edition = new Edition(launchPeriod: false);
        Assert.Equal("Free", edition.EditionName);
        Assert.True(edition.IsEnabled(Feature.SendFiles));
        Assert.True(edition.IsEnabled(Feature.ReceiveFiles));
        Assert.False(edition.IsEnabled(Feature.SendToSeveralDevices));
        Assert.False(edition.IsEnabled(Feature.FullSpeed));
        Assert.False(edition.IsEnabled(Feature.UnlimitedSends));

        var changes = 0;
        edition.Changed += () => changes++;
        edition.SetPro(true);
        edition.SetPro(true);
        Assert.Equal(1, changes);
        Assert.Equal("Pro", edition.EditionName);
        Assert.True(Enum.GetValues<Feature>().All(edition.IsEnabled));
    }

    [Fact]
    public void LaunchPeriodMakesEverythingFreeWithoutClaimingPro()
    {
        var edition = new Edition(launchPeriod: true);
        Assert.False(edition.IsPro);
        Assert.True(Enum.GetValues<Feature>().All(edition.IsEnabled));

        using var dir = new TempDir();
        var quota = new SendQuota(dir.Combine("usage.json"), edition);
        for (var i = 0; i < FreeLimits.SendsPerDay * 3; i++) Assert.True(quota.TryUse());
        Assert.Null(quota.RemainingToday);
    }

    [Fact]
    public void DailyQuotaCountsResetsAndPersists()
    {
        using var dir = new TempDir();
        var path = dir.Combine("usage.json");
        var now = new DateTime(2026, 10, 7, 23, 0, 0);
        var edition = new Edition(launchPeriod: false);
        var quota = new SendQuota(path, edition, () => now);

        for (var i = 0; i < FreeLimits.SendsPerDay; i++) Assert.True(quota.TryUse());
        Assert.Equal(0, quota.RemainingToday);
        Assert.False(quota.CanSend());
        Assert.False(quota.TryUse());
        Assert.Equal(FreeLimits.SendsPerDay, quota.UsedToday);

        // Survives a restart.
        var reloaded = new SendQuota(path, edition, () => now);
        Assert.False(reloaded.CanSend());

        // A new day starts fresh.
        now = now.AddHours(2);
        Assert.Equal(FreeLimits.SendsPerDay, reloaded.RemainingToday);
        Assert.True(reloaded.TryUse());
        Assert.Equal(1, reloaded.UsedToday);

        // Pro is unlimited.
        now = now.AddHours(-2);
        edition.SetPro(true);
        Assert.Null(quota.RemainingToday);
        Assert.True(quota.TryUse());
    }

    [Fact]
    public async Task RateLimiterPacesAndCanBeLifted()
    {
        long rate = 8 * 1024 * 1024;
        var limiter = new RateLimiter(() => rate);
        var clock = Stopwatch.StartNew();
        for (var i = 0; i < 16; i++) await limiter.WaitAsync(256 * 1024, CancellationToken.None); // 4 MB at 8 MB/s
        Assert.InRange(clock.Elapsed.TotalSeconds, 0.2, 2.0);

        rate = 0;
        clock.Restart();
        for (var i = 0; i < 1000; i++) await limiter.WaitAsync(256 * 1024, CancellationToken.None);
        Assert.True(clock.Elapsed < TimeSpan.FromMilliseconds(500));
    }

    [Fact]
    public async Task FreeEditionSendsAtLimitedSpeedAndProAtFullSpeed()
    {
        await using var receiver = new TestNode("Receiver");
        await using var free = new TestNode("Free sender", pro: false);
        var size = 8L * 1024 * 1024; // 1.6 s at the free limit
        var file = free.CreateFile("big.bin", size);

        var clock = Stopwatch.StartNew();
        var session = free.Node.Send(receiver.AsDevice(), new[] { file });
        await Wait.ForFinishAsync(session);
        var freeTime = clock.Elapsed;
        Assert.Equal(TransferState.Completed, session.State);
        Assert.True(freeTime >= TimeSpan.FromSeconds(1.1), $"free send took {freeTime}");

        // Upgrading lifts the limit straight away.
        free.Edition.SetPro(true);
        clock.Restart();
        session = free.Node.Send(receiver.AsDevice(), new[] { file });
        await Wait.ForFinishAsync(session);
        Assert.Equal(TransferState.Completed, session.State);
        Assert.True(clock.Elapsed < freeTime, $"pro send took {clock.Elapsed}, free {freeTime}");
    }

    [Fact]
    public async Task FreeEditionStopsAfterDailySends()
    {
        await using var receiver = new TestNode("Receiver");
        await using var free = new TestNode("Free sender", pro: false);
        var file = free.CreateFile("note.txt", 100);
        for (var i = 0; i < FreeLimits.SendsPerDay; i++)
            await Wait.ForFinishAsync(free.Node.Send(receiver.AsDevice(), new[] { file }));

        var ex = Assert.Throws<TransferException>(() => free.Node.Send(receiver.AsDevice(), new[] { file }));
        Assert.Equal(TransferErrorKind.SendLimitReached, ex.Error.Kind);
        Assert.Equal(FreeLimits.SendsPerDay, free.Node.Transfers.Sessions.Count(s => s.Direction == TransferDirection.Send));

        free.Edition.SetPro(true);
        await Wait.ForFinishAsync(free.Node.Send(receiver.AsDevice(), new[] { file }));
    }
}
