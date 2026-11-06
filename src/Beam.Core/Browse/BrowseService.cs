using System.Net;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using Beam.Core.Diagnostics;
using Beam.Core.Direct;
using Beam.Core.Discovery;
using Beam.Core.Files;
using Beam.Core.Localization;
using Beam.Core.Protocol;
using Beam.Core.Settings;
using Beam.Core.Transfer;

namespace Beam.Core.Browse;

/// <summary>Someone browsing this device right now (shown with a Stop button).</summary>
public sealed class BrowseVisit
{
    internal BrowseVisit(string deviceId, string deviceName, string fingerprint, CancellationTokenSource stop)
    {
        DeviceId = deviceId;
        DeviceName = deviceName;
        Fingerprint = fingerprint;
        Stopper = stop;
    }

    public string DeviceId { get; }

    public string DeviceName { get; }

    public string Fingerprint { get; }

    internal CancellationTokenSource Stopper { get; }
}

/// <summary>What a platform can make previews of (photos, videos); null where there is nothing to show.</summary>
public interface IBrowseThumbnails
{
    /// <summary>A JPEG of the file, at most <paramref name="max"/> pixels across; null when it isn't a picture or video.</summary>
    byte[]? Create(string path, int max);
}

/// <summary>
/// Browsing the shared folders of a trusted device, and letting trusted devices browse this one.
/// <para>
/// <b>Who:</b> only trusted devices (certificate fingerprints added with a code or "Always accept"), and only when this
/// device has browsing switched on. Each visit is asked about ("Always allow" skips that for a device); a visit can be
/// stopped at any time. <b>What:</b> only the shared folders, never above them, never through links, never hidden or
/// system files. Files are listed and read; with "Allow adding files" they can be added (never replaced, changed or
/// deleted). Everything goes over the TLS connection both certificates are checked on, on the shared Wi-Fi or a direct
/// link, and every file is checked (SHA-256) after it arrives.
/// </para>
/// </summary>
public sealed partial class BrowseService
{
    /// <summary>A visit that dropped (Wi-Fi to direct link and back) carries on without asking again for this long.</summary>
    internal static TimeSpan SessionGrace { get; set; } = TimeSpan.FromMinutes(10);

    internal static TimeSpan ApprovalTimeout { get; set; } = TimeSpan.FromSeconds(60);

    private const int ChunkSize = 256 * 1024;
    private const int MaxVisits = 4;
    private const int MaxEntries = 20_000;

    private readonly TransferService _transfers;
    private readonly DiscoveryService _discovery;
    private readonly DirectManager _direct;
    private readonly SettingsStore _settings;
    private readonly IIncomingTransferHandler _handler;
    private readonly Func<IReadOnlyList<(string Name, string Path)>> _defaultFolders;
    private readonly IBrowseThumbnails? _thumbnails;
    private readonly IExternalFiles? _external;
    private readonly Func<string, bool> _heardNearby;

    // Sessions given to a device: its fingerprint and until when it may come back without being asked.
    private readonly ConcurrentDictionary<string, (string Fingerprint, long Until)> _sessions = new();
    private readonly ConcurrentDictionary<BrowseVisit, int> _visits = new();
    private readonly SemaphoreSlim _asking = new(1, 1);
    // Visits stopped by the user: not let back in (by a reconnect, or "always allowed") for a minute.
    private readonly ConcurrentDictionary<string, long> _stopped = new(StringComparer.OrdinalIgnoreCase);

    internal static TimeSpan StoppedFor { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Tests: a pause after each piece of a file read, to drop the connection half way.</summary>
    internal static TimeSpan ReadPause { get; set; }

    internal BrowseService(TransferService transfers, DiscoveryService discovery, DirectManager direct, SettingsStore settings,
        IIncomingTransferHandler handler, Func<IReadOnlyList<(string Name, string Path)>> defaultFolders, IBrowseThumbnails? thumbnails,
        Func<string, bool>? heardNearby = null)
    {
        _heardNearby = heardNearby ?? (_ => false);
        _transfers = transfers;
        _discovery = discovery;
        _direct = direct;
        _settings = settings;
        _handler = handler;
        _defaultFolders = defaultFolders;
        _thumbnails = thumbnails;
        _external = transfers.ExternalFiles;
        _transfers.BrowseHandler = HandleAsync;
        // Joined a browsing device's direct network: stay while it browses (no transfer runs to show it's in use).
        _discovery.DeviceFound += (_, e) => RememberAddresses(e.Device);
        _discovery.DeviceChanged += (_, e) => RememberAddresses(e.Device);
        _direct.InUseBy = fingerprint => _visits.Keys.Any(v => string.Equals(v.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Someone started or stopped browsing this device (raised on any thread).</summary>
    public event Action? VisitsChanged;

    /// <summary>A browsing device added a file to a shared folder (its full path here).</summary>
    public event Action<BrowseVisit, string>? FileAdded;

    /// <summary>Who is browsing this device now.</summary>
    public IReadOnlyList<BrowseVisit> Visits => _visits.Keys.ToList();

    /// <summary>Ends a visit now. The device has to ask again (or be always allowed) to come back.</summary>
    public void Stop(BrowseVisit visit)
    {
        foreach (var session in _sessions.Where(s => string.Equals(s.Value.Fingerprint, visit.Fingerprint, StringComparison.OrdinalIgnoreCase)).ToList())
            _sessions.TryRemove(session.Key, out _);
        _stopped[visit.Fingerprint] = Environment.TickCount64 + (long)StoppedFor.TotalMilliseconds;
        Cancel(visit);
        Log.Info($"Browse: stopped {visit.DeviceName}'s visit");
    }

    /// <summary>
    /// Switches browsing on or off. The first time it is on, the usual folders are shared (the user changes them in
    /// Settings → Browsing). Off ends every visit.
    /// </summary>
    public void SetEnabled(bool enabled)
    {
        _settings.Update(s =>
        {
            s.BrowsingEnabled = enabled;
            if (enabled && !s.SharedFoldersSetUp)
            {
                s.SharedFoldersSetUp = true;
                foreach (var (name, path) in _defaultFolders())
                    if (Directory.Exists(path) && !s.SharedFolders.Any(f => SamePath(f.Path, path)))
                        s.SharedFolders.Add(new SharedFolder { Id = NewId(), Name = name, Path = path });
            }
        });
        if (!enabled) StopAll();
        Log.Info($"Browse: browsing this device is {(enabled ? "on" : "off")}");
    }

    /// <summary>Shares another folder (a path this device can read).</summary>
    public void AddFolder(string path, string? name = null)
    {
        _settings.Update(s =>
        {
            if (s.SharedFolders.Any(f => SamePath(f.Path, path))) return;
            s.SharedFolders.Add(new SharedFolder
            {
                Id = NewId(),
                Name = string.IsNullOrWhiteSpace(name) ? Path.GetFileName(Path.TrimEndingDirectorySeparator(path)) : name,
                Path = path,
            });
        });
    }

    /// <summary>Stops sharing a folder (effective at once, also for devices browsing now).</summary>
    public void RemoveFolder(string id) => _settings.Update(s => s.SharedFolders.RemoveAll(f => f.Id == id));

    /// <summary>A trusted device no longer browses without being asked.</summary>
    public void Forget(string fingerprint) =>
        _settings.Update(s =>
        {
            foreach (var device in s.TrustedDevices.Where(t => string.Equals(t.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase)))
                device.AlwaysBrowse = false;
        });

    /// <summary>Tests: every connection drops (as when the Wi-Fi goes); visitors may come back without being asked.</summary>
    internal void DropAll()
    {
        foreach (var visit in _visits.Keys) Cancel(visit);
    }

    /// <summary>A trusted device heard on the shared network: keep where, to reach it there next time.</summary>
    internal void RememberAddresses(DeviceInfo device)
    {
        var addresses = device.Endpoints.Where(e => !_transfers.IsDirectAddress(e.Address)).Select(e => e.ToString()).ToList();
        if (addresses.Count == 0) return;
        var trusted = _settings.Current.TrustedDevices.FirstOrDefault(t => string.Equals(t.Fingerprint, device.Fingerprint, StringComparison.OrdinalIgnoreCase));
        if (trusted == null || trusted.Addresses.Take(addresses.Count).SequenceEqual(addresses)) return;
        _settings.Update(s =>
        {
            foreach (var t in s.TrustedDevices.Where(t => string.Equals(t.Fingerprint, device.Fingerprint, StringComparison.OrdinalIgnoreCase)))
                t.Addresses = addresses.Concat(t.Addresses).Distinct().Take(4).ToList();
        });
    }

    internal bool HeardNearby(string deviceId) => _heardNearby(deviceId);

    internal IReadOnlyList<IPEndPoint> RememberedAddresses(string fingerprint) =>
        _settings.Current.TrustedDevices.FirstOrDefault(t => string.Equals(t.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase))?.Addresses
            .Select(a => IPEndPoint.TryParse(a, out var e) ? e : null).OfType<IPEndPoint>().ToList()
        ?? (IReadOnlyList<IPEndPoint>)Array.Empty<IPEndPoint>();

    private void StopAll()
    {
        _sessions.Clear();
        foreach (var visit in _visits.Keys) Cancel(visit);
    }

    private static void Cancel(BrowseVisit visit)
    {
        try { visit.Stopper.Cancel(); }
        catch (ObjectDisposedException) { /* already over */ }
    }

    private static string NewId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
            OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    // ---------------------------------------------------------------- being browsed

    private async Task HandleAsync(PeerConnection connection, CancellationToken token)
    {
        var channel = connection.Channel;
        var open = await channel.ReadMessageAsync<BrowseRequestMessage>(FrameType.BrowseRequest, TimeSpan.FromSeconds(30), token).ConfigureAwait(false);
        var hello = connection.RemoteHello;
        var fingerprint = connection.RemoteFingerprint;
        var name = AppSettings.NormalizeDeviceName(hello.DeviceName);
        if (open.Action != BrowseActions.Open)
        {
            await RefuseAsync(channel, BrowseReasons.Failed, "Not a browsing request.", token).ConfigureAwait(false);
            return;
        }

        var settings = _settings.Current;
        var trusted = settings.TrustedDevices.FirstOrDefault(t => string.Equals(t.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase));
        if (trusted == null)
        {
            // Not even told whether browsing is on: only trusted devices learn anything.
            Log.Warn($"Browse: refused \"{name}\" ({hello.DeviceId}): not a trusted device");
            await RefuseAsync(channel, BrowseReasons.NotTrusted,
                L.T("{0} doesn't trust this device. Add each other as trusted devices first (Settings → Trusted devices).", settings.DeviceName), token).ConfigureAwait(false);
            return;
        }

        if (_stopped.TryGetValue(fingerprint, out var stoppedUntil) && stoppedUntil > Environment.TickCount64)
        {
            await RefuseAsync(channel, BrowseReasons.Stopped, L.T("{0} ended browsing.", settings.DeviceName), token).ConfigureAwait(false);
            return;
        }

        if (!settings.BrowsingEnabled)
        {
            await RefuseAsync(channel, BrowseReasons.Off,
                L.T("Browsing is off on {0}. Turn it on there: Settings → Browsing.", settings.DeviceName), token).ConfigureAwait(false);
            return;
        }

        if (_visits.Count >= MaxVisits && !_visits.Keys.Any(v => v.Fingerprint == fingerprint))
        {
            await RefuseAsync(channel, BrowseReasons.Failed, L.T("{0} is busy. Try again in a moment.", settings.DeviceName), token).ConfigureAwait(false);
            return;
        }

        // Carrying on (after a reconnect, or a second connection for a download): no question again.
        var now = Environment.TickCount64;
        var resumed = open.Session != null && _sessions.TryGetValue(open.Session, out var known)
                                           && string.Equals(known.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase) && known.Until > now;
        if (!resumed && !trusted.AlwaysBrowse)
        {
            var decision = await AskAsync(new IncomingBrowseRequest(hello.DeviceId, name, fingerprint), token).ConfigureAwait(false);
            if (decision == BrowseDecision.Deny)
            {
                Log.Info($"Browse: {name} wasn't allowed to browse");
                await RefuseAsync(channel, BrowseReasons.Declined, L.T("{0} didn't allow browsing.", settings.DeviceName), token).ConfigureAwait(false);
                return;
            }

            if (decision == BrowseDecision.AllowAlways)
                _settings.Update(s =>
                {
                    foreach (var device in s.TrustedDevices.Where(t => string.Equals(t.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase)))
                        device.AlwaysBrowse = true;
                });
        }

        var session = resumed ? open.Session! : DirectSession.NewToken();
        _sessions[session] = (fingerprint, long.MaxValue); // while connected; the grace starts when it ends
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var visit = new BrowseVisit(hello.DeviceId, name, fingerprint, stop);
        var first = !_visits.Keys.Any(v => string.Equals(v.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase));
        _visits[visit] = 0;
        if (first) Log.Info($"Browse: {name} ({hello.DeviceId}) is browsing this device{(resumed ? " (carrying on)" : "")}");
        VisitsChanged?.Invoke();
        try
        {
            await channel.SendAsync(FrameType.BrowseResponse, new BrowseResponseMessage
            {
                Ok = true,
                Session = session,
                Name = settings.DeviceName,
                CanAdd = settings.BrowseAllowAdding,
                Roots = settings.SharedFolders.Select(f => new BrowseRoot { Id = f.Id, Name = f.Name }).ToList(),
            }, stop.Token).ConfigureAwait(false);
            await ServeAsync(visit, channel, stop.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or EndOfStreamException)
        {
            // the visitor left, the connection dropped, or the visit was stopped
        }
        finally
        {
            _visits.TryRemove(visit, out _);
            if (_sessions.TryGetValue(session, out var entry) && entry.Until == long.MaxValue)
                _sessions[session] = (fingerprint, Environment.TickCount64 + (long)SessionGrace.TotalMilliseconds);
            if (!_visits.Keys.Any(v => string.Equals(v.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase)))
                Log.Info($"Browse: {name} stopped browsing");
            VisitsChanged?.Invoke();
        }
    }

    /// <summary>One question at a time (two devices asking at once get asked one after the other).</summary>
    private async Task<BrowseDecision> AskAsync(IncomingBrowseRequest request, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(ApprovalTimeout);
        try
        {
            await _asking.WaitAsync(timeout.Token).ConfigureAwait(false);
            try
            {
                return await _handler.ApproveBrowseAsync(request, timeout.Token).ConfigureAwait(false);
            }
            finally
            {
                _asking.Release();
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return BrowseDecision.Deny; // nobody answered
        }
    }

    private static Task RefuseAsync(FrameChannel channel, string reason, string message, CancellationToken token) =>
        channel.SendAsync(FrameType.BrowseResponse, new BrowseResponseMessage { Ok = false, Reason = reason, Message = message }, token);

    private async Task ServeAsync(BrowseVisit visit, FrameChannel channel, CancellationToken token)
    {
        while (true)
        {
            var request = await channel.ReadMessageAsync<BrowseRequestMessage>(FrameType.BrowseRequest, TimeSpan.FromMinutes(30), token)
                .ConfigureAwait(false);
            var settings = _settings.Current; // read every time: a folder unshared, or browsing switched off, counts at once
            if (!settings.BrowsingEnabled || !settings.TrustedDevices.Any(t => string.Equals(t.Fingerprint, visit.Fingerprint, StringComparison.OrdinalIgnoreCase)))
                return;
            switch (request.Action)
            {
                case BrowseActions.List:
                    await ListAsync(settings, request, channel, token).ConfigureAwait(false);
                    break;
                case BrowseActions.Read:
                    await ReadAsync(settings, request, channel, token).ConfigureAwait(false);
                    break;
                case BrowseActions.Thumbnail:
                    await ThumbnailAsync(settings, request, channel, token).ConfigureAwait(false);
                    break;
                case BrowseActions.Write:
                    await WriteAsync(visit, settings, request, channel, token).ConfigureAwait(false);
                    break;
                default:
                    await RefuseAsync(channel, BrowseReasons.Failed, "Unknown request.", token).ConfigureAwait(false);
                    break;
            }
        }
    }

    private Task NotFoundAsync(FrameChannel channel, CancellationToken token) =>
        RefuseAsync(channel, BrowseReasons.NotFound, L.T("This isn't shared (any more), or it was moved."), token);

    private async Task ListAsync(AppSettings settings, BrowseRequestMessage request, FrameChannel channel, CancellationToken token)
    {
        if (Resolve(settings, request.Root, request.Path) is not { } folder || !Directory.Exists(folder))
        {
            await NotFoundAsync(channel, token).ConfigureAwait(false);
            return;
        }

        var entries = new List<BrowseEntry>();
        try
        {
            var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = Hidden, RecurseSubdirectories = false };
            foreach (var info in new DirectoryInfo(folder).EnumerateFileSystemInfos("*", options))
            {
                if (IsHiddenName(info.Name) || info.LinkTarget != null) continue;
                entries.Add(new BrowseEntry
                {
                    Name = info.Name,
                    IsFolder = info is DirectoryInfo,
                    Size = info is FileInfo file ? file.Length : 0,
                    Modified = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds(),
                });
                if (entries.Count >= MaxEntries) break;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Browse: couldn't list a shared folder ({ex.GetType().Name})");
            await RefuseAsync(channel, BrowseReasons.Failed, L.T("This folder can't be opened."), token).ConfigureAwait(false);
            return;
        }

        await channel.SendAsync(FrameType.BrowseResponse, new BrowseResponseMessage { Ok = true, Entries = entries }, token).ConfigureAwait(false);
    }

    private async Task ReadAsync(AppSettings settings, BrowseRequestMessage request, FrameChannel channel, CancellationToken token)
    {
        if (Resolve(settings, request.Root, request.Path) is not { } path || !File.Exists(path))
        {
            await NotFoundAsync(channel, token).ConfigureAwait(false);
            return;
        }

        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan | FileOptions.Asynchronous);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await RefuseAsync(channel, BrowseReasons.Failed, L.T("This file can't be opened."), token).ConfigureAwait(false);
            return;
        }

        await using (stream)
        {
            var size = stream.Length;
            var offset = Math.Clamp(request.Offset, 0, size);
            await channel.SendAsync(FrameType.BrowseResponse, new BrowseResponseMessage { Ok = true, Size = size }, token).ConfigureAwait(false);
            // The hash covers the whole file, also the part the other side already has (it hashes its copy the same way).
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var position = await BrowseStreaming.SendAsync(channel, stream, offset, size, hash, null, ReadPause, token).ConfigureAwait(false);

            await channel.SendAsync(FrameType.BrowseResponse, new BrowseResponseMessage
            {
                Ok = position == size,
                Size = position,
                Hash = Convert.ToHexString(hash.GetHashAndReset()),
            }, token).ConfigureAwait(false);
        }
    }

    private async Task ThumbnailAsync(AppSettings settings, BrowseRequestMessage request, FrameChannel channel, CancellationToken token)
    {
        var path = Resolve(settings, request.Root, request.Path);
        byte[]? image = null;
        if (path != null && File.Exists(path) && _thumbnails != null)
        {
            try
            {
                image = await Task.Run(() => _thumbnails.Create(path, Math.Clamp(request.Max, 32, 512)), token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Info($"Browse: no preview ({ex.GetType().Name})");
            }
        }

        await channel.SendAsync(FrameType.BrowseResponse, new BrowseResponseMessage
        {
            Ok = image is { Length: > 0 and < 48 * 1024 },
            Image = image is { Length: > 0 and < 48 * 1024 } ? Convert.ToBase64String(image) : null,
        }, token).ConfigureAwait(false);
    }

    private async Task WriteAsync(BrowseVisit visit, AppSettings settings, BrowseRequestMessage request, FrameChannel channel, CancellationToken token)
    {
        if (!settings.BrowseAllowAdding)
        {
            await RefuseAsync(channel, BrowseReasons.ReadOnly, L.T("{0} doesn't allow adding files.", settings.DeviceName), token).ConfigureAwait(false);
            return;
        }

        var folder = Resolve(settings, request.Root, request.Path);
        var name = request.Name is { Length: > 0 } n ? SafePath.SanitizeSegment(Path.GetFileName(n)) : null;
        if (folder == null || !Directory.Exists(folder) || name == null || IsHiddenName(name) || request.Size < 0)
        {
            await NotFoundAsync(channel, token).ConfigureAwait(false);
            return;
        }

        // The folder's own storage (on Android "/" is the read-only system partition with nothing free); unknown: the write says.
        var free = DiskSpace.GetAvailableBytes(folder);
        if (free >= 0 && request.Size > free - 64L * 1024 * 1024)
        {
            await RefuseAsync(channel, BrowseReasons.Failed, L.T("There isn't enough free space on {0}.", settings.DeviceName), token).ConfigureAwait(false);
            return;
        }

        var part = Path.Combine(folder, "." + name + "." + DirectSession.NewToken()[..8] + ".beam-part");
        var kept = false;
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var writer = new PipelinedFileWriter(
                             new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.Asynchronous), name))
            {
                await channel.SendAsync(FrameType.BrowseResponse, new BrowseResponseMessage { Ok = true }, token).ConfigureAwait(false);
                await BrowseStreaming.ReceiveAsync(channel, writer, 0, request.Size, hash, null, TimeSpan.FromMinutes(2), token).ConfigureAwait(false);
                await writer.CompleteAsync().ConfigureAwait(false);
            }

            var commit = await channel.ReadMessageAsync<BrowseRequestMessage>(FrameType.BrowseRequest, TimeSpan.FromMinutes(2), token).ConfigureAwait(false);
            var actual = Convert.ToHexString(hash.GetHashAndReset());
            if (commit.Action != BrowseActions.Commit || !string.Equals(commit.Hash, actual, StringComparison.OrdinalIgnoreCase))
            {
                await RefuseAsync(channel, BrowseReasons.Failed, L.T("The file arrived damaged. Try again."), token).ConfigureAwait(false);
                return;
            }

            // Never over an existing file: a new name instead ("photo (1).jpg").
            var target = Path.Combine(folder, FileNaming.MakeUnique(name, c => FileNaming.Exists(Path.Combine(folder, c))));
            File.Move(part, target, overwrite: false);
            kept = true;
            if (request.Modified > 0)
            {
                try { File.SetLastWriteTimeUtc(target, DateTimeOffset.FromUnixTimeMilliseconds(request.Modified).UtcDateTime); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { /* the time is a nicety */ }
            }

            Log.Info($"Browse: {visit.DeviceName} added a file ({request.Size} bytes)");
            FileAdded?.Invoke(visit, target);
            await channel.SendAsync(FrameType.BrowseResponse, new BrowseResponseMessage { Ok = true, Name = Path.GetFileName(target) }, token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or LocalFileException && !token.IsCancellationRequested)
        {
            Log.Warn($"Browse: couldn't add a file ({ex.GetType().Name})");
            await RefuseAsync(channel, BrowseReasons.Failed, L.T("The file couldn't be saved on {0}.", settings.DeviceName), token).ConfigureAwait(false);
        }
        finally
        {
            if (!kept)
            {
                try { File.Delete(part); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* left as a hidden part file */ }
            }
        }
    }

    private const FileAttributes Hidden = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint;

    /// <summary>Dot-files and dot-folders (Android, Linux, macOS) and Beam's own unfinished files stay out of sight.</summary>
    private static bool IsHiddenName(string name) => name.StartsWith('.') || name.EndsWith(".beam-part", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The full path of something in a shared folder, or null when it isn't one: an unknown root, a path that isn't a
    /// plain relative one ("..", empty or odd segments), a hidden or system item on the way, or a link (which could
    /// lead outside). Checked again on every request, so unsharing a folder counts at once.
    /// </summary>
    internal static string? Resolve(AppSettings settings, string? rootId, string? path)
    {
        if (settings.SharedFolders.FirstOrDefault(f => f.Id == rootId) is not { } root) return null;
        string full;
        try
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root.Path));
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return null;
        }

        if (!Directory.Exists(full)) return null;
        if (string.IsNullOrEmpty(path)) return full;
        var segments = path.Split('/');
        if (segments.Length > SafePath.MaxDepth) return null;
        var current = full;
        foreach (var segment in segments)
        {
            if (segment.Length == 0 || segment is "." or ".." || segment.Length > 255 || IsHiddenName(segment)
                || segment.IndexOfAny(new[] { '\\', ':', '\0' }) >= 0 || segment.Any(char.IsControl))
                return null;
            current = Path.Combine(current, segment);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (!info.Exists) return null;
            if ((info.Attributes & Hidden) != 0 || info.LinkTarget != null) return null;
        }

        return SafePath.IsInside(full, current) ? current : null;
    }
}
