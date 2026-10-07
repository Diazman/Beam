using System.Text.Json;
using Beam.Core.Diagnostics;
using Beam.Core.Storage;

namespace Beam.Core.Settings;

/// <summary>
/// Loads and saves <see cref="AppSettings"/>. Readers always get an immutable-by-convention
/// copy; changes go through <see cref="Update"/> so they are persisted and announced.
/// </summary>
public sealed class SettingsStore
{
    private readonly string _path;
    private readonly object _gate = new();
    private AppSettings _current;

    public SettingsStore(string path)
    {
        _path = path;
        _current = Load(path);
    }

    public event Action<AppSettings>? Changed;

    public AppSettings Current
    {
        get
        {
            lock (_gate) return Clone(_current);
        }
    }

    public void Update(Action<AppSettings> mutate)
    {
        AppSettings snapshot;
        lock (_gate)
        {
            var copy = Clone(_current);
            mutate(copy);
            copy.Normalize();
            _current = copy;
            Save(copy);
            snapshot = Clone(copy);
        }

        Changed?.Invoke(snapshot);
    }

    private void Save(AppSettings settings)
    {
        try
        {
            AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(settings, StorageJson.Default.AppSettings));
        }
        catch (Exception ex)
        {
            Log.Error("Could not save settings", ex);
        }
    }

    private static AppSettings Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize(File.ReadAllText(path), StorageJson.Default.AppSettings);
                if (loaded != null)
                {
                    loaded.Normalize();
                    return loaded;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Settings file was unreadable; using defaults", ex);
            try { File.Copy(path, path + ".corrupt", overwrite: true); } catch { /* best effort */ }
        }

        var fresh = new AppSettings();
        fresh.Normalize();
        return fresh;
    }

    private static AppSettings Clone(AppSettings settings) =>
        JsonSerializer.Deserialize(JsonSerializer.Serialize(settings, StorageJson.Default.AppSettings), StorageJson.Default.AppSettings)!;
}
