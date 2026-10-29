using Beam.Core.Localization;

namespace Beam.App.Platform;

/// <summary>A folder the user picked for received files: the local path to use, or why it can't be used.</summary>
public sealed record ReceiveFolderChoice(string? Path, string? Problem = null);

/// <summary>A button on a notification (e.g. Accept / Decline on incoming files).</summary>
public sealed record NotificationButton(string Label, Action OnClick);

/// <summary>Permissions a phone asks the user for, with Beam's explanation first.</summary>
public enum AppPermission
{
    /// <summary>Notifications: incoming files and finished transfers (Android 13+).</summary>
    Notifications,

    /// <summary>Direct connections and finding paired computers without Wi-Fi (Android: Nearby devices, or Location before 13).</summary>
    NearbyDevices,

    /// <summary>Backups: reading the phone's photos and videos (Android: Photos and videos).</summary>
    MediaLibrary,
}

/// <summary>How the check that the phone's owner is holding it went.</summary>
public enum OwnerCheck
{
    /// <summary>Fingerprint, face, PIN, pattern or password accepted.</summary>
    Confirmed,

    /// <summary>Cancelled, or not recognised.</summary>
    Refused,

    /// <summary>The phone has no screen lock: there is nothing to check against.</summary>
    NoScreenLock,
}

/// <summary>Operating-system integration. Everything OS-specific in the app goes through here.</summary>
public interface IPlatformServices
{
    /// <summary>A phone or tablet: no tray, no "start with Windows", received files always go to Downloads/Beam.</summary>
    bool IsPhone => false;

    /// <summary>Opens a folder in the file manager.</summary>
    void OpenFolder(string path);

    /// <summary>Opens the containing folder with the item selected.</summary>
    void RevealInFolder(string path);

    void OpenFile(string path);

    /// <summary>Opens a web or store link.</summary>
    void OpenUrl(string url);

    bool SupportsStartWithSystem { get; }

    bool GetStartWithSystem();

    void SetStartWithSystem(bool enabled);

    /// <summary>Shows a system notification. <paramref name="onActivated"/> runs (on any thread) when it is clicked.</summary>
    void ShowNotification(string title, string message, Action? onActivated = null);

    /// <summary>
    /// A notification that asks something (incoming files): with buttons where the system supports them, replaced or
    /// removed by <paramref name="tag"/>. Callbacks run on any thread.
    /// </summary>
    void ShowQuestionNotification(string tag, string title, string message, Action onActivated, IReadOnlyList<NotificationButton> buttons) =>
        ShowNotification(title, message, onActivated);

    /// <summary>Removes the notification shown with <paramref name="tag"/> (answered, or no longer relevant).</summary>
    void CancelNotification(string tag)
    {
    }

    /// <summary>True when the permission is granted (or not needed on this system).</summary>
    bool HasPermission(AppPermission permission) => true;

    /// <summary>Asks the system for the permission; true when granted.</summary>
    Task<bool> RequestPermissionAsync(AppPermission permission) => Task.FromResult(true);

    /// <summary>Android names Nearby devices "Location" before Android 13: the explanation says so.</summary>
    bool NearbyDevicesIsLocation => false;

    /// <summary>Where received files go by default; null means the Downloads folder.</summary>
    string? DefaultReceiveFolder => null;

    /// <summary>Turns what the folder picker returned into a folder Beam can save into (phones get content:// addresses).</summary>
    ReceiveFolderChoice CheckReceiveFolder(string picked) => new(picked);

    /// <summary>The device can scan QR codes (phones, with the camera).</summary>
    bool CanScanCodes => false;

    /// <summary>Scans a QR code with the camera; null when cancelled or unavailable.</summary>
    Task<string?> ScanCodeAsync() => Task.FromResult<string?>(null);

    /// <summary>True when the system firewall would stop devices on a direct connection from reaching Beam (Windows).</summary>
    Task<bool> FirewallBlocksDirectAsync() => Task.FromResult(false);

    /// <summary>
    /// True when this device's network is a "public" one in Windows and the firewall stops devices on it from reaching
    /// Beam (so nothing nearby could send to this computer).
    /// </summary>
    Task<bool> FirewallBlocksLocalNetworkAsync() => Task.FromResult(false);

    /// <summary>
    /// Allows Beam through the firewall for nearby devices: on the same network and on direct connections (Windows asks
    /// for administrator permission).
    /// </summary>
    Task<bool> AllowDirectThroughFirewallAsync() => Task.FromResult(true);

    /// <summary>This device can back up its photos, videos and folders to a computer (Android).</summary>
    bool SupportsBackup => false;

    /// <summary>
    /// A folder chosen for backups: keeps Beam's access to it after a restart (Android: a lasting permission to read
    /// it). Returns the address to keep, or null when it can't be read later.
    /// </summary>
    string? KeepFolderAccess(string picked) => picked;

    /// <summary>
    /// Asks the person holding the phone to prove they are its owner with the phone's own lock (fingerprint, face, PIN,
    /// pattern or password), as banking apps do. Desktops and test fakes answer <see cref="OwnerCheck.Confirmed"/>.
    /// </summary>
    Task<OwnerCheck> ConfirmOwnerAsync(string title, string reason) => Task.FromResult(OwnerCheck.Confirmed);

    /// <summary>
    /// A notice about this phone's backups with a "Stop backup" button that works even after Beam was closed
    /// (<paramref name="stop"/> runs where the app is still open).
    /// </summary>
    void ShowBackupNotification(string tag, string title, string message, string targetId, Action stop) =>
        ShowQuestionNotification(tag, title, message, () => { }, new[] { new NotificationButton(L.T("Stop backup"), stop) });

    /// <summary>The automatic backups changed: the system runs them in the background on Wi-Fi when there are any (Android).</summary>
    void ScheduleBackups(bool any)
    {
    }

    /// <summary>Lets the user pass Beam's log on, for troubleshooting (desktop: opens the log folder).</summary>
    void ShareLog(string logDirectory) => OpenFolder(logDirectory);
}
