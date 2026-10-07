using System.Text;
using Avalonia.Media;
using Beam.App.Infrastructure;
using Beam.App.Platform;
using Beam.App.Services;
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

    public TransferViewModel(TransferSession session, IPlatformServices platform, Action<TransferViewModel> dismiss)
    {
        Session = session;
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

    public string Heading => IsSend ? $"To {Session.PeerName}" : $"From {Session.PeerName}";

    public string ItemTitle => string.IsNullOrEmpty(Session.Title) ? "Files" : Session.Title;

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

    public string CancelText => State == TransferState.Interrupted ? "Discard" : "Cancel";

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

    public string DetailsToggleText => ShowDetails ? "Hide details" : "Details";

    public RelayCommand CancelCommand { get; }

    public RelayCommand ResumeCommand { get; }

    public RelayCommand DismissCommand { get; }

    public RelayCommand OpenCommand { get; }

    public RelayCommand ShowInFolderCommand { get; }

    public RelayCommand ToggleDetailsCommand { get; }

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
        SizeText = snapshot.TotalBytes > 0 ? $"{Format.Bytes(snapshot.TransferredBytes)} of {Format.Bytes(snapshot.TotalBytes)}" : "";
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
                     nameof(IsNeutral), nameof(IsIndeterminate), nameof(ShowProgress), nameof(CanOpen), nameof(CanOpenFile),
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
                return "Preparing files…";
            case TransferState.Connecting:
                return $"Connecting to {peer}…";
            case TransferState.WaitingForAcceptance:
                return $"Waiting for {peer} to accept…";
            case TransferState.AwaitingDecision:
                return "Waiting for your answer…";
            case TransferState.Reconnecting:
                return "Connection lost. Reconnecting…";
            case TransferState.Interrupted:
                return s.Error?.Message ?? $"Connection lost. Waiting for {peer} to reconnect.";
            case TransferState.Transferring:
                if (s.TotalFiles <= 1) return IsSend ? "Sending" : "Receiving";
                var current = Math.Min(s.TotalFiles, s.CompletedFiles + s.FailedFiles + 1);
                return $"Transferring {current:N0} of {s.TotalFiles:N0} files";
            case TransferState.Completed:
                var done = IsSend ? $"Sent {Format.Count(s.CompletedFiles, "file")}" : $"Received {Format.Count(s.CompletedFiles, "file")}";
                if (s.CompletedFiles == 0 && s.TotalFiles == 0) done = IsSend ? "Sent" : "Received";
                return s.SkippedFiles > 0 ? $"{done} · {s.SkippedFiles:N0} skipped" : done;
            case TransferState.CompletedWithErrors:
                return $"Finished, but {Format.Count(s.FailedFiles, "file")} couldn't be transferred";
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
            builder.AppendLine("Files with problems:");
            foreach (var failure in failures.Take(20)) builder.AppendLine($"• {failure.RelativePath}: {failure.Reason}");
            if (failures.Count > 20) builder.AppendLine($"…and {failures.Count - 20} more");
        }

        if (s.Error?.Details is { Length: > 0 } details && s.Error.Kind != TransferErrorKind.CancelledByUser)
        {
            if (builder.Length > 0) builder.AppendLine();
            builder.AppendLine("Technical details:");
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
