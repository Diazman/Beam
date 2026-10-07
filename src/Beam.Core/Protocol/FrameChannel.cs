using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Beam.Core.Protocol;

public readonly record struct Frame(FrameType Type, ReadOnlyMemory<byte> Payload);

public sealed class ProtocolException : Exception
{
    public ProtocolException(string message) : base(message)
    {
    }
}

/// <summary>
/// Reads and writes length-prefixed frames on a stream. One reader and any number of
/// writers may use it concurrently (writes are serialised). The payload returned by
/// <see cref="ReadAsync"/> is only valid until the next read.
/// </summary>
public sealed class FrameChannel
{
    public const int HeaderSize = 5;
    public const int DataChunkSize = 256 * 1024;
    public const int MaxDataPayload = 1024 * 1024;
    public const int MaxControlPayload = 32 * 1024 * 1024; // ~250,000 files in one offer

    private readonly Stream _stream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly byte[] _header = new byte[HeaderSize];
    private byte[] _readBuffer = new byte[64 * 1024];

    public FrameChannel(Stream stream)
    {
        _stream = stream;
    }

    public Task SendAsync<T>(FrameType type, T message, CancellationToken cancellationToken)
    {
        var typeInfo = (JsonTypeInfo<T>)ProtocolJson.Default.GetTypeInfo(typeof(T))!;
        var json = JsonSerializer.SerializeToUtf8Bytes(message, typeInfo);
        var buffer = new byte[HeaderSize + json.Length];
        WriteHeader(buffer, type, json.Length);
        json.CopyTo(buffer, HeaderSize);
        return WriteAsync(buffer, cancellationToken);
    }

    /// <summary>
    /// Sends file bytes. <paramref name="buffer"/> must have <see cref="HeaderSize"/> free bytes at the
    /// start, followed by <paramref name="payloadLength"/> bytes of data; this avoids an extra copy.
    /// </summary>
    public Task SendDataAsync(byte[] buffer, int payloadLength, CancellationToken cancellationToken)
    {
        if (payloadLength > MaxDataPayload) throw new ArgumentOutOfRangeException(nameof(payloadLength));
        WriteHeader(buffer, FrameType.FileData, payloadLength);
        return WriteAsync(buffer.AsMemory(0, HeaderSize + payloadLength), cancellationToken);
    }

    public async Task<Frame> ReadAsync(CancellationToken cancellationToken)
    {
        await _stream.ReadExactlyAsync(_header, cancellationToken).ConfigureAwait(false);
        var type = (FrameType)_header[0];
        var length = BinaryPrimitives.ReadInt32BigEndian(_header.AsSpan(1));
        if (!Enum.IsDefined(type)) throw new ProtocolException($"Unknown frame type {(byte)type}.");
        var max = type == FrameType.FileData ? MaxDataPayload : MaxControlPayload;
        if (length < 0 || length > max) throw new ProtocolException($"Frame of {length} bytes exceeds the limit for {type}.");
        if (_readBuffer.Length < length)
            _readBuffer = new byte[Math.Max(length, Math.Min(_readBuffer.Length * 2, max))];
        await _stream.ReadExactlyAsync(_readBuffer.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
        return new Frame(type, _readBuffer.AsMemory(0, length));
    }

    public async Task<Frame> ReadAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (timeout == Timeout.InfiniteTimeSpan) return await ReadAsync(cancellationToken).ConfigureAwait(false);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        try
        {
            return await ReadAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The other device stopped responding.");
        }
    }

    /// <summary>Reads a frame and requires it to be <paramref name="expected"/> (a Cancel frame is surfaced as <see cref="RemoteCancelException"/>).</summary>
    public async Task<T> ReadMessageAsync<T>(FrameType expected, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var frame = await ReadAsync(timeout, cancellationToken).ConfigureAwait(false);
        if (frame.Type == FrameType.Cancel) throw new RemoteCancelException(Parse<CancelMessage>(frame));
        if (frame.Type != expected) throw new ProtocolException($"Expected {expected} but received {frame.Type}.");
        return Parse<T>(frame);
    }

    public static T Parse<T>(Frame frame)
    {
        var typeInfo = (JsonTypeInfo<T>)ProtocolJson.Default.GetTypeInfo(typeof(T))!;
        try
        {
            return JsonSerializer.Deserialize(frame.Payload.Span, typeInfo)
                   ?? throw new ProtocolException($"Empty {frame.Type} message.");
        }
        catch (JsonException ex)
        {
            throw new ProtocolException($"Malformed {frame.Type} message: {ex.Message}");
        }
    }

    private static void WriteHeader(byte[] buffer, FrameType type, int length)
    {
        buffer[0] = (byte)type;
        BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(1), length);
    }

    private async Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }
}

/// <summary>The other side sent a Cancel frame.</summary>
public sealed class RemoteCancelException : Exception
{
    public RemoteCancelException(CancelMessage message) : base($"Remote cancelled: {message.Reason} {message.Detail}")
    {
        Reason = message.Reason;
        Detail = message.Detail;
    }

    public string Reason { get; }

    public string? Detail { get; }
}
