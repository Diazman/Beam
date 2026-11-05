using System.Collections.ObjectModel;
using Avalonia.Threading;
using Beam.App.Infrastructure;
using Beam.App.Platform;
using Beam.App.ViewModels.Dialogs;
using Beam.Core.Browse;
using Beam.Core.Diagnostics;
using Beam.Core.Discovery;
using Beam.Core.Localization;
using Beam.Core.Transfer;

namespace Beam.App.ViewModels;

/// <summary>Someone browsing this device right now, shown in a bar at the top with Stop.</summary>
public sealed class BrowseVisitViewModel
{
    public BrowseVisitViewModel(BrowseVisit visit, Action<BrowseVisit> stop)
    {
        Visit = visit;
        Text = L.T("{0} is browsing your shared folders", visit.DeviceName);
        StopCommand = new RelayCommand(() => stop(visit));
    }

    public BrowseVisit Visit { get; }

    public string Text { get; }

    public RelayCommand StopCommand { get; }
}

// Browsing other devices (the Browse page), and being browsed (the question, the bar with Stop).
public sealed partial class MainViewModel
{
    private const string VisitNotificationTag = "browse-visits";
    private BrowseViewModel? _browse;
    private Page _beforeBrowse = Page.Home;

    /// <summary>The Browse page while it is open.</summary>
    public BrowseViewModel? Browse => _browse;

    public ObservableCollection<BrowseVisitViewModel> Visits { get; } = new();

    public bool HasVisits => Visits.Count > 0;

    /// <summary>Opens the Browse page for a trusted device (closing one open before).</summary>
    public async Task OpenBrowseAsync(DeviceInfo device)
    {
        if (await ResolveAsync(device) is not { } target) return;
        await CloseBrowseAsync();
        _beforeBrowse = _page == Page.Browse ? _beforeBrowse : _page;
        _browse = new BrowseViewModel(Node, _ui, this, target);
        Navigate(Page.Browse);
        await _browse.ConnectAsync();
    }

    /// <summary>Back to where browsing was opened from; the visit ends (downloads carry on).</summary>
    public void CloseBrowse() => Navigate(_beforeBrowse == Page.Browse ? Page.Home : _beforeBrowse); // closes the visit too

    private async Task CloseBrowseAsync()
    {
        var browse = _browse;
        _browse = null;
        if (browse != null) await browse.DisposeAsync();
    }

    private void AttachBrowse()
    {
        Node.Browse.VisitsChanged += () => Dispatcher.UIThread.Post(RefreshVisits);
        Node.Browse.FileAdded += (visit, path) => Dispatcher.UIThread.Post(() =>
            Notify(s => s.NotifyOnTransferFinished, L.T("{0} added a file", visit.DeviceName), Path.GetFileName(path)));
    }

    private void RefreshVisits()
    {
        // One row per device (a download runs on its own connection, still the same visit to the user).
        var current = Node.Browse.Visits.GroupBy(v => v.Fingerprint, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
        if (Visits.Select(v => v.Visit.Fingerprint).SequenceEqual(current.Select(v => v.Fingerprint))) return;
        Visits.Clear();
        foreach (var visit in current) Visits.Add(new BrowseVisitViewModel(visit, StopVisit));
        OnPropertyChanged(nameof(HasVisits));
        if (current.Count == 0)
        {
            _platform.CancelNotification(VisitNotificationTag);
            return;
        }

        // Also outside Beam (phones): who is browsing, and Stop.
        var names = string.Join(", ", current.Select(v => v.DeviceName));
        _platform.ShowQuestionNotification(VisitNotificationTag, L.T("Your files are being browsed"),
            L.T("{0} is browsing your shared folders", names), () => Dispatcher.UIThread.Post(_ui.BringToFront),
            new[] { new NotificationButton(L.T("Stop"), () => Dispatcher.UIThread.Post(StopAllVisits)) });
    }

    private void StopVisit(BrowseVisit visit)
    {
        foreach (var same in Node.Browse.Visits.Where(v => string.Equals(v.Fingerprint, visit.Fingerprint, StringComparison.OrdinalIgnoreCase)))
            Node.Browse.Stop(same);
    }

    private void StopAllVisits()
    {
        foreach (var visit in Node.Browse.Visits) Node.Browse.Stop(visit);
    }

    /// <summary>A trusted device asks to browse (unless always allowed). One question at a time; unanswered means no.</summary>
    public Task<BrowseDecision> ApproveBrowseAsync(IncomingBrowseRequest request, CancellationToken cancellationToken) =>
        Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var settings = Node.Settings.Current;
            var dialog = new BrowseRequestViewModel(request, settings.BrowseAllowAdding, settings.SharedFolders.Select(f => f.Name).ToList());
            var tag = "browse-" + request.Fingerprint;
            if (!_ui.IsWindowActive && settings.NotificationsEnabled && settings.NotifyOnIncomingRequest)
            {
                _platform.ShowQuestionNotification(tag, L.T("Browse request"), dialog.Title,
                    () => Dispatcher.UIThread.Post(_ui.BringToFront),
                    new[]
                    {
                        // "Always" needs the checkbox: only from the dialog itself.
                        new NotificationButton(L.T("Allow"), () => Dispatcher.UIThread.Post(() => dialog.AllowCommand.Execute(null))),
                        new NotificationButton(L.T("Don't allow"), () => Dispatcher.UIThread.Post(() => dialog.DenyCommand.Execute(null))),
                    });
                _ui.BringToFront();
            }

            try
            {
                var result = await ShowDialogAsync(dialog, cancellationToken);
                var decision = result is BrowseDecision d ? d : BrowseDecision.Deny;
                Log.Info($"Browse: the user answered {decision} to {request.DeviceName}");
                return decision;
            }
            catch (OperationCanceledException)
            {
                return BrowseDecision.Deny;
            }
            finally
            {
                _platform.CancelNotification(tag);
            }
        });
}
