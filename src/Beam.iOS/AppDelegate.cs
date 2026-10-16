using Avalonia;
using Avalonia.iOS;
using Foundation;
using UIKit;

namespace Beam.iOS;

[Register("AppDelegate")]
public partial class AppDelegate : AvaloniaAppDelegate<App>
{
    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder) =>
        base.CustomizeAppBuilder(builder).WithInterFont();
}

public static class Program
{
    private static void Main(string[] args)
    {
        // A crash leaves its reason in Beam's log.
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Beam.Core.Diagnostics.Log.Error("Unhandled exception", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Beam.Core.Diagnostics.Log.Warn("Unobserved task exception", e.Exception);
            e.SetObserved();
        };
        UIApplication.Main(args, null, typeof(AppDelegate));
    }
}
