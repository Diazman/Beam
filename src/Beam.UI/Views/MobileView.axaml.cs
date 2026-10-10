using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Beam.App.Services;
using Beam.Core.Localization;

namespace Beam.App.Views;

/// <summary>The phone screen. Also provides <see cref="IUiServices"/> on phones, where there is no window.</summary>
public partial class MobileView : UserControl, IUiServices
{
    public MobileView() => InitializeComponent();

    /// <summary>Set by the phone host while the app is in the foreground.</summary>
    public bool IsInForeground { get; set; } = true;

    public bool IsWindowActive => IsInForeground;

    private TopLevel? Top => TopLevel.GetTopLevel(this);

    public void BringToFront()
    {
        // A phone app can't bring itself to the front; the host shows a notification instead.
    }

    public async Task<IReadOnlyList<string>> PickFilesAsync()
    {
        if (Top is not { } top) return Array.Empty<string>();
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = L.T("Choose files to send"),
            AllowMultiple = true,
        });
        // Phones hand out content:// addresses rather than paths; IExternalFiles opens them. The address must stay
        // exactly as Android wrote it: Uri.ToString() decodes %3A and %2F, and Android then refuses the changed address.
        return files.Select(Address).ToList();
    }

    /// <summary>Returns the folder's address (content://…/tree/…); the platform lists what's inside when sending.</summary>
    public async Task<IReadOnlyList<string>> PickFoldersAsync()
    {
        if (Top is not { } top) return Array.Empty<string>();
        var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = L.T("Choose a folder to send"), AllowMultiple = false });
        return folders.Select(Address).ToList();
    }

    /// <summary>Returns the picked folder's address (content://…/tree/…); the platform turns it into a path it can save into.</summary>
    public async Task<string?> PickFolderAsync(string title, string? startFolder)
    {
        if (Top is not { } top) return null;
        var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
        return folders.Count == 0 ? null : Address(folders[0]);
    }

    private static string Address(IStorageItem item) => item.TryGetLocalPath() ?? item.Path.OriginalString;

    public async Task CopyToClipboardAsync(string text)
    {
        if (Top?.Clipboard is { } clipboard) await clipboard.SetTextAsync(text);
    }

    public async Task<string?> GetClipboardTextAsync()
    {
        try
        {
            return Top?.Clipboard is { } clipboard ? await clipboard.TryGetTextAsync() : null;
        }
        catch
        {
            return null;
        }
    }
}
