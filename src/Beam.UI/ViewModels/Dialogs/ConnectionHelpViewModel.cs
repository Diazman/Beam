using System.Text;
using Avalonia.Media;
using Beam.App.Infrastructure;
using Beam.App.Services;
using Beam.Core.Direct;
using Beam.Core.Localization;

namespace Beam.App.ViewModels.Dialogs;

/// <summary>
/// "How to keep the phone connected" on the Phone page: the steps, and why a phone may leave this computer's Wi-Fi.
/// When this computer's Wi-Fi shares its internet (Windows' Mobile hotspot), it also offers a Wi-Fi code for joining
/// it in the phone's Wi-Fi settings; otherwise it explains how to stop the phone switching to another Wi-Fi.
/// </summary>
public sealed class ConnectionHelpViewModel : DialogViewModel
{
    public ConnectionHelpViewModel(DirectNetwork? network)
    {
        SharesInternet = network?.SharesInternet == true;
        if (SharesInternet)
        {
            InternetText = L.T("This computer shares its internet on its own Wi-Fi ({0}), so the phone has no reason to leave it for another Wi-Fi in the middle of a transfer.", network!.Ssid);
            JoinText = L.T("For the steadiest connection, also join {0} in the phone's Wi-Fi settings like any other Wi-Fi: scan this code there, or type the password {1}.", network.Ssid, network.Passphrase);
            WiFiQr = QrCode.Create(WiFiCode(network)).Geometry;
        }

        CloseCommand = new RelayCommand(() => Close(null));
    }

    public bool SharesInternet { get; }

    public bool NoInternet => !SharesInternet;

    public string InternetText { get; } = "";

    public string JoinText { get; } = "";

    /// <summary>The standard Wi-Fi code (WIFI:…) that phones' Wi-Fi settings and camera apps join directly.</summary>
    public Geometry? WiFiQr { get; }

    public RelayCommand CloseCommand { get; }

    public override bool CanDismiss => true;

    internal static string WiFiCode(DirectNetwork network) => $"WIFI:T:WPA;S:{Escape(network.Ssid)};P:{Escape(network.Passphrase)};;";

    private static string Escape(string value)
    {
        var escaped = new StringBuilder();
        foreach (var c in value)
        {
            if (c is '\\' or ';' or ',' or ':' or '"') escaped.Append('\\');
            escaped.Append(c);
        }

        return escaped.ToString();
    }
}
