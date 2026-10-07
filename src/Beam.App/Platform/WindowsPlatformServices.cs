using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Beam.Core;
using Beam.Core.Diagnostics;
using Microsoft.Win32;

namespace Beam.App.Platform;

[SupportedOSPlatform("windows")]
internal sealed partial class WindowsPlatformServices : IPlatformServices
{
    /// <summary>Must match the AppUserModelID on the Start menu shortcut created by the installer.</summary>
    public const string AppUserModelId = "Beam.Desktop";

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private readonly ToastNotifier? _toasts;

    public WindowsPlatformServices()
    {
        try
        {
            // A packaged (Store) app already has an identity from its package.
            if (!PackageInfo.IsPackaged) SetCurrentProcessExplicitAppUserModelID(AppUserModelId);
        }
        catch (Exception ex)
        {
            Log.Warn("Could not set AppUserModelID", ex);
        }

        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240)) _toasts = new ToastNotifier();
    }

    public bool SupportsStartWithSystem => true;

    public void OpenFolder(string path) => Shell("explorer.exe", $"\"{path}\"");

    public void RevealInFolder(string path)
    {
        if (File.Exists(path) || Directory.Exists(path)) Shell("explorer.exe", $"/select,\"{path}\"");
        else OpenFolder(Path.GetDirectoryName(path) ?? path);
    }

    public void OpenFile(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not open {path}: {ex.Message}");
            RevealInFolder(path);
        }
    }

    public bool GetStartWithSystem()
    {
        if (PackageInfo.IsPackaged) return PackagedStartup.IsEnabled();
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(AppInfo.ProductName) is string;
        }
        catch
        {
            return false;
        }
    }

    public void SetStartWithSystem(bool enabled)
    {
        if (PackageInfo.IsPackaged)
        {
            PackagedStartup.SetEnabled(enabled);
            return;
        }

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled)
            {
                var exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Beam.exe");
                key.SetValue(AppInfo.ProductName, $"\"{exe}\" --minimized");
            }
            else
            {
                key.DeleteValue(AppInfo.ProductName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Could not change the start-with-Windows setting", ex);
        }
    }

    public void ShowNotification(string title, string message, Action? onActivated = null)
    {
        if (_toasts != null && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240)) _toasts.Show(title, message, onActivated);
    }

    private static void Shell(string program, string arguments)
    {
        try
        {
            Process.Start(new ProcessStartInfo(program, arguments) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not run {program}: {ex.Message}");
        }
    }

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SetCurrentProcessExplicitAppUserModelID(string appId);
}
