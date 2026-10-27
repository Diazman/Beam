using System.Diagnostics;
using System.Text;

namespace Beam.Core.Diagnostics;

/// <summary>
/// Minimal thread-safe file logger. Technical details go here so user-facing
/// messages can stay friendly; the log folder is reachable from Settings.
/// </summary>
public static class Log
{
    /// <summary>A day's file is continued in a new one past this size (a PC that runs Beam for weeks).</summary>
    internal const long MaxFileBytes = 20L * 1024 * 1024;

    private static readonly object Gate = new();
    private static StreamWriter? _writer;
    private static DateTime _day;

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
                OpenLocked(DateTime.Now);
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
        var now = DateTime.Now;
        var line = $"{now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
        if (ex != null) line += Environment.NewLine + "    " + ex.ToString().Replace("\n", "\n    ");
        Debug.WriteLine(line);
        lock (Gate)
        {
            try
            {
                if (_writer == null) return;
                // A new file each day, and when one gets big; old ones are removed after a week.
                if (now.Date != _day || _writer.BaseStream.Length > MaxFileBytes) OpenLocked(now);
                _writer?.WriteLine(line);
            }
            catch
            {
                // never let logging crash the app
            }
        }
    }

    private static void OpenLocked(DateTime now)
    {
        var directory = Directory!;
        foreach (var old in new DirectoryInfo(directory).GetFiles("beam-*.log"))
        {
            if (old.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-7))
            {
                try { old.Delete(); } catch { /* best effort */ }
            }
        }

        var path = Path.Combine(directory, $"beam-{now:yyyyMMdd}.log");
        if (File.Exists(path) && new FileInfo(path).Length > MaxFileBytes) path = Path.Combine(directory, $"beam-{now:yyyyMMdd-HHmmss}.log");
        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        _writer?.Dispose();
        _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
        _day = now.Date;
    }
}
