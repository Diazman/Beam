using System.Net;
using System.Security.Cryptography;
using System.Text;
using Beam.Core.Diagnostics;
using Beam.Core.Direct;
using Beam.Core.Localization;
#if WINDOWS_WINRT
using Windows.Devices.WiFiDirect;
using Windows.Security.Credentials;
#endif

namespace Beam.App.Platform;

/// <summary>
/// The PC side of a direct link: Windows starts a Wi-Fi Direct group with "legacy" access, i.e. a small
/// WPA2 network (DIRECT-xx-Beam-…) that the phone joins with the passphrase it was sent over Beam's
/// encrypted connection. The PC keeps its normal Wi-Fi connection where the adapter allows it.
/// Windows hands out 192.168.137.x on this network, which is how Beam recognizes the direct path.
/// </summary>
internal sealed class WindowsDirectLink : IDirectLink
{
    private const string Alphabet = "abcdefghijkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    private readonly Func<string> _deviceName;
    private readonly SemaphoreSlim _gate = new(1, 1);
#if WINDOWS_WINRT
    private WiFiDirectAdvertisementPublisher? _publisher;
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

    public async Task<DirectNetwork> HostAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
#if WINDOWS_WINRT
            if (_publisher?.Status == WiFiDirectAdvertisementPublisherStatus.Started && _network != null) return _network;
            StopPublisher();

            var network = new DirectNetwork(NetworkName(_deviceName()), Random(12));
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
#endif
            _network = null;
        }
        finally
        {
            _gate.Release();
        }
    }

#if WINDOWS_WINRT
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

    /// <summary>
    /// "DIRECT-" plus two random characters is required for Wi-Fi Direct groups (Android checks it);
    /// the rest shows people whose network it is. At most 32 bytes.
    /// </summary>
    internal static string NetworkName(string deviceName)
    {
        var name = new StringBuilder("DIRECT-").Append(Random(2)).Append("-Beam-");
        foreach (var c in deviceName)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is ' ' or '-' or '_')) continue;
            if (name.Length >= 32) break;
            name.Append(c);
        }

        return name.ToString().TrimEnd(' ', '-');
    }

    private static string Random(int length)
    {
        var chars = new char[length];
        for (var i = 0; i < length; i++) chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        return new string(chars);
    }
}
