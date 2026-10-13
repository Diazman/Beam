using System.Net;
using Beam.Core.Diagnostics;
using Beam.Core.Direct;
using Beam.Core.Localization;
#if WINDOWS_WINRT
using Windows.Devices.Radios;
using Windows.Devices.WiFi;
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
/// To reach another computer directly, this PC's Wi-Fi can also join that computer's network (never a phone's: phones
/// join the PC), and goes back to the Wi-Fi it was on when the link stops.
/// </summary>
internal sealed class WindowsDirectLink : IDirectLink, IDirectLinkEvents, IInternetSharing, IAsyncDisposable
{
    private readonly Func<string> _deviceName;
    private readonly RadioOperations _radio = new("Direct network");
#if WINDOWS_WINRT
    private WiFiDirectAdvertisementPublisher? _publisher;
    private NetworkOperatorTetheringManager? _hotspot;
    private NetworkOperatorTetheringAccessPointConfiguration? _userHotspotSettings; // restored when Beam's hotspot stops
    private WiFiAdapter? _joinedAdapter; // this PC's Wi-Fi while it is on another computer's network
    private string? _joinedSsid;
    private string? _previousSsid; // the Wi-Fi to go back to
    private string? _addedProfile; // a Wi-Fi profile Beam added to join by name (removed when leaving)
    private readonly object _restoreGate = new();
    private Task _restoring = Task.CompletedTask; // going back to the previous Wi-Fi, removing a profile (in the background)
#endif
    private DirectNetwork? _network;
    private long _ops; // order of start/stop requests, so a late stop doesn't undo a newer start
    private long _lastStartOp;
    private long _running; // the start or join running now (one at a time)
    private long _owner; // the start or join that set up what is on now (a network started, or one joined)
    private volatile bool _closed;

    private WindowsDirectLink(Func<string> deviceName) => _deviceName = deviceName;

    public DirectRoles Roles => DirectRoles.Host | DirectRoles.JoinComputers;

#if WINDOWS_WINRT
    private bool _canShare;
    private long _canShareCheckedAt = long.MinValue / 2;
#endif

    /// <summary>
    /// This PC's network would share its internet: it is online and Windows can run its Mobile hotspot from here (and the
    /// user's own hotspot isn't on). Checked at most every 30 seconds; announced so two PCs agree which one hosts.
    /// </summary>
    public bool CanShareInternet
    {
        get
        {
#if WINDOWS_WINRT
            if (_hotspot != null) return true;
            if (Environment.TickCount64 - Volatile.Read(ref _canShareCheckedAt) < 30_000) return _canShare;
            _canShareCheckedAt = Environment.TickCount64;
            try
            {
                var profile = NetworkInformation.GetInternetConnectionProfile();
                _canShare = profile != null && profile.GetNetworkConnectivityLevel() == NetworkConnectivityLevel.InternetAccess
                            && NetworkOperatorTetheringManager.GetTetheringCapabilityFromConnectionProfile(profile) == TetheringCapability.Enabled
                            && NetworkOperatorTetheringManager.CreateFromConnectionProfile(profile).TetheringOperationalState == TetheringOperationalState.Off;
            }
            catch
            {
                _canShare = false;
            }

            return _canShare;
#else
            return false;
#endif
        }
    }

#pragma warning disable CS0067 // raised only in the Windows build
    public event Action? Lost;
#pragma warning restore CS0067

    /// <summary>Null where Windows has no Wi-Fi Direct API (the plain net8.0 build).</summary>
    public static IDirectLink? TryCreate(Func<string> deviceName)
    {
#if WINDOWS_WINRT
        return OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763) ? new WindowsDirectLink(deviceName) : null;
#else
        return null;
#endif
    }

    /// <summary>How long starting this PC's network may take before Beam gives up (so one stuck attempt can't block the rest).</summary>
    private static readonly TimeSpan HostTimeout = TimeSpan.FromSeconds(45);

    private static readonly TimeSpan JoinTimeout = TimeSpan.FromSeconds(100);

    /// <remarks>
    /// With a time limit, as Windows' Wi-Fi calls can hang. The attempt's token is cancelled at the limit; if it still
    /// doesn't stop, it keeps the radio to itself until it does, and a network it started that late is stopped again.
    /// </remarks>
    public Task<DirectNetwork> HostAsync(DirectNetwork? preferred, CancellationToken cancellationToken)
    {
        var attempt = 0L;
        return _radio.RunAsync("starting this PC's network", HostTimeout,
            token =>
            {
                attempt = BeginAttempt();
                return WhileOpenAsync(HostCoreAsync(preferred, token));
            },
            () =>
            {
                Log.Warn("Direct network: Windows didn't finish starting it in time");
                return new DirectLinkException(L.T("This computer's Wi-Fi didn't start the direct connection in time. Make sure Wi-Fi is turned on."));
            },
            undoLateResult: _ => UndoAttemptAsync(attempt),
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

    /// <summary>What the running attempt just set up (a network started, or joined) belongs to it.</summary>
    private void OwnedByRunningAttempt() => Volatile.Write(ref _owner, Volatile.Read(ref _running));

    /// <summary>
    /// An attempt finished after its caller gave up: stop what it set up, but only if it set it up (a network that was
    /// already on, which it merely returned, is someone else's).
    /// </summary>
    private Task UndoAttemptAsync(long attempt)
    {
        if (Volatile.Read(ref _owner) != attempt)
        {
            Log.Info("Direct network: the attempt given up on didn't start anything new; leaving it as it is");
            return Task.CompletedTask;
        }

        return StopAllAsync();
    }

    /// <summary>Beam closed while this start or join ran (closing doesn't wait for it forever): don't leave it on.</summary>
    private async Task<T> WhileOpenAsync<T>(Task<T> operation)
    {
        var result = await operation.ConfigureAwait(false);
        if (!_closed) return result;
        Log.Info("Direct network: Beam closed meanwhile; stopping it again");
        await StopAllAsync().ConfigureAwait(false);
        throw new DirectLinkException(L.T("This device can't connect directly."));
    }

    private async Task<DirectNetwork> HostCoreAsync(DirectNetwork? preferred, CancellationToken cancellationToken)
    {
        {
#if WINDOWS_WINRT
            await WaitForRestoreAsync(cancellationToken).ConfigureAwait(false);
            await EnsureWifiOnAsync().ConfigureAwait(false);
            if (_joinedAdapter != null)
            {
                Log.Info($"Wi-Fi: leaving {_joinedSsid} before starting this PC's own network");
                await LeaveJoinedNetworkAsync().ConfigureAwait(false);
            }

            if (_publisher?.Status == WiFiDirectAdvertisementPublisherStatus.Started && _network != null
                && (preferred == null || preferred == _network)) return _network;
            if (_hotspot?.TetheringOperationalState == TetheringOperationalState.On && _network != null
                && (preferred == null || preferred == _network)) return _network;
            StopPublisher();
            await StopHotspotAsync().ConfigureAwait(false);
            var network = preferred ?? DirectNetwork.Create(_deviceName());
            // A Wi-Fi Direct network first: phones join it reliably. Windows' Mobile hotspot (which shares this PC's
            // internet) only when that can't start: phones' join requests to it often went unanswered.
            try
            {
                return await StartPublisherAsync(network, cancellationToken).ConfigureAwait(false);
            }
            catch (DirectLinkException ex) when (!cancellationToken.IsCancellationRequested)
            {
                Log.Info($"Wi-Fi Direct: {network.Ssid} didn't start ({ex.Message}); trying the Mobile hotspot");
                if (await TryStartHotspotAsync(network, cancellationToken).ConfigureAwait(false))
                {
                    _network = network with { SharesInternet = true };
                    OwnedByRunningAttempt();
                    return _network;
                }

                throw;
            }
#else
            await Task.CompletedTask.ConfigureAwait(false);
            throw new DirectLinkException(L.T("This device can't connect directly."));
#endif
        }
    }

#if WINDOWS_WINRT
    private async Task<DirectNetwork> StartPublisherAsync(DirectNetwork network, CancellationToken cancellationToken)
    {
        {
            Log.Info($"Wi-Fi Direct: starting {network.Ssid}");
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
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
                // Not kept anywhere yet: stop it here, or it could start later with nobody to stop it.
                StopUntracked(publisher);
                if (ex is OperationCanceledException) throw;
                throw new DirectLinkException(L.T("This computer's Wi-Fi didn't start the direct connection in time. Make sure Wi-Fi is turned on."));
            }

            if (result.Status != WiFiDirectAdvertisementPublisherStatus.Started)
            {
                StopUntracked(publisher);
                throw new DirectLinkException(result.Error == WiFiDirectError.ResourceInUse
                    ? L.T("This computer's Wi-Fi is busy with another direct connection or a mobile hotspot. Turn off the hotspot and try again.")
                    : L.T("This computer can't start a direct connection: its Wi-Fi doesn't support Wi-Fi Direct, or Wi-Fi is turned off."));
            }

            _publisher = publisher;
            _network = network;
            OwnedByRunningAttempt();
            Log.Info($"Wi-Fi Direct network {network.Ssid} started");
            return network;
        }
    }
#endif

    /// <summary>Joins another computer's direct network with this PC's Wi-Fi (the PC leaves its current Wi-Fi meanwhile).</summary>
    public Task<IPAddress?> JoinAsync(DirectNetwork network, CancellationToken cancellationToken)
    {
        var attempt = 0L;
        return _radio.RunAsync($"joining {network.Ssid}", JoinTimeout,
            token =>
            {
                attempt = BeginAttempt();
                return WhileOpenAsync(JoinCoreAsync(network, token));
            },
            () =>
            {
                Log.Warn($"Wi-Fi: joining {network.Ssid} didn't finish in time");
                return new DirectLinkException(L.T("This computer couldn't join the other computer's network. Move the computers closer together and try again."));
            },
            undoLateResult: _ => UndoAttemptAsync(attempt), // joined after all, too late: leave again
            cancellationToken);
    }

    private async Task<IPAddress?> JoinCoreAsync(DirectNetwork network, CancellationToken cancellationToken)
    {
#if WINDOWS_WINRT
        {
            await WaitForRestoreAsync(cancellationToken).ConfigureAwait(false);
            await EnsureWifiOnAsync().ConfigureAwait(false);
            // This PC's own network must go first: every Windows hotspot is 192.168.137.1, so with its own still on, this
            // PC would reach itself at the other's address.
            if (_publisher != null || _hotspot != null)
            {
                Log.Info($"Wi-Fi: stopping this PC's own network {_network?.Ssid} before joining {network.Ssid}");
                StopPublisher();
                await StopHotspotAsync().ConfigureAwait(false);
                _network = null;
            }

            if (await WiFiAdapter.RequestAccessAsync().AsTask(cancellationToken).ConfigureAwait(false) != WiFiAccessStatus.Allowed)
                throw new DirectLinkException(L.T("Windows didn't let Beam use Wi-Fi. In Windows Settings → Privacy & security → Location, turn on location access and let desktop apps use it, then try again."));
            var adapters = await WiFiAdapter.FindAllAdaptersAsync().AsTask(cancellationToken).ConfigureAwait(false);
            var adapter = adapters.FirstOrDefault()
                          ?? throw new DirectLinkException(L.T("This computer has no Wi-Fi, so it can't join the other computer's network."));

            var current = await ConnectedSsidAsync(adapter).ConfigureAwait(false);
            if (current == network.Ssid && _joinedAdapter != null) return await WaitForHostAsync(adapter, cancellationToken).ConfigureAwait(false);

            WiFiAvailableNetwork? target = null;
            var seen = 0;
            for (var attempt = 0; attempt < 3 && target == null; attempt++)
            {
                if (attempt > 0) await Task.Delay(1500, cancellationToken).ConfigureAwait(false);
                await adapter.ScanAsync().AsTask(cancellationToken).ConfigureAwait(false);
                seen = adapter.NetworkReport.AvailableNetworks.Count;
                target = adapter.NetworkReport.AvailableNetworks.FirstOrDefault(n => n.Ssid == network.Ssid);
            }

            _previousSsid ??= current;
            Log.Info($"Wi-Fi: joining {network.Ssid}{(current != null ? $" (leaving {current} meanwhile)" : "")}");
            try
            {
                return await ConnectAndWaitAsync(adapter, network, target, seen, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Given up while Windows was connecting: it may still get there, and this PC would stay on that network
                // with nothing to bring it back.
                Log.Info($"Wi-Fi: joining {network.Ssid} was given up; going back");
                if (_joinedAdapter != null) await LeaveJoinedNetworkAsync().ConfigureAwait(false);
                else await AbandonJoinAsync(adapter, network.Ssid).ConfigureAwait(false);
                throw;
            }
        }
#else
        await Task.CompletedTask.ConfigureAwait(false);
        throw new DirectLinkException(L.T("This device can't connect directly."));
#endif
    }

#if WINDOWS_WINRT
    private async Task<IPAddress?> ConnectAndWaitAsync(WiFiAdapter adapter, DirectNetwork network, WiFiAvailableNetwork? target, int seen,
        CancellationToken cancellationToken)
    {
        bool joined;
        if (target != null)
        {
            var result = await adapter.ConnectAsync(target, WiFiReconnectionKind.Manual, new PasswordCredential { Password = network.Passphrase })
                .AsTask(cancellationToken).ConfigureAwait(false);
            joined = result.ConnectionStatus == WiFiConnectionStatus.Success;
            if (!joined) Log.Warn($"Wi-Fi: joining {network.Ssid} failed ({result.ConnectionStatus})");
        }
        else
        {
            // Windows 11 hides Wi-Fi scan results from desktop apps unless location access is on ({seen} networks
            // seen): join by name through a Wi-Fi profile instead, which needs no scan.
            Log.Info($"Wi-Fi: {network.Ssid} not in the scan ({seen} networks seen); joining it by name");
            _addedProfile = network.Ssid; // before adding it: removed again however the join ends
            joined = await WlanProfiles.ConnectAsync(network.Ssid, network.Passphrase, cancellationToken).ConfigureAwait(false)
                     && await WaitForSsidAsync(adapter, network.Ssid, cancellationToken).ConfigureAwait(false);
        }

        if (!joined)
        {
            await AbandonJoinAsync(adapter, network.Ssid).ConfigureAwait(false);
            throw new DirectLinkException(seen == 0
                ? L.T("The other computer's network isn't in range. Move the computers closer together; if it still isn't found, turn on location access for desktop apps in Windows Settings → Privacy & security → Location.")
                : L.T("This computer couldn't join the other computer's network. Move the computers closer together and try again."));
        }

        _joinedAdapter = adapter;
        _joinedSsid = network.Ssid;
        OwnedByRunningAttempt();
        NetworkInformation.NetworkStatusChanged -= OnNetworkStatusChanged;
        NetworkInformation.NetworkStatusChanged += OnNetworkStatusChanged;
        var host = await WaitForHostAsync(adapter, cancellationToken).ConfigureAwait(false);
        Log.Info($"Wi-Fi: connected to {network.Ssid}, host at {host}");
        return host;
    }

    /// <summary>A join that didn't work out: off that network if Windows got there anyway, and back to the previous Wi-Fi.</summary>
    private async Task AbandonJoinAsync(WiFiAdapter adapter, string ssid)
    {
        try
        {
            if (await ConnectedSsidAsync(adapter).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false) == ssid) adapter.Disconnect();
        }
        catch (Exception ex)
        {
            Log.Warn($"Wi-Fi: leaving {ssid} failed: {ex.Message}");
        }

        TrackRestore(ReturnToPreviousWiFiAsync(adapter));
        if (Interlocked.Exchange(ref _addedProfile, null) is { } added) TrackRestore(WlanProfiles.RemoveAsync(added));
    }
#endif

    /// <summary>Beam is closing: stop the network now and wait for it (the caller limits how long).</summary>
    public async ValueTask DisposeAsync()
    {
        _closed = true; // a start or join still running stops what it set up when it ends
        Interlocked.Increment(ref _ops);
        await _radio.RunExclusiveAsync("closing", TimeSpan.FromSeconds(8), StopAllAsync, runAnyway: true).ConfigureAwait(false); // stop anyway if something hangs
    }

    /// <summary>Stops this PC's network and leaves another's (the caller holds the radio, or Beam is closing).</summary>
    private async Task StopAllAsync()
    {
#if WINDOWS_WINRT
        StopPublisher();
        await StopHotspotAsync().ConfigureAwait(false);
        await LeaveJoinedNetworkAsync().ConfigureAwait(false);
#else
        await Task.CompletedTask.ConfigureAwait(false);
#endif
        _network = null;
    }

    /// <summary>
    /// Stops this PC's network (and leaves another computer's) in the background: never blocks the caller (often the
    /// window), and is skipped when the network was started again after the stop was asked for.
    /// </summary>
    public void Stop()
    {
        var op = Interlocked.Increment(ref _ops);
        // Queued behind any start or join still running (so it can't run alongside one), and skipped when a newer
        // start got there first.
        _ = _radio.RunExclusiveAsync("stopping", TimeSpan.FromMinutes(2), async () =>
        {
            if (Volatile.Read(ref _lastStartOp) > op) return; // started again meanwhile: keep it
            try
            {
                await StopAllAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warn("Direct network: stopping failed", ex);
            }
        });
    }

#if WINDOWS_WINRT
    private static async Task<string?> ConnectedSsidAsync(WiFiAdapter adapter)
    {
        try
        {
            var profile = await adapter.NetworkAdapter.GetConnectedProfileAsync().AsTask().ConfigureAwait(false);
            return profile?.IsWlanConnectionProfile == true ? profile.WlanConnectionProfileDetails.GetConnectedSsid() : null;
        }
        catch (Exception ex)
        {
            Log.Warn("Wi-Fi: reading the current network failed", ex);
            return null;
        }
    }

    private static async Task<bool> WaitForSsidAsync(WiFiAdapter adapter, string ssid, CancellationToken cancellationToken)
    {
        var deadline = Environment.TickCount64 + 25_000;
        while (Environment.TickCount64 < deadline)
        {
            if (await ConnectedSsidAsync(adapter).ConfigureAwait(false) == ssid) return true;
            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }

        Log.Warn($"Wi-Fi: not on {ssid} after joining it by name");
        return false;
    }

    /// <summary>The other computer's address on its network (its gateway; Windows hosts are 192.168.137.1).</summary>
    private static async Task<IPAddress> WaitForHostAsync(WiFiAdapter adapter, CancellationToken cancellationToken)
    {
        var id = adapter.NetworkAdapter.NetworkAdapterId;
        var deadline = Environment.TickCount64 + 15_000;
        while (Environment.TickCount64 < deadline)
        {
            var nic = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(n => Guid.TryParse(n.Id, out var nicId) && nicId == id);
            var gateway = nic?.GetIPProperties().GatewayAddresses
                .Select(g => g.Address).FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !a.Equals(IPAddress.Any));
            if (gateway != null) return gateway;
            await Task.Delay(300, cancellationToken).ConfigureAwait(false);
        }

        return IPAddress.Parse("192.168.137.1");
    }

    /// <summary>Windows dropped the other computer's network (it stopped, or went out of range).</summary>
    private async void OnNetworkStatusChanged(object? sender)
    {
        var adapter = _joinedAdapter;
        if (adapter == null) return;
        try
        {
            await Task.Delay(1000).ConfigureAwait(false); // let the change settle
            if (_joinedAdapter == null || await ConnectedSsidAsync(adapter).ConfigureAwait(false) == _joinedSsid) return;
            Log.Warn($"Wi-Fi: lost {_joinedSsid}");
            NetworkInformation.NetworkStatusChanged -= OnNetworkStatusChanged;
            _joinedAdapter = null;
            _joinedSsid = null;
            TrackRestore(ReturnToPreviousWiFiAsync(adapter));
            Lost?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Warn("Wi-Fi: checking the network failed", ex);
        }
    }

    private async Task LeaveJoinedNetworkAsync()
    {
        var adapter = _joinedAdapter;
        var ssid = _joinedSsid;
        NetworkInformation.NetworkStatusChanged -= OnNetworkStatusChanged;
        _joinedAdapter = null;
        if (adapter == null) return;
        Log.Info($"Wi-Fi: leaving {ssid}");
        _joinedSsid = null;
        // Windows may already be back on the user's own Wi-Fi (the other network went away a moment ago): disconnecting
        // now would drop that connection, and transfers over it, instead.
        string? now = null;
        try
        {
            now = await ConnectedSsidAsync(adapter).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Unknown: disconnect as before.
        }

        if (now != null && now != ssid)
        {
            Log.Info($"Wi-Fi: already back on {now}; not disconnecting");
        }
        else
        {
            try
            {
                adapter.Disconnect();
            }
            catch (Exception ex)
            {
                Log.Warn("Wi-Fi: disconnecting failed", ex);
            }
        }

        TrackRestore(ReturnToPreviousWiFiAsync(adapter));
        if (Interlocked.Exchange(ref _addedProfile, null) is { } added) TrackRestore(WlanProfiles.RemoveAsync(added));
    }

    /// <summary>Keeps background clean-up (back to the previous Wi-Fi) so the next start or join waits for it.</summary>
    private void TrackRestore(Task task)
    {
        lock (_restoreGate)
        {
            var before = _restoring;
            _restoring = Task.WhenAll(before, task.ContinueWith(t => _ = t.Exception, TaskScheduler.Default));
        }
    }

    /// <summary>
    /// Waits (a while) for clean-up of an earlier join: it reconnects the Wi-Fi to the previous network, which mustn't
    /// happen in the middle of the next join.
    /// </summary>
    private async Task WaitForRestoreAsync(CancellationToken cancellationToken)
    {
        Task pending;
        lock (_restoreGate) pending = _restoring;
        if (pending.IsCompleted) return;
        try
        {
            Log.Info("Wi-Fi: waiting for the previous network to come back first");
            await pending.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Log.Warn("Wi-Fi: going back to the previous network is taking long; going ahead");
        }
    }

    /// <summary>Back to the Wi-Fi this PC was on before joining (Windows usually does this by itself; this makes sure).</summary>
    private async Task ReturnToPreviousWiFiAsync(WiFiAdapter adapter)
    {
        var previous = _previousSsid;
        _previousSsid = null;
        if (string.IsNullOrEmpty(previous)) return;
        try
        {
            await Task.Delay(1500).ConfigureAwait(false);
            if (await ConnectedSsidAsync(adapter).ConfigureAwait(false) is { } now && now != _joinedSsid) return; // already back
            await adapter.ScanAsync().AsTask().ConfigureAwait(false);
            var network = adapter.NetworkReport.AvailableNetworks.FirstOrDefault(n => n.Ssid == previous);
            if (network != null)
            {
                var result = await adapter.ConnectAsync(network, WiFiReconnectionKind.Automatic).AsTask().ConfigureAwait(false); // its saved profile
                Log.Info($"Wi-Fi: back on {previous} ({result.ConnectionStatus})");
                if (result.ConnectionStatus == WiFiConnectionStatus.Success) return;
            }

            // No scan results (location access off), or Windows turned the connection down: its saved profile by name.
            Log.Info($"Wi-Fi: back on {previous} by name ({await WlanProfiles.ReconnectAsync(previous).ConfigureAwait(false)})");
        }
        catch (Exception ex)
        {
            Log.Warn($"Wi-Fi: going back to {previous} failed ({ex.Message}); trying its saved profile by name");
            try
            {
                Log.Info($"Wi-Fi: back on {previous} by name ({await WlanProfiles.ReconnectAsync(previous).ConfigureAwait(false)})");
            }
            catch (Exception again)
            {
                Log.Warn($"Wi-Fi: going back to {previous} by name failed: {again.Message}");
            }
        }
    }

    /// <summary>Wi-Fi is switched off (airplane mode, or in quick settings): Beam turns it on itself.</summary>
    private static async Task EnsureWifiOnAsync()
    {
        try
        {
            var wifi = (await Radio.GetRadiosAsync()).FirstOrDefault(r => r.Kind == RadioKind.WiFi);
            if (wifi == null || wifi.State == RadioState.On) return;
            if (await Radio.RequestAccessAsync() != RadioAccessStatus.Allowed)
            {
                Log.Info("Wi-Fi: it is off, and Windows doesn't let Beam turn it on");
                return;
            }

            var result = await wifi.SetStateAsync(RadioState.On);
            Log.Info($"Wi-Fi: it was off; turning it on: {result}");
            for (var i = 0; i < 20 && wifi.State != RadioState.On; i++) await Task.Delay(250).ConfigureAwait(false);
            await Task.Delay(1500).ConfigureAwait(false); // the adapter needs a moment before it can host or join
        }
        catch (Exception ex)
        {
            Log.Warn($"Wi-Fi: couldn't check whether it is on: {ex.Message}");
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
#endif
}
