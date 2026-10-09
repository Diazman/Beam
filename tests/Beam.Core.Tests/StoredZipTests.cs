using System.IO.Compression;
using Beam.Core.Phone;

namespace Beam.Core.Tests;

[Collection("StoredZip")] // Zip64Threshold is static
public class StoredZipTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)] // forces the ZIP64 path (files and offsets "over 4 GB") with small files
    public async Task ArchiveOpensWithTheSizeAnnouncedAndIntactFiles(bool zip64)
    {
        using var dir = new TempDir();
        var items = new List<StoredZip.Item>();
        var expected = new Dictionary<string, byte[]>();
        foreach (var (name, size) in new[] { ("Photos/IMG_1.jpg", 300_000), ("Photos/2026/Фото €.png", 70_000), ("Photos/empty.txt", 0), ("Photos/notes.txt", 12) })
        {
            var path = dir.Combine(Guid.NewGuid().ToString("N"));
            var data = new byte[size];
            new Random(size).NextBytes(data);
            File.WriteAllBytes(path, data);
            items.Add(new StoredZip.Item(name, path, size, new DateTime(2026, 9, 1, 12, 30, 10, DateTimeKind.Utc)));
            expected[name] = data;
        }

        var previous = StoredZip.Zip64Threshold;
        if (zip64) StoredZip.Zip64Threshold = 1;
        try
        {
            using var output = new MemoryStream();
            var progress = 0L;
            var done = new List<string>();
            await StoredZip.WriteAsync(output, items, _ => Task.CompletedTask, (_, n) => progress += n, i => done.Add(i.Name), CancellationToken.None);

            Assert.Equal(StoredZip.Length(items), output.Length);
            if (Environment.GetEnvironmentVariable("BEAM_ZIP_OUT") is { } outDir)
                File.WriteAllBytes(Path.Combine(outDir, zip64 ? "zip64.zip" : "plain.zip"), output.ToArray());
            Assert.Equal(expected.Values.Sum(d => (long)d.Length), progress);
            Assert.Equal(items.Select(i => i.Name), done);

            output.Position = 0;
            using var zip = new ZipArchive(output, ZipArchiveMode.Read);
            Assert.Equal(expected.Count, zip.Entries.Count);
            foreach (var entry in zip.Entries)
            {
                using var read = new MemoryStream();
                using (var s = entry.Open()) s.CopyTo(read); // ZipArchive checks the CRC
                Assert.Equal(expected[entry.FullName], read.ToArray());
                Assert.Equal(new DateTime(2026, 9, 1, 12, 30, 10, DateTimeKind.Utc).ToLocalTime(), entry.LastWriteTime.DateTime, TimeSpan.FromSeconds(2));
            }
        }
        finally
        {
            StoredZip.Zip64Threshold = previous;
        }
    }
}
