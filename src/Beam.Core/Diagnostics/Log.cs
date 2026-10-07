using System.Diagnostics;
using System.Text;

namespace Beam.Core.Diagnostics;

/// <summary>
/// Minimal thread-safe file logger. Technical details go here so user-facing
/// messages can stay friendly; the log folder is reachable from Settings.
/// </summary>
public static class Log
{
    private static readonly object Gate = new();
    private static StreamWriter? _writer;

    public static string? Directory { get; private set; }

    public static void Initialize(string directory)
    {
        lock (Gate)
        {
            if (_writer != null) return;
            try
            {
                System.IO.Directory.CreateDirectory(directory);
                Directory = directory;
                foreach (var old in new DirectoryInfo(directory).GetFiles("beam-*.log"))
                {
                    if (old.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-7))
                    {
                        try { old.Delete(); } catch { /* best effort */ }
                    }
                }

                var path = Path.Combine(directory, $"beam-{DateTime.Now:yyyyMMdd}.log");
                var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Logging disabled: {ex.Message}");
            }
        }
    }

    public static void Info(string message) => Write("INF", message, null);

    public static void Warn(string message, Exception? ex = null) => Write("WRN", message, ex);

    public static void Error(string message, Exception? ex = null) => Write("ERR", message, ex);

    private static void Write(string level, string message, Exception? ex)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
        if (ex != null) line += Environment.NewLine + "    " + ex.ToString().Replace("\n", "\n    ");
        Debug.WriteLine(line);
        lock (Gate)
        {
            try { _writer?.WriteLine(line); } catch { /* never let logging crash the app */ }
        }
    }
}
