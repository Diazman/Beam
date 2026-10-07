using System.Buffers;
using System.Threading.Channels;

namespace Beam.Core.Transfer;

/// <summary>
/// Write-behind for received data: the network reader hands chunks to a bounded queue and keeps
/// reading while a background task writes them to disk. Memory use is capped at a few chunks.
/// </summary>
internal sealed class PipelinedFileWriter : IAsyncDisposable
{
    private const int QueueDepth = 8;

    private readonly FileStream _stream;
    private readonly string _description;
    private readonly Channel<(byte[] Buffer, int Length)> _queue;
    private readonly Task _writer;
    private volatile Exception? _error;

    public PipelinedFileWriter(FileStream stream, string description)
    {
        _stream = stream;
        _description = description;
        _queue = Channel.CreateBounded<(byte[], int)>(new BoundedChannelOptions(QueueDepth)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
        _writer = Task.Run(WriteLoopAsync);
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        ThrowIfFailed();
        var buffer = ArrayPool<byte>.Shared.Rent(data.Length);
        data.CopyTo(buffer);
        try
        {
            await _queue.Writer.WriteAsync((buffer, data.Length), cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(buffer);
            throw;
        }
    }

    /// <summary>Waits for all queued data to reach the file and flushes it.</summary>
    public async Task CompleteAsync()
    {
        _queue.Writer.TryComplete();
        await _writer.ConfigureAwait(false);
        ThrowIfFailed();
        try
        {
            await _stream.FlushAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new LocalFileException(_description, ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        try { await _writer.ConfigureAwait(false); } catch { /* error already captured */ }
        try { await _stream.DisposeAsync().ConfigureAwait(false); } catch { /* best effort */ }
    }

    private void ThrowIfFailed()
    {
        if (_error != null) throw new LocalFileException(_description, _error);
    }

    private async Task WriteLoopAsync()
    {
        await foreach (var (buffer, length) in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                // After a failure keep draining so the producer never blocks on a full queue.
                if (_error == null) await _stream.WriteAsync(buffer.AsMemory(0, length)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _error = ex;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }
}
