using System.Net;
using Beam.Core.Diagnostics;
using Beam.Core.Direct;
using Beam.Core.Localization;
using Foundation;
using NetworkExtension;

namespace Beam.iOS;

/// <summary>
/// The iPhone side of a direct link: it can only join. iOS doesn't let apps start a Wi-Fi network (no hotspot, no
/// Wi-Fi Direct), so a computer hosts and the iPhone joins its network with NEHotspotConfiguration: iOS asks the user
/// "Join Wi-Fi network …?" and the iPhone leaves its own Wi-Fi meanwhile. Needs the Hotspot Configuration entitlement
/// (a paid Apple Developer account). iOS doesn't show apps the network's gateway, so the host's address comes with the
/// network details (<see cref="DirectNetwork.HostAddress"/>), else it is .1 of a Wi-Fi Direct subnet.
/// </summary>
internal sealed class IosDirectLink : IDirectLink, IDirectLinkEvents
{
    private static readonly TimeSpan JoinTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan AddressTimeout = TimeSpan.FromSeconds(20);

    private readonly object _gate = new();
    private string? _joinedSsid;
    private IPAddress? _host;
    private CancellationTokenSource? _watch;

    public DirectRoles Roles => DirectRoles.Join;

    public event Action? Lost;

    public Task<DirectNetwork> HostAsync(DirectNetwork? preferred, CancellationToken cancellationToken) =>
        throw new DirectLinkException(L.T("This device can't start a direct connection."));

    public async Task<IPAddress?> JoinAsync(DirectNetwork network, CancellationToken cancellationToken)
    {
        lock (_gate)
            if (_joinedSsid == network.Ssid && _host != null && DirectAddresses.OwnAddressNear(_host) != null)
                return _host;

        Leave();
        Log.Info($"Wi-Fi: asking iOS to join {network.Ssid}");
        var configuration = new NEHotspotConfiguration(network.Ssid, network.Passphrase, false) { JoinOnce = true };
        var answer = new TaskCompletionSource<NSError?>(TaskCreationOptions.RunContinuationsAsynchronously);
        NEHotspotConfigurationManager.SharedManager.ApplyConfiguration(configuration, error => answer.TrySetResult(error));
        NSError? failure;
        try
        {
            failure = await answer.Task.WaitAsync(JoinTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new DirectLinkException(L.T("This phone couldn't join the direct connection. Move the devices closer together and try again."));
        }
        catch (OperationCanceledException)
        {
            Forget(network.Ssid);
            throw;
        }

        // 13: already on it. 7: the user tapped Cancel. Anything else: iOS couldn't join.
        if (failure != null && failure.Code != 13)
        {
            Log.Warn($"Wi-Fi: iOS didn't join {network.Ssid} ({failure.Code}: {failure.LocalizedDescription})");
            throw new DirectLinkException(failure.Code == 7
                ? L.T("The phone's user didn't allow joining the direct network.")
                : L.T("This phone couldn't join the direct connection. Move the devices closer together and try again."));
        }

        // iOS says "done" even when it didn't get there: only an address on that network counts.
        var until = Environment.TickCount64 + (long)AddressTimeout.TotalMilliseconds;
        IPAddress? host = null;
        while (Environment.TickCount64 < until)
        {
            DirectAddresses.Invalidate();
            host = network.HostAddress is { } offered
                ? DirectAddresses.OwnAddressNear(offered) != null ? offered : null
                : DirectAddresses.DirectHostsOfLocalNetworks().FirstOrDefault();
            if (host != null) break;
            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }

        if (host == null)
        {
            Forget(network.Ssid);
            throw new DirectLinkException(L.T("This phone couldn't join the direct connection. Move the devices closer together and try again."));
        }

        lock (_gate)
        {
            _joinedSsid = network.Ssid;
            _host = host;
            _watch = new CancellationTokenSource();
            _ = WatchAsync(host, _watch.Token);
        }

        Log.Info($"Wi-Fi: connected to {network.Ssid}, host at {host} (own address {DirectAddresses.OwnAddressNear(host)})");
        return host;
    }

    /// <summary>The joined network went away when this device no longer has an address on it (checked every 2 s).</summary>
    private async Task WatchAsync(IPAddress host, CancellationToken token)
    {
        var missing = 0;
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(2000, token).ConfigureAwait(false);
                DirectAddresses.Invalidate();
                missing = DirectAddresses.OwnAddressNear(host) == null ? missing + 1 : 0;
                if (missing < 2) continue;
                string? ssid;
                lock (_gate)
                {
                    if (token.IsCancellationRequested) return;
                    ssid = _joinedSsid;
                    _joinedSsid = null;
                    _host = null;
                }

                Log.Warn($"Wi-Fi: lost {ssid}");
                Lost?.Invoke();
                return;
            }
        }
        catch (OperationCanceledException)
        {
            // left on purpose
        }
    }

    public void Stop() => Leave();

    private void Leave()
    {
        string? ssid;
        lock (_gate)
        {
            ssid = _joinedSsid;
            _joinedSsid = null;
            _host = null;
            _watch?.Cancel();
            _watch = null;
        }

        if (ssid == null) return;
        Log.Info($"Wi-Fi: leaving {ssid}");
        Forget(ssid);
    }

    /// <summary>Removes Beam's configuration for the network: iOS leaves it and goes back to its own Wi-Fi.</summary>
    private static void Forget(string ssid)
    {
        try
        {
            NEHotspotConfigurationManager.SharedManager.RemoveConfiguration(ssid);
        }
        catch (Exception ex)
        {
            Log.Info($"Wi-Fi: removing {ssid} failed: {ex.Message}");
        }
    }
}
