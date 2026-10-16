using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Avalonia.Threading;
using Beam.App.Services;
using Beam.App.ViewModels;
using Beam.App.Views;
using Beam.Core;
using Beam.Core.Diagnostics;
using Beam.Core.Discovery;
using Beam.Core.Storage;
using Foundation;
using UIKit;

namespace Beam.iOS;

/// <summary>Owns the Beam node for the app process and connects it to iOS (storage, Wi-Fi, discovery).</summary>
internal static class IosHost
{
    private static AppDataPaths? _paths;
    private static BeamNode? _node;
    private static MainViewModel? _viewModel;
    private static MobileView? _view;
    private static Timer? _discoveryTimer;
    private static int _ticks;
    private static NSObject? _activeObserver, _inactiveObserver;
    private static nint _backgroundTask = -1;

    /// <summary>Settings, identity, history: Library/Application Support/Beam (not shown in the Files app).</summary>
    public static AppDataPaths Paths => _paths ??= new AppDataPaths(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Beam"));

    /// <summary>Received files go to the app's Documents folder, which the Files app shows ("On My iPhone" → Beam).</summary>
    public static string ReceiveFolder => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

    public static MainViewModel CreateViewModel(MobileView view)
    {
        _view = view;
        if (_viewModel != null) return _viewModel;

        Paths.EnsureCreated();
        var directLink = new IosDirectLink();
        _node = BeamNode.Create(new BeamNodeOptions
        {
            Paths = Paths,
            DeviceKind = DeviceKinds.Phone,
            DirectLink = directLink,
            // iOS drops broadcast and multicast without Apple's multicast permission: others are asked one by one.
            Discovery = new DiscoveryOptions { UseMulticastAndBroadcast = false },
        });
        var firstRun = !_node.Settings.Current.FirstRunCompleted;
        _node.Settings.Update(s =>
        {
            s.ReceiveFolder = ReceiveFolder; // the app's own folder: iOS gives it no other
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
            Log.Warn("Could not create the folder for received files", ex);
        }

        _node.Transfers.SessionStarted += _ => UpdateBackgroundTime();
        _node.Transfers.SessionFinished += _ => UpdateBackgroundTime();
        WatchForeground();
        StartDiscovery(_node);
        _viewModel = new MainViewModel(_node, new IosPlatformServices(), view, new UnavailableStoreService());
        _ = _viewModel.InitializeAsync(new CommandLine(false, Array.Empty<string>()));
        return _viewModel;
    }

    public static void SetForeground(bool foreground)
    {
        if (_view != null) _view.IsInForeground = foreground;
    }

    private static void WatchForeground()
    {
        _activeObserver = UIApplication.Notifications.ObserveDidBecomeActive((_, _) => SetForeground(true));
        _inactiveObserver = UIApplication.Notifications.ObserveWillResignActive((_, _) => SetForeground(false));
    }

    /// <summary>
    /// While a transfer runs, asks iOS for extra time when Beam goes to the background (a few minutes at most: iOS
    /// suspends apps after that; the transfer then picks up where it stopped once Beam is opened again).
    /// </summary>
    private static void UpdateBackgroundTime()
    {
        if (_node == null) return;
        var running = _node.Transfers.Sessions.Any(s => !s.IsFinished);
        UIApplication.SharedApplication.InvokeOnMainThread(() =>
        {
            if (running && _backgroundTask < 0)
            {
                _backgroundTask = UIApplication.SharedApplication.BeginBackgroundTask("Beam transfer", () =>
                {
                    Log.Info("iOS ended Beam's background time; transfers continue when Beam is opened again");
                    EndBackgroundTask();
                });
            }
            else if (!running)
            {
                EndBackgroundTask();
            }
        });
    }

    private static void EndBackgroundTask()
    {
        if (_backgroundTask < 0) return;
        UIApplication.SharedApplication.EndBackgroundTask(_backgroundTask);
        _backgroundTask = -1;
    }

    /// <summary>
    /// Finding others without broadcast: devices already known are asked every 5 s (they only answer when asked), and
    /// every address of this network (/24) every 30 s, so a computer that just started is found too.
    /// </summary>
    private static void StartDiscovery(BeamNode node)
    {
        _discoveryTimer = new Timer(_ =>
        {
            try
            {
                var tick = Interlocked.Increment(ref _ticks);
                foreach (var device in node.Discovery.Devices)
                    foreach (var endpoint in device.Endpoints)
                        node.Discovery.Query(endpoint.Address);
                if (tick % 6 == 1)
                    foreach (var address in NeighbourAddresses())
                        node.Discovery.Query(address);
            }
            catch (Exception ex)
            {
                Log.Info($"Discovery: asking nearby addresses failed: {ex.Message}");
            }
        }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5));
    }

    /// <summary>Every other address on this device's IPv4 networks (at most a /24 each).</summary>
    private static IEnumerable<IPAddress> NeighbourAddresses()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus == OperationalStatus.Down || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            // Wi-Fi (en0) and a computer's network joined for a direct link; not mobile data (pdp_ip) or VPNs.
            if (!nic.Name.StartsWith("en", StringComparison.Ordinal)) continue;
            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                var bytes = unicast.Address.GetAddressBytes();
                if (bytes[0] == 169 && bytes[1] == 254) continue;
                for (var last = 1; last < 255; last++)
                {
                    if (last == bytes[3]) continue;
                    yield return new IPAddress(new[] { bytes[0], bytes[1], bytes[2], (byte)last });
                }
            }
        }
    }

    /// <summary>The name people gave the device ("Diaz's iPhone"), else the model.</summary>
    private static string FriendlyDeviceName()
    {
        var name = UIDevice.CurrentDevice.Name;
        if (string.IsNullOrWhiteSpace(name)) name = UIDevice.CurrentDevice.Model ?? "iPhone";
        return name.Length > Core.Settings.AppSettings.MaxDeviceNameLength ? name[..Core.Settings.AppSettings.MaxDeviceNameLength] : name;
    }
}
