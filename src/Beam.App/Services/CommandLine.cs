namespace Beam.App.Services;

/// <summary>
/// Command line: <c>Beam.exe [--minimized] [--software-rendering] [--send] [paths...]</c>.
/// <c>--send</c> (or bare paths) adds files to the send list; "Send with Beam" in Explorer runs
/// <c>Beam.exe --send "%1"</c> (installer) or the Windows 11 menu handler in native/BeamContextMenu (Store), which
/// passes very long selections as <c>--send-list &lt;file&gt;</c> (see <see cref="ExpandListFiles"/>).
/// </summary>
public sealed record CommandLine(bool StartMinimized, IReadOnlyList<string> SendPaths, bool SoftwareRendering = false)
{
    /// <summary>
    /// Replaces <c>--send-list &lt;file&gt;</c> (one UTF-8 path per line, written to the temp folder by the Explorer menu
    /// handler) with <c>--send</c> and those paths, and deletes the list file.
    /// </summary>
    public static string[] ExpandListFiles(string[] args)
    {
        var index = Array.FindIndex(args, a => string.Equals(a, "--send-list", StringComparison.OrdinalIgnoreCase));
        if (index < 0) return args;
        var result = args.Take(index).ToList();
        result.Add("--send");
        if (index + 1 < args.Length)
        {
            var listFile = args[index + 1];
            try
            {
                // Only lists the menu handler wrote: a file in the temp folder.
                var temp = Path.GetFullPath(Path.GetTempPath());
                var full = Path.GetFullPath(listFile);
                if (full.StartsWith(temp, StringComparison.OrdinalIgnoreCase) && File.Exists(full))
                {
                    result.AddRange(File.ReadAllLines(full).Select(l => l.Trim()).Where(l => l.Length > 0));
                    File.Delete(full);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                Beam.Core.Diagnostics.Log.Warn("Could not read the list of files to send", ex);
            }
        }

        result.AddRange(args.Skip(index + 2));
        return result.ToArray();
    }

    public static CommandLine Parse(IEnumerable<string> args)
    {
        var minimized = false;
        var software = false;
        var paths = new List<string>();
        foreach (var arg in args)
        {
            if (string.Equals(arg, "--minimized", StringComparison.OrdinalIgnoreCase) || string.Equals(arg, "/minimized", StringComparison.OrdinalIgnoreCase))
                minimized = true;
            else if (string.Equals(arg, "--software-rendering", StringComparison.OrdinalIgnoreCase))
                software = true;
            else if (string.Equals(arg, "--send", StringComparison.OrdinalIgnoreCase))
                continue;
            else if (!arg.StartsWith("--", StringComparison.Ordinal) && (File.Exists(arg) || Directory.Exists(arg)))
                paths.Add(Path.GetFullPath(arg));
        }

        return new CommandLine(minimized, paths, software);
    }
}
