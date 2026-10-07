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
        App.StartupCommandLine = CommandLine.Parse(args);
        App.Instance = instance;

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
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
