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

/// <summary>A folder in "Choose folders": ticked when it is backed up; "New" while the user hasn't seen it yet.</summary>
public sealed class FolderChoiceViewModel : ObservableObject
{
    private readonly WholePhoneViewModel _owner;

    public FolderChoiceViewModel(WholePhoneViewModel owner, BackupFolderInfo folder)
    {
        _owner = owner;
        Folder = folder;
    }

    public BackupFolderInfo Folder { get; }

    public string Name => Folder.Name;

    public string Details => L.Plural(Folder.Count, "{0} file", "{0} files");

    public bool IsNew => _owner.IsNew(Folder.Path);

    public bool IsChecked
    {
        get => _owner.Includes(Folder.Path);
        set => _owner.SetFolder(Folder.Path, value);
    }

    /// <summary>Gray and fixed while "All folders" is ticked.</summary>
    public bool IsEditable => !_owner.AllFolders;

    internal void Refresh()
    {
        OnPropertyChanged(nameof(IsChecked));
        OnPropertyChanged(nameof(IsEditable));
        OnPropertyChanged(nameof(IsNew));
    }
}

/// <summary>
/// Photos, videos, music or documents from the whole phone: a switch, and "Choose folders" (closed at first): "All
/// folders" (every folder, also later ones; the list gray) or the folders ticked one by one, a new folder shown as new
/// and left out until it is ticked.
/// </summary>
public sealed class WholePhoneViewModel : ObservableObject
{
    private readonly BackupViewModel _owner;
    private readonly Func<bool> _isOn;
    private readonly Action<bool> _setOn;
    private IReadOnlyList<BackupFolderInfo>? _loaded;
    private bool _isLoading;
    private string _loadError = "";

    public WholePhoneViewModel(BackupViewModel owner, string kind, string title, string caption, Func<bool> isOn, Action<bool> setOn)
    {
        _owner = owner;
        Kind = kind;
        Title = title;
        Caption = caption;
        _isOn = isOn;
        _setOn = setOn;
        ToggleFoldersCommand = new RelayCommand(() =>
        {
            ShowsFolders = !ShowsFolders;
            OnPropertyChanged(nameof(ShowsFolders));
            OnPropertyChanged(nameof(FoldersButtonText));
            if (ShowsFolders) LoadAsync().Forget("Listing folders");
            else _owner.MarkSeen(this); // closed: the folders shown aren't new any more
        });
    }

    public string Kind { get; }

    public string Title { get; }

    public string Caption { get; }

    public bool IsOn
    {
        get => _isOn();
        set => _setOn(value);
    }

    public bool ShowsFolders { get; private set; }

    public string FoldersButtonText => ShowsFolders ? L.T("Hide folders") : L.T("Choose folders");

    public RelayCommand ToggleFoldersCommand { get; }

    public ObservableCollection<FolderChoiceViewModel> Folders { get; } = new();

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetProperty(ref _isLoading, value);
    }

    public string LoadError
    {
        get => _loadError;
        private set
        {
            if (SetProperty(ref _loadError, value)) OnPropertyChanged(nameof(HasLoadError));
        }
    }

    public bool HasLoadError => LoadError.Length > 0;

    /// <summary>"All folders": everything, also folders that appear later; unticked, each folder is chosen.</summary>
    public bool AllFolders
    {
        get => Source?.AllFolders ?? true;
        set => _owner.SetAllFolders(this, value);
    }

    /// <summary>"All folders", "Skipping: WhatsApp Images", "New, not backed up: Telegram".</summary>
    public string Summary
    {
        get
        {
            if (Source is not { } source) return "";
            if (source.AllFolders) return L.T("All folders");
            static string NameOf(string path) => path.Length == 0 ? L.T("Main storage") : path[(path.LastIndexOf('/') + 1)..];
            var skipped = source.SeenFolders.Where(f => !source.Folders.Contains(f)).Select(NameOf).ToList();
            var fresh = source.AnnouncedFolders.Concat(LoadedPaths ?? Array.Empty<string>())
                .Where(f => !source.SeenFolders.Contains(f) && !source.Folders.Contains(f)).Distinct().Select(NameOf).ToList();
            var parts = new List<string>();
            if (skipped.Count > 0) parts.Add(L.T("Skipping: {0}", string.Join(", ", skipped)));
            if (fresh.Count > 0) parts.Add(L.T("New, not backed up: {0}", string.Join(", ", fresh)));
            return parts.Count > 0 ? string.Join(" · ", parts) : L.T("Chosen folders");
        }
    }

    internal BackupSource? Source => _owner.SourceOf(Kind);

    internal IReadOnlyList<string>? LoadedPaths => _loaded?.Select(f => f.Path).ToList();

    internal bool Includes(string path) => Source?.IncludesFolder(path) == true;

    internal bool IsNew(string path) => Source is { AllFolders: false } source && !source.SeenFolders.Contains(path);

    internal void SetFolder(string path, bool ticked) => _owner.SetFolder(this, path, ticked);

    internal void Refresh()
    {
        OnPropertyChanged(nameof(IsOn));
        OnPropertyChanged(nameof(AllFolders));
        OnPropertyChanged(nameof(Summary));
        foreach (var folder in Folders) folder.Refresh();
    }

    private async Task LoadAsync()
    {
        if (Source is not { } source) return;
        IsLoading = true;
        LoadError = "";
        try
        {
            _loaded = await _owner.ListFoldersAsync(source);
            Folders.Clear();
            foreach (var folder in _loaded) Folders.Add(new FolderChoiceViewModel(this, folder));
        }
        catch (Exception ex)
        {
            Log.Warn($"Backup: couldn't list the {Kind} folders", ex);
            LoadError = L.T("Couldn't list the folders: {0}", ex.Message);
        }
        finally
        {
            IsLoading = false;
            Refresh();
        }
    }
}

/// <summary>One kind of file a folder backup can take (a checkbox); "Documents" ticks or clears all of its kinds.</summary>
public sealed class FileKindViewModel : ObservableObject
{
    private readonly BackupSourceViewModel _owner;
    private readonly IReadOnlyList<string> _kinds;

    public FileKindViewModel(BackupSourceViewModel owner, string label, string details, IReadOnlyList<string> kinds, bool isGroup = false, bool isChild = false)
    {
        _owner = owner;
        Label = label;
        Details = details;
        _kinds = kinds;
        IsGroup = isGroup;
        IsChild = isChild;
    }

    public string Label { get; }

    /// <summary>Its usual extensions ("DOC, DOCX, ODT…").</summary>
    public string Details { get; }

    public bool HasDetails => Details.Length > 0;

    public bool IsGroup { get; }

    /// <summary>One of the documents' kinds: shown indented under "Documents".</summary>
    public bool IsChild { get; }

    /// <summary>Ticked; for "Documents", null when only some of its kinds are.</summary>
    public bool? IsChecked
    {
        get
        {
            var ticked = _kinds.Count(_owner.Has);
            return ticked == _kinds.Count ? true : ticked == 0 ? false : null;
        }
        set => _owner.Set(_kinds, value == true);
    }

    /// <summary>Gray and fixed while "All file types" is ticked.</summary>
    public bool IsEditable => !_owner.Source.AllTypes;

    internal void Refresh()
    {
        OnPropertyChanged(nameof(IsChecked));
        OnPropertyChanged(nameof(IsEditable));
    }
}

/// <summary>On the phone: one thing that is backed up (the photos, the videos, or a folder and its kinds of files).</summary>
public sealed class BackupSourceViewModel : ObservableObject
{
    private readonly Action _changed;

    private readonly Action _saveNow;

    public BackupSourceViewModel(BackupSource source, Action changed, Action saveNow, Action<BackupSourceViewModel> remove)
    {
        Source = source;
        _changed = changed;
        _saveNow = saveNow;
        RemoveCommand = new RelayCommand(() => remove(this));
        ToggleKindsCommand = new RelayCommand(() =>
        {
            ShowsKinds = !ShowsKinds;
            OnPropertyChanged(nameof(ShowsKinds));
            OnPropertyChanged(nameof(KindsButtonText));
        });
        if (!IsFolder) return;
        Source.Types ??= Source.EffectiveTypes().ToList();
        string Of(string id) => Examples(BackupFileTypes.Kinds.First(k => k.Id == id).Extensions);
        FileKinds = new[]
        {
            new FileKindViewModel(this, L.T("Photos"), Of(BackupFileTypes.Photos), new[] { BackupFileTypes.Photos }),
            new FileKindViewModel(this, L.T("Videos"), Of(BackupFileTypes.Videos), new[] { BackupFileTypes.Videos }),
            new FileKindViewModel(this, L.T("Documents"), "", BackupFileTypes.Documents, isGroup: true),
            new FileKindViewModel(this, "PDF", "", new[] { BackupFileTypes.Pdf }, isChild: true),
            new FileKindViewModel(this, "Word", Of(BackupFileTypes.Word), new[] { BackupFileTypes.Word }, isChild: true),
            new FileKindViewModel(this, "Excel", Of(BackupFileTypes.Excel), new[] { BackupFileTypes.Excel }, isChild: true),
            new FileKindViewModel(this, "PowerPoint", Of(BackupFileTypes.PowerPoint), new[] { BackupFileTypes.PowerPoint }, isChild: true),
            new FileKindViewModel(this, L.T("Text"), Of(BackupFileTypes.Text), new[] { BackupFileTypes.Text }, isChild: true),
            new FileKindViewModel(this, L.T("E-books"), Of(BackupFileTypes.Ebooks), new[] { BackupFileTypes.Ebooks }, isChild: true),
            new FileKindViewModel(this, L.T("Music"), Of(BackupFileTypes.Music), new[] { BackupFileTypes.Music }),
            new FileKindViewModel(this, L.T("Archives"), Of(BackupFileTypes.Archives), new[] { BackupFileTypes.Archives }),
            new FileKindViewModel(this, L.T("Other"), L.T("Every other kind of file"), new[] { BackupFileTypes.Other }),
        };
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

    /// <summary>For a folder: a checkbox per kind of file, "Other" last.</summary>
    public IReadOnlyList<FileKindViewModel> FileKinds { get; } = Array.Empty<FileKindViewModel>();

    /// <summary>The folder is backed up (off: kept with its choices, nothing of it backed up).</summary>
    public bool Enabled
    {
        get => Source.Enabled;
        set
        {
            if (Source.Enabled == value) return;
            Source.Enabled = value;
            OnPropertyChanged();
            _saveNow();
        }
    }

    /// <summary>The checkboxes are open (they start closed: the summary says what is chosen).</summary>
    public bool ShowsKinds { get; private set; }

    public string KindsButtonText => ShowsKinds ? L.T("Hide file types") : L.T("Choose file types");

    public RelayCommand ToggleKindsCommand { get; }

    /// <summary>"All file types", "File types: Documents, Music", "File types: PDF, Word" or "No file types".</summary>
    public string KindsSummary
    {
        get
        {
            var types = Source.EffectiveTypes();
            if (types.Count == 0) return L.T("No file types");
            if (Source.AllTypes || BackupFileTypes.All.All(types.Contains)) return L.T("All file types");
            var names = new List<string>();
            foreach (var kind in FileKinds.Where(k => !k.IsChild))
            {
                if (kind.IsChecked == true) names.Add(kind.Label);
                else if (kind.IsChecked == null) names.AddRange(FileKinds.Where(c => c.IsChild && c.IsChecked == true).Select(c => c.Label));
            }

            return L.T("File types: {0}", string.Join(", ", names));
        }
    }

    /// <summary>A folder with nothing ticked: nothing of it is backed up.</summary>
    public bool HasNoKinds => IsFolder && Source.Types is { Count: 0 };

    public RelayCommand RemoveCommand { get; }

    /// <summary>
    /// "All file types": every kind, the checkboxes below gray. Unticked, every kind stays ticked and each can be changed.
    /// </summary>
    public bool AllTypes
    {
        get => Source.AllTypes;
        set
        {
            if (Source.AllTypes == value) return;
            Source.AllTypes = value;
            if (!value) Source.Types = BackupFileTypes.All.ToList();
            OnPropertyChanged();
            foreach (var kind in FileKinds) kind.Refresh();
            OnPropertyChanged(nameof(HasNoKinds));
            OnPropertyChanged(nameof(KindsSummary));
            _saveNow();
        }
    }

    internal bool Has(string kind) => Source.AllTypes || Source.Types?.Contains(kind) == true;

    internal void Set(IReadOnlyList<string> kinds, bool ticked)
    {
        if (Source.AllTypes) return; // fixed while "All file types" is ticked
        var types = Source.Types ??= new List<string>();
        var before = types.Count;
        types.RemoveAll(kinds.Contains);
        if (ticked) types.AddRange(kinds);
        // Kept in the list's order, so the same choice always looks the same when saved.
        Source.Types = BackupFileTypes.All.Where(types.Contains).ToList();
        foreach (var kind in FileKinds) kind.Refresh();
        OnPropertyChanged(nameof(HasNoKinds));
        OnPropertyChanged(nameof(KindsSummary));
        _changed();
    }

    private static string Examples(string[] extensions) =>
        string.Join(", ", extensions.Take(4).Select(e => e.TrimStart('.').ToUpperInvariant())) + (extensions.Length > 4 ? "…" : "");
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
    private DateTime _ownerConfirmedAt = DateTime.MinValue;
    private string _message = "";
    private bool _isBusy;

    public BackupViewModel(BeamNode node, IPlatformServices platform, IUiServices ui, MainViewModel main)
    {
        _node = node;
        _platform = platform;
        _ui = ui;
        _main = main;

        AddPhoneCommand = new AsyncCommand(AddPhoneAsync);
        ChangeFolderCommand = new AsyncCommand(ChangeFolderAsync);
        OpenFolderCommand = new RelayCommand(() =>
        {
            Directory.CreateDirectory(BackupFolder);
            _platform.OpenFolder(BackupFolder);
        });
        SetUpCommand = new AsyncCommand(ScanAsync, () => !IsBusy);
        AddFolderCommand = new AsyncCommand(AddFolderAsync);
        BackUpNowCommand = new AsyncCommand(BackUpNowAsync, () => _plan is { HasAnythingToBackUp: true } && !IsRunning);
        StopCommand = new AsyncCommand(StopAsync);

        // Typing file types: saved once the typing stops (each save checks the backup again).
        // Not started here (that constructor overload starts them): the save timer runs only after an edit, and while
        // it runs newer saved settings are held back, so a timer running from the start would save an old copy.
        _saveTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1.5) };
        _saveTimer.Tick += (_, _) => SaveAsync().Forget("Saving the backup choices");
        _statusTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _statusTimer.Tick += (_, _) => RefreshStatus();

        WholePhoneKinds = new[]
        {
            new WholePhoneViewModel(this, BackupSourceKinds.Photos, L.T("Photos"), L.T("Every photo on this phone"), () => BackUpPhotos, v => BackUpPhotos = v),
            new WholePhoneViewModel(this, BackupSourceKinds.Videos, L.T("Videos"), L.T("Every video on this phone"), () => BackUpVideos, v => BackUpVideos = v),
            new WholePhoneViewModel(this, BackupSourceKinds.Music, L.T("Music"), L.T("Every song and audio file on this phone"), () => BackUpMusic, v => BackUpMusic = v),
            new WholePhoneViewModel(this, BackupSourceKinds.Documents, L.T("Documents"),
                L.T("Every PDF, Word, Excel, PowerPoint, text file and e-book on this phone"), () => BackUpDocuments, v => BackUpDocuments = v),
        };
        node.Backup.Changed += () => Dispatcher.UIThread.Post(Reload);
        node.Settings.Changed += _ => Dispatcher.UIThread.Post(Reload);
        node.Discovery.DeviceFound += (_, _) => Dispatcher.UIThread.Post(RefreshStatus);
        node.Discovery.DeviceLost += (_, _) => Dispatcher.UIThread.Post(RefreshStatus);
        Reload();
    }

    /// <summary>After the owner confirmed it's them, further changes don't ask again for this long.</summary>
    internal static TimeSpan OwnerGrace { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>The backup section is there: computers take backups, phones that can back up make them.</summary>
    public bool IsAvailable => IsComputer || _platform.SupportsBackup;

    public bool IsComputer => !_platform.IsPhone;

    public bool IsPhone => _platform.IsPhone && _platform.SupportsBackup;

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

    /// <summary>Photos, videos, music and documents from the whole phone: a switch each, and their folders.</summary>
    public IReadOnlyList<WholePhoneViewModel> WholePhoneKinds { get; private set; } = Array.Empty<WholePhoneViewModel>();

    /// <summary>Every photo on the phone (its media library).</summary>
    public bool BackUpPhotos
    {
        get => IsOn(BackupSourceKinds.Photos);
        set => SetWholePhoneAsync(BackupSourceKinds.Photos, value).Forget("Changing what is backed up");
    }

    public bool BackUpVideos
    {
        get => IsOn(BackupSourceKinds.Videos);
        set => SetWholePhoneAsync(BackupSourceKinds.Videos, value).Forget("Changing what is backed up");
    }

    public bool BackUpMusic
    {
        get => IsOn(BackupSourceKinds.Music);
        set => SetWholePhoneAsync(BackupSourceKinds.Music, value).Forget("Changing what is backed up");
    }

    /// <summary>Every document on the phone, wherever it is (Android: needs "All files access").</summary>
    public bool BackUpDocuments
    {
        get => IsOn(BackupSourceKinds.Documents);
        set => SetWholePhoneAsync(BackupSourceKinds.Documents, value).Forget("Changing what is backed up");
    }

    /// <summary>The folders chosen one by one (the whole-phone kinds are the switches above).</summary>
    public ObservableCollection<BackupSourceViewModel> Sources { get; } = new();

    public bool HasSources => Sources.Count > 0;

    public bool Automatic
    {
        get => _plan?.Automatic ?? false;
        set
        {
            if (_plan == null || _plan.Automatic == value) return;
            _plan.Automatic = value;
            SaveAsync().Forget("Saving the backup choices");
        }
    }

    /// <summary>On the computer: the phone's folders kept; off, one folder per kind of file (Photos, Videos, Documents…).</summary>
    public bool KeepFolderNames
    {
        get => _plan?.KeepFolderNames ?? false;
        set
        {
            if (_plan == null || _plan.KeepFolderNames == value) return;
            _plan.KeepFolderNames = value;
            SaveAsync().Forget("Saving the backup choices");
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

            if (!_plan.HasAnythingToBackUp) return L.T("Choose what to back up.");
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

    public AsyncCommand AddFolderCommand { get; }

    public AsyncCommand BackUpNowCommand { get; }

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

        // Whoever holds this phone must prove it's theirs: otherwise anyone handed the phone for a minute could send
        // its files to their own computer.
        if (!await EnsureOwnerAsync(L.T("Confirm it's you to back up this phone to {0}.", code.Name))) return;
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

    private bool IsOn(string kind) => _plan?.Sources.Any(s => s.Kind == kind && s.Enabled) == true;

    internal BackupSource? SourceOf(string kind) => _plan?.Sources.FirstOrDefault(s => s.Kind == kind && s.Enabled);

    internal Task<IReadOnlyList<BackupFolderInfo>> ListFoldersAsync(BackupSource source) =>
        _node.Backup.ListFoldersAsync(source.Copy(), CancellationToken.None);

    /// <summary>
    /// "All folders" ticked: everything, also later folders (backs up more: the owner confirms). Unticked: every folder
    /// listed stays ticked and can be changed; later folders wait to be ticked.
    /// </summary>
    internal void SetAllFolders(WholePhoneViewModel kind, bool all)
    {
        if (SourceOf(kind.Kind) is not { } source || source.AllFolders == all) return;
        if (!all)
        {
            if (kind.LoadedPaths is not { } loaded)
            {
                kind.Refresh(); // the list isn't there yet: nothing to keep ticked
                return;
            }

            source.Folders = loaded.ToList();
            source.SeenFolders = source.SeenFolders.Union(loaded).ToList();
        }

        source.AllFolders = all;
        kind.Refresh();
        SaveAsync().Forget("Saving the backup choices");
    }

    internal void SetFolder(WholePhoneViewModel kind, string path, bool ticked)
    {
        if (SourceOf(kind.Kind) is not { AllFolders: false } source || source.Folders.Contains(path) == ticked) return;
        if (ticked) source.Folders.Add(path);
        else source.Folders.Remove(path);
        if (!source.SeenFolders.Contains(path)) source.SeenFolders.Add(path);
        kind.Refresh();
        SaveAsync().Forget("Saving the backup choices");
    }

    /// <summary>The folder list was closed: the folders it showed aren't new any more.</summary>
    internal void MarkSeen(WholePhoneViewModel kind)
    {
        if (SourceOf(kind.Kind) is not { AllFolders: false } source || kind.LoadedPaths is not { } loaded) return;
        var unseen = loaded.Where(f => !source.SeenFolders.Contains(f)).ToList();
        if (unseen.Count == 0) return;
        source.SeenFolders.AddRange(unseen);
        kind.Refresh();
        SaveAsync().Forget("Saving the backup choices");
    }

    private static readonly string[] WholePhoneProperties = { nameof(BackUpPhotos), nameof(BackUpVideos), nameof(BackUpMusic), nameof(BackUpDocuments) };

    /// <summary>
    /// A whole-phone switch: on asks for Android's permission (with Beam's reason first) and, when it backs up more,
    /// the owner's fingerprint, face or screen lock; off never asks, and keeps the folder choices for when it is on
    /// again. What was backed up stays on the computer.
    /// </summary>
    private async Task SetWholePhoneAsync(string kind, bool on)
    {
        if (_plan == null || IsOn(kind) == on) return;
        if (on)
        {
            var permission = kind switch
            {
                BackupSourceKinds.Music => AppPermission.Music,
                BackupSourceKinds.Documents => AppPermission.AllFiles,
                _ => AppPermission.MediaLibrary,
            };
            if (!await _main.EnsurePermissionAsync(permission) || _plan == null)
            {
                foreach (var name in WholePhoneProperties) OnPropertyChanged(name); // the switch goes back off
                foreach (var row in WholePhoneKinds) row.Refresh();
                return;
            }

            // On again: its folder choices as they were left (off only pauses it).
            if (_plan.Sources.FirstOrDefault(s => s.Kind == kind) is { } paused) paused.Enabled = true;
            else _plan.Sources.Add(new BackupSource { Kind = kind, Name = WholePhoneName(kind) });
        }
        else
        {
            foreach (var source in _plan.Sources.Where(s => s.Kind == kind)) source.Enabled = false;
        }

        await SaveAsync();
    }

    /// <summary>Its folder's name on the computer when the phone's folder names are kept.</summary>
    private static string WholePhoneName(string kind) => kind switch
    {
        BackupSourceKinds.Photos => L.T("Photos"),
        BackupSourceKinds.Videos => L.T("Videos"),
        BackupSourceKinds.Music => L.T("Music"),
        _ => L.T("Documents"),
    };

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
            AllTypes = true,
        });
        await SaveAsync();
    }

    private async Task BackUpNowAsync()
    {
        if (_plan == null) return;
        await SaveAsync();
        if (_plan == null) return;
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

    /// <summary>
    /// Saves the choices. Anything that backs up more than before (a new source, other file types, automatic backups
    /// switched on) needs the owner's fingerprint, face or screen lock first; refused, the edits are dropped.
    /// </summary>
    private async Task SaveAsync()
    {
        _saveTimer.Stop();
        if (_plan is not { } plan) return;
        var stored = _node.Settings.Current.Backups.FirstOrDefault(b => b.TargetId == plan.TargetId);
        if (stored == null) return;
        if (Widens(stored, plan) && !await EnsureOwnerAsync(L.T("Confirm it's you to change what this phone backs up.")))
        {
            _plan = null; // back to what is saved
            Reload();
            return;
        }

        _node.Backup.Save(plan);
        Reload();
    }

    private static string SourceKey(BackupSource s) => $"{s.Kind}|{s.Address}|{s.Name}|{s.Enabled}|{s.AllTypes}|{string.Join(",", s.EffectiveTypes())}";

    /// <summary>
    /// Backs up more than before: a new source, a source switched on, a newly ticked kind, or automatic backups switched on.
    /// </summary>
    private static bool Widens(BackupPlan stored, BackupPlan plan)
    {
        if (plan.Automatic && !stored.Automatic) return true;
        foreach (var source in plan.Sources.Where(s => s.Enabled))
        {
            var before = stored.Sources.FirstOrDefault(s => s.Enabled && s.Kind == source.Kind && s.Address == source.Address);
            if (before == null || source.EffectiveTypes().Except(before.EffectiveTypes()).Any()) return true;
            // The whole phone's folders: "All folders" ticked again, or a folder ticked that wasn't.
            if (source.AllFolders && !before.AllFolders) return true;
            if (!source.AllFolders && !before.AllFolders && source.Folders.Except(before.Folders).Any()) return true;
        }

        return false;
    }

    /// <summary>
    /// The phone's owner confirms with the phone's own lock. Not asked again for a short while (setting up and then
    /// choosing what to back up is one confirmation).
    /// </summary>
    private async Task<bool> EnsureOwnerAsync(string reason)
    {
        if (DateTime.UtcNow - _ownerConfirmedAt < OwnerGrace) return true;
        switch (await _platform.ConfirmOwnerAsync(L.T("Confirm it's you"), reason))
        {
            case OwnerCheck.Confirmed:
                break;
            case OwnerCheck.NoScreenLock:
                if (await _main.ShowDialogAsync(new ConfirmViewModel(L.T("This phone has no screen lock"),
                        L.T("Anyone who picks up this phone could back up its files to their own computer. To prevent that, set a screen lock (PIN, pattern, fingerprint or face) in the phone's settings."),
                        L.T("Continue anyway"), L.T("Cancel"))) is not true)
                    return false;
                break;
            default:
                Log.Info("Backup: the owner check was cancelled or failed; nothing changed");
                Message = L.T("Nothing was changed. Confirm with your fingerprint, face or screen lock to change this phone's backup.");
                return false;
        }

        _ownerConfirmedAt = DateTime.UtcNow;
        Message = "";
        return true;
    }

    private void RemoveSource(BackupSourceViewModel source)
    {
        if (_plan == null) return;
        _plan.Sources.Remove(source.Source); // backing up less never needs a confirmation
        SaveAsync().Forget("Saving the backup choices");
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
        var sources = (_plan?.Sources ?? new List<BackupSource>()).Where(s => s.Kind == BackupSourceKinds.Folder).ToList();
        if (!Sources.Select(s => SourceKey(s.Source)).SequenceEqual(sources.Select(SourceKey)))
        {
            // Other choices: new rows, a folder's file types staying open if they were.
            var open = Sources.Where(s => s.ShowsKinds).Select(s => s.Source.Address).ToHashSet();
            Sources.Clear();
            foreach (var source in sources)
            {
                var row = new BackupSourceViewModel(source, OnSourcesEdited, () => SaveAsync().Forget("Saving the backup choices"), RemoveSource);
                if (source.Address != null && open.Contains(source.Address)) row.ToggleKindsCommand.Execute(null);
                Sources.Add(row);
            }

            OnPropertyChanged(nameof(HasSources));
        }
        else
        {
            // Same choices (any other setting was saved): the rows stay as they are, open or closed, and the fresh
            // copy uses their sources.
            for (var i = 0; i < Sources.Count; i++) _plan!.Sources[_plan.Sources.IndexOf(sources[i])] = Sources[i].Source;
        }

        foreach (var name in new[] { nameof(HasPlan), nameof(HasNoPlan), nameof(TargetName), nameof(TargetText), nameof(Automatic), nameof(KeepFolderNames) }.Concat(WholePhoneProperties))
            OnPropertyChanged(name);
        foreach (var kind in WholePhoneKinds) kind.Refresh();
        _platform.ScheduleBackups(settings.Backups.Any(b => b.Automatic && b.HasAnythingToBackUp));
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
