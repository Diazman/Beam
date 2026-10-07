using System.Runtime.InteropServices;

namespace Beam.Core.Storage;

public static partial class KnownFolders
{
    private static readonly Guid DownloadsId = new("374DE290-123F-4565-9164-39C4925E467B");

    /// <summary>The user's Downloads folder (honours a relocated Downloads folder on Windows).</summary>
    public static string Downloads
    {
        get
        {
            if (OperatingSystem.IsWindows())
            {
                try
                {
                    if (SHGetKnownFolderPath(DownloadsId, 0, IntPtr.Zero, out var pathPtr) == 0)
                    {
                        try
                        {
                            var path = Marshal.PtrToStringUni(pathPtr);
                            if (!string.IsNullOrEmpty(path)) return path;
                        }
                        finally
                        {
                            Marshal.FreeCoTaskMem(pathPtr);
                        }
                    }
                }
                catch
                {
                    // fall through to the conventional location
                }
            }
            else
            {
                var xdg = Environment.GetEnvironmentVariable("XDG_DOWNLOAD_DIR");
                if (!string.IsNullOrWhiteSpace(xdg)) return xdg;
            }

            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        }
    }

    [LibraryImport("shell32.dll")]
    private static partial int SHGetKnownFolderPath(in Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);
}
