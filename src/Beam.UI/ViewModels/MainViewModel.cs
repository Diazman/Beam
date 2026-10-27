using System.Collections.ObjectModel;
using Avalonia.Threading;
using Beam.App.Infrastructure;
using Beam.App.Platform;
using Beam.App.Services;
using Beam.App.ViewModels.Dialogs;
using Beam.Core;
using Beam.Core.Diagnostics;
using Beam.Core.Direct;
using Beam.Core.Discovery;
using Beam.Core.Localization;
using Beam.Core.Settings;
using Beam.Core.Transfer;

namespace Beam.App.ViewModels;

public enum Page
{
    Home,
    Receive,
    Phone,
    History,
    Settings,

    /// <summary>Phones only: the list of transfers (the desktop shows it beside the current page).</summary>
    Transfers,
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
    private TransfersPage? _transfersPage;
    private DialogViewModel? _dialog;
    private bool _isDragOver;

    public MainViewModel(BeamNode node, IPlatformServices platform, IUiServices ui, IStoreService? store = null)
    {
        Node = node;
        _platform = platform;
        _ui = ui;
        Rating = new RatingService(node, store ?? new UnavailableStoreService());
        node.IncomingHandler = this;

        Home = new HomeViewModel(node, ui, this);
        Phone = new PhoneViewModel(node, ui, this);
        Receive = new ReceiveViewModel(node, this);
        History = new HistoryViewModel(node.History, platform, this);
        Settings = new SettingsViewModel(node, platform, ui, this);

        ClearFinishedCommand = new RelayCommand(ClearFinished);
        DismissDialogCommand = new RelayCommand(() => Dialog?.Dismiss());

        _progressTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => RefreshTransfers());

        foreach (var session in node.Transfers.Sessions) AddTransfer(session);
        node.Transfers.SessionStarted += s => Dispatcher.UIThread.Post(() => AddTransfer(s));
        node.Direct.BeforeHosting = () => Dispatcher.UIThread.InvokeAsync(EnsureFirewallForDirectAsync);
        node.Direct.Problem += message => Dispatcher.UIThread.Post(() => ShowDirectProblem(message));
    }

    private bool _showingDirectProblem;

    /// <summary>Switching a running transfer to or from a direct link wasn't possible; it carries on as it was.</summary>
    private async void ShowDirectProblem(string message)
    {
        if (_showingDirectProblem) return;
        _showingDirectProblem = true;
        try
        {
            await ShowDialogAsync(new ConfirmViewModel(L.T("Couldn't connect directly"),
                message + "\n\n" + L.T("The transfer continues the way it was going."), L.T("OK"), ""));
        }
        catch (Exception ex)
        {
            Log.Warn("Showing a direct connection problem failed", ex); // an async void that throws would end the app
        }
        finally
        {
            _showingDirectProblem = false;
        }
    }

    public BeamNode Node { get; }

    public RatingService Rating { get; }

    /// <summary>Running on a phone or tablet (see <see cref="IPlatformServices.IsPhone"/>).</summary>
    public bool IsPhone => _platform.IsPhone;

    internal IPlatformServices Platform => _platform;

    public HomeViewModel Home { get; }

    public PhoneViewModel Phone { get; }

    public ReceiveViewModel Receive { get; }

    public HistoryViewModel History { get; }

    public SettingsViewModel Settings { get; }

    public object CurrentPage => _page switch
    {
        Page.Phone => Phone,
        Page.Receive => Receive,
        Page.History => History,
        Page.Settings => Settings,
        Page.Transfers => _transfersPage ??= new TransfersPage(this),
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

    public bool IsReceivePage
    {
        get => _page == Page.Receive;
        set
        {
            if (value) Navigate(Page.Receive);
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

    public bool IsTransfersPage
    {
        get => _page == Page.Transfers;
        set
        {
            if (value) Navigate(Page.Transfers);
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

    public RelayCommand ClearFinishedCommand { get; }

    public RelayCommand DismissDialogCommand { get; }

    /// <summary>Shows the first-run screen if needed, then starts networking.</summary>
    public async Task InitializeAsync(CommandLine commandLine)
    {
        if (_page == Page.Home)
        {
            Node.Nearby.SetLooking(true);
            EnsureBluetoothOn();
        }
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
                L.T("Beam can't use the network"),
                L.T("Another program may be blocking Beam's network port. Restart your computer and try again. You can still look at your transfer history."),
                L.T("OK"), ""));
        }

        HandleCommandLine(commandLine);
        Dispatcher.UIThread.InvokeAsync(EnsureFirewallForLocalNetworkAsync).Forget("Checking the firewall");

        // Phones: notifications are how people learn that files are coming. Asked once, with the reason first.
        if (!Node.Settings.Current.NotificationPermissionAsked && !_platform.HasPermission(AppPermission.Notifications))
        {
            Node.Settings.Update(s => s.NotificationPermissionAsked = true);
            await EnsurePermissionAsync(AppPermission.Notifications);
        }
    }

    /// <summary>
    /// Explains why Beam needs a permission, then lets the system ask. Asked when the feature is used, not all at
    /// once when the app opens. True when it is granted.
    /// </summary>
    internal async Task<bool> EnsurePermissionAsync(AppPermission permission)
    {
        if (_platform.HasPermission(permission)) return true;
        var (title, text) = permission switch
        {
            AppPermission.Notifications => (L.T("Get notified about incoming files"),
                L.T("Beam shows a notification when someone sends you files, so you can accept them even when Beam isn't open. The phone will ask you to allow notifications.")),
            _ when _platform.NearbyDevicesIsLocation => (L.T("Allow direct connections"),
                L.T("To connect to a computer directly, even without Wi-Fi, Android needs Beam to have the Location permission. Beam doesn't use or keep your location.")),
            _ => (L.T("Allow direct connections"),
                L.T("To connect to a computer directly, even without Wi-Fi, and to notice paired computers nearby, Android needs the “Nearby devices” permission.")),
        };
        if (await ShowDialogAsync(new ConfirmViewModel(title, text, L.T("Continue"), L.T("Not now"))) is not true) return false;
        return await _platform.RequestPermissionAsync(permission);
    }

    private readonly SemaphoreSlim _firewallGate = new(1, 1);
    private bool _firewallAskedThisRun;

    /// <summary>
    /// At start: Windows sees this PC's network as public and its firewall stops nearby devices from sending here (the
    /// usual first-run prompt only allows private networks). Ask once per run to allow Beam for nearby devices.
    /// </summary>
    internal async Task EnsureFirewallForLocalNetworkAsync()
    {
        await _firewallGate.WaitAsync();
        try
        {
            if (_firewallAskedThisRun || !await _platform.FirewallBlocksLocalNetworkAsync()) return;
            _firewallAskedThisRun = true;
            var allow = await ShowDialogAsync(new ConfirmViewModel(
                L.T("Let nearby devices send to this computer?"),
                L.T("Windows treats this network as public, so its firewall stops phones and computers on it from sending files to this computer. Allow Beam for devices on the same network and on direct connections? Windows will ask for permission once."),
                L.T("Allow"), L.T("Not now")));
            if (allow is true && !await _platform.AllowDirectThroughFirewallAsync())
                await ShowDialogAsync(new ConfirmViewModel(L.T("Nearby devices are still blocked"),
                    L.T("Windows didn't allow the change. Beam will ask again the next time it starts."), L.T("OK"), ""));
        }
        finally
        {
            _firewallGate.Release();
        }
    }

    /// <summary>
    /// Windows Firewall blocks Beam on a PC's own direct network (it counts as public), so phones connected directly
    /// couldn't reach it. Asked right before this PC first starts its network in a run, never at startup.
    /// </summary>
    internal async Task EnsureFirewallForDirectAsync()
    {
        await _firewallGate.WaitAsync();
        try
        {
            if (_firewallAskedThisRun || !Node.Direct.CanHost) return;
            _firewallAskedThisRun = true;
            if (!await _platform.FirewallBlocksDirectAsync()) return;
            var allow = await ShowDialogAsync(new ConfirmViewModel(
                L.T("Allow direct connections?"),
                L.T("Windows Firewall stops phones and computers that connect directly to this computer. Allow Beam for devices connected directly and on the same network? Windows will ask for permission once."),
                L.T("Allow"), L.T("Not now")));
            if (allow is true && !await _platform.AllowDirectThroughFirewallAsync())
                await ShowDialogAsync(new ConfirmViewModel(L.T("Direct connections are still blocked"),
                    L.T("Windows didn't allow the change. Beam will ask again the next time you connect directly."), L.T("OK"), ""));
        }
        finally
        {
            _firewallGate.Release();
        }
    }

    public void HandleCommandLine(CommandLine commandLine)
    {
        if (commandLine.SendPaths.Count == 0 && string.IsNullOrWhiteSpace(commandLine.SendText)) return;
        Navigate(Page.Home);
        Home.AddPaths(commandLine.SendPaths);
        if (!string.IsNullOrWhiteSpace(commandLine.SendText)) Home.SharedText = commandLine.SendText.Trim();
    }

    /// <summary>
    /// The phone's Back button: closes a dialog that can be closed, else goes back to Send. False on Send (the system
    /// then leaves the app as usual).
    /// </summary>
    public bool GoBack()
    {
        if (Dialog is { } dialog)
        {
            dialog.Dismiss();
            return true; // a dialog that must be answered stays, and Back doesn't leave the app under it
        }

        if (_page == Page.Home) return false;
        Navigate(Page.Home);
        return true;
    }

    public void Navigate(Page page)
    {
        if (_page == Page.Settings && page != Page.Settings) Settings.SaveName();
        if (_page == page) return;
        if (_page == Page.Receive && page != Page.Receive) Receive.OnHidden();
        if (page != Page.Home) Node.Nearby.SetLooking(false);
        _page = page;
        if (page == Page.Phone) Phone.OnShown();
        if (page == Page.Receive) Receive.OnShown();
        if (page == Page.Home)
        {
            Node.Nearby.SetLooking(true);
            EnsureBluetoothOn();
        }
        OnPropertyChanged(nameof(CurrentPage));
        OnPropertyChanged(nameof(CurrentPageKind));
        OnPropertyChanged(nameof(IsHomePage));
        OnPropertyChanged(nameof(IsPhonePage));
        OnPropertyChanged(nameof(IsReceivePage));
        OnPropertyChanged(nameof(IsHistoryPage));
        OnPropertyChanged(nameof(IsSettingsPage));
        OnPropertyChanged(nameof(IsTransfersPage));
    }

    /// <summary>
    /// The sending mode chosen on Send: Direct (this device starts a direct link when Send is pressed and sends only over
    /// it) or the Wi-Fi network. Direct only where this device can start one.
    /// </summary>
    public ConnectionMethod SendMode => Node.Settings.Current.Connection == ConnectionMethod.Direct && Node.Direct.CanHost
        ? ConnectionMethod.Direct
        : ConnectionMethod.SameNetwork;

    /// <summary>Starts sending to one device in the chosen mode. Returns false (after telling the user why) if it couldn't start.</summary>
    public async Task<bool> StartSendAsync(DeviceInfo device, IReadOnlyList<string> paths)
    {
        if (await ResolveAsync(device) is not { } target) return false;
        device = target;
        try
        {
            Node.Send(device, paths, SendMode);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Could not start transfer", ex);
            await ShowDialogAsync(new ConfirmViewModel(L.T("Couldn't send to {0}", device.Name),
                ex is TransferException te ? te.Error.Message : L.T("Something went wrong while starting the transfer. Try again; if it keeps happening, send the log from Settings."),
                L.T("OK"), ""));
            return false;
        }
    }

    /// <summary>
    /// A device heard only over Bluetooth: get its details first (Bluetooth handshake; it starts its own Wi-Fi). Null,
    /// after telling the user why, when that fails.
    /// </summary>
    private async Task<DeviceInfo?> ResolveAsync(DeviceInfo device)
    {
        if (!Beam.Core.Nearby.NearbyService.IsPlaceholder(device)) return device;
        if (IsPhone && !await EnsurePermissionAsync(AppPermission.NearbyDevices)) return null; // to join its Wi-Fi
        try
        {
            return await Node.Nearby.ResolveAsync(device, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Log.Warn($"Couldn't reach {device.Name} over Bluetooth: {ex.Message}");
            await ShowDialogAsync(new ConfirmViewModel(L.T("Couldn't send to {0}", device.Name),
                ex is Beam.Core.Direct.DirectLinkException ? ex.Message : L.T("{0} didn't answer over Bluetooth. Move closer and try again.", device.Name),
                L.T("OK"), ""));
            return null;
        }
    }

    private bool _radiosChecked;

    /// <summary>
    /// Send or Receive opened: get what direct connections need, once per run so a "no" isn't asked again and again. On
    /// phones: the Nearby devices permissions (Wi-Fi and Bluetooth). Then Bluetooth on: Windows turns it on itself;
    /// Android shows its one-tap prompt.
    /// </summary>
    internal async void EnsureBluetoothOn()
    {
        if (_radiosChecked) return;
        _radiosChecked = true;
        try
        {
            if (IsPhone && Node.Direct.IsSupported && !_platform.HasPermission(AppPermission.NearbyDevices))
                await EnsurePermissionAsync(AppPermission.NearbyDevices);
            if (Node.Nearby.State == Beam.Core.Nearby.NearbyRadioState.NeedsPermission) await Node.Nearby.RequestPermissionAsync();
            if (Node.Nearby.State == Beam.Core.Nearby.NearbyRadioState.Off) await Node.Nearby.TurnOnAsync();
        }
        catch (Exception ex)
        {
            Log.Warn($"Getting Wi-Fi and Bluetooth ready failed: {ex.Message}");
        }
    }

    internal Task CopyToClipboardAsync(string text) => _ui.CopyToClipboardAsync(text);

    /// <summary>Copies text; false when the clipboard can't be used now (another app holds it).</summary>
    private async Task<bool> TryCopyAsync(string text)
    {
        try
        {
            await _ui.CopyToClipboardAsync(text);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not copy the received text: {ex.Message}");
            return false;
        }
    }

    /// <summary>Sends text to one device. Returns false (after telling the user why) if it couldn't start.</summary>
    public async Task<bool> StartSendTextAsync(DeviceInfo device, string text)
    {
        if (await ResolveAsync(device) is not { } target) return false;
        device = target;
        try
        {
            Node.SendText(device, text);
            return true;
        }
        catch (TransferException ex)
        {
            await ShowDialogAsync(new ConfirmViewModel(L.T("Can't send this text"), ex.Error.Message, L.T("OK"), ""));
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
            L.Plural(active, "A transfer is still in progress", "{0} transfers are still in progress"),
            L.T("If you quit Beam now, the transfer will stop and unfinished files won't be saved."),
            L.T("Stop and quit"),
            L.T("Keep transferring"),
            isDestructive: true));
        return result is true;
    }

    /// <summary>The window was closed but Beam keeps running in the notification area: say so once.</summary>
    public void OnWindowHiddenToTray()
    {
        if (Node.Settings.Current.TrayHintShown) return;
        Node.Settings.Update(s => s.TrayHintShown = true);
        _platform.ShowNotification(
            L.T("Beam is still running"),
            L.T("Nearby computers can still send you files. To quit, right-click the Beam icon in the notification area."),
            () => Dispatcher.UIThread.Post(_ui.BringToFront));
    }

    public void OnDeviceAppeared(DeviceInfo device)
    {
        // Ignore the burst of devices found right after startup.
        if (DateTime.UtcNow - _startedAt < TimeSpan.FromSeconds(15)) return;
        if (_deviceNotifiedAt.TryGetValue(device.Id, out var last) && DateTime.UtcNow - last < TimeSpan.FromMinutes(30)) return;
        _deviceNotifiedAt[device.Id] = DateTime.UtcNow;
        Notify(s => s.NotifyOnDeviceFound, L.T("{0} is nearby", device.Name), L.T("Open Beam to send files to it."));
    }

    /// <summary>Waits a moment so the user sees the result, then asks for a rating if nothing else is going on.</summary>
    private async Task AskForRatingWhenIdleAsync()
    {
        // Wait for a quiet moment (no transfer running, no dialog open, Beam in front); if none comes soon, the next
        // finished transfer tries again.
        for (var attempt = 0; attempt < 15; attempt++)
        {
            await Task.Delay(RatingPromptDelay);
            if (HasActiveTransfers || HasDialog || !_ui.IsWindowActive) continue;
            await Rating.MaybeAskForRatingAsync();
            return;
        }
    }

    /// <summary>Delay between a finished transfer and the rating prompt (shortened in tests).</summary>
    internal static TimeSpan RatingPromptDelay { get; set; } = TimeSpan.FromSeconds(2);

    // ----- IIncomingTransferHandler (called by the engine on background threads) -----

    public Task<bool> ReceiveTextAsync(IncomingText text, TransferSession session, CancellationToken cancellationToken) =>
        Dispatcher.UIThread.InvokeAsync(async () =>
        {
            if (text.IsTrusted && await TryCopyAsync(text.Text))
            {
                // From a trusted device: no prompt — copy it and say so.
                var link = IncomingTextViewModel.TryGetLink(text.Text);
                _platform.ShowNotification(link != null ? L.T("Link from {0} copied", text.SenderName) : L.T("Text from {0} copied", text.SenderName),
                    text.Text.Length > 120 ? text.Text[..117] + "…" : text.Text,
                    () => Dispatcher.UIThread.Post(_ui.BringToFront));
                return true;
            }

            var dialog = new IncomingTextViewModel(text.SenderName, text.Text, _ui, _platform);
            if (!_ui.IsWindowActive)
            {
                Notify(s => s.NotifyOnIncomingRequest, dialog.Title, L.T("Open Beam to read it."), bringToFront: false);
                _ui.BringToFront();
            }

            return await ShowDialogAsync(dialog, cancellationToken) is true;
        });

    /// <summary>
    /// An unpaired sender asks this device to join its direct network. Says plainly what that does (this device leaves
    /// its Wi-Fi while the files come; a phone may also show Android's own prompt) and shows the security code to compare.
    /// </summary>
    public Task<bool> ApproveDirectConnectionAsync(IncomingDirectRequest request, CancellationToken cancellationToken) =>
        Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var code = Beam.Core.Identity.DeviceIdentity.ShortCode(request.SenderFingerprint);
            var message = L.T("{0} wants to send you files over a direct Wi-Fi connection. This device leaves its Wi-Fi network while the files come, and goes back afterwards. Security code: {1}",
                request.SenderName, code);
            if (request.ClaimedOnly) message += "\n\n" + L.T("Found over Bluetooth: Beam checks this code again on the direct connection before any file comes.");
            var dialog = new ConfirmViewModel(L.T("Connect directly to {0}?", request.SenderName), message, L.T("Connect"), L.T("Not now"));
            if (!_ui.IsWindowActive)
            {
                Notify(s => s.NotifyOnIncomingRequest, dialog.Title, L.T("Open Beam to answer."), bringToFront: false);
                _ui.BringToFront();
            }

            return await ShowDialogAsync(dialog, cancellationToken) is true;
        });

    /// <summary>The user chose to send a failed Direct transfer over the Wi-Fi network instead (a new, separate transfer).</summary>
    private async Task SendOverNetworkInsteadAsync(TransferSession failed)
    {
        try
        {
            Node.SendOverNetworkInstead(failed);
        }
        catch (TransferException ex)
        {
            await ShowDialogAsync(new ConfirmViewModel(L.T("Couldn't send to {0}", failed.PeerName), ex.Error.Message, L.T("OK"), ""));
        }
    }

    public Task<IncomingDecision> RequestApprovalAsync(IncomingRequest request, TransferSession session, CancellationToken cancellationToken) =>
        Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var canTrust = request.SenderFingerprint.Length > 0;
            var dialog = new IncomingRequestViewModel(request, _ui, canTrust);
            var tag = "request-" + session.Id;
            var settings = Node.Settings.Current;
            if (!_ui.IsWindowActive && settings.NotificationsEnabled && settings.NotifyOnIncomingRequest)
            {
                // Answer right from the notification (phones), or open Beam to see the details.
                _platform.ShowQuestionNotification(tag, L.T("Incoming files"), dialog.Title,
                    () => Dispatcher.UIThread.Post(_ui.BringToFront),
                    new[]
                    {
                        new NotificationButton(L.T("Accept"), () => Dispatcher.UIThread.Post(() =>
                        {
                            if (dialog.AcceptCommand.CanExecute(null)) dialog.AcceptCommand.Execute(null);
                            else _ui.BringToFront(); // e.g. not enough space: the dialog explains
                        })),
                        new NotificationButton(L.T("Decline"), () => Dispatcher.UIThread.Post(() => dialog.DeclineCommand.Execute(null))),
                    });
                _ui.BringToFront();
            }

            bool accepted;
            try
            {
                accepted = await ShowDialogAsync(dialog, cancellationToken) is true;
            }
            finally
            {
                _platform.CancelNotification(tag);
            }

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
        var vm = new TransferViewModel(session, _platform, Dismiss, _ui.CopyToClipboardAsync, Node.Direct, Node.Discovery.Find, SendOverNetworkInsteadAsync);
        session.DirectChanged += _ => Dispatcher.UIThread.Post(vm.Refresh);
        Transfers.Insert(0, vm);
        session.StateChanged += s => Dispatcher.UIThread.Post(() => OnSessionStateChanged(vm));
        // A short transfer can finish before this subscription; catch up so it is still announced and counted.
        Dispatcher.UIThread.Post(() => OnSessionStateChanged(vm));
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
        else vm.Announced = false; // "Try again" after a failure: announce the new outcome
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
        // State-change events can arrive more than once for the same final state; announce and count each transfer once.
        if (vm.Announced) return;
        vm.Announced = true;
        var session = vm.Session;
        var snapshot = session.GetSnapshot();
        if (snapshot.State == TransferState.Completed)
        {
            Node.Settings.Update(s => s.CompletedTransfers++);
            AskForRatingWhenIdleAsync().Forget("Asking for a rating");
        }

        var title = session.Title;
        switch (snapshot.State)
        {
            case TransferState.Completed when session.Direction == TransferDirection.Receive:
                var folder = Path.GetFileName(Path.TrimEndingDirectorySeparator(session.DestinationFolder ?? ""));
                Notify(s => s.NotifyOnTransferFinished, L.T("Files received"),
                    L.T("{0} from {1} was saved to {2}.", title, session.PeerName, folder), () => vm.OpenCommand.Execute(null));
                break;
            case TransferState.Completed:
                Notify(s => s.NotifyOnTransferFinished, L.T("Files sent"), L.T("{0} was sent to {1}.", title, session.PeerName));
                break;
            case TransferState.CompletedWithErrors:
                Notify(s => s.NotifyOnTransferFinished, L.T("Transfer finished with problems"),
                    L.Plural(snapshot.TotalFiles, "{1} of {0} file couldn't be transferred.", "{1} of {0} files couldn't be transferred.", snapshot.FailedFiles));
                break;
            case TransferState.Failed:
                Notify(s => s.NotifyOnTransferFinished, L.T("Transfer failed"), snapshot.Error?.Message ?? L.T("The transfer could not be completed."));
                break;
            case TransferState.Declined when session.Direction == TransferDirection.Send:
            case TransferState.Cancelled when snapshot.Error?.Kind == TransferErrorKind.CancelledByRemote:
                Notify(s => s.NotifyOnTransferFinished, L.T("Transfer stopped"), snapshot.Error?.Message ?? L.T("The transfer was stopped."));

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

/// <summary>Phones: running transfers and the history in one tab.</summary>
public sealed class TransfersPage : ObservableObject
{
    public TransfersPage(MainViewModel main)
    {
        Main = main;
        main.Transfers.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsEmpty));
        main.History.Items.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsEmpty));
    }

    public MainViewModel Main { get; }

    public bool IsEmpty => Main.Transfers.Count == 0 && Main.History.Items.Count == 0;
}
