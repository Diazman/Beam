namespace Beam.App.Platform;

/// <summary>A folder the user picked for received files: the local path to use, or why it can't be used.</summary>
public sealed record ReceiveFolderChoice(string? Path, string? Problem = null);

/// <summary>Operating-system integration. Everything OS-specific in the app goes through here.</summary>
public interface IPlatformServices
{
    /// <summary>A phone or tablet: no tray, no "start with Windows", received files always go to Downloads/Beam.</summary>
    bool IsPhone => false;

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

    /// <summary>Where received files go by default; null means the Downloads folder.</summary>
    string? DefaultReceiveFolder => null;

    /// <summary>Turns what the folder picker returned into a folder Beam can save into (phones get content:// addresses).</summary>
    ReceiveFolderChoice CheckReceiveFolder(string picked) => new(picked);

    /// <summary>Lets the user pass Beam's log on, for troubleshooting (desktop: opens the log folder).</summary>
    void ShareLog(string logDirectory) => OpenFolder(logDirectory);
}
