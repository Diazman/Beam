using System.Runtime.Versioning;
using Beam.App.Services;
using Beam.Core.Diagnostics;
#if WINDOWS_WINRT
using Windows.Services.Store;
#endif

namespace Beam.App.Platform;

/// <summary>
/// Sells and checks the "Beam Pro" add-on through the Microsoft Store. The add-on is a Durable
/// add-on whose Product ID in Partner Center is <see cref="ProProductId"/>; nothing else needs
/// to be configured in code.
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
internal sealed class WindowsStoreService : IStoreService
{
    /// <summary>The add-on's Product ID ("In-app offer token") as entered in Partner Center.</summary>
    public const string ProProductId = "BeamPro";

    private readonly Func<IntPtr> _windowHandle;
#if WINDOWS_WINRT
    private StoreContext? _context;
#endif

    public WindowsStoreService(Func<IntPtr> windowHandle)
    {
        _windowHandle = windowHandle;
    }

#if WINDOWS_WINRT
    public event Action? LicenseChanged;
#else
    public event Action? LicenseChanged
    {
        add { }
        remove { }
    }
#endif

    public bool CanPurchase => true;

    /// <summary>Only Store installs (packaged) can use the Store APIs.</summary>
    public static IStoreService Create(Func<IntPtr> windowHandle) =>
#if WINDOWS_WINRT
        PackageInfo.IsPackaged ? new WindowsStoreService(windowHandle) : new UnavailableStoreService();
#else
        new UnavailableStoreService();
#endif

#if WINDOWS_WINRT
    public async Task<bool?> OwnsProAsync()
    {
        var license = await Context().GetAppLicenseAsync();
        if (license == null) return null;
        foreach (var addOn in license.AddOnLicenses.Values)
        {
            if (string.Equals(addOn.InAppOfferToken, ProProductId, StringComparison.OrdinalIgnoreCase) && addOn.IsActive) return true;
        }

        return false;
    }

    public async Task<string?> GetProPriceAsync() => (await FindProductAsync())?.Price?.FormattedPrice;

    public async Task<PurchaseOutcome> PurchaseProAsync()
    {
        var product = await FindProductAsync();
        if (product == null) return PurchaseOutcome.NotAvailable;
        if (product.IsInUserCollection) return PurchaseOutcome.AlreadyOwned;
        var result = await product.RequestPurchaseAsync();
        if (result.ExtendedError != null) Log.Warn($"Store purchase status {result.Status}: {result.ExtendedError.Message}");
        return result.Status switch
        {
            StorePurchaseStatus.Succeeded => PurchaseOutcome.Purchased,
            StorePurchaseStatus.AlreadyPurchased => PurchaseOutcome.AlreadyOwned,
            StorePurchaseStatus.NotPurchased => PurchaseOutcome.Cancelled,
            _ => PurchaseOutcome.Failed,
        };
    }

    private async Task<StoreProduct?> FindProductAsync()
    {
        var result = await Context().GetAssociatedStoreProductsAsync(new[] { "Durable" });
        if (result.ExtendedError != null) Log.Warn($"Store products unavailable: {result.ExtendedError.Message}");
        return result.Products?.Values.FirstOrDefault(p => string.Equals(p.InAppOfferToken, ProProductId, StringComparison.OrdinalIgnoreCase));
    }

    private StoreContext Context()
    {
        if (_context != null) return _context;
        var context = StoreContext.GetDefault();
        // Desktop apps must tell the Store which window owns its purchase dialog.
        var hwnd = _windowHandle();
        if (hwnd != IntPtr.Zero) WinRT.Interop.InitializeWithWindow.Initialize(context, hwnd);
        context.OfflineLicensesChanged += (_, _) => LicenseChanged?.Invoke();
        return _context = context;
    }
#else
    public Task<bool?> OwnsProAsync() => Task.FromResult<bool?>(null);

    public Task<string?> GetProPriceAsync() => Task.FromResult<string?>(null);

    public Task<PurchaseOutcome> PurchaseProAsync() => Task.FromResult(PurchaseOutcome.NotAvailable);
#endif
}
