using System.Net.Sockets;
using System.Security.Authentication;
using Beam.Core.Protocol;

namespace Beam.Core.Transfer;

public enum TransferErrorKind
{
    Unknown,
    ConnectFailed,
    ConnectionLost,
    Declined,
    TimedOut,
    Busy,
    NotEnoughSpace,
    DestinationUnavailable,
    PermissionDenied,
    CancelledByRemote,
    CancelledByUser,
    VerificationFailed,
    IncompatibleVersion,
    IdentityMismatch,
    SecureConnectionFailed,
    NothingToSend,
    Protocol,
}

/// <summary>An error with a message suitable for normal users plus technical details for troubleshooting.</summary>
public sealed record TransferError(TransferErrorKind Kind, string Message, string? Details = null)
{
    /// <summary>Errors after which trying again (resume) makes sense.</summary>
    public bool IsRetryable => Kind is TransferErrorKind.ConnectFailed or TransferErrorKind.ConnectionLost
        or TransferErrorKind.NotEnoughSpace or TransferErrorKind.DestinationUnavailable
        or TransferErrorKind.PermissionDenied or TransferErrorKind.TimedOut or TransferErrorKind.Busy
        or TransferErrorKind.Unknown or TransferErrorKind.SecureConnectionFailed;
}

/// <summary>Carries a <see cref="TransferError"/> through the call stack.</summary>
public sealed class TransferException : Exception
{
    public TransferException(TransferError error, Exception? inner = null) : base(error.Details ?? error.Message, inner)
    {
        Error = error;
    }

    public TransferException(TransferErrorKind kind, string message, string? details = null, Exception? inner = null)
        : this(new TransferError(kind, message, details ?? inner?.Message), inner)
    {
    }

    public TransferError Error { get; }
}

/// <summary>A failure reading or writing a local file (as opposed to the network).</summary>
internal sealed class LocalFileException : Exception
{
    public LocalFileException(string message, Exception inner) : base(message, inner)
    {
    }
}

public static class ErrorTranslator
{
    /// <summary>True for failures where reconnecting could help.</summary>
    public static bool IsTransientNetworkFailure(Exception ex) => ex switch
    {
        TransferException te => te.Error.Kind is TransferErrorKind.ConnectionLost or TransferErrorKind.ConnectFailed,
        RemoteCancelException => false,
        ProtocolException => false,
        LocalFileException => false,
        OperationCanceledException => false,
        SocketException => true,
        EndOfStreamException => true,
        TimeoutException => true,
        IOException => true,
        ObjectDisposedException => true,
        _ => false,
    };

    public static TransferError FromException(Exception ex, string peerName)
    {
        switch (ex)
        {
            case TransferException te:
                return te.Error;
            case RemoteCancelException rc:
                return FromRemoteReason(rc.Reason, peerName, rc.Detail);
            case LocalFileException lf:
                return FromLocalFileException(lf.InnerException ?? lf, lf.Message);
            case OperationCanceledException:
                return new TransferError(TransferErrorKind.CancelledByUser, "The transfer was cancelled.");
            case AuthenticationException:
                return new TransferError(TransferErrorKind.SecureConnectionFailed,
                    $"A secure connection with {peerName} couldn't be established.", ex.Message);
            case ProtocolException:
                return new TransferError(TransferErrorKind.Protocol,
                    $"{peerName} sent something Beam didn't understand. Make sure both computers run the latest version.", ex.Message);
            case SocketException se when se.SocketErrorCode is SocketError.ConnectionRefused or SocketError.HostUnreachable
                or SocketError.NetworkUnreachable or SocketError.TimedOut or SocketError.HostNotFound or SocketError.HostDown:
                return new TransferError(TransferErrorKind.ConnectFailed,
                    $"Couldn't reach {peerName}. Make sure Beam is open on that computer and both computers are on the same network.",
                    $"{se.SocketErrorCode}: {se.Message}");
            case TimeoutException:
                return new TransferError(TransferErrorKind.ConnectionLost,
                    $"{peerName} stopped responding. The transfer could not be completed.", ex.Message);
            case SocketException or EndOfStreamException or IOException or ObjectDisposedException:
                return new TransferError(TransferErrorKind.ConnectionLost,
                    "Connection was lost. The transfer could not be completed.", Describe(ex));
            default:
                return new TransferError(TransferErrorKind.Unknown, "Something went wrong and the transfer stopped.", ex.ToString());
        }
    }

    public static TransferError FromRemoteReason(string reason, string peerName, string? detail = null) => reason switch
    {
        Reasons.Declined => new(TransferErrorKind.Declined, $"{peerName} declined the files.", detail),
        Reasons.TimedOut => new(TransferErrorKind.TimedOut, $"{peerName} didn't respond to the request in time.", detail),
        Reasons.Busy => new(TransferErrorKind.Busy, $"{peerName} is busy with another request. Try again in a moment.", detail),
        Reasons.NoSpace => new(TransferErrorKind.NotEnoughSpace, $"{peerName} doesn't have enough free disk space.", detail),
        Reasons.DestinationUnavailable => new(TransferErrorKind.DestinationUnavailable,
            $"The folder on {peerName} where files are saved isn't available.", detail),
        Reasons.PermissionDenied => new(TransferErrorKind.PermissionDenied,
            $"{peerName} isn't allowed to save files in its receive folder.", detail),
        Reasons.WriteFailed => new(TransferErrorKind.DestinationUnavailable, $"{peerName} couldn't save the files.", detail),
        Reasons.Shutdown => new(TransferErrorKind.CancelledByRemote, $"Beam was closed on {peerName}.", detail),
        Reasons.Protocol => new(TransferErrorKind.Protocol,
            $"{peerName} couldn't understand this transfer. Make sure both computers run the latest version of Beam.", detail),
        _ => new(TransferErrorKind.CancelledByRemote, $"{peerName} cancelled the transfer.", detail),
    };

    /// <summary>Maps a local disk problem to a friendly error.</summary>
    public static TransferError FromLocalFileException(Exception ex, string? context = null)
    {
        var details = context == null ? Describe(ex) : $"{context}: {Describe(ex)}";
        if (IsDiskFull(ex))
            return new TransferError(TransferErrorKind.NotEnoughSpace, "The disk is full. Free up some space and try again.", details);
        return ex switch
        {
            UnauthorizedAccessException => new TransferError(TransferErrorKind.PermissionDenied,
                "Beam doesn't have permission to save files in this folder. Choose a different folder in Settings.", details),
            DirectoryNotFoundException or DriveNotFoundException => new TransferError(TransferErrorKind.DestinationUnavailable,
                "The folder where files are saved is no longer available. Check that the drive is connected.", details),
            PathTooLongException => new TransferError(TransferErrorKind.DestinationUnavailable,
                "A file name is too long to be saved in this folder.", details),
            _ => new TransferError(TransferErrorKind.DestinationUnavailable, "A file couldn't be saved.", details),
        };
    }

    public static string ReasonFor(TransferError error) => error.Kind switch
    {
        TransferErrorKind.NotEnoughSpace => Reasons.NoSpace,
        TransferErrorKind.PermissionDenied => Reasons.PermissionDenied,
        TransferErrorKind.DestinationUnavailable => Reasons.DestinationUnavailable,
        _ => Reasons.WriteFailed,
    };

    public static bool IsDiskFull(Exception ex)
    {
        var code = ex.HResult & 0xFFFF;
        // ERROR_DISK_FULL (112), ERROR_HANDLE_DISK_FULL (39); ENOSPC (28) on Unix.
        return ex is IOException && (code == 112 || code == 39 || (!OperatingSystem.IsWindows() && code == 28)
                                     || ex.Message.Contains("No space left", StringComparison.OrdinalIgnoreCase));
    }

    private static string Describe(Exception ex)
    {
        var message = $"{ex.GetType().Name}: {ex.Message}";
        if (ex.InnerException != null) message += $" ({ex.InnerException.GetType().Name}: {ex.InnerException.Message})";
        return message;
    }
}
