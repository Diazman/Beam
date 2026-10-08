using Beam.Core;
using Beam.Core.Diagnostics;
using Beam.Core.Licensing;

namespace Beam.App.Services;

public enum PurchaseOutcome
{
    Purchased,
    AlreadyOwned,
    Cancelled,
    NotAvailable,
    Failed,
}

/// <summary>Where the Pro upgrade is bought (the Microsoft Store), faked in tests.</summary>
public interface IStoreService
{
    /// <summary>False for builds that weren't installed from the Store (e.g. the GitHub downloads).</summary>
    bool CanPurchase { get; }

    /// <summary>True/false when the store answered; null when it couldn't be asked (e.g. offline with no cached license).</summary>
    Task<bool?> OwnsProAsync();

    /// <summary>The localized price, e.g. "₺149,99" or "$4.99", if known.</summary>
    Task<string?> GetProPriceAsync();

    Task<PurchaseOutcome> PurchaseProAsync();

    /// <summary>Shows the store's own "Rate and review" dialog (it decides whether to show anything).</summary>
    Task RequestReviewAsync();

    /// <summary>Raised when the store reports a license change (e.g. bought on another device, or refunded).</summary>
    event Action? LicenseChanged;
}

/// <summary>Builds outside the Store can't sell anything.</summary>
public sealed class UnavailableStoreService : IStoreService
{
    public bool CanPurchase => false;

    public event Action? LicenseChanged
    {
        add { }
        remove { }
    }

    public Task<bool?> OwnsProAsync() => Task.FromResult<bool?>(false);

    public Task<string?> GetProPriceAsync() => Task.FromResult<string?>(null);

    public Task<PurchaseOutcome> PurchaseProAsync() => Task.FromResult(PurchaseOutcome.NotAvailable);

    public Task RequestReviewAsync() => Task.CompletedTask;
}

/// <summary>Keeps the node's edition in sync with what the user owns, and sells the upgrade.</summary>
public sealed class ProService
{
    /// <summary>
    /// Microsoft Store "Store ID" of Beam (Partner Center → App identity), e.g. "9NXXXXXXXXXX".
    /// Used for the "Get Beam Pro in the Microsoft Store" link in builds that can't buy Pro directly.
    /// </summary>
    public const string StoreProductId = "";

    private readonly Edition? _edition;
    private readonly IStoreService _store;

    public ProService(BeamNode node, IStoreService store)
    {
        Node = node;
        _edition = node.Edition as Edition;
        _store = store;
        _store.LicenseChanged += () => _ = RefreshAsync();
    }

    public BeamNode Node { get; }

    public bool IsPro => Node.Edition.IsPro;

    public bool CanPurchase => _store.CanPurchase;

    public string? Price { get; private set; }

    /// <summary>
    /// The Pro add-on currently costs nothing (it is priced Free during the launch period, so early users
    /// can claim it and keep it). Detected from the store's formatted price: "Free", "$0.00", "0,00 ₺"…
    /// </summary>
    public bool PriceIsFree => Price != null && !Price.Any(c => c is >= '1' and <= '9');

    public bool IsLaunchPeriod => Node.Edition.IsLaunchPeriod;

    public static string StorePageUrl => string.IsNullOrEmpty(StoreProductId)
        ? "https://apps.microsoft.com/search?query=Beam%20File%20Transfer"
        : $"ms-windows-store://pdp/?productid={StoreProductId}";

    /// <summary>Asks the store what the user owns. Never downgrades on errors (e.g. offline).</summary>
    public async Task RefreshAsync()
    {
        try
        {
            var owns = await _store.OwnsProAsync();
            if (owns != null) _edition?.SetPro(owns.Value);
            if (CanPurchase && Price == null) Price = await _store.GetProPriceAsync();
        }
        catch (Exception ex)
        {
            Log.Warn("Could not check the Pro license", ex);
        }
    }

    /// <summary>Completed transfers before Beam asks for a rating.</summary>
    public const int TransfersBeforeRatingPrompt = 3;

    /// <summary>
    /// Asks for a store rating once, after a few successful transfers — a moment when the app just worked.
    /// Only in the Store version (the GitHub builds can't open a Store review).
    /// </summary>
    public async Task<bool> MaybeAskForRatingAsync()
    {
        var settings = Node.Settings.Current;
        if (!CanPurchase || settings.RatingRequested || settings.CompletedTransfers < TransfersBeforeRatingPrompt) return false;
        Node.Settings.Update(s => s.RatingRequested = true);
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

    public async Task<PurchaseOutcome> PurchaseAsync()
    {
        PurchaseOutcome outcome;
        try
        {
            outcome = await _store.PurchaseProAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Purchase failed", ex);
            outcome = PurchaseOutcome.Failed;
        }

        Log.Info($"Pro purchase: {outcome}");
        if (outcome is PurchaseOutcome.Purchased or PurchaseOutcome.AlreadyOwned) _edition?.SetPro(true);
        return outcome;
    }
}
