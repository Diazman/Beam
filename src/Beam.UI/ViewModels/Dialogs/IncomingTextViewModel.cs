using Beam.App.Infrastructure;
using Beam.App.Platform;
using Beam.App.Services;
using Beam.Core.Localization;

namespace Beam.App.ViewModels.Dialogs;

/// <summary>"Laptop sent you text": read it, copy it, or open the link. Returns true once seen.</summary>
public sealed class IncomingTextViewModel : DialogViewModel
{
    private readonly IUiServices _ui;
    private string _copyStatus = "";

    public IncomingTextViewModel(string senderName, string text, IUiServices ui, IPlatformServices platform)
    {
        _ui = ui;
        Text = text;
        Link = TryGetLink(text);
        Title = Link != null ? L.T("{0} sent you a link", senderName) : L.T("{0} sent you text", senderName);
        CopyCommand = new AsyncCommand(CopyAsync);
        OpenLinkCommand = new RelayCommand(() =>
        {
            if (Link != null) platform.OpenUrl(Link);
            Close(true);
        });
        CloseCommand = new RelayCommand(() => Close(true));
    }

    public string Title { get; }

    public string Text { get; }

    /// <summary>The text when it is a single web link (http/https), otherwise null.</summary>
    public string? Link { get; }

    public bool HasLink => Link != null;

    public string CopyStatus
    {
        get => _copyStatus;
        private set => SetProperty(ref _copyStatus, value);
    }

    public AsyncCommand CopyCommand { get; }

    public RelayCommand OpenLinkCommand { get; }

    public RelayCommand CloseCommand { get; }

    public override bool CanDismiss => true;

    public override object? DismissResult => true;

    /// <summary>Only plain http(s) links can be opened — never file:, javascript: or other schemes.</summary>
    public static string? TryGetLink(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Contains(' ') || trimmed.Contains('\n')) return null;
        return Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            ? uri.AbsoluteUri
            : null;
    }

    private async Task CopyAsync()
    {
        await _ui.CopyToClipboardAsync(Text);
        CopyStatus = L.T("Copied");
    }
}
