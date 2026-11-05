using System.Net;
using Beam.Core.Discovery;
using Beam.Core.Localization;
using Beam.Core.Settings;
using Beam.Core.Transfer;

namespace Beam.Core.Direct;

/// <summary>A direct link kept for something other than a transfer (browsing), set up like a Direct send's.</summary>
public sealed class DirectLinkLease
{
    internal DirectLinkLease(object send) => Send = send;

    internal object Send { get; }
}

public sealed partial class DirectManager
{
    /// <summary>
    /// A direct link to <paramref name="device"/> for browsing: nothing is set up yet. <see cref="ConnectLinkAsync"/>
    /// sets it up (this device hosts, the other joins, both checked by certificate) and gives the verified address;
    /// <see cref="CloseLinkAsync"/> ends it.
    /// </summary>
    internal DirectLinkLease OpenLink(DeviceInfo device) =>
        new(new DirectSend { Receiver = device, Session = NewSession(device), Mode = ConnectionMethod.Direct });

    /// <summary>
    /// The link's verified address on the direct network (set up the first time, checked again or set up anew after
    /// a drop). Throws <see cref="TransferException"/> naming what failed.
    /// </summary>
    internal async Task<IPEndPoint> ConnectLinkAsync(DirectLinkLease lease, CancellationToken cancellationToken)
    {
        var send = (DirectSend)lease.Send;
        await PrepareDirectAsync(send, cancellationToken).ConfigureAwait(false);
        return send.Session.VerifiedEndpoint
               ?? throw new TransferException(TransferErrorKind.DirectFailed, L.T("Couldn't connect directly to {0}.", send.Receiver.Name));
    }

    /// <summary>The link was lost: the next <see cref="ConnectLinkAsync"/> sets it up again.</summary>
    internal void LinkLost(DirectLinkLease lease) => ((DirectSend)lease.Send).LinkLost = true;

    /// <summary>The link isn't needed any more: its session ends, and this device's network goes when nothing else uses it.</summary>
    internal Task CloseLinkAsync(DirectLinkLease lease) => FinishSendAsync((DirectSend)lease.Send);
}
