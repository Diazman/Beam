using System.Text;

namespace Beam.Core.Storage;

/// <summary>Writes small state files so a crash mid-write never leaves a truncated file behind.</summary>
public static class AtomicFile
{
    public static void WriteAllBytes(string path, byte[] content)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var temp = path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(content);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temp, path, overwrite: true);
    }

    public static void WriteAllText(string path, string content) =>
        WriteAllBytes(path, new UTF8Encoding(false).GetBytes(content));
}
