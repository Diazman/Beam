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

public sealed class HistoryItemViewModel : ObservableObject
{
    private readonly HistoryEntry _entry;
    private readonly IPlatformServices _platform;
    private bool _canShow;
    private bool _checkedFiles;

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

    public string When => Format.When(_entry.Timestamp.ToLocalTime().DateTime, DateTime.Today);

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

    internal HistoryEntry Entry => _entry;

    /// <summary>
    /// The received files (or their folder) are still there. Checked once, off the UI thread (a network drive or a
    /// removed USB stick can take seconds to answer); false until then.
    /// </summary>
    public bool CanShow
    {
        get
        {
            if (!_checkedFiles && !IsSend)
            {
                _checkedFiles = true;
                Task.Run(() => _entry.Paths.Any(p => File.Exists(p) || Directory.Exists(p))
                               || (_entry.Folder != null && Directory.Exists(_entry.Folder) && _entry.Status != HistoryStatus.Declined))
                    .ContinueWith(t =>
                    {
                        if (t.IsCompletedSuccessfully && t.Result) Dispatcher.UIThread.Post(() => SetProperty(ref _canShow, true, nameof(CanShow)));
                    }, TaskScheduler.Default);
            }

            return _canShow;
        }
    }

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

    /// <summary>
    /// Brings the list up to date. A new transfer only adds its row at the top (rebuilding all 500 rows each time made
    /// the window stutter); anything else (cleared, an entry changed) rebuilds it.
    /// </summary>
    public void Reload()
    {
        var entries = _store.Entries;
        // New rows at the top, and (at the 500 limit) the oldest gone at the bottom: everything else is unchanged.
        var added = Items.Count == 0 ? entries.Count : IndexOf(entries, Items[0].Entry);
        var kept = Math.Min(Items.Count, entries.Count - added);
        var onlyNew = added >= 0 && Enumerable.Range(0, kept).All(i => ReferenceEquals(Items[i].Entry, entries[i + added]));
        if (onlyNew)
        {
            while (Items.Count > kept) Items.RemoveAt(Items.Count - 1);
            for (var i = added - 1; i >= 0; i--) Items.Insert(0, new HistoryItemViewModel(entries[i], _platform, _main.CopyToClipboardAsync));
        }
        else
        {
            Items.Clear();
            foreach (var entry in entries) Items.Add(new HistoryItemViewModel(entry, _platform, _main.CopyToClipboardAsync));
        }

        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasItems));
        ClearCommand.RaiseCanExecuteChanged();
    }

    private static int IndexOf(IReadOnlyList<HistoryEntry> entries, HistoryEntry entry)
    {
        for (var i = 0; i < entries.Count; i++)
            if (ReferenceEquals(entries[i], entry)) return i;
        return -1;
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
