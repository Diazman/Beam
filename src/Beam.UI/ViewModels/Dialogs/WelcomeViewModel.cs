using Beam.App.Infrastructure;
using Beam.Core.Settings;

namespace Beam.App.ViewModels.Dialogs;

/// <summary>First launch: one screen that explains Beam and lets the user name this computer.</summary>
public sealed class WelcomeViewModel : DialogViewModel
{
    private string _deviceName;

    public WelcomeViewModel(string currentName, bool isWindows)
    {
        _deviceName = currentName;
        ShowFirewallNote = isWindows;
        StartCommand = new RelayCommand(() => Close(AppSettings.NormalizeDeviceName(DeviceName)));
    }

    public string DeviceName
    {
        get => _deviceName;
        set => SetProperty(ref _deviceName, value);
    }

    public int MaxNameLength => AppSettings.MaxDeviceNameLength;

    public bool ShowFirewallNote { get; }

    public RelayCommand StartCommand { get; }
}
