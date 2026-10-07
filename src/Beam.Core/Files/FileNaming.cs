namespace Beam.Core.Files;

public static class FileNaming
{
    /// <summary>
    /// Returns "name.ext", or "name (1).ext", "name (2).ext", ... — the first name for which
    /// <paramref name="isTaken"/> returns false. Mirrors how Windows Explorer names copies.
    /// </summary>
    public static string MakeUnique(string name, Func<string, bool> isTaken, bool isDirectory = false)
    {
        if (!isTaken(name)) return name;
        var extension = isDirectory ? "" : Path.GetExtension(name);
        var stem = extension.Length > 0 ? name[..^extension.Length] : name;
        for (var i = 1; i < 100_000; i++)
        {
            var candidate = $"{stem} ({i}){extension}";
            if (!isTaken(candidate)) return candidate;
        }

        return $"{stem} ({Guid.NewGuid():N}){extension}";
    }

    /// <summary>True if a file or directory with this exact path exists.</summary>
    public static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);
}
