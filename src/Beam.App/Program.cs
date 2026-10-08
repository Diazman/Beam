using Avalonia;
using Avalonia.Media;
using Avalonia.Threading;
using Beam.App.Services;
using Beam.Core.Diagnostics;
using Beam.Core.Storage;

namespace Beam.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var paths = AppDataPaths.Default();
        if (Environment.GetEnvironmentVariable(AppDataPaths.OverrideVariable) == null && Platform.PackageInfo.LocalDataFolder is { } packaged)
            paths = new AppDataPaths(packaged); // Store install: use the package's own data folder
        // Started from Windows' Share dialog (Store version): treat the shared items like "--send <paths>".
        var shared = OperatingSystem.IsWindows() ? Platform.PackageInfo.TakeSharedItems() : Array.Empty<string>();
        if (shared.Count > 0) args = new[] { "--send" }.Concat(shared).ToArray();

        var key = SingleInstance.KeyFor(paths.Root);
        using var instance = SingleInstance.TryAcquire(key);
        if (instance == null)
        {
            // Beam is already running: hand over (bring window forward / add files) and exit.
            SingleInstance.SendToPrimary(key, args);
            return 0;
        }

        paths.EnsureCreated();
        Log.Initialize(paths.LogDirectory);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("Unhandled exception", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Warn("Unobserved task exception", e.Exception);
            e.SetObserved();
        };

        App.DataPaths = paths;
        var commandLine = CommandLine.Parse(args);
        if (Platform.PackageInfo.LaunchedByStartupTask()) commandLine = commandLine with { StartMinimized = true };
        App.StartupCommandLine = commandLine;
        App.Instance = instance;

        try
        {
            var builder = BuildAvaloniaApp();
            if (App.StartupCommandLine.SoftwareRendering || Environment.GetEnvironmentVariable("BEAM_SOFTWARE_RENDERING") == "1")
            {
                // Troubleshooting switch for PCs whose graphics drivers render a blank/black window.
                Log.Info("Using software rendering");
                builder = builder.With(new Win32PlatformOptions { RenderingMode = new[] { Win32RenderingMode.Software } })
                    .With(new X11PlatformOptions { RenderingMode = new[] { X11RenderingMode.Software } });
            }

            builder.StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
            return 0;
        }
        catch (Exception ex)
        {
            Log.Error("Fatal error", ex);
            throw;
        }
    }

    /// <summary>Also used by the visual designer.</summary>
    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace()
            .AfterSetup(_ => Dispatcher.UIThread.UnhandledException += (_, e) =>
            {
                Log.Error("Unhandled UI exception", e.Exception);
                e.Handled = true;
            });

        // Windows uses Segoe UI; elsewhere bundle Inter so the app looks the same everywhere.
        if (!OperatingSystem.IsWindows())
            builder = builder.WithInterFont().With(new FontManagerOptions { DefaultFamilyName = "fonts:Inter#Inter" });
        return builder;
    }
}
