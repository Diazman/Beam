using Beam.Core.Localization;

namespace Beam.Core.Licensing;

/// <summary>
/// Capabilities that depend on the edition. UI and engine code ask <see cref="IEditionPolicy"/>
/// instead of hard-coding availability.
/// </summary>
public enum Feature
{
    SendFiles,
    ReceiveFiles,
    SendFolders,
    ResumeTransfers,
    TrustedDevices,
    MultipleSimultaneousTransfers,
    ManualConnection,

    /// <summary>Pro: choose several computers and send to all of them at once.</summary>
    SendToSeveralDevices,

    /// <summary>Pro: sending is not limited to <see cref="FreeLimits.MaxSendBytesPerSecond"/>.</summary>
    FullSpeed,

    /// <summary>Pro: no daily limit on the number of sends.</summary>
    UnlimitedSends,
}

/// <summary>What the free edition allows. Receiving is never limited.</summary>
public static class FreeLimits
{
    /// <summary>Sending speed of the free edition: 5 MB/s (about 42 Mbit/s).</summary>
    public const long MaxSendBytesPerSecond = 5L * 1024 * 1024;

    /// <summary>Sends per day in the free edition (each computer sent to counts as one).</summary>
    public const int SendsPerDay = 10;
}

public interface IEditionPolicy
{
    string EditionName { get; }

    bool IsPro { get; }

    /// <summary>True while all features are free for everyone.</summary>
    bool IsLaunchPeriod { get; }

    bool IsEnabled(Feature feature);

    /// <summary>Raised (on any thread) when the edition changes, e.g. after buying Pro.</summary>
    event Action? Changed;
}

/// <summary>
/// The current edition. Starts as Free; the app switches it to Pro when the store reports
/// that the user owns the Pro upgrade.
/// </summary>
public sealed class Edition : IEditionPolicy
{
    /// <summary>
    /// While Beam is new, every feature is free and unlimited (docs/PLAN.md, phase A). Set to false in the
    /// release that switches to freemium (PLAN step 6.7); people who claimed Pro meanwhile keep it.
    /// </summary>
    public const bool LaunchPeriodActive = true;

    private volatile bool _isPro;

    public Edition(bool isPro = false, bool launchPeriod = LaunchPeriodActive)
    {
        _isPro = isPro;
        IsLaunchPeriod = launchPeriod;
    }

    /// <summary>True while all features are free for everyone (see <see cref="LaunchPeriodActive"/>).</summary>
    public bool IsLaunchPeriod { get; }

    public event Action? Changed;

    public string EditionName => _isPro ? "Pro" : L.T("Free");

    public bool IsPro => _isPro;

    public static bool IsProFeature(Feature feature) =>
        feature is Feature.SendToSeveralDevices or Feature.FullSpeed or Feature.UnlimitedSends;

    public bool IsEnabled(Feature feature) => _isPro || IsLaunchPeriod || !IsProFeature(feature);

    public void SetPro(bool isPro)
    {
        if (_isPro == isPro) return;
        _isPro = isPro;
        Changed?.Invoke();
    }
}
