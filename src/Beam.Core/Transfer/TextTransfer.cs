using Beam.Core.Diagnostics;
using Beam.Core.Discovery;
using Beam.Core.Protocol;
using Beam.Core.Localization;

namespace Beam.Core.Transfer;

/// <summary>
/// Sends and receives a piece of text or a link over its own short connection (protocol 2):
/// Hello(purpose "text") → Text → Result (shown) or Cancel (dismissed).
/// </summary>
internal static class TextTransfer
{
    /// <summary>Longest text Beam sends (characters).</summary>
    public const int MaxLength = 256 * 1024;

    public static async Task SendAsync(TransferService service, TransferSession session, string text, Func<DeviceInfo> target)
    {
        var token = session.CancellationToken;
        var device = target();
        try
        {
            session.SetState(TransferState.Connecting);
            await using var connection = await SecureTransport.ConnectAsync(
                service.Identity,
                service.CreateHello(ConnectionPurpose.Text),
                device.Endpoints,
                string.IsNullOrEmpty(device.Fingerprint) ? null : device.Fingerprint,
                session.PeerName,
                token).ConfigureAwait(false);

            if (connection.RemoteHello.Protocol < AppInfo.TextProtocolVersion)
            {
                session.SetState(TransferState.Failed, new TransferError(TransferErrorKind.IncompatibleVersion,
                    L.T("{0} has an older version of Beam that can't receive text. Update Beam on {0}.", session.PeerName)));
                return;
            }

            session.SetState(TransferState.WaitingForAcceptance);
            await connection.Channel.SendAsync(FrameType.Text, new TextMessage { TransferId = session.Id, Text = text }, token).ConfigureAwait(false);
            var reply = await connection.Channel.ReadAsync(TimeSpan.FromMinutes(3), token).ConfigureAwait(false);
            switch (reply.Type)
            {
                case FrameType.Result:
                    session.AddTransferred(text.Length);
                    session.FileCompleted();
                    session.SetState(TransferState.Completed);
                    break;
                case FrameType.Cancel:
                    var cancel = FrameChannel.Parse<CancelMessage>(reply);
                    session.SetState(TransferState.Declined, ErrorTranslator.FromRemoteReason(cancel.Reason, session.PeerName, cancel.Detail));
                    break;
                default:
                    throw new ProtocolException($"Unexpected {reply.Type} after sending text.");
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            session.SetState(TransferState.Cancelled, new TransferError(TransferErrorKind.CancelledByUser, L.T("You cancelled sending the text.")));
        }
        catch (Exception ex)
        {
            Log.Warn($"Sending text to {session.PeerName} failed: {ex.Message}");
            session.SetState(TransferState.Failed, ex is TransferException te ? te.Error : ErrorTranslator.FromException(ex, session.PeerName));
        }
    }

    public static async Task ReceiveAsync(TransferService service, PeerConnection connection, CancellationToken serviceToken)
    {
        var message = await connection.Channel.ReadMessageAsync<TextMessage>(FrameType.Text, TimeSpan.FromSeconds(30), serviceToken).ConfigureAwait(false);
        if (message.Text.Length == 0 || message.Text.Length > MaxLength) throw new ProtocolException("Invalid text.");

        var hello = connection.RemoteHello;
        var id = string.IsNullOrWhiteSpace(message.TransferId) ? Guid.NewGuid().ToString("N") : message.TransferId;
        var session = service.CreateIncomingSession(id, connection);
        session.Text = message.Text;
        session.SetDescription(new[] { Describe(message.Text) });
        session.SetTotals(1, message.Text.Length, 0, 0, 0);
        var trusted = service.Policy().TrustedFingerprints.Contains(connection.RemoteFingerprint);
        session.SetState(trusted ? TransferState.Transferring : TransferState.AwaitingDecision);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(serviceToken, session.CancellationToken);
        cts.CancelAfter(service.Policy().ApprovalTimeout);
        bool shown;
        try
        {
            shown = await service.Handler.ReceiveTextAsync(new IncomingText
            {
                SenderName = hello.DeviceName,
                SenderFingerprint = connection.RemoteFingerprint,
                Text = message.Text,
                IsTrusted = trusted,
            }, session, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            shown = false;
        }

        if (shown)
        {
            session.AddTransferred(message.Text.Length);
            session.FileCompleted();
            await connection.Channel.SendAsync(FrameType.Result, new ResultMessage { Completed = 1 }, serviceToken).ConfigureAwait(false);
            session.SetState(TransferState.Completed);
        }
        else
        {
            await connection.Channel.SendAsync(FrameType.Cancel, new CancelMessage { Reason = Reasons.Declined }, serviceToken).ConfigureAwait(false);
            session.SetState(TransferState.Declined, new TransferError(TransferErrorKind.Declined, L.T("You dismissed the text.")));
        }
    }

    /// <summary>One-line title for cards and history: the start of the text.</summary>
    public static string Describe(string text)
    {
        var line = text.Trim().Split('\n', 2)[0].Trim();
        if (line.Length == 0) line = L.T("Text");
        return line.Length > 60 ? line[..57].TrimEnd() + "…" : line;
    }
}
