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

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        AndroidHost.RequestPermissions(this);
        if (savedInstanceState == null) AndroidHost.Share(SharedItems.From(Intent));
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
