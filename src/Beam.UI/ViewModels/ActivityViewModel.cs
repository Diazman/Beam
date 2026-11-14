using Beam.App.Infrastructure;

namespace Beam.App.ViewModels;

/// <summary>
/// Activity: transfers running now (and finished ones still worth a look: failed, or to open) on top, the history
/// below. The same on the desktop and on phones.
/// </summary>
public sealed class ActivityViewModel : ObservableObject
{
    public ActivityViewModel(MainViewModel main)
    {
        Main = main;
        main.Transfers.CollectionChanged += (_, _) => Refresh();
        main.History.Items.CollectionChanged += (_, _) => Refresh();
        main.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MainViewModel.HasFinishedTransfers) or nameof(MainViewModel.ActiveTransferCount)) Refresh();
        };
    }

    public MainViewModel Main { get; }

    public HistoryViewModel History => Main.History;

    public bool IsEmpty => Main.Transfers.Count == 0 && Main.History.Items.Count == 0;

    public bool HasCurrent => Main.Transfers.Count > 0;

    public bool HasHistory => Main.History.Items.Count > 0;

    private void Refresh()
    {
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasCurrent));
        OnPropertyChanged(nameof(HasHistory));
    }
}
