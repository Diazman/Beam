using System.Security.Cryptography;
using Beam.Core.Protocol;
using Beam.Core.Transfer;

namespace Beam.Core.Browse;

/// <summary>
/// Moving a file's bytes over a browsing connection as fast as ordinary transfers: the next piece is read (and
/// hashed) from disk while the current one is encrypted and sent, and what arrives is written to disk behind the
/// network reader instead of in turn with it.
/// </summary>
internal static class BrowseStreaming
{
    private const int Chunk = FrameChannel.DataChunkSize;

    /// <summary>
    /// Hashes <paramref name="stream"/> from its start and sends it from <paramref name="offset"/> to <paramref name="size"/>
    /// as <see cref="FrameType.BrowseData"/> frames. Returns how far it got (less than size when the file got shorter).
    /// </summary>
    public static async Task<long> SendAsync(FrameChannel channel, Stream stream, long offset, long size, IncrementalHash hash,
        Action<int>? sent, TimeSpan? pause, CancellationToken token)
    {
        var current = new byte[FrameChannel.HeaderSize + Chunk];
        var next = new byte[FrameChannel.HeaderSize + Chunk];
        long position = 0;

        // The part the other side already has: only hashed.
        while (position < offset)
        {
            var read = await ReadAsync(stream, hash, current, Math.Min(Chunk, offset - position), token).ConfigureAwait(false);
            if (read == 0) return position;
            position += read;
        }

        var length = await ReadAsync(stream, hash, current, Math.Min(Chunk, size - position), token).ConfigureAwait(false);
        while (length > 0)
        {
            var remaining = size - position - length;
            var buffer = next;
            var reading = remaining > 0 ? Task.Run(() => ReadAsync(stream, hash, buffer, Math.Min(Chunk, remaining), token), token) : null;
            try
            {
                await channel.SendDataAsync(FrameType.BrowseData, current, length, token).ConfigureAwait(false);
            }
            catch
            {
                if (reading != null) await Observe(reading).ConfigureAwait(false); // no read left running on the stream
                throw;
            }

            position += length;
            sent?.Invoke(length);
            if (pause is { } p && p > TimeSpan.Zero) await Task.Delay(p, token).ConfigureAwait(false);
            length = reading == null ? 0 : await reading.ConfigureAwait(false);
            (current, next) = (next, current);
        }

        return position;
    }

    /// <summary>
    /// Receives <see cref="FrameType.BrowseData"/> frames until <paramref name="size"/> bytes are there (from
    /// <paramref name="received"/>), writing them behind the reader and hashing them.
    /// </summary>
    public static async Task ReceiveAsync(FrameChannel channel, PipelinedFileWriter writer, long received, long size, IncrementalHash hash,
        Action<int>? arrived, TimeSpan timeout, CancellationToken token)
    {
        while (received < size)
        {
            var frame = await channel.ReadAsync(timeout, token).ConfigureAwait(false);
            if (frame.Type != FrameType.BrowseData || received + frame.Payload.Length > size)
                throw new ProtocolException("Unexpected data while receiving a file.");
            hash.AppendData(frame.Payload.Span);
            await writer.WriteAsync(frame.Payload, token).ConfigureAwait(false); // copies: the frame buffer is reused
            received += frame.Payload.Length;
            arrived?.Invoke(frame.Payload.Length);
        }
    }

    /// <summary>Hashes the first <paramref name="length"/> bytes of a file (a resumed download's part already there).</summary>
    public static async Task HashPrefixAsync(string path, long length, IncrementalHash hash, CancellationToken token)
    {
        if (length <= 0) return;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan | FileOptions.Asynchronous);
        var buffer = new byte[Chunk];
        long done = 0;
        while (done < length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, length - done)), token).ConfigureAwait(false);
            if (read == 0) throw new IOException("The part file got shorter.");
            hash.AppendData(buffer, 0, read);
            done += read;
        }
    }

    private static async Task<int> ReadAsync(Stream stream, IncrementalHash hash, byte[] buffer, long want, CancellationToken token)
    {
        var total = 0;
        while (total < want)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(FrameChannel.HeaderSize + total, (int)(want - total)), token).ConfigureAwait(false);
            if (read == 0) break;
            total += read;
        }

        hash.AppendData(buffer, FrameChannel.HeaderSize, total);
        return total;
    }

    private static async Task Observe(Task task)
    {
        try { await task.ConfigureAwait(false); } catch { /* the send failed already */ }
    }
}
