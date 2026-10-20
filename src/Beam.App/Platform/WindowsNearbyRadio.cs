using System.Collections.Concurrent;
using Beam.Core.Diagnostics;
using Beam.Core.Nearby;
#if WINDOWS_WINRT
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Radios;
#endif

namespace Beam.App.Platform;

/// <summary>
/// Bluetooth LE on Windows (WinRT, works in desktop apps without asking the user). Receive: a beacon (advertisement
/// publisher, manufacturer data) plus Beam's GATT service, connectable, for the handshake; the GATT server needs a
/// Bluetooth adapter that supports the peripheral role, which most do. Send: an advertisement watcher and a GATT client.
/// The beacon and the service are two advertisements; Windows sends both from the adapter's address, and a sender that
/// can't find the service at the beacon's address tries addresses that advertised the service recently.
/// </summary>
internal sealed class WindowsNearbyRadio : INearbyRadio
{
    private NearbyRadioState _state = NearbyRadioState.Unsupported;
#if WINDOWS_WINRT
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, byte[]> _writes = new();
    private readonly ConcurrentDictionary<ulong, long> _serviceSeen = new();

    /// <summary>
    /// Whether each address heard is public or random. Phones advertise from random (private) addresses; connecting to
    /// one as if it were public can fail or time out, so the type heard is the type connected to.
    /// </summary>
    private readonly ConcurrentDictionary<ulong, BluetoothAddressType> _addressTypes = new();
    private BluetoothAdapter? _adapter;
    private Radio? _radio;
    private BluetoothLEAdvertisementPublisher? _publisher;
    private GattServiceProvider? _provider;
    private INearbyResponder? _responder;
    private BluetoothLEAdvertisementWatcher? _watcher;
    private Action<NearbySighting>? _seen;
    private int _generation;
#endif

    private WindowsNearbyRadio()
    {
    }

    public static WindowsNearbyRadio? TryCreate()
    {
#if WINDOWS_WINRT
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763)) return null;
        var radio = new WindowsNearbyRadio();
        _ = radio.InitializeAsync();
        return radio;
#else
        return null;
#endif
    }

    public NearbyRadioState State => _state;

#pragma warning disable CS0067 // raised only where WinRT is available
    public event Action? StateChanged;
#pragma warning restore CS0067

    public Task<bool> RequestPermissionAsync() => Task.FromResult(true);

#if WINDOWS_WINRT
    private async Task InitializeAsync()
    {
        try
        {
            _adapter = await BluetoothAdapter.GetDefaultAsync();
            if (_adapter == null || !_adapter.IsLowEnergySupported)
            {
                Log.Info("Nearby: no Bluetooth LE adapter");
                return;
            }

            Log.Info($"Nearby: Bluetooth adapter (peripheral role {(_adapter.IsPeripheralRoleSupported ? "supported" : "not supported")}, " +
                     $"central role {(_adapter.IsCentralRoleSupported ? "supported" : "not supported")})");
            _radio = await _adapter.GetRadioAsync();
            if (_radio != null) _radio.StateChanged += (_, _) => UpdateState();
            UpdateState();
        }
        catch (Exception ex)
        {
            Log.Warn($"Nearby: Bluetooth unavailable: {ex.Message}");
        }
    }

    private void UpdateState()
    {
        var state = _radio == null ? NearbyRadioState.Unsupported : _radio.State == RadioState.On ? NearbyRadioState.Ready : NearbyRadioState.Off;
        if (state == _state) return;
        _state = state;
        StateChanged?.Invoke();
    }

    // ---- Receiving ----

    public void StartAdvertising(byte[] beacon, INearbyResponder responder)
    {
        int generation;
        lock (_gate)
        {
            StopAdvertisingLocked();
            generation = ++_generation;
            _responder = responder;
            _publisher = new BluetoothLEAdvertisementPublisher();
            _publisher.Advertisement.ManufacturerData.Add(new BluetoothLEManufacturerData(NearbyBeacon.CompanyId, beacon.AsBuffer()));
            _publisher.StatusChanged += (_, args) =>
            {
                if (args.Status == BluetoothLEAdvertisementPublisherStatus.Aborted || args.Error != BluetoothError.Success)
                    Log.Warn($"Nearby: beacon {args.Status} ({args.Error})");
            };
            _publisher.Start();
        }

        if (_adapter?.IsPeripheralRoleSupported == true) _ = StartServiceAsync(generation);
        else Log.Warn("Nearby: this PC's Bluetooth can't accept connections (no peripheral role); senders need the same Wi-Fi");
    }

    private async Task StartServiceAsync(int generation)
    {
        try
        {
            var created = await GattServiceProvider.CreateAsync(NearbyUuids.Service);
            if (created.Error != BluetoothError.Success)
            {
                Log.Warn($"Nearby: Beam's Bluetooth service couldn't start ({created.Error})");
                return;
            }

            var provider = created.ServiceProvider;
            var characteristic = await provider.Service.CreateCharacteristicAsync(NearbyUuids.Handshake, new GattLocalCharacteristicParameters
            {
                CharacteristicProperties = GattCharacteristicProperties.Read | GattCharacteristicProperties.Write,
                ReadProtectionLevel = GattProtectionLevel.Plain,
                WriteProtectionLevel = GattProtectionLevel.Plain,
            });
            if (characteristic.Error != BluetoothError.Success)
            {
                Log.Warn($"Nearby: Beam's Bluetooth service couldn't start ({characteristic.Error})");
                return;
            }

            characteristic.Characteristic.WriteRequested += OnWriteRequested;
            characteristic.Characteristic.ReadRequested += OnReadRequested;
            lock (_gate)
            {
                if (generation != _generation)
                {
                    return; // stopped (or restarted) meanwhile
                }

                _provider = provider;
                provider.AdvertisementStatusChanged += (_, args) =>
                {
                    if (args.Error != BluetoothError.Success) Log.Warn($"Nearby: Bluetooth service {args.Status} ({args.Error})");
                };
                provider.StartAdvertising(new GattServiceProviderAdvertisingParameters { IsConnectable = true, IsDiscoverable = true });
            }

            Log.Info("Nearby: Beam's Bluetooth service is on");
        }
        catch (Exception ex)
        {
            Log.Warn($"Nearby: Beam's Bluetooth service couldn't start: {ex.Message}");
        }
    }

    private async void OnWriteRequested(GattLocalCharacteristic sender, GattWriteRequestedEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            var request = await args.GetRequestAsync();
            if (request == null) return;
            var key = args.Session.DeviceId.Id;
            var data = request.Value.ToArray();
            // A long write arrives in pieces (offsets); the request is complete once it has all its bytes.
            // Write events can run at the same time (async handlers): pieces are put together one at a time.
            var offset = (int)request.Offset;
            byte[]? complete = null;
            lock (_writes)
            {
                var buffer = offset == 0 ? data : Place(_writes.GetValueOrDefault(key) ?? Array.Empty<byte>(), offset, data);
                if (NearbyHandshake.ExpectedLength(buffer) is { } length && buffer.Length >= length)
                {
                    _writes.TryRemove(key, out _);
                    complete = buffer[..length];
                }
                else
                {
                    _writes[key] = buffer;
                }
            }

            if (complete != null) _responder?.Request(key, complete);

            if (request.Option == GattWriteOption.WriteWithResponse) request.Respond();
        }
        catch (Exception ex)
        {
            Log.Warn($"Nearby: Bluetooth write failed: {ex.Message}");
        }
        finally
        {
            deferral.Complete();
        }
    }

    private static byte[] Place(byte[] existing, int offset, byte[] data)
    {
        if (offset > 1024) return existing;
        var result = new byte[Math.Max(existing.Length, offset + data.Length)];
        existing.CopyTo(result, 0);
        data.CopyTo(result, offset);
        return result;
    }

    private async void OnReadRequested(GattLocalCharacteristic sender, GattReadRequestedEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            var request = await args.GetRequestAsync();
            if (request == null) return;
            var value = _responder?.Response(args.Session.DeviceId.Id) ?? NearbyHandshake.Refuse("Not receiving.");
            var offset = (int)request.Offset;
            if (offset > value.Length) request.RespondWithProtocolError(GattProtocolError.InvalidOffset);
            else request.RespondWithValue(value.AsSpan(offset).ToArray().AsBuffer());
        }
        catch (Exception ex)
        {
            Log.Warn($"Nearby: Bluetooth read failed: {ex.Message}");
        }
        finally
        {
            deferral.Complete();
        }
    }

    public void StopAdvertising()
    {
        lock (_gate)
        {
            _generation++;
            StopAdvertisingLocked();
        }
    }

    private void StopAdvertisingLocked()
    {
        try
        {
            _publisher?.Stop();
        }
        catch (Exception ex)
        {
            Log.Warn($"Nearby: stopping the beacon failed: {ex.Message}");
        }

        try
        {
            _provider?.StopAdvertising();
        }
        catch (Exception ex)
        {
            Log.Warn($"Nearby: stopping Beam's Bluetooth service failed: {ex.Message}");
        }

        _publisher = null;
        _provider = null;
        _writes.Clear();
    }

    // ---- Sending ----

    public void StartScanning(Action<NearbySighting> seen)
    {
        lock (_gate)
        {
            _seen = seen;
            if (_watcher != null) return;
            _watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
            _watcher.Received += OnReceived;
            _watcher.Stopped += (_, args) =>
            {
                if (args.Error != BluetoothError.Success) Log.Warn($"Nearby: Bluetooth scanning stopped ({args.Error})");
            };
            _watcher.Start();
        }

        Log.Info("Nearby: looking for receivers over Bluetooth");
    }

    private void OnReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
    {
        try
        {
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041)) _addressTypes[args.BluetoothAddress] = args.BluetoothAddressType;
            if (args.Advertisement.ServiceUuids.Contains(NearbyUuids.Service)) _serviceSeen[args.BluetoothAddress] = Environment.TickCount64;
            foreach (var data in args.Advertisement.ManufacturerData)
            {
                if (data.CompanyId != NearbyBeacon.CompanyId) continue;
                if (NearbyBeacon.TryParse(data.Data.ToArray()) is { } beacon)
                    _seen?.Invoke(new NearbySighting(args.BluetoothAddress.ToString("X12"), beacon, args.RawSignalStrengthInDBm));
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Nearby: reading an advertisement failed: {ex.Message}");
        }
    }

    public void StopScanning()
    {
        lock (_gate)
        {
            if (_watcher == null) return;
            try
            {
                _watcher.Received -= OnReceived;
                _watcher.Stop();
            }
            catch (Exception ex)
            {
                Log.Warn($"Nearby: stopping the scan failed: {ex.Message}");
            }

            _watcher = null;
        }
    }

    public IReadOnlyList<string> RecentServiceAddresses() =>
        _serviceSeen.Where(s => Environment.TickCount64 - s.Value < 30_000).OrderByDescending(s => s.Value).Select(s => s.Key.ToString("X12")).ToList();

    /// <summary>The device at <paramref name="address"/>, as the address type it was heard with (Windows 10 2004+).</summary>
    private Windows.Foundation.IAsyncOperation<BluetoothLEDevice> OpenDeviceAsync(ulong address)
    {
        if (_addressTypes.TryGetValue(address, out var type) && type != BluetoothAddressType.Unspecified
            && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            Log.Info($"Nearby: connecting to {address:X12} ({type} address)");
            return BluetoothLEDevice.FromBluetoothAddressAsync(address, type);
        }

        return BluetoothLEDevice.FromBluetoothAddressAsync(address);
    }

    public async Task<INearbyConnection> ConnectAsync(string address, CancellationToken cancellationToken)
    {
        var value = Convert.ToUInt64(address, 16);
        var device = await OpenDeviceAsync(value).AsTask(cancellationToken) ?? throw new IOException("The device isn't in range.");
        try
        {
            var services = await device.GetGattServicesForUuidAsync(NearbyUuids.Service, BluetoothCacheMode.Uncached).AsTask(cancellationToken);
            if (services.Status != GattCommunicationStatus.Success || services.Services.Count == 0)
                throw new IOException($"No Beam service there ({services.Status})");
            var service = services.Services[0];
            var characteristics = await service.GetCharacteristicsForUuidAsync(NearbyUuids.Handshake, BluetoothCacheMode.Uncached).AsTask(cancellationToken);
            if (characteristics.Status != GattCommunicationStatus.Success || characteristics.Characteristics.Count == 0)
            {
                service.Dispose();
                throw new IOException($"No handshake there ({characteristics.Status})");
            }

            return new Connection(device, service, characteristics.Characteristics[0]);
        }
        catch
        {
            device.Dispose();
            throw;
        }
    }

    /// <summary>Turns the Bluetooth radio on (Windows lets desktop apps do that; it may ask the user once).</summary>
    public async Task<bool> TurnOnAsync()
    {
        if (_radio == null) return false;
        if (_radio.State == RadioState.On) return true;
        var access = await Radio.RequestAccessAsync();
        if (access != RadioAccessStatus.Allowed)
        {
            Log.Info($"Nearby: not allowed to turn Bluetooth on ({access})");
            return false;
        }

        var result = await _radio.SetStateAsync(RadioState.On);
        Log.Info($"Nearby: turning Bluetooth on: {result}");
        return result == RadioAccessStatus.Allowed;
    }

    private sealed class Connection : INearbyConnection
    {
        private readonly BluetoothLEDevice _device;
        private readonly GattDeviceService _service;
        private readonly GattCharacteristic _characteristic;

        public Connection(BluetoothLEDevice device, GattDeviceService service, GattCharacteristic characteristic)
        {
            _device = device;
            _service = service;
            _characteristic = characteristic;
        }

        public async Task WriteAsync(byte[] data, CancellationToken cancellationToken)
        {
            var result = await _characteristic.WriteValueWithResultAsync(data.AsBuffer(), GattWriteOption.WriteWithResponse).AsTask(cancellationToken);
            if (result.Status != GattCommunicationStatus.Success) throw new IOException($"Bluetooth write failed ({result.Status})");
        }

        public async Task<byte[]> ReadAsync(CancellationToken cancellationToken)
        {
            var result = await _characteristic.ReadValueAsync(BluetoothCacheMode.Uncached).AsTask(cancellationToken);
            if (result.Status != GattCommunicationStatus.Success) throw new IOException($"Bluetooth read failed ({result.Status})");
            return result.Value.ToArray();
        }

        public ValueTask DisposeAsync()
        {
            _service.Dispose();
            _device.Dispose();
            return ValueTask.CompletedTask;
        }
    }
#else
    public void StartAdvertising(byte[] beacon, INearbyResponder responder)
    {
    }

    public void StopAdvertising()
    {
    }

    public void StartScanning(Action<NearbySighting> seen)
    {
    }

    public void StopScanning()
    {
    }

    public Task<bool> TurnOnAsync() => Task.FromResult(false);

    public IReadOnlyList<string> RecentServiceAddresses() => Array.Empty<string>();

    public Task<INearbyConnection> ConnectAsync(string address, CancellationToken cancellationToken) =>
        Task.FromException<INearbyConnection>(new PlatformNotSupportedException());
#endif
}
