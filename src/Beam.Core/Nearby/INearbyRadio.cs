namespace Beam.Core.Nearby;

/// <summary>Whether Bluetooth can be used to find nearby devices.</summary>
public enum NearbyRadioState
{
    /// <summary>No Bluetooth LE (or it can't advertise/scan on this device).</summary>
    Unsupported,

    /// <summary>Bluetooth is turned off.</summary>
    Off,

    /// <summary>The app may not use Bluetooth yet (Android: Nearby devices permission).</summary>
    NeedsPermission,

    Ready,
}

/// <summary>A Beam beacon heard over Bluetooth.</summary>
/// <param name="Address">The radio's address (or the platform's handle for it), to connect to.</param>
public sealed record NearbySighting(string Address, NearbyBeacon Beacon, int Rssi);

/// <summary>
/// Answers the handshake on the receiving device. The platform's GATT server passes each sender's write to
/// <see cref="Request"/> and returns <see cref="Response"/> to its reads (keyed by the sender's radio address).
/// </summary>
public interface INearbyResponder
{
    void Request(string sender, byte[] data);

    byte[] Response(string sender);

    /// <summary>The sender disconnected: forget its handshake.</summary>
    void Forget(string sender);
}

/// <summary>A GATT connection to a receiver's Beam service.</summary>
public interface INearbyConnection : IAsyncDisposable
{
    Task WriteAsync(byte[] data, CancellationToken cancellationToken);

    Task<byte[]> ReadAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The platform's Bluetooth LE: a beacon with a GATT service while Receive is open, a scanner while looking for
/// receivers, and GATT client connections for the handshake. Beam.Core runs the protocol; the platform only moves bytes.
/// Methods are safe to call from any thread; events arrive on any thread.
/// </summary>
public interface INearbyRadio
{
    NearbyRadioState State { get; }

    /// <summary><see cref="State"/> may have changed (Bluetooth turned on or off, permission granted).</summary>
    event Action? StateChanged;

    /// <summary>Phones: asks for the Bluetooth permission (no-op elsewhere). True when granted.</summary>
    Task<bool> RequestPermissionAsync();

    /// <summary>Advertises <paramref name="beacon"/> (manufacturer data) with Beam's GATT service; replaces a running one.</summary>
    void StartAdvertising(byte[] beacon, INearbyResponder responder);

    void StopAdvertising();

    void StartScanning(Action<NearbySighting> seen);

    void StopScanning();

    Task<INearbyConnection> ConnectAsync(string address, CancellationToken cancellationToken);
}

/// <summary>Beam's Bluetooth service and the one characteristic the handshake goes through (write, then read).</summary>
public static class NearbyUuids
{
    public static readonly Guid Service = Guid.Parse("8b2f6a10-4c1e-4d7b-9e55-3b0e5a1c7d01");
    public static readonly Guid Handshake = Guid.Parse("8b2f6a11-4c1e-4d7b-9e55-3b0e5a1c7d01");
}
