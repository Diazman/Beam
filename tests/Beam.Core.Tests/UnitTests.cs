using System.Globalization;
using Beam.Core.Files;
using Beam.Core.Identity;
using Beam.Core.Protocol;
using Beam.Core.Settings;
using Beam.Core.Util;

namespace Beam.Core.Tests;

public class SafePathTests
{
    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("a/../../evil.txt")]
    [InlineData("/etc/passwd")]
    [InlineData("\\\\server\\share\\x")]
    [InlineData("a//b")]
    [InlineData("./a")]
    [InlineData("")]
    public void RejectsUnsafePaths(string path) => Assert.Null(SafePath.SplitRelative(path));

    [Theory]
    [InlineData("Photos/2026/a.jpg", new[] { "Photos", "2026", "a.jpg" })]
    [InlineData("Photos\\a.jpg", new[] { "Photos", "a.jpg" })]
    [InlineData("what?.txt", new[] { "what_.txt" })]
    [InlineData("a:b|c*.txt", new[] { "a_b_c_.txt" })]
    [InlineData("CON", new[] { "_CON" })]
    [InlineData("nul.txt", new[] { "_nul.txt" })]
    [InlineData("trailing dot.", new[] { "trailing dot" })]
    [InlineData("日本語/ファイル.txt", new[] { "日本語", "ファイル.txt" })]
    [InlineData("...", new[] { "_" })]
    [InlineData("C:x", new[] { "C_x" })]
    [InlineData("C:\\Windows\\x", new[] { "C_", "Windows", "x" })]
    public void SanitizesNames(string path, string[] expected) => Assert.Equal(expected, SafePath.SplitRelative(path));

    [Fact]
    public void CombineNeverEscapesRoot()
    {
        using var dir = new TempDir();
        Assert.StartsWith(dir.Path, SafePath.Combine(dir.Path, new[] { "a", "b.txt" }));
        Assert.Throws<InvalidOperationException>(() => SafePath.Combine(dir.Path, new[] { "..", "x" }));
    }

    [Fact]
    public void TruncatesVeryLongNamesKeepingExtension()
    {
        var name = SafePath.SanitizeSegment(new string('x', 400) + ".jpeg");
        Assert.True(name.Length <= SafePath.MaxSegmentLength);
        Assert.EndsWith(".jpeg", name);
    }

    [Fact]
    public void MakeUniqueNamesLikeExplorer()
    {
        var taken = new HashSet<string> { "a.txt", "a (1).txt" };
        Assert.Equal("a (2).txt", FileNaming.MakeUnique("a.txt", taken.Contains));
        Assert.Equal("b.txt", FileNaming.MakeUnique("b.txt", taken.Contains));
        Assert.Equal("my.folder (1)", FileNaming.MakeUnique("my.folder", n => n == "my.folder", isDirectory: true));
    }
}

public class ManifestTests
{
    [Fact]
    public void BuildsRelativePathsAndCounts()
    {
        using var dir = new TempDir();
        TestFiles.Write(dir.Combine("top.txt"), 10, 1);
        Directory.CreateDirectory(dir.Combine("Folder", "Sub", "Empty"));
        TestFiles.Write(dir.Combine("Folder", "a.txt"), 20, 2);
        TestFiles.Write(dir.Combine("Folder", "Sub", "b.txt"), 30, 3);

        var manifest = ManifestBuilder.Build(new[] { dir.Combine("top.txt"), dir.Combine("Folder"), dir.Combine("missing.txt") });

        Assert.Equal(new[] { "top.txt", "Folder" }, manifest.RootNames);
        Assert.Equal(3, manifest.FileCount);
        Assert.Equal(3, manifest.DirectoryCount);
        Assert.Equal(60, manifest.TotalBytes);
        Assert.Contains(manifest.Entries, e => e.RelativePath == "Folder/Sub/b.txt" && e.Size == 30);
        Assert.Contains(manifest.Entries, e => e.RelativePath == "Folder/Sub/Empty" && e.IsDirectory);
        Assert.Single(manifest.Warnings);
        Assert.Equal("top.txt and 1 more", manifest.Title);
    }

    [Fact]
    public void DuplicateTopLevelNamesAreDisambiguated()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.Combine("x"));
        Directory.CreateDirectory(dir.Combine("y"));
        TestFiles.Write(dir.Combine("x", "same.txt"), 1, 1);
        TestFiles.Write(dir.Combine("y", "same.txt"), 2, 2);

        var manifest = ManifestBuilder.Build(new[] { dir.Combine("x", "same.txt"), dir.Combine("y", "same.txt"), dir.Combine("x", "same.txt") });

        Assert.Equal(new[] { "same.txt", "same (1).txt" }, manifest.RootNames);
    }

    [Fact]
    public void DoesNotFollowFolderLinks()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.Combine("Loop"));
        TestFiles.Write(dir.Combine("Loop", "a.txt"), 1, 1);
        try
        {
            Directory.CreateSymbolicLink(dir.Combine("Loop", "self"), dir.Combine("Loop"));
        }
        catch (Exception)
        {
            return; // symlinks not permitted on this machine
        }

        var manifest = ManifestBuilder.Build(new[] { dir.Combine("Loop") });
        Assert.Equal(1, manifest.FileCount);
        Assert.Contains(manifest.Warnings, w => w.Contains("not followed"));
    }
}

public class FormatTests
{
    public FormatTests() => CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(512, "512 B")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(505413632, "482 MB")]
    [InlineData(1503238553, "1.4 GB")]
    public void Bytes(long value, string expected) => Assert.Equal(expected, Format.Bytes(value));

    [Fact]
    public void Remaining()
    {
        Assert.Equal("About 11 seconds remaining", Format.Remaining(TimeSpan.FromSeconds(10.2)));
        Assert.Equal("A few seconds remaining", Format.Remaining(TimeSpan.FromSeconds(3)));
        Assert.Equal("About 45 seconds remaining", Format.Remaining(TimeSpan.FromSeconds(44)));
        Assert.Equal("About 1 minute remaining", Format.Remaining(TimeSpan.FromSeconds(70)));
        Assert.Equal("About 12 minutes remaining", Format.Remaining(TimeSpan.FromMinutes(12.2)));
        Assert.Equal("About 2 hours 5 min remaining", Format.Remaining(TimeSpan.FromMinutes(125)));
    }

    [Fact]
    public void Counts()
    {
        Assert.Equal("1 file", Format.Count(1, "file"));
        Assert.Equal("1,024 files", Format.Count(1024, "file"));
        Assert.Equal("2 folders, 1 file", Format.Contents(1, 2));
    }

    [Fact]
    public void SpeedMeterSmoothsAndEstimates()
    {
        var meter = new SpeedMeter();
        long t = 0;
        for (var i = 0; i <= 20; i++)
        {
            meter.Sample(i * 10_000_000L, t);
            t += 100;
        }

        Assert.InRange(meter.BytesPerSecond, 90_000_000, 110_000_000);
        var eta = meter.EstimateRemaining(1_000_000_000);
        Assert.NotNull(eta);
        Assert.InRange(eta!.Value.TotalSeconds, 9, 11);
    }
}

public class StorageTests
{
    [Fact]
    public void IdentitySurvivesRestart()
    {
        using var dir = new TempDir();
        var path = dir.Combine("identity.json");
        string id, fingerprint;
        using (var first = DeviceIdentity.LoadOrCreate(path))
        {
            id = first.DeviceId;
            fingerprint = first.Fingerprint;
            Assert.True(first.Certificate.HasPrivateKey);
        }

        using var second = DeviceIdentity.LoadOrCreate(path);
        Assert.Equal(id, second.DeviceId);
        Assert.Equal(fingerprint, second.Fingerprint);
        Assert.Equal(9, DeviceIdentity.ShortCode(fingerprint).Length);
    }

    [Fact]
    public void CorruptIdentityIsRegenerated()
    {
        using var dir = new TempDir();
        var path = dir.Combine("identity.json");
        File.WriteAllText(path, "{ garbage");
        using var identity = DeviceIdentity.LoadOrCreate(path);
        Assert.True(identity.Certificate.HasPrivateKey);
    }

    [Fact]
    public void SettingsPersistAndNormalize()
    {
        using var dir = new TempDir();
        var path = dir.Combine("settings.json");
        var store = new SettingsStore(path);
        store.Update(s =>
        {
            s.DeviceName = "   " + new string('n', 80);
            s.Theme = ThemePreference.Dark;
            s.ManualAddresses.Add("10.0.0.2");
            s.ManualAddresses.Add("10.0.0.2");
        });

        var reloaded = new SettingsStore(path).Current;
        Assert.Equal(AppSettings.MaxDeviceNameLength, reloaded.DeviceName.Length);
        Assert.Equal(ThemePreference.Dark, reloaded.Theme);
        Assert.Single(reloaded.ManualAddresses);
        Assert.Contains("\"theme\": \"Dark\"", File.ReadAllText(path));
    }

    [Fact]
    public void CorruptSettingsFallBackToDefaults()
    {
        using var dir = new TempDir();
        var path = dir.Combine("settings.json");
        File.WriteAllText(path, "{{{{");
        var store = new SettingsStore(path);
        Assert.True(store.Current.CloseToTray);
        Assert.True(File.Exists(path + ".corrupt"));
    }

    [Fact]
    public void HistoryIsCappedAndClearable()
    {
        using var dir = new TempDir();
        var store = new History.HistoryStore(dir.Combine("history.json"));
        for (var i = 0; i < History.HistoryStore.MaxEntries + 20; i++)
            store.Add(new History.HistoryEntry { Id = i.ToString(), Title = $"t{i}", Timestamp = DateTimeOffset.Now });

        var reloaded = new History.HistoryStore(dir.Combine("history.json"));
        Assert.Equal(History.HistoryStore.MaxEntries, reloaded.Entries.Count);
        Assert.Equal("t519", reloaded.Entries[0].Title);
        reloaded.Clear();
        Assert.Empty(new History.HistoryStore(dir.Combine("history.json")).Entries);
    }
}

public class FrameChannelTests
{
    [Fact]
    public async Task RoundTripsControlAndDataFrames()
    {
        var stream = new MemoryStream();
        var writer = new FrameChannel(stream);
        await writer.SendAsync(FrameType.FileHeader, new FileHeaderMessage { Index = 7, Offset = 99 }, CancellationToken.None);
        var buffer = new byte[FrameChannel.HeaderSize + 3];
        buffer[5] = 1;
        buffer[6] = 2;
        buffer[7] = 3;
        await writer.SendDataAsync(buffer, 3, CancellationToken.None);

        stream.Position = 0;
        var reader = new FrameChannel(stream);
        var header = await reader.ReadMessageAsync<FileHeaderMessage>(FrameType.FileHeader, TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.Equal(7, header.Index);
        Assert.Equal(99, header.Offset);
        var data = await reader.ReadAsync(CancellationToken.None);
        Assert.Equal(FrameType.FileData, data.Type);
        Assert.Equal(new byte[] { 1, 2, 3 }, data.Payload.ToArray());
        await Assert.ThrowsAsync<EndOfStreamException>(() => reader.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task RejectsOversizedAndUnknownFrames()
    {
        var oversized = new MemoryStream(new byte[] { (byte)FrameType.FileData, 0x7F, 0xFF, 0xFF, 0xFF });
        await Assert.ThrowsAsync<ProtocolException>(() => new FrameChannel(oversized).ReadAsync(CancellationToken.None));
        var unknown = new MemoryStream(new byte[] { 200, 0, 0, 0, 0 });
        await Assert.ThrowsAsync<ProtocolException>(() => new FrameChannel(unknown).ReadAsync(CancellationToken.None));
    }
}
