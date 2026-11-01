using Beam.Core.Localization;

namespace Beam.Core.Backup;

/// <summary>
/// The main folders of "Choose folders": the camera, screenshots, downloads and the usual apps, each gathering every
/// folder of its kind on the phone ("WhatsApp" is WhatsApp Images, WhatsApp Business Images, its videos…). Ticking
/// one ticks the group, so a folder of it that appears later is backed up too. Other folders are chosen one by one.
/// </summary>
public static class BackupFolderGroups
{
    /// <summary>In a source's <see cref="BackupSource.Folders"/> and <see cref="BackupSource.SeenFolders"/>: a whole group.</summary>
    public const string Prefix = "group:";

    private sealed record Group(string Id, Func<string> Name, Func<string[], string, bool> Matches);

    private static bool Has(string path, string word) => path.Contains(word, StringComparison.OrdinalIgnoreCase);

    // Apps first (their folders may also be called "Camera" or "Screenshots"), then the phone's own folders.
    private static readonly Group[] All =
    {
        new("whatsapp", () => "WhatsApp", (_, p) => Has(p, "whatsapp")),
        new("telegram", () => "Telegram", (_, p) => Has(p, "telegram")),
        new("instagram", () => "Instagram", (_, p) => Has(p, "instagram")),
        new("facebook", () => "Facebook & Messenger", (_, p) => Has(p, "facebook") || Has(p, "messenger") || Has(p, "com.facebook.orca")),
        new("snapchat", () => "Snapchat", (_, p) => Has(p, "snapchat")),
        new("viber", () => "Viber", (_, p) => Has(p, "viber")),
        new("signal", () => "Signal", (s, _) => s.Any(x => x.Equals("Signal", StringComparison.OrdinalIgnoreCase) || x.StartsWith("org.thoughtcrime", StringComparison.OrdinalIgnoreCase))),
        new("tiktok", () => "TikTok", (_, p) => Has(p, "tiktok") || Has(p, "musically")),
        new("twitter", () => "X (Twitter)", (_, p) => Has(p, "twitter")),
        new("screenshots", () => L.T("Screenshots"), (s, _) => s.Any(x => x.StartsWith("Screenshot", StringComparison.OrdinalIgnoreCase))),
        new("camera", () => L.T("Camera"), IsCamera),
        new("downloads", () => L.T("Downloads"), (s, _) => s.Length > 0 && (s[0].Equals("Download", StringComparison.OrdinalIgnoreCase) || s[0].Equals("Downloads", StringComparison.OrdinalIgnoreCase))),
    };

    /// <summary>"DCIM/Camera", "Pictures/Camera", "DCIM" itself, or a camera's numbered folder ("DCIM/100ANDRO", "DCIM/100MEDIA").</summary>
    private static bool IsCamera(string[] segments, string path)
    {
        if (segments.Any(x => x.Equals("Camera", StringComparison.OrdinalIgnoreCase))) return true;
        if (segments.Length == 0 || !segments[0].Equals("DCIM", StringComparison.OrdinalIgnoreCase)) return false;
        return segments.Length == 1 || (segments.Length == 2 && segments[1].Length > 3 && segments[1][..3].All(char.IsDigit));
    }

    /// <summary>The order the main folders are listed in.</summary>
    public static IReadOnlyList<string> Order { get; } = new[]
    {
        "camera", "screenshots", "whatsapp", "telegram", "instagram", "facebook", "snapchat", "viber", "signal", "tiktok", "twitter", "downloads",
    };

    /// <summary>The main folder a folder on the phone belongs to ("Android/media/com.whatsapp/…" → whatsapp); null: another folder.</summary>
    public static string? Of(string path)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return All.FirstOrDefault(g => g.Matches(segments, path))?.Id;
    }

    /// <summary>"WhatsApp", "Camera"…</summary>
    public static string NameOf(string id) => All.FirstOrDefault(g => g.Id == id)?.Name() ?? id;
}
