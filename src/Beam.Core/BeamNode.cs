using Beam.Core.Files;
using System.Net;
using System.Net.Sockets;
using Beam.Core.Diagnostics;
using Beam.Core.Direct;
using Beam.Core.Discovery;
using Beam.Core.History;
using Beam.Core.Identity;
using Beam.Core.Licensing;
using Beam.Core.Phone;
using Beam.Core.Settings;
using Beam.Core.Storage;
using Beam.Core.Transfer;
using Beam.Core.Localization;

namespace Beam.Core;

public sealed class BeamNodeOptions
{
    public required AppDataPaths Paths { get; init; }

    public int TransferPort { get; init; } = AppInfo.TransferPort;

    public DiscoveryOptions Discovery { get; init; } = new();

    public IIncomingTransferHandler? Handler { get; init; }

    public IEditionPolicy Edition { get; init; } = new Edition();

    /// <summary>Opens files handed over as streams (Android content:// URIs). Null on desktop.</summary>
    public IExternalFiles? ExternalFiles { get; init; }

    /// <summary>What this device is ("desktop", "laptop", "phone"…), shown to others.</summary>
    public string? DeviceKind { get; init; }

    /// <summary>The platform's direct Wi-Fi link (Wi-Fi Direct); null where there is none.</summary>
    public IDirectLink? DirectLink { get; init; }
}

/// <summary>
/// Everything a Beam device needs, wired together: identity, settings, history, discovery and
/// transfers. The desktop app (and tests, and a future phone app) host one of these.
/// </summary>
public sealed class BeamNode : IAsyncDisposable
{
    private readonly BeamNodeOptions _options;
    private readonly DelegatingHandler _handler;
    private readonly CancellationTokenSource _cts = new();
    private Task? _manualPeerTask;
    private ConnectionMethod _connection;
    private bool _started;
    private int _disposed;

    private BeamNode(BeamNodeOptions options)
    {
        _options = options;
        options.Paths.EnsureCreated();
        Log.Initialize(options.Paths.LogDirectory);

        Settings = new SettingsStore(options.Paths.SettingsFile);
        History = new HistoryStore(options.Paths.HistoryFile);
        Identity = DeviceIdentity.LoadOrCreate(options.Paths.IdentityFile);
        ResumeStore = new ResumeStore(options.Paths.ResumeDirectory);
        ResumeStore.CleanupExpired();
        _handler = new DelegatingHandler { Inner = options.Handler };

        Quota = new SendQuota(options.Paths.UsageFile, options.Edition);
        Transfers = new TransferService(Identity, ResumeStore, _handler, CreatePolicy, () => Settings.Current.DeviceName,
            deviceKind: options.DeviceKind is { } kind ? () => kind : null,
            sendBytesPerSecond: () => options.Edition.IsEnabled(Feature.FullSpeed) ? 0 : FreeLimits.MaxSendBytesPerSecond)
        {
            ExternalFiles = options.ExternalFiles,
        };
        Transfers.SessionFinished += RecordHistory;
        PhoneLink = new PhoneLinkServer(Transfers, Quota, () => Settings.Current.DeviceName, CreatePolicy, _handler);
        Discovery = new DiscoveryService(options.Discovery,
            () => new LocalAnnouncement(Identity.DeviceId, Settings.Current.DeviceName, Transfers.Port, Identity.Fingerprint,
                options.DeviceKind ?? DeviceKinds.Desktop, Direct!.Roles));
        Discovery.Discoverable = Settings.Current.Discoverable;
        Direct = new DirectManager(Transfers, Discovery, Settings, () => Identity.DeviceId, options.DirectLink);
        _connection = Settings.Current.Connection;
        Settings.Changed += OnSettingsChanged;
    }

    public SettingsStore Settings { get; }

    public HistoryStore History { get; }

    public DeviceIdentity Identity { get; }

    public ResumeStore ResumeStore { get; }

    public TransferService Transfers { get; }

    public DiscoveryService Discovery { get; }

    /// <summary>Same network or direct Wi-Fi link, per device and switchable during transfers.</summary>
    public DirectManager Direct { get; }

    public IEditionPolicy Edition => _options.Edition;

    /// <summary>Sending and receiving with phones through their web browser (started on demand).</summary>
    public PhoneLinkServer PhoneLink { get; }

    /// <summary>The free edition's daily send limit.</summary>
    public SendQuota Quota { get; }

    public AppDataPaths Paths => _options.Paths;

    /// <summary>Set by the UI: asks the user about incoming transfers. Without one, transfers are declined.</summary>
    public IIncomingTransferHandler? IncomingHandler
    {
        get => _handler.Inner;
        set => _handler.Inner = value;
    }

    public static BeamNode Create(BeamNodeOptions options) => new(options);

    /// <summary>Opens the network ports. Call after the user has seen the first-run explanation.</summary>
    public void Start()
    {
        if (_started) return;
        _started = true;
        Log.Info($"Starting {AppInfo.ProductName} {AppInfo.Version} as '{Settings.Current.DeviceName}' ({Identity.DeviceId}, {DeviceIdentity.ShortCode(Identity.Fingerprint)})");
        Transfers.Start(_options.TransferPort);
        Discovery.Start();
        Direct.Start();
        _manualPeerTask = Task.Run(ManualPeerLoopAsync);
    }

    /// <summary>
    /// Starts sending to one device. Each call counts as one send towards the free edition's daily
    /// limit; throws <see cref="TransferException"/> (<see cref="TransferErrorKind.SendLimitReached"/>) when it is used up.
    /// </summary>
    public TransferSession Send(DeviceInfo device, IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) throw new ArgumentException("Nothing to send.", nameof(paths));
        if (!Quota.TryUse())
            throw new TransferException(TransferErrorKind.SendLimitReached,
                L.Plural(FreeLimits.SendsPerDay, "You've used today's {0} free send. Upgrade to Beam Pro for unlimited sends, or send again tomorrow.", "You've used today's {0} free sends. Upgrade to Beam Pro for unlimited sends, or send again tomorrow."));
        return Transfers.Send(device, paths, () => Direct.Order(Discovery.Find(device.Id) ?? device));
    }

    /// <summary>
    /// This device's pairing code (shown as a QR code on the PC): who it is, where it is, and its direct network,
    /// so a phone's Beam app that scans it can find and reach it from then on, with or without a shared Wi-Fi.
    /// </summary>
    public PairingCode CreatePairingCode() => new()
    {
        DeviceId = Identity.DeviceId,
        Name = Settings.Current.DeviceName,
        Fingerprint = Identity.Fingerprint,
        Kind = _options.DeviceKind ?? DeviceKinds.Desktop,
        Addresses = GetLocalAddresses().Where(a => !a.StartsWith("192.168.137.", StringComparison.Ordinal)).Take(4).ToList(),
        Ssid = Direct.CanHost ? Direct.OwnNetwork().Ssid : null,
        Passphrase = Direct.CanHost ? Direct.OwnNetwork().Passphrase : null,
    };

    /// <summary>
    /// Pairs with the device whose code was scanned: trusts it (its files are accepted without asking), remembers
    /// its direct network, and looks for it at the addresses in the code. Returns the device.
    /// </summary>
    public DeviceInfo Pair(PairingCode code)
    {
        var device = code.ToDevice();
        Settings.Update(s =>
        {
            s.TrustedDevices.RemoveAll(t => string.Equals(t.Fingerprint, code.Fingerprint, StringComparison.OrdinalIgnoreCase));
            s.TrustedDevices.Add(new TrustedDevice { Fingerprint = code.Fingerprint, DeviceId = code.DeviceId, Name = code.Name });
        });
        if (code.Network is { } network) Direct.Remember(code.DeviceId, code.Name, code.Fingerprint, device.Kind, network);
        foreach (var endpoint in device.Endpoints) Discovery.AddUnicastTarget(endpoint.Address);
        Discovery.Refresh();
        Log.Info($"Paired with {code.Name} ({code.DeviceId}) from its code");
        return device;
    }

    /// <summary>Sends a piece of text or a link (counts as a send for the free edition).</summary>
    public TransferSession SendText(DeviceInfo device, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("Nothing to send.", nameof(text));
        if (!Quota.TryUse())
            throw new TransferException(TransferErrorKind.SendLimitReached,
                L.Plural(FreeLimits.SendsPerDay, "You've used today's {0} free send. Upgrade to Beam Pro for unlimited sends, or send again tomorrow.", "You've used today's {0} free sends. Upgrade to Beam Pro for unlimited sends, or send again tomorrow."));
        return Transfers.SendText(device, text, () => Direct.Order(Discovery.Find(device.Id) ?? device));
    }

    /// <summary>
    /// Connects to a device by the address shown on its screen ("192.168.1.20", "192.168.1.20:47822",
    /// or a computer name) and remembers it. Throws <see cref="TransferException"/> with a friendly message.
    /// </summary>
    public async Task<DeviceInfo> AddDeviceByAddressAsync(string text, CancellationToken cancellationToken)
    {
        var (host, port) = ParseAddress(text);
        IPAddress[] addresses;
        try
        {
            addresses = IPAddress.TryParse(host, out var ip)
                ? new[] { ip }
                : (await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false))
                    .Where(a => a.AddressFamily == AddressFamily.InterNetwork).ToArray();
        }
        catch (SocketException ex)
        {
            throw new TransferException(TransferErrorKind.ConnectFailed, L.T("Couldn't find a computer called \"{0}\" on this network.", host), ex.Message, ex);
        }

        if (addresses.Length == 0)
            throw new TransferException(TransferErrorKind.ConnectFailed, L.T("Couldn't find a computer called \"{0}\" on this network.", host));

        var ports = port != null ? new[] { port.Value } : Enumerable.Range(AppInfo.TransferPort, 4).ToArray();
        Exception? last = null;
        foreach (var address in addresses)
        {
            foreach (var candidatePort in ports)
            {
                try
                {
                    var device = await Transfers.ProbeAsync(new IPEndPoint(address, candidatePort), cancellationToken).ConfigureAwait(false);
                    var normalized = port != null || candidatePort != AppInfo.TransferPort ? $"{address}:{candidatePort}" : address.ToString();
                    Settings.Update(s =>
                    {
                        if (!s.ManualAddresses.Contains(normalized, StringComparer.OrdinalIgnoreCase)) s.ManualAddresses.Add(normalized);
                    });
                    Discovery.AddUnicastTarget(address);
                    Discovery.ReportReachable(device);
                    return Discovery.Find(device.Id) ?? device;
                }
                catch (TransferException ex) when (ex.Error.Kind is TransferErrorKind.ConnectFailed && ex.InnerException is SocketException)
                {
                    last = ex;
                }
            }
        }

        if (last is TransferException te) throw new TransferException(new TransferError(TransferErrorKind.ConnectFailed,
            L.T("Couldn't connect to {0}. Check the address, and make sure Beam is open on that computer.", text.Trim()), te.Error.Details), last);
        throw new TransferException(TransferErrorKind.ConnectFailed, L.T("Couldn't connect to {0}.", text.Trim()));
    }

    public void ForgetManualAddress(string address)
    {
        Settings.Update(s => s.ManualAddresses.RemoveAll(a => string.Equals(a, address, StringComparison.OrdinalIgnoreCase)));
        if (TryParseEndPoint(address, out var endpoint)) Discovery.RemoveUnicastTarget(endpoint.Address);
    }

    /// <summary>The address other people can type to reach this computer (fallback when discovery fails).</summary>
    public IReadOnlyList<string> GetLocalAddresses()
    {
        var result = new List<string>();
        try
        {
            foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (!DirectAddresses.IsUsable(nic)) continue;
                if (nic.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
                foreach (var address in nic.GetIPProperties().UnicastAddresses)
                {
                    if (address.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var bytes = address.Address.GetAddressBytes();
                    if (bytes[0] == 169 && bytes[1] == 254) continue; // no DHCP: not useful
                    var text = address.Address.ToString();
                    if (Transfers.Port != AppInfo.TransferPort && Transfers.Port != 0) text += ":" + Transfers.Port;
                    result.Add(text);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Could not list local addresses", ex);
        }

        return result;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return; // safe to call more than once
        _cts.Cancel();
        Settings.Changed -= OnSettingsChanged;
        Direct.Stop();
        Discovery.Dispose();
        await PhoneLink.DisposeAsync().ConfigureAwait(false);
        await Transfers.DisposeAsync().ConfigureAwait(false);
        if (_manualPeerTask != null)
        {
            try { await _manualPeerTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { /* ignore */ }
        }

        Identity.Dispose();
        Log.Info("Stopped");
    }

    internal static (string Host, int? Port) ParseAddress(string text)
    {
        var trimmed = (text ?? "").Trim();
        if (trimmed.Length == 0) throw new TransferException(TransferErrorKind.ConnectFailed, L.T("Enter the address shown on the other computer."));
        var colon = trimmed.LastIndexOf(':');
        if (colon > 0 && trimmed.IndexOf(':') == colon)
        {
            if (!int.TryParse(trimmed[(colon + 1)..], out var port) || port is <= 0 or > 65535)
                throw new TransferException(TransferErrorKind.ConnectFailed, L.T("That address doesn't look right. It should look like 192.168.1.20."));
            return (trimmed[..colon], port);
        }

        return (trimmed, null);
    }

    private static bool TryParseEndPoint(string text, out IPEndPoint endpoint)
    {
        endpoint = new IPEndPoint(IPAddress.None, 0);
        try
        {
            var (host, port) = ParseAddress(text);
            if (!IPAddress.TryParse(host, out var ip)) return false;
            endpoint = new IPEndPoint(ip, port ?? AppInfo.TransferPort);
            return true;
        }
        catch (TransferException)
        {
            return false;
        }
    }

    private IncomingPolicy CreatePolicy()
    {
        var settings = Settings.Current;
        return new IncomingPolicy
        {
            DefaultFolder = settings.EffectiveReceiveFolder,
            TrustedFingerprints = _options.Edition.IsEnabled(Feature.TrustedDevices)
                ? settings.TrustedDevices.Select(t => t.Fingerprint).ToHashSet(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(),
        };
    }

    private void OnSettingsChanged(AppSettings settings)
    {
        Discovery.Discoverable = settings.Discoverable;
        Discovery.AnnounceNow();
        if (settings.Connection != _connection)
        {
            _connection = settings.Connection;
            Direct.OnSettingChanged(settings.Connection);
        }
    }

    private async Task ManualPeerLoopAsync()
    {
        foreach (var address in Settings.Current.ManualAddresses)
        {
            if (TryParseEndPoint(address, out var endpoint)) Discovery.AddUnicastTarget(endpoint.Address);
        }

        var token = _cts.Token;
        while (!token.IsCancellationRequested)
        {
            foreach (var address in Settings.Current.ManualAddresses)
            {
                if (!TryParseEndPoint(address, out var endpoint)) continue;
                var known = Discovery.Devices.Any(d => d.Endpoints.Any(e => e.Address.Equals(endpoint.Address)));
                if (known) continue;
                try
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
                    cts.CancelAfter(TimeSpan.FromSeconds(8));
                    var device = await Transfers.ProbeAsync(endpoint, cts.Token).ConfigureAwait(false);
                    Discovery.ReportReachable(device);
                }
                catch (Exception) when (!token.IsCancellationRequested)
                {
                    // Not reachable right now; try again next round.
                }
                catch
                {
                    return;
                }
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(8), token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void RecordHistory(TransferSession session)
    {
        var snapshot = session.GetSnapshot();
        var status = snapshot.State switch
        {
            TransferState.Completed => HistoryStatus.Completed,
            TransferState.CompletedWithErrors => HistoryStatus.CompletedWithErrors,
            TransferState.Declined => HistoryStatus.Declined,
            TransferState.Cancelled => HistoryStatus.Cancelled,
            TransferState.Interrupted => HistoryStatus.Interrupted,
            _ => HistoryStatus.Failed,
        };

        History.Add(new HistoryEntry
        {
            Id = session.Id,
            Direction = session.Direction,
            DeviceName = session.PeerName,
            DeviceId = session.PeerId,
            Title = string.IsNullOrEmpty(session.Title) ? "Files" : session.Title,
            FileCount = snapshot.TotalFiles + snapshot.SkippedFiles,
            TotalBytes = snapshot.TotalBytes,
            Timestamp = session.FinishedAt ?? DateTimeOffset.Now,
            Status = status,
            Message = snapshot.Error?.Message,
            Folder = session.DestinationFolder,
            Paths = session.SavedRootPaths.ToList(),
            Text = session.Text is { } text ? (text.Length > HistoryStore.MaxTextLength ? text[..HistoryStore.MaxTextLength] : text) : null,
        });
    }

    private sealed class DelegatingHandler : IIncomingTransferHandler
    {
        public IIncomingTransferHandler? Inner { get; set; }

        public Task<bool> ReceiveTextAsync(IncomingText text, TransferSession session, CancellationToken cancellationToken) =>
            Inner?.ReceiveTextAsync(text, session, cancellationToken) ?? Task.FromResult(false);

        public Task<IncomingDecision> RequestApprovalAsync(IncomingRequest request, TransferSession session, CancellationToken cancellationToken) =>
            Inner?.RequestApprovalAsync(request, session, cancellationToken) ?? Task.FromResult(IncomingDecision.Decline());

        public Task<IReadOnlyList<ConflictAction>> ResolveConflictsAsync(IncomingRequest request, IReadOnlyList<FileConflict> conflicts, CancellationToken cancellationToken) =>
            Inner?.ResolveConflictsAsync(request, conflicts, cancellationToken)
            ?? Task.FromResult<IReadOnlyList<ConflictAction>>(conflicts.Select(_ => ConflictAction.KeepBoth).ToList());
    }
}
