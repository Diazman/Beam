using Beam.Core.Diagnostics;

namespace Beam.App.Platform;

/// <summary>
/// "Start with Windows" for the Store (MSIX) build, which uses the package's StartupTask
/// instead of the registry Run key (MSIX virtualises registry writes).
/// </summary>
internal static class PackagedStartup
{
    public static bool IsEnabled()
    {
#if WINDOWS_WINRT
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 14393)) return false;
        try
        {
            return Task.Run(async () =>
            {
                var task = await Windows.ApplicationModel.StartupTask.GetAsync(PackageInfo.StartupTaskId);
                return task.State is Windows.ApplicationModel.StartupTaskState.Enabled
                    or Windows.ApplicationModel.StartupTaskState.EnabledByPolicy;
            }).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Log.Warn("Could not read startup task state", ex);
        }
#endif
        return false;
    }

    public static void SetEnabled(bool enabled)
    {
#if WINDOWS_WINRT
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 14393)) return;
        try
        {
            Task.Run(async () =>
            {
                var task = await Windows.ApplicationModel.StartupTask.GetAsync(PackageInfo.StartupTaskId);
                if (!enabled)
                {
                    task.Disable();
                    return;
                }

                var state = await task.RequestEnableAsync();
                if (state == Windows.ApplicationModel.StartupTaskState.DisabledByUser)
                    Log.Info("Start with Windows is turned off in Windows Settings > Apps > Startup");
            }).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Log.Warn("Could not change startup task", ex);
        }
#else
        _ = enabled;
#endif
    }
}
