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
        // Phones hand out content:// addresses rather than paths; IExternalFiles opens them.
        return files.Select(f => f.TryGetLocalPath() ?? f.Path.ToString()).ToList();
    }

    // Phones can't send whole folders yet (pickers return folder addresses Beam can't list).
    public Task<IReadOnlyList<string>> PickFoldersAsync() => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

    public Task<string?> PickFolderAsync(string title, string? startFolder) => Task.FromResult<string?>(null);

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
