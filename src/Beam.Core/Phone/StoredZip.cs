using System.Buffers.Binary;
using System.Text;

namespace Beam.Core.Phone;

/// <summary>
/// Streams files as a ZIP archive without compressing them ("stored"), so a phone browser can download a whole
/// folder at network speed. The exact size is known in advance (Content-Length, real progress in the browser);
/// ZIP64 is used for files or archives of 4 GB and more. Each file's CRC is computed just before it is sent.
/// </summary>
internal static class StoredZip
{
    /// <summary>Sizes and offsets from this value on need ZIP64 (lowered in tests to exercise that path).</summary>
    internal static long Zip64Threshold { get; set; } = 0xFFFFFFFF;

    public sealed record Item(string Name, string Path, long Size, DateTime ModifiedUtc);

    private sealed record Entry(Item Item, byte[] Name, long Offset, bool Zip64Size, bool Zip64Offset)
    {
        public int LocalExtra => Zip64Size ? 20 : 0;

        public int CentralExtra => (Zip64Size ? 16 : 0) + (Zip64Offset ? 8 : 0) is var n && n > 0 ? n + 4 : 0;

        public long LocalLength => 30 + Name.Length + LocalExtra;

        public long CentralLength => 46 + Name.Length + CentralExtra;
    }

    private sealed record Layout(IReadOnlyList<Entry> Entries, long CentralOffset, long CentralSize, bool Zip64End, long Length);

    /// <summary>The archive's exact size in bytes.</summary>
    public static long Length(IReadOnlyList<Item> items) => Plan(items).Length;

    /// <summary>
    /// Writes the archive. <paramref name="beforeWrite"/> runs before each block of file data (rate limiting);
    /// <paramref name="fileDone"/> runs after each file's data was written.
    /// </summary>
    public static async Task WriteAsync(Stream output, IReadOnlyList<Item> items, Func<int, Task> beforeWrite, Action<Item, long> progress,
        Action<Item> fileDone, CancellationToken token)
    {
        var layout = Plan(items);
        var crcs = new uint[layout.Entries.Count];
        var buffer = new byte[256 * 1024];
        for (var i = 0; i < layout.Entries.Count; i++)
        {
            var entry = layout.Entries[i];
            await using var file = new FileStream(entry.Item.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
            crcs[i] = await CrcAsync(file, entry.Item.Size, buffer, token).ConfigureAwait(false);
            file.Position = 0;
            await output.WriteAsync(LocalHeader(entry, crcs[i]), token).ConfigureAwait(false);
            var remaining = entry.Item.Size;
            while (remaining > 0)
            {
                var read = await file.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), token).ConfigureAwait(false);
                if (read == 0) throw new IOException($"{entry.Item.Name} got smaller while it was being sent.");
                await beforeWrite(read).ConfigureAwait(false);
                await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                remaining -= read;
                progress(entry.Item, read);
            }

            fileDone(entry.Item);
        }

        using var central = new MemoryStream();
        for (var i = 0; i < layout.Entries.Count; i++) central.Write(CentralHeader(layout.Entries[i], crcs[i]));
        central.Write(End(layout));
        await output.WriteAsync(central.ToArray(), token).ConfigureAwait(false);
    }

    private static Layout Plan(IReadOnlyList<Item> items)
    {
        var entries = new List<Entry>(items.Count);
        long offset = 0;
        foreach (var item in items)
        {
            var entry = new Entry(item, Encoding.UTF8.GetBytes(item.Name), offset, item.Size >= Zip64Threshold, offset >= Zip64Threshold);
            entries.Add(entry);
            offset += entry.LocalLength + item.Size;
        }

        var centralSize = entries.Sum(e => e.CentralLength);
        var zip64End = entries.Count >= 0xFFFF || offset >= Zip64Threshold || centralSize >= Zip64Threshold;
        return new Layout(entries, offset, centralSize, zip64End, offset + centralSize + (zip64End ? 56 + 20 : 0) + 22);
    }

    private static byte[] LocalHeader(Entry e, uint crc)
    {
        var b = new byte[e.LocalLength];
        var s = b.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(s, 0x04034b50);
        BinaryPrimitives.WriteUInt16LittleEndian(s[4..], (ushort)(e.Zip64Size ? 45 : 20));
        BinaryPrimitives.WriteUInt16LittleEndian(s[6..], 0x0800); // names are UTF-8
        BinaryPrimitives.WriteUInt16LittleEndian(s[8..], 0); // stored
        WriteDosTime(s[10..], e.Item.ModifiedUtc);
        BinaryPrimitives.WriteUInt32LittleEndian(s[14..], crc);
        var size = e.Zip64Size ? 0xFFFFFFFF : (uint)e.Item.Size;
        BinaryPrimitives.WriteUInt32LittleEndian(s[18..], size);
        BinaryPrimitives.WriteUInt32LittleEndian(s[22..], size);
        BinaryPrimitives.WriteUInt16LittleEndian(s[26..], (ushort)e.Name.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(s[28..], (ushort)e.LocalExtra);
        e.Name.CopyTo(s[30..]);
        if (e.Zip64Size)
        {
            var x = s[(30 + e.Name.Length)..];
            BinaryPrimitives.WriteUInt16LittleEndian(x, 0x0001);
            BinaryPrimitives.WriteUInt16LittleEndian(x[2..], 16);
            BinaryPrimitives.WriteInt64LittleEndian(x[4..], e.Item.Size);
            BinaryPrimitives.WriteInt64LittleEndian(x[12..], e.Item.Size);
        }

        return b;
    }

    private static byte[] CentralHeader(Entry e, uint crc)
    {
        var b = new byte[e.CentralLength];
        var s = b.AsSpan();
        var zip64 = e.Zip64Size || e.Zip64Offset;
        BinaryPrimitives.WriteUInt32LittleEndian(s, 0x02014b50);
        BinaryPrimitives.WriteUInt16LittleEndian(s[4..], 45);
        BinaryPrimitives.WriteUInt16LittleEndian(s[6..], (ushort)(zip64 ? 45 : 20));
        BinaryPrimitives.WriteUInt16LittleEndian(s[8..], 0x0800);
        BinaryPrimitives.WriteUInt16LittleEndian(s[10..], 0);
        WriteDosTime(s[12..], e.Item.ModifiedUtc);
        BinaryPrimitives.WriteUInt32LittleEndian(s[16..], crc);
        var size = e.Zip64Size ? 0xFFFFFFFF : (uint)e.Item.Size;
        BinaryPrimitives.WriteUInt32LittleEndian(s[20..], size);
        BinaryPrimitives.WriteUInt32LittleEndian(s[24..], size);
        BinaryPrimitives.WriteUInt16LittleEndian(s[28..], (ushort)e.Name.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(s[30..], (ushort)e.CentralExtra);
        // comment length, disk number, internal and external attributes: 0
        BinaryPrimitives.WriteUInt32LittleEndian(s[42..], e.Zip64Offset ? 0xFFFFFFFF : (uint)e.Offset);
        e.Name.CopyTo(s[46..]);
        if (e.CentralExtra > 0)
        {
            var x = s[(46 + e.Name.Length)..];
            BinaryPrimitives.WriteUInt16LittleEndian(x, 0x0001);
            BinaryPrimitives.WriteUInt16LittleEndian(x[2..], (ushort)(e.CentralExtra - 4));
            var p = 4;
            if (e.Zip64Size)
            {
                BinaryPrimitives.WriteInt64LittleEndian(x[p..], e.Item.Size);
                BinaryPrimitives.WriteInt64LittleEndian(x[(p + 8)..], e.Item.Size);
                p += 16;
            }

            if (e.Zip64Offset) BinaryPrimitives.WriteInt64LittleEndian(x[p..], e.Offset);
        }

        return b;
    }

    private static byte[] End(Layout layout)
    {
        var count = layout.Entries.Count;
        using var m = new MemoryStream();
        Span<byte> s = stackalloc byte[56];
        if (layout.Zip64End)
        {
            var zip64EndOffset = layout.CentralOffset + layout.CentralSize;
            s.Clear();
            BinaryPrimitives.WriteUInt32LittleEndian(s, 0x06064b50);
            BinaryPrimitives.WriteInt64LittleEndian(s[4..], 44);
            BinaryPrimitives.WriteUInt16LittleEndian(s[12..], 45);
            BinaryPrimitives.WriteUInt16LittleEndian(s[14..], 45);
            BinaryPrimitives.WriteInt64LittleEndian(s[24..], count);
            BinaryPrimitives.WriteInt64LittleEndian(s[32..], count);
            BinaryPrimitives.WriteInt64LittleEndian(s[40..], layout.CentralSize);
            BinaryPrimitives.WriteInt64LittleEndian(s[48..], layout.CentralOffset);
            m.Write(s[..56]);
            s.Clear();
            BinaryPrimitives.WriteUInt32LittleEndian(s, 0x07064b50);
            BinaryPrimitives.WriteInt64LittleEndian(s[8..], zip64EndOffset);
            BinaryPrimitives.WriteUInt32LittleEndian(s[16..], 1);
            m.Write(s[..20]);
        }

        s.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(s, 0x06054b50);
        var shortCount = (ushort)Math.Min(count, 0xFFFF);
        BinaryPrimitives.WriteUInt16LittleEndian(s[8..], shortCount);
        BinaryPrimitives.WriteUInt16LittleEndian(s[10..], shortCount);
        BinaryPrimitives.WriteUInt32LittleEndian(s[12..], layout.CentralSize >= Zip64Threshold ? 0xFFFFFFFF : (uint)layout.CentralSize);
        BinaryPrimitives.WriteUInt32LittleEndian(s[16..], layout.CentralOffset >= Zip64Threshold ? 0xFFFFFFFF : (uint)layout.CentralOffset);
        m.Write(s[..22]);
        return m.ToArray();
    }

    private static void WriteDosTime(Span<byte> s, DateTime utc)
    {
        var t = utc.ToLocalTime();
        if (t.Year < 1980) t = new DateTime(1980, 1, 1);
        if (t.Year > 2107) t = new DateTime(2107, 12, 31);
        BinaryPrimitives.WriteUInt16LittleEndian(s, (ushort)((t.Hour << 11) | (t.Minute << 5) | (t.Second / 2)));
        BinaryPrimitives.WriteUInt16LittleEndian(s[2..], (ushort)(((t.Year - 1980) << 9) | (t.Month << 5) | t.Day));
    }

    private static async Task<uint> CrcAsync(Stream file, long size, byte[] buffer, CancellationToken token)
    {
        var crc = new System.IO.Hashing.Crc32();
        var remaining = size;
        while (remaining > 0)
        {
            var read = await file.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), token).ConfigureAwait(false);
            if (read == 0) throw new IOException("A file got smaller while it was being sent.");
            crc.Append(buffer.AsSpan(0, read));
            remaining -= read;
        }

        return crc.GetCurrentHashAsUInt32();
    }
}
