using Avalonia.Markup.Xaml;
using Beam.Core.Localization;

namespace Beam.App.Localization;

/// <summary>
/// Translated text in XAML: <c>Text="{l:T 'Nearby computers'}"</c> (the English text is the key). Texts about this
/// device can give a phone wording: <c>{l:T 'Name this computer', Phone='Name this phone'}</c>.
/// </summary>
public sealed class TExtension : MarkupExtension
{
    public TExtension()
    {
    }

    public TExtension(string text) => Text = text;

    public string Text { get; set; } = "";

    /// <summary>Used instead of <see cref="Text"/> on phones.</summary>
    public string? Phone { get; set; }

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        Phone != null ? L.ForDevice(Text, Phone) : L.T(Text);
}
