using System.Collections.ObjectModel;
using Avalonia.Threading;
using Beam.App.Infrastructure;
using Beam.App.Platform;
using Beam.App.Services;
using Beam.App.ViewModels.Dialogs;
using Beam.Core.Diagnostics;
using Beam.Core;
using Beam.Core.Identity;
using Beam.Core.Localization;
using Beam.Core.Util;
using Beam.Core.Settings;
using Beam.Core.Storage;

namespace Beam.App.ViewModels;

public sealed record ThemeOption(ThemePreference Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>A choice in the language list; an empty code follows Windows.</summary>
public sealed record LanguageOption(string Code, string Label)
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

    public string Details => L.T("Security code {0} · added {1:d}", DeviceIdentity.ShortCode(Device.Fingerprint), Device.AddedAt.ToLocalTime());

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
        ResetFolderCommand = new RelayCommand(() => _node.Settings.Update(s => s.ReceiveFolder = _platform.DefaultReceiveFolder), () => !UsesDefaultFolder);
        OpenLogsCommand = new RelayCommand(() => _platform.ShareLog(_node.Paths.LogDirectory));
        CopyAddressCommand = new AsyncCommand(() => _ui.CopyToClipboardAsync(LocalAddresses));
        OpenPrivacyCommand = new RelayCommand(() => _main.Platform.OpenUrl(AppInfo.PrivacyPolicyUrl));

        ThemeOptions = new[]
        {
            new ThemeOption(ThemePreference.System, platform.IsPhone ? L.T("Use phone setting") : L.T("Use Windows setting")),
            new ThemeOption(ThemePreference.Light, L.T("Light")),
            new ThemeOption(ThemePreference.Dark, L.T("Dark")),
        };

        // Each language is listed in its own name so people can find theirs whatever language Beam is in.
        LanguageOptions = new[] { new LanguageOption("", platform.IsPhone ? L.T("Use phone language") : L.T("Use Windows language")) }
            .Concat(L.Languages.Select(l => new LanguageOption(l.Code, l.NativeName)))
            .ToArray();

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

    public bool UsesDefaultFolder => string.IsNullOrEmpty(_node.Settings.Current.ReceiveFolder)
                                     || string.Equals(_node.Settings.Current.ReceiveFolder, _platform.DefaultReceiveFolder, StringComparison.OrdinalIgnoreCase);

    public bool SupportsStartup => _platform.SupportsStartWithSystem;

    /// <summary>Desktop-only settings (tray, receive folder) are hidden on phones.</summary>
    public bool IsDesktop => !_platform.IsPhone;

    public string ThisDeviceTitle => _platform.IsPhone ? L.T("This phone") : L.T("This computer");

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

    public IReadOnlyList<LanguageOption> LanguageOptions { get; }

    public LanguageOption SelectedLanguage
    {
        get => LanguageOptions.FirstOrDefault(o => o.Code == _node.Settings.Current.Language) ?? LanguageOptions[0];
        set
        {
            if (value != null) Apply(s => s.Language = value.Code);
        }
    }

    /// <summary>The chosen language takes effect the next time Beam starts.</summary>
    public bool LanguageNeedsRestart => L.Resolve(_node.Settings.Current.Language) != L.Current;

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
            return addresses.Count == 0 ? L.T("Not connected to a network") : string.Join(", ", addresses);
        }
    }

    public string SecurityCode => DeviceIdentity.ShortCode(_node.Identity.Fingerprint);

    public string VersionText => $"{AppInfo.ProductName} {AppInfo.Version} ({AppInfo.Build})";

    public RelayCommand OpenPrivacyCommand { get; }

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
            NameStatus = L.T("Saved. Nearby devices will see the new name.");
        }

        if (DeviceName != normalized) DeviceName = normalized;
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
                         nameof(NotifyOnTransferFinished), nameof(NotifyOnDeviceFound), nameof(SelectedTheme), nameof(SelectedLanguage),
                         nameof(LanguageNeedsRestart), nameof(LocalAddresses),
                     })
            {
                OnPropertyChanged(name);
            }

            ResetFolderCommand.RaiseCanExecuteChanged();

            // The lists are rebuilt only when they changed (any setting saved, e.g. the window size, came through here).
            static string Key(TrustedDevice t) => $"{t.Fingerprint}|{t.DeviceId}|{t.Name}|{t.AddedAt:O}";
            if (!TrustedDevices.Select(t => Key(t.Device)).SequenceEqual(settings.TrustedDevices.Select(Key)))
            {
                TrustedDevices.Clear();
                foreach (var device in settings.TrustedDevices) TrustedDevices.Add(new TrustedDeviceViewModel(device, RemoveTrusted));
                OnPropertyChanged(nameof(HasTrustedDevices));
            }

            if (!ManualAddresses.Select(m => m.Address).SequenceEqual(settings.ManualAddresses))
            {
                ManualAddresses.Clear();
                foreach (var address in settings.ManualAddresses) ManualAddresses.Add(new ManualAddressViewModel(address, RemoveManual));
                OnPropertyChanged(nameof(HasManualAddresses));
            }
        }
        finally
        {
            _suppress = false;
        }
    }

    private async Task ChangeFolderAsync()
    {
        var picked = await _ui.PickFolderAsync(L.T("Choose where received files are saved"), ReceiveFolder);
        if (string.IsNullOrEmpty(picked)) return;
        var choice = _platform.CheckReceiveFolder(picked);
        if (choice.Path == null)
        {
            await _main.ShowDialogAsync(new ConfirmViewModel(L.T("Can't use this folder"),
                choice.Problem ?? L.T("Beam can't save files in that folder. Choose another one."), L.T("OK"), ""));
            return;
        }

        var folder = choice.Path;
        _node.Settings.Update(s => s.ReceiveFolder = _platform.DefaultReceiveFolder == null
                                                      && string.Equals(folder, KnownFolders.Downloads, StringComparison.OrdinalIgnoreCase) ? null : folder);
    }

    private async void RemoveTrusted(TrustedDeviceViewModel device)
    {
        try
        {
            var confirmed = await _main.ShowDialogAsync(new ConfirmViewModel(
                L.T("Stop trusting {0}?", device.Name),
                L.T("Beam will ask you again before accepting files from {0}.", device.Name),
                L.T("Stop trusting")));
            if (confirmed is true)
                _node.Settings.Update(s => s.TrustedDevices.RemoveAll(t => t.Fingerprint == device.Device.Fingerprint));
        }
        catch (Exception ex)
        {
            Log.Warn("Removing a trusted device failed", ex); // an async void that throws would end the app
        }
    }

    private void RemoveManual(ManualAddressViewModel address) => _node.ForgetManualAddress(address.Address);
}
