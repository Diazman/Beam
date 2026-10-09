using Avalonia.Media;
using Avalonia.Threading;
using Beam.App.Infrastructure;
using Beam.App.Services;
using Beam.Core;
using Beam.Core.Diagnostics;
using Beam.Core.Direct;
using Beam.Core.Localization;
using Beam.Core.Phone;
using Beam.Core.Transfer;
using Beam.Core.Util;

namespace Beam.App.ViewModels;

/// <summary>
/// The Phone page: shows a QR code that opens Beam's page in a phone's browser, so files can go
/// both ways with any phone — no app to install.
/// </summary>
public sealed class PhoneViewModel : ObservableObject
{
    /// <summary>The link stops working after this long without any phone activity.</summary>
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(30);

    private readonly BeamNode _node;
    private readonly IUiServices _ui;
    private readonly MainViewModel _main;
    private readonly DispatcherTimer _idleTimer;
    private DateTime _lastActivity = DateTime.UtcNow;
    private Geometry? _qr;
    private string _link = "";
    private string _problem = "";
    private string _copyStatus = "";
    private DirectNetwork? _directNetwork;
    private bool _directStarting;
    private Geometry? _wifiQr;
    private readonly DispatcherTimer _directTimer;

    public PhoneViewModel(BeamNode node, IUiServices ui, MainViewModel main)
    {
        _node = node;
        _ui = ui;
        _main = main;
        TurnOnCommand = new RelayCommand(TurnOn);
        TurnOffCommand = new AsyncCommand(TurnOffAsync);
        CopyLinkCommand = new AsyncCommand(CopyLinkAsync);
        ShareFilesCommand = new AsyncCommand(async () => await ShareAsync(await _ui.PickFilesAsync()));
        ShareFolderCommand = new AsyncCommand(async () => await ShareAsync(await _ui.PickFoldersAsync()));
        StopSharingCommand = new RelayCommand(() => _node.PhoneLink.StopSharing());
        ConnectDirectlyCommand = new AsyncCommand(ConnectDirectlyAsync);
        StopDirectCommand = new RelayCommand(StopDirect);
        // The computer's direct-network address appears a moment after the network starts.
        _directTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Refresh());

        _node.PhoneLink.Activity += () => Dispatcher.UIThread.Post(() =>
        {
            _lastActivity = DateTime.UtcNow;
            Refresh();
        });
        _idleTimer = new DispatcherTimer(TimeSpan.FromMinutes(1), DispatcherPriority.Background, async (_, _) =>
        {
            if (IsOn && DateTime.UtcNow - _lastActivity > IdleTimeout && !HasActivePhoneTransfers) await TurnOffAsync();
        });
    }

    public bool IsOn => _node.PhoneLink.IsRunning;

    public bool IsOff => !IsOn;

    public Geometry? Qr
    {
        get => _qr;
        private set => SetProperty(ref _qr, value);
    }

    public string Link
    {
        get => _link;
        private set => SetProperty(ref _link, value);
    }

    public string OtherLinks { get; private set; } = "";

    public bool HasOtherLinks => OtherLinks.Length > 0;

    public string ConnectionText => _node.PhoneLink.LastVisitor is { } visitor
        ? L.T("Connected: {0}. Keep the page open on the phone while files transfer.", visitor)
        : L.T("Waiting for a phone to open the link…");

    public bool IsConnected => _node.PhoneLink.LastVisitor != null;

    public IReadOnlyList<string> SharedNames => _node.PhoneLink.SharedNames;

    public bool IsSharing => SharedNames.Count > 0;

    public string SharedSummary
    {
        get
        {
            if (!IsSharing) return L.T("Nothing shared yet. Files you share appear on the phone's page, ready to download.");
            var count = SharedNames.Count;
            var names = string.Join(", ", SharedNames.Take(3).Select(n => n.Split('/')[^1])) + (count > 3 ? "…" : "");
            return L.Plural(count, "Your phone can download {0} file: {1}", "Your phone can download {0} files: {1}", names);
        }
    }

    public string Problem
    {
        get => _problem;
        private set
        {
            if (SetProperty(ref _problem, value)) OnPropertyChanged(nameof(HasProblem));
        }
    }

    public bool HasProblem => Problem.Length > 0;

    public string CopyStatus
    {
        get => _copyStatus;
        private set => SetProperty(ref _copyStatus, value);
    }

    /// <summary>This computer can create its own Wi-Fi for a phone (Wi-Fi Direct): for iPhones, or when there is no shared Wi-Fi.</summary>
    public bool CanConnectDirectly => _node.Direct.CanHost;

    public bool OffersDirect => IsOn && CanConnectDirectly && !IsDirect && !_directStarting;

    /// <summary>Phones join this computer's own Wi-Fi network instead of a shared one.</summary>
    public bool IsDirect => _directNetwork != null;

    public bool IsDirectStarting => _directStarting;

    /// <summary>"Join this Wi-Fi" code for the phone's camera.</summary>
    public Geometry? WifiQr
    {
        get => _wifiQr;
        private set => SetProperty(ref _wifiQr, value);
    }

    public string DirectNetworkName => _directNetwork?.Ssid ?? "";

    public string DirectPassword => _directNetwork?.Passphrase ?? "";

    /// <summary>The page's address on the direct network isn't known yet.</summary>
    public bool IsWaitingForDirectAddress => IsDirect && Link.Length == 0;

    public AsyncCommand ConnectDirectlyCommand { get; }

    public RelayCommand StopDirectCommand { get; }

    public RelayCommand TurnOnCommand { get; }

    public AsyncCommand TurnOffCommand { get; }

    public AsyncCommand CopyLinkCommand { get; }

    public AsyncCommand ShareFilesCommand { get; }

    public AsyncCommand ShareFolderCommand { get; }

    public RelayCommand StopSharingCommand { get; }

    private bool HasActivePhoneTransfers => _node.Transfers.Sessions.Any(s => s.PeerId == PhoneLinkServer.PhonePeerId && !s.IsFinished && s.State != TransferState.WaitingForAcceptance);

    /// <summary>Shares paths with the phone (used by the page's buttons and by drag and drop).</summary>
    public async Task ShareAsync(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;
        if (!IsOn) TurnOn();
        if (!IsOn) return;
        try
        {
            _node.PhoneLink.Share(paths);
            Problem = "";
        }
        catch (TransferException ex) when (ex.Error.Kind == TransferErrorKind.SendLimitReached)
        {
            if (await _main.ShowUpgradeAsync(ex.Error.Message)) await ShareAsync(paths);
        }
        catch (TransferException ex)
        {
            Problem = ex.Error.Message;
        }

        Refresh();
    }

    private void TurnOn()
    {
        try
        {
            _node.PhoneLink.Start();
            _lastActivity = DateTime.UtcNow;
            Problem = "";
            _idleTimer.Start();
        }
        catch (Exception ex)
        {
            Log.Error("Phone link failed to start", ex);
            Problem = L.T("Beam couldn't open a network port for phones. Restart Beam and try again.");
        }

        Refresh();
    }

    private async Task ConnectDirectlyAsync()
    {
        if (!IsOn || IsDirect || _directStarting) return;
        _directStarting = true;
        Problem = "";
        Refresh();
        try
        {
            var network = await _node.Direct.HostForBrowserAsync(CancellationToken.None);
            _directNetwork = network;
            WifiQr = QrCode.Create(network.ToWifiQrText()).Geometry;
            _directTimer.Start();
        }
        catch (Exception ex)
        {
            Log.Warn("Direct network for the phone failed", ex);
            Problem = ex is DirectLinkException ? ex.Message : L.T("Couldn't connect directly. Beam continues over the network.");
        }
        finally
        {
            _directStarting = false;
            Refresh();
        }
    }

    private void StopDirect()
    {
        if (_directNetwork == null) return;
        _directNetwork = null;
        WifiQr = null;
        _directTimer.Stop();
        _node.Direct.StopForBrowser();
        Refresh();
    }

    private async Task TurnOffAsync()
    {
        StopDirect();
        _idleTimer.Stop();
        await _node.PhoneLink.StopAsync();
        Refresh();
    }

    private async Task CopyLinkAsync()
    {
        if (Link.Length == 0) return;
        await _ui.CopyToClipboardAsync(Link);
        CopyStatus = L.T("Copied");
    }

    private void Refresh()
    {
        if (IsOn)
        {
            var links = _node.PhoneLink.GetLinks(_node.GetLocalAddresses());
            if (IsDirect)
            {
                // Only the address on this computer's own Wi-Fi: the phone has no other network then.
                links = links.Where(l => System.Net.IPAddress.TryParse(new Uri(l).Host, out var ip) && _node.Transfers.IsDirectAddress(ip)).ToList();
                if (links.Count > 0) _directTimer.Stop();
            }

            var link = links.FirstOrDefault() ?? "";
            if (link != Link)
            {
                Link = link;
                CopyStatus = "";
                if (link.Length > 0)
                {
                    Qr = QrCode.Create(link).Geometry;
                }
                else
                {
                    Qr = null;
                    if (!IsDirect) Problem = L.T("This computer isn't connected to a network. Connect to Wi-Fi, then try again.");
                }
            }

            OtherLinks = links.Count > 1 ? L.T("Other networks: {0}", string.Join("  ·  ", links.Skip(1))) : "";
        }
        else
        {
            Link = "";
            Qr = null;
            OtherLinks = "";
        }

        foreach (var name in new[]
                 {
                     nameof(IsOn), nameof(IsOff), nameof(OtherLinks), nameof(HasOtherLinks), nameof(ConnectionText),
                     nameof(IsConnected), nameof(SharedNames), nameof(IsSharing), nameof(SharedSummary),
                     nameof(CanConnectDirectly), nameof(OffersDirect), nameof(IsDirect), nameof(IsDirectStarting),
                     nameof(DirectNetworkName), nameof(DirectPassword), nameof(IsWaitingForDirectAddress),
                 })
        {
            OnPropertyChanged(name);
        }
    }
}
