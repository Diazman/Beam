using System.Net;
using Beam.Core.Diagnostics;
using Beam.Core.Direct;
using Beam.Core.Localization;
#if WINDOWS_WINRT
using Windows.Devices.WiFiDirect;
using Windows.Foundation.Metadata;
using Windows.Networking.Connectivity;
using Windows.Networking.NetworkOperators;
using Windows.Security.Credentials;
#endif

namespace Beam.App.Platform;

/// <summary>
/// The PC side of a direct link: a small WPA2 network (DIRECT-xx-Beam-…) that the phone joins with the passphrase it
/// got from the PC's code or over Beam's encrypted connection. When this PC has internet and its Wi-Fi can share it
/// while staying connected, the network is Windows' Mobile hotspot, which shares that internet: phones then have no
/// reason to switch to another Wi-Fi mid-transfer. Otherwise it is a Wi-Fi Direct group with "legacy" access (no
/// internet). Windows hands out 192.168.137.x on both, which is how Beam recognizes the direct path.
/// </summary>
internal sealed class WindowsDirectLink : IDirectLink
{
    private readonly Func<string> _deviceName;
    private readonly SemaphoreSlim _gate = new(1, 1);
#if WINDOWS_WINRT
    private WiFiDirectAdvertisementPublisher? _publisher;
    private NetworkOperatorTetheringManager? _hotspot;
    private NetworkOperatorTetheringAccessPointConfiguration? _userHotspotSettings; // restored when Beam's hotspot stops
#endif
    private DirectNetwork? _network;

    private WindowsDirectLink(Func<string> deviceName) => _deviceName = deviceName;

    public DirectRoles Roles => DirectRoles.Host;

    /// <summary>Null where Windows has no Wi-Fi Direct API (the plain net8.0 build).</summary>
    public static IDirectLink? TryCreate(Func<string> deviceName)
    {
#if WINDOWS_WINRT
        return OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763) ? new WindowsDirectLink(deviceName) : null;
#else
        return null;
#endif
    }

    public async Task<DirectNetwork> HostAsync(DirectNetwork? preferred, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
#if WINDOWS_WINRT
            if (_publisher?.Status == WiFiDirectAdvertisementPublisherStatus.Started && _network != null
                && (preferred == null || preferred == _network)) return _network;
            if (_hotspot?.TetheringOperationalState == TetheringOperationalState.On && _network != null
                && (preferred == null || preferred == _network)) return _network;
            StopPublisher();
            await StopHotspotAsync().ConfigureAwait(false);
            var network = preferred ?? DirectNetwork.Create(_deviceName());
            if (await TryStartHotspotAsync(network, cancellationToken).ConfigureAwait(false))
            {
                _network = network with { SharesInternet = true };
                return _network;
            }

            var publisher = new WiFiDirectAdvertisementPublisher();
            var advertisement = publisher.Advertisement;
            advertisement.IsAutonomousGroupOwnerEnabled = true;
            advertisement.ListenStateDiscoverability = WiFiDirectAdvertisementListenStateDiscoverability.Normal;
            advertisement.LegacySettings.IsEnabled = true;
            advertisement.LegacySettings.Ssid = network.Ssid;
            advertisement.LegacySettings.Passphrase = new PasswordCredential { Password = network.Passphrase };

            var started = new TaskCompletionSource<WiFiDirectAdvertisementPublisherStatusChangedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            publisher.StatusChanged += (_, e) =>
            {
                Log.Info($"Wi-Fi Direct: {e.Status} ({e.Error})");
                if (e.Status is WiFiDirectAdvertisementPublisherStatus.Started or WiFiDirectAdvertisementPublisherStatus.Aborted
                    or WiFiDirectAdvertisementPublisherStatus.Stopped)
                    started.TrySetResult(e);
            };

            try
            {
                publisher.Start();
            }
            catch (Exception ex)
            {
                throw new DirectLinkException(L.T("This computer can't start a direct connection: its Wi-Fi doesn't support Wi-Fi Direct, or Wi-Fi is turned off."), ex);
            }

            WiFiDirectAdvertisementPublisherStatusChangedEventArgs result;
            try
            {
                result = await started.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                publisher.Stop();
                throw new DirectLinkException(L.T("This computer's Wi-Fi didn't start the direct connection in time. Make sure Wi-Fi is turned on."));
            }

            if (result.Status != WiFiDirectAdvertisementPublisherStatus.Started)
            {
                throw new DirectLinkException(result.Error == WiFiDirectError.ResourceInUse
                    ? L.T("This computer's Wi-Fi is busy with another direct connection or a mobile hotspot. Turn off the hotspot and try again.")
                    : L.T("This computer can't start a direct connection: its Wi-Fi doesn't support Wi-Fi Direct, or Wi-Fi is turned off."));
            }

            _publisher = publisher;
            _network = network;
            Log.Info($"Wi-Fi Direct network {network.Ssid} started");
            return network;
#else
            await Task.CompletedTask.ConfigureAwait(false);
            throw new DirectLinkException(L.T("This device can't connect directly."));
#endif
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Windows only hosts: a phone (or another PC's Beam) joins the PC's network.</summary>
    public Task<IPAddress?> JoinAsync(DirectNetwork network, CancellationToken cancellationToken) =>
        throw new DirectLinkException(L.T("This device can't connect directly."));

    public void Stop()
    {
        _gate.Wait();
        try
        {
#if WINDOWS_WINRT
            StopPublisher();
            StopHotspotAsync().GetAwaiter().GetResult();
#endif
            _network = null;
        }
        finally
        {
            _gate.Release();
        }
    }

#if WINDOWS_WINRT
    /// <summary>
    /// Starts Windows' Mobile hotspot as <paramref name="network"/>, sharing this PC's internet. False (and the plain
    /// Wi-Fi Direct network is used) when there's no internet, the Wi-Fi can't share it while staying connected, the
    /// user's own hotspot is already on, or Windows refuses.
    /// </summary>
    private async Task<bool> TryStartHotspotAsync(DirectNetwork network, CancellationToken cancellationToken)
    {
        try
        {
            var profile = NetworkInformation.GetInternetConnectionProfile();
            if (profile == null || profile.GetNetworkConnectivityLevel() != NetworkConnectivityLevel.InternetAccess)
            {
                Log.Info("Hotspot: this PC has no internet to share; using a Wi-Fi Direct network");
                return false;
            }

            var capability = NetworkOperatorTetheringManager.GetTetheringCapabilityFromConnectionProfile(profile);
            if (capability != TetheringCapability.Enabled)
            {
                Log.Info($"Hotspot: not available here ({capability}); using a Wi-Fi Direct network");
                return false;
            }

            var manager = NetworkOperatorTetheringManager.CreateFromConnectionProfile(profile);
            if (manager.TetheringOperationalState != TetheringOperationalState.Off)
            {
                Log.Info($"Hotspot: already {manager.TetheringOperationalState} (turned on in Windows); using a Wi-Fi Direct network");
                return false;
            }

            NetworkOperatorTetheringOperationResult result;
            if (ApiInformation.IsTypePresent("Windows.Networking.NetworkOperators.NetworkOperatorTetheringSessionAccessPointConfiguration"))
            {
                // Windows 11: settings for this session only; the user's own hotspot name and password stay as they are.
                var session = new NetworkOperatorTetheringSessionAccessPointConfiguration { Ssid = network.Ssid, Passphrase = network.Passphrase };
                result = await manager.StartTetheringAsync(session).AsTask(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                // Older Windows: set Beam's name and password, and put the user's back when Beam's hotspot stops.
                _userHotspotSettings = manager.GetCurrentAccessPointConfiguration();
                var settings = new NetworkOperatorTetheringAccessPointConfiguration { Ssid = network.Ssid, Passphrase = network.Passphrase };
                await manager.ConfigureAccessPointAsync(settings).AsTask(cancellationToken).ConfigureAwait(false);
                result = await manager.StartTetheringAsync().AsTask(cancellationToken).ConfigureAwait(false);
            }

            if (result.Status != TetheringOperationStatus.Success)
            {
                Log.Info($"Hotspot: Windows didn't start it ({result.Status} {result.AdditionalErrorMessage}); using a Wi-Fi Direct network");
                await RestoreUserHotspotSettingsAsync(manager).ConfigureAwait(false);
                return false;
            }

            _hotspot = manager;
            Log.Info($"Hotspot {network.Ssid} started, sharing this PC's internet ({profile.ProfileName})");
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn($"Hotspot: couldn't start it ({ex.Message}); using a Wi-Fi Direct network");
            return false;
        }
    }

    private async Task StopHotspotAsync()
    {
        var manager = _hotspot;
        _hotspot = null;
        if (manager == null) return;
        try
        {
            if (manager.TetheringOperationalState != TetheringOperationalState.Off)
                await manager.StopTetheringAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            Log.Info("Hotspot stopped");
        }
        catch (Exception ex)
        {
            Log.Warn("Hotspot: stopping failed", ex);
        }

        await RestoreUserHotspotSettingsAsync(manager).ConfigureAwait(false);
    }

    private async Task RestoreUserHotspotSettingsAsync(NetworkOperatorTetheringManager manager)
    {
        var saved = _userHotspotSettings;
        _userHotspotSettings = null;
        if (saved == null) return;
        try
        {
            await manager.ConfigureAccessPointAsync(saved).AsTask().WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn("Hotspot: restoring the hotspot's own name and password failed", ex);
        }
    }

    private void StopPublisher()
    {
        if (_publisher == null) return;
        try
        {
            if (_publisher.Status == WiFiDirectAdvertisementPublisherStatus.Started) _publisher.Stop();
        }
        catch (Exception ex)
        {
            Log.Warn("Wi-Fi Direct: stopping failed", ex);
        }

        _publisher = null;
    }
#endif
}
