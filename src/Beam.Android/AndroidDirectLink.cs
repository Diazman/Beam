using System.Net;
using System.Security.Cryptography;
using Android;
using Android.Content;
using Android.Content.PM;
using Android.Net.Wifi.P2p;
using Android.OS;
using Beam.Core.Diagnostics;
using Beam.Core.Direct;
using Beam.Core.Localization;

namespace Beam.Droid;

/// <summary>
/// The phone side of a direct link, using Wi-Fi Direct (WifiP2pManager). The phone joins a PC's (or another
/// phone's) direct network by its name and passphrase, or starts one itself. Joining by name and
/// passphrase needs Android 10; the phone stays on its normal Wi-Fi at the same time on most models.
/// </summary>
internal sealed class AndroidDirectLink : IDirectLink
{
    private const string Alphabet = "abcdefghijkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    private static readonly TimeSpan GroupTimeout = TimeSpan.FromSeconds(30);

    private readonly Context _context;
    private readonly Func<string> _deviceName;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly WifiP2pManager? _manager;
    private WifiP2pManager.Channel? _channel;

    public AndroidDirectLink(Context context, Func<string> deviceName)
    {
        _context = context;
        _deviceName = deviceName;
        if (OperatingSystem.IsAndroidVersionAtLeast(29) && context.PackageManager?.HasSystemFeature(PackageManager.FeatureWifiDirect) == true)
            _manager = (WifiP2pManager?)context.GetSystemService(Context.WifiP2pService);
    }

    public DirectRoles Roles => _manager != null ? DirectRoles.Host | DirectRoles.Join : DirectRoles.None;

    public async Task<DirectNetwork> HostAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var channel = await PrepareAsync().ConfigureAwait(false);
            if (await GroupInfoAsync(channel).ConfigureAwait(false) is { IsGroupOwner: true } existing
                && !string.IsNullOrEmpty(existing.NetworkName) && !string.IsNullOrEmpty(existing.Passphrase))
                return new DirectNetwork(existing.NetworkName, existing.Passphrase);

            await RemoveGroupAsync(channel).ConfigureAwait(false);
            var network = new DirectNetwork(NetworkName(_deviceName()), Random(12));
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

            await RemoveGroupAsync(channel).ConfigureAwait(false);
            var config = new WifiP2pConfig.Builder()
                .SetNetworkName(network.Ssid)!
                .SetPassphrase(network.Passphrase)!
                .EnablePersistentMode(false)!
                .Build();
            Log.Info($"Wi-Fi Direct: joining {network.Ssid}");
            await RunAsync(listener => _manager!.Connect(channel, config, listener), "join the direct network").ConfigureAwait(false);

            var deadline = DateTime.UtcNow + GroupTimeout;
            while (DateTime.UtcNow < deadline)
            {
                if (await ConnectionInfoAsync(channel).ConfigureAwait(false) is { GroupFormed: true, IsGroupOwner: false, GroupOwnerAddress: { } owner })
                {
                    Log.Info($"Wi-Fi Direct: joined {network.Ssid}, host at {owner.HostAddress}");
                    return IPAddress.Parse(owner.HostAddress!);
                }

                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            }

            await RemoveGroupAsync(channel).ConfigureAwait(false);
            throw new DirectLinkException(L.T("This phone couldn't join the direct connection. Move the devices closer together and try again."));
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Stop()
    {
        var channel = _channel;
        if (_manager == null || channel == null) return;
        _ = RemoveGroupAsync(channel);
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

    /// <summary>"DIRECT-" plus two random characters is required by Android for Wi-Fi Direct groups.</summary>
    private static string NetworkName(string deviceName)
    {
        var name = "DIRECT-" + Random(2) + "-Beam-" + new string(deviceName.Where(c => char.IsAsciiLetterOrDigit(c) || c is ' ' or '-').ToArray());
        return (name.Length > 32 ? name[..32] : name).TrimEnd(' ', '-');
    }

    private static string Random(int length)
    {
        var chars = new char[length];
        for (var i = 0; i < length; i++) chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        return new string(chars);
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
