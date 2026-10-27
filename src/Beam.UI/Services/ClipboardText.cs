using Avalonia.Input.Platform;

namespace Beam.App.Services;

/// <summary>Copying and pasting text, the same on the desktop window and the phone screen.</summary>
internal static class ClipboardText
{
    /// <summary>Copies text; another app holding the clipboard (Windows lets one at a time) is waited out briefly.</summary>
    public static async Task CopyAsync(IClipboard? clipboard, string text)
    {
        if (clipboard == null) return;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await clipboard.SetTextAsync(text);
                return;
            }
            catch (Exception) when (attempt < 5)
            {
                await Task.Delay(100 * attempt);
            }
        }
    }

    /// <summary>The clipboard's text, or null (nothing there, or another app holds it).</summary>
    public static async Task<string?> ReadAsync(IClipboard? clipboard)
    {
        try
        {
            return clipboard == null ? null : await clipboard.TryGetTextAsync();
        }
        catch
        {
            return null;
        }
    }
}
