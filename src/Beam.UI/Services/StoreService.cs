using Beam.Core;
using Beam.Core.Diagnostics;

namespace Beam.App.Services;

/// <summary>The app store Beam was installed from (the Microsoft Store), faked in tests.</summary>
public interface IStoreService
{
    /// <summary>False for builds that weren't installed from a store (e.g. the GitHub downloads).</summary>
    bool CanRequestReview { get; }

    /// <summary>Shows the store's own "Rate and review" dialog (it decides whether to show anything).</summary>
    Task RequestReviewAsync();
}

/// <summary>Builds outside a store.</summary>
public sealed class UnavailableStoreService : IStoreService
{
    public bool CanRequestReview => false;

    public Task RequestReviewAsync() => Task.CompletedTask;
}

/// <summary>Asks for a store rating once, after Beam has worked a few times.</summary>
public sealed class RatingService
{
    /// <summary>Completed transfers before Beam asks for a rating.</summary>
    public const int TransfersBeforeRatingPrompt = 3;

    private readonly BeamNode _node;
    private readonly IStoreService _store;

    public RatingService(BeamNode node, IStoreService store)
    {
        _node = node;
        _store = store;
    }

    /// <summary>
    /// Asks once, after a few successful transfers — a moment when the app just worked. Only in store builds
    /// (the GitHub builds can't open a store review).
    /// </summary>
    public async Task<bool> MaybeAskForRatingAsync()
    {
        var settings = _node.Settings.Current;
        if (!_store.CanRequestReview || settings.RatingRequested || settings.CompletedTransfers < TransfersBeforeRatingPrompt) return false;
        _node.Settings.Update(s => s.RatingRequested = true);
        try
        {
            await _store.RequestReviewAsync();
        }
        catch (Exception ex)
        {
            Log.Warn("Could not show the rating prompt", ex);
        }

        return true;
    }
}
