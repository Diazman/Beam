using System.Collections.Concurrent;
using System.Net;
using Beam.Core.Diagnostics;
using Beam.Core.Discovery;
using Beam.Core.Localization;
using Beam.Core.Protocol;
using Beam.Core.Settings;
using Beam.Core.Transfer;

namespace Beam.Core.Direct;

// The sender's own network for its direct sessions: started on the first Send, shared by the sessions that follow, stopped after the last.
public sealed partial class DirectManager
{
    /// <summary>
    /// A share of this device's direct network for <paramref name="session"/>: started on the first one (after Send,
    /// with a fresh passphrase), shared by the sessions that follow while it's on. Refused while the link is someone
    /// else's (this device being on another device's network).
    /// </summary>
    private async Task<DirectNetwork> AcquireHostAsync(string owner, string session, bool forPhone, bool forComputer, CancellationToken token)
    {
        Task<DirectNetwork> start;
        lock (_hostGate)
        {
            // Started earlier, but gone since (the OS or driver stopped it): start it again rather than offer a dead network.
            if (_sessionHostStart is { IsCompletedSuccessfully: true } && !HostNetworkReady())
            {
                Log.Warn($"Direct: this device's network for direct sessions is gone; starting it again. Adapters: {DirectAddresses.Describe()}");
                _sessionHostStart = null;
                HostedNetwork = null;
            }

            if (!_hostLeases.Contains(owner) || _sessionHostStart == null || _sessionHostStart.IsFaulted || _sessionHostStart.IsCanceled)
            {
                if (_sessionHostStart == null || _sessionHostStart.IsFaulted || _sessionHostStart.IsCanceled)
                {
                    // Nobody else may be using the radio: starting would end their network (one Wi-Fi adapter).
                    var others = _linkedPeers.Keys.Where(k => !k.StartsWith("session:", StringComparison.Ordinal)).ToList();
                    if (others.Count > 0)
                    {
                        Log.Info($"Direct: not starting a network for {session}: the link is in use ({string.Join(", ", others)})");
                        throw new DirectLinkException(L.T("This device's direct connection is in use right now. Try again when it's done."));
                    }

                    _sessionHostStart = StartSessionHostAsync(forPhone, forComputer);
                }

                _hostLeases.Add(owner);
                _linkedPeers[LeaseKey(owner)] = 0;
            }

            start = _sessionHostStart!;
        }

        try
        {
            return await start.WaitAsync(token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A start that failed or was given up is retried by the next setup; the share itself is given back when the
            // transfer is over (or the user moves it to the network), not here: a recovering transfer still needs it.
            if (ex is not OperationCanceledException) lock (_hostGate) if (ReferenceEquals(_sessionHostStart, start) && start.IsCompleted) _sessionHostStart = null;
            throw;
        }
    }

    /// <param name="forPhone">A phone joins first: this device's internet-sharing network if it can (later receivers join the same).</param>
    /// <param name="forComputer">A computer joins first: this phone's hotspot if it can run one (later receivers join the same).</param>
    private async Task<DirectNetwork> StartSessionHostAsync(bool forPhone, bool forComputer)
    {
        await Task.Yield(); // started under the lock: run outside it
        try
        {
            await PrepareAsync().ConfigureAwait(false);
            // The same name as always (so a phone that approved it before may connect without asking again), with a
            // passphrase made for these sessions: it is useless once the network goes off.
            var fresh = DirectNetwork.ForSession(OwnNetworkName());
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
            limit.CancelAfter(HostStartTimeout);
            DirectNetwork started;
            try
            {
                if (_link is IInternetSharing sharing) sharing.PreferInternetSharing = forPhone;
                if (_link is IAccessPointHosting accessPoint) accessPoint.PreferAccessPoint = forComputer;
                Log.Info($"Direct: starting {fresh.Ssid} for direct sessions{(forPhone && _link is IInternetSharing ? " (a phone joins: sharing this device's internet if it can)" : "")}");
                started = await _link!.HostAsync(fresh, limit.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!_stopping.IsCancellationRequested)
            {
                throw new DirectLinkException(L.T("This device's Wi-Fi didn't start the direct network in time. Make sure Wi-Fi is on."));
            }

            // "Started" is not "usable": wait for this device's address on it.
            var until = Environment.TickCount64 + (long)HostReadyTimeout.TotalMilliseconds;
            while (!HostNetworkReady())
            {
                if (Environment.TickCount64 > until)
                {
                    Log.Warn($"Direct: {started.Ssid} said it started, but has no address after {HostReadyTimeout.TotalSeconds:0} s. Adapters: {DirectAddresses.Describe()}");
                    StopSessionHost();
                    throw new DirectLinkException(L.T("The direct network said it started, but it isn't usable. Turn Wi-Fi off and on, then try again."));
                }

                await Task.Delay(250, _stopping.Token).ConfigureAwait(false);
                DirectAddresses.Invalidate();
            }

            bool unused;
            lock (_hostGate)
            {
                unused = _hostLeases.Count == 0;
                if (unused) _sessionHostStart = null;
            }

            HostedNetwork = started;
            DirectAddresses.Invalidate();
            Changed?.Invoke();
            if (unused)
            {
                Log.Info($"Direct: {started.Ssid} came up after every session let go of it; stopping it");
                StopLinkIfUnused();
            }
            else
            {
                Log.Info($"Direct: {started.Ssid} is on for direct sessions{(started.SharesInternet ? ", sharing this device's internet" : "")}" +
                         $"{(started.IsAccessPoint ? " (this phone's hotspot)" : "")}");
            }

            return started;
        }
        catch
        {
            lock (_hostGate) _sessionHostStart = null;
            throw;
        }
    }

    /// <summary>The network started for sessions turned out unusable: off again (it is this device's own, started just now).</summary>
    private void StopSessionHost()
    {
        try
        {
            _link?.Stop();
        }
        catch (Exception ex)
        {
            Log.Warn("Direct: stopping the unusable network failed", ex);
        }

        HostedNetwork = null;
    }

    private bool HoldsHost(string owner)
    {
        lock (_hostGate) return _hostLeases.Contains(owner);
    }

    /// <summary>Gives back <paramref name="session"/>'s share; the network goes off once no session (or anyone else) uses it.</summary>
    private void ReleaseHost(string owner)
    {
        bool stop;
        lock (_hostGate)
        {
            if (!_hostLeases.Remove(owner)) return;
            _linkedPeers.TryRemove(LeaseKey(owner), out _);
            stop = _hostLeases.Count == 0 && _sessionHostStart is { IsCompleted: true };
            if (stop) _sessionHostStart = null;
        }

        if (stop)
        {
            Log.Info("Direct: no direct session needs this device's network any more");
            StopLinkIfUnused();
        }
    }
}
