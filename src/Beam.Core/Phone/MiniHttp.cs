using System.Net.Sockets;
using System.Text;

namespace Beam.Core.Phone;

/// <summary>A parsed HTTP/1.1 request head. The body is read from <see cref="Body"/>.</summary>
internal sealed class HttpRequest
{
    public required string Method { get; init; }

    /// <summary>Path without the query string, still percent-encoded.</summary>
    public required string Path { get; init; }

    public required Dictionary<string, string> Headers { get; init; }

    public required Stream Body { get; init; }

    public long ContentLength => Headers.TryGetValue("content-length", out var value) && long.TryParse(value, out var length) && length >= 0 ? length : 0;

    public string UserAgent => Headers.GetValueOrDefault("user-agent") ?? "";
}

/// <summary>
/// Just enough HTTP/1.1 for a phone browser talking to Beam: one request per connection,
/// Content-Length bodies, no chunked uploads. (HttpListener needs administrator rights to listen
/// on the network on Windows, and ASP.NET Core would add tens of megabytes to the app.)
/// </summary>
internal static class MiniHttp
{
    private const int MaxHeaderBytes = 16 * 1024;
    private static readonly byte[] HeaderEnd = "\r\n\r\n"u8.ToArray();

    public static async Task<HttpRequest?> ReadRequestAsync(NetworkStream stream, CancellationToken token)
    {
        var buffer = new byte[MaxHeaderBytes];
        var filled = 0;
        int end;
        while ((end = buffer.AsSpan(0, filled).IndexOf(HeaderEnd)) < 0)
        {
            if (filled == buffer.Length) return null; // headers too large
            var read = await stream.ReadAsync(buffer.AsMemory(filled), token).ConfigureAwait(false);
            if (read == 0) return null;
            filled += read;
        }

        var head = Encoding.ASCII.GetString(buffer, 0, end);
        var lines = head.Split("\r\n");
        var parts = lines[0].Split(' ');
        if (parts.Length != 3 || !parts[2].StartsWith("HTTP/1.", StringComparison.Ordinal)) return null;

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            headers[line[..colon].Trim().ToLowerInvariant()] = line[(colon + 1)..].Trim();
        }

        if (headers.TryGetValue("transfer-encoding", out var encoding) && !encoding.Equals("identity", StringComparison.OrdinalIgnoreCase))
            return null; // chunked bodies aren't supported (browsers send Content-Length for files)

        var target = parts[1];
        var query = target.IndexOf('?');
        var path = query >= 0 ? target[..query] : target;
        var bodyStart = end + HeaderEnd.Length;
        var length = headers.TryGetValue("content-length", out var value) && long.TryParse(value, out var parsed) && parsed >= 0 ? parsed : 0;
        return new HttpRequest
        {
            Method = parts[0].ToUpperInvariant(),
            Path = path,
            Headers = headers,
            Body = new BodyStream(buffer.AsMemory(bodyStart, filled - bodyStart), stream, length),
        };
    }

    public static Task WriteJsonAsync(Stream stream, int status, string json, CancellationToken token) =>
        WriteAsync(stream, status, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(json), token);

    public static Task WriteTextAsync(Stream stream, int status, string text, CancellationToken token) =>
        WriteAsync(stream, status, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(text), token);

    public static async Task WriteAsync(Stream stream, int status, string contentType, byte[] body, CancellationToken token, string? extraHeaders = null)
    {
        await WriteHeadAsync(stream, status, contentType, body.Length, token, extraHeaders).ConfigureAwait(false);
        await stream.WriteAsync(body, token).ConfigureAwait(false);
    }

    public static async Task WriteHeadAsync(Stream stream, int status, string contentType, long length, CancellationToken token, string? extraHeaders = null)
    {
        var head = new StringBuilder()
            .Append("HTTP/1.1 ").Append(status).Append(' ').Append(Reason(status)).Append("\r\n")
            .Append("Content-Type: ").Append(contentType).Append("\r\n")
            .Append("Content-Length: ").Append(length).Append("\r\n")
            .Append("Connection: close\r\n")
            .Append("Cache-Control: no-store\r\n")
            .Append("X-Content-Type-Options: nosniff\r\n")
            .Append("Referrer-Policy: no-referrer\r\n")
            .Append(extraHeaders ?? "")
            .Append("\r\n");
        await stream.WriteAsync(Encoding.UTF8.GetBytes(head.ToString()), token).ConfigureAwait(false);
    }

    private static string Reason(int status) => status switch
    {
        200 => "OK",
        400 => "Bad Request",
        403 => "Forbidden",
        404 => "Not Found",
        405 => "Method Not Allowed",
        409 => "Conflict",
        413 => "Payload Too Large",
        429 => "Too Many Requests",
        500 => "Internal Server Error",
        503 => "Service Unavailable",
        _ => "Status",
    };

    /// <summary>The request body: bytes already read with the headers, then the rest from the socket, up to Content-Length.</summary>
    private sealed class BodyStream : Stream
    {
        private readonly NetworkStream _inner;
        private ReadOnlyMemory<byte> _prefix;
        private long _remaining;

        public BodyStream(ReadOnlyMemory<byte> prefix, NetworkStream inner, long length)
        {
            _prefix = prefix.Length > length ? prefix[..(int)length] : prefix;
            _inner = inner;
            _remaining = length;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_remaining <= 0 || buffer.Length == 0) return 0;
            int read;
            if (!_prefix.IsEmpty)
            {
                read = Math.Min(buffer.Length, _prefix.Length);
                _prefix[..read].CopyTo(buffer);
                _prefix = _prefix[read..];
            }
            else
            {
                var want = (int)Math.Min(buffer.Length, _remaining);
                read = await _inner.ReadAsync(buffer[..want], cancellationToken).ConfigureAwait(false);
                if (read == 0) throw new EndOfStreamException("The phone stopped sending.");
            }

            _remaining -= read;
            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count).GetAwaiter().GetResult();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
