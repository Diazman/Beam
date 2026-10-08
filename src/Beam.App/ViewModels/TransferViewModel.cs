using System.Text;
using Avalonia.Media;
using Beam.App.Infrastructure;
using Beam.App.Platform;
using Beam.App.Services;
using Beam.Core.Localization;
using Beam.Core.Transfer;
using Beam.Core.Util;

namespace Beam.App.ViewModels;

/// <summary>One card in the Transfers panel. Refreshed a few times per second from the engine's snapshot.</summary>
public sealed class TransferViewModel : ObservableObject
{
    private readonly IPlatformServices _platform;
    private readonly Action<TransferViewModel> _dismiss;
    private readonly SpeedMeter _speed = new();
    private TransferSnapshot? _last;
    private bool _showDetails;
    private string _statusText = "";
    private string _sizeText = "";
    private string _speedText = "";
    private string _etaText = "";
    private string _currentFileText = "";
    private double _progress;

    public TransferViewModel(TransferSession session, IPlatformServices platform, Action<TransferViewModel> dismiss, Func<string, Task>? copy = null)
    {
        Session = session;
        CopyTextCommand = new AsyncCommand(async () =>
        {
            if (Session.Text != null && copy != null) await copy(Session.Text);
        });
        _platform = platform;
        _dismiss = dismiss;
        CancelCommand = new RelayCommand(Session.Cancel, () => Session.CanCancel);
        ResumeCommand = new RelayCommand(Session.Resume, () => Session.CanResume);
        DismissCommand = new RelayCommand(() => _dismiss(this));
        OpenCommand = new RelayCommand(Open, () => CanOpen);
        ShowInFolderCommand = new RelayCommand(ShowInFolder, () => CanOpen);
        ToggleDetailsCommand = new RelayCommand(() => ShowDetails = !ShowDetails);
        Refresh();
    }

    public TransferSession Session { get; }

    public bool IsSend => Session.Direction == TransferDirection.Send;

    public string Heading => IsSend ? L.T("To {0}", Session.PeerName) : L.T("From {0}", Session.PeerName);

    public string ItemTitle => string.IsNullOrEmpty(Session.Title) ? L.T("Files") : Session.Title;

    public Geometry? Icon => Icons.Get(IsSend ? Icons.Send : Icons.Receive);

    public TransferState State => _last?.State ?? Session.State;

    public bool IsFinished => TransferSession.IsFinalState(State);

    public bool IsActive => !IsFinished && State != TransferState.Interrupted;

    public bool IsSuccess => State == TransferState.Completed;

    public bool IsWarning => State is TransferState.CompletedWithErrors or TransferState.Interrupted or TransferState.Reconnecting;

    public bool IsError => State == TransferState.Failed;

    public bool IsNeutral => State is TransferState.Cancelled or TransferState.Declined;

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public double Progress
    {
        get => _progress;
        private set => SetProperty(ref _progress, value);
    }

    public bool IsIndeterminate => State is TransferState.Preparing or TransferState.Connecting or TransferState.WaitingForAcceptance
        or TransferState.AwaitingDecision or TransferState.Reconnecting;

    public bool ShowProgress => !IsFinished;

    public string SizeText
    {
        get => _sizeText;
        private set => SetProperty(ref _sizeText, value);
    }

    public string SpeedText
    {
        get => _speedText;
        private set => SetProperty(ref _speedText, value);
    }

    public string EtaText
    {
        get => _etaText;
        private set => SetProperty(ref _etaText, value);
    }

    public string CurrentFileText
    {
        get => _currentFileText;
        private set => SetProperty(ref _currentFileText, value);
    }

    public bool ShowStats => State == TransferState.Transferring || (State == TransferState.Interrupted && SizeText.Length > 0);

    public bool HasCurrentFile => State == TransferState.Transferring && CurrentFileText.Length > 0;

    public bool HasEta => State == TransferState.Transferring && EtaText.Length > 0;

    public bool CanOpen => !IsSend && IsFinished && Session.SavedRootPaths.Any(p => File.Exists(p) || Directory.Exists(p));

    /// <summary>A single received file can be opened directly; otherwise the folder is shown.</summary>
    public bool CanOpenFile => CanOpen && Session.SavedRootPaths.Count == 1 && File.Exists(Session.SavedRootPaths[0]);

    public bool CanResume => Session.CanResume;

    public bool CanCancel => Session.CanCancel;

    public string CancelText => State == TransferState.Interrupted ? L.T("Discard") : L.T("Cancel");

    public bool HasDetails => DetailsText.Length > 0;

    public string DetailsText { get; private set; } = "";

    public bool ShowDetails
    {
        get => _showDetails;
        set
        {
            if (SetProperty(ref _showDetails, value)) OnPropertyChanged(nameof(DetailsToggleText));
        }
    }

    public string DetailsToggleText => ShowDetails ? L.T("Hide details") : L.T("Details");

    public RelayCommand CancelCommand { get; }

    public RelayCommand ResumeCommand { get; }

    public RelayCommand DismissCommand { get; }

    public RelayCommand OpenCommand { get; }

    public RelayCommand ShowInFolderCommand { get; }

    public RelayCommand ToggleDetailsCommand { get; }

    /// <summary>Text transfers: copy the text again.</summary>
    public AsyncCommand CopyTextCommand { get; }

    /// <summary>The finished transfer was already announced (notification, counters).</summary>
    internal bool Announced { get; set; }

    public bool CanCopyText => Session.IsText && State == TransferState.Completed;

    /// <summary>Pulls the latest numbers from the engine. Called on the UI thread.</summary>
    public void Refresh()
    {
        var snapshot = Session.GetSnapshot();
        var stateChanged = _last == null || _last.State != snapshot.State || !Equals(_last.Error, snapshot.Error)
                           || _last.FailedFiles != snapshot.FailedFiles || _last.CompletedFiles != snapshot.CompletedFiles;
        _last = snapshot;

        if (snapshot.State == TransferState.Transferring) _speed.Sample(snapshot.TransferredBytes);
        else _speed.Reset();

        Progress = snapshot.TotalBytes > 0
            ? Math.Clamp(snapshot.TransferredBytes * 100.0 / snapshot.TotalBytes, 0, 100)
            : snapshot.TotalFiles > 0 ? snapshot.CompletedFiles * 100.0 / snapshot.TotalFiles : 0;
        SizeText = snapshot.TotalBytes > 0 ? L.T("{0} of {1}", Format.Bytes(snapshot.TransferredBytes), Format.Bytes(snapshot.TotalBytes)) : "";
        SpeedText = _speed.BytesPerSecond > 0 ? Format.Speed(_speed.BytesPerSecond) : "";
        var eta = _speed.EstimateRemaining(Math.Max(0, snapshot.TotalBytes - snapshot.TransferredBytes));
        EtaText = eta == null ? "" : Format.Remaining(eta.Value);
        CurrentFileText = snapshot.CurrentFile == null ? "" : Path.GetFileName(snapshot.CurrentFile);
        StatusText = BuildStatus(snapshot);

        OnPropertyChanged(nameof(ShowStats));
        OnPropertyChanged(nameof(HasCurrentFile));
        OnPropertyChanged(nameof(HasEta));

        if (!stateChanged) return;
        DetailsText = BuildDetails(snapshot);
        foreach (var name in new[]
                 {
                     nameof(State), nameof(IsFinished), nameof(IsActive), nameof(IsSuccess), nameof(IsWarning), nameof(IsError),
                     nameof(IsNeutral), nameof(IsIndeterminate), nameof(ShowProgress), nameof(CanOpen), nameof(CanOpenFile), nameof(CanCopyText),
                     nameof(CanResume), nameof(CanCancel), nameof(CancelText), nameof(HasDetails), nameof(DetailsText), nameof(ItemTitle),
                 })
        {
            OnPropertyChanged(name);
        }

        CancelCommand.RaiseCanExecuteChanged();
        ResumeCommand.RaiseCanExecuteChanged();
        OpenCommand.RaiseCanExecuteChanged();
        ShowInFolderCommand.RaiseCanExecuteChanged();
    }

    private string BuildStatus(TransferSnapshot s)
    {
        var peer = Session.PeerName;
        switch (s.State)
        {
            case TransferState.Preparing:
                return L.T("Preparing files…");
            case TransferState.Connecting:
                return L.T("Connecting to {0}…", peer);
            case TransferState.WaitingForAcceptance when Session.PeerId == Beam.Core.Phone.PhoneLinkServer.PhonePeerId:
                return L.T("Ready to download on your phone");
            case TransferState.WaitingForAcceptance:
                return L.T("Waiting for {0} to accept…", peer);
            case TransferState.AwaitingDecision:
                return L.T("Waiting for your answer…");
            case TransferState.Reconnecting:
                return L.T("Connection lost. Reconnecting…");
            case TransferState.Interrupted:
                return s.Error?.Message ?? L.T("Connection lost. Waiting for {0} to reconnect.", peer);
            case TransferState.Transferring:
                if (s.TotalFiles <= 1) return IsSend ? L.T("Sending") : L.T("Receiving");
                var current = Math.Min(s.TotalFiles, s.CompletedFiles + s.FailedFiles + 1);
                return L.Plural(s.TotalFiles, "Transferring {1:N0} of {0} file", "Transferring {1:N0} of {0} files", current);
            case TransferState.Completed when Session.IsText:
                return IsSend ? L.T("Text sent") : L.T("Text received");
            case TransferState.Completed:
                var done = IsSend
                    ? L.Plural(s.CompletedFiles, "Sent {0} file", "Sent {0} files")
                    : L.Plural(s.CompletedFiles, "Received {0} file", "Received {0} files");
                if (s.CompletedFiles == 0 && s.TotalFiles == 0) done = IsSend ? L.T("Sent") : L.T("Received");
                return s.SkippedFiles > 0 ? L.T("{0} · {1:N0} skipped", done, s.SkippedFiles) : done;
            case TransferState.CompletedWithErrors:
                return L.Plural(s.FailedFiles, "Finished, but {0} file couldn't be transferred", "Finished, but {0} files couldn't be transferred");
            default:
                return s.Error?.Message ?? s.State.ToString();
        }
    }

    private string BuildDetails(TransferSnapshot s)
    {
        var builder = new StringBuilder();
        var failures = Session.Failures;
        if (failures.Count > 0)
        {
            builder.AppendLine(L.T("Files with problems:"));
            foreach (var failure in failures.Take(20)) builder.AppendLine($"• {failure.RelativePath}: {failure.Reason}");
            if (failures.Count > 20) builder.AppendLine(L.T("…and {0} more", failures.Count - 20));
        }

        if (s.Error?.Details is { Length: > 0 } details && s.Error.Kind != TransferErrorKind.CancelledByUser)
        {
            if (builder.Length > 0) builder.AppendLine();
            builder.AppendLine(L.T("Technical details:"));

            builder.Append(details.Length > 1500 ? details[..1500] + "…" : details);
        }

        return builder.ToString().TrimEnd();
    }

    private void Open()
    {
        if (CanOpenFile) _platform.OpenFile(Session.SavedRootPaths[0]);
        else ShowInFolder();
    }

    private void ShowInFolder()
    {
        var first = Session.SavedRootPaths.FirstOrDefault(p => File.Exists(p) || Directory.Exists(p));
        if (first != null) _platform.RevealInFolder(first);
        else if (Session.DestinationFolder != null) _platform.OpenFolder(Session.DestinationFolder);
    }
}
