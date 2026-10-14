using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Threading;
using Beam.App.Infrastructure;
using Beam.App.Services;
using Beam.Core;
using Beam.Core.Diagnostics;
using Beam.Core.Discovery;
using Beam.Core.Localization;
using Beam.Core.Util;

namespace Beam.App.ViewModels;

public sealed class DeviceViewModel : ObservableObject
{
    private DeviceInfo _device;
    private bool _isSelected;

    private readonly Func<DeviceInfo, DeviceChannels>? _channels;

    public DeviceViewModel(DeviceInfo device, Action<DeviceViewModel> select, Func<DeviceInfo, DeviceChannels>? channels = null)
    {
        _device = device;
        _channels = channels;
        SelectCommand = new RelayCommand(() => select(this));
    }

    /// <summary>The direct link changed somewhere: update the subtitle.</summary>
    public void RefreshDirect() => OnPropertyChanged(nameof(Subtitle));

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
                Beam.Core.Nearby.NearbyService.BluetoothPlatform => _device.Kind == DeviceKinds.Phone ? L.T("Phone") : L.T("Computer"),
                _ => L.T("Computer"),
            };
            if (_device.IsManual && _device.Endpoints.Count > 0 && _device.Platform != Beam.Core.Nearby.NearbyService.BluetoothPlatform
                && _channels?.Invoke(_device) is not { Bluetooth: true }) return $"{platform} · {_device.AddressText}";
            // One entry per device, whichever way it was found; how it can be reached is shown separately.
            return _channels?.Invoke(_device) switch
            {
                { LocalNetwork: true, Bluetooth: true } => L.T("{0} · Found over local network and Bluetooth", platform),
                { LocalNetwork: true } => L.T("{0} · Found over local network", platform),
                { Bluetooth: true } => L.T("{0} · Found over Bluetooth", platform),
                _ => L.T("{0} · Nearby", platform),
            };
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
    private string? _sharedText;
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
        ClearSharedTextCommand = new RelayCommand(() => SharedText = null);
        SendCommand = new AsyncCommand(SendAsync, () => CanSend);
        SendTextCommand = new AsyncCommand(SendTextAsync, () => _selected.Count > 0);
        RefreshCommand = new RelayCommand(Refresh);
        ScanCodeCommand = new AsyncCommand(ScanCodeAsync);
        ConnectByAddressCommand = new AsyncCommand(ConnectByAddressAsync);
        EditNameCommand = new RelayCommand(() => _main.Navigate(Page.Settings));
        AllowBluetoothCommand = new AsyncCommand(async () => await _node.Nearby.RequestPermissionAsync());
        node.Nearby.Changed += () => Dispatcher.UIThread.Post(() =>
        {
            OnPropertyChanged(nameof(HasBluetoothNotice));
            OnPropertyChanged(nameof(BluetoothNotice));
            OnPropertyChanged(nameof(BluetoothNeedsPermission));
            OnPropertyChanged(nameof(DiscoveryStatus));
            OnPropertyChanged(nameof(HasDiscoveryStatus));
            foreach (var device in Devices) device.RefreshDirect();
        });

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
        node.Direct.Changed += () => Dispatcher.UIThread.Post(() =>
        {
            foreach (var device in Devices) device.RefreshDirect();
        });
        node.Settings.Changed += _ => Dispatcher.UIThread.Post(() =>
        {
            OnPropertyChanged(nameof(LocalDeviceName));
            OnPropertyChanged(nameof(IsDiscoverable));
            OnPropertyChanged(nameof(VisibilityText));
            OnPropertyChanged(nameof(PreferDirect));
            OnPropertyChanged(nameof(DiscoveryStatus));
            OnPropertyChanged(nameof(HasDiscoveryStatus));
        });
    }

    public ObservableCollection<DeviceViewModel> Devices { get; } = new();

    public ObservableCollection<PendingItemViewModel> Items { get; } = new();

    /// <summary>Folders, drag and drop: desktop only.</summary>
    public bool IsDesktop => !_main.IsPhone;

    public string LocalDeviceName => _node.Settings.Current.DeviceName;

    public bool IsDiscoverable => _node.Settings.Current.Discoverable;

    public string VisibilityText => IsDiscoverable ? L.T("Visible to nearby devices") : L.T("Hidden from nearby devices");

    public bool HasDevices => Devices.Count > 0;

    public bool HasNoDevices => Devices.Count == 0;

    /// <summary>
    /// Send directly (the Connection setting): pressing Send starts this device's own direct Wi-Fi link, the chosen
    /// receivers join it, and the files go only over it. Off: over the Wi-Fi network, as always.
    /// </summary>
    public bool PreferDirect
    {
        get => _node.Settings.Current.Connection == Beam.Core.Settings.ConnectionMethod.Direct;
        set => _ = SetPreferDirectAsync(value);
    }

    public bool CanConnectDirectly => _node.Direct.CanHost;

    /// <summary>What the Send page is doing to find receivers ("Searching over Bluetooth and the local network…").</summary>
    public string DiscoveryStatus
    {
        get
        {
            if (!PreferDirect) return "";
            var bluetooth = _node.Nearby.State == Beam.Core.Nearby.NearbyRadioState.Ready;
            return bluetooth ? L.T("Searching over Bluetooth and the local network…") : L.T("Searching over the local network…");
        }
    }

    public bool HasDiscoveryStatus => DiscoveryStatus.Length > 0;

    private async Task SetPreferDirectAsync(bool value)
    {
        if (value && _main.IsPhone && !await _main.EnsurePermissionAsync(Platform.AppPermission.NearbyDevices))
        {
            OnPropertyChanged(nameof(PreferDirect));
            return;
        }

        _node.Settings.Update(s => s.Connection = value ? Beam.Core.Settings.ConnectionMethod.Direct : Beam.Core.Settings.ConnectionMethod.SameNetwork);
    }

    /// <summary>Bluetooth is off or not allowed: devices that aren't on this Wi-Fi can't be found.</summary>
    public bool HasBluetoothNotice => _node.Nearby.State is Beam.Core.Nearby.NearbyRadioState.Off or Beam.Core.Nearby.NearbyRadioState.NeedsPermission;

    public bool BluetoothNeedsPermission => _node.Nearby.State == Beam.Core.Nearby.NearbyRadioState.NeedsPermission;

    public string BluetoothNotice => _node.Nearby.State == Beam.Core.Nearby.NearbyRadioState.NeedsPermission
        ? L.T("Allow Nearby devices to also find devices that aren't on your Wi-Fi (they need Receive open).")
        : L.T("Turn on Bluetooth to also find devices that aren't on your Wi-Fi (they need Receive open).");

    public AsyncCommand AllowBluetoothCommand { get; }

    public bool DiscoveryUnavailable => _node.Discovery.Problem != null;

    public string DiscoveryProblem => _node.Discovery.Problem ?? "";

    public bool ShowSearchHint
    {
        get => _showSearchHint;
        private set => SetProperty(ref _showSearchHint, value);
    }

    /// <summary>The first chosen device.</summary>
    public DeviceViewModel? SelectedDevice => _selected.FirstOrDefault();

    /// <summary>The chosen computers, in the order they were picked.</summary>
    public IReadOnlyList<DeviceViewModel> SelectedDevices => _selected;

    public string DeviceHint => Devices.Count > 1 ? L.T("Choose one or more devices.") : "";

    public bool HasDeviceHint => DeviceHint.Length > 0;

    public bool HasItems => Items.Count > 0;

    public bool HasNoItems => Items.Count == 0;

    /// <summary>Text or a link shared to Beam from another app; Send sends it (after any files).</summary>
    public string? SharedText
    {
        get => _sharedText;
        set
        {
            if (!SetProperty(ref _sharedText, string.IsNullOrWhiteSpace(value) ? null : value)) return;
            OnPropertyChanged(nameof(HasSharedText));
            OnPropertyChanged(nameof(ShowDropZone));
            OnPropertyChanged(nameof(SharedTextIsLink));
            UpdateSendState();
        }
    }

    public bool HasSharedText => _sharedText != null;

    public bool SharedTextIsLink => _sharedText is { } text && Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";

    /// <summary>The empty state (drop zone, "Send text or link"): nothing chosen to send yet.</summary>
    public bool ShowDropZone => Items.Count == 0 && _sharedText == null;

    public RelayCommand ClearSharedTextCommand { get; }

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

    public bool CanSend => _selected.Count > 0 && (Items.Count > 0 || _sharedText != null);

    public string SendSummary
    {
        get
        {
            if (Items.Count == 0 && _sharedText != null)
            {
                if (_selected.Count == 0) return L.T("Choose a device to send to.");
                return SharedTextIsLink ? L.T("Send the link to {0}", TargetsText) : L.T("Send the text to {0}", TargetsText);
            }

            if (Items.Count == 0 && _selected.Count == 0) return L.T("Choose a device and add files to send.");
            if (Items.Count == 0) return L.T("Add files or folders to send to {0}.", TargetsText);
            if (_selected.Count == 0) return L.T("Choose a device to send to.");
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
        _ => L.Plural(_selected.Count, "{0} device", "{0} devices"),
    };

    public AsyncCommand ChooseFilesCommand { get; }

    public AsyncCommand ChooseFolderCommand { get; }

    public RelayCommand ClearCommand { get; }

    public AsyncCommand SendCommand { get; }

    /// <summary>Opens the "send text" box for the chosen computers (pre-filled with copied text, if any).</summary>
    public AsyncCommand SendTextCommand { get; }

    public RelayCommand RefreshCommand { get; }

    /// <summary>Phones: scan a computer's code to pair with it (works with and without a shared Wi-Fi).</summary>
    public AsyncCommand ScanCodeCommand { get; }

    public bool CanScanCodes => _main.Platform.CanScanCodes;

    public AsyncCommand ConnectByAddressCommand { get; }

    public RelayCommand EditNameCommand { get; }

    /// <summary>Adds dropped/picked/command-line paths to the send list.</summary>
    public void AddPaths(IEnumerable<string?> paths)
    {
        foreach (var raw in paths)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            if (_node.Transfers.ExternalFiles is { } externals && externals.TryGetFolderName(raw, out var folderName))
            {
                // A folder the platform hands over by address (Android): listed in the background.
                if (Items.Any(i => i.Path == raw)) continue;
                var folder = new PendingItemViewModel(raw, isFolder: true, RemoveItem, folderName) { IsMeasuring = true };
                Items.Add(folder);
                _ = MeasureExternalFolderAsync(folder, externals);
                continue;
            }

            if (_node.Transfers.ExternalFiles?.TryGet(raw, out var external) == true)
            {
                // A file the platform handed over as a stream (Android content:// URI).
                if (Items.Any(i => i.Path == raw)) continue;
                Items.Add(new PendingItemViewModel(raw, isFolder: false, RemoveItem, external.Name) { Size = external.Size, FileCount = 1 });
                continue;
            }

            if (raw.StartsWith("content://", StringComparison.OrdinalIgnoreCase))
            {
                Log.Warn($"Can't open {raw}: the phone didn't allow reading it");
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

    /// <summary>Clicking a device toggles it; several can be chosen and get the same files.</summary>
    public void SelectDevice(DeviceViewModel device)
    {
        _selectionIsAutomatic = false;
        if (_selected.Contains(device))
        {
            Deselect(device);
            return;
        }

        device.IsSelected = true;
        _selected.Add(device);
        OnSelectionChanged();
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
            Devices.Insert(index, new DeviceViewModel(device, SelectDevice, _node.ChannelsOf));
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
        OnPropertyChanged(nameof(ShowDropZone));
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

    private async Task MeasureExternalFolderAsync(PendingItemViewModel item, Beam.Core.Files.IExternalFiles externals)
    {
        var token = item.Measuring.Token;
        try
        {
            var files = await Task.Run(() => externals.ListFolder(item.Path, token).Where(i => i.File != null).ToList(), token);
            item.Size = files.Sum(f => f.File!.Size);
            item.FileCount = files.Count;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not list {item.Path}: {ex.Message}");
        }

        item.IsMeasuring = false;
        OnItemsChanged();
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

    private async Task ScanCodeAsync()
    {
        var scanned = await _main.Platform.ScanCodeAsync();
        if (string.IsNullOrWhiteSpace(scanned)) return; // cancelled
        if (Beam.Core.Direct.PairingCode.TryParse(scanned) is not { } code)
        {
            await _main.ShowDialogAsync(new Dialogs.ConfirmViewModel(L.T("That's not a Beam code"),
                L.T("On the computer, open Beam and go to Phone, then scan the code shown there."), L.T("OK"), ""));
            return;
        }

        var device = _node.Pair(code);
        Log.Info($"Paired with {device.Name} by scanning its code");
        // Pairing only: nothing is joined. A direct link is set up when files are sent with Direct chosen.
        await _main.ShowDialogAsync(new Dialogs.ConfirmViewModel(L.T("Paired with {0}", device.Name),
            L.T("{0} appears here whenever it's on the same Wi-Fi, or nearby over Bluetooth with Receive open. Files from it are accepted without asking.", device.Name),
            L.T("OK"), ""));
    }

    private async Task SendTextAsync()
    {
        if (_selected.Count == 0) return;
        if (!await EnsureWifiForDirectAsync()) return;
        var targets = _selected.Select(d => d.Device).ToList();
        // Starts empty: reading the clipboard unasked could show a copied password (and Android announces every read).
        if (await _main.ShowDialogAsync(new Dialogs.SendTextViewModel(TargetsText, "", _ui)) is not string text) return;
        foreach (var target in targets) await _main.StartSendTextAsync(target, text);
    }

    /// <summary>
    /// Direct chosen and Wi-Fi off on this device: offer to turn it on (the system's Wi-Fi panel on Android) before
    /// anything starts. False when it stays off; nothing is then sent (and never over another connection instead).
    /// </summary>
    private async Task<bool> EnsureWifiForDirectAsync()
    {
        if (!PreferDirect || !_node.Direct.IsWifiOff) return true;
        var ask = new Dialogs.ConfirmViewModel(L.T("Wi-Fi is off"),
            L.T("Sending directly needs Wi-Fi turned on on this device. It doesn't need to be connected to a network."),
            L.T("Turn on Wi-Fi"));
        if (await _main.ShowDialogAsync(ask) is not true) return false;
        if (await _node.Direct.RequestWifiOnAsync(TimeSpan.FromSeconds(60))) return true;
        await _main.ShowDialogAsync(new Dialogs.ConfirmViewModel(L.T("Wi-Fi is off"),
            L.T("Turn on Wi-Fi, then press Send again."), L.T("OK"), ""));
        return false;
    }

    private async Task SendAsync()
    {
        if (!CanSend) return;
        if (!await EnsureWifiForDirectAsync()) return;
        var targets = _selected.Select(d => d.Device).ToList();
        var paths = Items.Select(i => i.Path).ToList();
        var text = _sharedText;
        if (paths.Count > 0)
        {
            var started = false;
            foreach (var target in targets) started |= await _main.StartSendAsync(target, paths);
            if (!started) return;
            ClearItems();
        }

        if (text != null)
        {
            var sent = false;
            foreach (var target in targets) sent |= await _main.StartSendTextAsync(target, text);
            if (sent) SharedText = null;
        }
    }
}
