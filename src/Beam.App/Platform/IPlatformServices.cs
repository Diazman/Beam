namespace Beam.App.Platform;

/// <summary>Operating-system integration. Everything OS-specific in the app goes through here.</summary>
public interface IPlatformServices
{
    /// <summary>Opens a folder in the file manager.</summary>
    void OpenFolder(string path);

    /// <summary>Opens the containing folder with the item selected.</summary>
    void RevealInFolder(string path);

    void OpenFile(string path);

    /// <summary>Opens a web or store link.</summary>
    void OpenUrl(string url);

    bool SupportsStartWithSystem { get; }

    bool GetStartWithSystem();

    void SetStartWithSystem(bool enabled);

    /// <summary>Shows a system notification. <paramref name="onActivated"/> runs (on any thread) when it is clicked.</summary>
    void ShowNotification(string title, string message, Action? onActivated = null);
}

public static class PlatformServices
{
    public static IPlatformServices Create() =>
        OperatingSystem.IsWindows() ? new WindowsPlatformServices() : new GenericPlatformServices();
}
