namespace Beam.App.Services;

/// <summary>
/// Command line: <c>Beam.exe [--minimized] [--software-rendering] [--send] [paths...]</c>.
/// <c>--send</c> (or bare paths) adds files to the send list — this is the hook for a future
/// "Send with Beam" Explorer context-menu entry, which only needs to run <c>Beam.exe --send "%1"</c>.
/// </summary>
public sealed record CommandLine(bool StartMinimized, IReadOnlyList<string> SendPaths, bool SoftwareRendering = false)
{
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
