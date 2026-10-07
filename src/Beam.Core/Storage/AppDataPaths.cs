namespace Beam.Core.Storage;

/// <summary>Locations of everything Beam stores on disk (never the transferred files themselves).</summary>
public sealed class AppDataPaths
{
    /// <summary>Environment variable that overrides the data folder (portable mode, tests, running two instances).</summary>
    public const string OverrideVariable = "BEAM_DATA_DIR";

    public AppDataPaths(string root)
    {
        Root = Path.GetFullPath(root);
    }

    public string Root { get; }

    public string SettingsFile => Path.Combine(Root, "settings.json");

    public string HistoryFile => Path.Combine(Root, "history.json");

    public string IdentityFile => Path.Combine(Root, "identity.json");

    public string UsageFile => Path.Combine(Root, "usage.json");

    public string ResumeDirectory => Path.Combine(Root, "resume");

    public string LogDirectory => Path.Combine(Root, "logs");

    public static AppDataPaths Default()
    {
        var overridden = Environment.GetEnvironmentVariable(OverrideVariable);
        if (!string.IsNullOrWhiteSpace(overridden)) return new AppDataPaths(overridden);

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create);
        if (string.IsNullOrEmpty(local))
            local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        return new AppDataPaths(Path.Combine(local, AppInfo.ProductName));
    }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(ResumeDirectory);
        Directory.CreateDirectory(LogDirectory);
    }
}
