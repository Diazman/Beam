namespace Beam.App.Services;

/// <summary>Things view models need from the window (implemented by MainWindow, faked in tests).</summary>
public interface IUiServices
{
    Task<IReadOnlyList<string>> PickFilesAsync();

    Task<IReadOnlyList<string>> PickFoldersAsync();

    Task<string?> PickFolderAsync(string title, string? startFolder);

    /// <summary>Shows, restores and focuses the main window.</summary>
    void BringToFront();

    /// <summary>True when the window is visible and focused (notifications are then unnecessary).</summary>
    bool IsWindowActive { get; }

    Task CopyToClipboardAsync(string text);

    /// <summary>Text currently on the clipboard, if any.</summary>
    Task<string?> GetClipboardTextAsync();
}
