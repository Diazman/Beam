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
/// The Phone page: for phones without the Beam app. A code opens Beam's page in the phone's browser over the shared
/// Wi-Fi. (Phones with the app use Send and Receive, which find each other by themselves, even without Wi-Fi.)
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
    private bool _browserMode = true; // the Beam app uses Send and Receive; this page is for phones' browsers
    private string _link = "";
    private string _problem = "";
    private string _copyStatus = "";

    public PhoneViewModel(BeamNode node, IUiServices ui, MainViewModel main)
    {
        _node = node;
        _ui = ui;
        _main = main;
        ShowBrowserCommand = new RelayCommand(ShowBrowser);
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

    /// <summary>Showing the code that opens Beam's page in a phone's browser.</summary>
    public bool ShowsBrowserCode => _browserMode;

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

    public AsyncCommand CopyLinkCommand { get; }

    public AsyncCommand ShareFilesCommand { get; }

    public AsyncCommand ShareFolderCommand { get; }

    public RelayCommand StopSharingCommand { get; }

    private bool HasActivePhoneTransfers => _node.Transfers.Sessions.Any(s => s.PeerId == PhoneLinkServer.PhonePeerId && !s.IsFinished && s.State != TransferState.WaitingForAcceptance);

    /// <summary>The page became visible: Beam's page for phone browsers starts.</summary>
    internal void OnShown()
    {
        if (!IsOn) ShowBrowser();
        Refresh();
    }

    internal void OnHidden()
    {
        // The page keeps running for the phone (it stops after a while without activity).
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

    private void ShowBrowser()
    {
        _browserMode = true;
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
        if (IsOn)
        {
            var links = _node.PhoneLink.GetLinks(_node.GetLocalAddresses())
                .Where(l => !(System.Net.IPAddress.TryParse(new Uri(l).Host, out var ip) && _node.Transfers.IsDirectAddress(ip))).ToList();
            var link = links.FirstOrDefault() ?? "";
            if (link != Link)
            {
                Link = link;
                CopyStatus = "";
                // Just the link: short codes scan with any camera app (some, e.g. Xiaomi's, gave up on the longer code
                // that also carried the app's pairing details). The app has its own code.
                Qr = link.Length > 0 ? QrCode.Create(link).Geometry : null;
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
                     nameof(IsOn), nameof(ShowsBrowserCode), nameof(OtherLinks), nameof(HasOtherLinks), nameof(ConnectionText), nameof(IsConnected),
                     nameof(SharedNames), nameof(IsSharing), nameof(SharedSummary),
                 })
        {
            OnPropertyChanged(name);
        }
    }
}
