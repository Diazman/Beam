using System.Collections.ObjectModel;
using Avalonia.Threading;
using Beam.App.Infrastructure;
using Beam.App.Platform;
using Beam.App.Services;
using Beam.App.ViewModels.Dialogs;
using Beam.Core;
using Beam.Core.Diagnostics;
using Beam.Core.Discovery;
using Beam.Core.Licensing;
using Beam.Core.Settings;
using Beam.Core.Transfer;

namespace Beam.App.ViewModels;

public enum Page
{
    Home,
    Phone,
    History,
    Settings,
}

/// <summary>
/// Root view model: navigation, the transfers panel, dialogs, notifications, and the bridge
/// that lets the transfer engine ask the user about incoming files.
/// </summary>
public sealed class MainViewModel : ObservableObject, IIncomingTransferHandler
{
    private readonly IPlatformServices _platform;
    private readonly IUiServices _ui;
    private readonly List<DialogViewModel> _dialogQueue = new();
    private readonly Dictionary<string, string> _acceptedFolders = new();
    private readonly Dictionary<string, DateTime> _deviceNotifiedAt = new();
    private readonly DispatcherTimer _progressTimer;
    private readonly DateTime _startedAt = DateTime.UtcNow;
    private Page _page = Page.Home;
    private DialogViewModel? _dialog;
    private bool _isDragOver;

    public MainViewModel(BeamNode node, IPlatformServices platform, IUiServices ui, IStoreService? store = null)
    {
        Node = node;
        _platform = platform;
        _ui = ui;
        Pro = new ProService(node, store ?? new UnavailableStoreService());
        node.IncomingHandler = this;

        Home = new HomeViewModel(node, ui, this);
        Phone = new PhoneViewModel(node, ui, this);
        History = new HistoryViewModel(node.History, platform, this);
        Settings = new SettingsViewModel(node, platform, ui, this);

        ShowHomeCommand = new RelayCommand(() => Navigate(Page.Home));
        ShowHistoryCommand = new RelayCommand(() => Navigate(Page.History));
        ShowSettingsCommand = new RelayCommand(() => Navigate(Page.Settings));
        ClearFinishedCommand = new RelayCommand(ClearFinished);
        DismissDialogCommand = new RelayCommand(() => Dialog?.Dismiss());

        _progressTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => RefreshTransfers());

        foreach (var session in node.Transfers.Sessions) AddTransfer(session);
        node.Transfers.SessionStarted += s => Dispatcher.UIThread.Post(() => AddTransfer(s));
        node.Edition.Changed += () => Dispatcher.UIThread.Post(OnEditionChanged);
        node.Quota.Changed += () => Dispatcher.UIThread.Post(Home.OnEditionChanged);
    }

    public BeamNode Node { get; }

    public ProService Pro { get; }

    public bool IsPro => Node.Edition.IsPro;

    public HomeViewModel Home { get; }

    public PhoneViewModel Phone { get; }

    public HistoryViewModel History { get; }

    public SettingsViewModel Settings { get; }

    public object CurrentPage => _page switch
    {
        Page.Phone => Phone,
        Page.History => History,
        Page.Settings => Settings,
        _ => Home,
    };

    public Page CurrentPageKind => _page;

    public bool IsHomePage
    {
        get => _page == Page.Home;
        set
        {
            if (value) Navigate(Page.Home);
        }
    }

    public bool IsPhonePage
    {
        get => _page == Page.Phone;
        set
        {
            if (value) Navigate(Page.Phone);
        }
    }

    public bool IsHistoryPage
    {
        get => _page == Page.History;
        set
        {
            if (value) Navigate(Page.History);
        }
    }

    public bool IsSettingsPage
    {
        get => _page == Page.Settings;
        set
        {
            if (value) Navigate(Page.Settings);
        }
    }

    public ObservableCollection<TransferViewModel> Transfers { get; } = new();

    public bool HasTransfers => Transfers.Count > 0;

    public bool HasFinishedTransfers => Transfers.Any(t => t.IsFinished);

    public int ActiveTransferCount => Transfers.Count(t => t.IsActive);

    public bool HasActiveTransfers => ActiveTransferCount > 0;

    public DialogViewModel? Dialog
    {
        get => _dialog;
        private set
        {
            if (SetProperty(ref _dialog, value)) OnPropertyChanged(nameof(HasDialog));
        }
    }

    public bool HasDialog => Dialog != null;

    public bool IsDragOver
    {
        get => _isDragOver;
        set => SetProperty(ref _isDragOver, value);
    }

    public RelayCommand ShowHomeCommand { get; }

    public RelayCommand ShowHistoryCommand { get; }

    public RelayCommand ShowSettingsCommand { get; }

    public RelayCommand ClearFinishedCommand { get; }

    public RelayCommand DismissDialogCommand { get; }

    /// <summary>Shows the first-run screen if needed, then starts networking.</summary>
    public async Task InitializeAsync(CommandLine commandLine)
    {
        if (_platform.SupportsStartWithSystem && Node.Settings.Current.StartWithWindows != _platform.GetStartWithSystem())
            Node.Settings.Update(s => s.StartWithWindows = _platform.GetStartWithSystem());

        if (!Node.Settings.Current.FirstRunCompleted)
        {
            var name = await ShowDialogAsync(new WelcomeViewModel(Node.Settings.Current.DeviceName, OperatingSystem.IsWindows())) as string;
            Node.Settings.Update(s =>
            {
                s.FirstRunCompleted = true;
                if (!string.IsNullOrWhiteSpace(name)) s.DeviceName = name;
            });
            Settings.DeviceName = Node.Settings.Current.DeviceName;
        }

        try
        {
            Node.Start();
        }
        catch (Exception ex)
        {
            Log.Error("Networking failed to start", ex);
            await ShowDialogAsync(new ConfirmViewModel(
                "Beam can't use the network",
                "Another program may be blocking Beam's network port. Restart your computer and try again. You can still look at your transfer history.",
                "OK", ""));
        }

        HandleCommandLine(commandLine);
        await Pro.RefreshAsync();
    }

    /// <summary>Shows what Pro offers and lets the user buy it. True when the user has Pro afterwards.</summary>
    public async Task<bool> ShowUpgradeAsync(string? reason = null)
    {
        if (IsPro) return true;
        await Pro.RefreshAsync();
        if (IsPro) return true;
        return await ShowDialogAsync(new UpgradeViewModel(Pro, _platform, reason)) is true && IsPro;
    }

    public void HandleCommandLine(CommandLine commandLine)
    {
        if (commandLine.SendPaths.Count == 0) return;
        Navigate(Page.Home);
        Home.AddPaths(commandLine.SendPaths);
    }

    public void Navigate(Page page)
    {
        if (_page == Page.Settings && page != Page.Settings) Settings.SaveName();
        if (_page == page) return;
        _page = page;
        OnPropertyChanged(nameof(CurrentPage));
        OnPropertyChanged(nameof(CurrentPageKind));
        OnPropertyChanged(nameof(IsHomePage));
        OnPropertyChanged(nameof(IsPhonePage));
        OnPropertyChanged(nameof(IsHistoryPage));
        OnPropertyChanged(nameof(IsSettingsPage));
    }

    /// <summary>Starts sending to one device. Returns false (after telling the user why) if it couldn't start.</summary>
    public async Task<bool> StartSendAsync(DeviceInfo device, IReadOnlyList<string> paths)
    {
        try
        {
            Node.Send(device, paths);
            return true;
        }
        catch (TransferException ex) when (ex.Error.Kind == TransferErrorKind.SendLimitReached)
        {
            if (!await ShowUpgradeAsync(ex.Error.Message)) return false;
            return await StartSendAsync(device, paths);
        }
        catch (Exception ex)
        {
            Log.Error("Could not start transfer", ex);
            return false;
        }
    }

    public async Task<DeviceInfo?> ConnectByAddressAsync() =>
        await ShowDialogAsync(new AddDeviceViewModel(Node)) as DeviceInfo;

    /// <summary>Queues a dialog and waits for its result. Cancelling the token closes it (throws <see cref="OperationCanceledException"/>).</summary>
    public async Task<object?> ShowDialogAsync(DialogViewModel dialog, CancellationToken cancellationToken = default)
    {
        Dispatcher.UIThread.VerifyAccess();
        _dialogQueue.Add(dialog);
        if (Dialog == null) Dialog = _dialogQueue[0];
        using var registration = cancellationToken.Register(() => Dispatcher.UIThread.Post(dialog.Abort));
        try
        {
            return await dialog.Completion;
        }
        finally
        {
            _dialogQueue.Remove(dialog);
            if (Dialog == dialog) Dialog = _dialogQueue.FirstOrDefault();
        }
    }

    /// <summary>Asks before quitting while transfers are running. True means go ahead.</summary>
    public async Task<bool> ConfirmQuitAsync()
    {
        var active = ActiveTransferCount;
        if (active == 0) return true;
        var result = await ShowDialogAsync(new ConfirmViewModel(
            active == 1 ? "A transfer is still in progress" : $"{active} transfers are still in progress",
            "If you quit Beam now, the transfer will stop and unfinished files won't be saved.",
            "Stop and quit",
            "Keep transferring",
            isDestructive: true));
        return result is true;
    }

    /// <summary>The window was closed but Beam keeps running in the notification area: say so once.</summary>
    public void OnWindowHiddenToTray()
    {
        if (Node.Settings.Current.TrayHintShown) return;
        Node.Settings.Update(s => s.TrayHintShown = true);
        _platform.ShowNotification(
            "Beam is still running",
            "Nearby computers can still send you files. To quit, right-click the Beam icon in the notification area.",
            () => Dispatcher.UIThread.Post(_ui.BringToFront));
    }

    public void OnDeviceAppeared(DeviceInfo device)
    {
        // Ignore the burst of devices found right after startup.
        if (DateTime.UtcNow - _startedAt < TimeSpan.FromSeconds(15)) return;
        if (_deviceNotifiedAt.TryGetValue(device.Id, out var last) && DateTime.UtcNow - last < TimeSpan.FromMinutes(30)) return;
        _deviceNotifiedAt[device.Id] = DateTime.UtcNow;
        Notify(s => s.NotifyOnDeviceFound, $"{device.Name} is nearby", "Open Beam to send files to it.");
    }

    private void OnEditionChanged()
    {
        OnPropertyChanged(nameof(IsPro));
        Home.OnEditionChanged();
        Settings.OnEditionChanged();
    }

    // ----- IIncomingTransferHandler (called by the engine on background threads) -----

    public Task<IncomingDecision> RequestApprovalAsync(IncomingRequest request, TransferSession session, CancellationToken cancellationToken) =>
        Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var canTrust = Node.Edition.IsEnabled(Feature.TrustedDevices) && request.SenderFingerprint.Length > 0;
            var dialog = new IncomingRequestViewModel(request, _ui, canTrust);
            if (!_ui.IsWindowActive)
            {
                Notify(s => s.NotifyOnIncomingRequest, "Incoming files", dialog.Title, bringToFront: false);
                _ui.BringToFront();
            }

            var accepted = await ShowDialogAsync(dialog, cancellationToken) is true;
            if (!accepted) return IncomingDecision.Decline();

            if (dialog.TrustDevice)
            {
                Node.Settings.Update(s =>
                {
                    s.TrustedDevices.RemoveAll(t => t.Fingerprint == request.SenderFingerprint);
                    s.TrustedDevices.Add(new TrustedDevice
                    {
                        Fingerprint = request.SenderFingerprint,
                        DeviceId = request.SenderId,
                        Name = request.SenderName,
                    });
                });
            }

            _acceptedFolders[request.TransferId] = dialog.Folder;
            return IncomingDecision.Accept(dialog.Folder);
        });

    public Task<IReadOnlyList<ConflictAction>> ResolveConflictsAsync(IncomingRequest request, IReadOnlyList<FileConflict> conflicts, CancellationToken cancellationToken) =>
        Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var folder = _acceptedFolders.TryGetValue(request.TransferId, out var f) ? f : request.DefaultFolder;
            var folderName = Path.GetFileName(Path.TrimEndingDirectorySeparator(folder));
            if (string.IsNullOrEmpty(folderName)) folderName = folder;
            var actions = new List<ConflictAction>(conflicts.Count);
            ConflictAction? forAll = null;
            for (var i = 0; i < conflicts.Count; i++)
            {
                if (forAll != null)
                {
                    actions.Add(forAll.Value);
                    continue;
                }

                var choice = (ConflictChoice)(await ShowDialogAsync(new ConflictViewModel(conflicts[i], folderName, conflicts.Count - i - 1), cancellationToken))!;
                actions.Add(choice.Action);
                if (choice.ApplyToAll) forAll = choice.Action;
            }

            return (IReadOnlyList<ConflictAction>)actions;
        });

    // ----- transfers panel -----

    private void AddTransfer(TransferSession session)
    {
        if (Transfers.Any(t => t.Session == session)) return;
        var vm = new TransferViewModel(session, _platform, Dismiss);
        Transfers.Insert(0, vm);
        session.StateChanged += s => Dispatcher.UIThread.Post(() => OnSessionStateChanged(vm));
        OnTransfersChanged();
        _progressTimer.Start();
    }

    private void OnSessionStateChanged(TransferViewModel vm)
    {
        vm.Refresh();
        var session = vm.Session;
        if (!Transfers.Contains(vm)) return;

        // A request the user declined here doesn't need a card.
        if (session.Direction == TransferDirection.Receive && vm.State == TransferState.Declined
            && session.Error?.Kind == TransferErrorKind.Declined)
        {
            Dismiss(vm);
            return;
        }

        if (vm.IsFinished) AnnounceFinished(vm);
        OnTransfersChanged();
        if (!vm.IsFinished) _progressTimer.Start();
    }

    private void RefreshTransfers()
    {
        var anyActive = false;
        foreach (var transfer in Transfers)
        {
            if (transfer.IsFinished) continue;
            transfer.Refresh();
            anyActive |= !transfer.IsFinished;
        }

        if (!anyActive) _progressTimer.Stop();
        OnPropertyChanged(nameof(ActiveTransferCount));
        OnPropertyChanged(nameof(HasActiveTransfers));
    }

    private void Dismiss(TransferViewModel vm)
    {
        if (!vm.IsFinished) return;
        Transfers.Remove(vm);
        Node.Transfers.Forget(vm.Session);
        OnTransfersChanged();
    }

    private void ClearFinished()
    {
        foreach (var vm in Transfers.Where(t => t.IsFinished).ToList()) Dismiss(vm);
    }

    private void OnTransfersChanged()
    {
        OnPropertyChanged(nameof(HasTransfers));
        OnPropertyChanged(nameof(HasFinishedTransfers));
        OnPropertyChanged(nameof(ActiveTransferCount));
        OnPropertyChanged(nameof(HasActiveTransfers));
    }

    private void AnnounceFinished(TransferViewModel vm)
    {
        var session = vm.Session;
        var snapshot = session.GetSnapshot();
        var title = session.Title;
        switch (snapshot.State)
        {
            case TransferState.Completed when session.Direction == TransferDirection.Receive:
                var folder = Path.GetFileName(Path.TrimEndingDirectorySeparator(session.DestinationFolder ?? ""));
                Notify(s => s.NotifyOnTransferFinished, "Files received",
                    $"{title} from {session.PeerName} was saved to {folder}.", () => vm.OpenCommand.Execute(null));
                break;
            case TransferState.Completed:
                Notify(s => s.NotifyOnTransferFinished, "Files sent", $"{title} was sent to {session.PeerName}.");
                break;
            case TransferState.CompletedWithErrors:
                Notify(s => s.NotifyOnTransferFinished, "Transfer finished with problems",
                    $"{snapshot.FailedFiles} of {snapshot.TotalFiles} files couldn't be transferred.");
                break;
            case TransferState.Failed:
                Notify(s => s.NotifyOnTransferFinished, "Transfer failed", snapshot.Error?.Message ?? "The transfer could not be completed.");
                break;
            case TransferState.Declined when session.Direction == TransferDirection.Send:
            case TransferState.Cancelled when snapshot.Error?.Kind == TransferErrorKind.CancelledByRemote:
                Notify(s => s.NotifyOnTransferFinished, "Transfer stopped", snapshot.Error?.Message ?? "The transfer was stopped.");
                break;
        }
    }

    private void Notify(Func<AppSettings, bool> preference, string title, string message, Action? onClick = null, bool bringToFront = true)
    {
        var settings = Node.Settings.Current;
        if (!settings.NotificationsEnabled || !preference(settings) || _ui.IsWindowActive) return;
        _platform.ShowNotification(title, message, () => Dispatcher.UIThread.Post(() =>
        {
            if (bringToFront) _ui.BringToFront();
            onClick?.Invoke();
        }));
    }
}
