#if WINDOWS_WINRT
using System.Net;
using Beam.Core.Diagnostics;
using Beam.Core.Direct;
using Beam.Core.Localization;
using Windows.Devices.Radios;
using Windows.Devices.WiFi;
using Windows.Devices.WiFiDirect;
using Windows.Foundation.Metadata;
using Windows.Networking.Connectivity;
using Windows.Networking.NetworkOperators;
using Windows.Security.Credentials;

namespace Beam.App.Platform;

// Windows' Mobile hotspot as this PC's direct network (it shares the PC's internet), and stopping what was started.
internal sealed partial class WindowsDirectLink : IDirectLink, IDirectLinkEvents, IInternetSharing, IAsyncDisposable
{
    /// <summary>
    /// The hotspot on the band this PC's own Wi-Fi is on, so one radio isn't split between two channels far apart; and
    /// (Windows 11 24H2) the hotspot's speed put before the PC's own connection while Beam uses it.
    /// </summary>
    private static void UseOwnBand(NetworkOperatorTetheringSessionAccessPointConfiguration session)
    {
        try
        {
            var type = typeof(NetworkOperatorTetheringSessionAccessPointConfiguration).FullName;
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041) && ApiInformation.IsPropertyPresent(type, "Band"))
            {
                var band = WlanTuning.ConnectedBand() switch
                {
                    2 => TetheringWiFiBand.TwoPointFourGigahertz,
                    5 => TetheringWiFiBand.FiveGigahertz,
                    _ => TetheringWiFiBand.Auto,
                };
                if (band != TetheringWiFiBand.Auto && session.IsBandSupported(band))
                {
                    session.Band = band;
                    Log.Info($"Hotspot: on {band}, the band this PC's own Wi-Fi is on");
                }
            }

            if (ApiInformation.IsPropertyPresent(type, "PerformancePriority"))
            {
                session.PerformancePriority = TetheringWiFiPerformancePriority.TetheringOverStation;
                Log.Info("Hotspot: its speed comes before this PC's own Wi-Fi connection");
            }
        }
        catch (Exception ex)
        {
            Log.Info($"Hotspot: couldn't choose the band ({ex.Message})");
        }
    }

    /// <summary>
    /// WPA2 for Beam's hotspot, also when the user's own hotspot is set to WPA3: phones join it with a WPA2 passphrase
    /// (Android's join request has to match the network's security exactly).
    /// </summary>
    private static void UseWpa2(NetworkOperatorTetheringSessionAccessPointConfiguration session)
    {
        try
        {
            if (ApiInformation.IsPropertyPresent(typeof(NetworkOperatorTetheringSessionAccessPointConfiguration).FullName, "AuthenticationKind"))
                session.AuthenticationKind = TetheringWiFiAuthenticationKind.Wpa2;
        }
        catch (Exception ex)
        {
            Log.Info($"Hotspot: couldn't choose WPA2 ({ex.Message})");
        }
    }

    private static void UseWpa2(NetworkOperatorTetheringAccessPointConfiguration settings)
    {
        try
        {
            if (ApiInformation.IsPropertyPresent(typeof(NetworkOperatorTetheringAccessPointConfiguration).FullName, "AuthenticationKind"))
                settings.AuthenticationKind = TetheringWiFiAuthenticationKind.Wpa2;
        }
        catch (Exception ex)
        {
            Log.Info($"Hotspot: couldn't choose WPA2 ({ex.Message})");
        }
    }

    /// <summary>
    /// Starts Windows' Mobile hotspot as <paramref name="network"/>, sharing this PC's internet (when its Wi-Fi Direct
    /// network can't start). False when there's no internet, the Wi-Fi can't share it while staying connected, the user's
    /// own hotspot is already on, or Windows refuses.
    /// </summary>
    private async Task<bool> TryStartHotspotAsync(DirectNetwork network, CancellationToken cancellationToken)
    {
        NetworkOperatorTetheringManager? startedBy = null;
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
                if (!File.Exists(HotspotMarker))
                {
                    Log.Info($"Hotspot: already {manager.TetheringOperationalState} (turned on in Windows); using a Wi-Fi Direct network");
                    return false;
                }

                // Beam's own, left on by an earlier run (closed or crashed before stopping it): start it afresh.
                Log.Info("Hotspot: Beam's hotspot from an earlier run is still on; restarting it");
                await manager.StopTetheringAsync().AsTask(cancellationToken).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
                DeleteHotspotMarker();
            }

            NetworkOperatorTetheringOperationResult result;
            startedBy = manager; // from here on it is Beam's own (it was off, or Beam's from an earlier run)
            if (ApiInformation.IsTypePresent("Windows.Networking.NetworkOperators.NetworkOperatorTetheringSessionAccessPointConfiguration"))
            {
                // Windows 11: settings for this session only; the user's own hotspot name and password stay as they are.
                var session = new NetworkOperatorTetheringSessionAccessPointConfiguration { Ssid = network.Ssid, Passphrase = network.Passphrase };
                UseWpa2(session);
                UseOwnBand(session);
                result = await manager.StartTetheringAsync(session).AsTask(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                // Older Windows: set Beam's name and password, and put the user's back when Beam's hotspot stops.
                _userHotspotSettings = manager.GetCurrentAccessPointConfiguration();
                var settings = new NetworkOperatorTetheringAccessPointConfiguration { Ssid = network.Ssid, Passphrase = network.Passphrase };
                UseWpa2(settings);
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
            WriteHotspotMarker(network.Ssid);
            Log.Info($"Hotspot {network.Ssid} started, sharing this PC's internet ({profile.ProfileName})");
            return true;
        }
        catch (OperationCanceledException)
        {
            // Given up while Windows was starting it: it may come on anyway, with nothing to turn it off.
            if (startedBy is { } started && _hotspot == null) await StopUntrackedHotspotAsync(started).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn($"Hotspot: couldn't start it ({ex.Message}); using a Wi-Fi Direct network");
            return false;
        }
    }

    /// <summary>Exists while Beam's hotspot is on, so the next run knows a hotspot still on is Beam's own.</summary>
    private static string HotspotMarker =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Beam", "hotspot-on");

    private static void WriteHotspotMarker(string ssid)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(HotspotMarker)!);
            File.WriteAllText(HotspotMarker, ssid);
        }
        catch (Exception ex)
        {
            Log.Warn("Hotspot: couldn't note that it's on", ex);
        }
    }

    private static void DeleteHotspotMarker()
    {
        try
        {
            File.Delete(HotspotMarker);
        }
        catch
        {
            // best effort
        }
    }

    private async Task StopHotspotAsync()
    {
        var manager = _hotspot;
        _hotspot = null;
        if (manager == null) return;
        DeleteHotspotMarker();
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

    private async Task StopUntrackedHotspotAsync(NetworkOperatorTetheringManager manager)
    {
        try
        {
            if (manager.TetheringOperationalState != TetheringOperationalState.Off)
                await manager.StopTetheringAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn($"Hotspot: stopping the one given up on failed: {ex.Message}");
        }

        await RestoreUserHotspotSettingsAsync(manager).ConfigureAwait(false);
    }

    private static void StopUntracked(WiFiDirectAdvertisementPublisher publisher)
    {
        try
        {
            if (publisher.Status is WiFiDirectAdvertisementPublisherStatus.Started or WiFiDirectAdvertisementPublisherStatus.Created) publisher.Stop();
        }
        catch (Exception ex)
        {
            Log.Info($"Wi-Fi Direct: stopping the one given up on: {ex.Message}");
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
}
#endif
