using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Threading;
using Beam.App.Infrastructure;
using Beam.App.Platform;
using Beam.App.Services;
using Beam.Core;
using Beam.Core.Diagnostics;
using Beam.Core.Discovery;
using Beam.Core.Localization;
using Beam.Core.Settings;

namespace Beam.App.ViewModels;

/// <summary>One device on the Devices page: nearby now, trusted, or both.</summary>
public sealed class DeviceRowViewModel
{
    public DeviceRowViewModel(string id, string name, Geometry? icon, string status, bool isNearby, TrustedDeviceViewModel? trusted,
        Action<DeviceRowViewModel> send, Action<DeviceRowViewModel> browse)
    {
        Id = id;
        Name = name;
        Icon = icon;
        Status = status;
        IsNearby = isNearby;
        Trusted = trusted;
        SendCommand = new RelayCommand(() => send(this), () => isNearby);
        BrowseCommand = new RelayCommand(() => browse(this));
    }

    public string Id { get; }

    public string Name { get; }

    public Geometry? Icon { get; }

    /// <summary>"Windows · On this Wi-Fi", "Not nearby right now"…</summary>
    public string Status { get; }

    public bool IsNearby { get; }

    public TrustedDeviceViewModel? Trusted { get; }

    public bool IsTrusted => Trusted != null;

    /// <summary>Its shared folders can be browsed (trusted devices only).</summary>
    public bool CanBrowse => IsTrusted;

    /// <summary>It browses this device's shared folders without being asked ("Ask again" undoes that).</summary>
    public bool AlwaysBrowse => Trusted?.AlwaysBrowse == true;

    public RelayCommand SendCommand { get; }

    public RelayCommand BrowseCommand { get; }

    /// <summary>A menu with the less common actions (trusted devices only).</summary>
    public bool HasMore => IsTrusted;
}

/// <summary>
/// Devices: everything about other devices in one place. Nearby and trusted devices (send to one, browse a trusted
/// one's shared folders, stop trusting it), adding a trusted device, connecting by address, scanning a code, and
/// sharing with a device that has no Beam app through its web browser.
/// </summary>
public sealed class DevicesViewModel : ObservableObject
{
    private readonly BeamNode _node;
    private readonly IPlatformServices _platform;
    private readonly MainViewModel _main;
    private bool _rebuildQueued;

    public DevicesViewModel(BeamNode node, IPlatformServices platform, MainViewModel main)
    {
        _node = node;
        _platform = platform;
        _main = main;
        main.Home.Devices.CollectionChanged += (_, _) => QueueRebuild();
        main.Settings.TrustedDevices.CollectionChanged += (_, _) => QueueRebuild();
        node.Settings.Changed += _ => QueueRebuild(); // "Ask again", names
        node.Nearby.Changed += QueueRebuild; // heard over Bluetooth, or no longer
        OpenWebShareCommand = new RelayCommand(() => _main.Navigate(Page.Phone));
        Rebuild();
    }

    public ObservableCollection<DeviceRowViewModel> Rows { get; } = new();

    public bool HasRows => Rows.Count > 0;

    public bool HasNoRows => Rows.Count == 0;

    /// <summary>Add a trusted device (a one-time code, shown or scanned).</summary>
    public AsyncCommand AddTrustedCommand => _main.Settings.AddTrustedCommand;

    public AsyncCommand ConnectByAddressCommand => _main.Home.ConnectByAddressCommand;

    public AsyncCommand ScanCodeCommand => _main.Home.ScanCodeCommand;

    public bool CanScanCodes => _platform.CanScanCodes;

    public bool IsAddingTrusted => _main.Settings.IsAddingTrusted;

    /// <summary>Sharing with a device that has no Beam app, through its web browser (computers).</summary>
    public bool CanShareWithBrowser => !_platform.IsPhone;

    public RelayCommand OpenWebShareCommand { get; }

    public void OnShown() => Rebuild();

    private void QueueRebuild()
    {
        if (_rebuildQueued) return;
        _rebuildQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _rebuildQueued = false;
            Rebuild();
        });
    }

    /// <summary>Nearby devices first (trusted ones among them marked), then trusted devices that aren't nearby now.</summary>
    private void Rebuild()
    {
        var rows = new List<DeviceRowViewModel>();
        var trusted = _main.Settings.TrustedDevices.ToList();
        foreach (var device in _main.Home.Devices)
        {
            var info = device.Device;
            var match = trusted.FirstOrDefault(t => t.Device.DeviceId == info.Id
                                                    || string.Equals(t.Device.Fingerprint, info.Fingerprint, StringComparison.OrdinalIgnoreCase));
            if (match != null) trusted.Remove(match);
            rows.Add(new DeviceRowViewModel(info.Id, device.Name, device.Icon, StatusOf(info), true, match, Send, Browse));
        }

        foreach (var device in trusted)
            rows.Add(new DeviceRowViewModel(device.Device.DeviceId, device.Name, Icons.Get("IconDevices"),
                L.T("Not nearby right now"), false, device, Send, Browse));

        // Rebuilt only when something shown changed (keeps a menu that is open from closing).
        static string Key(DeviceRowViewModel r) => $"{r.Id}|{r.Name}|{r.Status}|{r.IsNearby}|{r.IsTrusted}|{r.AlwaysBrowse}";
        if (Rows.Select(Key).SequenceEqual(rows.Select(Key))) return;
        Rows.Clear();
        foreach (var row in rows) Rows.Add(row);
        OnPropertyChanged(nameof(HasRows));
        OnPropertyChanged(nameof(HasNoRows));
    }

    private string StatusOf(DeviceInfo device)
    {
        var platform = device.Platform switch
        {
            "windows" => "Windows",
            "macos" => "Mac",
            "linux" => "Linux",
            "android" => "Android",
            "ios" => "iPhone",
            _ => device.Kind == DeviceKinds.Phone ? L.T("Phone") : L.T("Computer"),
        };
        var channels = _node.ChannelsOf(device);
        var where = channels switch
        {
            { LocalNetwork: true } => L.T("On this Wi-Fi"),
            { Bluetooth: true } => L.T("Nearby"),
            _ => L.T("Nearby"),
        };
        return $"{platform} · {where}";
    }

    private void Send(DeviceRowViewModel row)
    {
        if (!_main.Home.ChooseOnly(row.Id)) return;
        _main.Navigate(Page.Home);
    }

    private void Browse(DeviceRowViewModel row)
    {
        var device = _main.Home.Devices.FirstOrDefault(d => d.Id == row.Id)?.Device
                     ?? new DeviceInfo { Id = row.Id, Name = row.Name, Fingerprint = row.Trusted?.Device.Fingerprint ?? "" };
        _main.OpenBrowseAsync(device).Forget("Opening browsing");
    }
}
