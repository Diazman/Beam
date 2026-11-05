using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Beam.App.Infrastructure;
using Beam.App.Services;
using Beam.Core;
using Beam.Core.Browse;
using Beam.Core.Diagnostics;
using Beam.Core.Discovery;
using Beam.Core.Localization;
using Beam.Core.Transfer;
using Beam.Core.Util;

namespace Beam.App.ViewModels;

/// <summary>A file or folder in the list (or, at the top, one of the shared folders).</summary>
public sealed class BrowseEntryViewModel : ObservableObject
{
    private static readonly HashSet<string> Previewable = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".heic", ".heif", ".bmp", ".mp4", ".mov", ".m4v", ".3gp", ".mkv", ".webm",
    };

    private bool _isChecked;
    private Bitmap? _thumbnail;

    public BrowseEntryViewModel(BrowseItem item, Action<BrowseEntryViewModel> open, Action changed)
    {
        Item = item;
        OpenCommand = new RelayCommand(() => open(this));
        _changed = changed;
    }

    private readonly Action _changed;

    public BrowseItem Item { get; }

    public string Name => Item.Name;

    public bool IsFolder => Item.IsFolder;

    /// <summary>A shared folder at the top (can't be downloaded whole from there: open it first).</summary>
    public bool IsRoot => Item.Path.Length == 0;

    public bool CanCheck => !IsRoot;

    public string Details => IsRoot ? L.T("Shared folder")
        : IsFolder ? L.T("Folder")
        : $"{Format.Bytes(Item.Size)} · {Item.Modified.LocalDateTime:g}";

    public Geometry? Icon => Icons.Get(IsFolder ? Icons.Folder : CanPreview ? "IconImage" : Icons.File);

    internal bool CanPreview => !IsFolder && Previewable.Contains(System.IO.Path.GetExtension(Item.Name));

    public Bitmap? Thumbnail
    {
        get => _thumbnail;
        internal set
        {
            if (SetProperty(ref _thumbnail, value)) OnPropertyChanged(nameof(HasThumbnail));
        }
    }

    public bool HasThumbnail => _thumbnail != null;

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (SetProperty(ref _isChecked, value && CanCheck)) _changed();
        }
    }

    public RelayCommand OpenCommand { get; }
}

/// <summary>One step of the path shown above the list ("Shared folders › Photos › Trip").</summary>
public sealed class BrowseCrumbViewModel
{
    public BrowseCrumbViewModel(string name, int depth, Action<int> go, bool isLast)
    {
        Name = name;
        IsLast = isLast;
        GoCommand = new RelayCommand(() => go(depth));
    }

    public string Name { get; }

    public bool IsLast { get; }

    public bool IsNotLast => !IsLast;

    public RelayCommand GoCommand { get; }
}

/// <summary>
/// Browsing a trusted device's shared folders: the folders it shares, what is in them, downloading chosen files and
/// folders, and adding files when it allows that. Opened from Send (a trusted device) or Settings → Trusted devices.
/// </summary>
public sealed class BrowseViewModel : ObservableObject, IAsyncDisposable
{
    private readonly BeamNode _node;
    private readonly IUiServices _ui;
    private readonly MainViewModel _main;
    private readonly DeviceInfo _device;
    private readonly CancellationTokenSource _closed = new();
    private BrowseSession? _session;
    private CancellationTokenSource? _listing;
    private string? _root;
    private string _rootName = "";
    private string _path = "";
    private bool _isConnecting = true;
    private bool _isLoading;
    private string _error = "";
    private string _status = "";

    public BrowseViewModel(BeamNode node, IUiServices ui, MainViewModel main, DeviceInfo device)
    {
        _node = node;
        _ui = ui;
        _main = main;
        _device = device;
        Title = L.T("{0}'s files", device.Name);
        BackCommand = new RelayCommand(GoUp);
        CloseCommand = new RelayCommand(() => _main.CloseBrowse());
        RetryCommand = new AsyncCommand(ConnectAsync);
        DownloadCommand = new RelayCommand(Download, () => SelectedCount > 0);
        AddFilesCommand = new AsyncCommand(AddFilesAsync, () => CanAddHere);
        ClearSelectionCommand = new RelayCommand(ClearSelection);
    }

    public string Title { get; }

    public string DeviceName => _session?.Name ?? _device.Name;

    public ObservableCollection<BrowseEntryViewModel> Entries { get; } = new();

    public ObservableCollection<BrowseCrumbViewModel> Crumbs { get; } = new();

    public bool IsConnecting
    {
        get => _isConnecting;
        private set
        {
            if (SetProperty(ref _isConnecting, value)) RefreshState();
        }
    }

    public string ConnectingText => L.T("Connecting to {0}… It may ask there whether to allow this.", _device.Name);

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetProperty(ref _isLoading, value);
    }

    public string Error
    {
        get => _error;
        private set
        {
            if (SetProperty(ref _error, value)) RefreshState();
        }
    }

    public bool HasError => Error.Length > 0;

    public bool IsReady => !IsConnecting && !HasError;

    public bool IsEmpty => IsReady && !IsLoading && Entries.Count == 0;

    public string EmptyText => _root == null ? L.T("{0} doesn't share any folders.", DeviceName) : L.T("This folder is empty.");

    /// <summary>How it is connected: the shared Wi-Fi, a direct link, or reconnecting.</summary>
    public string ConnectionText => _session?.Path switch
    {
        TransferPath.Network => L.T("Connected over Wi-Fi"),
        TransferPath.Direct => L.T("Connected directly"),
        _ => IsConnecting ? "" : L.T("Reconnecting…"),
    };

    /// <summary>What happened last ("Downloading 3 items to Downloads").</summary>
    public string Status
    {
        get => _status;
        private set
        {
            if (SetProperty(ref _status, value)) OnPropertyChanged(nameof(HasStatus));
        }
    }

    public bool HasStatus => Status.Length > 0;

    public bool CanGoUp => _root != null;

    public int SelectedCount => Entries.Count(e => e.IsChecked);

    public bool HasSelection => SelectedCount > 0;

    public string SelectionText => L.Plural(SelectedCount, "{0} selected", "{0} selected");

    /// <summary>The other device allows adding files, and a folder of it is open.</summary>
    public bool CanAddHere => _session is { CanAdd: true } && _root != null && IsReady;

    public bool ShowsBottomBar => IsReady && (HasSelection || CanAddHere);

    public RelayCommand BackCommand { get; }

    public RelayCommand CloseCommand { get; }

    public AsyncCommand RetryCommand { get; }

    public RelayCommand DownloadCommand { get; }

    public AsyncCommand AddFilesCommand { get; }

    public RelayCommand ClearSelectionCommand { get; }

    /// <summary>Opens the visit (the other device may ask its user first).</summary>
    public async Task ConnectAsync()
    {
        Error = "";
        IsConnecting = true;
        try
        {
            if (_session == null)
            {
                _session = await _node.Browse.OpenAsync(_device, _closed.Token);
                _session.Changed += () => Dispatcher.UIThread.Post(() =>
                {
                    OnPropertyChanged(nameof(ConnectionText));
                    OnPropertyChanged(nameof(DeviceName));
                    RefreshState();
                });
            }

            IsConnecting = false;
            await ShowAsync(_root, _rootName, _path);
        }
        catch (OperationCanceledException) when (_closed.IsCancellationRequested)
        {
            // left the page
        }
        catch (Exception ex)
        {
            Log.Info($"Browse: couldn't open {_device.Name} ({ex.GetType().Name})");
            IsConnecting = false;
            Error = Message(ex);
        }
    }

    private static string Message(Exception ex) => ex is TransferException te
        ? te.Error.Message
        : L.T("Something went wrong. Try again; if it keeps happening, send the log from Settings.");

    /// <summary>Shows the shared folders (no root), or a folder in one.</summary>
    private async Task ShowAsync(string? root, string rootName, string path)
    {
        if (_session == null) return;
        _listing?.Cancel();
        var listing = _listing = CancellationTokenSource.CreateLinkedTokenSource(_closed.Token);
        IsLoading = true;
        Status = "";
        try
        {
            IReadOnlyList<BrowseItem> items = root == null
                ? _session.Roots.Select(r => new BrowseItem(r.Id, "", r.Name, true, 0, default)).ToList()
                : await _session.ListAsync(root, path, listing.Token);
            if (listing.IsCancellationRequested) return;
            _root = root;
            _rootName = rootName;
            _path = path;
            Entries.Clear();
            foreach (var item in items) Entries.Add(new BrowseEntryViewModel(item, Open, OnSelectionChanged));
            BuildCrumbs();
            Error = "";
            LoadThumbnailsAsync(Entries.Where(e => e.CanPreview).ToList(), listing.Token).Forget("Loading previews");
        }
        catch (OperationCanceledException) when (listing.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Log.Info($"Browse: couldn't list a folder of {_device.Name} ({ex.GetType().Name})");
            Status = Message(ex);
        }
        finally
        {
            if (_listing == listing) IsLoading = false;
            RefreshState();
        }
    }

    private void BuildCrumbs()
    {
        Crumbs.Clear();
        var parts = new List<string> { L.T("Shared folders") };
        if (_root != null)
        {
            parts.Add(_rootName);
            if (_path.Length > 0) parts.AddRange(_path.Split('/'));
        }

        for (var i = 0; i < parts.Count; i++) Crumbs.Add(new BrowseCrumbViewModel(parts[i], i, GoTo, i == parts.Count - 1));
    }

    /// <summary>A step of the path: 0 is the shared folders, 1 the shared folder, then its subfolders.</summary>
    private void GoTo(int depth)
    {
        if (depth == 0) ShowAsync(null, "", "").Forget("Browsing");
        else if (_root != null)
        {
            var path = string.Join('/', _path.Split('/', StringSplitOptions.RemoveEmptyEntries).Take(depth - 1));
            ShowAsync(_root, _rootName, path).Forget("Browsing");
        }
    }

    private void GoUp()
    {
        if (_root == null) _main.CloseBrowse();
        else GoTo(Crumbs.Count - 2);
    }

    /// <summary>The phone's Back button: up a folder; false at the top (the page then closes).</summary>
    public bool GoBack()
    {
        if (_root == null) return false;
        GoUp();
        return true;
    }

    private void Open(BrowseEntryViewModel entry)
    {
        if (entry.IsRoot) ShowAsync(entry.Item.Root, entry.Name, "").Forget("Browsing");
        else if (entry.IsFolder) ShowAsync(_root, _rootName, entry.Item.Path).Forget("Browsing");
        else entry.IsChecked = !entry.IsChecked; // a file: tapping it selects it
    }

    private async Task LoadThumbnailsAsync(IReadOnlyList<BrowseEntryViewModel> entries, CancellationToken token)
    {
        foreach (var entry in entries.Take(200))
        {
            if (token.IsCancellationRequested || _session == null) return;
            try
            {
                var bytes = await _session.ThumbnailAsync(entry.Item, 160, token);
                if (bytes == null) continue;
                using var stream = new MemoryStream(bytes);
                entry.Thumbnail = Bitmap.DecodeToWidth(stream, 96);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Info($"Browse: no preview ({ex.GetType().Name})");
                if (ex is TransferException) return; // the connection is gone: no more tries now
            }
        }
    }

    private void OnSelectionChanged()
    {
        DownloadCommand.RaiseCanExecuteChanged();
        RefreshState();
    }

    private void ClearSelection()
    {
        foreach (var entry in Entries) entry.IsChecked = false;
    }

    private void Download()
    {
        if (_session == null) return;
        var items = Entries.Where(e => e.IsChecked).Select(e => e.Item).ToList();
        if (items.Count == 0) return;
        _session.Download(items);
        ClearSelection();
        Status = L.Plural(items.Count, "Downloading {0} item to {1}. See Transfers.", "Downloading {0} items to {1}. See Transfers.",
            Path.GetFileName(Path.TrimEndingDirectorySeparator(_node.Settings.Current.EffectiveReceiveFolder)));
    }

    private async Task AddFilesAsync()
    {
        if (_session == null || _root == null) return;
        var files = await _ui.PickFilesAsync();
        if (files.Count == 0) return;
        var root = _root;
        var rootName = _rootName;
        var path = _path;
        var upload = _session.Upload(root, path, files);
        Status = L.Plural(files.Count, "Adding {0} file to this folder. See Transfers.", "Adding {0} files to this folder. See Transfers.");
        upload.StateChanged += _ => Dispatcher.UIThread.Post(() =>
        {
            // Done: show them here if this folder is still open.
            if (upload.IsFinished && _root == root && _path == path) ShowAsync(root, rootName, path).Forget("Browsing");
        });
    }

    private void RefreshState()
    {
        foreach (var name in new[]
                 {
                     nameof(HasError), nameof(IsReady), nameof(IsEmpty), nameof(EmptyText), nameof(CanGoUp), nameof(SelectedCount),
                     nameof(HasSelection), nameof(SelectionText), nameof(CanAddHere), nameof(ShowsBottomBar), nameof(ConnectionText),
                 })
            OnPropertyChanged(name);
        AddFilesCommand.RaiseCanExecuteChanged();
    }

    /// <summary>Leaving the page ends the visit (downloads already started carry on).</summary>
    public async ValueTask DisposeAsync()
    {
        _closed.Cancel();
        if (_session != null) await _session.DisposeAsync();
        _session = null;
    }
}
