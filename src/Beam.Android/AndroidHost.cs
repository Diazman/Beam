using Android;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Media;
using Android.Net.Wifi;
using Android.OS;
using Beam.App.Services;
using Beam.App.ViewModels;
using Beam.App.Views;
using Beam.Core;
using Beam.Core.Diagnostics;
using Beam.Core.Discovery;
using Beam.Core.Storage;
using Beam.Core.Transfer;
using AndroidEnvironment = Android.OS.Environment;

namespace Beam.Droid;

/// <summary>Owns the Beam node for the app process and connects it to Android (storage, Wi-Fi, permissions).</summary>
internal static class AndroidHost
{
    private static AppDataPaths? _paths;
    private static BeamNode? _node;
    private static MainViewModel? _viewModel;
    private static MobileView? _view;
    private static WifiManager.MulticastLock? _multicastLock;

    private static Context Context => global::Android.App.Application.Context;

    /// <summary>Settings, identity, history: the app's private storage.</summary>
    public static AppDataPaths Paths => _paths ??= new AppDataPaths(Context.FilesDir!.AbsolutePath);

    /// <summary>Received files go to Downloads/Beam, where the Files and Gallery apps show them.</summary>
    public static string ReceiveFolder =>
        Path.Combine(AndroidEnvironment.GetExternalStoragePublicDirectory(AndroidEnvironment.DirectoryDownloads)!.AbsolutePath, "Beam");

    public static MainViewModel CreateViewModel(MobileView view)
    {
        _view = view;
        if (_viewModel != null) return _viewModel;

        _node = BeamNode.Create(new BeamNodeOptions
        {
            Paths = Paths,
            ExternalFiles = new ContentFiles(Context.ContentResolver!),
            DeviceKind = DeviceKinds.Phone,
        });
        var firstRun = !_node.Settings.Current.FirstRunCompleted;
        _node.Settings.Update(s =>
        {
            s.ReceiveFolder = ReceiveFolder;
            s.CloseToTray = false;
            s.StartWithWindows = false;
            if (firstRun) s.DeviceName = FriendlyDeviceName();
        });
        try
        {
            Directory.CreateDirectory(ReceiveFolder);
        }
        catch (Exception ex)
        {
            Log.Warn("Could not create Downloads/Beam (storage permission not granted yet?)", ex);
        }

        _node.Transfers.SessionFinished += AddToMediaLibrary;
        AcquireMulticastLock();
        _viewModel = new MainViewModel(_node, new AndroidPlatformServices(), view, new UnavailableStoreService());
        _ = _viewModel.InitializeAsync(new CommandLine(false, Array.Empty<string>()));
        return _viewModel;
    }

    public static void SetForeground(bool foreground)
    {
        if (_view != null) _view.IsInForeground = foreground;
    }

    /// <summary>Notifications (Android 13+) and, up to Android 10, saving to Downloads need the user's OK.</summary>
    public static void RequestPermissions(Activity activity)
    {
        var wanted = new List<string>();
        if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu) wanted.Add(Manifest.Permission.PostNotifications);
        if (Build.VERSION.SdkInt <= BuildVersionCodes.Q) wanted.Add(Manifest.Permission.WriteExternalStorage);
        var missing = wanted.Where(p => activity.CheckSelfPermission(p) != Permission.Granted).ToArray();
        if (missing.Length > 0) activity.RequestPermissions(missing, 1);
    }

    /// <summary>The name people gave the phone in Settings ("Diaz's Galaxy"), else the model.</summary>
    private static string FriendlyDeviceName()
    {
        string? name = null;
        try
        {
            name = global::Android.Provider.Settings.Global.GetString(Context.ContentResolver, "device_name");
        }
        catch
        {
            // not available on every phone
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            var maker = Build.Manufacturer ?? "";
            var model = Build.Model ?? "Android";
            name = model.StartsWith(maker, StringComparison.OrdinalIgnoreCase) || maker.Length == 0
                ? model
                : char.ToUpperInvariant(maker[0]) + maker[1..] + " " + model;
        }

        return name.Length > Core.Settings.AppSettings.MaxDeviceNameLength ? name[..Core.Settings.AppSettings.MaxDeviceNameLength] : name;
    }

    /// <summary>Android drops broadcast/multicast packets unless an app holds this lock (needed to find computers).</summary>
    private static void AcquireMulticastLock()
    {
        try
        {
            var wifi = (WifiManager?)Context.GetSystemService(Context.WifiService);
            _multicastLock = wifi?.CreateMulticastLock("Beam discovery");
            _multicastLock?.SetReferenceCounted(false);
            _multicastLock?.Acquire();
        }
        catch (Exception ex)
        {
            Log.Warn("Could not acquire the multicast lock", ex);
        }
    }

    /// <summary>Makes received files show up in Gallery and Files right away.</summary>
    private static void AddToMediaLibrary(TransferSession session)
    {
        if (session.Direction != TransferDirection.Receive || session.SavedRootPaths.Count == 0) return;
        try
        {
            var files = session.SavedRootPaths
                .SelectMany(p => Directory.Exists(p) ? Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories) : new[] { p })
                .Where(File.Exists)
                .ToArray();
            if (files.Length > 0) MediaScannerConnection.ScanFile(Context, files, null, null);
        }
        catch (Exception ex)
        {
            Log.Warn("Could not add received files to the media library", ex);
        }
    }
}
