using System.Runtime.Versioning;
using Beam.App.Services;
using Beam.Core.Diagnostics;
#if WINDOWS_WINRT
using Windows.Services.Store;
#endif

namespace Beam.App.Platform;

/// <summary>The Microsoft Store, for its "Rate and review" dialog.</summary>
[SupportedOSPlatform("windows10.0.10240")]
internal sealed class WindowsStoreService : IStoreService
{
    private readonly Func<IntPtr> _windowHandle;
#if WINDOWS_WINRT
    private StoreContext? _context;
#endif

    private WindowsStoreService(Func<IntPtr> windowHandle) => _windowHandle = windowHandle;

    public bool CanRequestReview => true;

    /// <summary>Only Store installs (packaged) can use the Store APIs.</summary>
    public static IStoreService Create(Func<IntPtr> windowHandle) =>
#if WINDOWS_WINRT
        PackageInfo.IsPackaged ? new WindowsStoreService(windowHandle) : new UnavailableStoreService();
#else
        new UnavailableStoreService();
#endif

#if WINDOWS_WINRT
    [SupportedOSPlatform("windows10.0.17763")]
    public async Task RequestReviewAsync()
    {
        var result = await Context().RequestRateAndReviewAppAsync();
        Log.Info($"Rating prompt: {result.Status}");
    }

    [SupportedOSPlatform("windows10.0.14393")]
    private StoreContext Context()
    {
        if (_context != null) return _context;
        var context = StoreContext.GetDefault();
        // Desktop apps must tell the Store which window owns its dialogs.
        var hwnd = _windowHandle();
        if (hwnd != IntPtr.Zero) WinRT.Interop.InitializeWithWindow.Initialize(context, hwnd);
        return _context = context;
    }
#else
    public Task RequestReviewAsync() => Task.CompletedTask;
#endif
}
