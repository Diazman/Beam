using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Threading;
using Beam.App.Infrastructure;
using Beam.App.Platform;
using Beam.App.Services;
using Beam.App.ViewModels.Dialogs;
using Beam.Core.History;
using Beam.Core.Localization;
using Beam.Core.Transfer;
using Beam.Core.Util;

namespace Beam.App.ViewModels;

public sealed class HistoryItemViewModel
{
    private readonly HistoryEntry _entry;
    private readonly IPlatformServices _platform;

    public HistoryItemViewModel(HistoryEntry entry, IPlatformServices platform, Func<string, Task>? copy = null)
    {
        _entry = entry;
        _platform = platform;
        ShowCommand = new RelayCommand(Show);
        CopyTextCommand = new AsyncCommand(async () =>
        {
            if (_entry.Text != null && copy != null) await copy(_entry.Text);
        });
    }

    public bool IsText => _entry.Text != null;

    public bool CanCopyText => IsText && _entry.Status == HistoryStatus.Completed;

    public AsyncCommand CopyTextCommand { get; }

    public bool IsSend => _entry.Direction == TransferDirection.Send;

    public Geometry? Icon => Icons.Get(IsSend ? Icons.Send : Icons.Receive);

    public string Title => _entry.Title;

    public string Subtitle
    {
        get
        {
            var who = IsSend ? L.T("To {0}", _entry.DeviceName) : L.T("From {0}", _entry.DeviceName);
            var parts = new List<string> { who };
            if (_entry.Text != null) return L.T("{0} · Text", who);
            if (_entry.FileCount > 0) parts.Add(L.Plural(_entry.FileCount, "{0} file", "{0} files"));
            if (_entry.TotalBytes > 0) parts.Add(Format.Bytes(_entry.TotalBytes));
            return string.Join(" · ", parts);
        }
    }

    public string When
    {
        get
        {
            var local = _entry.Timestamp.ToLocalTime();
            var today = DateTime.Today;
            if (local.Date == today) return L.T("Today, {0:t}", local);
            if (local.Date == today.AddDays(-1)) return L.T("Yesterday, {0:t}", local);
            return local.Year == today.Year ? local.ToString("MMM d, t") : local.ToString("MMM d yyyy, t");
        }
    }

    public string StatusText => _entry.Status switch
    {
        HistoryStatus.Completed => L.T("Completed"),
        HistoryStatus.CompletedWithErrors => L.T("Some files failed"),
        HistoryStatus.Failed => L.T("Failed"),
        HistoryStatus.Cancelled => L.T("Cancelled"),
        HistoryStatus.Declined => L.T("Declined"),
        HistoryStatus.Interrupted => L.T("Interrupted"),
        _ => _entry.Status.ToString(),
    };

    public bool IsSuccess => _entry.Status == HistoryStatus.Completed;

    public bool IsWarning => _entry.Status is HistoryStatus.CompletedWithErrors or HistoryStatus.Interrupted;

    public bool IsError => _entry.Status == HistoryStatus.Failed;

    public bool IsNeutral => _entry.Status is HistoryStatus.Cancelled or HistoryStatus.Declined;

    public string Tooltip => _entry.Message ?? StatusText;

    public bool CanShow => !IsSend && (_entry.Paths.Any(p => File.Exists(p) || Directory.Exists(p))
                                       || (_entry.Folder != null && Directory.Exists(_entry.Folder) && _entry.Status != HistoryStatus.Declined));

    public RelayCommand ShowCommand { get; }

    private void Show()
    {
        var existing = _entry.Paths.FirstOrDefault(p => File.Exists(p) || Directory.Exists(p));
        if (existing != null) _platform.RevealInFolder(existing);
        else if (_entry.Folder != null) _platform.OpenFolder(_entry.Folder);
    }
}

public sealed class HistoryViewModel : ObservableObject
{
    private readonly HistoryStore _store;
    private readonly IPlatformServices _platform;
    private readonly MainViewModel _main;

    public HistoryViewModel(HistoryStore store, IPlatformServices platform, MainViewModel main)
    {
        _store = store;
        _platform = platform;
        _main = main;
        ClearCommand = new AsyncCommand(ClearAsync, () => Items.Count > 0);
        store.Changed += () => Dispatcher.UIThread.Post(Reload);
        Reload();
    }

    public ObservableCollection<HistoryItemViewModel> Items { get; } = new();

    public bool IsEmpty => Items.Count == 0;

    public bool HasItems => Items.Count > 0;

    public AsyncCommand ClearCommand { get; }

    public void Reload()
    {
        Items.Clear();
        foreach (var entry in _store.Entries) Items.Add(new HistoryItemViewModel(entry, _platform, _main.CopyToClipboardAsync));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasItems));
        ClearCommand.RaiseCanExecuteChanged();
    }

    private async Task ClearAsync()
    {
        var confirmed = await _main.ShowDialogAsync(new ConfirmViewModel(
            L.T("Clear transfer history?"),
            L.T("The list of past transfers will be removed. Files you sent or received are not affected."),
            L.T("Clear history"),

            isDestructive: true));
        if (confirmed is true) _store.Clear();
    }
}
