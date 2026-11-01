using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Beam.Core.Diagnostics;
using Beam.Core.Direct;
using Beam.Core.Discovery;
using Beam.Core.Files;
using Beam.Core.Localization;
using Beam.Core.Protocol;
using Beam.Core.Settings;
using Beam.Core.Transfer;

namespace Beam.Core.Backup;

/// <summary>
/// Automatic backups from a device (a phone) to a computer over the Wi-Fi network both are on.
/// <para>
/// <b>Setup</b> (once): the computer shows a code (QR) with its certificate fingerprint and a one-time token, good for a
/// few minutes. The phone scans it, connects to that certificate only, and presents the token; the computer then
/// knows the phone by its certificate. Only someone who saw the computer's screen can set up a backup to it, and
/// neither side can be impersonated afterwards: each insists on the other's certificate.
/// </para>
/// <para>
/// <b>Backups</b>: whenever the computer is on the same network (and again every hour while it is), the phone offers
/// everything it backs up; the computer asks only for what it doesn't have yet (same name, size and time) and takes
/// it without asking anyone, into that phone's own folder. Files are encrypted on the way (TLS, both certificates
/// checked) and verified on arrival. Nothing goes over a direct link or anywhere but that computer, and nothing is
/// ever deleted on the computer because it was deleted on the phone.
/// </para>
/// </summary>
public sealed class BackupService : IDisposable
{
    internal static TimeSpan InviteLifetime { get; set; } = TimeSpan.FromMinutes(10);
    private const int MaxFailedSetups = 5;

    private readonly TransferService _transfers;
    private readonly DiscoveryService _discovery;
    private readonly SettingsStore _settings;
    private readonly Func<PairingCode> _pairingCode;
    private readonly IBackupSources? _sources;
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, TransferSession> _running = new();
    private readonly ConcurrentDictionary<string, long> _lastStarted = new();
    private readonly ConcurrentDictionary<string, bool> _present = new();
    private readonly ConcurrentDictionary<string, bool> _warnedMismatch = new();
    private readonly ConcurrentDictionary<string, bool> _restart = new();
    private readonly Timer _timer;
    private Invite? _invite;

    private sealed record Invite(string Token, DateTimeOffset ExpiresAt)
    {
        public int Failures { get; set; }
    }

    /// <summary>While the computer stays on the network, how often everything is checked again.</summary>
    internal static TimeSpan RecheckInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Two automatic backups never start closer together than this (a network that comes and goes).</summary>
    internal static TimeSpan MinimumGap { get; set; } = TimeSpan.FromMinutes(2);

    internal BackupService(TransferService transfers, DiscoveryService discovery, SettingsStore settings, Func<PairingCode> pairingCode,
        IBackupSources? sources)
    {
        _transfers = transfers;
        _discovery = discovery;
        _settings = settings;
        _pairingCode = pairingCode;
        _sources = sources;
        _transfers.BackupHandler = HandleAsync;
        _transfers.SessionFinished += OnSessionFinished;
        _discovery.DeviceFound += OnDevice;
        _discovery.DeviceChanged += OnDevice;
        _discovery.DeviceLost += OnDeviceLost;
        _timer = new Timer(_ => CheckAll(), null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
    }

    /// <summary>Something about backups changed (a run started or ended, a partner was added): raised on any thread.</summary>
    public event Action? Changed;

    /// <summary>On the computer: a phone used the setup code and may back up here now.</summary>
    public event Action<BackupPartner>? PartnerAdded;

    /// <summary>On the phone: a backup to a computer was set up (raised on any thread), so the phone can say so where it can't be missed.</summary>
    public event Action<BackupPlan>? SetUp;

    /// <summary>
    /// On the phone: a backup found folders that aren't backed up because they are new ("All folders" is off): the
    /// computer's name, the kind ("photos"), and the folders' names. Once per folder; raised on any thread.
    /// </summary>
    public event Action<string, string, IReadOnlyList<string>>? NewFolders;

    /// <summary>The folders on this phone with a whole-phone kind of file, for "Choose folders" (takes a moment).</summary>
    public Task<IReadOnlyList<BackupFolderInfo>> ListFoldersAsync(BackupSource source, CancellationToken cancellationToken) =>
        Task.Run(() => BackupManifest.ListFolders(source, _sources, cancellationToken), cancellationToken);

    internal void Start()
    {
        // The computer's last known addresses: found even on a network that drops Beam's announcements.
        foreach (var plan in _settings.Current.Backups)
            foreach (var address in plan.Addresses)
                if (IPAddress.TryParse(address, out var ip)) _discovery.AddUnicastTarget(ip);
    }

    // ---------------------------------------------------------------- the computer

    /// <summary>
    /// The code this computer shows to set up a backup: who it is (with its certificate) and a new one-time token.
    /// Valid for a few minutes, for one phone; a new code replaces the previous one.
    /// </summary>
    public PairingCode CreateSetupCode()
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        lock (_gate) _invite = new Invite(token, DateTimeOffset.UtcNow + InviteLifetime);
        var code = _pairingCode();
        code.BackupToken = token;
        Log.Info("Backup: showing a setup code (valid for 10 minutes, one phone)");
        return code;
    }

    /// <summary>The setup code was closed: it can't be used any more.</summary>
    public void CancelSetupCode()
    {
        lock (_gate) _invite = null;
    }

    /// <summary>Stops taking backups from <paramref name="fingerprint"/> (what it backed up stays on this computer).</summary>
    public void RemovePartner(string fingerprint)
    {
        _settings.Update(s => s.BackupPartners.RemoveAll(p => string.Equals(p.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase)));
        Log.Info($"Backup: no longer taking backups from {DeviceShort(fingerprint)}");
        Changed?.Invoke();
    }

    /// <summary>A phone connected for a backup setup.</summary>
    private async Task HandleAsync(PeerConnection connection, CancellationToken token)
    {
        var request = await connection.Channel.ReadMessageAsync<BackupRequestMessage>(FrameType.BackupRequest, TimeSpan.FromSeconds(30), token)
            .ConfigureAwait(false);
        var response = request.Action == BackupActions.Setup
            ? Setup(request, connection)
            : new BackupResponseMessage { Ok = false, Message = "Unknown request." };
        await connection.Channel.SendAsync(FrameType.BackupResponse, response, token).ConfigureAwait(false);
    }

    private BackupResponseMessage Setup(BackupRequestMessage request, PeerConnection connection)
    {
        var hello = connection.RemoteHello;
        var name = AppSettings.NormalizeDeviceName(hello.DeviceName);
        lock (_gate)
        {
            var invite = _invite;
            var valid = invite != null && invite.ExpiresAt > DateTimeOffset.UtcNow && request.Token != null
                        && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(request.Token), Encoding.UTF8.GetBytes(invite.Token));
            if (!valid)
            {
                if (invite != null && ++invite.Failures >= MaxFailedSetups) _invite = null; // someone is guessing: the code goes
                Log.Warn($"Backup: refused a setup from \"{name}\" ({hello.DeviceId}): {(invite == null ? "no setup code is shown" : "wrong or expired code")}");
                return new BackupResponseMessage
                {
                    Ok = false,
                    Message = L.T("This code isn't valid any more. On the computer, open Settings → Backup → Add a phone, and scan the new code."),
                };
            }

            _invite = null; // one phone per code
        }

        BackupPartner? added = null;
        _settings.Update(s =>
        {
            // The same phone again (re-installed: a new certificate), or a phone set up before: its folder stays.
            var existing = s.BackupPartners.FirstOrDefault(p => string.Equals(p.Fingerprint, connection.RemoteFingerprint, StringComparison.OrdinalIgnoreCase))
                           ?? s.BackupPartners.FirstOrDefault(p => p.DeviceId == hello.DeviceId);
            var folder = existing?.Folder ?? UniqueFolder(s, name);
            s.BackupPartners.RemoveAll(p => p == existing);
            added = new BackupPartner
            {
                DeviceId = hello.DeviceId,
                Name = name,
                Fingerprint = connection.RemoteFingerprint.ToLowerInvariant(),
                Folder = folder,
                AddedAt = existing?.AddedAt ?? DateTimeOffset.UtcNow,
                LastBackupAt = existing?.LastBackupAt,
                LastFileCount = existing?.LastFileCount ?? 0,
            };
            s.BackupPartners.Add(added);
        });

        try
        {
            Directory.CreateDirectory(added!.Folder);
        }
        catch (Exception ex)
        {
            Log.Warn($"Backup: couldn't create {added!.Folder}", ex);
        }

        Log.Info($"Backup: {name} ({hello.DeviceId}, {DeviceShort(connection.RemoteFingerprint)}) may back up here now, into {added.Folder}");
        PartnerAdded?.Invoke(added);
        Changed?.Invoke();
        return new BackupResponseMessage { Ok = true, Name = _settings.Current.DeviceName };
    }

    private static string UniqueFolder(AppSettings settings, string name)
    {
        var root = settings.EffectiveBackupFolder;
        var used = settings.BackupPartners.Select(p => p.Folder).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var segment = FileNaming.MakeUnique(SafePath.SanitizeSegment(name), n => used.Contains(Path.Combine(root, n)), isDirectory: true);
        return Path.Combine(root, segment);
    }

    // ---------------------------------------------------------------- the phone

    /// <summary>
    /// Sets up a backup to the computer whose setup code was scanned: connects to that computer's certificate only,
    /// over the Wi-Fi network both are on, and presents the code's one-time token. Returns the plan (no sources yet
    /// when it is new). Throws <see cref="TransferException"/> with a message for the user.
    /// </summary>
    public async Task<BackupPlan> SetUpAsync(PairingCode code, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(code.BackupToken))
            throw new TransferException(TransferErrorKind.Protocol,
                L.T("This is a pairing code, not a backup code. On the computer, open Settings → Backup → Add a phone, and scan that code."));
        var endpoints = code.ToDevice().Endpoints.Concat(_discovery.Find(code.DeviceId)?.Endpoints ?? Array.Empty<IPEndPoint>())
            .Where(e => !_transfers.IsDirectAddress(e.Address))
            .DistinctBy(e => e.ToString())
            .ToList();
        if (endpoints.Count == 0)
            throw new TransferException(TransferErrorKind.ConnectFailed,
                L.T("Couldn't reach {0}. Make sure this phone and the computer are on the same Wi-Fi, then scan the code again.", code.Name));

        BackupResponseMessage response;
        string fingerprint;
        try
        {
            await using var connection = await SecureTransport.ConnectAsync(_transfers.Identity, _transfers.CreateHello(ConnectionPurpose.Backup),
                endpoints, code.Fingerprint, code.Name, cancellationToken).ConfigureAwait(false);
            await connection.Channel.SendAsync(FrameType.BackupRequest, new BackupRequestMessage { Action = BackupActions.Setup, Token = code.BackupToken },
                cancellationToken).ConfigureAwait(false);
            response = await connection.Channel.ReadMessageAsync<BackupResponseMessage>(FrameType.BackupResponse, TimeSpan.FromSeconds(30), cancellationToken)
                .ConfigureAwait(false);
            fingerprint = connection.RemoteFingerprint;
        }
        catch (TransferException ex) when (ex.Error.Kind == TransferErrorKind.ConnectFailed)
        {
            throw new TransferException(new TransferError(TransferErrorKind.ConnectFailed,
                L.T("Couldn't reach {0}. Make sure this phone and the computer are on the same Wi-Fi, then scan the code again.", code.Name),
                ex.Error.Details), ex);
        }

        if (!response.Ok) throw new TransferException(TransferErrorKind.Declined, response.Message ?? L.T("{0} didn't accept the backup.", code.Name));

        BackupPlan? plan = null;
        _settings.Update(s =>
        {
            var existing = s.Backups.FirstOrDefault(b => b.TargetId == code.DeviceId);
            s.Backups.RemoveAll(b => b.TargetId == code.DeviceId);
            plan = existing ?? new BackupPlan();
            plan.TargetId = code.DeviceId;
            plan.TargetName = string.IsNullOrWhiteSpace(response.Name) ? code.Name : AppSettings.NormalizeDeviceName(response.Name);
            plan.TargetFingerprint = fingerprint.ToLowerInvariant();
            plan.Addresses = endpoints.Select(e => e.Address.ToString()).Distinct().Take(4).ToList();
            plan.SetUpAt = DateTimeOffset.UtcNow;
            plan.LastError = null;
            s.Backups.Add(plan);
        });
        foreach (var endpoint in endpoints) _discovery.AddUnicastTarget(endpoint.Address);
        Log.Info($"Backup: set up to {plan!.TargetName} ({plan.TargetId}, {DeviceShort(plan.TargetFingerprint)})");
        SetUp?.Invoke(plan);
        Changed?.Invoke();
        return plan;
    }

    /// <summary>
    /// Saves what is backed up and whether automatically (the rest of <paramref name="plan"/>, such as how the last
    /// backup went, stays as recorded); a changed plan is checked right away.
    /// </summary>
    public void Save(BackupPlan plan)
    {
        BackupPlan? before = null;
        _settings.Update(s =>
        {
            if (s.Backups.FirstOrDefault(b => b.TargetId == plan.TargetId) is not { } stored) return;
            before = new BackupPlan { Sources = stored.Sources.Select(c => c.Copy()).ToList() };
            var announced = stored.Sources.ToDictionary(c => c.Kind + "|" + c.Address, c => c.AnnouncedFolders);
            stored.Sources = plan.Sources.Select(c => c.Copy()).ToList();
            // What the phone already told the user about stays told (recorded while the screen had an older copy).
            foreach (var source in stored.Sources)
                if (announced.TryGetValue(source.Kind + "|" + source.Address, out var told))
                    source.AnnouncedFolders = source.AnnouncedFolders.Union(told).ToList();
            stored.Automatic = plan.Automatic;
            stored.KeepFolderNames = plan.KeepFolderNames;
        });
        plan = _settings.Current.Backups.FirstOrDefault(b => b.TargetId == plan.TargetId) ?? plan;
        _lastStarted.TryRemove(plan.TargetId, out _);
        if (before != null && Narrows(before, plan) && _running.TryGetValue(plan.TargetId, out var running))
        {
            // A running backup sends the list it made when it started: what was just switched off would still go.
            // It stops, and starts again at once with the new choices (what is already there isn't sent again).
            Log.Info($"Backup to {plan.TargetName}: less is backed up now; the running backup starts again with the new choices");
            _restart[plan.TargetId] = true;
            running.Cancel();
        }

        Changed?.Invoke();
        if (plan.Automatic && Reachable(plan) is { } device) TryStart(plan, device, automatic: true);
    }

    /// <summary>
    /// <paramref name="after"/> backs up less than <paramref name="before"/>: a source switched off or removed, a kind
    /// of file unticked, "All folders" unticked or a folder unticked.
    /// </summary>
    internal static bool Narrows(BackupPlan before, BackupPlan after)
    {
        foreach (var old in before.Sources.Where(s => s.Enabled))
        {
            var now = after.Sources.FirstOrDefault(s => s.Enabled && s.Kind == old.Kind && s.Address == old.Address);
            if (now == null || old.EffectiveTypes().Except(now.EffectiveTypes()).Any()) return true;
            if (old.Kind == BackupSourceKinds.Folder) continue;
            if (old.AllFolders && !now.AllFolders) return true;
            if (!old.AllFolders && !now.AllFolders && old.Folders.Except(now.Folders).Any()) return true;
        }

        return false;
    }

    /// <summary>Stops backing up to a computer (what was backed up stays there).</summary>
    public void Remove(string targetId)
    {
        if (_running.TryGetValue(targetId, out var session)) session.Cancel();
        _settings.Update(s => s.Backups.RemoveAll(b => b.TargetId == targetId));
        Changed?.Invoke();
    }

    /// <summary>The backup running to <paramref name="targetId"/>, if any.</summary>
    public TransferSession? Running(string targetId) => _running.TryGetValue(targetId, out var session) ? session : null;

    /// <summary>The computer is on the same network now (and is who it was at setup, as far as it says).</summary>
    public bool IsReachable(BackupPlan plan) => Reachable(plan) != null;

    /// <summary>
    /// Backs up now. Throws <see cref="TransferException"/> when the computer isn't on this network. Returns the run
    /// (a running one, if a backup is already going).
    /// </summary>
    public TransferSession BackUpNow(string targetId)
    {
        var plan = _settings.Current.Backups.FirstOrDefault(b => b.TargetId == targetId)
                   ?? throw new InvalidOperationException("No backup to that computer.");
        var device = Reachable(plan) ?? throw new TransferException(TransferErrorKind.ConnectFailed,
            L.T("{0} isn't on this Wi-Fi right now. The backup runs by itself as soon as both are on the same Wi-Fi.", plan.TargetName));
        return TryStart(plan, device, automatic: false) ?? Running(targetId)!;
    }

    /// <summary>
    /// For a background job (Android): starts the backups that are due, waiting up to <paramref name="findFor"/> for
    /// their computers to show up on the network, then until they finish (or <paramref name="cancellationToken"/>).
    /// </summary>
    public async Task RunDueAsync(TimeSpan findFor, CancellationToken cancellationToken)
    {
        var plans = _settings.Current.Backups.Where(b => b.Automatic && b.HasAnythingToBackUp).ToList();
        if (plans.Count == 0) return;
        _discovery.Refresh();
        var deadline = Environment.TickCount64 + (long)findFor.TotalMilliseconds;
        var started = new List<TransferSession>();
        var pending = plans.ToList();
        while (pending.Count > 0 && Environment.TickCount64 < deadline)
        {
            foreach (var plan in pending.ToList())
            {
                if (Reachable(plan) is not { } device) continue;
                pending.Remove(plan);
                if (TryStart(plan, device, automatic: true) is { } session) started.Add(session);
                else if (Running(plan.TargetId) is { } running) started.Add(running);
            }

            if (pending.Count > 0) await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }

        foreach (var session in started)
            while (!session.IsFinished) await Task.Delay(500, cancellationToken).ConfigureAwait(false);
    }

    private void OnDevice(object? sender, DeviceEventArgs e)
    {
        var plan = _settings.Current.Backups.FirstOrDefault(b => b.TargetId == e.Device.Id);
        if (plan == null) return;
        var wasPresent = _present.TryGetValue(plan.TargetId, out var present) && present;
        if (Reachable(plan) is not { } device) return;
        _present[plan.TargetId] = true;
        // Back on the same network (home again): check now. Otherwise once an hour.
        if (!wasPresent) _lastStarted.TryRemove(plan.TargetId, out _);
        if (plan.Automatic) TryStart(plan, device, automatic: true);
    }

    private void OnDeviceLost(object? sender, DeviceEventArgs e) => _present[e.Device.Id] = false;

    private void CheckAll()
    {
        try
        {
            foreach (var plan in _settings.Current.Backups.Where(b => b.Automatic))
                if (Reachable(plan) is { } device) TryStart(plan, device, automatic: true);
        }
        catch (Exception ex)
        {
            Log.Warn("Backup: checking failed", ex);
        }
    }

    /// <summary>The computer as found on the shared network, pinned to its certificate; null when it isn't here.</summary>
    private DeviceInfo? Reachable(BackupPlan plan)
    {
        var device = _discovery.Find(plan.TargetId);
        if (device == null) return null;
        // An announcement anyone could send names another certificate: not our computer (connecting would fail anyway).
        if (!string.IsNullOrEmpty(device.Fingerprint) && !string.Equals(device.Fingerprint, plan.TargetFingerprint, StringComparison.OrdinalIgnoreCase))
        {
            if (_warnedMismatch.TryAdd(plan.TargetId, true))
                Log.Warn($"Backup: {device.Name} announces another certificate than the one set up; not backing up to it");
            return null;
        }

        // The same network only: never a direct link (nor Bluetooth, which has no address).
        var endpoints = device.Endpoints.Where(e => !_transfers.IsDirectAddress(e.Address)).ToList();
        return endpoints.Count == 0 ? null : device with { Fingerprint = plan.TargetFingerprint, Endpoints = endpoints };
    }

    private TransferSession? TryStart(BackupPlan plan, DeviceInfo device, bool automatic)
    {
        lock (_gate)
        {
            if (_running.ContainsKey(plan.TargetId)) return null;
            if (!plan.HasAnythingToBackUp) return null;
            var now = Environment.TickCount64;
            if (automatic && _lastStarted.TryGetValue(plan.TargetId, out var last))
            {
                var since = TimeSpan.FromMilliseconds(now - last);
                if (since < MinimumGap || (since < RecheckInterval && plan.LastError == null)) return null;
            }

            _lastStarted[plan.TargetId] = now;
            var targetId = plan.TargetId;
            var session = _transfers.SendBackup(device,
                () => _discovery.Find(targetId) is { } found && Reachable(plan) is { } current ? current : device with { Endpoints = Array.Empty<IPEndPoint>() },
                token =>
                {
                    var fresh = new List<(BackupSource Source, string Folder)>();
                    var manifest = BackupManifest.Build(plan, _sources, _transfers.ExternalFiles, token, fresh);
                    Announce(plan, fresh);
                    return manifest;
                },
                L.T("Backup to {0}", plan.TargetName));
            _running[plan.TargetId] = session;
            Log.Info($"Backup to {plan.TargetName}: {(automatic ? "checking (automatic)" : "checking (Back up now)")}");
            Changed?.Invoke();
            return session;
        }
    }

    /// <summary>New folders left out of a backup: told once each (and remembered as told).</summary>
    private void Announce(BackupPlan plan, List<(BackupSource Source, string Folder)> fresh)
    {
        if (fresh.Count == 0) return;
        var told = new List<(string Kind, List<string> Folders)>();
        _settings.Update(s =>
        {
            if (s.Backups.FirstOrDefault(b => b.TargetId == plan.TargetId) is not { } stored) return;
            foreach (var group in fresh.GroupBy(f => f.Source.Kind))
            {
                if (stored.Sources.FirstOrDefault(c => c.Kind == group.Key) is not { } source) continue;
                var folders = group.Select(f => f.Folder).Where(f => !source.AnnouncedFolders.Contains(f)).Distinct().ToList();
                if (folders.Count == 0) continue;
                source.AnnouncedFolders.AddRange(folders);
                told.Add((group.Key, folders));
            }
        });
        foreach (var (kind, folders) in told)
        {
            var names = folders.Select(f => f.Length == 0 ? L.T("Main storage") : f[(f.LastIndexOf('/') + 1)..]).ToList();
            Log.Info($"Backup: {names.Count} new {kind} folder(s) not backed up until chosen");
            NewFolders?.Invoke(plan.TargetName, kind, names);
        }
    }

    private void OnSessionFinished(TransferSession session)
    {
        if (!session.IsBackup) return;
        var snapshot = session.GetSnapshot();
        if (session.Direction == TransferDirection.Receive)
        {
            if (snapshot.CompletedFiles == 0) return;
            _settings.Update(s =>
            {
                if (s.BackupPartners.FirstOrDefault(p => string.Equals(p.Fingerprint, session.PeerFingerprint, StringComparison.OrdinalIgnoreCase)) is { } partner)
                {
                    partner.LastBackupAt = DateTimeOffset.UtcNow;
                    partner.LastFileCount = snapshot.CompletedFiles;
                }
            });
            Changed?.Invoke();
            return;
        }

        if (!_running.TryRemove(new KeyValuePair<string, TransferSession>(session.PeerId, session))) return;
        var done = snapshot.State is TransferState.Completed or TransferState.CompletedWithErrors;
        _settings.Update(s =>
        {
            if (s.Backups.FirstOrDefault(b => b.TargetId == session.PeerId) is not { } plan) return;
            if (done)
            {
                plan.LastCompletedAt = DateTimeOffset.UtcNow;
                if (snapshot.CompletedFiles > 0) plan.LastFileCount = snapshot.CompletedFiles;
                plan.LastError = snapshot.State == TransferState.CompletedWithErrors ? snapshot.Error?.Message : null;
            }
            else if (snapshot.State != TransferState.Cancelled)
            {
                plan.LastError = snapshot.Error?.Message ?? L.T("The backup didn't finish.");
            }
        });
        Log.Info($"Backup to {session.PeerName}: {snapshot.State}, {snapshot.CompletedFiles} sent, {snapshot.SkippedFiles} already there");
        Changed?.Invoke();
        if (_restart.TryRemove(session.PeerId, out _)
            && _settings.Current.Backups.FirstOrDefault(b => b.TargetId == session.PeerId) is { } current && Reachable(current) is { } device)
            TryStart(current, device, automatic: false);
    }

    private static string DeviceShort(string fingerprint) => Identity.DeviceIdentity.ShortCode(fingerprint);

    public void Dispose()
    {
        _timer.Dispose();
        _discovery.DeviceFound -= OnDevice;
        _discovery.DeviceChanged -= OnDevice;
        _discovery.DeviceLost -= OnDeviceLost;
        _transfers.SessionFinished -= OnSessionFinished;
    }
}
