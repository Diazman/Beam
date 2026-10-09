using Avalonia.Markup.Xaml;
using Beam.Core.Localization;

namespace Beam.App.Localization;

/// <summary>Translated text in XAML: <c>Text="{l:T 'Nearby computers'}"</c> (the English text is the key).</summary>
public sealed class TExtension : MarkupExtension
{
    public TExtension()
    {
    }

    public TExtension(string text) => Text = text;

    public string Text { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) => L.T(Text);
}
