using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Beam.Core.Identity;
using Beam.Core.Protocol;
using Beam.Core.Localization;

namespace Beam.Core.Transfer;

/// <summary>An authenticated, encrypted connection to another device.</summary>
internal sealed class PeerConnection : IAsyncDisposable
{
    private readonly Socket _socket;
    private readonly SslStream _ssl;
    private int _disposed;

    public PeerConnection(Socket socket, SslStream ssl, FrameChannel channel, string remoteFingerprint, HelloMessage remoteHello)
    {
        _socket = socket;
        _ssl = ssl;
        Channel = channel;
        RemoteFingerprint = remoteFingerprint;
        RemoteHello = remoteHello;
        RemoteEndPoint = socket.RemoteEndPoint as IPEndPoint ?? new IPEndPoint(IPAddress.None, 0);
        LocalEndPoint = socket.LocalEndPoint as IPEndPoint ?? new IPEndPoint(IPAddress.None, 0);
    }

    public FrameChannel Channel { get; }

    public string RemoteFingerprint { get; }

    public HelloMessage RemoteHello { get; }

    public IPEndPoint RemoteEndPoint { get; }

    /// <summary>This device's address the connection uses (tells which network or link it came in on).</summary>
    public IPEndPoint LocalEndPoint { get; }

    /// <summary>Tears the connection down immediately; pending reads and writes fail.</summary>
    public void Abort()
    {
        try { _socket.Dispose(); } catch { /* already closed */ }
    }

    /// <summary>Best-effort: tell the other side we are cancelling, then close.</summary>
    public async Task SendCancelAndCloseAsync(string reason, string? detail = null)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await Channel.SendAsync(FrameType.Cancel, new CancelMessage { Reason = reason, Detail = detail }, cts.Token).ConfigureAwait(false);
        }
        catch
        {
            // The connection may already be gone; closing is what matters.
        }

        // Close gracefully: if a socket is closed while the peer's data is still unread, Windows
        // resets the connection and the peer loses the Cancel frame we just sent. So signal end of
        // stream and discard incoming data until the peer closes too (it stops once it reads Cancel).
        try
        {
            _socket.Shutdown(SocketShutdown.Send);
            var buffer = new byte[64 * 1024];
            using var drain = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            while (await _socket.ReceiveAsync(buffer, SocketFlags.None, drain.Token).ConfigureAwait(false) > 0)
            {
            }
        }
        catch
        {
            // Peer closed, reset, or took too long: either way we're done.
        }

        await DisposeAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await _ssl.ShutdownAsync().WaitAsync(cts.Token).ConfigureAwait(false);
        }
        catch
        {
            // ignore: we are closing anyway
        }

        try { await _ssl.DisposeAsync().ConfigureAwait(false); } catch { /* ignore */ }
        Abort();
    }
}

internal static class SecureTransport
{
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(15);

    public static void ConfigureSocket(Socket socket)
    {
        socket.NoDelay = true;
        try
        {
            // Detect a vanished peer (sleep, Wi-Fi drop) within ~30 seconds even when idle.
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 15);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 5);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 3);
        }
        catch
        {
            // Older systems: rely on application-level timeouts.
        }
    }

    public static async Task<(SslStream Stream, string Fingerprint)> AuthenticateAsServerAsync(
        Socket socket, X509Certificate2 certificate, CancellationToken cancellationToken)
    {
        var ssl = new SslStream(new NetworkStream(socket, ownsSocket: true), leaveInnerStreamOpen: false);
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(HandshakeTimeout);
            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = certificate,
                ClientCertificateRequired = true,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                // Devices use self-signed certificates; identity is checked by fingerprint instead of a CA.
                RemoteCertificateValidationCallback = (_, cert, _, _) => cert != null,
            }, cts.Token).ConfigureAwait(false);

            var remote = ssl.RemoteCertificate ?? throw new AuthenticationException("The other device did not present a certificate.");
            return (ssl, DeviceIdentity.ComputeFingerprint(remote));
        }
        catch
        {
            await ssl.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Connects to the first reachable endpoint, performs mutual TLS, verifies the remote
    /// fingerprint (when known) and exchanges Hello messages.
    /// </summary>
    public static async Task<PeerConnection> ConnectAsync(
        DeviceIdentity identity,
        HelloMessage hello,
        IReadOnlyList<IPEndPoint> endpoints,
        string? expectedFingerprint,
        string peerName,
        CancellationToken cancellationToken)
    {
        if (endpoints.Count == 0)
            throw new TransferException(TransferErrorKind.ConnectFailed,
                L.T("{0} isn't available right now. Make sure Beam is open on that computer.", peerName));

        Exception? lastError = null;
        foreach (var endpoint in endpoints)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var socket = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                using (var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    cts.CancelAfter(ConnectTimeout);
                    try
                    {
                        await socket.ConnectAsync(endpoint, cts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        throw new SocketException((int)SocketError.TimedOut);
                    }
                }

                ConfigureSocket(socket);
            }
            catch (Exception ex) when (ex is SocketException or IOException)
            {
                lastError = ex;
                socket.Dispose();
                continue;
            }

            return await HandshakeAsync(socket, identity, hello, expectedFingerprint, peerName, cancellationToken).ConfigureAwait(false);
        }

        var details = lastError?.Message;
        throw new TransferException(TransferErrorKind.ConnectFailed,
            L.T("Couldn't reach {0}. Make sure Beam is open on that computer and both computers are on the same network.", peerName),
            details, lastError);
    }

    private static async Task<PeerConnection> HandshakeAsync(
        Socket socket, DeviceIdentity identity, HelloMessage hello, string? expectedFingerprint, string peerName, CancellationToken cancellationToken)
    {
        var ssl = new SslStream(new NetworkStream(socket, ownsSocket: true), leaveInnerStreamOpen: false);
        try
        {
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                cts.CancelAfter(HandshakeTimeout);
                try
                {
                    await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                    {
                        TargetHost = "beam.local",
                        ClientCertificates = new X509CertificateCollection { identity.Certificate },
                        LocalCertificateSelectionCallback = (_, _, _, _, _) => identity.Certificate,
                        CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                        RemoteCertificateValidationCallback = (_, cert, _, _) => cert != null,
                    }, cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new TransferException(TransferErrorKind.ConnectFailed, L.T("{0} didn't respond.", peerName), "TLS handshake timed out");
                }
            }

            var remoteCert = ssl.RemoteCertificate ?? throw new AuthenticationException("No server certificate.");
            var fingerprint = DeviceIdentity.ComputeFingerprint(remoteCert);
            if (!string.IsNullOrEmpty(expectedFingerprint) && !string.Equals(fingerprint, expectedFingerprint, StringComparison.OrdinalIgnoreCase))
            {
                throw new TransferException(TransferErrorKind.IdentityMismatch,
                    L.T("Beam couldn't confirm this is really {0}, so nothing was sent. If Beam was reinstalled there, wait a few seconds and try again.", peerName),
                    $"Expected fingerprint {expectedFingerprint}, got {fingerprint}");
            }

            var channel = new FrameChannel(ssl);
            await channel.SendAsync(FrameType.Hello, hello, cancellationToken).ConfigureAwait(false);
            var reply = await channel.ReadMessageAsync<HelloMessage>(FrameType.HelloReply, HandshakeTimeout, cancellationToken).ConfigureAwait(false);
            EnsureCompatible(reply, peerName);
            return new PeerConnection(socket, ssl, channel, fingerprint, reply);
        }
        catch
        {
            await ssl.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public static bool IsCompatible(HelloMessage remote) =>
        remote.Protocol >= AppInfo.MinimumProtocolVersion && remote.MinProtocol <= AppInfo.ProtocolVersion;

    public static void EnsureCompatible(HelloMessage remote, string peerName)
    {
        if (!IsCompatible(remote))
        {
            throw new TransferException(TransferErrorKind.IncompatibleVersion,
                L.T("{0} is running a different version of Beam. Update Beam on both computers to the latest version.", peerName),
                $"Remote protocol {remote.Protocol} (min {remote.MinProtocol}), local {AppInfo.ProtocolVersion} (min {AppInfo.MinimumProtocolVersion})");
        }
    }
}
