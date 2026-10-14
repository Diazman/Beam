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
/// The phone side of a direct link. Needs Android 10.
/// <para>
/// Hosting (this phone sends): a Wi-Fi Direct group with this session's name and passphrase
/// (WifiP2pManager.CreateGroup with a WifiP2pConfig), which also accepts ordinary Wi-Fi ("legacy") clients such as a
/// Windows PC's Wi-Fi adapter. Group owners are 192.168.49.1.
/// </para>
/// <para>
/// Joining (this phone receives): a phone's group is first joined the Wi-Fi Direct way, which keeps the phone on its
/// normal Wi-Fi too; a computer's network (and a group where that failed) is joined as an ordinary Wi-Fi network for
/// Beam only (WifiNetworkSpecifier): Android shows its "Connect to DIRECT-…?" prompt, which the user must approve
/// (Beam never claims it connects without that), and may approve it again without asking for the same network.
/// Beam's process is then bound to that network, so its connections (and only Beam's) use it; Beam.Core still checks
/// the route by reporting in over it. Leaving releases only that binding and request.
/// </para>
/// </summary>
internal sealed class AndroidDirectLink : IDirectLink, IDirectLinkEvents, IDirectNetworkScanner, IDirectRadio
{
    private static readonly TimeSpan GroupTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan P2pJoinTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan WifiJoinTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan VisibleTimeout = TimeSpan.FromSeconds(15);

    /// <summary>How long Android may take to answer a Wi-Fi Direct request (create a group, connect) before Beam withdraws it.</summary>
    private static readonly TimeSpan ActionTimeout = TimeSpan.FromSeconds(20);

    /// <summary>Upper limits for a whole start or join (the caller usually gives up sooner); they include asking the user.</summary>
    private static readonly TimeSpan HostTimeout = TimeSpan.FromSeconds(150);
    private static readonly TimeSpan JoinTimeout = TimeSpan.FromSeconds(210);

    /// <summary>Networks to join as ordinary Wi-Fi (joining them the Wi-Fi Direct way failed before).</summary>
    private readonly HashSet<string> _joinAsWifi = new();

    private readonly Context _context;
    private readonly Func<string> _deviceName;
    private readonly RadioOperations _radio = new("Wi-Fi Direct");
    private long _ops; // order of start/stop requests, so a late stop doesn't undo a newer start
    private long _lastStartOp;
    private long _running; // the start or join running now (one at a time)
    private long _owner; // the start or join that set up what is on now (a group started, or a network joined)
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

    /// <remarks>
    /// One radio operation at a time (<see cref="RadioOperations"/>): a start or join that is given up keeps the radio to
    /// itself until it has really stopped, and a group it created too late is removed again.
    /// </remarks>
    public Task<DirectNetwork> HostAsync(DirectNetwork? preferred, CancellationToken cancellationToken)
    {
        var attempt = 0L;
        return _radio.RunAsync("starting this phone's network", HostTimeout,
            token =>
            {
                attempt = BeginAttempt();
                return HostCoreAsync(preferred, token);
            },
            () => new DirectLinkException(L.T("This phone couldn't start a direct connection. Turn Wi-Fi on and try again.")),
            undoLateResult: _ => UndoAttemptAsync(attempt, joined: false),
            cancellationToken);
    }

    /// <summary>A start or join begins (under the radio gate): it is the newest, and whatever it sets up is its own.</summary>
    private long BeginAttempt()
    {
        var attempt = Interlocked.Increment(ref _ops);
        Volatile.Write(ref _lastStartOp, attempt);
        Volatile.Write(ref _running, attempt);
        return attempt;
    }

    private void OwnedByRunningAttempt() => Volatile.Write(ref _owner, Volatile.Read(ref _running));

    /// <summary>
    /// An attempt finished after its caller gave up: undo what it set up, but only if it set it up (a group or network
    /// that was already there, which it merely returned, is someone else's).
    /// </summary>
    private async Task UndoAttemptAsync(long attempt, bool joined)
    {
        if (Volatile.Read(ref _owner) != attempt)
        {
            Log.Info("Wi-Fi Direct: the attempt given up on didn't set up anything new; leaving it as it is");
            return;
        }

        // A start only created a group (a computer's network this phone was on before it is someone else's); a join
        // left everything else first, so both the network it joined and any group are its own.
        if (joined) ReleaseWifiNetwork();
        await RemoveGroupIfOpenAsync().ConfigureAwait(false);
    }

    private async Task<DirectNetwork> HostCoreAsync(DirectNetwork? preferred, CancellationToken cancellationToken)
    {
        {
            var channel = await PrepareAsync().ConfigureAwait(false);
            await EnsureWifiOnAsync(cancellationToken).ConfigureAwait(false);
            await EnsureLocationOnAsync(cancellationToken).ConfigureAwait(false);
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
            await RunAsync(listener => _manager!.CreateGroup(channel, config, listener), "start a direct network",
                () => RemoveGroupAsync(channel), cancellationToken).ConfigureAwait(false);

            try
            {
                var deadline = DateTime.UtcNow + GroupTimeout;
                while (DateTime.UtcNow < deadline)
                {
                    if (await GroupInfoAsync(channel).ConfigureAwait(false) is { IsGroupOwner: true } group)
                    {
                        OwnedByRunningAttempt();
                        Log.Info($"Wi-Fi Direct: hosting {group.NetworkName}");
                        return new DirectNetwork(group.NetworkName ?? network.Ssid, group.Passphrase ?? network.Passphrase);
                    }

                    await Task.Delay(500, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is System.OperationCanceledException or TimeoutException)
            {
                // Android accepted the request: the group may still form, with nobody to remove it.
                await RemoveGroupAsync(channel).ConfigureAwait(false);
                throw;
            }

            Log.Warn($"Wi-Fi Direct: the group didn't form within {GroupTimeout.TotalSeconds:0} s; withdrawing it");
            await RemoveGroupAsync(channel).ConfigureAwait(false);
            throw new DirectLinkException(L.T("This phone couldn't start a direct connection. Turn Wi-Fi on and try again."));
        }
    }

    public Task<IPAddress?> JoinAsync(DirectNetwork network, CancellationToken cancellationToken)
    {
        var attempt = 0L;
        return _radio.RunAsync($"joining {network.Ssid}", JoinTimeout,
            token =>
            {
                attempt = BeginAttempt();
                return JoinCoreAsync(network, token);
            },
            () => new DirectLinkException(L.T("This phone couldn't join the direct connection. Move the devices closer together and try again.")),
            undoLateResult: _ => UndoAttemptAsync(attempt, joined: true), // joined after all, too late: leave again
            cancellationToken);
    }

    private async Task<IPAddress?> JoinCoreAsync(DirectNetwork network, CancellationToken cancellationToken)
    {
        {
            var channel = await PrepareAsync().ConfigureAwait(false);
            await EnsureWifiOnAsync(cancellationToken).ConfigureAwait(false);
            await EnsureLocationOnAsync(cancellationToken).ConfigureAwait(false);
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
            await RunAsync(listener => _manager!.Connect(channel, config, listener), "join the direct network",
                () => WithdrawConnectAsync(channel), cancellationToken).ConfigureAwait(false);

            var deadline = DateTime.UtcNow + P2pJoinTimeout;
            while (DateTime.UtcNow < deadline)
            {
                if (await ConnectionInfoAsync(channel).ConfigureAwait(false) is { GroupFormed: true, IsGroupOwner: false, GroupOwnerAddress: { } owner })
                {
                    OwnedByRunningAttempt();
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
        catch (Exception ex) when (ex is System.OperationCanceledException or TimeoutException)
        {
            // Given up (or Android stopped answering): withdraw the pending connection, or it may still complete and
            // pull the phone into that group.
            await WithdrawConnectAsync(channel).ConfigureAwait(false);
            throw;
        }

        // Not joined in time: withdraw it before trying the other way.
        await WithdrawConnectAsync(channel).ConfigureAwait(false);
        return null;
    }

    /// <summary>
    /// Waits (up to <see cref="VisibleTimeout"/>) until the network shows up in Wi-Fi scans. A network that was just
    /// started takes a few seconds to appear; asking Android for it before that ends in "no network found".
    /// </summary>
    /// <returns>The network's access point address (BSSID) when it was seen; null otherwise.</returns>
    private async Task<string?> WaitUntilVisibleAsync(string ssid, CancellationToken cancellationToken)
    {
        // The network's name and passphrase came over Beam's authenticated channel: Android's WifiNetworkSpecifier
        // doesn't need a scan to use them. Without scan access (permission not granted) there is nothing to wait for.
        var permission = OperatingSystem.IsAndroidVersionAtLeast(33) ? Manifest.Permission.NearbyWifiDevices : Manifest.Permission.AccessFineLocation;
        if (_context.CheckSelfPermission(permission) != Permission.Granted)
        {
            Log.Info($"Wi-Fi: no scan access; asking Android for {ssid} by name");
            return null;
        }

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
        var op = Interlocked.Increment(ref _ops);
        ReleaseWifiNetwork(); // at once: Beam's connections mustn't stay bound to a network being left
        if (_manager == null || _channel == null) return;
        // The group is removed after any start or join still running (never alongside it), unless a newer start came first.
        _ = _radio.RunExclusiveAsync("stopping", TimeSpan.FromMinutes(2), async () =>
        {
            if (Volatile.Read(ref _lastStartOp) > op) return;
            ReleaseWifiNetwork(); // also one a join running when Stop was called set up afterwards
            await RemoveGroupIfOpenAsync().ConfigureAwait(false);
        });
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
            callback = new WifiCallback(network.Ssid, OnNetworkLost, () => IsWifiEnabled);
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

        // Stop() let go of this request while Android was connecting: binding to it now would tie all of Beam's
        // connections to a network that is being dropped.
        if (!ReferenceEquals(Volatile.Read(ref _wifiNetwork), callback))
        {
            Log.Info($"Wi-Fi: {network.Ssid} came up after the direct link was stopped; not using it");
            throw new System.OperationCanceledException();
        }

        // Beam's own connections (and only Beam's) now use this network; other apps keep the phone's normal internet.
        connectivity.BindProcessToNetwork(joined);
        OwnedByRunningAttempt();
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

    public bool? IsWifiEnabled => _context.GetSystemService(Context.WifiService) is WifiManager wifi ? wifi.IsWifiEnabled : null;

    /// <summary>
    /// Wi-Fi is off: Android 10+ doesn't let apps switch it on, so Beam opens the system's Wi-Fi panel (one tap) when it
    /// is in front, and waits until Wi-Fi is on and settled. False when it stays off (panel dismissed, Beam in the
    /// background, or no answer within <paramref name="wait"/>).
    /// </summary>
    public async Task<bool> RequestWifiOnAsync(TimeSpan wait, CancellationToken cancellationToken)
    {
        if (_context.GetSystemService(Context.WifiService) is not WifiManager wifi) return false;
        if (wifi.IsWifiEnabled) return true;
        if (OperatingSystem.IsAndroidVersionAtLeast(29) && AndroidHost.CurrentActivity is { } activity)
        {
            Log.Info("Wi-Fi: it is off; showing Android's Wi-Fi panel");
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
        }
        else
        {
            Log.Info("Wi-Fi: it is off and Beam isn't in front; the user has to turn it on");
        }

        var deadline = System.Environment.TickCount64 + (long)wait.TotalMilliseconds;
        while (!wifi.IsWifiEnabled && System.Environment.TickCount64 < deadline) await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        if (!wifi.IsWifiEnabled) return false;
        // "Enabled" comes before the radio can scan, join or host: give it a moment.
        const int Enabled = 3; // WifiManager.WIFI_STATE_ENABLED
        for (var i = 0; i < 20 && (int)wifi.WifiState != Enabled; i++) await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        await Task.Delay(1500, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task EnsureWifiOnAsync(CancellationToken cancellationToken)
    {
        if (!await RequestWifiOnAsync(TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false))
            throw new DirectLinkException(L.T("Turn on Wi-Fi to connect directly.")) { WifiOff = true };
    }

    /// <summary>
    /// Up to Android 12, Wi-Fi Direct and joining a network for an app only work with Location turned on (the phone's
    /// Location switch, not just the permission): with it off they fail with "error" or never answer. Apps can't switch
    /// it on, so Beam opens the Location settings and waits a little.
    /// </summary>
    private async Task EnsureLocationOnAsync(CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(33)) return;
        if (_context.GetSystemService(Context.LocationService) is not global::Android.Locations.LocationManager location || location.IsLocationEnabled) return;
        if (AndroidHost.CurrentActivity is { } activity)
        {
            Log.Info("Location: it is off (Android needs it on for direct connections); asking the user to turn it on");
            activity.RunOnUiThread(() =>
            {
                try
                {
                    activity.StartActivity(new Intent(global::Android.Provider.Settings.ActionLocationSourceSettings));
                }
                catch (Exception ex)
                {
                    Log.Warn($"Location: couldn't open its settings: {ex.Message}");
                }
            });
            for (var i = 0; i < 60 && !location.IsLocationEnabled; i++) await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }

        if (!location.IsLocationEnabled)
            throw new DirectLinkException(L.T("Turn on Location to connect directly. Android needs it for direct connections; Beam doesn't use your location."));
        Log.Info("Location: on");
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

    /// <summary>
    /// Starts a Wi-Fi Direct request and waits for Android's answer, at most <see cref="ActionTimeout"/>. Unanswered (or
    /// given up), the request is withdrawn with <paramref name="withdraw"/> before this returns, so a late success can't
    /// interfere with the next attempt.
    /// </summary>
    private async Task RunAsync(Action<WifiP2pManager.IActionListener> start, string what, Func<Task> withdraw, CancellationToken cancellationToken)
    {
        var listener = new ActionListener();
        start(listener);
        WifiP2pFailureReason? reason;
        try
        {
            reason = await RadioCallbacks.WaitAsync(listener.Task, ActionTimeout, withdraw, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Log.Warn($"Wi-Fi Direct: no answer to \"{what}\" within {ActionTimeout.TotalSeconds:0} s; withdrew it");
            throw new DirectLinkException(L.T("The phone's Wi-Fi is busy. Try again in a moment."));
        }

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

    private Task RemoveGroupIfOpenAsync() => _channel is { } channel && _manager != null ? RemoveGroupAsync(channel) : Task.CompletedTask;

    /// <summary>Withdraws a pending Wi-Fi Direct connection and leaves any group it formed (best effort, bounded).</summary>
    private async Task WithdrawConnectAsync(WifiP2pManager.Channel channel)
    {
        var listener = new ActionListener();
        try
        {
            _manager!.CancelConnect(channel, listener);
            await listener.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Info($"Wi-Fi Direct: withdrawing the connection: {ex.Message}");
        }

        await RemoveGroupAsync(channel).ConfigureAwait(false);
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

        public WifiCallback(string ssid, Action<WifiCallback> lost, Func<bool?>? wifiEnabled = null)
        {
            Ssid = ssid;
            _lost = lost;
            WifiEnabled = wifiEnabled;
        }

        private Func<bool?>? WifiEnabled { get; }

        public string Ssid { get; }

        public Android.Net.Network? Network { get; private set; }

        public IPAddress? Host { get; set; }

        public TaskCompletionSource<Android.Net.Network?> Available { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>When Android said the network is available (to tell, when it's lost, how long it lasted).</summary>
        public long AvailableAt { get; private set; }

        public override void OnAvailable(Android.Net.Network network)
        {
            Network = network;
            AvailableAt = System.Environment.TickCount64;
            Available.TrySetResult(network);
        }

        public override void OnLosing(Android.Net.Network network, int maxMsToLive)
        {
            Log.Warn($"Wi-Fi: Android is about to drop {Ssid} (within {maxMsToLive} ms, {Seconds()} s after it came up)");
        }

        private string Seconds() => AvailableAt == 0 ? "?" : ((System.Environment.TickCount64 - AvailableAt) / 1000.0).ToString("0.0");

        public override void OnUnavailable()
        {
            Log.Info($"Wi-Fi: Android says {Ssid} is unavailable (the request was turned down, or the network wasn't found)");
            Available.TrySetResult(null);
        }

        public override void OnLost(Android.Net.Network network)
        {
            Log.Warn($"Wi-Fi: lost {Ssid} ({Seconds()} s after it came up; Wi-Fi {(WifiEnabled?.Invoke() is { } on ? on ? "on" : "off" : "?")})");
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
