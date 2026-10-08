using System.Runtime.InteropServices;
using Beam.Core.Diagnostics;

namespace Beam.App.Platform;

/// <summary>
/// Detects whether Beam runs from an MSIX package (Microsoft Store install) or as a classic
/// desktop app (installer / portable exe). A few Windows features work differently in each.
/// </summary>
internal static partial class PackageInfo
{
    /// <summary>Must match the StartupTask TaskId in packaging/msix/AppxManifest.xml.</summary>
    public const string StartupTaskId = "BeamStartup";

    private const int AppModelErrorNoPackage = 15700;

    public static bool IsPackaged { get; } = Detect();

    /// <summary>The package's local data folder (visible to Explorer), or null when not packaged.</summary>
    public static string? LocalDataFolder
    {
        get
        {
#if WINDOWS_WINRT
            if (!IsPackaged || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240)) return null;
            try
            {
                return Windows.Storage.ApplicationData.Current.LocalFolder.Path;
            }
            catch (Exception ex)
            {
                Log.Warn("Could not get the package data folder", ex);
            }
#endif
            return null;
        }
    }

    /// <summary>True when Windows started the packaged app at sign-in (its startup task).</summary>
    public static bool LaunchedByStartupTask()
    {
#if WINDOWS_WINRT
        if (!IsPackaged || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17134)) return false;
        try
        {
            return Windows.ApplicationModel.AppInstance.GetActivatedEventArgs()?.Kind
                   == Windows.ApplicationModel.Activation.ActivationKind.StartupTask;
        }
        catch (Exception ex)
        {
            Log.Warn("Could not read activation kind", ex);
        }
#endif
        return false;
    }

    /// <summary>
    /// When Windows started Beam from its Share dialog, returns the shared files and folders (and tells
    /// Windows the share is handled); otherwise an empty list.
    /// </summary>
    public static IReadOnlyList<string> TakeSharedItems()
    {
#if WINDOWS_WINRT
        if (!IsPackaged || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17134)) return Array.Empty<string>();
        try
        {
            if (Windows.ApplicationModel.AppInstance.GetActivatedEventArgs() is not Windows.ApplicationModel.Activation.ShareTargetActivatedEventArgs share)
                return Array.Empty<string>();
            var operation = share.ShareOperation;
            var paths = new List<string>();
            if (operation.Data.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
            {
                // Off the STA main thread: the WinRT call completes on a worker thread.
                var items = Task.Run(async () => await operation.Data.GetStorageItemsAsync()).GetAwaiter().GetResult();
                paths.AddRange(items.Select(i => i.Path).Where(p => !string.IsNullOrEmpty(p)));
            }

            operation.ReportCompleted();
            Log.Info($"Started from the Share dialog with {paths.Count} item(s)");
            return paths;
        }
        catch (Exception ex)
        {
            Log.Warn("Could not read shared items", ex);
        }
#endif
        return Array.Empty<string>();
    }

    private static unsafe bool Detect()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 2)) return false;
        try
        {
            uint length = 0;
            return GetCurrentPackageFullName(ref length, null) != AppModelErrorNoPackage;
        }
        catch
        {
            return false;
        }
    }

    [LibraryImport("kernel32.dll")]
    private static unsafe partial int GetCurrentPackageFullName(ref uint packageFullNameLength, char* packageFullName);
}
