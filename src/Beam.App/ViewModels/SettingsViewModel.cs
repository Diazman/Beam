using System.Collections.ObjectModel;
using Avalonia.Threading;
using Beam.App.Infrastructure;
using Beam.App.Platform;
using Beam.App.Services;
using Beam.App.ViewModels.Dialogs;
using Beam.Core;
using Beam.Core.Identity;
using Beam.Core.Licensing;
using Beam.Core.Util;
using Beam.Core.Settings;
using Beam.Core.Storage;

namespace Beam.App.ViewModels;

public sealed record ThemeOption(ThemePreference Value, string Label)
{
    public override string ToString() => Label;
}

public sealed class TrustedDeviceViewModel
{
    public TrustedDeviceViewModel(TrustedDevice device, Action<TrustedDeviceViewModel> remove)
    {
        Device = device;
        RemoveCommand = new RelayCommand(() => remove(this));
    }

    public TrustedDevice Device { get; }

    public string Name => Device.Name;

    public string Details => $"Security code {DeviceIdentity.ShortCode(Device.Fingerprint)} · added {Device.AddedAt.ToLocalTime():d}";

    public RelayCommand RemoveCommand { get; }
}

public sealed class ManualAddressViewModel
{
    public ManualAddressViewModel(string address, Action<ManualAddressViewModel> remove)
    {
        Address = address;
        RemoveCommand = new RelayCommand(() => remove(this));
    }

    public string Address { get; }

    public RelayCommand RemoveCommand { get; }
}

public sealed class SettingsViewModel : ObservableObject
{
    private readonly BeamNode _node;
    private readonly IPlatformServices _platform;
    private readonly IUiServices _ui;
    private readonly MainViewModel _main;
    private string _deviceName;
    private string _nameStatus = "";
    private string _proStatus = "";
    private bool _suppress;

    public SettingsViewModel(BeamNode node, IPlatformServices platform, IUiServices ui, MainViewModel main)
    {
        _node = node;
        _platform = platform;
        _ui = ui;
        _main = main;
        _deviceName = node.Settings.Current.DeviceName;

        SaveNameCommand = new RelayCommand(SaveName);
        ChangeFolderCommand = new AsyncCommand(ChangeFolderAsync);
        OpenFolderCommand = new RelayCommand(() =>
        {
            Directory.CreateDirectory(ReceiveFolder);
            _platform.OpenFolder(ReceiveFolder);
        });
        ResetFolderCommand = new RelayCommand(() => _node.Settings.Update(s => s.ReceiveFolder = null), () => !UsesDefaultFolder);
        OpenLogsCommand = new RelayCommand(() => _platform.OpenFolder(_node.Paths.LogDirectory));
        CopyAddressCommand = new AsyncCommand(() => _ui.CopyToClipboardAsync(LocalAddresses));
        UpgradeCommand = new AsyncCommand(() => _main.ShowUpgradeAsync());
        RestorePurchaseCommand = new AsyncCommand(RestorePurchaseAsync);

        ThemeOptions = new[]
        {
            new ThemeOption(ThemePreference.System, "Use Windows setting"),
            new ThemeOption(ThemePreference.Light, "Light"),
            new ThemeOption(ThemePreference.Dark, "Dark"),
        };

        node.Settings.Changed += _ => Dispatcher.UIThread.Post(Reload);
        Reload();
    }

    public string DeviceName
    {
        get => _deviceName;
        set
        {
            if (SetProperty(ref _deviceName, value)) NameStatus = "";
        }
    }

    public int MaxNameLength => AppSettings.MaxDeviceNameLength;

    public string NameStatus
    {
        get => _nameStatus;
        private set => SetProperty(ref _nameStatus, value);
    }

    public string ReceiveFolder => _node.Settings.Current.EffectiveReceiveFolder;

    public bool UsesDefaultFolder => string.IsNullOrEmpty(_node.Settings.Current.ReceiveFolder);

    public bool SupportsStartup => _platform.SupportsStartWithSystem;

    public bool StartWithWindows
    {
        get => _node.Settings.Current.StartWithWindows;
        set => Apply(s => s.StartWithWindows = value, () => _platform.SetStartWithSystem(value));
    }

    public bool CloseToTray
    {
        get => _node.Settings.Current.CloseToTray;
        set => Apply(s => s.CloseToTray = value);
    }

    public bool Discoverable
    {
        get => _node.Settings.Current.Discoverable;
        set => Apply(s => s.Discoverable = value);
    }

    public bool NotificationsEnabled
    {
        get => _node.Settings.Current.NotificationsEnabled;
        set => Apply(s => s.NotificationsEnabled = value);
    }

    public bool NotifyOnIncomingRequest
    {
        get => _node.Settings.Current.NotifyOnIncomingRequest;
        set => Apply(s => s.NotifyOnIncomingRequest = value);
    }

    public bool NotifyOnTransferFinished
    {
        get => _node.Settings.Current.NotifyOnTransferFinished;
        set => Apply(s => s.NotifyOnTransferFinished = value);
    }

    public bool NotifyOnDeviceFound
    {
        get => _node.Settings.Current.NotifyOnDeviceFound;
        set => Apply(s => s.NotifyOnDeviceFound = value);
    }

    public IReadOnlyList<ThemeOption> ThemeOptions { get; }

    public ThemeOption SelectedTheme
    {
        get => ThemeOptions.First(o => o.Value == _node.Settings.Current.Theme);
        set
        {
            if (value != null) Apply(s => s.Theme = value.Value);
        }
    }

    public ObservableCollection<TrustedDeviceViewModel> TrustedDevices { get; } = new();

    public bool HasTrustedDevices => TrustedDevices.Count > 0;

    public ObservableCollection<ManualAddressViewModel> ManualAddresses { get; } = new();

    public bool HasManualAddresses => ManualAddresses.Count > 0;

    public string LocalAddresses
    {
        get
        {
            var addresses = _node.GetLocalAddresses();
            return addresses.Count == 0 ? "Not connected to a network" : string.Join(", ", addresses);
        }
    }

    public string SecurityCode => DeviceIdentity.ShortCode(_node.Identity.Fingerprint);

    public string VersionText => $"{AppInfo.ProductName} {AppInfo.Version} · {_node.Edition.EditionName}";

    public string DataFolder => _node.Paths.Root;

    public bool IsPro => _node.Edition.IsPro;

    public bool IsFree => !IsPro;

    public string PlanTitle => IsPro ? "Beam Pro" : "Beam Free";

    public string PlanDescription => IsPro
        ? "Thank you for supporting Beam! You can send to several computers at once, at full speed, as often as you like."
        : $"Send to one computer at a time, at up to {Format.Bytes(FreeLimits.MaxSendBytesPerSecond)}/s, {FreeLimits.SendsPerDay} times a day. "
          + $"Today you've used {_node.Quota.UsedToday} of {FreeLimits.SendsPerDay}. Receiving files is always free and unlimited.";

    public bool CanRestorePurchase => _main.Pro.CanPurchase && IsFree;

    public string ProStatus
    {
        get => _proStatus;
        private set => SetProperty(ref _proStatus, value);
    }

    public AsyncCommand UpgradeCommand { get; }

    public AsyncCommand RestorePurchaseCommand { get; }

    public RelayCommand SaveNameCommand { get; }

    public AsyncCommand ChangeFolderCommand { get; }

    public RelayCommand OpenFolderCommand { get; }

    public RelayCommand ResetFolderCommand { get; }

    public RelayCommand OpenLogsCommand { get; }

    public AsyncCommand CopyAddressCommand { get; }

    /// <summary>Applies the edited name (called on Enter, on losing focus, and when leaving the page).</summary>
    public void SaveName()
    {
        var normalized = AppSettings.NormalizeDeviceName(DeviceName);
        if (normalized != _node.Settings.Current.DeviceName)
        {
            _node.Settings.Update(s => s.DeviceName = normalized);
            NameStatus = "Saved. Nearby computers will see the new name.";
        }

        if (DeviceName != normalized) DeviceName = normalized;
    }

    internal void OnEditionChanged()
    {
        foreach (var name in new[] { nameof(IsPro), nameof(IsFree), nameof(PlanTitle), nameof(PlanDescription), nameof(CanRestorePurchase), nameof(VersionText) })
            OnPropertyChanged(name);
    }

    private async Task RestorePurchaseAsync()
    {
        ProStatus = "Checking…";
        await _main.Pro.RefreshAsync();
        ProStatus = IsPro ? "" : "No Beam Pro purchase was found for the Microsoft account signed in to the Store.";
    }

    private void Apply(Action<AppSettings> change, Action? sideEffect = null)
    {
        if (_suppress) return;
        _node.Settings.Update(change);
        sideEffect?.Invoke();
    }

    private void Reload()
    {
        _suppress = true;
        try
        {
            var settings = _node.Settings.Current;
            foreach (var name in new[]
                     {
                         nameof(ReceiveFolder), nameof(UsesDefaultFolder), nameof(StartWithWindows), nameof(CloseToTray),
                         nameof(Discoverable), nameof(NotificationsEnabled), nameof(NotifyOnIncomingRequest),
                         nameof(NotifyOnTransferFinished), nameof(NotifyOnDeviceFound), nameof(SelectedTheme), nameof(LocalAddresses),
                         nameof(PlanDescription),
                     })
            {
                OnPropertyChanged(name);
            }

            ResetFolderCommand.RaiseCanExecuteChanged();

            TrustedDevices.Clear();
            foreach (var device in settings.TrustedDevices) TrustedDevices.Add(new TrustedDeviceViewModel(device, RemoveTrusted));
            OnPropertyChanged(nameof(HasTrustedDevices));

            ManualAddresses.Clear();
            foreach (var address in settings.ManualAddresses) ManualAddresses.Add(new ManualAddressViewModel(address, RemoveManual));
            OnPropertyChanged(nameof(HasManualAddresses));
        }
        finally
        {
            _suppress = false;
        }
    }

    private async Task ChangeFolderAsync()
    {
        var folder = await _ui.PickFolderAsync("Choose where received files are saved", ReceiveFolder);
        if (string.IsNullOrEmpty(folder)) return;
        _node.Settings.Update(s => s.ReceiveFolder = string.Equals(folder, KnownFolders.Downloads, StringComparison.OrdinalIgnoreCase) ? null : folder);
    }

    private async void RemoveTrusted(TrustedDeviceViewModel device)
    {
        var confirmed = await _main.ShowDialogAsync(new ConfirmViewModel(
            $"Stop trusting {device.Name}?",
            $"Beam will ask you again before accepting files from {device.Name}.",
            "Stop trusting"));
        if (confirmed is true)
            _node.Settings.Update(s => s.TrustedDevices.RemoveAll(t => t.Fingerprint == device.Device.Fingerprint));
    }

    private void RemoveManual(ManualAddressViewModel address) => _node.ForgetManualAddress(address.Address);
}
