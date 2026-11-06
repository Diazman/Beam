using System.Net;
using System.Security.Cryptography;
using System.Text;
using Beam.Core.Diagnostics;
using Beam.Core.Direct;
using Beam.Core.Discovery;
using Beam.Core.Localization;
using Beam.Core.Protocol;
using Beam.Core.Settings;
using Beam.Core.Transfer;

namespace Beam.Core.Trust;

/// <summary>
/// "Add a trusted device": one device shows a code (QR) with its certificate fingerprint and a one-time token, good for
/// a few minutes; the other scans it, connects to that certificate only and presents the token. Both then trust each
/// other's certificate: files are accepted without asking, and either may ask to browse the other. Only someone who
/// saw the code on screen can do it; afterwards neither can be impersonated.
/// </summary>
public sealed class TrustService
{
    internal static TimeSpan InviteLifetime { get; set; } = TimeSpan.FromMinutes(10);
    private const int MaxFailedTries = 5;

    private readonly TransferService _transfers;
    private readonly DiscoveryService _discovery;
    private readonly SettingsStore _settings;
    private readonly Func<PairingCode> _pairingCode;
    private readonly Action<string, string, string, string> _remember;
    private readonly object _gate = new();
    private Invite? _invite;

    private sealed record Invite(string Token, DateTimeOffset ExpiresAt)
    {
        public int Failures { get; set; }
    }

    internal TrustService(TransferService transfers, DiscoveryService discovery, SettingsStore settings, Func<PairingCode> pairingCode,
        Action<string, string, string, string> remember)
    {
        _transfers = transfers;
        _discovery = discovery;
        _settings = settings;
        _pairingCode = pairingCode;
        _remember = remember;
        _transfers.PairHandler = HandleAsync;
    }

    /// <summary>The device that scanned this device's code now trusts it, and this device trusts it (raised on any thread).</summary>
    public event Action<TrustedDevice>? Added;

    /// <summary>This device's code: who it is and a new one-time token (10 minutes, one device; a new code replaces it).</summary>
    public PairingCode CreateCode()
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        lock (_gate) _invite = new Invite(token, DateTimeOffset.UtcNow + InviteLifetime);
        var code = _pairingCode();
        code.TrustToken = token;
        Log.Info("Trust: showing a code to add a trusted device (valid for 10 minutes, one device)");
        return code;
    }

    /// <summary>The code was closed: it can't be used any more.</summary>
    public void CancelCode()
    {
        lock (_gate) _invite = null;
    }

    /// <summary>
    /// The code of another device was scanned: connects to that device's certificate only (on the Wi-Fi both are on),
    /// presents the token, and both then trust each other. Throws <see cref="TransferException"/> with a message for the user.
    /// </summary>
    public async Task<TrustedDevice> AddAsync(PairingCode code, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(code.TrustToken))
            throw new TransferException(TransferErrorKind.Protocol,
                L.T("This isn't a code for adding a trusted device. On the other device, open Settings → Trusted devices → Add a trusted device."));
        if (string.Equals(code.Fingerprint, _transfers.Identity.Fingerprint, StringComparison.OrdinalIgnoreCase))
            throw new TransferException(TransferErrorKind.Protocol, L.T("This is this device's own code."));
        var endpoints = code.ToDevice().Endpoints.Concat(_discovery.Find(code.DeviceId)?.Endpoints ?? Array.Empty<IPEndPoint>())
            .Where(e => !_transfers.IsDirectAddress(e.Address))
            .DistinctBy(e => e.ToString())
            .ToList();
        var unreachable = L.T("Couldn't reach {0}. To add a trusted device, both need to be on the same Wi-Fi (only this once). Then scan the code again.", code.Name);
        if (endpoints.Count == 0) throw new TransferException(TransferErrorKind.ConnectFailed, unreachable);

        PairResponseMessage response;
        try
        {
            await using var connection = await SecureTransport.ConnectAsync(_transfers.Identity, _transfers.CreateHello(ConnectionPurpose.Pair),
                endpoints, code.Fingerprint, code.Name, cancellationToken).ConfigureAwait(false);
            await connection.Channel.SendAsync(FrameType.PairRequest, new PairRequestMessage { Token = code.TrustToken }, cancellationToken).ConfigureAwait(false);
            response = await connection.Channel.ReadMessageAsync<PairResponseMessage>(FrameType.PairResponse, TimeSpan.FromSeconds(30), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TransferException ex) when (ex.Error.Kind == TransferErrorKind.ConnectFailed)
        {
            throw new TransferException(new TransferError(TransferErrorKind.ConnectFailed, unreachable, ex.Error.Details), ex);
        }

        if (!response.Ok) throw new TransferException(TransferErrorKind.Declined, response.Message ?? L.T("{0} didn't accept.", code.Name));
        var name = string.IsNullOrWhiteSpace(response.Name) ? code.Name : AppSettings.NormalizeDeviceName(response.Name);
        var device = Trust(code.Fingerprint, code.DeviceId, name, code.ToDevice().Kind);
        var addresses = endpoints.Select(e => e.ToString()).Take(4).ToList();
        _settings.Update(s =>
        {
            foreach (var t in s.TrustedDevices.Where(t => string.Equals(t.Fingerprint, code.Fingerprint, StringComparison.OrdinalIgnoreCase)))
                t.Addresses = addresses;
        });
        foreach (var endpoint in endpoints) _discovery.AddUnicastTarget(endpoint.Address);
        _discovery.Refresh();
        Log.Info($"Trust: {name} ({code.DeviceId}) and this device trust each other now (its code was scanned)");
        Added?.Invoke(device);
        return device;
    }

    /// <summary>Stops trusting a device: it can no longer send without asking, nor ask to browse.</summary>
    public void Remove(string fingerprint)
    {
        _settings.Update(s => s.TrustedDevices.RemoveAll(t => string.Equals(t.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase)));
        Log.Info("Trust: a device is no longer trusted");
    }

    /// <summary>A device scanned this device's code.</summary>
    private async Task HandleAsync(PeerConnection connection, CancellationToken token)
    {
        var request = await connection.Channel.ReadMessageAsync<PairRequestMessage>(FrameType.PairRequest, TimeSpan.FromSeconds(30), token)
            .ConfigureAwait(false);
        var hello = connection.RemoteHello;
        var name = AppSettings.NormalizeDeviceName(hello.DeviceName);
        bool valid;
        lock (_gate)
        {
            var invite = _invite;
            valid = invite != null && invite.ExpiresAt > DateTimeOffset.UtcNow && request.Token != null
                    && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(request.Token), Encoding.UTF8.GetBytes(invite.Token));
            if (valid) _invite = null; // one device per code
            else if (invite != null && ++invite.Failures >= MaxFailedTries) _invite = null; // someone is guessing: the code goes
        }

        if (!valid)
        {
            Log.Warn($"Trust: refused \"{name}\" ({hello.DeviceId}): no valid code is shown");
            await connection.Channel.SendAsync(FrameType.PairResponse, new PairResponseMessage
            {
                Ok = false,
                Message = L.T("This code isn't valid any more. Show a new one on the other device (Settings → Trusted devices → Add a trusted device) and scan it."),
            }, token).ConfigureAwait(false);
            return;
        }

        var device = Trust(connection.RemoteFingerprint, hello.DeviceId, name, string.IsNullOrEmpty(hello.Kind) ? DeviceKinds.Desktop : hello.Kind);
        await connection.Channel.SendAsync(FrameType.PairResponse, new PairResponseMessage { Ok = true, Name = _settings.Current.DeviceName }, token)
            .ConfigureAwait(false);
        Log.Info($"Trust: {name} ({hello.DeviceId}) scanned this device's code; they trust each other now");
        Added?.Invoke(device);
    }

    private TrustedDevice Trust(string fingerprint, string deviceId, string name, string kind)
    {
        var device = new TrustedDevice { Fingerprint = fingerprint.ToLowerInvariant(), DeviceId = deviceId, Name = name };
        _settings.Update(s =>
        {
            // A device trusted before (re-installed: a new certificate, the same id) keeps its browsing choice.
            var before = s.TrustedDevices.FirstOrDefault(t => string.Equals(t.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase));
            if (before != null)
            {
                device.AlwaysBrowse = before.AlwaysBrowse;
                device.Addresses = before.Addresses;
            }
            s.TrustedDevices.RemoveAll(t => string.Equals(t.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase) || t.DeviceId == deviceId);
            s.TrustedDevices.Add(device);
        });
        _remember(deviceId, name, fingerprint, kind);
        return device;
    }
}
