using System.Collections.ObjectModel;
using Avalonia.Threading;
using Beam.App.Infrastructure;
using Beam.App.Platform;
using Beam.App.Services;
using Beam.App.ViewModels.Dialogs;
using Beam.Core;
using Beam.Core.Backup;
using Beam.Core.Diagnostics;
using Beam.Core.Direct;
using Beam.Core.Localization;
using Beam.Core.Transfer;

namespace Beam.App.ViewModels;

/// <summary>A choice of which files of a folder are backed up.</summary>
public sealed record FileTypeOption(string Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>On the computer: a phone that backs up here.</summary>
public sealed class BackupPartnerViewModel
{
    public BackupPartnerViewModel(BackupPartner partner, Action<BackupPartnerViewModel> open, Action<BackupPartnerViewModel> remove)
    {
        Partner = partner;
        OpenCommand = new RelayCommand(() => open(this));
        RemoveCommand = new RelayCommand(() => remove(this));
    }

    public BackupPartner Partner { get; }

    public string Name => Partner.Name;

    public string Details => Partner.LastBackupAt is { } last
        ? L.T("Last backup {0:g} · into {1}", last.ToLocalTime().DateTime, Partner.Folder)
        : L.T("No backup yet · into {0}", Partner.Folder);

    public RelayCommand OpenCommand { get; }

    public RelayCommand RemoveCommand { get; }
}

/// <summary>On the phone: one thing that is backed up (the photos, the videos, or a folder and its kinds of files).</summary>
public sealed class BackupSourceViewModel : ObservableObject
{
    private readonly Action _changed;

    public BackupSourceViewModel(BackupSource source, IReadOnlyList<FileTypeOption> fileTypes, Action changed, Action<BackupSourceViewModel> remove)
    {
        Source = source;
        FileTypeOptions = fileTypes;
        _changed = changed;
        RemoveCommand = new RelayCommand(() => remove(this));
    }

    public BackupSource Source { get; }

    public string Title => Source.Kind switch
    {
        BackupSourceKinds.Photos => L.T("Photos"),
        BackupSourceKinds.Videos => L.T("Videos"),
        _ => Source.Name,
    };

    public string Details => Source.Kind switch
    {
        BackupSourceKinds.Photos => L.T("All photos on this phone"),
        BackupSourceKinds.Videos => L.T("All videos on this phone"),
        _ => L.T("Folder"),
    };

    public bool IsFolder => Source.Kind == BackupSourceKinds.Folder;

    public IReadOnlyList<FileTypeOption> FileTypeOptions { get; }

    public FileTypeOption SelectedFileType
    {
        get => FileTypeOptions.FirstOrDefault(o => o.Value == Source.FileTypes) ?? FileTypeOptions[0];
        set
        {
            if (value == null || value.Value == Source.FileTypes) return;
            Source.FileTypes = value.Value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsCustom));
            _changed();
        }
    }

    public bool IsCustom => IsFolder && Source.FileTypes == BackupFileTypes.Custom;

    public string Extensions
    {
        get => Source.Extensions ?? "";
        set
        {
            if (value == Source.Extensions) return;
            Source.Extensions = value;
            OnPropertyChanged();
            _changed();
        }
    }

    public RelayCommand RemoveCommand { get; }
}

/// <summary>
/// Settings → Backup. On a computer: set up phones that back up here (a one-time code), and the list of them. On a
/// phone: set up a backup by scanning a computer's code, choose what is backed up, and see how it went.
/// </summary>
public sealed class BackupViewModel : ObservableObject
{
    private readonly BeamNode _node;
    private readonly IPlatformServices _platform;
    private readonly IUiServices _ui;
    private readonly MainViewModel _main;
    private readonly DispatcherTimer _saveTimer;
    private readonly DispatcherTimer _statusTimer;
    private BackupPlan? _plan;
    private string _message = "";
    private bool _isBusy;

    public BackupViewModel(BeamNode node, IPlatformServices platform, IUiServices ui, MainViewModel main)
    {
        _node = node;
        _platform = platform;
        _ui = ui;
        _main = main;
        FileTypeOptions = new[]
        {
            new FileTypeOption(BackupFileTypes.All, L.T("All files")),
            new FileTypeOption(BackupFileTypes.Photos, L.T("Photos only")),
            new FileTypeOption(BackupFileTypes.Videos, L.T("Videos only")),
            new FileTypeOption(BackupFileTypes.Documents, L.T("Documents only (PDF, Word, Excel…)")),
            new FileTypeOption(BackupFileTypes.Custom, L.T("Only these types…")),
        };

        AddPhoneCommand = new AsyncCommand(AddPhoneAsync);
        ChangeFolderCommand = new AsyncCommand(ChangeFolderAsync);
        OpenFolderCommand = new RelayCommand(() =>
        {
            Directory.CreateDirectory(BackupFolder);
            _platform.OpenFolder(BackupFolder);
        });
        SetUpCommand = new AsyncCommand(ScanAsync, () => !IsBusy);
        AddPhotosCommand = new AsyncCommand(() => AddMediaAsync(BackupSourceKinds.Photos), () => !Has(BackupSourceKinds.Photos));
        AddVideosCommand = new AsyncCommand(() => AddMediaAsync(BackupSourceKinds.Videos), () => !Has(BackupSourceKinds.Videos));
        AddFolderCommand = new AsyncCommand(AddFolderAsync);
        BackUpNowCommand = new RelayCommand(BackUpNow, () => _plan is { Sources.Count: > 0 } && !IsRunning);
        StopCommand = new AsyncCommand(StopAsync);

        // Typing file types: saved once the typing stops (each save checks the backup again).
        _saveTimer = new DispatcherTimer(TimeSpan.FromSeconds(1.5), DispatcherPriority.Background, (_, _) => SaveNow());
        _statusTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => RefreshStatus());

        node.Backup.Changed += () => Dispatcher.UIThread.Post(Reload);
        node.Settings.Changed += _ => Dispatcher.UIThread.Post(Reload);
        node.Discovery.DeviceFound += (_, _) => Dispatcher.UIThread.Post(RefreshStatus);
        node.Discovery.DeviceLost += (_, _) => Dispatcher.UIThread.Post(RefreshStatus);
        Reload();
    }

    /// <summary>The backup section is there: computers take backups, phones that can back up make them.</summary>
    public bool IsAvailable => IsComputer || _platform.SupportsBackup;

    public bool IsComputer => !_platform.IsPhone;

    public bool IsPhone => _platform.IsPhone && _platform.SupportsBackup;

    public IReadOnlyList<FileTypeOption> FileTypeOptions { get; }

    // ------------------------------------------------------------ the computer

    public ObservableCollection<BackupPartnerViewModel> Partners { get; } = new();

    public bool HasPartners => Partners.Count > 0;

    public string BackupFolder => _node.Settings.Current.EffectiveBackupFolder;

    public AsyncCommand AddPhoneCommand { get; }

    public AsyncCommand ChangeFolderCommand { get; }

    public RelayCommand OpenFolderCommand { get; }

    // ------------------------------------------------------------ the phone

    public bool HasPlan => _plan != null;

    public bool HasNoPlan => _plan == null;

    public string TargetName => _plan?.TargetName ?? "";

    public string TargetText => L.T("Backing up to {0}", TargetName);

    public ObservableCollection<BackupSourceViewModel> Sources { get; } = new();

    public bool HasSources => Sources.Count > 0;

    public bool Automatic
    {
        get => _plan?.Automatic ?? false;
        set
        {
            if (_plan == null || _plan.Automatic == value) return;
            _plan.Automatic = value;
            SaveNow();
        }
    }

    public bool IsRunning => _plan != null && _node.Backup.Running(_plan.TargetId) != null;

    public string StatusText
    {
        get
        {
            if (_plan == null) return "";
            if (_node.Backup.Running(_plan.TargetId) is { } session)
            {
                var snapshot = session.GetSnapshot();
                return snapshot.State == TransferState.Transferring && snapshot.TotalFiles > 0
                    ? L.Plural(snapshot.TotalFiles, "Backing up: {1} of {0} new file…", "Backing up: {1} of {0} new files…", snapshot.CompletedFiles)
                    : L.T("Checking what's new…");
            }

            if (_plan.Sources.Count == 0) return L.T("Choose what to back up.");
            if (_plan.LastError is { } error) return L.T("The last backup didn't finish: {0}", error);
            var here = _node.Backup.IsReachable(_plan);
            if (_plan.LastCompletedAt is { } last)
                return here || !_plan.Automatic
                    ? L.T("Everything was backed up {0:g}.", last.ToLocalTime().DateTime)
                    : L.T("Everything was backed up {0:g}. New files are backed up when this phone and {1} are on the same Wi-Fi.", last.ToLocalTime().DateTime, TargetName);
            return here ? L.T("Ready to back up.") : L.T("Waiting for {0} on the same Wi-Fi.", TargetName);
        }
    }

    public bool HasError => _plan?.LastError != null && !IsRunning;

    public string Message
    {
        get => _message;
        private set
        {
            if (SetProperty(ref _message, value)) OnPropertyChanged(nameof(HasMessage));
        }
    }

    public bool HasMessage => Message.Length > 0;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value)) SetUpCommand.RaiseCanExecuteChanged();
        }
    }

    public AsyncCommand SetUpCommand { get; }

    public AsyncCommand AddPhotosCommand { get; }

    public AsyncCommand AddVideosCommand { get; }

    public AsyncCommand AddFolderCommand { get; }

    public RelayCommand BackUpNowCommand { get; }

    public AsyncCommand StopCommand { get; }

    /// <summary>
    /// A backup code was scanned (here or with the scan button on the home page): sets up the backup to that computer.
    /// </summary>
    public async Task SetUpFromCodeAsync(PairingCode code)
    {
        if (!IsPhone)
        {
            await _main.ShowDialogAsync(new ConfirmViewModel(L.T("Backup isn't available here"),
                L.T("Backups are made from Beam on an Android phone."), L.T("OK"), ""));
            return;
        }

        IsBusy = true;
        Message = "";
        var previous = _node.Settings.Current.Backups.FirstOrDefault(b => b.TargetId != code.DeviceId);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var plan = await _node.Backup.SetUpAsync(code, cts.Token);
            // One computer at a time: a new one takes over what the previous one backed up.
            if (previous != null)
            {
                if (plan.Sources.Count == 0) plan.Sources.AddRange(previous.Sources);
                _node.Backup.Remove(previous.TargetId);
            }

            if (plan.Sources.Count == 0)
            {
                // The usual choice to start with; easy to change below.
                plan.Sources.Add(new BackupSource { Kind = BackupSourceKinds.Photos, Name = L.T("Photos") });
                plan.Sources.Add(new BackupSource { Kind = BackupSourceKinds.Videos, Name = L.T("Videos") });
            }

            if (!await _main.EnsurePermissionAsync(AppPermission.MediaLibrary))
                plan.Sources.RemoveAll(s => s.Kind is BackupSourceKinds.Photos or BackupSourceKinds.Videos);
            _node.Backup.Save(plan);
            Reload();
            _main.IsSettingsPage = true;
            await _main.ShowDialogAsync(new ConfirmViewModel(L.T("Backup is set up"),
                L.T("This phone backs up to {0} by itself whenever both are on the same Wi-Fi. Only new and changed files are sent, encrypted, and only to {0}. Choose what is backed up in Settings → Backup.", plan.TargetName),
                L.T("OK"), ""));
        }
        catch (TransferException ex)
        {
            Message = ex.Error.Message;
            Log.Info($"Backup setup failed: {ex.Error.Details ?? ex.Error.Message}");
        }
        catch (OperationCanceledException)
        {
            Message = L.T("Couldn't reach {0}. Make sure this phone and the computer are on the same Wi-Fi, then scan the code again.", code.Name);
        }
        catch (Exception ex)
        {
            Message = L.T("The backup couldn't be set up. Try again; if it keeps happening, send the log from Settings.");
            Log.Warn("Backup setup failed", ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    internal void OnShown() => RefreshStatus();

    private bool Has(string kind) => _plan?.Sources.Any(s => s.Kind == kind) == true;

    private async Task ScanAsync()
    {
        var scanned = await _platform.ScanCodeAsync();
        if (string.IsNullOrWhiteSpace(scanned)) return;
        if (PairingCode.TryParse(scanned) is not { BackupToken: not null } code)
        {
            Message = L.T("That's not a backup code. On the computer, open Beam → Settings → Backup → Add a phone, and scan the code shown there.");
            return;
        }

        await SetUpFromCodeAsync(code);
    }

    private async Task AddMediaAsync(string kind)
    {
        if (_plan == null || Has(kind)) return;
        if (!await _main.EnsurePermissionAsync(AppPermission.MediaLibrary)) return;
        _plan.Sources.Add(new BackupSource { Kind = kind, Name = kind == BackupSourceKinds.Photos ? L.T("Photos") : L.T("Videos") });
        SaveNow();
    }

    private async Task AddFolderAsync()
    {
        if (_plan == null) return;
        var picked = await _ui.PickFolderAsync(L.T("Choose a folder to back up"), null);
        if (string.IsNullOrEmpty(picked)) return;
        if (_platform.KeepFolderAccess(picked) is not { } address)
        {
            await _main.ShowDialogAsync(new ConfirmViewModel(L.T("Can't back up this folder"),
                L.T("Android doesn't let Beam keep reading that folder. Choose another one."), L.T("OK"), ""));
            return;
        }

        if (_plan.Sources.Any(s => s.Kind == BackupSourceKinds.Folder && s.Address == address)) return;
        var name = _node.Transfers.ExternalFiles?.TryGetFolderName(address, out var folderName) == true
            ? folderName
            : Path.GetFileName(Path.TrimEndingDirectorySeparator(address));
        _plan.Sources.Add(new BackupSource
        {
            Kind = BackupSourceKinds.Folder,
            Address = address,
            Name = string.IsNullOrWhiteSpace(name) ? L.T("Folder") : name,
        });
        SaveNow();
    }

    private void BackUpNow()
    {
        if (_plan == null) return;
        SaveNow();
        try
        {
            _node.Backup.BackUpNow(_plan.TargetId);
            Message = "";
        }
        catch (TransferException ex)
        {
            Message = ex.Error.Message;
        }

        RefreshStatus();
    }

    private async Task StopAsync()
    {
        if (_plan == null) return;
        var name = _plan.TargetName;
        if (await _main.ShowDialogAsync(new ConfirmViewModel(L.T("Stop backing up to {0}?", name),
                L.T("Nothing more is backed up. What was already backed up stays on {0}.", name), L.T("Stop backup"), isDestructive: true)) is not true)
            return;
        _node.Backup.Remove(_plan.TargetId);
        Reload();
    }

    private void OnSourcesEdited()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void SaveNow()
    {
        _saveTimer.Stop();
        if (_plan == null) return;
        _node.Backup.Save(_plan);
        Reload();
    }

    private void RemoveSource(BackupSourceViewModel source)
    {
        if (_plan == null) return;
        _plan.Sources.Remove(source.Source);
        SaveNow();
    }

    private async Task AddPhoneAsync()
    {
        var dialog = new BackupCodeViewModel(_node);
        try
        {
            await _main.ShowDialogAsync(dialog);
        }
        finally
        {
            dialog.OnClosed();
        }

        Reload();
    }

    private async Task ChangeFolderAsync()
    {
        var picked = await _ui.PickFolderAsync(L.T("Choose where backups are saved"), BackupFolder);
        if (string.IsNullOrEmpty(picked)) return;
        // Phones set up already keep their folders; new ones go into the new place.
        _node.Settings.Update(s => s.BackupFolder = picked);
    }

    private async void RemovePartner(BackupPartnerViewModel partner)
    {
        try
        {
            var confirmed = await _main.ShowDialogAsync(new ConfirmViewModel(
                L.T("Stop taking backups from {0}?", partner.Name),
                L.T("{0} can't back up to this computer any more, unless it's set up again. What it backed up stays in {1}.", partner.Name, partner.Partner.Folder),
                L.T("Stop backups"), isDestructive: true));
            if (confirmed is true) _node.Backup.RemovePartner(partner.Partner.Fingerprint);
        }
        catch (Exception ex)
        {
            Log.Warn("Removing a backup partner failed", ex); // an async void that throws would end the app
        }
    }

    private void OpenPartner(BackupPartnerViewModel partner)
    {
        Directory.CreateDirectory(partner.Partner.Folder);
        _platform.OpenFolder(partner.Partner.Folder);
    }

    private void Reload()
    {
        var settings = _node.Settings.Current;

        static string PartnerKey(BackupPartner p) => $"{p.Fingerprint}|{p.Name}|{p.Folder}|{p.LastBackupAt:O}|{p.LastFileCount}";
        if (!Partners.Select(p => PartnerKey(p.Partner)).SequenceEqual(settings.BackupPartners.Select(PartnerKey)))
        {
            Partners.Clear();
            foreach (var partner in settings.BackupPartners) Partners.Add(new BackupPartnerViewModel(partner, OpenPartner, RemovePartner));
            OnPropertyChanged(nameof(HasPartners));
        }

        OnPropertyChanged(nameof(BackupFolder));

        // The phone's plan (a copy: the settings hold their own until it is saved).
        var stored = settings.Backups.FirstOrDefault();
        if (_saveTimer.IsEnabled && stored?.TargetId == _plan?.TargetId) return; // typing: the edits win until saved
        _plan = stored;
        static string SourceKey(BackupSource s) => $"{s.Kind}|{s.Address}|{s.Name}|{s.FileTypes}|{s.Extensions}";
        var sources = _plan?.Sources ?? new List<BackupSource>();
        if (!Sources.Select(s => SourceKey(s.Source)).SequenceEqual(sources.Select(SourceKey)))
        {
            Sources.Clear();
            foreach (var source in sources) Sources.Add(new BackupSourceViewModel(source, FileTypeOptions, OnSourcesEdited, RemoveSource));
            OnPropertyChanged(nameof(HasSources));
        }
        else
        {
            // Same choices: keep the rows (and the text being typed), pointing at the fresh copy.
            for (var i = 0; i < Sources.Count; i++)
                if (!ReferenceEquals(Sources[i].Source, sources[i]))
                    Sources[i] = new BackupSourceViewModel(sources[i], FileTypeOptions, OnSourcesEdited, RemoveSource);
        }

        foreach (var name in new[] { nameof(HasPlan), nameof(HasNoPlan), nameof(TargetName), nameof(TargetText), nameof(Automatic) })
            OnPropertyChanged(name);
        AddPhotosCommand.RaiseCanExecuteChanged();
        AddVideosCommand.RaiseCanExecuteChanged();
        _platform.ScheduleBackups(settings.Backups.Any(b => b.Automatic && b.Sources.Count > 0));
        RefreshStatus();
    }

    private void RefreshStatus()
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(HasError));
        BackUpNowCommand.RaiseCanExecuteChanged();
        if (IsRunning) _statusTimer.Start();
        else _statusTimer.Stop();
    }
}
