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
internal sealed class AndroidDirectLink : IDirectLink, IDirectNetworkScanner
{
    private static readonly TimeSpan GroupTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan P2pJoinTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan WifiJoinTimeout = TimeSpan.FromSeconds(60);

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
    public IReadOnlyCollection<string> VisibleNetworks()
    {
        try
        {
            var permission = OperatingSystem.IsAndroidVersionAtLeast(33) ? Manifest.Permission.NearbyWifiDevices : Manifest.Permission.AccessFineLocation;
            if (_context.CheckSelfPermission(permission) != Permission.Granted) return Array.Empty<string>();
            if (_context.GetSystemService(Context.WifiService) is not WifiManager wifi) return Array.Empty<string>();
            if (System.Environment.TickCount64 - _lastScanStart > 30_000)
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
            if (await GroupInfoAsync(channel).ConfigureAwait(false) is { } current && current.NetworkName == network.Ssid
                && await ConnectionInfoAsync(channel).ConfigureAwait(false) is { GroupFormed: true, GroupOwnerAddress: { } existing })
                return IPAddress.Parse(existing.HostAddress!);

            if (_wifiNetwork is { Network: not null, Host: { } joinedHost } wifi && wifi.Ssid == network.Ssid) return joinedHost;

            await RemoveGroupAsync(channel).ConfigureAwait(false);
            ReleaseWifiNetwork();
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

            await RemoveGroupAsync(channel).ConfigureAwait(false);
            return await JoinAsWifiNetworkAsync(network, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
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
        var specifier = new WifiNetworkSpecifier.Builder().SetSsid(network.Ssid)!.SetWpa2Passphrase(network.Passphrase)!.Build();
        var request = new NetworkRequest.Builder()
            .AddTransportType(TransportType.Wifi)!
            .RemoveCapability(NetCapability.Internet)!
            .SetNetworkSpecifier(specifier)!
            .Build();
        var callback = new WifiCallback(network.Ssid);
        Log.Info($"Wi-Fi: asking Android to connect to {network.Ssid}");
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

        Android.Net.Network? joined;
        try
        {
            joined = await callback.Available.Task.WaitAsync(WifiJoinTimeout + TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            joined = null;
        }

        if (joined == null)
        {
            ReleaseWifiNetwork();
            throw new DirectLinkException(L.T("This phone couldn't join the direct connection. When the phone asks to connect to the DIRECT-… network, tap Connect. Move the devices closer together and try again."));
        }

        // Beam's own connections (and only Beam's) now use this network; other apps keep the phone's normal internet.
        connectivity.BindProcessToNetwork(joined);
        var host = HostAddress(connectivity.GetLinkProperties(joined));
        callback.Host = host;
        Log.Info($"Wi-Fi: connected to {network.Ssid}, host at {host?.ToString() ?? "unknown"}");
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
        }
        catch (Exception ex)
        {
            Log.Warn("Wi-Fi: leaving the direct network failed", ex);
        }
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
        public WifiCallback(string ssid) => Ssid = ssid;

        public string Ssid { get; }

        public Android.Net.Network? Network { get; private set; }

        public IPAddress? Host { get; set; }

        public TaskCompletionSource<Android.Net.Network?> Available { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override void OnAvailable(Android.Net.Network network)
        {
            Network = network;
            Available.TrySetResult(network);
        }

        public override void OnUnavailable() => Available.TrySetResult(null);

        public override void OnLost(Android.Net.Network network)
        {
            Log.Info($"Wi-Fi: lost {Ssid}");
            Network = null;
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
