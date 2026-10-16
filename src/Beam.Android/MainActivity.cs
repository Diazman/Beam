using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Avalonia;
using Avalonia.Android;

namespace Beam.Droid;

[Activity(
    Label = "Beam",
    Theme = "@style/BeamTheme",
    Icon = "@mipmap/ic_launcher",
    MainLauncher = true,
    LaunchMode = LaunchMode.SingleTask,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode
                           | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
// "Beam" in the Share sheet of the Gallery, Files, the browser and every other app: files of any type, text and links.
[IntentFilter(new[] { Intent.ActionSend, Intent.ActionSendMultiple }, Categories = new[] { Intent.CategoryDefault }, DataMimeType = "*/*")]
public class MainActivity : AvaloniaMainActivity<App>
{
    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder) =>
        base.CustomizeAppBuilder(builder).WithInterFont();

    private static int _crashLogging;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        // A crash leaves its reason in Beam's log (before, the app just closed with nothing written).
        if (Interlocked.Exchange(ref _crashLogging, 1) == 0)
        {
            global::Android.Runtime.AndroidEnvironment.UnhandledExceptionRaiser += (_, e) => Beam.Core.Diagnostics.Log.Error("Unhandled exception", e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (_, e) => Beam.Core.Diagnostics.Log.Error("Unhandled exception", e.ExceptionObject as Exception);
            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                Beam.Core.Diagnostics.Log.Warn("Unobserved task exception", e.Exception);
                e.SetObserved();
            };
        }

        base.OnCreate(savedInstanceState);
        AndroidHost.RequestPermissions(this);
        if (savedInstanceState == null)
        {
            AndroidHost.Share(SharedItems.From(Intent));
            NotificationActions.Run(Intent);
        }
    }

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        AndroidHost.OnPermissionResult(requestCode, grantResults);
    }

    /// <summary>Shared again while Beam is already open (the activity is single-task).</summary>
    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        AndroidHost.Share(SharedItems.From(intent));
        NotificationActions.Run(intent); // a notification was tapped
    }

    protected override void OnResume()
    {
        base.OnResume();
        AndroidHost.SetForeground(true);
    }

    protected override void OnPause()
    {
        AndroidHost.SetForeground(false);
        base.OnPause();
    }
}
