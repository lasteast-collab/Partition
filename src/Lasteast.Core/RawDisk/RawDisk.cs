using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using Lasteast.Core.Models;
using Lasteast.Core.PartitionTables;

namespace Lasteast.Core.RawDisk;

/// <summary>Sector-addressed storage: a physical disk on Windows, or an image file in tests.</summary>
public interface IBlockDevice : IDisposable
{
    long Length { get; }
    int SectorSize { get; }
    void Read(long offset, Span<byte> buffer);
    void Write(long offset, ReadOnlySpan<byte> buffer);
    void Flush();
}

/// <summary>Image-file block device (tests, and disk images).</summary>
public sealed class FileBlockDevice : IBlockDevice
{
    private readonly FileStream _fs;
    public FileBlockDevice(string path, int sectorSize = 512, bool writable = true)
    {
        _fs = new FileStream(path, FileMode.Open, writable ? FileAccess.ReadWrite : FileAccess.Read, FileShare.ReadWrite);
        SectorSize = sectorSize;
    }

    public long Length => _fs.Length;
    public int SectorSize { get; }

    public void Read(long offset, Span<byte> buffer)
    {
        _fs.Position = offset;
        int total = 0;
        while (total < buffer.Length)
        {
            int n = _fs.Read(buffer[total..]);
            if (n <= 0) throw new IOException($"short read at {offset + total}");
            total += n;
        }
    }

    public void Write(long offset, ReadOnlySpan<byte> buffer)
    {
        _fs.Position = offset;
        _fs.Write(buffer);
    }

    public void Flush() => _fs.Flush(true);
    public void Dispose() => _fs.Dispose();
}

public sealed class RawOperationException(string key, string detail) : Exception(detail)
{
    public string Key { get; } = key;
}

public sealed record RawProgress(string Phase, long Done, long Total);

// =============================================================================================
//  Partition-style conversion
// =============================================================================================

/// <summary>
/// Non-destructive MBR ↔ GPT conversion of the partition table only; partition data is never moved.
/// Order of writes is chosen so an interruption leaves the disk readable as its old style:
///  MBR→GPT: backup GPT (in previously unused space at the end) first, then the primary GPT + protective MBR.
///  GPT→MBR: the new MBR first (no longer protective, so tools read MBR), then the GPT structures are cleared.
/// </summary>
public static class StyleConverter
{
    public static string BasicName(Guid type) => type == PartitionTypes.GptEfiSystem ? "EFI system partition"
        : type == PartitionTypes.GptWindowsRecovery ? "Recovery" : "Basic data partition";

    /// <summary>Checks and builds the conversion without writing. Throws <see cref="RawOperationException"/> when not possible.</summary>
    public static (byte[] Head, byte[] Tail, long TailOffset) PlanMbrToGpt(IBlockDevice dev, Func<Guid>? newGuid = null)
    {
        newGuid ??= Guid.NewGuid;
        int ss = dev.SectorSize;
        var s0 = new byte[ss];
        dev.Read(0, s0);
        var mbr = PartitionTableCodec.ReadMbr(s0);
        if (mbr.IsProtective) throw new RawOperationException("Conv.Error.AlreadyGpt", "disk is already GPT");
        if (mbr.HasExtended) throw new RawOperationException("Conv.Error.Extended", "extended/logical partitions are not supported");
        var total = (ulong)(dev.Length / ss);
        var (first, last) = PartitionTableCodec.DefaultUsable(total, ss);
        var entries = new List<GptEntry>();
        foreach (var e in mbr.Entries.Where(e => !e.IsEmpty))
        {
            var type = TypeMap.ToGpt(e.Type) ?? throw new RawOperationException("Conv.Error.Type", $"MBR type 0x{e.Type:X2} has no GPT equivalent");
            if (e.FirstLba < first) throw new RawOperationException("Conv.Error.NoRoomStart", $"partition at LBA {e.FirstLba} overlaps the GPT header area");
            if (e.LastLba > last) throw new RawOperationException("Conv.Error.NoRoomEnd", $"partition ending at LBA {e.LastLba} overlaps the backup GPT area");
            entries.Add(new GptEntry(type, newGuid(), e.FirstLba, e.LastLba, 0, BasicName(type)));
        }
        var gpt = new GptDisk(newGuid(), first, last, PartitionTableCodec.DefaultEntryCount, PartitionTableCodec.DefaultEntrySize, entries);
        var (head, tail, tailLba) = PartitionTableCodec.BuildGpt(gpt, total, ss, s0);
        return (head, tail, (long)tailLba * ss);
    }

    public static void ConvertMbrToGpt(IBlockDevice dev, Func<Guid>? newGuid = null)
    {
        var (head, tail, tailOffset) = PlanMbrToGpt(dev, newGuid);
        dev.Write(tailOffset, tail);
        dev.Flush();
        dev.Write(0, head);
        dev.Flush();
    }

    /// <param name="fileSystems">File system per partition start LBA (picks 0x07 vs 0x0C); may be empty.</param>
    public static byte[] PlanGptToMbr(IBlockDevice dev, IReadOnlyDictionary<ulong, string?> fileSystems, uint? signature = null)
    {
        int ss = dev.SectorSize;
        var head = new byte[(2 + PartitionTableCodec.EntryArraySectors(ss)) * ss];
        dev.Read(0, head);
        var gpt = PartitionTableCodec.ReadGpt(head, ss);
        var kept = gpt.Entries.Where(e => e.Type != PartitionTypes.GptMicrosoftReserved).OrderBy(e => e.FirstLba).ToList();
        if (kept.Count > 4) throw new RawOperationException("Conv.Error.TooMany", "MBR holds at most 4 partitions");
        var entries = new List<MbrEntry>();
        foreach (var e in kept)
        {
            if (e.LastLba >= uint.MaxValue || e.Sectors > uint.MaxValue)
                throw new RawOperationException("Conv.Error.Beyond2TB", "a partition lies beyond 2 TB");
            fileSystems.TryGetValue(e.FirstLba, out var fs);
            var t = TypeMap.ToMbr(e.Type, fs) ?? throw new RawOperationException("Conv.Error.Type", $"GPT type {e.Type} has no MBR equivalent");
            entries.Add(new MbrEntry(false, t, (uint)e.FirstLba, (uint)e.Sectors));
        }
        var s0 = head.AsSpan(0, ss).ToArray();
        var sig = signature ?? (uint)RandomNumberGenerator.GetInt32(1, int.MaxValue);
        PartitionTableCodec.WriteMbr(s0, new MbrDisk(sig, Array.Empty<byte>(), entries));
        return s0;
    }

    public static void ConvertGptToMbr(IBlockDevice dev, IReadOnlyDictionary<ulong, string?> fileSystems, uint? signature = null)
    {
        int ss = dev.SectorSize;
        var s0 = PlanGptToMbr(dev, fileSystems, signature);
        dev.Write(0, s0);
        dev.Flush();
        int arraySectors = PartitionTableCodec.EntryArraySectors(ss);
        var zeros = new byte[(1 + arraySectors) * ss];
        dev.Write(ss, zeros);                                             // primary header + entries
        dev.Write(dev.Length - zeros.Length, zeros);                      // backup entries + header
        dev.Flush();
    }
}

// =============================================================================================
//  Cloning
// =============================================================================================

public enum CloneMode
{
    /// <summary>Every sector of every partition (and the gaps before the first partition).</summary>
    SectorBySector,
    /// <summary>Only allocated clusters of NTFS volumes (other partitions in full).</summary>
    UsedSpace,
}

public sealed record ByteRange(long Offset, long Length)
{
    public long End => Offset + Length;
}

/// <summary>Byte ranges of one partition that hold data (relative to the disk start). Null = copy everything.</summary>
public delegate IReadOnlyList<ByteRange>? UsedRangeProvider(PartitionInfo partition);

public sealed record ClonePlan(
    IReadOnlyList<ByteRange> Regions,
    long BytesToCopy,
    bool RewriteGpt,
    bool NewIdentifiers,
    PartitionStyle Style,
    /// <summary>Start offsets of the partitions being copied; null = all. Table entries for the others are dropped.</summary>
    IReadOnlyCollection<ulong>? Partitions = null);

public static class ClonePlanner
{
    public const long Chunk = 4L * 1024 * 1024;
    private const long MiB = 1024 * 1024;

    /// <summary>Validates and builds the list of regions to copy. Destination offsets equal source offsets.</summary>
    public static ClonePlan Plan(DiskInfo source, long destLength, int destSectorSize, CloneMode mode, bool newIdentifiers,
        UsedRangeProvider? used = null, IReadOnlyCollection<ulong>? onlyPartitions = null)
    {
        if (source.BytesPerLogicalSector != destSectorSize)
            throw new RawOperationException("Clone.Error.SectorSize", $"sector sizes differ ({source.BytesPerLogicalSector} vs {destSectorSize})");
        if (source.Style is not (PartitionStyle.Gpt or PartitionStyle.Mbr))
            throw new RawOperationException("Clone.Error.Style", "source has no MBR/GPT partition table");
        if (source.IsDynamic) throw new RawOperationException("Clone.Error.Dynamic", "dynamic disks cannot be cloned");

        var parts = source.Partitions.Where(p => p.Role != PartitionRole.MbrExtended && p.SizeBytes > 0)
            .Where(p => onlyPartitions is null || onlyPartitions.Contains(p.OffsetBytes)).ToList();
        if (source.Partitions.Any(p => p.Role is PartitionRole.MbrExtended or PartitionRole.MbrLogical))
            throw new RawOperationException("Clone.Error.Extended", "MBR extended/logical partitions are not supported");

        long ss = destSectorSize;
        long usableEnd = source.Style == PartitionStyle.Gpt
            ? destLength - (1 + PartitionTableCodec.EntryArraySectors(destSectorSize)) * ss
            : Math.Min(destLength, (long)uint.MaxValue * ss);
        var lastEnd = parts.Count == 0 ? 0 : parts.Max(p => (long)p.EndBytes);
        if (lastEnd > usableEnd) throw new RawOperationException("Clone.Error.TooSmall", $"partitions end at {lastEnd}, destination usable end is {usableEnd}");

        var regions = new List<ByteRange>();
        var firstStart = parts.Count == 0 ? MiB : parts.Min(p => (long)p.OffsetBytes);
        regions.Add(new ByteRange(0, Math.Min(firstStart, MiB * 16))); // MBR/boot area and gap before the first partition
        foreach (var p in parts)
        {
            var ranges = mode == CloneMode.UsedSpace ? used?.Invoke(p) : null;
            if (ranges is null)
            {
                regions.Add(new ByteRange((long)p.OffsetBytes, (long)p.SizeBytes));
                continue;
            }
            // always include the first and last MiB of the volume (boot sector, backup boot sector)
            regions.Add(new ByteRange((long)p.OffsetBytes, Math.Min(MiB, (long)p.SizeBytes)));
            regions.Add(new ByteRange(Math.Max((long)p.OffsetBytes, (long)p.EndBytes - MiB), Math.Min(MiB, (long)p.SizeBytes)));
            foreach (var r in ranges)
            {
                var start = Math.Max(r.Offset, (long)p.OffsetBytes);
                var end = Math.Min(r.End, (long)p.EndBytes);
                if (end > start) regions.Add(new ByteRange(start, end - start));
            }
        }
        var merged = Merge(regions, ss);
        return new ClonePlan(merged, merged.Sum(r => r.Length), source.Style == PartitionStyle.Gpt, newIdentifiers, source.Style,
            onlyPartitions is null ? null : parts.Select(p => p.OffsetBytes).ToHashSet());
    }

    /// <summary>Sorts, sector-aligns and merges overlapping/adjacent ranges.</summary>
    public static List<ByteRange> Merge(IEnumerable<ByteRange> ranges, long sector)
    {
        var aligned = ranges.Where(r => r.Length > 0)
            .Select(r => { var s = r.Offset / sector * sector; var e = (r.End + sector - 1) / sector * sector; return new ByteRange(s, e - s); })
            .OrderBy(r => r.Offset).ToList();
        var result = new List<ByteRange>();
        foreach (var r in aligned)
        {
            if (result.Count > 0 && r.Offset <= result[^1].End)
            {
                var last = result[^1];
                result[^1] = new ByteRange(last.Offset, Math.Max(last.End, r.End) - last.Offset);
            }
            else result.Add(r);
        }
        return result;
    }
}

public sealed record CloneResult(long BytesCopied, bool Verified, TimeSpan Duration);

/// <summary>Reads source bytes (e.g. from a volume snapshot instead of the live disk).</summary>
public delegate void RangeReader(long offset, Span<byte> buffer);

public static class Cloner
{
    /// <summary>
    /// Copies <paramref name="plan"/>'s regions from <paramref name="source"/> to <paramref name="dest"/>, then rewrites the
    /// destination's GPT for its own size (and new identifiers when requested). With <paramref name="verify"/>, every copied
    /// chunk is read back from the destination and compared by SHA-256.
    /// <paramref name="sourceReader"/> lets the caller read some ranges from a snapshot instead of the live disk.
    /// </summary>
    public static CloneResult Run(IBlockDevice source, IBlockDevice dest, ClonePlan plan, bool verify,
        IProgress<RawProgress>? progress, CancellationToken ct, RangeReader? sourceReader = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        RangeReader read = sourceReader ?? ((o, b) => source.Read(o, b));
        var hashes = verify ? new List<(long Offset, int Length, byte[] Hash)>() : null;
        var buf = new byte[ClonePlanner.Chunk];
        long done = 0;
        foreach (var r in plan.Regions)
        {
            for (long pos = 0; pos < r.Length; pos += ClonePlanner.Chunk)
            {
                ct.ThrowIfCancellationRequested();
                int n = (int)Math.Min(ClonePlanner.Chunk, r.Length - pos);
                var span = buf.AsSpan(0, n);
                read(r.Offset + pos, span);
                dest.Write(r.Offset + pos, span);
                hashes?.Add((r.Offset + pos, n, SHA256.HashData(span)));
                done += n;
                progress?.Report(new RawProgress("Copy", done, plan.BytesToCopy));
            }
        }
        dest.Flush();

        FinishTable(source, dest, plan);

        if (hashes is not null)
        {
            long checkedBytes = 0;
            foreach (var (off, len, expectedHash) in hashes)
            {
                var hash = expectedHash;
                ct.ThrowIfCancellationRequested();
                var span = buf.AsSpan(0, len);
                dest.Read(off, span);
                // the partition-table area was rewritten on purpose: compare it against the new tables instead
                if (off == 0 && (plan.RewriteGpt || plan.Partitions is not null)) { checkedBytes += len; continue; }
                if (off == 0 && plan.NewIdentifiers && plan.Style == PartitionStyle.Mbr) span[440..444].Clear();
                var actual = SHA256.HashData(span);
                if (off == 0 && plan.NewIdentifiers && plan.Style == PartitionStyle.Mbr)
                {
                    var src = new byte[len];
                    read(off, src);
                    src.AsSpan(440, 4).Clear();
                    hash = SHA256.HashData(src);
                }
                if (!actual.AsSpan().SequenceEqual(hash))
                    throw new RawOperationException("Clone.Error.Verify", $"verification failed at offset {off}");
                checkedBytes += len;
                progress?.Report(new RawProgress("Verify", checkedBytes, plan.BytesToCopy));
            }
        }
        return new CloneResult(done, verify, sw.Elapsed);
    }

    /// <summary>Writes destination partition-table structures appropriate to its size.</summary>
    private static void FinishTable(IBlockDevice source, IBlockDevice dest, ClonePlan plan)
    {
        int ss = dest.SectorSize;
        if (plan.Style == PartitionStyle.Gpt)
        {
            var head = new byte[(2 + PartitionTableCodec.EntryArraySectors(ss)) * ss];
            source.Read(0, head);
            var gpt = PartitionTableCodec.ReadGpt(head, ss);
            var total = (ulong)(dest.Length / ss);
            var (first, last) = PartitionTableCodec.DefaultUsable(total, ss);
            var kept = gpt.Entries.Where(e => plan.Partitions is null || plan.Partitions.Contains(e.FirstLba * (ulong)ss));
            var entries = plan.NewIdentifiers ? kept.Select(e => e with { Id = Guid.NewGuid() }).ToList() : kept.ToList();
            var g = gpt with { DiskGuid = plan.NewIdentifiers ? Guid.NewGuid() : gpt.DiskGuid, FirstUsableLba = first, LastUsableLba = last, Entries = entries };
            var (newHead, tail, tailLba) = PartitionTableCodec.BuildGpt(g, total, ss, head.AsSpan(0, ss));
            dest.Write(0, newHead);
            dest.Write((long)tailLba * ss, tail);
        }
        else if (plan.NewIdentifiers || plan.Partitions is not null)
        {
            var s0 = new byte[ss];
            dest.Read(0, s0);
            if (plan.Partitions is not null)
            {
                var mbr = PartitionTableCodec.ReadMbr(s0);
                var entries = mbr.Entries.Select(e => !e.IsEmpty && plan.Partitions.Contains((ulong)e.FirstLba * (ulong)ss) ? e : new MbrEntry(false, 0, 0, 0)).ToList();
                PartitionTableCodec.WriteMbr(s0, mbr with { Entries = entries });
            }
            if (plan.NewIdentifiers)
                BinaryPrimitives.WriteUInt32LittleEndian(s0.AsSpan(440), (uint)RandomNumberGenerator.GetInt32(1, int.MaxValue));
            dest.Write(0, s0);
        }
        dest.Flush();
    }
}

// =============================================================================================
//  Moving a partition
// =============================================================================================

/// <summary>Persistent progress of a partition move, so an interrupted move can be resumed.</summary>
public sealed record MoveJournal
{
    public required Guid Id { get; init; }
    public required string DiskIdentity { get; init; }
    public required long SourceOffset { get; init; }
    public required long DestOffset { get; init; }
    public required long Length { get; init; }
    public required long ChunkSize { get; init; }
    public required long ChunksDone { get; set; }
    public bool DataCopied { get; set; }
    public bool TableUpdated { get; set; }
    public DateTimeOffset Started { get; init; } = DateTimeOffset.Now;

    public long ChunkCount => (Length + ChunkSize - 1) / ChunkSize;

    public void Save(string path)
    {
        var tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            JsonSerializer.Serialize(fs, this);
        File.Move(tmp, path, true);
    }

    public static MoveJournal? Load(string path) => File.Exists(path) ? JsonSerializer.Deserialize<MoveJournal>(File.ReadAllText(path)) : null;
}

/// <summary>
/// Moves a partition's data to a new start offset on the same disk, then updates the partition table and the
/// file system's "hidden sectors" field. Chunks never exceed the shift distance, so the source and destination of a
/// chunk never overlap and re-copying the chunk that was in flight during a crash is always safe (idempotent).
/// The journal is written (write-through) after every chunk.
/// </summary>
public static class PartitionMover
{
    public static MoveJournal NewJournal(string diskIdentity, long from, long to, long length, int sectorSize)
    {
        if (from == to) throw new RawOperationException("Move.Error.NoChange", "same offset");
        long shift = Math.Abs(to - from);
        long chunk = Math.Min(ClonePlanner.Chunk, shift) / sectorSize * sectorSize;
        if (chunk <= 0) throw new RawOperationException("Move.Error.Alignment", "shift smaller than a sector");
        return new MoveJournal { Id = Guid.NewGuid(), DiskIdentity = diskIdentity, SourceOffset = from, DestOffset = to, Length = length, ChunkSize = chunk, ChunksDone = 0 };
    }

    /// <summary>Copies the data (resuming from <see cref="MoveJournal.ChunksDone"/>), then updates tables.</summary>
    public static void Run(IBlockDevice dev, MoveJournal j, string journalPath, IProgress<RawProgress>? progress, CancellationToken ct)
    {
        bool left = j.DestOffset < j.SourceOffset;
        var buf = new byte[j.ChunkSize];
        long count = j.ChunkCount;
        j.Save(journalPath);
        for (long k = j.ChunksDone; k < count; k++)
        {
            ct.ThrowIfCancellationRequested(); // between chunks only; the journal allows resuming
            // moving left: copy from the start; moving right: copy from the end
            long index = left ? k : count - 1 - k;
            long rel = index * j.ChunkSize;
            int n = (int)Math.Min(j.ChunkSize, j.Length - rel);
            var span = buf.AsSpan(0, n);
            dev.Read(j.SourceOffset + rel, span);
            dev.Write(j.DestOffset + rel, span);
            dev.Flush();
            j.ChunksDone = k + 1;
            j.Save(journalPath);
            progress?.Report(new RawProgress("Move", j.ChunksDone * j.ChunkSize, j.Length));
        }
        j.DataCopied = true;
        j.Save(journalPath);

        UpdateTables(dev, j.SourceOffset, j.DestOffset);
        PatchHiddenSectors(dev, j.DestOffset, j.Length);
        dev.Flush();
        j.TableUpdated = true;
        j.Save(journalPath);
    }

    /// <summary>Points the partition entry that starts at <paramref name="from"/> to <paramref name="to"/> (GPT both copies, or MBR).</summary>
    public static void UpdateTables(IBlockDevice dev, long from, long to)
    {
        int ss = dev.SectorSize;
        ulong fromLba = (ulong)(from / ss), toLba = (ulong)(to / ss);
        var s0 = new byte[ss];
        dev.Read(0, s0);
        var mbr = PartitionTableCodec.ReadMbr(s0);
        if (mbr.IsProtective)
        {
            var head = new byte[(2 + PartitionTableCodec.EntryArraySectors(ss)) * ss];
            dev.Read(0, head);
            var gpt = PartitionTableCodec.ReadGpt(head, ss);
            var e = gpt.Entries.FirstOrDefault(x => x.FirstLba == fromLba)
                    ?? (gpt.Entries.FirstOrDefault(x => x.FirstLba == toLba) is not null ? null
                        : throw new RawOperationException("Move.Error.Entry", "partition entry not found"));
            if (e is null) return; // already updated (resume after table write)
            var moved = e with { FirstLba = toLba, LastLba = toLba + e.Sectors - 1 };
            var updated = gpt with { Entries = gpt.Entries.Select(x => x == e ? moved : x).ToList() };
            var total = (ulong)(dev.Length / ss);
            var (newHead, tail, tailLba) = PartitionTableCodec.BuildGpt(updated, total, ss, s0);
            dev.Write((long)tailLba * ss, tail);
            dev.Write(0, newHead);
        }
        else
        {
            var e = mbr.Entries.FirstOrDefault(x => !x.IsEmpty && x.FirstLba == fromLba);
            if (e is null)
            {
                if (mbr.Entries.Any(x => !x.IsEmpty && x.FirstLba == toLba)) return;
                throw new RawOperationException("Move.Error.Entry", "partition entry not found");
            }
            var updated = mbr with { Entries = mbr.Entries.Select(x => x == e ? e with { FirstLba = (uint)toLba } : x).ToList() };
            PartitionTableCodec.WriteMbr(s0, updated);
            dev.Write(0, s0);
        }
    }

    /// <summary>NTFS and FAT boot sectors record the partition's start LBA ("hidden sectors", offset 0x1C).</summary>
    /// <summary>
    /// exFAT records its partition offset (in sectors) in the main and backup boot regions, each protected by a
    /// checksum in its 12th sector: both fields and both checksums are rewritten.
    /// </summary>
    public static void PatchExfatOffset(IBlockDevice dev, long partitionOffset)
    {
        int ss = dev.SectorSize;
        foreach (long region in new[] { 0L, 12L })
        {
            var r = new byte[12 * ss];
            dev.Read(partitionOffset + region * ss, r);
            if (!r.AsSpan(3, 8).SequenceEqual("EXFAT   "u8)) continue;
            BinaryPrimitives.WriteUInt64LittleEndian(r.AsSpan(0x40), (ulong)(partitionOffset / ss));
            uint chk = 0;
            for (int i = 0; i < 11 * ss; i++)
            {
                if (i is 106 or 107 or 112) continue;
                chk = ((chk & 1) != 0 ? 0x80000000u : 0) + (chk >> 1) + r[i];
            }
            for (int i = 11 * ss; i < 12 * ss; i += 4) BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(i), chk);
            dev.Write(partitionOffset + region * ss, r);
        }
    }

    public static void PatchHiddenSectors(IBlockDevice dev, long partitionOffset, long length)
    {
        int ss = dev.SectorSize;
        uint lba = (uint)Math.Min(partitionOffset / ss, uint.MaxValue);
        var bs = new byte[ss];
        dev.Read(partitionOffset, bs);
        bool ntfs = bs.AsSpan(3, 8).SequenceEqual("NTFS    "u8);
        bool fat = bs.AsSpan(82, 8).SequenceEqual("FAT32   "u8) || bs.AsSpan(54, 5).SequenceEqual("FAT16"u8) || bs.AsSpan(54, 5).SequenceEqual("FAT12"u8);
        if (bs.AsSpan(3, 8).SequenceEqual("EXFAT   "u8)) { PatchExfatOffset(dev, partitionOffset); return; }
        if (!ntfs && !fat) return;
        BinaryPrimitives.WriteUInt32LittleEndian(bs.AsSpan(0x1C), lba);
        dev.Write(partitionOffset, bs);
        if (ntfs)
        {
            // backup boot sector: last sector of the volume (total sectors at 0x28 + 1)
            var totalSectors = (long)BinaryPrimitives.ReadUInt64LittleEndian(bs.AsSpan(0x28));
            long backup = partitionOffset + totalSectors * ss;
            if (backup + ss <= partitionOffset + length)
            {
                var b = new byte[ss];
                dev.Read(backup, b);
                if (b.AsSpan(3, 8).SequenceEqual("NTFS    "u8))
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0x1C), lba);
                    dev.Write(backup, b);
                }
            }
        }
        else if (bs.AsSpan(82, 8).SequenceEqual("FAT32   "u8))
        {
            // FAT32 backup boot sector at the sector given in 0x32 (usually 6)
            var backupSector = BinaryPrimitives.ReadUInt16LittleEndian(bs.AsSpan(0x32));
            if (backupSector is > 0 and < 32)
            {
                var b = new byte[ss];
                dev.Read(partitionOffset + backupSector * ss, b);
                if (b.AsSpan(82, 8).SequenceEqual("FAT32   "u8))
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0x1C), lba);
                    dev.Write(partitionOffset + backupSector * ss, b);
                }
            }
        }
    }
}

public sealed record PartitionCopyReport(long BytesCopied, bool Verified);

/// <summary>Copies a partition's bytes to another position (same or another device), only the given ranges when known.</summary>
public static class PartitionCopier
{
    /// <param name="used">Volume-relative byte ranges holding data (NTFS used clusters); null copies everything.</param>
    public static PartitionCopyReport Copy(RangeReader read, IBlockDevice dest, long srcOffset, long destOffset, long length,
        IReadOnlyList<ByteRange>? used, bool verify, IProgress<RawProgress>? progress, CancellationToken ct)
    {
        int ss = Math.Max(512, dest.SectorSize);
        const int Chunk = 4 * 1024 * 1024;
        var ranges = used is null
            ? new List<ByteRange> { new(0, length) }
            // always the first and last MiB too: boot sectors and the NTFS backup boot sector
            : ClonePlanner.Merge(used.Append(new ByteRange(0, Math.Min(length, 1 << 20))).Append(new ByteRange(Math.Max(0, length - (1 << 20)), Math.Min(length, 1 << 20))), ss)
                .Where(r => r.Offset < length).Select(r => new ByteRange(r.Offset, Math.Min(r.Length, length - r.Offset))).ToList();
        long total = ranges.Sum(r => r.Length), done = 0;
        var buf = new byte[Chunk];
        var back = verify ? new byte[Chunk] : null;
        foreach (var r in ranges)
        {
            for (long o = 0; o < r.Length;)
            {
                ct.ThrowIfCancellationRequested();
                int n = (int)Math.Min(Chunk, r.Length - o);
                read(srcOffset + r.Offset + o, buf.AsSpan(0, n));
                dest.Write(destOffset + r.Offset + o, buf.AsSpan(0, n));
                o += n;
                done += n;
                progress?.Report(new RawProgress("Copy", done, total));
            }
        }
        dest.Flush();
        if (verify)
        {
            long vdone = 0;
            foreach (var r in ranges)
                for (long o = 0; o < r.Length;)
                {
                    ct.ThrowIfCancellationRequested();
                    int n = (int)Math.Min(Chunk, r.Length - o);
                    read(srcOffset + r.Offset + o, buf.AsSpan(0, n));
                    dest.Read(destOffset + r.Offset + o, back.AsSpan(0, n));
                    if (!buf.AsSpan(0, n).SequenceEqual(back.AsSpan(0, n))) throw new RawOperationException("CopyP.Error.Verify", $"copy differs at {r.Offset + o}");
                    o += n;
                    vdone += n;
                    progress?.Report(new RawProgress("Verify", vdone, total));
                }
        }
        return new PartitionCopyReport(done, verify);
    }
}
