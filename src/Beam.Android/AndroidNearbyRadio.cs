using System.Collections.Concurrent;
using Android;
using Android.Bluetooth;
using Android.Bluetooth.LE;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Beam.Core.Diagnostics;
using Beam.Core.Nearby;
using Java.Util;

namespace Beam.Droid;

/// <summary>
/// Bluetooth LE on Android. Receive: one connectable advertisement, the beacon (manufacturer data) with Beam's service
/// in the scan response, and a GATT server for the handshake. Send: a scanner and a GATT client. Android 12+ asks once
/// for "Nearby devices" (scan, advertise, connect; never used for location); older versions use the location
/// permission Beam already asks for direct connections.
/// </summary>
internal sealed class AndroidNearbyRadio : INearbyRadio
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);

    private readonly Context _context;
    private readonly BluetoothManager? _manager;
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, byte[]> _writes = new();
    private readonly ConcurrentDictionary<string, long> _serviceSeen = new();
    private INearbyResponder? _responder;
    private BluetoothGattServer? _server;
    private Advertising? _advertising;
    private Scanning? _scanning;
    private Action<NearbySighting>? _seen;
    private CancellationTokenSource? _stopScanLater;

    public AndroidNearbyRadio(Context context)
    {
        _context = context;
        if (context.PackageManager?.HasSystemFeature(PackageManager.FeatureBluetoothLe) == true)
            _manager = context.GetSystemService(Context.BluetoothService) as BluetoothManager;
        if (_manager == null) return;
        var receiver = new StateReceiver(() => StateChanged?.Invoke());
        var filter = new IntentFilter(BluetoothAdapter.ActionStateChanged);
        if (OperatingSystem.IsAndroidVersionAtLeast(33)) context.RegisterReceiver(receiver, filter, ReceiverFlags.NotExported);
        else context.RegisterReceiver(receiver, filter);
    }

    private BluetoothAdapter? Adapter => _manager?.Adapter;

    public NearbyRadioState State
    {
        get
        {
            if (Adapter is not { } adapter) return NearbyRadioState.Unsupported;
            if (!HasPermissions()) return NearbyRadioState.NeedsPermission;
            return adapter.IsEnabled ? NearbyRadioState.Ready : NearbyRadioState.Off;
        }
    }

    public event Action? StateChanged;

    private static string[] Permissions => OperatingSystem.IsAndroidVersionAtLeast(31)
        ? new[] { Manifest.Permission.BluetoothScan, Manifest.Permission.BluetoothAdvertise, Manifest.Permission.BluetoothConnect }
        : new[] { Manifest.Permission.AccessFineLocation };

    private bool HasPermissions() => Permissions.All(p => _context.CheckSelfPermission(p) == Permission.Granted);

    public async Task<bool> RequestPermissionAsync()
    {
        if (HasPermissions()) return true;
        var granted = await AndroidHost.RequestPermissionsAsync(Permissions).ConfigureAwait(false);
        StateChanged?.Invoke();
        return granted;
    }

    // ---- Receiving ----

    public void StartAdvertising(byte[] beacon, INearbyResponder responder)
    {
        lock (_gate)
        {
            StopAdvertisingLocked();
            _responder = responder;
            if (Adapter?.BluetoothLeAdvertiser is not { } advertiser)
            {
                Log.Warn("Nearby: this phone can't advertise over Bluetooth LE");
                return;
            }

            if (_server == null)
            {
                _server = _manager!.OpenGattServer(_context, new ServerCallback(this));
                if (_server == null)
                {
                    Log.Warn("Nearby: Beam's Bluetooth service couldn't start");
                    return;
                }

                var service = new BluetoothGattService(Uuid(NearbyUuids.Service), GattServiceType.Primary);
                service.AddCharacteristic(new BluetoothGattCharacteristic(Uuid(NearbyUuids.Handshake), GattProperty.Read | GattProperty.Write,
                    GattPermission.Read | GattPermission.Write));
                _server.AddService(service);
            }

            var settings = new AdvertiseSettings.Builder()
                .SetAdvertiseMode(AdvertiseMode.LowLatency)!
                .SetConnectable(true)!
                .SetTimeout(0)!
                .SetTxPowerLevel(AdvertiseTx.PowerHigh)!
                .Build();
            var data = new AdvertiseData.Builder()
                .SetIncludeDeviceName(false)!
                .AddManufacturerData(NearbyBeacon.CompanyId, beacon)!
                .Build();
            var scanResponse = new AdvertiseData.Builder()
                .SetIncludeDeviceName(false)!
                .AddServiceUuid(new ParcelUuid(Uuid(NearbyUuids.Service)))!
                .Build();
            _advertising = new Advertising();
            advertiser.StartAdvertising(settings, data, scanResponse, _advertising);
        }
    }

    public void StopAdvertising()
    {
        lock (_gate)
        {
            StopAdvertisingLocked();
            try
            {
                _server?.Close();
            }
            catch (Exception ex)
            {
                Log.Warn($"Nearby: closing Beam's Bluetooth service failed: {ex.Message}");
            }

            _server = null;
        }
    }

    private void StopAdvertisingLocked()
    {
        if (_advertising == null) return;
        try
        {
            Adapter?.BluetoothLeAdvertiser?.StopAdvertising(_advertising);
        }
        catch (Exception ex)
        {
            Log.Warn($"Nearby: stopping the beacon failed: {ex.Message}");
        }

        _advertising = null;
        _writes.Clear();
    }

    private void OnWrite(BluetoothDevice device, int requestId, bool prepared, bool responseNeeded, int offset, byte[] value)
    {
        var key = device.Address ?? "";
        // Binder threads can deliver callbacks at the same time: pieces of a long write are put together one at a time.
        lock (_writes)
        {
            var buffer = offset == 0 && !prepared ? value : Place(_writes.GetValueOrDefault(key) ?? Array.Empty<byte>(), offset, value);
            if (prepared) _writes[key] = buffer; // completed by OnExecuteWrite
            else Deliver(key, buffer);
        }

        if (responseNeeded) _server?.SendResponse(device, requestId, GattStatus.Success, offset, value);
    }

    private void OnExecuteWrite(BluetoothDevice device, int requestId, bool execute)
    {
        var key = device.Address ?? "";
        lock (_writes)
            if (_writes.TryRemove(key, out var buffer) && execute) Deliver(key, buffer);
        _server?.SendResponse(device, requestId, GattStatus.Success, 0, null);
    }

    private void Deliver(string key, byte[] buffer)
    {
        if (NearbyHandshake.ExpectedLength(buffer) is not { } length || buffer.Length < length)
        {
            _writes[key] = buffer;
            return;
        }

        _writes.TryRemove(key, out _);
        _responder?.Request(key, buffer[..length]);
    }

    private static byte[] Place(byte[] existing, int offset, byte[] data)
    {
        if (offset > 1024) return existing;
        var result = new byte[Math.Max(existing.Length, offset + data.Length)];
        existing.CopyTo(result, 0);
        data.CopyTo(result, offset);
        return result;
    }

    private void OnRead(BluetoothDevice device, int requestId, int offset)
    {
        var value = _responder?.Response(device.Address ?? "") ?? NearbyHandshake.Refuse("Not receiving.");
        if (offset > value.Length) _server?.SendResponse(device, requestId, GattStatus.InvalidOffset, offset, null);
        else _server?.SendResponse(device, requestId, GattStatus.Success, offset, value[offset..]);
    }

    private sealed class ServerCallback : BluetoothGattServerCallback
    {
        private readonly AndroidNearbyRadio _radio;

        public ServerCallback(AndroidNearbyRadio radio) => _radio = radio;

        public override void OnConnectionStateChange(BluetoothDevice? device, ProfileState status, ProfileState newState)
        {
            if (device != null && newState == ProfileState.Disconnected)
            {
                _radio._writes.TryRemove(device.Address ?? "", out _);
                _radio._responder?.Forget(device.Address ?? "");
            }
        }

        public override void OnCharacteristicWriteRequest(BluetoothDevice? device, int requestId, BluetoothGattCharacteristic? characteristic, bool preparedWrite,
            bool responseNeeded, int offset, byte[]? value)
        {
            if (device == null) return;
            try
            {
                _radio.OnWrite(device, requestId, preparedWrite, responseNeeded, offset, value ?? Array.Empty<byte>());
            }
            catch (Exception ex)
            {
                Log.Warn($"Nearby: Bluetooth write failed: {ex.Message}");
            }
        }

        public override void OnExecuteWrite(BluetoothDevice? device, int requestId, bool execute)
        {
            if (device == null) return;
            try
            {
                _radio.OnExecuteWrite(device, requestId, execute);
            }
            catch (Exception ex)
            {
                Log.Warn($"Nearby: Bluetooth write failed: {ex.Message}");
            }
        }

        public override void OnCharacteristicReadRequest(BluetoothDevice? device, int requestId, int offset, BluetoothGattCharacteristic? characteristic)
        {
            if (device == null) return;
            try
            {
                _radio.OnRead(device, requestId, offset);
            }
            catch (Exception ex)
            {
                Log.Warn($"Nearby: Bluetooth read failed: {ex.Message}");
            }
        }
    }

    private sealed class Advertising : AdvertiseCallback
    {
        public override void OnStartSuccess(AdvertiseSettings? settingsInEffect) => Log.Info("Nearby: Bluetooth beacon on");

        public override void OnStartFailure(AdvertiseFailure errorCode) => Log.Warn($"Nearby: Bluetooth beacon failed ({errorCode})");
    }

    // ---- Sending ----

    public void StartScanning(Action<NearbySighting> seen)
    {
        lock (_gate)
        {
            _seen = seen;
            _stopScanLater?.Cancel();
            _stopScanLater = null;
            if (_scanning != null) return;
            if (Adapter?.BluetoothLeScanner is not { } scanner) return;
            var settings = new ScanSettings.Builder().SetScanMode(global::Android.Bluetooth.LE.ScanMode.LowLatency)!.Build();
            _scanning = new Scanning(this);
            scanner.StartScan(null, settings, _scanning);
        }

        Log.Info("Nearby: looking for receivers over Bluetooth");
    }

    /// <summary>Android blocks apps that start scanning more than 5 times in 30 s: stop only after a quiet moment.</summary>
    public void StopScanning()
    {
        CancellationTokenSource later;
        lock (_gate)
        {
            if (_scanning == null || _stopScanLater != null) return;
            later = _stopScanLater = new CancellationTokenSource();
        }

        _ = Task.Delay(TimeSpan.FromSeconds(15), later.Token).ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            lock (_gate)
            {
                if (_stopScanLater != later) return;
                _stopScanLater = null;
                try
                {
                    if (_scanning != null) Adapter?.BluetoothLeScanner?.StopScan(_scanning);
                }
                catch (Exception ex)
                {
                    Log.Warn($"Nearby: stopping the scan failed: {ex.Message}");
                }

                _scanning = null;
            }
        }, TaskScheduler.Default);
    }

    private void OnScanResult(ScanResult? result)
    {
        if (result?.Device?.Address is not { } address || result.ScanRecord is not { } record) return;
        if (record.ServiceUuids?.Any(u => u.Uuid?.ToString() == NearbyUuids.Service.ToString()) == true) _serviceSeen[address] = System.Environment.TickCount64;
        if (record.GetManufacturerSpecificData(NearbyBeacon.CompanyId) is { } data && NearbyBeacon.TryParse(data) is { } beacon)
            _seen?.Invoke(new NearbySighting(address, beacon, result.Rssi));
    }

    private sealed class Scanning : ScanCallback
    {
        private readonly AndroidNearbyRadio _radio;

        public Scanning(AndroidNearbyRadio radio) => _radio = radio;

        public override void OnScanResult(ScanCallbackType callbackType, ScanResult? result)
        {
            try
            {
                _radio.OnScanResult(result);
            }
            catch (Exception ex)
            {
                Log.Warn($"Nearby: reading an advertisement failed: {ex.Message}");
            }
        }

        public override void OnBatchScanResults(IList<ScanResult>? results)
        {
            foreach (var result in results ?? Array.Empty<ScanResult>()) OnScanResult(ScanCallbackType.AllMatches, result);
        }

        public override void OnScanFailed(ScanFailure errorCode)
        {
            Log.Warn($"Nearby: Bluetooth scan failed ({errorCode})");
            lock (_radio._gate) _radio._scanning = null;
        }
    }

    public IReadOnlyList<string> RecentServiceAddresses() =>
        _serviceSeen.Where(s => System.Environment.TickCount64 - s.Value < 30_000).OrderByDescending(s => s.Value).Select(s => s.Key).ToList();

    public async Task<INearbyConnection> ConnectAsync(string address, CancellationToken cancellationToken)
    {
        if (Adapter is not { } adapter) throw new IOException("No Bluetooth.");
        try
        {
            return await Client.ConnectAsync(_context, adapter.GetRemoteDevice(address)!, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException ex) when (ex is not MissingServiceException && ex.Message.Contains("status 133", StringComparison.Ordinal))
        {
            // Status 133 right after a scan is common: once more.
            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            return await Client.ConnectAsync(_context, adapter.GetRemoteDevice(address)!, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Android doesn't let apps switch Bluetooth on by themselves (since Android 13): it shows its own one-tap
    /// "Allow Beam to turn on Bluetooth?" prompt instead.
    /// </summary>
    public Task<bool> TurnOnAsync()
    {
        if (Adapter is not { } adapter) return Task.FromResult(false);
        if (adapter.IsEnabled) return Task.FromResult(true);
        if (!HasPermissions() || AndroidHost.CurrentActivity is not { } activity) return Task.FromResult(false);
        activity.RunOnUiThread(() =>
        {
            try
            {
                activity.StartActivity(new Intent(BluetoothAdapter.ActionRequestEnable));
            }
            catch (Exception ex)
            {
                Log.Warn($"Nearby: couldn't ask to turn Bluetooth on: {ex.Message}");
            }
        });
        return Task.FromResult(true);
    }

    private sealed class MissingServiceException : IOException
    {
        public MissingServiceException() : base("No Beam service there.")
        {
        }
    }

    /// <summary>A GATT client connection: one operation at a time, each answered through the callback.</summary>
    private sealed class Client : BluetoothGattCallback, INearbyConnection
    {
        private readonly TaskCompletionSource<bool> _connected = NewSource<bool>();
        private TaskCompletionSource<bool>? _discovered;
        private TaskCompletionSource<bool>? _mtu;
        private TaskCompletionSource<bool>? _written;
        private TaskCompletionSource<byte[]>? _read;
        private BluetoothGatt? _gatt;
        private BluetoothGattCharacteristic? _characteristic;

        public static async Task<Client> ConnectAsync(Context context, BluetoothDevice device, CancellationToken cancellationToken)
        {
            var client = new Client();
            try
            {
                client._gatt = device.ConnectGatt(context, false, client, BluetoothTransports.Le) ?? throw new IOException("Couldn't connect.");
                await client._connected.Task.WaitAsync(ConnectTimeout, cancellationToken).ConfigureAwait(false);
                client._mtu = NewSource<bool>();
                if (client._gatt.RequestMtu(185))
                {
                    try
                    {
                        await client._mtu.Task.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
                    }
                    catch (TimeoutException)
                    {
                        // Long reads and writes work without it, just slower.
                    }
                }

                client._discovered = NewSource<bool>();
                if (!client._gatt.DiscoverServices()) throw new IOException("Couldn't look for Beam's service.");
                await client._discovered.Task.WaitAsync(ConnectTimeout, cancellationToken).ConfigureAwait(false);
                client._characteristic = client._gatt.GetService(Uuid(NearbyUuids.Service))?.GetCharacteristic(Uuid(NearbyUuids.Handshake))
                                         ?? throw new MissingServiceException();
                return client;
            }
            catch
            {
                await client.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        private static TaskCompletionSource<T> NewSource<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task WriteAsync(byte[] data, CancellationToken cancellationToken)
        {
            var written = _written = NewSource<bool>();
#pragma warning disable CA1422 // the Android 13 overload's binding differs between versions; this one works everywhere
            _characteristic!.WriteType = GattWriteType.Default;
            _characteristic.SetValue(data);
            var started = _gatt!.WriteCharacteristic(_characteristic);
#pragma warning restore CA1422
            if (!started) throw new IOException("Bluetooth write didn't start.");
            await written.Task.WaitAsync(ConnectTimeout, cancellationToken).ConfigureAwait(false);
        }

        public async Task<byte[]> ReadAsync(CancellationToken cancellationToken)
        {
            var read = _read = NewSource<byte[]>();
            if (!_gatt!.ReadCharacteristic(_characteristic)) throw new IOException("Bluetooth read didn't start.");
            return await read.Task.WaitAsync(ConnectTimeout, cancellationToken).ConfigureAwait(false);
        }

        public override void OnConnectionStateChange(BluetoothGatt? gatt, GattStatus status, ProfileState newState)
        {
            if (newState == ProfileState.Connected && status == GattStatus.Success)
            {
                _connected.TrySetResult(true);
                return;
            }

            var lost = new IOException($"Bluetooth connection {(newState == ProfileState.Connected ? "failed" : "lost")} (status {(int)status})");
            _connected.TrySetException(lost);
            _discovered?.TrySetException(lost);
            _written?.TrySetException(lost);
            _read?.TrySetException(lost);
        }

        public override void OnMtuChanged(BluetoothGatt? gatt, int mtu, GattStatus status) => _mtu?.TrySetResult(status == GattStatus.Success);

        public override void OnServicesDiscovered(BluetoothGatt? gatt, GattStatus status)
        {
            if (status == GattStatus.Success) _discovered?.TrySetResult(true);
            else _discovered?.TrySetException(new IOException($"Looking for Beam's service failed (status {(int)status})"));
        }

        public override void OnCharacteristicWrite(BluetoothGatt? gatt, BluetoothGattCharacteristic? characteristic, GattStatus status)
        {
            if (status == GattStatus.Success) _written?.TrySetResult(true);
            else _written?.TrySetException(new IOException($"Bluetooth write failed (status {(int)status})"));
        }

        public override void OnCharacteristicRead(BluetoothGatt gatt, BluetoothGattCharacteristic characteristic, byte[] value, GattStatus status)
        {
            if (status == GattStatus.Success) _read?.TrySetResult(value);
            else _read?.TrySetException(new IOException($"Bluetooth read failed (status {(int)status})"));
        }

#pragma warning disable CS0672, CA1422 // before Android 13 only this one is called
        public override void OnCharacteristicRead(BluetoothGatt? gatt, BluetoothGattCharacteristic? characteristic, GattStatus status)
        {
            if (status == GattStatus.Success) _read?.TrySetResult(characteristic?.GetValue() ?? Array.Empty<byte>());
            else _read?.TrySetException(new IOException($"Bluetooth read failed (status {(int)status})"));
        }
#pragma warning restore CS0672, CA1422

        public ValueTask DisposeAsync()
        {
            try
            {
                _gatt?.Disconnect();
                _gatt?.Close();
            }
            catch (Exception ex)
            {
                Log.Warn($"Nearby: closing the Bluetooth connection failed: {ex.Message}");
            }

            _gatt = null;
            return ValueTask.CompletedTask;
        }
    }

    private static UUID Uuid(Guid guid) => UUID.FromString(guid.ToString())!;

    private sealed class StateReceiver : BroadcastReceiver
    {
        private readonly Action _changed;

        public StateReceiver(Action changed) => _changed = changed;

        public override void OnReceive(Context? context, Intent? intent) => _changed();
    }
}
