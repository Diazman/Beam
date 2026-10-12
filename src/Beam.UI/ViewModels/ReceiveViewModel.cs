using Avalonia.Media;
using Avalonia.Threading;
using Beam.App.Infrastructure;
using Beam.App.Platform;
using Beam.App.Services;
using Beam.App.ViewModels.Dialogs;
using Beam.Core;
using Beam.Core.Identity;
using Beam.Core.Localization;
using Beam.Core.Nearby;
using Beam.Core.Settings;

namespace Beam.App.ViewModels;

/// <summary>
/// The Receive page. While it is open this device is ready to receive: senders on the same Wi-Fi see it as usual and
/// senders without a shared network find it over Bluetooth. With "Let senders connect directly" a sender that prefers
/// a direct connection makes it start its own Wi-Fi; the sender gets its name and passphrase from Beam (over the shared
/// Wi-Fi or Bluetooth), never from the user.
/// </summary>
public sealed class ReceiveViewModel : ObservableObject
{
    private readonly BeamNode _node;
    private readonly MainViewModel _main;
    private bool _shown;
    private bool _showsCode;
    private Geometry? _code;

    public ReceiveViewModel(BeamNode node, MainViewModel main)
    {
        _node = node;
        _main = main;
        AllowBluetoothCommand = new AsyncCommand(AllowBluetoothAsync);
        LearnMoreCommand = new AsyncCommand(() => _main.ShowDialogAsync(new ConnectionHelpViewModel(_node.Direct.HostedNetwork)));
        ToggleCodeCommand = new RelayCommand(() =>
        {
            _showsCode = !_showsCode;
            Refresh();
        });
        node.Nearby.Changed += Post;
        node.Direct.Changed += Post;
        node.Settings.Changed += _ => Post();
    }

    public string DeviceName => _node.Settings.Current.DeviceName;

    /// <summary>The code the sender's screen shows for this device, to check it's the right one.</summary>
    public string VerificationCode => DeviceIdentity.ShortCode(_node.Identity.Fingerprint);

    public string ReadyText => _main.IsPhone
        ? L.T("Open Send in Beam on the other device and pick this phone.")
        : L.T("Open Send in Beam on the other device and pick this computer.");

    /// <summary>Let senders connect directly: this device starts its own Wi-Fi when a sender asks for it.</summary>
    public bool AllowDirect
    {
        get => _node.Settings.Current.AllowDirectSenders;
        set => _ = SetAllowDirectAsync(value);
    }

    public bool CanConnectDirectly => _node.Direct.CanHost;

    private async Task SetAllowDirectAsync(bool value)
    {
        if (value && _main.IsPhone && !await _main.EnsurePermissionAsync(AppPermission.NearbyDevices))
        {
            OnPropertyChanged(nameof(AllowDirect));
            return;
        }

        _node.Settings.Update(s => s.AllowDirectSenders = value);
        Refresh();
    }

    // ---- Bluetooth ----

    public bool BluetoothReady => _node.Nearby.State == NearbyRadioState.Ready;

    public bool BluetoothNeedsPermission => _node.Nearby.State == NearbyRadioState.NeedsPermission;

    public string BluetoothText => _node.Nearby.State switch
    {
        NearbyRadioState.Ready => L.T("Devices nearby can find it over Bluetooth, even without a shared Wi-Fi."),
        NearbyRadioState.Off => L.T("Bluetooth is off. Turn it on so devices that aren't on your Wi-Fi can find it."),
        NearbyRadioState.NeedsPermission => L.T("Allow Nearby devices so phones and computers without a shared Wi-Fi can find it."),
        _ => L.T("No Bluetooth: devices need to be on the same Wi-Fi to find it."),
    };

    public AsyncCommand AllowBluetoothCommand { get; }

    private async Task AllowBluetoothAsync()
    {
        await _node.Nearby.RequestPermissionAsync();
        Refresh();
    }

    // ---- Its own Wi-Fi ----

    public bool NetworkOn => _node.Direct.HostedNetwork != null;

    public string NetworkText
    {
        get
        {
            if (_node.Direct.HostedNetwork is { } network)
                return network.SharesInternet
                    ? L.T("Its own Wi-Fi {0} is on and shares this computer's internet. Senders connect to it directly.", network.Ssid)
                    : L.T("Its own Wi-Fi {0} is on. Senders connect to it directly.", network.Ssid);
            if (!CanConnectDirectly) return L.T("This device can't start its own Wi-Fi: senders need the same Wi-Fi.");
            return AllowDirect
                ? L.T("Its own Wi-Fi starts when a sender wants a direct connection, or has no shared Wi-Fi.")
                : L.T("Senders connect over the shared Wi-Fi.");
        }
    }

    /// <summary>
    /// Its Wi-Fi has no internet: a phone that joins it may jump to a saved Wi-Fi with internet in the middle of a
    /// transfer. Explained under "Learn more".
    /// </summary>
    public bool ShowsPhoneWarning => _node.Direct.HostedNetwork is { SharesInternet: false };

    public AsyncCommand LearnMoreCommand { get; }

    // ---- Code (when they can't find each other) ----

    public bool ShowsCode => _showsCode;

    public string CodeButtonText => _showsCode ? L.T("Hide the code") : L.T("Can't find each other? Show a code");

    public Geometry? Code
    {
        get => _code;
        private set => SetProperty(ref _code, value);
    }

    public RelayCommand ToggleCodeCommand { get; }

    /// <summary>The page became visible: this device is now receiving.</summary>
    internal void OnShown()
    {
        _shown = true;
        _node.Nearby.SetReceiving(true);
        _main.EnsureBluetoothOn(); // so devices without a shared Wi-Fi find it
        Refresh();
    }

    /// <summary>The page was left: no longer visible over Bluetooth, and its own Wi-Fi goes off (unless in use).</summary>
    internal void OnHidden()
    {
        _shown = false;
        _showsCode = false;
        _node.Nearby.SetReceiving(false);
    }

    private void Post() => Dispatcher.UIThread.Post(Refresh);

    private void Refresh()
    {
        if (_shown && _showsCode) Code = QrCode.Create(_node.CreatePairingCode(connectDirectly: NetworkOn).AppendTo(null)).Geometry;
        foreach (var name in new[]
                 {
                     nameof(DeviceName), nameof(VerificationCode), nameof(AllowDirect), nameof(CanConnectDirectly), nameof(BluetoothReady),
                     nameof(BluetoothNeedsPermission), nameof(BluetoothText), nameof(NetworkOn), nameof(NetworkText), nameof(ShowsPhoneWarning),
                     nameof(ShowsCode), nameof(CodeButtonText),
                 })
        {
            OnPropertyChanged(name);
        }
    }
}
