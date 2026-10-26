using Beam.Core.Diagnostics;

namespace Beam.Core.Storage;

internal static class FileCleanup
{
    /// <summary>Deletes a leftover file if it is there; a file that can't be deleted is only logged.</summary>
    public static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not delete {path}: {ex.Message}");
        }
    }
}
