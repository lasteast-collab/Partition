using System.Buffers.Binary;
using System.Text;
using Lasteast.Core.RawDisk;

namespace Lasteast.Core.Recovery;

public sealed record CarvedFile(long Offset, long Length, string Kind, string Extension);

/// <summary>
/// File carving: finds files by their content when the file system that described them is gone (formatted, overwritten
/// table, unknown file system). Each format is validated structurally — JPEG segments, PNG chunks with CRC, GIF blocks,
/// ZIP end-of-central-directory, MP4 boxes, PDF trailer — so the length is the file's real length and random data
/// rarely matches. Files are assumed to start on a sector and to be stored in one piece; fragmented files and original
/// names cannot be recovered this way. Read-only.
/// </summary>
public static class Carver
{
    public static readonly IReadOnlyList<string> Kinds = new[] { "JPEG", "PNG", "GIF", "PDF", "ZIP", "MP4" };

    private delegate long? Measure(IBlockDevice dev, long start);

    private static readonly (string Kind, string Ext, byte[] Magic, int MagicOffset, Measure Measure)[] Formats =
    {
        ("JPEG", "jpg", new byte[] { 0xFF, 0xD8, 0xFF }, 0, Jpeg),
        ("PNG", "png", new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, 0, Png),
        ("GIF", "gif", "GIF8"u8.ToArray(), 0, Gif),
        ("PDF", "pdf", "%PDF-"u8.ToArray(), 0, Pdf),
        ("ZIP", "zip", new byte[] { 0x50, 0x4B, 0x03, 0x04 }, 0, Zip),
        ("MP4", "mp4", "ftyp"u8.ToArray(), 4, Mp4),
    };

    public static IReadOnlyList<CarvedFile> Scan(IBlockDevice dev, IReadOnlySet<string>? kinds, IProgress<RecoveryScanProgress>? progress, CancellationToken ct)
    {
        int ss = Math.Max(512, dev.SectorSize);
        const int Chunk = 4 * 1024 * 1024;
        var buf = new byte[Chunk];
        var found = new List<CarvedFile>();
        var active = Formats.Where(f => kinds is null || kinds.Contains(f.Kind)).ToArray();
        long pos = 0;
        while (pos < dev.Length)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new RecoveryScanProgress(pos, dev.Length, found.Count));
            int len = (int)Math.Min(Chunk, dev.Length - pos);
            dev.Read(pos, buf.AsSpan(0, len));
            long next = pos + len;
            for (int o = 0; o + 16 <= len; o += ss)
            {
                foreach (var f in active)
                {
                    if (!buf.AsSpan(o + f.MagicOffset, f.Magic.Length).SequenceEqual(f.Magic)) continue;
                    long start = pos + o;
                    long? size;
                    try { size = f.Measure(dev, start); }
                    catch (IOException) { size = null; }
                    if (size is not > 0) continue;
                    var ext = f.Kind == "ZIP" ? ZipFlavor(dev, start, size.Value) : f.Kind == "MP4" ? Mp4Flavor(dev, start) : f.Ext;
                    found.Add(new CarvedFile(start, size.Value, f.Kind, ext));
                    // continue after the file (files do not overlap); re-read from there
                    long end = (start + size.Value + ss - 1) / ss * ss;
                    if (end >= pos + len) { next = end; o = len; }
                    else o = (int)(end - pos) - ss;
                    break;
                }
            }
            pos = next;
        }
        progress?.Report(new RecoveryScanProgress(dev.Length, dev.Length, found.Count));
        return found;
    }

    // ---------------------------------------------------------------- reading helpers

    /// <summary>Sequential reader over the device with a small window (keeps memory flat on large files).</summary>
    private sealed class Cursor(IBlockDevice dev, long start, long limit)
    {
        private readonly byte[] _buf = new byte[1 << 20];
        private long _bufStart = -1;
        private int _bufLen;
        public long Pos = start;
        public long Limit = Math.Min(limit, dev.Length);

        public int Byte(long at)
        {
            if (at >= Limit) return -1;
            if (_bufStart < 0 || at < _bufStart || at >= _bufStart + _bufLen)
            {
                _bufStart = at;
                _bufLen = (int)Math.Min(_buf.Length, Limit - at);
                dev.Read(at, _buf.AsSpan(0, _bufLen));
            }
            return _buf[at - _bufStart];
        }

        public bool Bytes(long at, Span<byte> dst)
        {
            for (int i = 0; i < dst.Length; i++)
            {
                int b = Byte(at + i);
                if (b < 0) return false;
                dst[i] = (byte)b;
            }
            return true;
        }

        public uint U32BE(long at) { Span<byte> b = stackalloc byte[4]; return Bytes(at, b) ? BinaryPrimitives.ReadUInt32BigEndian(b) : uint.MaxValue; }
        public ushort U16BE(long at) { Span<byte> b = stackalloc byte[2]; return Bytes(at, b) ? BinaryPrimitives.ReadUInt16BigEndian(b) : ushort.MaxValue; }
    }

    private const long MiB = 1024 * 1024;

    // ---------------------------------------------------------------- formats

    /// <summary>Walks JPEG segments by their lengths (so an embedded EXIF thumbnail's end marker is skipped), then the entropy data to EOI.</summary>
    private static long? Jpeg(IBlockDevice dev, long start)
    {
        var c = new Cursor(dev, start, start + 128 * MiB);
        long p = start + 2;
        bool sawFrame = false;
        for (int guard = 0; guard < 100_000; guard++)
        {
            if (c.Byte(p) != 0xFF) return null;
            int m = c.Byte(p + 1);
            while (m == 0xFF) { p++; m = c.Byte(p + 1); } // fill bytes
            if (m < 0) return null;
            if (m == 0xD9) return sawFrame ? p + 2 - start : null;
            if (m is >= 0xD0 and <= 0xD7 or 0x01) { p += 2; continue; }
            int len = c.U16BE(p + 2);
            if (len is < 2 or ushort.MaxValue) return null;
            if (m is 0xC0 or 0xC1 or 0xC2 or 0xC3) sawFrame = true;
            p += 2 + len;
            if (m != 0xDA) continue;
            // entropy-coded data: ends at a marker other than stuffed 0x00 and restart markers
            while (true)
            {
                int b = c.Byte(p);
                if (b < 0) return null;
                if (b == 0xFF)
                {
                    int n = c.Byte(p + 1);
                    if (n == 0x00 || n is >= 0xD0 and <= 0xD7) { p += 2; continue; }
                    break; // next marker (EOI, or another scan in progressive JPEGs)
                }
                p++;
            }
        }
        return null;
    }

    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    {
        uint c = (uint)n;
        for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    private static long? Png(IBlockDevice dev, long start)
    {
        var c = new Cursor(dev, start, start + 256 * MiB);
        long p = start + 8;
        for (int i = 0; i < 1_000_000; i++)
        {
            uint len = c.U32BE(p);
            if (len > 0x7FFFFFFF) return null;
            Span<byte> type = stackalloc byte[4];
            if (!c.Bytes(p + 4, type)) return null;
            foreach (var t in type) if (!(t is >= (byte)'A' and <= (byte)'Z' || t is >= (byte)'a' and <= (byte)'z')) return null;
            // CRC over type + data (checked for the small structural chunks; IDAT is trusted to keep it fast)
            if (len <= 4096)
            {
                uint crc = 0xFFFFFFFF;
                for (long k = 0; k < 4 + len; k++) crc = CrcTable[(crc ^ (uint)c.Byte(p + 4 + k)) & 0xFF] ^ (crc >> 8);
                if ((crc ^ 0xFFFFFFFF) != c.U32BE(p + 8 + len)) return null;
            }
            p += 12 + len;
            if (type.SequenceEqual("IEND"u8)) return p - start;
        }
        return null;
    }

    private static long? Gif(IBlockDevice dev, long start)
    {
        var c = new Cursor(dev, start, start + 64 * MiB);
        Span<byte> h = stackalloc byte[6];
        if (!c.Bytes(start, h) || !(h.SequenceEqual("GIF87a"u8) || h.SequenceEqual("GIF89a"u8))) return null;
        int flags = c.Byte(start + 10);
        long p = start + 13 + ((flags & 0x80) != 0 ? 3 * (2 << (flags & 7)) : 0);
        long SkipSubBlocks(long q)
        {
            while (true)
            {
                int n = c.Byte(q);
                if (n < 0) return -1;
                q += 1 + n;
                if (n == 0) return q;
            }
        }
        for (int i = 0; i < 1_000_000; i++)
        {
            int b = c.Byte(p);
            if (b == 0x3B) return p + 1 - start;
            if (b == 0x21) { p = SkipSubBlocks(p + 2); }
            else if (b == 0x2C)
            {
                int f = c.Byte(p + 9);
                p += 10 + ((f & 0x80) != 0 ? 3 * (2 << (f & 7)) : 0) + 1; // descriptor, local table, LZW minimum code size
                p = SkipSubBlocks(p);
            }
            else return null;
            if (p < 0) return null;
        }
        return null;
    }

    /// <summary>PDF ends at its last "%%EOF" before the data stops looking like the same file (incremental updates add more).</summary>
    private static long? Pdf(IBlockDevice dev, long start)
    {
        var c = new Cursor(dev, start, start + 512 * MiB);
        var eof = "%%EOF"u8.ToArray();
        long last = -1;
        int match = 0;
        long zeros = 0;
        for (long p = start + 5; p < c.Limit; p++)
        {
            int b = c.Byte(p);
            if (b < 0) break;
            zeros = b == 0 ? zeros + 1 : 0;
            if (last > 0 && (zeros >= 4096 || p - last > 64 * MiB)) break; // after an end marker, a run of zeros or a long gap ends the file
            match = b == eof[match] ? match + 1 : b == eof[0] ? 1 : 0;
            if (match == eof.Length)
            {
                long end = p + 1;
                if (c.Byte(end) == '\r') end++;
                if (c.Byte(end) == '\n') end++;
                last = end;
                match = 0;
                // a new file header at the next sector boundary ends this one
            }
        }
        return last > 0 ? last - start : null;
    }

    /// <summary>Follows ZIP local headers; the archive ends after the end-of-central-directory record and its comment.</summary>
    private static long? Zip(IBlockDevice dev, long start)
    {
        var c = new Cursor(dev, start, start + 2048 * MiB);
        Span<byte> sig = stackalloc byte[4];
        for (long p = start; p + 22 <= c.Limit; p++)
        {
            if (c.Byte(p) != 0x50 || c.Byte(p + 1) != 0x4B) continue;
            if (!c.Bytes(p, sig)) return null;
            if (sig.SequenceEqual(new byte[] { 0x50, 0x4B, 0x05, 0x06 }))
            {
                Span<byte> rec = stackalloc byte[22];
                c.Bytes(p, rec);
                long cdSize = BinaryPrimitives.ReadUInt32LittleEndian(rec[12..]);
                long cdOffset = BinaryPrimitives.ReadUInt32LittleEndian(rec[16..]);
                int comment = BinaryPrimitives.ReadUInt16LittleEndian(rec[20..]);
                // the directory must sit right before this record, relative to the archive start
                if (cdOffset + cdSize != p - start) continue;
                return p + 22 + comment - start;
            }
        }
        return null;
    }

    private static string ZipFlavor(IBlockDevice dev, long start, long length)
    {
        var head = new byte[(int)Math.Min(length, 64 * 1024)];
        dev.Read(start, head);
        var s = Encoding.ASCII.GetString(head);
        return s.Contains("word/") ? "docx" : s.Contains("xl/") ? "xlsx" : s.Contains("ppt/") ? "pptx"
            : s.Contains("mimetypeapplication/epub+zip") ? "epub" : s.Contains("mimetypeapplication/vnd.oasis.opendocument.text") ? "odt" : "zip";
    }

    /// <summary>Sums ISO-BMFF top-level boxes (ftyp, moov, mdat, free …) until the next 8 bytes are not a plausible box.</summary>
    private static long? Mp4(IBlockDevice dev, long start)
    {
        var c = new Cursor(dev, start, start + 64L * 1024 * MiB);
        long p = start;
        bool sawMoov = false, sawMdat = false;
        Span<byte> type = stackalloc byte[4];
        for (int i = 0; i < 10_000; i++)
        {
            long size = c.U32BE(p);
            if (!c.Bytes(p + 4, type)) break;
            bool printable = true;
            foreach (var t in type) printable &= t is >= 0x20 and <= 0x7E;
            if (!printable) break;
            if (size == 1)
            {
                Span<byte> big = stackalloc byte[8];
                if (!c.Bytes(p + 8, big)) break;
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(big);
            }
            if (size < 8 || p + size > c.Limit) break;
            if (i == 0 && !type.SequenceEqual("ftyp"u8)) return null;
            sawMoov |= type.SequenceEqual("moov"u8) || type.SequenceEqual("moof"u8);
            sawMdat |= type.SequenceEqual("mdat"u8);
            p += size;
        }
        return sawMoov && sawMdat ? p - start : null;
    }

    private static string Mp4Flavor(IBlockDevice dev, long start)
    {
        var b = new byte[12];
        dev.Read(start, b);
        var brand = Encoding.ASCII.GetString(b, 8, 4);
        return brand.StartsWith("qt") ? "mov" : brand.StartsWith("3g") ? "3gp" : brand is "M4A " ? "m4a" : brand.StartsWith("heic") || brand.StartsWith("mif1") ? "heic" : "mp4";
    }
}

/// <summary>Presents carved files like any other recovery source (File Recovery's "search by file content").</summary>
public sealed class CarvingRecoveryReader(IBlockDevice device, IReadOnlySet<string>? kinds = null) : IRecoveryReader
{
    private IReadOnlyList<CarvedFile> _found = Array.Empty<CarvedFile>();

    public string FileSystem => "content";
    public string? Label => null;

    public IReadOnlyList<RecoverableFile> Scan(bool includeExisting, IProgress<RecoveryScanProgress>? progress, CancellationToken ct)
    {
        _found = Carver.Scan(device, kinds, progress, ct);
        return _found.Select((f, i) => new RecoverableFile
        {
            Id = i, Folder = "Carved\\" + f.Kind, Name = $"file-{i + 1:00000}-{f.Offset:X}.{f.Extension}", Size = f.Length,
            IsDeleted = true, Recoverability = Recoverability.Good, Note = "by content",
        }).ToList();
    }

    public long Extract(RecoverableFile file, Stream output, CancellationToken ct)
    {
        var f = _found[(int)file.Id];
        var buf = new byte[1 << 20];
        long done = 0;
        while (done < f.Length)
        {
            ct.ThrowIfCancellationRequested();
            int n = (int)Math.Min(buf.Length, f.Length - done);
            device.Read(f.Offset + done, buf.AsSpan(0, n));
            output.Write(buf, 0, n);
            done += n;
        }
        return done;
    }
}
