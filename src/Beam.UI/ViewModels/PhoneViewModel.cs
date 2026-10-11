using Avalonia.Media;
using Avalonia.Threading;
using Beam.App.Infrastructure;
using Beam.App.Services;
using Beam.Core;
using Beam.Core.Diagnostics;
using Beam.Core.Localization;
using Beam.Core.Phone;
using Beam.Core.Transfer;
using Beam.Core.Util;

namespace Beam.App.ViewModels;

/// <summary>
/// The Phone page. By default it shows one code for the Beam app on a phone: scanning it pairs the two and, when this
/// computer can, joins its own Wi-Fi (no shared network needed). Phones without the app use "Connect with the
/// browser": a code that opens Beam's page in the phone's browser over the shared Wi-Fi.
/// </summary>
public sealed class PhoneViewModel : ObservableObject
{
    /// <summary>The browser link stops working after this long without any phone activity.</summary>
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(30);

    private readonly BeamNode _node;
    private readonly IUiServices _ui;
    private readonly MainViewModel _main;
    private readonly DispatcherTimer _idleTimer;
    private DateTime _lastActivity = DateTime.UtcNow;
    private Geometry? _qr;
    private Geometry? _appQr;
    private bool _appQrConnectsDirectly;
    private bool _browserMode;
    private bool _shown;
    private bool _hosting;
    private bool _hostingStarting;
    private string _link = "";
    private string _problem = "";
    private string _copyStatus = "";

    public PhoneViewModel(BeamNode node, IUiServices ui, MainViewModel main)
    {
        _node = node;
        _ui = ui;
        _main = main;
        ShowBrowserCommand = new RelayCommand(ShowBrowser);
        ShowAppCodeCommand = new AsyncCommand(ShowAppCodeAsync);
        CopyLinkCommand = new AsyncCommand(CopyLinkAsync);
        ShareFilesCommand = new AsyncCommand(async () => await ShareAsync(await _ui.PickFilesAsync()));
        ShareFolderCommand = new AsyncCommand(async () => await ShareAsync(await _ui.PickFoldersAsync()));
        StopSharingCommand = new RelayCommand(() => _node.PhoneLink.StopSharing());

        _node.PhoneLink.Activity += () => Dispatcher.UIThread.Post(() =>
        {
            _lastActivity = DateTime.UtcNow;
            Refresh();
        });
        _idleTimer = new DispatcherTimer(TimeSpan.FromMinutes(1), DispatcherPriority.Background, async (_, _) =>
        {
            if (IsOn && DateTime.UtcNow - _lastActivity > IdleTimeout && !HasActivePhoneTransfers) await StopBrowserLinkAsync();
        });
    }

    /// <summary>Beam's page for phone browsers is running.</summary>
    public bool IsOn => _node.PhoneLink.IsRunning;

    /// <summary>Showing the code for the Beam app (the default).</summary>
    public bool ShowsAppCode => !_browserMode;

    /// <summary>Showing the code that opens Beam's page in a phone's browser.</summary>
    public bool ShowsBrowserCode => _browserMode;

    /// <summary>The Beam app's code: pairs, and joins this computer's own Wi-Fi when it is on.</summary>
    public Geometry? AppQr
    {
        get => _appQr;
        private set => SetProperty(ref _appQr, value);
    }

    /// <summary>The app's code also brings the phone onto this computer's own Wi-Fi.</summary>
    public bool AppCodeConnectsDirectly => _appQrConnectsDirectly;

    public string AppCodeText => _appQrConnectsDirectly
        ? L.T("On the phone, open Beam and tap the scan button (the code icon next to Nearby devices). The phone connects to this computer by itself, with or without Wi-Fi, and stays paired.")
        : L.T("On the phone, open Beam and tap the scan button (the code icon next to Nearby devices). Both need to be on the same Wi-Fi; after that they find each other by themselves.");

    public bool IsHostingStarting => _hostingStarting;

    /// <summary>The browser page's code (also pairs the Beam app, over the shared Wi-Fi).</summary>
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

    public RelayCommand ShowBrowserCommand { get; }

    public AsyncCommand ShowAppCodeCommand { get; }

    public AsyncCommand CopyLinkCommand { get; }

    public AsyncCommand ShareFilesCommand { get; }

    public AsyncCommand ShareFolderCommand { get; }

    public RelayCommand StopSharingCommand { get; }

    private bool HasActivePhoneTransfers => _node.Transfers.Sessions.Any(s => s.PeerId == PhoneLinkServer.PhonePeerId && !s.IsFinished && s.State != TransferState.WaitingForAcceptance);

    /// <summary>The page became visible: show the app's code and, where possible, open this computer's own Wi-Fi.</summary>
    internal void OnShown()
    {
        _shown = true;
        Refresh();
        if (!_browserMode) _ = StartHostingAsync();
    }

    /// <summary>The page was left: this computer's own Wi-Fi stops, unless a phone connected over it.</summary>
    internal void OnHidden()
    {
        _shown = false;
        StopHosting();
    }

    /// <summary>Shares paths with the phone's browser (the page's buttons and drag and drop).</summary>
    public async Task ShareAsync(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;
        if (!_browserMode) ShowBrowser();
        if (!IsOn) return;
        try
        {
            _node.PhoneLink.Share(paths);
            Problem = "";
        }
        catch (TransferException ex)
        {
            Problem = ex.Error.Message;
        }

        await Task.CompletedTask;
        Refresh();
    }

    private async Task StartHostingAsync()
    {
        if (_hosting || _hostingStarting || !_node.Direct.CanHost) return;
        _hostingStarting = true;
        Refresh();
        try
        {
            await _node.Direct.HostForBrowserAsync(CancellationToken.None);
            _hosting = true;
            if (!_shown || _browserMode) StopHosting(); // left meanwhile
        }
        catch (Exception ex)
        {
            // The code still pairs over the shared Wi-Fi.
            Log.Warn("This computer's own Wi-Fi for the phone didn't start", ex);
        }
        finally
        {
            _hostingStarting = false;
            Refresh();
        }
    }

    private void StopHosting()
    {
        if (!_hosting) return;
        _hosting = false;
        _node.Direct.StopForBrowser();
        Refresh();
    }

    private void ShowBrowser()
    {
        _browserMode = true;
        StopHosting();
        try
        {
            if (!IsOn) _node.PhoneLink.Start();
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

    private async Task ShowAppCodeAsync()
    {
        _browserMode = false;
        if (!HasActivePhoneTransfers && !IsSharing) await StopBrowserLinkAsync();
        Problem = "";
        Refresh();
        if (_shown) _ = StartHostingAsync();
    }

    private async Task StopBrowserLinkAsync()
    {
        _idleTimer.Stop();
        if (IsOn) await _node.PhoneLink.StopAsync();
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
        // The app's code: who this computer is, where, and (once it's on) its own Wi-Fi to join right away.
        var direct = _hosting;
        if (AppQr == null || direct != _appQrConnectsDirectly)
        {
            _appQrConnectsDirectly = direct;
            AppQr = QrCode.Create(_node.CreatePairingCode(connectDirectly: direct).AppendTo(null)).Geometry;
        }

        if (IsOn)
        {
            var links = _node.PhoneLink.GetLinks(_node.GetLocalAddresses())
                .Where(l => !(System.Net.IPAddress.TryParse(new Uri(l).Host, out var ip) && _node.Transfers.IsDirectAddress(ip))).ToList();
            var link = links.FirstOrDefault() ?? "";
            if (link != Link)
            {
                Link = link;
                CopyStatus = "";
                // The phone's camera opens the link; the Beam app's scanner also reads the pairing part after "#beam=".
                Qr = link.Length > 0 ? QrCode.Create(_node.CreatePairingCode().AppendTo(link)).Geometry : null;
            }

            if (link.Length == 0 && _browserMode) Problem = L.T("This computer isn't connected to a network. Connect to Wi-Fi, then try again.");
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
                     nameof(IsOn), nameof(ShowsAppCode), nameof(ShowsBrowserCode), nameof(AppCodeConnectsDirectly), nameof(AppCodeText),
                     nameof(IsHostingStarting), nameof(OtherLinks), nameof(HasOtherLinks), nameof(ConnectionText), nameof(IsConnected),
                     nameof(SharedNames), nameof(IsSharing), nameof(SharedSummary),
                 })
        {
            OnPropertyChanged(name);
        }
    }
}
