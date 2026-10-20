using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security;
using Beam.Core.Diagnostics;

namespace Beam.App.Platform;

/// <summary>
/// Joins a Wi-Fi network by name through a Windows Wi-Fi profile ("netsh wlan"), for when Windows hides scan results
/// from desktop apps (Windows 11 with location access off). Needs no administrator rights: the profile is for the
/// current user only, and Beam removes it when it leaves the network.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WlanProfiles
{
    public static async Task<bool> ConnectAsync(string ssid, string passphrase, CancellationToken cancellationToken)
    {
        var file = Path.Combine(Path.GetTempPath(), $"beam-wlan-{Guid.NewGuid():N}.xml");
        try
        {
            await File.WriteAllTextAsync(file, ProfileXml(ssid, passphrase), cancellationToken).ConfigureAwait(false);
            var (added, addOutput) = await NetshAsync($"wlan add profile filename=\"{file}\" user=current", cancellationToken).ConfigureAwait(false);
            if (added != 0)
            {
                Log.Warn($"Wi-Fi: adding a profile for {ssid} failed ({added}): {addOutput.Trim()}");
                return false;
            }

            var (connected, connectOutput) = await NetshAsync($"wlan connect name=\"{Quote(ProfileName(ssid))}\" ssid=\"{Quote(ssid)}\"", cancellationToken).ConfigureAwait(false);
            if (connected != 0) Log.Warn($"Wi-Fi: connecting to {ssid} by name failed ({connected}): {connectOutput.Trim()}");
            return connected == 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn($"Wi-Fi: joining {ssid} by name failed", ex);
            return false;
        }
        finally
        {
            try
            {
                File.Delete(file); // it holds the passphrase
            }
            catch
            {
                // temp file
            }
        }
    }

    /// <summary>Connects to a network this PC has a saved profile for (usually named like the network).</summary>
    public static async Task<int> ReconnectAsync(string ssid)
    {
        var (exit, _) = await NetshAsync($"wlan connect name=\"{Quote(ssid)}\"", CancellationToken.None).ConfigureAwait(false);
        return exit;
    }

    public static async Task RemoveAsync(string ssid)
    {
        try
        {
            await Task.Delay(3000).ConfigureAwait(false); // after Windows has moved on to the previous network
            await NetshAsync($"wlan delete profile name=\"{Quote(ProfileName(ssid))}\"", CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn($"Wi-Fi: removing the profile for {ssid} failed", ex);
        }
    }

    /// <summary>
    /// The profile's own name: not the network's, so it never collides with a profile Windows saved for that network
    /// earlier for all users (joining through Windows' Wi-Fi API does that), which a per-user profile can't replace.
    /// </summary>
    internal static string ProfileName(string ssid) => "Beam " + ssid;

    internal static string ProfileXml(string ssid, string passphrase)
    {
        var name = SecurityElement.Escape(ProfileName(ssid));
        var network = SecurityElement.Escape(ssid);
        var key = SecurityElement.Escape(passphrase);
        return $"""
            <?xml version="1.0"?>
            <WLANProfile xmlns="http://www.microsoft.com/networking/WLAN/profile/v1">
              <name>{name}</name>
              <SSIDConfig><SSID><name>{network}</name></SSID></SSIDConfig>
              <connectionType>ESS</connectionType>
              <connectionMode>manual</connectionMode>
              <MSM>
                <security>
                  <authEncryption><authentication>WPA2PSK</authentication><encryption>AES</encryption><useOneX>false</useOneX></authEncryption>
                  <sharedKey><keyType>passPhrase</keyType><protected>false</protected><keyMaterial>{key}</keyMaterial></sharedKey>
                </security>
              </MSM>
            </WLANProfile>
            """;
    }

    private static string Quote(string value) => value.Replace("\"", "");

    private static async Task<(int Exit, string Output)> NetshAsync(string arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("netsh.exe", arguments)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = Process.Start(start) ?? throw new InvalidOperationException("netsh didn't start.");
        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        return (process.ExitCode, output);
    }
}
