using System.Diagnostics;
using Beam.Core.Diagnostics;

namespace Beam.App.Platform;

/// <summary>Linux/macOS behaviour, used for development and automated UI tests.</summary>
internal sealed class GenericPlatformServices : IPlatformServices
{
    public bool SupportsStartWithSystem => false;

    public void OpenFolder(string path) => Launch(OperatingSystem.IsMacOS() ? "open" : "xdg-open", path);

    public void RevealInFolder(string path)
    {
        if (OperatingSystem.IsMacOS()) Launch("open", "-R", path);
        else OpenFolder(Directory.Exists(path) ? path : Path.GetDirectoryName(path) ?? path);
    }

    public void OpenFile(string path) => Launch(OperatingSystem.IsMacOS() ? "open" : "xdg-open", path);

    public bool GetStartWithSystem() => false;

    public void SetStartWithSystem(bool enabled)
    {
    }

    private static readonly Lazy<bool> HasNotifySend = new(() =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
        .Any(dir => File.Exists(Path.Combine(dir, "notify-send"))));

    public void ShowNotification(string title, string message, Action? onActivated = null)
    {
        if (OperatingSystem.IsLinux() && HasNotifySend.Value) Launch("notify-send", "--app-name=Beam", title, message);
    }

    private static void Launch(string program, params string[] args)
    {
        try
        {
            var info = new ProcessStartInfo(program) { UseShellExecute = false };
            foreach (var arg in args) info.ArgumentList.Add(arg);
            Process.Start(info)?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not run {program}: {ex.Message}");
        }
    }
}
