using System.Buffers.Binary;
using System.Text;
using Lasteast.Core.RawDisk;

namespace Lasteast.Core.Recovery;

/// <summary>A read-only window onto part of another device (a partition, or a found lost partition).</summary>
public sealed class OffsetDevice : IBlockDevice
{
    private readonly IBlockDevice _inner;
    private readonly long _offset;
    private readonly bool _ownsInner;

    public OffsetDevice(IBlockDevice inner, long offset, long length, bool ownsInner = false)
    {
        _ownsInner = ownsInner;
        if (offset < 0 || length <= 0 || offset + length > inner.Length) throw new ArgumentOutOfRangeException(nameof(length), "window outside the device");
        _inner = inner;
        _offset = offset;
        Length = length;
    }

    public long Length { get; }
    public int SectorSize => _inner.SectorSize;

    public void Read(long offset, Span<byte> buffer)
    {
        if (offset < 0 || offset + buffer.Length > Length) throw new IOException($"read outside the volume at {offset}");
        _inner.Read(_offset + offset, buffer);
    }

    public void Write(long offset, ReadOnlySpan<byte> buffer) => throw new InvalidOperationException("recovery sources are read-only");
    public void Flush() { }
    public void Dispose() { if (_ownsInner) _inner.Dispose(); }
}

public enum Recoverability
{
    /// <summary>Not deleted: content is intact.</summary>
    Intact,
    /// <summary>Deleted, but none of its clusters have been reused (or the data lives inside the file record).</summary>
    Good,
    /// <summary>Deleted, and some of its clusters are in use by other files now: content is probably damaged.</summary>
    Poor,
    /// <summary>The content cannot be read with this method (compressed, encrypted, or the location is lost).</summary>
    Unrecoverable,
}

public sealed record RecoverableFile
{
    public required long Id { get; init; }
    /// <summary>Folder path inside the volume, using '\' (empty for the root). "&lt;lost&gt;" when the parent folder is gone.</summary>
    public required string Folder { get; init; }
    public required string Name { get; init; }
    public required long Size { get; init; }
    public bool IsDeleted { get; init; }
    public DateTime? Modified { get; init; }
    public DateTime? Created { get; init; }
    public Recoverability Recoverability { get; init; }
    public string? Note { get; init; }
    public string FullPath => Folder.Length == 0 ? Name : Folder + "\\" + Name;
}

public sealed record RecoveryScanProgress(long Done, long Total, int Found);

/// <summary>A file system read straight from sectors (no Windows driver), so deleted entries and unmountable volumes are visible.</summary>
public interface IRecoveryReader
{
    string FileSystem { get; }
    string? Label { get; }
    IReadOnlyList<RecoverableFile> Scan(bool includeExisting, IProgress<RecoveryScanProgress>? progress, CancellationToken ct);
    /// <summary>Writes the file's content to <paramref name="output"/>; returns bytes written.</summary>
    long Extract(RecoverableFile file, Stream output, CancellationToken ct);
    /// <summary>Set when the volume is read through its backup boot sector (the start is damaged).</summary>
    bool ThroughBackupBootSector => false;
}

/// <summary>A read-only view of a volume with its damaged boot region replaced, in memory only, by the backup copy.</summary>
public sealed class PatchedDevice(IBlockDevice inner, IReadOnlyList<(long Offset, byte[] Data)> patches) : IBlockDevice
{
    public long Length => inner.Length;
    public int SectorSize => inner.SectorSize;

    public void Read(long offset, Span<byte> buffer)
    {
        inner.Read(offset, buffer);
        foreach (var (po, data) in patches)
        {
            long from = Math.Max(offset, po), to = Math.Min(offset + buffer.Length, po + data.Length);
            if (from < to) data.AsSpan((int)(from - po), (int)(to - from)).CopyTo(buffer[(int)(from - offset)..]);
        }
    }

    public void Write(long offset, ReadOnlySpan<byte> buffer) => throw new InvalidOperationException("recovery sources are read-only");
    public void Flush() { }
    public void Dispose() { }
}

/// <summary>Marks a reader that works through the backup boot sector.</summary>
internal sealed class BackupBootReader(IRecoveryReader inner) : IRecoveryReader
{
    public string FileSystem => inner.FileSystem;
    public string? Label => inner.Label;
    public IReadOnlyList<RecoverableFile> Scan(bool includeExisting, IProgress<RecoveryScanProgress>? progress, CancellationToken ct) => inner.Scan(includeExisting, progress, ct);
    public long Extract(RecoverableFile file, Stream output, CancellationToken ct) => inner.Extract(file, output, ct);
    public bool ThroughBackupBootSector => true;
}

public static class RecoveryReaders
{
    /// <summary>Opens the right reader for the volume's boot sector, or null when it is not NTFS/FAT32/exFAT.</summary>
    public static IRecoveryReader? Open(IBlockDevice volume)
    {
        var bs = new byte[512];
        volume.Read(0, bs);
        if (bs.AsSpan(3, 8).SequenceEqual("NTFS    "u8)) return new NtfsRecoveryReader(volume);
        if (bs.AsSpan(82, 8).SequenceEqual("FAT32   "u8)) return new Fat32RecoveryReader(volume);
        if (bs.AsSpan(3, 8).SequenceEqual("EXFAT   "u8)) return new ExfatRecoveryReader(volume);
        // damaged start: read through the file system's own backup boot sector, in memory only (nothing is written)
        try
        {
            var d = BootSectorRepair.Diagnose(volume);
            if (d.State == BootSectorState.Repairable && Open(new PatchedDevice(volume, d.Writes)) is { } r) return new BackupBootReader(r);
        }
        catch (IOException) { }
        return null;
    }

    internal static string SafeName(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name) sb.Append(ch < 32 || "<>:\"/\\|?*".Contains(ch) ? '_' : ch);
        var s = sb.ToString().TrimEnd(' ', '.');
        return s.Length == 0 ? "_" : s;
    }
}

public sealed record RecoveryReport(int Recovered, int Failed, long Bytes, IReadOnlyList<(string File, string Error)> Errors);

/// <summary>Copies recovered files into a folder that must not be on the disk being recovered (that could overwrite the data).</summary>
public static class RecoveryWriter
{
    /// <param name="destinationDisk">Disk number that holds <paramref name="destinationFolder"/> (null: not a local disk, e.g. a network share).</param>
    public static RecoveryReport Recover(IRecoveryReader reader, IEnumerable<RecoverableFile> files, string destinationFolder,
        int sourceDisk, int? destinationDisk, IProgress<RecoveryScanProgress>? progress, CancellationToken ct)
    {
        if (destinationDisk == sourceDisk)
            throw new RawOperationException("Rec.Error.SameDisk", "the destination folder is on the disk being recovered");
        var list = files.ToList();
        int ok = 0, n = 0;
        long bytes = 0;
        var errors = new List<(string, string)>();
        foreach (var f in list)
        {
            ct.ThrowIfCancellationRequested();
            n++;
            if (f.Recoverability == Recoverability.Unrecoverable) { errors.Add((f.FullPath, f.Note ?? "unrecoverable")); continue; }
            var folder = Path.Combine(new[] { destinationFolder }.Concat(f.Folder.Split('\\', StringSplitOptions.RemoveEmptyEntries).Select(RecoveryReaders.SafeName)).ToArray());
            try
            {
                Directory.CreateDirectory(folder);
                var target = Unique(Path.Combine(folder, RecoveryReaders.SafeName(f.Name)));
                using (var fs = new FileStream(target, FileMode.CreateNew, FileAccess.Write))
                    bytes += reader.Extract(f, fs, ct);
                if (f.Modified is { } m) File.SetLastWriteTimeUtc(target, m);
                if (f.Created is { } c) File.SetCreationTimeUtc(target, c);
                ok++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or RawOperationException)
            {
                errors.Add((f.FullPath, ex.Message));
            }
            progress?.Report(new RecoveryScanProgress(n, list.Count, ok));
        }
        return new RecoveryReport(ok, errors.Count, bytes, errors);
    }

    private static string Unique(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (int i = 1; ; i++)
        {
            var p = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(p)) return p;
        }
    }
}

// =============================================================================================
//  NTFS
// =============================================================================================

/// <summary>
/// Reads NTFS by parsing the MFT directly: existing and deleted file records, paths through parent references,
/// resident and non-resident data (data runs), and the cluster bitmap to judge whether deleted data was overwritten.
/// Very fragmented files whose attributes continue in extension records ($ATTRIBUTE_LIST) are followed, and
/// NTFS-compressed files (LZNT1) are decompressed. Encrypted (EFS) files are listed but cannot be read without the
/// owner's keys.
/// </summary>
public sealed class NtfsRecoveryReader : IRecoveryReader
{
    private readonly IBlockDevice _vol;
    private readonly int _bps;
    private readonly long _clusterSize;
    private readonly int _recordSize;
    private List<(long Vcn, long Lcn, long Count)> _mftRuns;
    private byte[]? _bitmap;
    private readonly Dictionary<long, RecordInfo> _records = new();

    private sealed record RecordInfo(long Number, ushort Sequence, bool InUse, bool IsDirectory, long ParentRef, ushort ParentSeq, string? Name,
        long Size, DateTime? Created, DateTime? Modified, byte[]? Resident, List<(long Vcn, long Lcn, long Count)>? Runs, string? Problem, int CompressionUnit);

    public NtfsRecoveryReader(IBlockDevice volume)
    {
        _vol = volume;
        var bs = new byte[512];
        volume.Read(0, bs);
        if (!bs.AsSpan(3, 8).SequenceEqual("NTFS    "u8)) throw new RawOperationException("Rec.Error.NotNtfs", "not an NTFS boot sector");
        _bps = BinaryPrimitives.ReadUInt16LittleEndian(bs.AsSpan(0x0B));
        int spcRaw = bs[0x0D];
        int spc = spcRaw > 0x80 ? 1 << (256 - spcRaw) : spcRaw;
        _clusterSize = (long)_bps * spc;
        sbyte cpr = (sbyte)bs[0x40];
        _recordSize = cpr < 0 ? 1 << -cpr : (int)(cpr * _clusterSize);
        if (_bps is not (512 or 1024 or 2048 or 4096) || spc <= 0 || _recordSize is < 512 or > 65536)
            throw new RawOperationException("Rec.Error.NotNtfs", "implausible NTFS geometry");
        long mftLcn = BinaryPrimitives.ReadInt64LittleEndian(bs.AsSpan(0x30));
        long mirrLcn = BinaryPrimitives.ReadInt64LittleEndian(bs.AsSpan(0x38));

        // record 0 describes where the MFT lives; when it is damaged, $MFTMirr holds a copy of it
        List<(long Vcn, long Lcn, long Count)>? runs = null;
        foreach (var lcn in new[] { mftLcn, mirrLcn })
        {
            if (lcn <= 0 || (lcn + 1) * _clusterSize > volume.Length) continue;
            var rec0 = new byte[_recordSize];
            _vol.Read(lcn * _clusterSize, rec0);
            if (ApplyFixups(rec0) && FindData(rec0, out _, out _, out _)?.Runs is { Count: > 0 } r) { runs = r; UsedMftMirror = lcn == mirrLcn; break; }
        }
        _mftRuns = runs ?? throw new RawOperationException("Rec.Error.Mft", "MFT record 0 and its mirror are damaged");
        // a fragmented MFT continues its run list in extension records (found with the runs known so far)
        var r0 = new byte[_recordSize];
        if (ReadRecord(0, r0) && Attributes(r0).Any(a => a.Type == 0x20)
            && FindData(RecordSet(0, r0), out _, out _, out _) is { Runs: { Count: > 0 } full } && full.Sum(x => x.Count) > _mftRuns.Sum(x => x.Count))
            _mftRuns = full;
        Label = ReadLabel();
    }

    public string FileSystem => "NTFS";
    public string? Label { get; }
    /// <summary>MFT record 0 was damaged and the MFT was located through $MFTMirr.</summary>
    public bool UsedMftMirror { get; }
    public long ClusterSize => _clusterSize;

    private long MftRecordCount => _mftRuns.Sum(r => r.Count) * _clusterSize / _recordSize;

    private bool ReadRecord(long n, byte[] buf)
    {
        long byteOff = n * _recordSize;
        // records may span clusters when cluster < record size
        int done = 0;
        while (done < _recordSize)
        {
            long v = (byteOff + done) / _clusterSize;
            var run = _mftRuns.FirstOrDefault(r => v >= r.Vcn && v < r.Vcn + r.Count);
            if (run.Count == 0 || run.Lcn < 0) return false;
            long pos = (run.Lcn + (v - run.Vcn)) * _clusterSize + (byteOff + done) % _clusterSize;
            int chunk = (int)Math.Min(_recordSize - done, _clusterSize - (byteOff + done) % _clusterSize);
            _vol.Read(pos, buf.AsSpan(done, chunk));
            done += chunk;
        }
        return buf.AsSpan(0, 4).SequenceEqual("FILE"u8) && ApplyFixups(buf);
    }

    /// <summary>Update sequence array: the last two bytes of every sector hold the USN; restore the original bytes.</summary>
    private bool ApplyFixups(byte[] rec)
    {
        int usaOff = BinaryPrimitives.ReadUInt16LittleEndian(rec.AsSpan(4));
        int usaCount = BinaryPrimitives.ReadUInt16LittleEndian(rec.AsSpan(6));
        if (usaOff + usaCount * 2 > rec.Length || usaCount == 0) return false;
        ushort usn = BinaryPrimitives.ReadUInt16LittleEndian(rec.AsSpan(usaOff));
        if (usaCount < 2) return true;
        int stride = rec.Length / (usaCount - 1);
        for (int i = 1; i < usaCount; i++)
        {
            int end = i * stride - 2;
            if (end + 2 > rec.Length) break;
            if (BinaryPrimitives.ReadUInt16LittleEndian(rec.AsSpan(end)) != usn) return false; // torn write
            rec[end] = rec[usaOff + i * 2];
            rec[end + 1] = rec[usaOff + i * 2 + 1];
        }
        return true;
    }

    private static IEnumerable<(int Type, int Offset, int Length)> Attributes(byte[] rec)
    {
        int off = BinaryPrimitives.ReadUInt16LittleEndian(rec.AsSpan(0x14));
        int used = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(rec.AsSpan(0x18)), (uint)rec.Length);
        while (off + 16 <= used)
        {
            int type = BinaryPrimitives.ReadInt32LittleEndian(rec.AsSpan(off));
            if (type == -1 || type == 0) yield break;
            int len = BinaryPrimitives.ReadInt32LittleEndian(rec.AsSpan(off + 4));
            if (len < 16 || off + len > used) yield break;
            yield return (type, off, len);
            off += len;
        }
    }

    private sealed record DataInfo(byte[]? Resident, List<(long Vcn, long Lcn, long Count)>? Runs, long Size, int CompressionUnit = 0);

    /// <summary>The base record plus the extension records its $ATTRIBUTE_LIST points to (each checked to belong to it).</summary>
    private List<byte[]> RecordSet(long n, byte[] baseRec)
    {
        var set = new List<byte[]> { baseRec.ToArray() };
        foreach (var (type, off, _) in Attributes(baseRec))
        {
            if (type != 0x20) continue;
            byte[]? list;
            if (baseRec[off + 8] == 0)
            {
                int vlen = BinaryPrimitives.ReadInt32LittleEndian(baseRec.AsSpan(off + 0x10));
                int voff = BinaryPrimitives.ReadUInt16LittleEndian(baseRec.AsSpan(off + 0x14));
                if (off + voff + vlen > baseRec.Length) break;
                list = baseRec.AsSpan(off + voff, vlen).ToArray();
            }
            else
            {
                int runOff = BinaryPrimitives.ReadUInt16LittleEndian(baseRec.AsSpan(off + 0x20));
                long size = BinaryPrimitives.ReadInt64LittleEndian(baseRec.AsSpan(off + 0x30));
                if (size is <= 0 or > 16 * 1024 * 1024) break;
                var lruns = DecodeRuns(baseRec, off + runOff, off + BinaryPrimitives.ReadInt32LittleEndian(baseRec.AsSpan(off + 4)));
                using var ms = new MemoryStream();
                try { Copy(new DataInfo(null, lruns, size), ms, CancellationToken.None); } catch (RawOperationException) { break; }
                list = ms.ToArray();
            }
            var seen = new HashSet<long> { n };
            for (int e = 0; e + 0x1A <= list.Length;)
            {
                int len = BinaryPrimitives.ReadUInt16LittleEndian(list.AsSpan(e + 4));
                if (len < 0x1A) break;
                long rec = BinaryPrimitives.ReadInt64LittleEndian(list.AsSpan(e + 0x10)) & 0xFFFFFFFFFFFF;
                if (seen.Add(rec))
                {
                    var ext = new byte[_recordSize];
                    bool ok;
                    try { ok = ReadRecord(rec, ext); } catch (IOException) { ok = false; }
                    // an extension record names its base record; a reused record does not
                    if (ok && (BinaryPrimitives.ReadInt64LittleEndian(ext.AsSpan(0x20)) & 0xFFFFFFFFFFFF) == n) set.Add(ext);
                }
                e += len;
            }
            break;
        }
        return set;
    }

    private DataInfo? FindData(byte[] rec, out int flags, out bool hasAttrList, out bool partial)
    {
        hasAttrList = Attributes(rec).Any(a => a.Type == 0x20);
        return FindData(new List<byte[]> { rec }, out flags, out partial, out _);
    }

    /// <summary>
    /// Unnamed $DATA across a record set: resident bytes, or the run list merged from every segment (segments in
    /// extension records continue at their own starting VCN). flags = attribute flags (compressed/encrypted/sparse).
    /// </summary>
    private DataInfo? FindData(List<byte[]> recs, out int flags, out bool partial, out int compressionUnit)
    {
        flags = 0;
        partial = false;
        compressionUnit = 0;
        var runs = new List<(long Vcn, long Lcn, long Count)>();
        long size = -1, lastVcn = -1;
        bool any = false;
        foreach (var rec in recs)
        foreach (var (type, off, _) in Attributes(rec))
        {
            if (type != 0x80 || rec[off + 9] != 0) continue;
            any = true;
            if (rec[off + 8] == 0)
            {
                flags = BinaryPrimitives.ReadUInt16LittleEndian(rec.AsSpan(off + 0x0C));
                int vlen = BinaryPrimitives.ReadInt32LittleEndian(rec.AsSpan(off + 0x10));
                int voff = BinaryPrimitives.ReadUInt16LittleEndian(rec.AsSpan(off + 0x14));
                if (off + voff + vlen > rec.Length) return null;
                return new DataInfo(rec.AsSpan(off + voff, vlen).ToArray(), null, vlen);
            }
            long startVcn = BinaryPrimitives.ReadInt64LittleEndian(rec.AsSpan(off + 0x10));
            long endVcn = BinaryPrimitives.ReadInt64LittleEndian(rec.AsSpan(off + 0x18));
            int runOff = BinaryPrimitives.ReadUInt16LittleEndian(rec.AsSpan(off + 0x20));
            if (startVcn == 0)
            {
                // sizes and the compression unit are only valid in the first segment
                flags = BinaryPrimitives.ReadUInt16LittleEndian(rec.AsSpan(off + 0x0C));
                size = BinaryPrimitives.ReadInt64LittleEndian(rec.AsSpan(off + 0x30));
                int cu = rec[off + 0x22];
                compressionUnit = cu is > 0 and <= 8 ? 1 << cu : 0;
            }
            lastVcn = Math.Max(lastVcn, endVcn);
            var seg = DecodeRuns(rec, off + runOff, off + BinaryPrimitives.ReadInt32LittleEndian(rec.AsSpan(off + 4)), startVcn);
            if (seg.Sum(r => r.Count) < endVcn - startVcn + 1) partial = true;
            runs.AddRange(seg);
        }
        if (!any) return null;
        runs = runs.OrderBy(r => r.Vcn).GroupBy(r => r.Vcn).Select(g => g.First()).ToList();
        if (size < 0) { partial = true; size = 0; } // first segment missing
        long covered = 0;
        foreach (var r in runs) { if (r.Vcn != covered) { partial = true; break; } covered += r.Count; }
        if (covered < lastVcn + 1) partial = true;
        return new DataInfo(null, runs, size, (flags & 0x0001) != 0 ? compressionUnit : 0);
    }

    /// <summary>Data runs: header nibbles give the length/offset field sizes; offsets are relative and signed; no offset = sparse (Lcn -1).</summary>
    public static List<(long Vcn, long Lcn, long Count)> DecodeRuns(byte[] rec, int pos, int end, long startVcn = 0)
    {
        var runs = new List<(long, long, long)>();
        long vcn = startVcn, lcn = 0;
        while (pos < end && rec[pos] != 0)
        {
            int lenSize = rec[pos] & 0x0F, offSize = rec[pos] >> 4;
            if (lenSize == 0 || lenSize > 8 || offSize > 8 || pos + 1 + lenSize + offSize > end) break;
            long count = 0;
            for (int i = 0; i < lenSize; i++) count |= (long)rec[pos + 1 + i] << (8 * i);
            if (offSize == 0)
            {
                runs.Add((vcn, -1, count));
            }
            else
            {
                long delta = 0;
                for (int i = 0; i < offSize; i++) delta |= (long)rec[pos + 1 + lenSize + i] << (8 * i);
                if ((rec[pos + lenSize + offSize] & 0x80) != 0) delta |= -1L << (8 * offSize); // sign extend
                lcn += delta;
                runs.Add((vcn, lcn, count));
            }
            vcn += count;
            pos += 1 + lenSize + offSize;
        }
        return runs;
    }

    private string? ReadLabel()
    {
        var buf = new byte[_recordSize];
        if (!ReadRecord(3, buf)) return null;
        foreach (var (type, off, _) in Attributes(buf))
        {
            if (type != 0x60 || buf[off + 8] != 0) continue;
            int vlen = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(off + 0x10));
            int voff = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(off + 0x14));
            return Encoding.Unicode.GetString(buf, off + voff, vlen);
        }
        return null;
    }

    private bool ClusterInUse(long lcn)
    {
        if (_bitmap is null)
        {
            var buf = new byte[_recordSize];
            var d = ReadRecord(6, buf) ? FindData(buf, out _, out _, out _) : null;
            if (d is null) { _bitmap = Array.Empty<byte>(); }
            else
            {
                using var ms = new MemoryStream();
                Copy(d, ms, CancellationToken.None);
                _bitmap = ms.ToArray();
            }
        }
        if (_bitmap.Length == 0) return true; // unknown: assume the worst
        long b = lcn >> 3;
        return b >= _bitmap.Length || (_bitmap[b] & (1 << (int)(lcn & 7))) != 0;
    }

    private static DateTime? FileTime(long v) => v > 0 && v < DateTime.MaxValue.ToFileTimeUtc() ? DateTime.FromFileTimeUtc(v) : null;

    public IReadOnlyList<RecoverableFile> Scan(bool includeExisting, IProgress<RecoveryScanProgress>? progress, CancellationToken ct)
    {
        _records.Clear();
        long total = MftRecordCount;
        var buf = new byte[_recordSize];
        for (long n = 0; n < total; n++)
        {
            if ((n & 1023) == 0) { ct.ThrowIfCancellationRequested(); progress?.Report(new RecoveryScanProgress(n, total, _records.Count)); }
            bool ok;
            try { ok = ReadRecord(n, buf); }
            catch (IOException) { ok = false; }
            if (!ok) continue;
            if (BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(0x20)) != 0) continue; // extension record
            ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(0x16));
            ushort seq = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(0x10));

            string? name = null;
            int bestNs = -1;
            long parent = 0;
            ushort parentSeq = 0;
            DateTime? created = null, modified = null;
            long fileNameSize = 0;
            var set = Attributes(buf).Any(a => a.Type == 0x20) ? RecordSet(n, buf) : new List<byte[]> { buf };
            foreach (var rec in set)
            foreach (var (type, off, _) in Attributes(rec))
            {
                var buf2 = rec;
                if (buf2[off + 8] != 0) continue;
                int voff = off + BinaryPrimitives.ReadUInt16LittleEndian(buf2.AsSpan(off + 0x14));
                if (type == 0x10 && voff + 16 <= buf2.Length)
                {
                    created = FileTime(BinaryPrimitives.ReadInt64LittleEndian(buf2.AsSpan(voff)));
                    modified = FileTime(BinaryPrimitives.ReadInt64LittleEndian(buf2.AsSpan(voff + 8)));
                }
                if (type == 0x30 && voff + 0x42 <= buf2.Length)
                {
                    int ns = buf2[voff + 0x41];
                    int rank = ns switch { 1 => 3, 3 => 3, 0 => 2, _ => 1 }; // Win32 > POSIX > DOS
                    if (rank <= bestNs) continue;
                    int len = buf2[voff + 0x40];
                    if (voff + 0x42 + len * 2 > buf2.Length) continue;
                    bestNs = rank;
                    long pref = BinaryPrimitives.ReadInt64LittleEndian(buf2.AsSpan(voff));
                    parent = pref & 0xFFFFFFFFFFFF;
                    parentSeq = (ushort)(pref >> 48);
                    name = Encoding.Unicode.GetString(buf2, voff + 0x42, len * 2);
                    fileNameSize = BinaryPrimitives.ReadInt64LittleEndian(buf2.AsSpan(voff + 0x30));
                }
            }
            bool isDir = (flags & 0x02) != 0;
            DataInfo? data = null;
            string? problem = null;
            if (!isDir)
            {
                data = FindData(set, out var aflags, out var partial, out _);
                if ((aflags & 0x4000) != 0) problem = "encrypted";
                // Windows truncates very fragmented files (attribute lists) before deleting them: the run list is
                // emptied, so nothing records where the data was. Say so instead of "recovering" an empty file.
                else if ((flags & 0x01) == 0 && data is { Resident: null, Size: 0 } && data.Runs!.Count == 0 && (set.Count > 1 || Attributes(buf).Any(x => x.Type == 0x20) || fileNameSize > 0))
                    problem = "location released by Windows when deleted (try search by content)";
                else if ((aflags & 0x0001) != 0 && data is { Resident: null, CompressionUnit: 0 }) problem = "compressed (unknown unit)";
                else if (partial) problem = "part of the location is lost";
            }
            _records[n] = new RecordInfo(n, seq, (flags & 0x01) != 0, isDir, parent, parentSeq, name,
                problem is not null && (data?.Size ?? 0) == 0 ? fileNameSize : data?.Size ?? 0, created, modified,
                data?.Resident, data?.Runs, problem, data?.CompressionUnit ?? 0);
        }

        var result = new List<RecoverableFile>();
        foreach (var r in _records.Values)
        {
            if (r.IsDirectory || r.Name is null) continue;
            if (r.Number < 24) continue; // metafiles
            if (r.InUse && !includeExisting) continue;
            var folder = PathOf(r.ParentRef, r.ParentSeq, 0);
            if (folder.StartsWith('$')) continue; // $Extend etc.
            var rec = Recoverability.Intact;
            if (r.Problem is not null) rec = Recoverability.Unrecoverable;
            else if (!r.InUse)
                rec = r.Resident is not null ? Recoverability.Good
                    : r.Runs is null ? Recoverability.Unrecoverable
                    : r.Runs.Any(x => x.Lcn >= 0 && Enumerable.Range(0, (int)Math.Min(x.Count, int.MaxValue)).Any(i => ClusterInUse(x.Lcn + i))) ? Recoverability.Poor
                    : Recoverability.Good;
            result.Add(new RecoverableFile
            {
                Id = r.Number, Folder = folder, Name = r.Name, Size = r.Size, IsDeleted = !r.InUse, Created = r.Created, Modified = r.Modified,
                Recoverability = rec, Note = r.Problem,
            });
        }
        progress?.Report(new RecoveryScanProgress(total, total, result.Count));
        return result.OrderBy(f => f.Folder, StringComparer.OrdinalIgnoreCase).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private string PathOf(long dir, ushort seq, int depth)
    {
        if (dir == 5) return "";
        if (depth > 64 || !_records.TryGetValue(dir, out var d) || !d.IsDirectory || d.Name is null || (seq != 0 && d.Sequence != seq && d.InUse))
            return "<lost>";
        var parent = PathOf(d.ParentRef, d.ParentSeq, depth + 1);
        return parent.Length == 0 ? d.Name : parent + "\\" + d.Name;
    }

    /// <summary>Technical description of a file's MFT records (for technician logs and diagnostics).</summary>
    public string Describe(RecoverableFile file)
    {
        var buf = new byte[_recordSize];
        if (!ReadRecord(file.Id, buf)) return "record unreadable";
        var sb = new StringBuilder();
        foreach (var rec in RecordSet(file.Id, buf))
        {
            sb.Append($"[rec flags={BinaryPrimitives.ReadUInt16LittleEndian(rec.AsSpan(0x16))} base={BinaryPrimitives.ReadInt64LittleEndian(rec.AsSpan(0x20)) & 0xFFFFFFFFFFFF}");
            foreach (var (type, off, len) in Attributes(rec))
            {
                sb.Append($" {type:X2}");
                if (rec[off + 8] != 0)
                {
                    long sv = BinaryPrimitives.ReadInt64LittleEndian(rec.AsSpan(off + 0x10)), ev = BinaryPrimitives.ReadInt64LittleEndian(rec.AsSpan(off + 0x18));
                    int runOff = BinaryPrimitives.ReadUInt16LittleEndian(rec.AsSpan(off + 0x20));
                    var runs = DecodeRuns(rec, off + runOff, off + len, sv);
                    sb.Append($"(vcn {sv}-{ev}, runs {runs.Count}/{runs.Sum(r => r.Count)}cl");
                    if (sv == 0) sb.Append($", alloc {BinaryPrimitives.ReadInt64LittleEndian(rec.AsSpan(off + 0x28))} size {BinaryPrimitives.ReadInt64LittleEndian(rec.AsSpan(off + 0x30))} init {BinaryPrimitives.ReadInt64LittleEndian(rec.AsSpan(off + 0x38))}");
                    sb.Append(')');
                }
                else if (type == 0x20) sb.Append($"(resident {BinaryPrimitives.ReadInt32LittleEndian(rec.AsSpan(off + 0x10))}b)");
            }
            sb.Append(']');
        }
        return sb.ToString();
    }

    public long Extract(RecoverableFile file, Stream output, CancellationToken ct)
    {
        if (!_records.TryGetValue(file.Id, out var r)) throw new RawOperationException("Rec.Error.Unknown", "scan the volume first");
        if (r.Problem is not null) throw new RawOperationException("Rec.Error.Unsupported", r.Problem);
        return Copy(new DataInfo(r.Resident, r.Runs, r.Size, r.CompressionUnit), output, ct);
    }

    private long Copy(DataInfo d, Stream output, CancellationToken ct)
    {
        if (d.Resident is not null) { output.Write(d.Resident); return d.Resident.Length; }
        if (d.CompressionUnit > 0) return CopyCompressed(d, output, ct);
        long remaining = d.Size, written = 0;
        var buf = new byte[_clusterSize * 64];
        foreach (var (_, lcn, count) in d.Runs!.OrderBy(x => x.Vcn))
        {
            for (long c = 0; c < count && remaining > 0; c += 64)
            {
                ct.ThrowIfCancellationRequested();
                long n = Math.Min(64, count - c);
                int len = (int)Math.Min(n * _clusterSize, remaining);
                var span = buf.AsSpan(0, len);
                if (lcn < 0) span.Clear();
                else _vol.Read((lcn + c) * _clusterSize, span);
                output.Write(span);
                remaining -= len;
                written += len;
            }
        }
        if (remaining > 0) throw new RawOperationException("Rec.Error.Truncated", $"{remaining} bytes missing from the data runs");
        return written;
    }

    /// <summary>
    /// NTFS compression works in units of (usually) 16 clusters: a unit with all clusters allocated is stored raw, one
    /// whose tail is sparse holds LZNT1-compressed data in its allocated clusters, an all-sparse unit is zeros.
    /// </summary>
    private long CopyCompressed(DataInfo d, Stream output, CancellationToken ct)
    {
        var runs = d.Runs!;
        int cu = d.CompressionUnit;
        long unitBytes = cu * _clusterSize, remaining = d.Size, written = 0;
        long Lcn(long vcn)
        {
            int lo = 0, hi = runs.Count - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                var r = runs[mid];
                if (vcn < r.Vcn) hi = mid - 1;
                else if (vcn >= r.Vcn + r.Count) lo = mid + 1;
                else return r.Lcn < 0 ? -1 : r.Lcn + (vcn - r.Vcn);
            }
            return -1;
        }
        var raw = new byte[unitBytes];
        for (long unit = 0; remaining > 0; unit++)
        {
            ct.ThrowIfCancellationRequested();
            int allocated = 0;
            for (int k = 0; k < cu; k++) if (Lcn(unit * cu + k) >= 0) allocated++;
            byte[] plain;
            if (allocated == 0) plain = new byte[unitBytes];
            else
            {
                for (int k = 0; k < allocated; k++)
                    _vol.Read(Lcn(unit * cu + k) * _clusterSize, raw.AsSpan((int)(k * _clusterSize), (int)_clusterSize));
                plain = allocated == cu ? raw : Lznt1.Decompress(raw.AsSpan(0, (int)(allocated * _clusterSize)), (int)unitBytes);
            }
            int len = (int)Math.Min(unitBytes, remaining);
            output.Write(plain, 0, len);
            remaining -= len;
            written += len;
        }
        return written;
    }
}

// =============================================================================================
//  FAT32
// =============================================================================================

/// <summary>
/// Reads FAT32 directories including deleted entries (first character 0xE5, long names restored from LFN entries).
/// Deleted files have their cluster chain cleared by Windows, so the content is read as a contiguous run from the
/// start cluster — correct for unfragmented files, which is the common case; files whose clusters are now in use are marked Poor.
/// </summary>
public sealed class Fat32RecoveryReader : IRecoveryReader
{
    private readonly IBlockDevice _vol;
    private readonly int _bps;
    private readonly long _clusterSize;
    private readonly long _dataStart;
    private readonly uint[] _fat;
    private readonly uint _rootCluster;
    private readonly long _clusterCount;
    private readonly Dictionary<long, (uint Cluster, bool Deleted)> _starts = new();

    public Fat32RecoveryReader(IBlockDevice volume)
    {
        _vol = volume;
        var bs = new byte[512];
        volume.Read(0, bs);
        if (!bs.AsSpan(82, 8).SequenceEqual("FAT32   "u8)) throw new RawOperationException("Rec.Error.NotFat", "not a FAT32 boot sector");
        _bps = BinaryPrimitives.ReadUInt16LittleEndian(bs.AsSpan(0x0B));
        int spc = bs[0x0D];
        int reserved = BinaryPrimitives.ReadUInt16LittleEndian(bs.AsSpan(0x0E));
        int fats = bs[0x10];
        long total = BinaryPrimitives.ReadUInt32LittleEndian(bs.AsSpan(0x20));
        long fatSize = BinaryPrimitives.ReadUInt32LittleEndian(bs.AsSpan(0x24));
        _rootCluster = BinaryPrimitives.ReadUInt32LittleEndian(bs.AsSpan(0x2C));
        if (_bps is not (512 or 1024 or 2048 or 4096) || spc == 0 || fats == 0 || fatSize == 0)
            throw new RawOperationException("Rec.Error.NotFat", "implausible FAT32 geometry");
        _clusterSize = (long)_bps * spc;
        _dataStart = (reserved + fats * fatSize) * _bps;
        _clusterCount = (total * _bps - _dataStart) / _clusterSize + 2;
        var fatBytes = new byte[fatSize * _bps];
        volume.Read((long)reserved * _bps, fatBytes);
        _fat = new uint[Math.Min(fatBytes.Length / 4, _clusterCount)];
        for (int i = 0; i < _fat.Length; i++) _fat[i] = BinaryPrimitives.ReadUInt32LittleEndian(fatBytes.AsSpan(i * 4)) & 0x0FFFFFFF;
        var label = Encoding.ASCII.GetString(bs, 0x47, 11).Trim();
        Label = label is "NO NAME" or "" ? null : label;
    }

    public string FileSystem => "FAT32";
    public string? Label { get; }

    private long ClusterOffset(uint c) => _dataStart + (c - 2) * _clusterSize;
    private bool Valid(uint c) => c >= 2 && c < _clusterCount;

    private List<uint> Chain(uint start)
    {
        var list = new List<uint>();
        var seen = new HashSet<uint>();
        for (uint c = start; Valid(c) && seen.Add(c) && list.Count < 1 << 22; c = c < _fat.Length ? _fat[c] : 0)
        {
            list.Add(c);
            if (c >= _fat.Length || _fat[c] >= 0x0FFFFFF8 || _fat[c] == 0) break;
        }
        return list;
    }

    private static DateTime? DosTime(ushort date, ushort time)
    {
        if (date == 0) return null;
        try
        {
            return new DateTime(1980 + (date >> 9), Math.Max(1, (date >> 5) & 15), Math.Max(1, date & 31),
                time >> 11, (time >> 5) & 63, Math.Min(59, (time & 31) * 2), DateTimeKind.Local).ToUniversalTime();
        }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    public IReadOnlyList<RecoverableFile> Scan(bool includeExisting, IProgress<RecoveryScanProgress>? progress, CancellationToken ct)
    {
        _starts.Clear();
        var result = new List<RecoverableFile>();
        var visited = new HashSet<uint>();
        long id = 0;
        var queue = new Queue<(uint Cluster, string Path, bool Deleted)>();
        queue.Enqueue((_rootCluster, "", false));
        while (queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (dirCluster, path, dirDeleted) = queue.Dequeue();
            if (!visited.Add(dirCluster)) continue;
            // a deleted directory has no chain: read its first cluster only
            var clusters = dirDeleted ? new List<uint> { dirCluster } : Chain(dirCluster);
            var lfn = new List<(int Order, string Part)>();
            foreach (var c in clusters)
            {
                var buf = new byte[_clusterSize];
                _vol.Read(ClusterOffset(c), buf);
                for (int e = 0; e < buf.Length; e += 32)
                {
                    byte first = buf[e];
                    if (first == 0x00) { lfn.Clear(); goto nextDir; }
                    byte attr = buf[e + 11];
                    if (attr == 0x0F)
                    {
                        var part = new StringBuilder();
                        foreach (var (o, n) in new[] { (1, 5), (14, 6), (28, 2) })
                            for (int k = 0; k < n; k++)
                            {
                                char ch = (char)BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(e + o + k * 2));
                                if (ch is '\0' or '￿') goto done;
                                part.Append(ch);
                            }
                        done:
                        lfn.Add((first == 0xE5 ? lfn.Count + 1 : first & 0x1F, part.ToString()));
                        continue;
                    }
                    bool deleted = first == 0xE5;
                    if ((attr & 0x08) != 0 || (!deleted && first == 0x2E)) { lfn.Clear(); continue; }
                    string shortName = ShortName(buf.AsSpan(e, 11));
                    string name = lfn.Count > 0
                        ? string.Concat((deleted ? lfn.AsEnumerable().Reverse().Select((x, i) => (Order: i + 1, x.Part)) : lfn).OrderBy(x => x.Order).Select(x => x.Part))
                        : shortName;
                    if (deleted && lfn.Count == 0) name = "_" + name[1..];
                    lfn.Clear();
                    if (name is "." or "..") continue;
                    uint start = (uint)(BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(e + 20)) << 16 | BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(e + 26)));
                    long size = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(e + 28));
                    var full = path.Length == 0 ? name : path + "\\" + name;
                    bool isDeleted = deleted || dirDeleted;
                    if ((attr & 0x10) != 0)
                    {
                        if (Valid(start)) queue.Enqueue((start, full, isDeleted));
                        continue;
                    }
                    if (!isDeleted && !includeExisting) continue;
                    var rec = !isDeleted ? Recoverability.Intact
                        : size == 0 ? Recoverability.Good
                        : !Valid(start) ? Recoverability.Unrecoverable
                        : ContiguousFree(start, size) ? Recoverability.Good : Recoverability.Poor;
                    _starts[id] = (start, isDeleted);
                    result.Add(new RecoverableFile
                    {
                        Id = id++, Folder = path, Name = name, Size = size, IsDeleted = isDeleted,
                        Modified = DosTime(BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(e + 24)), BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(e + 22))),
                        Created = DosTime(BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(e + 16)), BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(e + 14))),
                        Recoverability = rec,
                    });
                }
            }
            nextDir:
            progress?.Report(new RecoveryScanProgress(visited.Count, visited.Count + queue.Count, result.Count));
        }
        return result.OrderBy(f => f.Folder, StringComparer.OrdinalIgnoreCase).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private bool ContiguousFree(uint start, long size)
    {
        long n = (size + _clusterSize - 1) / _clusterSize;
        for (long i = 0; i < n; i++)
        {
            long c = start + i;
            if (c >= _fat.Length || _fat[c] != 0) return false;
        }
        return true;
    }

    private static string ShortName(ReadOnlySpan<byte> e)
    {
        var b = Encoding.Latin1.GetString(e[..8]).TrimEnd();
        var x = Encoding.Latin1.GetString(e.Slice(8, 3)).TrimEnd();
        if (b.Length > 0 && b[0] == 'å') b = "_" + b[1..];
        return x.Length == 0 ? b : b + "." + x;
    }

    public long Extract(RecoverableFile file, Stream output, CancellationToken ct)
    {
        if (!_starts.TryGetValue(file.Id, out var s)) throw new RawOperationException("Rec.Error.Unknown", "scan the volume first");
        long remaining = file.Size, written = 0;
        if (remaining == 0) return 0;
        var clusters = s.Deleted
            ? Enumerable.Range(0, (int)((remaining + _clusterSize - 1) / _clusterSize)).Select(i => (uint)(s.Cluster + i)).ToList()
            : Chain(s.Cluster);
        var buf = new byte[_clusterSize];
        foreach (var c in clusters)
        {
            ct.ThrowIfCancellationRequested();
            if (!Valid(c)) break;
            int len = (int)Math.Min(_clusterSize, remaining);
            _vol.Read(ClusterOffset(c), buf.AsSpan(0, len));
            output.Write(buf, 0, len);
            remaining -= len;
            written += len;
            if (remaining == 0) break;
        }
        if (remaining > 0) throw new RawOperationException("Rec.Error.Truncated", $"{remaining} bytes beyond the volume");
        return written;
    }
}

public static class VolumeLocator
{
    /// <summary>Disk that holds <paramref name="path"/> (longest matching mount point), or null for network/unknown locations.</summary>
    public static int? DiskOfPath(Models.StorageInventory inv, string path)
    {
        var full = Path.GetFullPath(path).TrimEnd('\\') + "\\";
        int? best = null;
        int bestLen = -1;
        foreach (var d in inv.Disks)
        foreach (var p in d.Partitions)
        foreach (var mp in p.Volume?.MountPoints ?? Array.Empty<string>())
        {
            var m = mp.TrimEnd('\\') + "\\";
            if (full.StartsWith(m, StringComparison.OrdinalIgnoreCase) && m.Length > bestLen) { best = d.Number; bestLen = m.Length; }
        }
        return best;
    }
}

/// <summary>LZNT1, the compression NTFS uses: 4 KiB chunks, each either stored or LZ77-coded with a position-dependent split of the copy tokens.</summary>
public static class Lznt1
{
    public static byte[] Decompress(ReadOnlySpan<byte> src, int maxOutput)
    {
        var dst = new byte[maxOutput];
        int sp = 0, dp = 0;
        while (sp + 2 <= src.Length && dp < maxOutput)
        {
            int header = BinaryPrimitives.ReadUInt16LittleEndian(src[sp..]);
            if (header == 0) break;
            int size = (header & 0x0FFF) + 1;
            sp += 2;
            int end = Math.Min(src.Length, sp + size);
            int chunkStart = dp;
            if ((header & 0x8000) == 0)
            {
                int n = Math.Min(end - sp, maxOutput - dp);
                src.Slice(sp, n).CopyTo(dst.AsSpan(dp));
                dp += n;
            }
            else
            {
                while (sp < end && dp < maxOutput)
                {
                    byte flags = src[sp++];
                    for (int bit = 0; bit < 8 && sp < end && dp < maxOutput; bit++)
                    {
                        if ((flags & (1 << bit)) == 0) { dst[dp++] = src[sp++]; continue; }
                        if (sp + 2 > end) break;
                        int token = BinaryPrimitives.ReadUInt16LittleEndian(src[sp..]);
                        sp += 2;
                        int shift = 12;
                        for (int i = dp - chunkStart - 1; i >= 0x10; i >>= 1) shift--;
                        int offset = (token >> shift) + 1;
                        int length = (token & ((1 << shift) - 1)) + 3;
                        if (offset > dp - chunkStart) throw new InvalidDataException("LZNT1 back-reference before the chunk start");
                        for (int k = 0; k < length && dp < maxOutput; k++, dp++) dst[dp] = dst[dp - offset];
                    }
                }
            }
            sp = end;
            // every chunk but the last stands for a full 4 KiB of output
            if (sp + 2 <= src.Length && BinaryPrimitives.ReadUInt16LittleEndian(src[sp..]) != 0)
                dp = Math.Min(maxOutput, chunkStart + 4096);
        }
        return dst;
    }
}
