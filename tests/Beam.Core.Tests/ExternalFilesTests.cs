using System.Diagnostics.CodeAnalysis;
using Beam.Core.Files;
using Beam.Core.Transfer;

namespace Beam.Core.Tests;

/// <summary>Files handed over as streams (Android content:// URIs) are sent like normal files.</summary>
public class ExternalFilesTests
{
    [Fact]
    public async Task ExternalStreamsAreSentIntactAlongsideNormalFiles()
    {
        var small = TestBytes(200_000, 1);           // goes through the small-file path
        var large = TestBytes(3 * 1024 * 1024 + 7, 2); // streamed in chunks
        var files = new FakeContentProvider
        {
            ["content://media/external/images/1001"] = ("IMG_2041.jpg", small),
            ["content://com.android.providers.downloads/document/77"] = ("Фото отпуска.mp4", large),
        };
        await using var sender = new TestNode("Phone", externalFiles: files);
        await using var receiver = new TestNode("PC");
        var normal = sender.CreateFile("notes.txt", 1234);

        var session = sender.Node.Send(receiver.AsDevice(), new[] { "content://media/external/images/1001", "content://com.android.providers.downloads/document/77", normal });
        Assert.Equal("IMG_2041.jpg and 2 more", session.Title);
        await Wait.ForFinishAsync(session);

        Assert.Equal(TransferState.Completed, session.State);
        Assert.Equal(small, File.ReadAllBytes(Path.Combine(receiver.ReceiveFolder, "IMG_2041.jpg")));
        Assert.Equal(large, File.ReadAllBytes(Path.Combine(receiver.ReceiveFolder, "Фото отпуска.mp4")));
        Assert.Equal(TestFiles.Hash(normal), TestFiles.Hash(Path.Combine(receiver.ReceiveFolder, "notes.txt")));
        Assert.Equal(3, Assert.Single(receiver.Handler.Requests).FileCount);
        Assert.All(files.Opened, s => Assert.True(s.Disposed));
    }

    [Fact]
    public void ManifestUsesTheExternalNameSizeAndDate()
    {
        var files = new FakeContentProvider { ["content://x/1"] = ("a/b:c.jpg", new byte[10]), ["content://x/2"] = ("a/b:c.jpg", new byte[3]) };
        var manifest = ManifestBuilder.Build(new[] { "content://x/1", "content://x/2", "content://x/1" }, external: files);

        Assert.Equal(2, manifest.Entries.Count);
        Assert.Equal(new[] { 10L, 3L }, manifest.Entries.Select(e => e.Size));
        Assert.All(manifest.Entries, e => Assert.DoesNotContain('/', e.RelativePath));
        Assert.NotEqual(manifest.Entries[0].RelativePath, manifest.Entries[1].RelativePath);
        Assert.Equal(new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc), manifest.Entries[0].ModifiedUtc);
    }

    private static byte[] TestBytes(int length, int seed)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    private sealed class FakeContentProvider : IExternalFiles
    {
        private readonly Dictionary<string, (string Name, byte[] Data)> _files = new();

        public List<NonSeekableStream> Opened { get; } = new();

        public (string Name, byte[] Data) this[string address]
        {
            set => _files[address] = value;
        }

        public bool TryGet(string address, [NotNullWhen(true)] out ExternalFile? file)
        {
            file = null;
            if (!_files.TryGetValue(address, out var entry)) return false;
            file = new ExternalFile(entry.Name, entry.Data.Length, new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc), () =>
            {
                var stream = new NonSeekableStream(entry.Data);
                lock (Opened) Opened.Add(stream);
                return stream;
            });
            return true;
        }
    }

    /// <summary>Like an Android content stream: readable, but no Length or Position.</summary>
    private sealed class NonSeekableStream : Stream
    {
        private readonly MemoryStream _inner;

        public NonSeekableStream(byte[] data) => _inner = new MemoryStream(data, writable: false);

        public bool Disposed { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }
}
