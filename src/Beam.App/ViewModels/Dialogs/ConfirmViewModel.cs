using Beam.App.Infrastructure;
using Beam.Core.Localization;

namespace Beam.App.ViewModels.Dialogs;

/// <summary>A yes/no question. Returns true for the primary action.</summary>
public sealed class ConfirmViewModel : DialogViewModel
{
    public ConfirmViewModel(string title, string message, string primaryText, string? secondaryText = null, bool isDestructive = false)
    {
        Title = title;
        Message = message;
        PrimaryText = primaryText;
        SecondaryText = secondaryText ?? L.T("Cancel");
        IsDestructive = isDestructive;
        PrimaryCommand = new RelayCommand(() => Close(true));
        SecondaryCommand = new RelayCommand(() => Close(false));
    }

    public string Title { get; }

    public string Message { get; }

    public string PrimaryText { get; }

    public string SecondaryText { get; }

    public bool IsDestructive { get; }

    public bool IsNotDestructive => !IsDestructive;

    public RelayCommand PrimaryCommand { get; }

    public RelayCommand SecondaryCommand { get; }

    public override bool CanDismiss => true;

    public override object? DismissResult => false;
}
