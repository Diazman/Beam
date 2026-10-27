using System.Collections.ObjectModel;
using Avalonia.Threading;
using Beam.App.Infrastructure;
using Beam.App.Platform;
using Beam.App.Services;
using Beam.App.ViewModels.Dialogs;
using Beam.Core;
using Beam.Core.Diagnostics;
using Beam.Core.Direct;
using Beam.Core.Discovery;
using Beam.Core.Localization;
using Beam.Core.Settings;
using Beam.Core.Transfer;

namespace Beam.App.ViewModels;

// Answering what other devices send: files, text, direct connections (the engine calls these on background threads).
public sealed partial class MainViewModel : ObservableObject, IIncomingTransferHandler
{
    public Task<bool> ReceiveTextAsync(IncomingText text, TransferSession session, CancellationToken cancellationToken) =>
        Dispatcher.UIThread.InvokeAsync(async () =>
        {
            if (text.IsTrusted && await TryCopyAsync(text.Text))
            {
                // From a trusted device: no prompt — copy it and say so.
                var link = IncomingTextViewModel.TryGetLink(text.Text);
                _platform.ShowNotification(link != null ? L.T("Link from {0} copied", text.SenderName) : L.T("Text from {0} copied", text.SenderName),
                    text.Text.Length > 120 ? text.Text[..117] + "…" : text.Text,
                    () => Dispatcher.UIThread.Post(_ui.BringToFront));
                return true;
            }

            var dialog = new IncomingTextViewModel(text.SenderName, text.Text, _ui, _platform);
            if (!_ui.IsWindowActive)
            {
                Notify(s => s.NotifyOnIncomingRequest, dialog.Title, L.T("Open Beam to read it."), bringToFront: false);
                _ui.BringToFront();
            }

            return await ShowDialogAsync(dialog, cancellationToken) is true;
        });

    /// <summary>
    /// An unpaired sender asks this device to join its direct network. Says plainly what that does (this device leaves
    /// its Wi-Fi while the files come; a phone may also show Android's own prompt) and shows the security code to compare.
    /// </summary>
    public Task<bool> ApproveDirectConnectionAsync(IncomingDirectRequest request, CancellationToken cancellationToken) =>
        Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var code = Beam.Core.Identity.DeviceIdentity.ShortCode(request.SenderFingerprint);
            var message = L.T("{0} wants to send you files over a direct Wi-Fi connection. This device leaves its Wi-Fi network while the files come, and goes back afterwards. Security code: {1}",
                request.SenderName, code);
            if (request.ClaimedOnly) message += "\n\n" + L.T("Found over Bluetooth: Beam checks this code again on the direct connection before any file comes.");
            var dialog = new ConfirmViewModel(L.T("Connect directly to {0}?", request.SenderName), message, L.T("Connect"), L.T("Not now"));
            if (!_ui.IsWindowActive)
            {
                Notify(s => s.NotifyOnIncomingRequest, dialog.Title, L.T("Open Beam to answer."), bringToFront: false);
                _ui.BringToFront();
            }

            return await ShowDialogAsync(dialog, cancellationToken) is true;
        });

    /// <summary>The user chose to send a failed Direct transfer over the Wi-Fi network instead (a new, separate transfer).</summary>
    private async Task SendOverNetworkInsteadAsync(TransferSession failed)
    {
        try
        {
            Node.SendOverNetworkInstead(failed);
        }
        catch (TransferException ex)
        {
            await ShowDialogAsync(new ConfirmViewModel(L.T("Couldn't send to {0}", failed.PeerName), ex.Error.Message, L.T("OK"), ""));
        }
    }

    public Task<IncomingDecision> RequestApprovalAsync(IncomingRequest request, TransferSession session, CancellationToken cancellationToken) =>
        Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var canTrust = request.SenderFingerprint.Length > 0;
            var dialog = new IncomingRequestViewModel(request, _ui, canTrust);
            var tag = "request-" + session.Id;
            var settings = Node.Settings.Current;
            if (!_ui.IsWindowActive && settings.NotificationsEnabled && settings.NotifyOnIncomingRequest)
            {
                // Answer right from the notification (phones), or open Beam to see the details.
                _platform.ShowQuestionNotification(tag, L.T("Incoming files"), dialog.Title,
                    () => Dispatcher.UIThread.Post(_ui.BringToFront),
                    new[]
                    {
                        new NotificationButton(L.T("Accept"), () => Dispatcher.UIThread.Post(() =>
                        {
                            if (dialog.AcceptCommand.CanExecute(null)) dialog.AcceptCommand.Execute(null);
                            else _ui.BringToFront(); // e.g. not enough space: the dialog explains
                        })),
                        new NotificationButton(L.T("Decline"), () => Dispatcher.UIThread.Post(() => dialog.DeclineCommand.Execute(null))),
                    });
                _ui.BringToFront();
            }

            bool accepted;
            try
            {
                accepted = await ShowDialogAsync(dialog, cancellationToken) is true;
            }
            finally
            {
                _platform.CancelNotification(tag);
            }

            if (!accepted) return IncomingDecision.Decline();

            if (dialog.TrustDevice)
            {
                Node.Settings.Update(s =>
                {
                    s.TrustedDevices.RemoveAll(t => t.Fingerprint == request.SenderFingerprint);
                    s.TrustedDevices.Add(new TrustedDevice
                    {
                        Fingerprint = request.SenderFingerprint,
                        DeviceId = request.SenderId,
                        Name = request.SenderName,
                    });
                });
            }

            _acceptedFolders[request.TransferId] = dialog.Folder;
            return IncomingDecision.Accept(dialog.Folder);
        });

    public Task<IReadOnlyList<ConflictAction>> ResolveConflictsAsync(IncomingRequest request, IReadOnlyList<FileConflict> conflicts, CancellationToken cancellationToken) =>
        Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var folder = _acceptedFolders.TryGetValue(request.TransferId, out var f) ? f : request.DefaultFolder;
            var folderName = Path.GetFileName(Path.TrimEndingDirectorySeparator(folder));
            if (string.IsNullOrEmpty(folderName)) folderName = folder;
            var actions = new List<ConflictAction>(conflicts.Count);
            ConflictAction? forAll = null;
            for (var i = 0; i < conflicts.Count; i++)
            {
                if (forAll != null)
                {
                    actions.Add(forAll.Value);
                    continue;
                }

                var choice = (ConflictChoice)(await ShowDialogAsync(new ConflictViewModel(conflicts[i], folderName, conflicts.Count - i - 1), cancellationToken))!;
                actions.Add(choice.Action);
                if (choice.ApplyToAll) forAll = choice.Action;
            }

            return (IReadOnlyList<ConflictAction>)actions;
        });
}
