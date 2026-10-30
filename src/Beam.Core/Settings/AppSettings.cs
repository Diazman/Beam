using System.Text.Json.Serialization;
using Beam.Core.Backup;
using Beam.Core.Storage;

namespace Beam.Core.Settings;

public enum ThemePreference
{
    System,
    Light,
    Dark,
}

/// <summary>How devices reach each other.</summary>
public enum ConnectionMethod
{
    /// <summary>Through the Wi-Fi network both devices are on (they keep their internet).</summary>
    SameNetwork,

    /// <summary>A direct Wi-Fi link between the two devices (Wi-Fi Direct): no router in between, works without any network.</summary>
    Direct,
}

/// <summary>A paired device this device knows (from its code, or an older version's direct connection): heard over Bluetooth, it is listed by name and checked by its certificate.</summary>
public sealed class KnownDirectNetwork
{
    public string DeviceId { get; set; } = "";

    public string Name { get; set; } = "";

    public string Fingerprint { get; set; } = "";

    public string Kind { get; set; } = "";
}

/// <summary>A device the user chose to always accept files from.</summary>
public sealed class TrustedDevice
{
    public string Fingerprint { get; set; } = "";

    public string DeviceId { get; set; } = "";

    public string Name { get; set; } = "";

    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class AppSettings
{
    public const int MaxDeviceNameLength = 40;

    public string DeviceName { get; set; } = DefaultDeviceName();

    /// <summary>Null means "use the Downloads folder".</summary>
    public string? ReceiveFolder { get; set; }

    public bool FirstRunCompleted { get; set; }

    public bool StartWithWindows { get; set; }

    /// <summary>Closing the window keeps Beam running in the notification area so files can still be received.</summary>
    public bool CloseToTray { get; set; } = true;

    public bool TrayHintShown { get; set; }

    /// <summary>Phones: Beam explained why it wants notifications and asked once.</summary>
    public bool NotificationPermissionAsked { get; set; }

    /// <summary>Completed transfers so far (decides when to ask for a rating).</summary>
    public int CompletedTransfers { get; set; }

    /// <summary>The "rate Beam" prompt was shown; it is never shown again.</summary>
    public bool RatingRequested { get; set; }

    /// <summary>Whether nearby computers can see this one.</summary>
    public bool Discoverable { get; set; } = true;

    public bool NotificationsEnabled { get; set; } = true;

    /// <summary>The name of this device's direct networks, created once (each session gets its own passphrase).</summary>
    public string? DirectNetworkName { get; set; }

    /// <summary>Paired devices known from scanning their code (the stored name is kept from older versions).</summary>
    public List<KnownDirectNetwork> KnownDirectNetworks { get; set; } = new();

    public bool NotifyOnIncomingRequest { get; set; } = true;

    public bool NotifyOnTransferFinished { get; set; } = true;

    public bool NotifyOnDeviceFound { get; set; }

    public ThemePreference Theme { get; set; } = ThemePreference.System;

    /// <summary>Same network (default) or a direct link; can also be switched during a transfer.</summary>
    public ConnectionMethod Connection { get; set; } = ConnectionMethod.SameNetwork;

    /// <summary>Interface language code ("en", "tr", "ru", "uz"); empty follows Windows.</summary>
    public string Language { get; set; } = "";

    public List<TrustedDevice> TrustedDevices { get; set; } = new();

    /// <summary>This device's backups to computers (a phone; one per computer).</summary>
    public List<BackupPlan> Backups { get; set; } = new();

    /// <summary>Devices allowed to back up to this computer, each into its own folder.</summary>
    public List<BackupPartner> BackupPartners { get; set; } = new();

    /// <summary>Where new backup partners' folders go; empty: Documents/Beam Backups.</summary>
    public string? BackupFolder { get; set; }

    [JsonIgnore]
    public string EffectiveBackupFolder => string.IsNullOrWhiteSpace(BackupFolder)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Beam Backups")
        : BackupFolder!;

    /// <summary>Addresses the user added by hand because automatic discovery could not find them.</summary>
    public List<string> ManualAddresses { get; set; } = new();

    [JsonIgnore]
    public string EffectiveReceiveFolder =>
        string.IsNullOrWhiteSpace(ReceiveFolder) ? KnownFolders.Downloads : ReceiveFolder!;

    public static string DefaultDeviceName()
    {
        var name = Environment.MachineName;
        return string.IsNullOrWhiteSpace(name) ? "My computer" : name;
    }

    public static string NormalizeDeviceName(string? name)
    {
        var cleaned = new string((name ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (cleaned.Length > MaxDeviceNameLength) cleaned = cleaned[..MaxDeviceNameLength].TrimEnd();
        return cleaned.Length == 0 ? DefaultDeviceName() : cleaned;
    }

    internal void Normalize()
    {
        DeviceName = NormalizeDeviceName(DeviceName);
        TrustedDevices ??= new();
        ManualAddresses ??= new();
        Backups = (Backups ?? new())
            .Where(b => !string.IsNullOrWhiteSpace(b.TargetId) && !string.IsNullOrWhiteSpace(b.TargetFingerprint))
            .GroupBy(b => b.TargetId)
            .Select(g => g.Last())
            .ToList();
        foreach (var plan in Backups)
        {
            plan.Sources ??= new();
            foreach (var source in plan.Sources.Where(s => s.Kind == BackupSourceKinds.Folder && s.Types == null))
            {
                // Saved before the checkboxes: the one choice becomes its kinds.
                source.Types = BackupFileTypes.FromLegacy(source.FileTypes, source.Extensions).ToList();
                source.FileTypes = null;
                source.Extensions = null;
            }
        }

        BackupPartners = (BackupPartners ?? new())
            .Where(p => !string.IsNullOrWhiteSpace(p.Fingerprint) && !string.IsNullOrWhiteSpace(p.Folder))
            .GroupBy(p => p.Fingerprint, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Last())
            .ToList();
        KnownDirectNetworks = (KnownDirectNetworks ?? new())
            .Where(k => !string.IsNullOrWhiteSpace(k.DeviceId) && !string.IsNullOrWhiteSpace(k.Fingerprint))
            .GroupBy(k => k.DeviceId)
            .Select(g => g.Last())
            .ToList();
        TrustedDevices = TrustedDevices
            .Where(t => !string.IsNullOrWhiteSpace(t.Fingerprint))
            .GroupBy(t => t.Fingerprint, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Last())
            .ToList();
        ManualAddresses = ManualAddresses
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Select(a => a.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (ReceiveFolder != null && string.IsNullOrWhiteSpace(ReceiveFolder)) ReceiveFolder = null;
    }
}
