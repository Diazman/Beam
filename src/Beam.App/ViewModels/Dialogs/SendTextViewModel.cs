using Beam.App.Infrastructure;
using Beam.App.Services;

namespace Beam.App.ViewModels.Dialogs;

/// <summary>Type or paste text or a link to send. Returns the text, or null when cancelled.</summary>
public sealed class SendTextViewModel : DialogViewModel
{
    private readonly IUiServices _ui;
    private string _text;

    public SendTextViewModel(string targets, string initialText, IUiServices ui)
    {
        _ui = ui;
        _text = initialText;
        Title = $"Send text to {targets}";
        SendCommand = new RelayCommand(() => Close(Text.Trim().Length > 0 ? Text : null), () => Text.Trim().Length > 0);
        CancelCommand = new RelayCommand(() => Close(null));
        PasteCommand = new AsyncCommand(PasteAsync);
    }

    public string Title { get; }

    public string Text
    {
        get => _text;
        set
        {
            if (SetProperty(ref _text, value ?? "")) SendCommand.RaiseCanExecuteChanged();
        }
    }

    public RelayCommand SendCommand { get; }

    public RelayCommand CancelCommand { get; }

    public AsyncCommand PasteCommand { get; }

    public override bool CanDismiss => true;

    public override object? DismissResult => null;

    private async Task PasteAsync()
    {
        var clip = await _ui.GetClipboardTextAsync();
        if (!string.IsNullOrEmpty(clip)) Text = clip;
    }
}
