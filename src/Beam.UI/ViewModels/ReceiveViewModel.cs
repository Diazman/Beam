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
using Beam.Core.Diagnostics;

namespace Beam.App.ViewModels;

/// <summary>
/// The Receive page. While it is open this device is ready to receive: senders on the same Wi-Fi see it as usual and
/// senders without a shared network find it over Bluetooth. Opening it starts no network: a sender that sends directly
/// starts its own network when it sends, and this device joins it (with this device's approval when the sender isn't
/// paired).
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

    public bool CanConnectDirectly => _node.Direct.IsSupported;

    /// <summary>
    /// This device's address on the Wi-Fi: when the sender's Beam can't find it, the sender types it there ("Connect by
    /// address" on Send).
    /// </summary>
    public string LocalAddresses
    {
        get
        {
            var addresses = _node.GetLocalAddresses();
            return addresses.Count == 0 ? L.T("Not connected to a network") : string.Join(", ", addresses);
        }
    }

    // ---- Bluetooth ----

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

    // ---- Direct connections ----

    /// <summary>This device's own network is on (only for the Phone page or an older sender; Receive starts none).</summary>
    public bool NetworkOn => _node.Direct.HostedNetwork != null;

    public string NetworkText
    {
        get
        {
            if (!CanConnectDirectly) return L.T("This device can't connect directly: senders need the same Wi-Fi.");
            return L.T("Senders that send directly start their own Wi-Fi when they send, and this device joins it.");
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
        if (_shown && _showsCode) Code = QrCode.Create(_node.CreatePairingCode().AppendTo(null)).Geometry;
        foreach (var name in new[]
                 {
                     nameof(DeviceName), nameof(VerificationCode), nameof(LocalAddresses), nameof(CanConnectDirectly),
                     nameof(BluetoothNeedsPermission), nameof(BluetoothText), nameof(NetworkOn), nameof(NetworkText), nameof(ShowsPhoneWarning),
                     nameof(ShowsCode), nameof(CodeButtonText),
                 })
        {
            OnPropertyChanged(name);
        }
    }
}
