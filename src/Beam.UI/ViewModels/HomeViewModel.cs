using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Threading;
using Beam.App.Infrastructure;
using Beam.App.Services;
using Beam.Core;
using Beam.Core.Diagnostics;
using Beam.Core.Discovery;
using Beam.Core.Licensing;
using Beam.Core.Localization;
using Beam.Core.Util;

namespace Beam.App.ViewModels;

public sealed class DeviceViewModel : ObservableObject
{
    private DeviceInfo _device;
    private bool _isSelected;

    public DeviceViewModel(DeviceInfo device, Action<DeviceViewModel> select)
    {
        _device = device;
        SelectCommand = new RelayCommand(() => select(this));
    }

    public DeviceInfo Device
    {
        get => _device;
        set
        {
            _device = value;
            OnPropertyChanged(nameof(Name));
            OnPropertyChanged(nameof(Subtitle));
            OnPropertyChanged(nameof(Icon));
        }
    }

    public string Id => _device.Id;

    public string Name => _device.Name;

    public string Subtitle
    {
        get
        {
            var platform = _device.Platform switch
            {
                "windows" => "Windows",
                "macos" => "Mac",
                "linux" => "Linux",
                "android" => "Android",
                "ios" => "iPhone",
                _ => L.T("Computer"),
            };
            return _device.IsManual ? $"{platform} · {_device.AddressText}" : L.T("{0} · Nearby", platform);
        }
    }

    public Geometry? Icon => Icons.Get(_device.Kind switch
    {
        DeviceKinds.Laptop => Icons.Laptop,
        DeviceKinds.Phone => Icons.Phone,
        _ => Icons.Desktop,
    });

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public RelayCommand SelectCommand { get; }
}

public sealed class PendingItemViewModel : ObservableObject
{
    private long _size;
    private int _fileCount;
    private bool _isMeasuring;

    public PendingItemViewModel(string path, bool isFolder, Action<PendingItemViewModel> remove, string? name = null)
    {
        Path = path;
        IsFolder = isFolder;
        Name = name ?? System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(path));
        if (string.IsNullOrEmpty(Name)) Name = path;
        RemoveCommand = new RelayCommand(() => remove(this));
    }

    public string Path { get; }

    public string Name { get; }

    public bool IsFolder { get; }

    public Geometry? Icon => Icons.Get(IsFolder ? Icons.Folder : Icons.File);

    public long Size
    {
        get => _size;
        set
        {
            if (SetProperty(ref _size, value)) OnPropertyChanged(nameof(Details));
        }
    }

    public int FileCount
    {
        get => _fileCount;
        set
        {
            if (SetProperty(ref _fileCount, value)) OnPropertyChanged(nameof(Details));
        }
    }

    public bool IsMeasuring
    {
        get => _isMeasuring;
        set
        {
            if (SetProperty(ref _isMeasuring, value)) OnPropertyChanged(nameof(Details));
        }
    }

    public string Details => IsFolder
        ? IsMeasuring
            ? L.Plural(FileCount, "Folder · counting files… {0} file", "Folder · counting files… {0} files")
            : L.Plural(FileCount, "Folder · {0} file · {1}", "Folder · {0} files · {1}", Format.Bytes(Size))
        : Format.Bytes(Size);

    public RelayCommand RemoveCommand { get; }

    internal CancellationTokenSource Measuring { get; } = new();
}

/// <summary>The main screen: nearby computers, the files to send, and the Send button.</summary>
public sealed class HomeViewModel : ObservableObject
{
    private readonly BeamNode _node;
    private readonly IUiServices _ui;
    private readonly MainViewModel _main;
    private readonly DispatcherTimer _searchHintTimer;
    private readonly List<DeviceViewModel> _selected = new();
    private bool _selectionIsAutomatic;
    private bool _showSearchHint;

    public HomeViewModel(BeamNode node, IUiServices ui, MainViewModel main)
    {
        _node = node;
        _ui = ui;
        _main = main;

        ChooseFilesCommand = new AsyncCommand(ChooseFilesAsync);
        ChooseFolderCommand = new AsyncCommand(ChooseFolderAsync);
        ClearCommand = new RelayCommand(ClearItems, () => Items.Count > 0);
        SendCommand = new AsyncCommand(SendAsync, () => CanSend);
        UpgradeCommand = new AsyncCommand(() => _main.ShowUpgradeAsync());
        SendTextCommand = new AsyncCommand(SendTextAsync, () => _selected.Count > 0);
        RefreshCommand = new RelayCommand(Refresh);
        ConnectByAddressCommand = new AsyncCommand(ConnectByAddressAsync);
        EditNameCommand = new RelayCommand(() => _main.Navigate(Page.Settings));

        Items.CollectionChanged += (_, _) => OnItemsChanged();
        _searchHintTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        _searchHintTimer.Tick += (_, _) =>
        {
            _searchHintTimer.Stop();
            ShowSearchHint = Devices.Count == 0;
        };
        _searchHintTimer.Start();

        foreach (var device in node.Discovery.Devices) AddOrUpdateDevice(device);
        node.Discovery.DeviceFound += (_, e) => Dispatcher.UIThread.Post(() => AddOrUpdateDevice(e.Device));
        node.Discovery.DeviceChanged += (_, e) => Dispatcher.UIThread.Post(() => AddOrUpdateDevice(e.Device));
        node.Discovery.DeviceLost += (_, e) => Dispatcher.UIThread.Post(() => RemoveDevice(e.Device.Id));
        node.Settings.Changed += _ => Dispatcher.UIThread.Post(() =>
        {
            OnPropertyChanged(nameof(LocalDeviceName));
            OnPropertyChanged(nameof(IsDiscoverable));
            OnPropertyChanged(nameof(VisibilityText));
        });
    }

    public ObservableCollection<DeviceViewModel> Devices { get; } = new();

    public ObservableCollection<PendingItemViewModel> Items { get; } = new();

    /// <summary>Folders, drag and drop: desktop only.</summary>
    public bool IsDesktop => !_main.IsPhone;

    public string LocalDeviceName => _node.Settings.Current.DeviceName;

    public bool IsDiscoverable => _node.Settings.Current.Discoverable;

    public string VisibilityText => IsDiscoverable ? L.T("Visible to nearby computers") : L.T("Hidden from nearby computers");

    public bool HasDevices => Devices.Count > 0;

    public bool HasNoDevices => Devices.Count == 0;

    public bool DiscoveryUnavailable => _node.Discovery.Problem != null;

    public string DiscoveryProblem => _node.Discovery.Problem ?? "";

    public bool ShowSearchHint
    {
        get => _showSearchHint;
        private set => SetProperty(ref _showSearchHint, value);
    }

    /// <summary>The first chosen computer (the only one in the free edition).</summary>
    public DeviceViewModel? SelectedDevice => _selected.FirstOrDefault();

    /// <summary>The chosen computers, in the order they were picked.</summary>
    public IReadOnlyList<DeviceViewModel> SelectedDevices => _selected;

    public bool IsPro => _node.Edition.IsPro;

    public bool IsFree => !IsPro;

    /// <summary>The free edition's limits apply (not Pro, and not during the free launch period).</summary>
    public bool ShowFreeLimits => !_node.Edition.IsEnabled(Feature.UnlimitedSends);

    public bool CanSelectSeveral => _node.Edition.IsEnabled(Feature.SendToSeveralDevices);

    /// <summary>Free edition with several computers around: mention that Pro can send to all of them.</summary>
    public bool ShowMultiSendUpsell => !CanSelectSeveral && Devices.Count > 1;

    public string DeviceHint => CanSelectSeveral && Devices.Count > 1 ? L.T("Choose one or more computers.") : "";

    public bool HasDeviceHint => DeviceHint.Length > 0;

    /// <summary>The free edition's limits, shown next to the Send button.</summary>
    public string PlanText
    {
        get
        {
            var left = _node.Quota.RemainingToday ?? 0;
            var speed = Format.Bytes(FreeLimits.MaxSendBytesPerSecond);
            var limit = _node.Quota.Limit;
            return left == 0
                ? L.T("Free · up to {0}/s · no free sends left today", speed)
                : L.Plural(limit, "Free · up to {1}/s · {2} of {0} free send left today", "Free · up to {1}/s · {2} of {0} free sends left today", speed, left);
        }
    }

    public bool HasItems => Items.Count > 0;

    public bool HasNoItems => Items.Count == 0;

    public long TotalBytes => Items.Sum(i => i.Size);

    public int TotalFiles => Items.Sum(i => i.IsFolder ? i.FileCount : 1);

    public bool IsMeasuring => Items.Any(i => i.IsMeasuring);

    public string ItemsSummary
    {
        get
        {
            var folders = Items.Count(i => i.IsFolder);
            var files = Items.Count - folders;
            var parts = new List<string>();
            if (folders > 0) parts.Add(L.Plural(folders, "{0} folder", "{0} folders"));
            if (files > 0) parts.Add(L.Plural(files, "{0} file", "{0} files"));
            var size = IsMeasuring ? L.T("calculating size…") : Format.Bytes(TotalBytes);
            var contents = string.Join(", ", parts);
            return folders > 0 && !IsMeasuring
                ? L.Plural(TotalFiles, "{1} · {0} file in total · {2}", "{1} · {0} files in total · {2}", contents, size)
                : $"{contents} · {size}";
        }
    }

    public bool CanSend => _selected.Count > 0 && Items.Count > 0;

    public string SendSummary
    {
        get
        {
            if (Items.Count == 0 && _selected.Count == 0) return L.T("Choose a computer and add files to send.");
            if (Items.Count == 0) return L.T("Add files or folders to send to {0}.", TargetsText);
            if (_selected.Count == 0) return L.T("Choose a computer to send to.");
            if (Items.Count == 1)
            {
                return IsMeasuring
                    ? L.T("Send “{0}” to {1}", Items[0].Name, TargetsText)
                    : L.T("Send “{0}” ({1}) to {2}", Items[0].Name, Format.Bytes(TotalBytes), TargetsText);
            }

            return IsMeasuring
                ? L.Plural(Items.Count, "Send {0} item to {1}", "Send {0} items to {1}", TargetsText)
                : L.Plural(Items.Count, "Send {0} item ({1}) to {2}", "Send {0} items ({1}) to {2}", Format.Bytes(TotalBytes), TargetsText);
        }
    }

    private string TargetsText => _selected.Count switch
    {
        1 => _selected[0].Name,
        2 => L.T("{0} and {1}", _selected[0].Name, _selected[1].Name),
        _ => L.Plural(_selected.Count, "{0} computer", "{0} computers"),
    };

    public AsyncCommand ChooseFilesCommand { get; }

    public AsyncCommand ChooseFolderCommand { get; }

    public RelayCommand ClearCommand { get; }

    public AsyncCommand SendCommand { get; }

    public AsyncCommand UpgradeCommand { get; }

    /// <summary>Opens the "send text" box for the chosen computers (pre-filled with copied text, if any).</summary>
    public AsyncCommand SendTextCommand { get; }

    public RelayCommand RefreshCommand { get; }

    public AsyncCommand ConnectByAddressCommand { get; }

    public RelayCommand EditNameCommand { get; }

    /// <summary>Adds dropped/picked/command-line paths to the send list.</summary>
    public void AddPaths(IEnumerable<string?> paths)
    {
        foreach (var raw in paths)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            if (_node.Transfers.ExternalFiles?.TryGet(raw, out var external) == true)
            {
                // A file the platform handed over as a stream (Android content:// URI).
                if (Items.Any(i => i.Path == raw)) continue;
                Items.Add(new PendingItemViewModel(raw, isFolder: false, RemoveItem, external.Name) { Size = external.Size, FileCount = 1 });
                continue;
            }

            string path;
            try
            {
                path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(raw));
            }
            catch
            {
                continue;
            }

            if (Items.Any(i => string.Equals(i.Path, path, OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase)))
                continue;

            if (Directory.Exists(path))
            {
                var item = new PendingItemViewModel(path, isFolder: true, RemoveItem) { IsMeasuring = true };
                Items.Add(item);
                _ = MeasureFolderAsync(item);
            }
            else if (File.Exists(path))
            {
                long size = 0;
                try { size = new FileInfo(path).Length; } catch { /* shown as 0 */ }
                Items.Add(new PendingItemViewModel(path, isFolder: false, RemoveItem) { Size = size, FileCount = 1 });
            }
        }
    }

    /// <summary>Clicking a computer toggles it. In the free edition, picking another one replaces the choice.</summary>
    public void SelectDevice(DeviceViewModel device)
    {
        _selectionIsAutomatic = false;
        if (_selected.Contains(device))
        {
            Deselect(device);
            return;
        }

        if (!CanSelectSeveral) ClearSelection();
        device.IsSelected = true;
        _selected.Add(device);
        OnSelectionChanged();
    }

    /// <summary>Called when the edition or the daily count changes.</summary>
    internal void OnEditionChanged()
    {
        if (!CanSelectSeveral)
        {
            foreach (var extra in _selected.Skip(1).ToList()) Deselect(extra);
        }

        foreach (var name in new[] { nameof(IsPro), nameof(IsFree), nameof(ShowFreeLimits), nameof(CanSelectSeveral), nameof(ShowMultiSendUpsell), nameof(DeviceHint), nameof(HasDeviceHint), nameof(PlanText) })
            OnPropertyChanged(name);
    }

    private void SelectOnly(DeviceViewModel? device)
    {
        ClearSelection();
        if (device != null)
        {
            device.IsSelected = true;
            _selected.Add(device);
        }

        OnSelectionChanged();
    }

    private void Deselect(DeviceViewModel device)
    {
        device.IsSelected = false;
        _selected.Remove(device);
        OnSelectionChanged();
    }

    private void ClearSelection()
    {
        foreach (var device in _selected) device.IsSelected = false;
        _selected.Clear();
    }

    private void OnSelectionChanged()
    {
        SendTextCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(SelectedDevice));
        OnPropertyChanged(nameof(SelectedDevices));
        UpdateSendState();
    }

    private void AddOrUpdateDevice(DeviceInfo device)
    {
        var existing = Devices.FirstOrDefault(d => d.Id == device.Id);
        if (existing != null)
        {
            existing.Device = device;
        }
        else
        {
            var index = 0;
            while (index < Devices.Count && string.Compare(Devices[index].Name, device.Name, StringComparison.CurrentCultureIgnoreCase) < 0) index++;
            Devices.Insert(index, new DeviceViewModel(device, SelectDevice));
            _main.OnDeviceAppeared(device);
        }

        // With exactly one other computer the choice is obvious, so pre-select it. Once more appear,
        // drop a selection the user didn't make so files never go to a computer they didn't pick.
        if (Devices.Count == 1 && _selected.Count == 0)
        {
            SelectOnly(Devices[0]);
            _selectionIsAutomatic = true;
        }
        else if (Devices.Count > 1 && _selectionIsAutomatic)
        {
            SelectOnly(null);
            _selectionIsAutomatic = false;
        }

        OnDevicesChanged();
    }

    private void RemoveDevice(string id)
    {
        var existing = Devices.FirstOrDefault(d => d.Id == id);
        if (existing == null) return;
        Devices.Remove(existing);
        if (_selected.Contains(existing)) Deselect(existing);
        OnDevicesChanged();
    }

    private void OnDevicesChanged()
    {
        OnPropertyChanged(nameof(HasDevices));
        OnPropertyChanged(nameof(HasNoDevices));
        OnPropertyChanged(nameof(ShowMultiSendUpsell));
        OnPropertyChanged(nameof(DeviceHint));
        OnPropertyChanged(nameof(HasDeviceHint));
        if (Devices.Count > 0) ShowSearchHint = false;
        else if (!_searchHintTimer.IsEnabled) _searchHintTimer.Start();
    }

    private void RemoveItem(PendingItemViewModel item)
    {
        item.Measuring.Cancel();
        Items.Remove(item);
    }

    private void ClearItems()
    {
        foreach (var item in Items) item.Measuring.Cancel();
        Items.Clear();
    }

    private void OnItemsChanged()
    {
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(HasNoItems));
        OnPropertyChanged(nameof(TotalBytes));
        OnPropertyChanged(nameof(TotalFiles));
        OnPropertyChanged(nameof(IsMeasuring));
        OnPropertyChanged(nameof(ItemsSummary));
        ClearCommand.RaiseCanExecuteChanged();
        UpdateSendState();
    }

    private void UpdateSendState()
    {
        OnPropertyChanged(nameof(CanSend));
        OnPropertyChanged(nameof(SendSummary));
        SendCommand.RaiseCanExecuteChanged();
    }

    private async Task MeasureFolderAsync(PendingItemViewModel item)
    {
        var token = item.Measuring.Token;
        long size = 0;
        var files = 0;
        try
        {
            await Task.Run(() =>
            {
                var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
                var lastReport = Environment.TickCount64;
                foreach (var file in new DirectoryInfo(item.Path).EnumerateFiles("*", options))
                {
                    token.ThrowIfCancellationRequested();
                    try { size += file.Length; } catch { /* unreadable: skipped when sending too */ }
                    files++;
                    if (Environment.TickCount64 - lastReport > 250)
                    {
                        lastReport = Environment.TickCount64;
                        var (s, f) = (size, files);
                        Dispatcher.UIThread.Post(() =>
                        {
                            item.Size = s;
                            item.FileCount = f;
                        });
                    }
                }
            }, token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not measure {item.Path}: {ex.Message}");
        }

        item.Size = size;
        item.FileCount = files;
        item.IsMeasuring = false;
        OnItemsChanged();
    }

    private async Task ChooseFilesAsync() => AddPaths(await _ui.PickFilesAsync());

    private async Task ChooseFolderAsync() => AddPaths(await _ui.PickFoldersAsync());

    private void Refresh()
    {
        _node.Discovery.Refresh();
        ShowSearchHint = false;
        if (Devices.Count == 0)
        {
            _searchHintTimer.Stop();
            _searchHintTimer.Start();
        }
    }

    private async Task ConnectByAddressAsync()
    {
        var device = await _main.ConnectByAddressAsync();
        if (device == null) return;
        var vm = Devices.FirstOrDefault(d => d.Id == device.Id);
        if (vm == null)
        {
            AddOrUpdateDevice(device);
            vm = Devices.FirstOrDefault(d => d.Id == device.Id);
        }

        if (vm != null && !vm.IsSelected) SelectDevice(vm);
    }

    private async Task SendTextAsync()
    {
        if (_selected.Count == 0) return;
        var targets = _selected.Select(d => d.Device).ToList();
        var clip = await _ui.GetClipboardTextAsync();
        var initial = clip is { Length: > 0 and <= 2000 } ? clip : "";
        if (await _main.ShowDialogAsync(new Dialogs.SendTextViewModel(TargetsText, initial, _ui)) is not string text) return;
        if (!_node.Quota.CanSend(targets.Count))
        {
            if (!await _main.ShowUpgradeAsync(L.Plural(FreeLimits.SendsPerDay, "You've used today's {0} free send. Upgrade for unlimited sends, or send again tomorrow.", "You've used today's {0} free sends. Upgrade for unlimited sends, or send again tomorrow."))) return;
        }

        foreach (var target in targets) await _main.StartSendTextAsync(target, text);
    }

    private async Task SendAsync()
    {
        if (!CanSend) return;
        var targets = _selected.Select(d => d.Device).ToList();
        var paths = Items.Select(i => i.Path).ToList();
        if (!_node.Quota.CanSend(targets.Count))
        {
            var left = _node.Quota.RemainingToday ?? 0;
            var reason = left == 0
                ? L.Plural(FreeLimits.SendsPerDay, "You've used today's {0} free send. Upgrade for unlimited sends, or send again tomorrow.", "You've used today's {0} free sends. Upgrade for unlimited sends, or send again tomorrow.")
                : L.Plural(left, "You have {0} free send left today, which isn't enough for {1} computers.", "You have {0} free sends left today, which isn't enough for {1} computers.", targets.Count);

            if (!await _main.ShowUpgradeAsync(reason)) return;
        }

        var started = false;
        foreach (var target in targets) started |= await _main.StartSendAsync(target, paths);
        if (started) ClearItems();
    }
}
