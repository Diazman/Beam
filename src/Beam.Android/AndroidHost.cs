using Android;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Media;
using Android.Net.Wifi;
using Android.OS;
using Avalonia.Threading;
using Beam.App.Services;
using Beam.App.ViewModels;
using Beam.App.Views;
using Beam.Core;
using Beam.Core.Diagnostics;
using Beam.Core.Discovery;
using Beam.Core.Storage;
using Beam.Core.Transfer;
using AndroidEnvironment = Android.OS.Environment;
using OwnerCheck = Beam.App.Platform.OwnerCheck;

namespace Beam.Droid;

/// <summary>Owns the Beam node for the app process and connects it to Android (storage, Wi-Fi, permissions).</summary>
internal static class AndroidHost
{
    private static AppDataPaths? _paths;
    private static BeamNode? _node;
    private static MainViewModel? _viewModel;
    private static MobileView? _view;
    private static WifiManager.MulticastLock? _multicastLock;
    private static SharedItems? _pendingShare;
    private static WeakReference<Activity>? _activity;
    private static TaskCompletionSource<bool>? _permission;
    private static int _permissionRequest = 100;
    private static TransferKeepAlive? _keepAlive; // held for the life of the app
    private static AndroidDirectLink? _directLink;

    private static Context Context => global::Android.App.Application.Context;

    /// <summary>Settings, identity, history: the app's private storage.</summary>
    public static AppDataPaths Paths => _paths ??= new AppDataPaths(Context.FilesDir!.AbsolutePath);

    /// <summary>Received files go to Downloads/Beam, where the Files and Gallery apps show them.</summary>
    public static string ReceiveFolder =>
        Path.Combine(AndroidEnvironment.GetExternalStoragePublicDirectory(AndroidEnvironment.DirectoryDownloads)!.AbsolutePath, "Beam");

    private static readonly object NodeGate = new();

    /// <summary>
    /// The app's Beam node, created on first use: by the screen, or in the background (a backup check, a notification
    /// button). <paramref name="create"/> false: null when Beam was never opened on this phone. <paramref name="start"/>:
    /// discovery and transfers run (only once Beam was set up on this phone), so a backup can reach the computer.
    /// </summary>
    public static BeamNode? EnsureNode(bool start, bool create)
    {
        lock (NodeGate)
        {
            if (_node == null)
            {
                if (!create && !File.Exists(Paths.SettingsFile)) return null;
                CreateNode();
            }

            if (start && _node!.Settings.Current.FirstRunCompleted) _node.Start();
            return _node;
        }
    }

    private static void CreateNode()
    {
        _directLink = new AndroidDirectLink(Context, () => _node?.Settings.Current.DeviceName ?? "Phone");
        _node = BeamNode.Create(new BeamNodeOptions
        {
            Paths = Paths,
            ExternalFiles = new ContentFiles(Context.ContentResolver!),
            BackupSources = new AndroidBackupSources(Context.ContentResolver!),
            DeviceKind = DeviceKinds.Phone,
            DirectLink = _directLink,
            NearbyRadio = new AndroidNearbyRadio(Context),
        });
        var firstRun = !_node.Settings.Current.FirstRunCompleted;
        _node.Settings.Update(s =>
        {
            // Downloads/Beam unless the user chose another folder in Settings.
            if (string.IsNullOrWhiteSpace(s.ReceiveFolder) || !s.ReceiveFolder.StartsWith('/')) s.ReceiveFolder = ReceiveFolder;
            s.CloseToTray = false;
            s.StartWithWindows = false;
            if (firstRun) s.DeviceName = FriendlyDeviceName();
        });
        try
        {
            Directory.CreateDirectory(_node.Settings.Current.EffectiveReceiveFolder);
        }
        catch (Exception ex)
        {
            Log.Warn("Could not create Downloads/Beam (storage permission not granted yet?)", ex);
        }

        _node.Transfers.SessionFinished += AddToMediaLibrary;
        BackupNotices.Attach(_node, new AndroidPlatformServices()); // also when there is no screen
        _keepAlive = new TransferKeepAlive(Context, _node.Transfers, _directLink);
        AcquireMulticastLock();
    }

    public static MainViewModel CreateViewModel(MobileView view)
    {
        _view = view;
        if (_viewModel != null) return _viewModel;

        var node = EnsureNode(start: false, create: true)!; // the screen starts it after the first-run explanation
        _viewModel = new MainViewModel(node, new AndroidPlatformServices(), view, new UnavailableStoreService());
        var share = Interlocked.Exchange(ref _pendingShare, null);
        _ = _viewModel.InitializeAsync(ToCommandLine(share));
        return _viewModel;
    }

    /// <summary>Puts files or text shared from another app on the Send screen (once Beam's screen exists).</summary>
    public static void Share(SharedItems? share)
    {
        if (share == null) return;
        Log.Info($"Shared to Beam: {share.Files.Count} file(s){(share.Text != null ? ", text" : "")}");
        if (_viewModel is { } viewModel)
            Dispatcher.UIThread.Post(() => viewModel.HandleCommandLine(ToCommandLine(share)));
        else
            _pendingShare = share;
    }

    private static CommandLine ToCommandLine(SharedItems? share) =>
        new(false, share?.Files ?? Array.Empty<string>(), SendText: share?.Text);

    public static void SetForeground(bool foreground)
    {
        if (_view != null) _view.IsInForeground = foreground;
        if (foreground) _keepAlive?.Retry();
    }

    /// <summary>
    /// Asks the user for one permission (e.g. Nearby devices for direct connections) and waits for the answer.
    /// False when it was refused or there is no screen to ask on.
    /// </summary>
    public static Task<bool> RequestPermissionAsync(string permission)
    {
        Activity? activity = null;
        if (_activity?.TryGetTarget(out activity) != true || activity == null) return Task.FromResult(false);
        if (activity.CheckSelfPermission(permission) == Permission.Granted) return Task.FromResult(true);
        var waiting = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Interlocked.Exchange(ref _permission, waiting)?.TrySetResult(false);
        var code = Interlocked.Increment(ref _permissionRequest);
        activity.RunOnUiThread(() => activity.RequestPermissions(new[] { permission }, code));
        return waiting.Task;
    }

    /// <summary>Asks for several permissions at once (one system prompt when they form one group, like Nearby devices).</summary>
    public static Task<bool> RequestPermissionsAsync(params string[] permissions)
    {
        Activity? activity = null;
        if (_activity?.TryGetTarget(out activity) != true || activity == null) return Task.FromResult(false);
        var missing = permissions.Where(p => activity.CheckSelfPermission(p) != Permission.Granted).ToArray();
        if (missing.Length == 0) return Task.FromResult(true);
        var waiting = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Interlocked.Exchange(ref _permission, waiting)?.TrySetResult(false);
        var code = Interlocked.Increment(ref _permissionRequest);
        activity.RunOnUiThread(() => activity.RequestPermissions(missing, code));
        return waiting.Task;
    }

    private static TaskCompletionSource<bool>? _confirmation;
    private const int ConfirmCredentialRequest = 90;

    /// <summary>
    /// The phone's own lock confirms the owner is holding it: fingerprint, face, PIN, pattern or password (Android's
    /// prompt, as banking apps use). Android 9 and older show the lock screen's "confirm your PIN" page instead.
    /// </summary>
    public static async Task<OwnerCheck> ConfirmOwnerAsync(string title, string reason)
    {
        var keyguard = (KeyguardManager?)Context.GetSystemService(Context.KeyguardService);
        if (keyguard is not { IsDeviceSecure: true }) return OwnerCheck.NoScreenLock;
        var activity = CurrentActivity;
        if (activity == null) return OwnerCheck.Refused;
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Interlocked.Exchange(ref _confirmation, done)?.TrySetResult(false);
        try
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(29))
            {
                var builder = new global::Android.Hardware.Biometrics.BiometricPrompt.Builder(activity).SetTitle(title).SetDescription(reason);
                if (OperatingSystem.IsAndroidVersionAtLeast(30))
                    builder.SetAllowedAuthenticators(global::Android.Hardware.Biometrics.BiometricManagerAuthenticators.BiometricWeak
                                                     | global::Android.Hardware.Biometrics.BiometricManagerAuthenticators.DeviceCredential);
                else
                    builder.SetDeviceCredentialAllowed(true);
                var prompt = builder.Build();
                activity.RunOnUiThread(() => prompt.Authenticate(new CancellationSignal(), activity.MainExecutor!, new OwnerCallback(done)));
            }
            else
            {
                var intent = keyguard.CreateConfirmDeviceCredentialIntent(title, reason);
                if (intent == null) return OwnerCheck.NoScreenLock;
                activity.RunOnUiThread(() => activity.StartActivityForResult(intent, ConfirmCredentialRequest));
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Backup: couldn't ask for the screen lock", ex);
            return OwnerCheck.Refused;
        }

        var confirmed = await done.Task;
        Log.Info(confirmed ? "Backup: the owner confirmed with the screen lock" : "Backup: the screen lock check was cancelled or failed");
        return confirmed ? OwnerCheck.Confirmed : OwnerCheck.Refused;
    }

    /// <summary>The lock screen's answer on Android 9 and older.</summary>
    public static void OnActivityResult(int requestCode, Result resultCode)
    {
        if (requestCode == ConfirmCredentialRequest) Interlocked.Exchange(ref _confirmation, null)?.TrySetResult(resultCode == Result.Ok);
    }

    private sealed class OwnerCallback : global::Android.Hardware.Biometrics.BiometricPrompt.AuthenticationCallback
    {
        private readonly TaskCompletionSource<bool> _done;

        public OwnerCallback(TaskCompletionSource<bool> done) => _done = done;

        public override void OnAuthenticationSucceeded(global::Android.Hardware.Biometrics.BiometricPrompt.AuthenticationResult? result) =>
            _done.TrySetResult(true);

        // Cancelled, too many tries, no way to check: not confirmed. (A single wrong finger lets the person try again.)
        public override void OnAuthenticationError(global::Android.Hardware.Biometrics.BiometricErrorCode errorCode, Java.Lang.ICharSequence? errString) =>
            _done.TrySetResult(false);
    }

    /// <summary>The activity on screen, for things that need one (the code scanner).</summary>
    public static Activity? CurrentActivity => _activity?.TryGetTarget(out var activity) == true ? activity : null;

    /// <summary>Called by the activity with the user's answer to <see cref="RequestPermissionAsync"/>.</summary>
    public static void OnPermissionResult(int requestCode, Permission[] results)
    {
        if (requestCode <= 100) return; // the start-up request
        Interlocked.Exchange(ref _permission, null)?.TrySetResult(results.Length > 0 && results.All(r => r == Permission.Granted));
    }

    /// <summary>
    /// Up to Android 10, saving to Downloads needs the user's OK, which Beam can't work without. Everything else
    /// (notifications, Nearby devices) is asked when it's needed, after Beam explains why.
    /// </summary>
    public static void RequestPermissions(Activity activity)
    {
        _activity = new WeakReference<Activity>(activity);
        var wanted = new List<string>();
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
