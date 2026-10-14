using System.Collections.Concurrent;
using Beam.Core.Nearby;

namespace Beam.Core.Tests;

/// <summary>Bluetooth LE between test nodes: beacons heard by scanners, and GATT writes/reads handed to the responder.</summary>
public sealed class FakeNearbyAir
{
    private readonly ConcurrentDictionary<string, (byte[] Beacon, INearbyResponder Responder)> _advertisers = new();
    private int _next;

    public int Connections;

    public int ScanStarts, ScanStops;

    /// <summary>The longest value written or read (a GATT attribute holds at most 512 bytes; longer ones fail on real radios).</summary>
    public int Longest;

    /// <summary>Scanners hear nothing from now on (out of range, or the radio stopped advertising).</summary>
    public volatile bool Silent;

    private void Measure(int length)
    {
        int seen;
        while (length > (seen = Volatile.Read(ref Longest)) && Interlocked.CompareExchange(ref Longest, length, seen) != seen) { }
        if (length > NearbyHandshake.MaxValueLength) throw new IOException($"A {length}-byte value doesn't fit a GATT attribute.");
    }

    public INearbyRadio Radio() => new FakeRadio(this, "radio-" + Interlocked.Increment(ref _next));

    /// <summary>What a receiver answers is replaced (to test tampering or impostors).</summary>
    public Func<byte[], byte[]>? Tamper { get; set; }

    private sealed class FakeRadio : INearbyRadio
    {
        private readonly FakeNearbyAir _air;
        private readonly string _address;
        private CancellationTokenSource? _scan;

        public FakeRadio(FakeNearbyAir air, string address)
        {
            _air = air;
            _address = address;
        }

        public NearbyRadioState State => NearbyRadioState.Ready;

        public event Action? StateChanged { add { } remove { } }

        public Task<bool> RequestPermissionAsync() => Task.FromResult(true);

        public Task<bool> TurnOnAsync() => Task.FromResult(true);

        public IReadOnlyList<string> RecentServiceAddresses() => _air._advertisers.Keys.Where(a => a != _address).ToList();

        public void StartAdvertising(byte[] beacon, INearbyResponder responder) => _air._advertisers[_address] = (beacon, responder);

        public void StopAdvertising() => _air._advertisers.TryRemove(_address, out _);

        public void StartScanning(Action<NearbySighting> seen)
        {
            _scan?.Cancel();
            Interlocked.Increment(ref _air.ScanStarts);
            var cts = _scan = new CancellationTokenSource();
            _ = Task.Run(async () =>
            {
                while (!cts.IsCancellationRequested)
                {
                    if (!_air.Silent)
                        foreach (var (address, ad) in _air._advertisers)
                            if (address != _address && NearbyBeacon.TryParse(ad.Beacon) is { } beacon) seen(new NearbySighting(address, beacon, -50));
                    try { await Task.Delay(100, cts.Token); } catch (OperationCanceledException) { }
                }
            });
        }

        public void StopScanning()
        {
            if (_scan is { IsCancellationRequested: false }) Interlocked.Increment(ref _air.ScanStops);
            _scan?.Cancel();
        }

        public Task<INearbyConnection> ConnectAsync(string address, CancellationToken cancellationToken)
        {
            if (!_air._advertisers.TryGetValue(address, out var ad)) throw new IOException("Not in range.");
            Interlocked.Increment(ref _air.Connections);
            return Task.FromResult<INearbyConnection>(new Connection(_air, ad.Responder, _address));
        }
    }

    private sealed class Connection : INearbyConnection
    {
        private readonly FakeNearbyAir _air;
        private readonly INearbyResponder _responder;
        private readonly string _sender;

        public Connection(FakeNearbyAir air, INearbyResponder responder, string sender)
        {
            _air = air;
            _responder = responder;
            _sender = sender;
        }

        public Task WriteAsync(byte[] data, CancellationToken cancellationToken)
        {
            _air.Measure(data.Length);
            _responder.Request(_sender, data);
            return Task.CompletedTask;
        }

        public Task<byte[]> ReadAsync(CancellationToken cancellationToken)
        {
            var answer = _responder.Response(_sender);
            _air.Measure(answer.Length);
            return Task.FromResult(_air.Tamper is { } tamper && answer.Length > 2 && answer[1] == 1 ? tamper(answer) : answer);
        }

        public ValueTask DisposeAsync()
        {
            _responder.Forget(_sender);
            return ValueTask.CompletedTask;
        }
    }
}
