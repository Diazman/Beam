using System.Net;
using System.Security.Cryptography;
using Android;
using Android.Content;
using Android.Content.PM;
using Android.Net;
using Android.Net.Wifi;
using Android.Net.Wifi.P2p;
using Android.OS;
using Beam.Core.Diagnostics;
using Beam.Core.Direct;
using Beam.Core.Localization;

namespace Beam.Droid;

/// <summary>
/// The phone side of a direct link. The phone joins a PC's (or another phone's) direct network by its name and
/// passphrase, or starts one itself (Wi-Fi Direct, WifiP2pManager). Joining first tries Wi-Fi Direct, which keeps
/// the phone on its normal Wi-Fi too; some phones can't join a Windows-hosted network that way (e.g. Xiaomi,
/// Android 12), so it then joins it as an ordinary Wi-Fi network (Android asks the user once), and Beam's
/// traffic goes over that network until the direct link is stopped. Needs Android 10.
/// </summary>
internal sealed class AndroidDirectLink : IDirectLink, IDirectLinkEvents, IDirectNetworkScanner
{
    private static readonly TimeSpan GroupTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan P2pJoinTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan WifiJoinTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan VisibleTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Networks to join as ordinary Wi-Fi (joining them the Wi-Fi Direct way failed before).</summary>
    private readonly HashSet<string> _joinAsWifi = new();

    private readonly Context _context;
    private readonly Func<string> _deviceName;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly WifiP2pManager? _manager;
    private WifiP2pManager.Channel? _channel;
    private WifiCallback? _wifiNetwork;

    public AndroidDirectLink(Context context, Func<string> deviceName)
    {
        _context = context;
        _deviceName = deviceName;
        if (OperatingSystem.IsAndroidVersionAtLeast(29) && context.PackageManager?.HasSystemFeature(PackageManager.FeatureWifiDirect) == true)
            _manager = (WifiP2pManager?)context.GetSystemService(Context.WifiP2pService);
    }

    public DirectRoles Roles => _manager != null ? DirectRoles.Host | DirectRoles.Join : DirectRoles.None;

    private long _lastScanStart;

    /// <summary>
    /// Wi-Fi networks in range, from Android's scan results (needs Nearby devices on Android 13+, location before).
    /// Asks for a fresh scan at most every 30 s (Android limits apps to a few scans per two minutes).
    /// </summary>
    public IReadOnlyCollection<string> VisibleNetworks() => VisibleNetworks(scanEveryMs: 30_000);

    private IReadOnlyCollection<string> VisibleNetworks(int scanEveryMs)
    {
        try
        {
            var permission = OperatingSystem.IsAndroidVersionAtLeast(33) ? Manifest.Permission.NearbyWifiDevices : Manifest.Permission.AccessFineLocation;
            if (_context.CheckSelfPermission(permission) != Permission.Granted) return Array.Empty<string>();
            if (_context.GetSystemService(Context.WifiService) is not WifiManager wifi) return Array.Empty<string>();
            if (System.Environment.TickCount64 - _lastScanStart > scanEveryMs)
            {
                _lastScanStart = System.Environment.TickCount64;
#pragma warning disable CA1422 // still the way to ask for a scan; Android throttles it
                wifi.StartScan();
#pragma warning restore CA1422
            }

#pragma warning disable CA1422 // ScanResult.Ssid: the replacement needs Android 13
            return wifi.ScanResults?.Select(r => r.Ssid?.Trim('"')).Where(s => !string.IsNullOrEmpty(s)).Select(s => s!).ToHashSet()
                   ?? (IReadOnlyCollection<string>)Array.Empty<string>();
#pragma warning restore CA1422
        }
        catch (Exception ex)
        {
            Log.Warn($"Wi-Fi scan results unavailable: {ex.Message}");
            return Array.Empty<string>();
        }
    }

    public async Task<DirectNetwork> HostAsync(DirectNetwork? preferred, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var channel = await PrepareAsync().ConfigureAwait(false);
            await EnsureWifiOnAsync(cancellationToken).ConfigureAwait(false);
            if (await GroupInfoAsync(channel).ConfigureAwait(false) is { IsGroupOwner: true } existing
                && !string.IsNullOrEmpty(existing.NetworkName) && !string.IsNullOrEmpty(existing.Passphrase))
                return new DirectNetwork(existing.NetworkName, existing.Passphrase);

            await RemoveGroupAsync(channel).ConfigureAwait(false);
            var network = preferred ?? DirectNetwork.Create(_deviceName());
            var config = new WifiP2pConfig.Builder()
                .SetNetworkName(network.Ssid)!
                .SetPassphrase(network.Passphrase)!
                .EnablePersistentMode(false)!
                .Build();
            await RunAsync(listener => _manager!.CreateGroup(channel, config, listener), "start a direct network").ConfigureAwait(false);

            var deadline = DateTime.UtcNow + GroupTimeout;
            while (DateTime.UtcNow < deadline)
            {
                if (await GroupInfoAsync(channel).ConfigureAwait(false) is { IsGroupOwner: true } group)
                {
                    Log.Info($"Wi-Fi Direct: hosting {group.NetworkName}");
                    return new DirectNetwork(group.NetworkName ?? network.Ssid, group.Passphrase ?? network.Passphrase);
                }

                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            }

            throw new DirectLinkException(L.T("This phone couldn't start a direct connection. Turn Wi-Fi on and try again."));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IPAddress?> JoinAsync(DirectNetwork network, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var channel = await PrepareAsync().ConfigureAwait(false);
            await EnsureWifiOnAsync(cancellationToken).ConfigureAwait(false);
            if (await GroupInfoAsync(channel).ConfigureAwait(false) is { } current && current.NetworkName == network.Ssid
                && await ConnectionInfoAsync(channel).ConfigureAwait(false) is { GroupFormed: true, GroupOwnerAddress: { } existing })
                return IPAddress.Parse(existing.HostAddress!);

            if (_wifiNetwork is { Network: not null, Host: { } joinedHost } wifi && wifi.Ssid == network.Ssid) return joinedHost;

            await RemoveGroupAsync(channel).ConfigureAwait(false);
            ReleaseWifiNetwork();
            // A computer's network (Windows) is joined as ordinary Wi-Fi straight away: joining it the Wi-Fi Direct way
            // fails on many phones and only costs time. So does a network where that already failed once.
            if (!network.HostIsComputer && !_joinAsWifi.Contains(network.Ssid))
            {
                if (await TryJoinAsP2pAsync(channel, network, cancellationToken).ConfigureAwait(false) is { } owner) return owner;
                _joinAsWifi.Add(network.Ssid);
                await RemoveGroupAsync(channel).ConfigureAwait(false);
            }

            return await JoinAsWifiNetworkAsync(network, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IPAddress?> TryJoinAsP2pAsync(WifiP2pManager.Channel channel, DirectNetwork network, CancellationToken cancellationToken)
    {
        try
        {
            var config = new WifiP2pConfig.Builder()
                .SetNetworkName(network.Ssid)!
                .SetPassphrase(network.Passphrase)!
                .EnablePersistentMode(false)!
                .Build();
            Log.Info($"Wi-Fi Direct: joining {network.Ssid}");
            await RunAsync(listener => _manager!.Connect(channel, config, listener), "join the direct network").ConfigureAwait(false);

            var deadline = DateTime.UtcNow + P2pJoinTimeout;
            while (DateTime.UtcNow < deadline)
            {
                if (await ConnectionInfoAsync(channel).ConfigureAwait(false) is { GroupFormed: true, IsGroupOwner: false, GroupOwnerAddress: { } owner })
                {
                    Log.Info($"Wi-Fi Direct: joined {network.Ssid}, host at {owner.HostAddress}");
                    return IPAddress.Parse(owner.HostAddress!);
                }

                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            }

            Log.Warn($"Wi-Fi Direct: didn't join {network.Ssid} within {P2pJoinTimeout.TotalSeconds:0} s");
        }
        catch (DirectLinkException ex)
        {
            Log.Warn($"Wi-Fi Direct: joining {network.Ssid} failed: {ex.Message}");
        }

        return null;
    }

    /// <summary>
    /// Waits (up to <see cref="VisibleTimeout"/>) until the network shows up in Wi-Fi scans. A network that was just
    /// started takes a few seconds to appear; asking Android for it before that ends in "no network found".
    /// </summary>
    /// <returns>The network's access point address (BSSID) when it was seen; null otherwise.</returns>
    private async Task<string?> WaitUntilVisibleAsync(string ssid, CancellationToken cancellationToken)
    {
        var deadline = System.Environment.TickCount64 + (long)VisibleTimeout.TotalMilliseconds;
        _lastScanStart = 0; // scan now
        var sawAnything = false;
        while (System.Environment.TickCount64 < deadline)
        {
            var visible = VisibleNetworks(scanEveryMs: 5_000);
            if (visible.Contains(ssid))
            {
                var bssid = BssidOf(ssid);
                Log.Info($"Wi-Fi: {ssid} is in range ({bssid ?? "no BSSID"})");
                return bssid;
            }

            sawAnything |= visible.Count > 0;
            await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
        }

        Log.Info(sawAnything ? $"Wi-Fi: {ssid} didn't show up in scans; asking Android anyway" : "Wi-Fi: no scan results; asking Android anyway");
        return null;
    }

    /// <summary>The strongest access point broadcasting <paramref name="ssid"/> in the last scan.</summary>
    private string? BssidOf(string ssid)
    {
        try
        {
            if (_context.GetSystemService(Context.WifiService) is not WifiManager wifi) return null;
#pragma warning disable CA1422 // ScanResult.Ssid: the replacement needs Android 13
            return wifi.ScanResults?.Where(r => r.Ssid?.Trim('"') == ssid && !string.IsNullOrEmpty(r.Bssid))
                .OrderByDescending(r => r.Level).Select(r => r.Bssid).FirstOrDefault();
#pragma warning restore CA1422
        }
        catch (Exception ex)
        {
            Log.Info($"Wi-Fi: no access point address for {ssid}: {ex.Message}");
            return null;
        }
    }

    public void Stop()
    {
        ReleaseWifiNetwork();
        var channel = _channel;
        if (_manager == null || channel == null) return;
        _ = RemoveGroupAsync(channel);
    }

    /// <summary>
    /// Joins the direct network as an ordinary Wi-Fi network for Beam only (WifiNetworkSpecifier): Android shows
    /// "Connect to DIRECT-…?" once, and Beam's connections then go over that network.
    /// </summary>
    private async Task<IPAddress?> JoinAsWifiNetworkAsync(DirectNetwork network, CancellationToken cancellationToken)
    {
        var connectivity = (ConnectivityManager?)_context.GetSystemService(Context.ConnectivityService)
                           ?? throw new DirectLinkException(L.T("This phone couldn't join the direct connection. Move the devices closer together and try again."));
        var bssid = await WaitUntilVisibleAsync(network.Ssid, cancellationToken).ConfigureAwait(false);
        var builder = new WifiNetworkSpecifier.Builder().SetSsid(network.Ssid)!.SetWpa2Passphrase(network.Passphrase)!;
        // The exact access point: Android connects to it without searching again, and once the user approved it,
        // later requests for it are usually approved without asking.
        if (bssid != null && global::Android.Net.MacAddress.FromString(bssid) is { } mac) builder.SetBssid(mac);
        var specifier = builder.Build();
        var request = new NetworkRequest.Builder()
            .AddTransportType(TransportType.Wifi)!
            .RemoveCapability(NetCapability.Internet)!
            .SetNetworkSpecifier(specifier)!
            .Build();
        WifiCallback callback;
        Android.Net.Network? joined = null;
        for (var attempt = 1; ; attempt++)
        {
            callback = new WifiCallback(network.Ssid, OnNetworkLost);
            Log.Info($"Wi-Fi: asking Android to connect to {network.Ssid}");
            var asked = System.Environment.TickCount64;
            try
            {
                connectivity.RequestNetwork(request!, callback, (int)WifiJoinTimeout.TotalMilliseconds);
            }
            catch (Exception ex)
            {
                Log.Warn("Wi-Fi: Android refused the network request", ex);
                throw new DirectLinkException(L.T("This phone couldn't join the direct connection. Move the devices closer together and try again."), ex);
            }

            _wifiNetwork = callback; // registered: released by ReleaseWifiNetwork
            try
            {
                joined = await callback.Available.Task.WaitAsync(WifiJoinTimeout + TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                Log.Info($"Wi-Fi: no answer from Android about {network.Ssid} within {WifiJoinTimeout.TotalSeconds:0} s");
                joined = null;
            }
            catch (System.OperationCanceledException)
            {
                // Gave up (e.g. sending over Wi-Fi instead): withdraw the request, or Android may still switch to the
                // network later and pull the phone off its Wi-Fi.
                ReleaseWifiNetwork();
                throw;
            }

            if (joined != null) break;
            ReleaseWifiNetwork();
            // Android sometimes turns the first request down at once (it was still busy with the previous network):
            // asking again a moment later usually works.
            if (attempt >= 2 || System.Environment.TickCount64 - asked > 3000) break;
            Log.Info($"Wi-Fi: Android turned {network.Ssid} down at once; asking again");
            await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
        }

        if (joined == null)
        {
            throw new DirectLinkException(L.T("This phone couldn't join the direct connection. When the phone asks to connect to the DIRECT-… network, tap Connect. Move the devices closer together and try again."));
        }

        // Beam's own connections (and only Beam's) now use this network; other apps keep the phone's normal internet.
        connectivity.BindProcessToNetwork(joined);
        var host = HostAddress(connectivity.GetLinkProperties(joined));
        callback.Host = host;
        Log.Info($"Wi-Fi: connected to {network.Ssid}, host at {host?.ToString() ?? "unknown"}");
        JoinedChanged?.Invoke();
        return host;
    }

    /// <summary>The direct network's host: its gateway, else the .1 address of the phone's subnet (Windows hosts are 192.168.137.1).</summary>
    private static IPAddress? HostAddress(LinkProperties? properties)
    {
        if (properties == null) return null;
        foreach (var route in properties.Routes)
        {
            if (route.Gateway?.HostAddress is { } gateway && IPAddress.TryParse(gateway, out var address)
                && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !address.Equals(IPAddress.Any))
                return address;
        }

        foreach (var link in properties.LinkAddresses)
        {
            if (link.Address?.HostAddress is { } own && IPAddress.TryParse(own, out var mine) && mine.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            {
                var bytes = mine.GetAddressBytes();
                bytes[3] = 1;
                return new IPAddress(bytes);
            }
        }

        return null;
    }

    public event Action? Lost;

    /// <summary>Beam is on a computer's network right now (joined and bound to it).</summary>
    public bool IsJoined => Volatile.Read(ref _wifiNetwork) is { Network: not null };

    /// <summary>Joined or left a computer's network.</summary>
    public event Action? JoinedChanged;

    /// <summary>
    /// Android dropped the PC's network (the PC stopped it, it went out of range, or Android gave up on it). Beam was
    /// bound to that network, so until it lets go every connection fails, also over the normal Wi-Fi: let go now.
    /// </summary>
    private void OnNetworkLost(WifiCallback callback)
    {
        if (!ReferenceEquals(Volatile.Read(ref _wifiNetwork), callback)) return;
        ReleaseWifiNetwork();
        Lost?.Invoke();
    }

    private void ReleaseWifiNetwork()
    {
        var callback = Interlocked.Exchange(ref _wifiNetwork, null);
        if (callback == null) return;
        try
        {
            var connectivity = (ConnectivityManager?)_context.GetSystemService(Context.ConnectivityService);
            connectivity?.BindProcessToNetwork(null);
            try
            {
                connectivity?.UnregisterNetworkCallback(callback);
            }
            catch (Java.Lang.IllegalArgumentException)
            {
                // Android already dropped the request (e.g. it timed out).
            }

            Log.Info($"Wi-Fi: left {callback.Ssid}");
            JoinedChanged?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Warn("Wi-Fi: leaving the direct network failed", ex);
        }
    }

    /// <summary>
    /// Wi-Fi is off: Android doesn't let apps switch it on (since Android 10), so Beam opens Android's Wi-Fi panel (one
    /// tap) and waits a little for it.
    /// </summary>
    private async Task EnsureWifiOnAsync(CancellationToken cancellationToken)
    {
        if (_context.GetSystemService(Context.WifiService) is not WifiManager wifi || wifi.IsWifiEnabled) return;
        if (AndroidHost.CurrentActivity is { } activity)
        {
            Log.Info("Wi-Fi: it is off; asking the user to turn it on");
            activity.RunOnUiThread(() =>
            {
                try
                {
                    activity.StartActivity(new Intent(global::Android.Provider.Settings.Panel.ActionWifi));
                }
                catch (Exception ex)
                {
                    Log.Warn($"Wi-Fi: couldn't open the Wi-Fi panel: {ex.Message}");
                }
            });
            for (var i = 0; i < 40 && !wifi.IsWifiEnabled; i++) await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }

        if (!wifi.IsWifiEnabled) throw new DirectLinkException(L.T("Turn on Wi-Fi to connect directly."));
        await Task.Delay(1500, cancellationToken).ConfigureAwait(false); // Wi-Fi needs a moment before it can join or host
    }

    /// <summary>Asks for the permission Wi-Fi Direct needs (Nearby devices on Android 13+, location before) and opens the channel.</summary>
    private async Task<WifiP2pManager.Channel> PrepareAsync()
    {
        if (_manager == null) throw new DirectLinkException(L.T("This phone can't connect directly. Direct connections need Android 10 or newer."));
        var permission = OperatingSystem.IsAndroidVersionAtLeast(33) ? Manifest.Permission.NearbyWifiDevices : Manifest.Permission.AccessFineLocation;
        if (_context.CheckSelfPermission(permission) != Permission.Granted && !await AndroidHost.RequestPermissionAsync(permission).ConfigureAwait(false))
        {
            throw new DirectLinkException(OperatingSystem.IsAndroidVersionAtLeast(33)
                ? L.T("Beam needs the \"Nearby devices\" permission to connect directly. Allow it in the phone's settings for Beam.")
                : L.T("Beam needs the location permission to connect directly (Android requires it for Wi-Fi Direct). Allow it in the phone's settings for Beam."));
        }

        return _channel ??= _manager.Initialize(_context, Looper.MainLooper, null)
                            ?? throw new DirectLinkException(L.T("This phone couldn't start a direct connection. Turn Wi-Fi on and try again."));
    }

    private async Task RunAsync(Action<WifiP2pManager.IActionListener> start, string what)
    {
        var listener = new ActionListener();
        start(listener);
        var reason = await listener.Task.ConfigureAwait(false);
        if (reason == null) return;
        Log.Warn($"Wi-Fi Direct: couldn't {what} (reason {reason})");
        throw new DirectLinkException(reason switch
        {
            WifiP2pFailureReason.P2pUnsupported => L.T("This phone can't connect directly. Direct connections need Android 10 or newer."),
            WifiP2pFailureReason.Busy => L.T("The phone's Wi-Fi is busy. Try again in a moment."),
            _ => OperatingSystem.IsAndroidVersionAtLeast(33)
                ? L.T("This phone couldn't start a direct connection. Turn Wi-Fi on and try again.")
                : L.T("This phone couldn't start a direct connection. Turn on Wi-Fi and Location, then try again."),
        });
    }

    private async Task RemoveGroupAsync(WifiP2pManager.Channel channel)
    {
        var listener = new ActionListener();
        try
        {
            _manager!.RemoveGroup(channel, listener);
            await listener.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Info($"Wi-Fi Direct: removing the group: {ex.Message}");
        }
    }

    private Task<WifiP2pInfo?> ConnectionInfoAsync(WifiP2pManager.Channel channel)
    {
        var listener = new InfoListener();
        _manager!.RequestConnectionInfo(channel, listener);
        return listener.Connection.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private Task<WifiP2pGroup?> GroupInfoAsync(WifiP2pManager.Channel channel)
    {
        var listener = new InfoListener();
        _manager!.RequestGroupInfo(channel, listener);
        return listener.Group.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }



    private sealed class WifiCallback : ConnectivityManager.NetworkCallback
    {
        private readonly Action<WifiCallback> _lost;

        public WifiCallback(string ssid, Action<WifiCallback> lost)
        {
            Ssid = ssid;
            _lost = lost;
        }

        public string Ssid { get; }

        public Android.Net.Network? Network { get; private set; }

        public IPAddress? Host { get; set; }

        public TaskCompletionSource<Android.Net.Network?> Available { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override void OnAvailable(Android.Net.Network network)
        {
            Network = network;
            Available.TrySetResult(network);
        }

        public override void OnUnavailable()
        {
            Log.Info($"Wi-Fi: Android says {Ssid} is unavailable (the request was turned down, or the network wasn't found)");
            Available.TrySetResult(null);
        }

        public override void OnLost(Android.Net.Network network)
        {
            Log.Warn($"Wi-Fi: lost {Ssid}");
            Network = null;
            _lost(this);
        }
    }

    private sealed class ActionListener : Java.Lang.Object, WifiP2pManager.IActionListener
    {
        private readonly TaskCompletionSource<WifiP2pFailureReason?> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Null on success, else why it failed (Error, P2pUnsupported, Busy…).</summary>
        public Task<WifiP2pFailureReason?> Task => _done.Task;

        public void OnSuccess() => _done.TrySetResult(null);

        public void OnFailure(WifiP2pFailureReason reason) => _done.TrySetResult(reason);
    }

    private sealed class InfoListener : Java.Lang.Object, WifiP2pManager.IConnectionInfoListener, WifiP2pManager.IGroupInfoListener
    {
        public TaskCompletionSource<WifiP2pInfo?> Connection { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<WifiP2pGroup?> Group { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void OnConnectionInfoAvailable(WifiP2pInfo? info) => Connection.TrySetResult(info);

        public void OnGroupInfoAvailable(WifiP2pGroup? group) => Group.TrySetResult(group);
    }
}
