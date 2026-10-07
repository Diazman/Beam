using System.Text.Json;
using Beam.Core.Diagnostics;
using Beam.Core.Storage;

namespace Beam.Core.Licensing;

public sealed class SendUsage
{
    /// <summary>Local date (yyyy-MM-dd) that <see cref="Sends"/> counts.</summary>
    public string Day { get; set; } = "";

    public int Sends { get; set; }
}

/// <summary>Counts sends per local day for the free edition's daily limit.</summary>
public sealed class SendQuota
{
    private readonly string _path;
    private readonly IEditionPolicy _edition;
    private readonly Func<DateTime> _now;
    private readonly object _gate = new();
    private SendUsage _usage;

    public SendQuota(string path, IEditionPolicy edition, Func<DateTime>? now = null)
    {
        _path = path;
        _edition = edition;
        _now = now ?? (() => DateTime.Now);
        _usage = Load(path);
    }

    /// <summary>Raised (on any thread) when the count changes.</summary>
    public event Action? Changed;

    public bool IsUnlimited => _edition.IsEnabled(Feature.UnlimitedSends);

    public int Limit => FreeLimits.SendsPerDay;

    public int UsedToday
    {
        get
        {
            lock (_gate) return _usage.Day == Today ? _usage.Sends : 0;
        }
    }

    /// <summary>Sends left today, or null when unlimited.</summary>
    public int? RemainingToday => IsUnlimited ? null : Math.Max(0, Limit - UsedToday);

    public bool CanSend(int count = 1) => IsUnlimited || UsedToday + count <= Limit;

    /// <summary>Counts <paramref name="count"/> sends if the limit allows them. Returns false (and counts nothing) otherwise.</summary>
    public bool TryUse(int count = 1)
    {
        lock (_gate)
        {
            var today = Today;
            if (_usage.Day != today) _usage = new SendUsage { Day = today };
            if (!IsUnlimited && _usage.Sends + count > Limit) return false;
            _usage.Sends += count;
            Save();
        }

        Changed?.Invoke();
        return true;
    }

    private string Today => _now().ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    private void Save()
    {
        try
        {
            AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(_usage, StorageJson.Default.SendUsage));
        }
        catch (Exception ex)
        {
            Log.Warn("Could not save send usage", ex);
        }
    }

    private static SendUsage Load(string path)
    {
        try
        {
            if (File.Exists(path)) return JsonSerializer.Deserialize(File.ReadAllText(path), StorageJson.Default.SendUsage) ?? new();
        }
        catch (Exception ex)
        {
            Log.Warn("Could not read send usage", ex);
        }

        return new SendUsage();
    }
}
