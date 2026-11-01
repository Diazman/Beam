using System.Runtime.CompilerServices;
using Beam.App.Platform;
using Beam.Core;
using Beam.Core.Diagnostics;
using Beam.Core.Localization;
using Beam.Core.Transfer;

namespace Beam.App.Services;

/// <summary>
/// Tells the phone's owner about its backups where they can't miss it: a notification when a backup to a computer is
/// set up, and one each time files were backed up, both naming the computer and with "Stop backup". Someone who set up
/// a backup on this phone without the owner knowing is found out the next time it runs. Shown whatever the
/// notification settings, and also when Beam runs without its screen (Android's background check).
/// </summary>
public static class BackupNotices
{
    private static readonly ConditionalWeakTable<BeamNode, object> Attached = new();

    /// <summary>Once per node: later calls (the screen after a background start) change nothing.</summary>
    public static void Attach(BeamNode node, IPlatformServices platform)
    {
        lock (Attached)
        {
            if (Attached.TryGetValue(node, out _)) return;
            Attached.Add(node, new object());
        }

        node.Backup.SetUp += plan => platform.ShowBackupNotification(Tag(plan.TargetId),
            L.T("This phone now backs up to {0}", plan.TargetName),
            L.T("Its chosen files go to {0} by themselves whenever both are on the same Wi-Fi. If you didn't set this up, tap Stop backup.", plan.TargetName),
            plan.TargetId, () => Stop(node, plan.TargetId));

        // "All folders" off and a folder appeared (a new app's photos): it isn't backed up until the user ticks it.
        node.Backup.NewFolders += (target, kind, names) => platform.ShowNotification(
            L.Plural(names.Count, "New folder on this phone", "New folders on this phone"),
            L.T("{0} ({1}) isn't backed up to {2}. To back it up, tick it in Beam: Settings → Backup → Choose folders.",
                string.Join(", ", names), KindName(kind), target));

        node.Transfers.SessionFinished += session =>
        {
            if (!session.IsBackup || session.Direction != TransferDirection.Send) return;
            var sent = session.GetSnapshot().CompletedFiles;
            if (sent == 0) return;
            platform.ShowBackupNotification(Tag(session.PeerId),
                L.Plural(sent, "{0} file backed up to {1}", "{0} files backed up to {1}", session.PeerName),
                L.T("This phone backs up to {0} whenever both are on the same Wi-Fi. If you didn't set this up, tap Stop backup.", session.PeerName),
                session.PeerId, () => Stop(node, session.PeerId));
        };
    }

    private static string Tag(string targetId) => "backup-" + targetId;

    private static string KindName(string kind) => kind switch
    {
        Beam.Core.Backup.BackupSourceKinds.Photos => L.T("Photos"),
        Beam.Core.Backup.BackupSourceKinds.Videos => L.T("Videos"),
        Beam.Core.Backup.BackupSourceKinds.Music => L.T("Music"),
        _ => L.T("Documents"),
    };

    private static void Stop(BeamNode node, string targetId)
    {
        Log.Info("Backup: stopped from its notification");
        node.Backup.Remove(targetId);
    }
}
