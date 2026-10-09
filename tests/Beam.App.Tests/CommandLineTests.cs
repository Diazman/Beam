using Beam.App.Services;

namespace Beam.App.Tests;

public class CommandLineTests
{
    [Fact]
    public void SendListFileIsExpandedAndDeleted()
    {
        var dir = Directory.CreateTempSubdirectory("beam-cl-");
        try
        {
            var a = Path.Combine(dir.FullName, "a b.txt");
            var b = Path.Combine(dir.FullName, "Фото.jpg");
            File.WriteAllText(a, "a");
            File.WriteAllText(b, "b");
            var list = Path.Combine(Path.GetTempPath(), $"bms{Guid.NewGuid():N}.tmp");
            File.WriteAllText(list, a + "\n" + b + "\r\n\n");

            var args = CommandLine.ExpandListFiles(new[] { "--send-list", list });

            Assert.Equal(new[] { "--send", a, b }, args);
            Assert.False(File.Exists(list));
            Assert.Equal(new[] { a, b }, CommandLine.Parse(args).SendPaths);
        }
        finally
        {
            dir.Delete(true);
        }
    }

    [Fact]
    public void ListFilesOutsideTheTempFolderAreIgnored()
    {
        var dir = Directory.CreateTempSubdirectory("beam-cl-");
        var outside = Path.Combine(Environment.CurrentDirectory, $"list-{Guid.NewGuid():N}.txt");
        try
        {
            File.WriteAllText(outside, dir.FullName);
            Assert.Equal(new[] { "--send" }, CommandLine.ExpandListFiles(new[] { "--send-list", outside }));
            Assert.True(File.Exists(outside));
            Assert.Equal(new[] { "--minimized" }, CommandLine.ExpandListFiles(new[] { "--minimized" }));
        }
        finally
        {
            File.Delete(outside);
            dir.Delete(true);
        }
    }
}
