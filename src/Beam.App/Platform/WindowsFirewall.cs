using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using Beam.Core.Diagnostics;

namespace Beam.App.Platform;

/// <summary>
/// Windows Firewall for direct connections. A PC's own direct network (Wi-Fi Direct, 192.168.137.x) counts as a
/// "public" network, where Windows blocks Beam unless allowed. The usual first-run firewall prompt only allows
/// private networks (and may add a block rule for public ones), so phones on the direct network couldn't reach the
/// PC. Beam checks this and, with the user's OK (one administrator prompt), allows itself only on direct networks.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsFirewall
{
    private const string DirectRuleName = "Beam (direct connection)";
    private static bool _askedThisRun;

    /// <summary>True when Windows Firewall would stop phones on the direct network from reaching Beam.</summary>
    public static async Task<bool> BlocksDirectConnectionsAsync()
    {
        var exe = Environment.ProcessPath;
        if (exe == null) return false;
        var script = $$"""
            $p = '{{Escape(exe)}}'
            $rules = @(Get-NetFirewallApplicationFilter -Program $p -ErrorAction SilentlyContinue | Get-NetFirewallRule -ErrorAction SilentlyContinue |
              Where-Object { $_.Direction -eq 'Inbound' -and $_.Enabled -eq 'True' })
            $blocked = @($rules | Where-Object { $_.Action -eq 'Block' }).Count
            $allowed = @($rules | Where-Object { $_.Action -eq 'Allow' -and ($_.Profile.ToString() -match 'Public|Any') }).Count
            if ($blocked -gt 0) { 'blocked' } elseif ($allowed -gt 0) { 'ok' } else { 'missing' }
            """;
        try
        {
            var (exit, output) = await RunAsync(script, elevated: false).ConfigureAwait(false);
            var result = output.Trim();
            Log.Info($"Firewall check: {result} (exit {exit})");
            return exit == 0 && result is "blocked" or "missing";
        }
        catch (Exception ex)
        {
            Log.Warn("Firewall check failed", ex);
            return false;
        }
    }

    /// <summary>
    /// Removes Beam's block rules and allows Beam on direct networks (and private ones, if not already). Windows asks
    /// the user for administrator permission. False if they said no or it failed.
    /// </summary>
    public static async Task<bool> AllowDirectConnectionsAsync()
    {
        var exe = Environment.ProcessPath;
        if (exe == null) return false;
        _askedThisRun = true;
        var script = $$"""
            $p = '{{Escape(exe)}}'
            Get-NetFirewallApplicationFilter -Program $p -ErrorAction SilentlyContinue | Get-NetFirewallRule -ErrorAction SilentlyContinue |
              Where-Object { $_.Direction -eq 'Inbound' -and $_.Action -eq 'Block' } | Remove-NetFirewallRule
            Get-NetFirewallRule -DisplayName '{{DirectRuleName}}' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
            New-NetFirewallRule -DisplayName '{{DirectRuleName}}' -Direction Inbound -Action Allow -Program $p -Profile Any `
              -RemoteAddress 192.168.137.0/24,192.168.49.0/24 | Out-Null
            $private = @(Get-NetFirewallApplicationFilter -Program $p -ErrorAction SilentlyContinue | Get-NetFirewallRule -ErrorAction SilentlyContinue |
              Where-Object { $_.Direction -eq 'Inbound' -and $_.Action -eq 'Allow' -and $_.DisplayName -ne '{{DirectRuleName}}' })
            if ($private.Count -eq 0) {
              New-NetFirewallRule -DisplayName 'Beam' -Direction Inbound -Action Allow -Program $p -Profile Private,Domain | Out-Null
            }
            """;
        try
        {
            var (exit, _) = await RunAsync(script, elevated: true).ConfigureAwait(false);
            Log.Info($"Firewall: allowing direct connections finished with {exit}");
            return exit == 0;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            Log.Info("Firewall: the user declined the administrator prompt");
            return false;
        }
        catch (Exception ex)
        {
            Log.Warn("Firewall: allowing direct connections failed", ex);
            return false;
        }
    }

    /// <summary>Before hosting a direct network: fix the firewall if needed (at most one prompt per run).</summary>
    public static async Task EnsureForDirectAsync()
    {
        if (_askedThisRun) return;
        if (await BlocksDirectConnectionsAsync().ConfigureAwait(false)) await AllowDirectConnectionsAsync().ConfigureAwait(false);
        _askedThisRun = true;
    }

    private static string Escape(string value) => value.Replace("'", "''");

    private static async Task<(int Exit, string Output)> RunAsync(string script, bool elevated)
    {
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var start = new ProcessStartInfo("powershell.exe", $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand {encoded}")
        {
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        if (elevated)
        {
            start.UseShellExecute = true; // needed for the administrator prompt ("runas"); no output then
            start.Verb = "runas";
        }
        else
        {
            start.UseShellExecute = false;
            start.RedirectStandardOutput = true;
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("PowerShell didn't start.");
        var output = elevated ? "" : await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(2)).ConfigureAwait(false);
        return (process.ExitCode, output);
    }
}
