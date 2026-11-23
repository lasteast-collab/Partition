using System.Buffers.Binary;
using Lasteast.Core.Models;
using Lasteast.Core.PartitionTables;
using Lasteast.Core.RawDisk;

namespace Lasteast.Core.Recovery;

public enum FoundConfidence { Low, Medium, High }

public sealed record FoundPartition
{
    public required ulong OffsetBytes { get; init; }
    public required ulong SizeBytes { get; init; }
    public required string FileSystem { get; init; }
    public string? Label { get; init; }
    public FoundConfidence Confidence { get; init; }
    /// <summary>Found through the NTFS backup boot sector; the primary boot sector at the start is damaged.</summary>
    public bool FromBackupBootSector { get; init; }
    /// <summary>Overlaps another candidate; only one of them can be restored.</summary>
    public bool Conflicts { get; init; }
    public ulong EndBytes => OffsetBytes + SizeBytes;
}

public enum LostScanMode
{
    /// <summary>1 MiB boundaries and legacy cylinder starts in unallocated space.</summary>
    Quick,
    /// <summary>Every sector of the whole disk (slow).</summary>
    Deep,
}

/// <summary>
/// Looks for file-system boot sectors (NTFS, FAT32, exFAT, plus NTFS backup boot sectors) where the partition table
/// has no partition. Only reads. Results are candidates: the user reviews them, and restoring one only writes a
/// partition-table entry (see <see cref="Operations.RecoverPartitionOp"/>).
/// </summary>
public static class LostPartitionScanner
{
    private const long MiB = 1024 * 1024;

    public static IReadOnlyList<FoundPartition> Scan(IBlockDevice disk, IReadOnlyList<(ulong Offset, ulong Size)> existing, LostScanMode mode,
        IProgress<RecoveryScanProgress>? progress, CancellationToken ct)
    {
        int ss = disk.SectorSize;
        var found = new Dictionary<ulong, FoundPartition>();
        bool Inside(long pos) => existing.Any(e => (ulong)pos >= e.Offset && (ulong)pos < e.Offset + e.Size);

        var sector = new byte[Math.Max(ss, 512)];
        void Check(long pos, ReadOnlySpan<byte> s)
        {
            if (s[510] != 0x55 || s[511] != 0xAA) return;
            var c = Recognize(s, pos, disk.Length, ss);
            if (c is null) return;
            if (c.FromBackupBootSector)
            {
                if (Inside((long)c.OffsetBytes) || found.ContainsKey(c.OffsetBytes)) return;
                // only trust the backup when the start really is damaged
                disk.Read((long)c.OffsetBytes, sector);
                if (Recognize(sector, (long)c.OffsetBytes, disk.Length, ss) is { FromBackupBootSector: false }) return;
            }
            else if (Inside(pos)) return;
            if (found.TryGetValue(c.OffsetBytes, out var prev) && prev.Confidence >= c.Confidence) return;
            if (c.FileSystem == "NTFS" && !c.FromBackupBootSector)
            {
                // the backup boot sector sits in the last sector of the volume
                long backup = (long)c.OffsetBytes + (long)c.SizeBytes - ss;
                if (backup + 512 <= disk.Length)
                {
                    disk.Read(backup, sector);
                    c = c with { Confidence = sector.AsSpan(0, 512).SequenceEqual(s[..512]) ? FoundConfidence.High : FoundConfidence.Medium };
                }
            }
            else if (c.FileSystem == "exFAT")
            {
                // the backup boot region starts at sector 12: an identical sector 12 earlier means this is the backup
                long before = (long)c.OffsetBytes - 12L * ss;
                if (before >= 0)
                {
                    disk.Read(before, sector);
                    if (sector.AsSpan(0, 120).SequenceEqual(s[..120])) return;
                }
                disk.Read((long)c.OffsetBytes + 12L * ss, sector);
                if (sector.AsSpan(0, 120).SequenceEqual(s[..120])) c = c with { Confidence = FoundConfidence.High };
            }
            else if (c.FileSystem == "FAT32")
            {
                disk.Read((long)c.OffsetBytes + 6L * ss, sector); // backup boot sector at sector 6
                c = c with { Confidence = sector.AsSpan(0, 90).SequenceEqual(s[..90]) ? FoundConfidence.High : FoundConfidence.Medium };
            }
            found[c.OffsetBytes] = c;
        }

        if (mode == LostScanMode.Quick)
        {
            var positions = new SortedSet<long>();
            for (long p = MiB; p < disk.Length; p += MiB) positions.Add(p);
            for (long cyl = 63; cyl * ss < disk.Length; cyl += 255 * 63) positions.Add(cyl * ss); // legacy CHS alignment
            positions.Add(63L * ss);
            // partitions usually start shortly after the start of a free region (64 KiB / 4 KiB alignments, e.g. diskpart on small disks)
            var gapStarts = new[] { 0L }.Concat(existing.Select(e => (long)(e.Offset + e.Size))).Distinct();
            foreach (var g in gapStarts)
                for (long p = (g + 4095) / 4096 * 4096; p < Math.Min(disk.Length, g + 16 * MiB); p += 4096)
                    if (p > 0) positions.Add(p);
            long done = 0;
            foreach (var p in positions)
            {
                if ((done++ & 1023) == 0) { ct.ThrowIfCancellationRequested(); progress?.Report(new RecoveryScanProgress(done, positions.Count, found.Count)); }
                if (Inside(p) || p + 512 > disk.Length) continue;
                disk.Read(p, sector);
                Check(p, sector);
            }
            // NTFS backup boot sectors just before the next boundary (volume end) of damaged volumes
            foreach (var p in positions)
            {
                long b = p - ss;
                if (b <= 0 || b + 512 > disk.Length) continue;
                disk.Read(b, sector);
                if (sector.AsSpan(3, 8).SequenceEqual("NTFS    "u8)) Check(b, sector);
            }
        }
        else
        {
            var chunk = new byte[4 * MiB];
            for (long p = 0; p < disk.Length; p += chunk.Length)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(new RecoveryScanProgress(p, disk.Length, found.Count));
                int len = (int)Math.Min(chunk.Length, disk.Length - p);
                disk.Read(p, chunk.AsSpan(0, len));
                for (int o = 0; o + 512 <= len; o += ss)
                    if (p + o > 0) Check(p + o, chunk.AsSpan(o, 512));
            }
        }
        progress?.Report(new RecoveryScanProgress(1, 1, found.Count));

        var list = found.Values.Where(f => !existing.Any(e => f.OffsetBytes < e.Offset + e.Size && e.Offset < f.EndBytes))
            .OrderBy(f => f.OffsetBytes).ToList();
        // boot-sector look-alikes inside a confirmed volume (file contents, nested images) are not partitions of this disk
        list = list.Where(f => !list.Any(o => !ReferenceEquals(o, f) && o.Confidence == FoundConfidence.High
                                              && f.OffsetBytes > o.OffsetBytes && f.OffsetBytes < o.EndBytes)).ToList();
        return list.Select(f => f with { Conflicts = list.Any(o => !ReferenceEquals(o, f) && f.OffsetBytes < o.EndBytes && o.OffsetBytes < f.EndBytes) }).ToList();
    }

    /// <summary>Recognizes a boot sector at <paramref name="pos"/>. Returns null for anything implausible.</summary>
    public static FoundPartition? Recognize(ReadOnlySpan<byte> s, long pos, long diskLength, int ss)
    {
        if (s[510] != 0x55 || s[511] != 0xAA) return null;
        int bps = BinaryPrimitives.ReadUInt16LittleEndian(s[0x0B..]);
        if (s[3..11].SequenceEqual("NTFS    "u8))
        {
            int spc = s[0x0D] > 0x80 ? 1 << (256 - s[0x0D]) : s[0x0D];
            long total = BinaryPrimitives.ReadInt64LittleEndian(s[0x28..]);
            long mft = BinaryPrimitives.ReadInt64LittleEndian(s[0x30..]);
            if (bps != ss || spc <= 0 || total <= 0 || mft <= 0 || mft * spc >= total) return null;
            long size = (total + 1) * bps; // + the backup boot sector
            long hidden = BinaryPrimitives.ReadUInt32LittleEndian(s[0x1C..]) * (long)ss;
            long backupStart = pos - total * bps;
            // "hidden sectors" records where the volume started: tells a primary boot sector from its backup copy
            bool isBackup = hidden != pos && hidden == backupStart && backupStart > 0;
            if (!isBackup && pos + size <= diskLength)
                return new FoundPartition { OffsetBytes = (ulong)pos, SizeBytes = (ulong)size, FileSystem = "NTFS", Confidence = FoundConfidence.Medium };
            // a backup boot sector: the volume started total sectors earlier
            long start = backupStart;
            return start > 0 ? new FoundPartition { OffsetBytes = (ulong)start, SizeBytes = (ulong)size, FileSystem = "NTFS", Confidence = FoundConfidence.Medium, FromBackupBootSector = true } : null;
        }
        if (s[82..90].SequenceEqual("FAT32   "u8))
        {
            int spc = s[0x0D];
            int reserved = BinaryPrimitives.ReadUInt16LittleEndian(s[0x0E..]);
            long total = BinaryPrimitives.ReadUInt32LittleEndian(s[0x20..]);
            long fatSize = BinaryPrimitives.ReadUInt32LittleEndian(s[0x24..]);
            if (bps != ss || spc == 0 || (spc & (spc - 1)) != 0 || reserved == 0 || s[0x10] is 0 or > 2 || total == 0 || fatSize == 0) return null;
            long size = total * bps;
            if (pos + size > diskLength) return null;
            var label = System.Text.Encoding.ASCII.GetString(s.Slice(0x47, 11)).Trim();
            return new FoundPartition { OffsetBytes = (ulong)pos, SizeBytes = (ulong)size, FileSystem = "FAT32", Label = label is "NO NAME" or "" ? null : label, Confidence = FoundConfidence.Medium };
        }
        if (s[3..11].SequenceEqual("EXFAT   "u8))
        {
            long length = BinaryPrimitives.ReadInt64LittleEndian(s[0x48..]);
            int shift = s[0x6C];
            if (shift is < 9 or > 12 || (1 << shift) != ss || length <= 0) return null;
            long size = length << shift;
            if (pos + size > diskLength) return null;
            long recorded = BinaryPrimitives.ReadInt64LittleEndian(s[0x40..]);
            return new FoundPartition
            {
                OffsetBytes = (ulong)pos, SizeBytes = (ulong)size, FileSystem = "exFAT",
                Confidence = recorded == pos / ss ? FoundConfidence.High : FoundConfidence.Medium,
            };
        }
        return null;
    }
}

/// <summary>Adds a single partition-table entry for a found partition (the data itself is not touched).</summary>
public static class PartitionTableEditor
{
    /// <summary>
    /// Adds an entry. A disk without a valid table gets a new MBR (only sector 0 is written, which never belongs to a partition).
    /// When <paramref name="restoreBootSector"/> is set (NTFS found through its backup), the backup boot sector is copied to the start.
    /// </summary>
    public static void AddEntry(IBlockDevice dev, ulong offset, ulong size, string fileSystem, bool restoreBootSector = false)
    {
        int ss = dev.SectorSize;
        if (offset % (ulong)ss != 0 || size % (ulong)ss != 0) throw new RawOperationException("Rec.Error.Alignment", "not sector aligned");
        var s0 = new byte[ss];
        dev.Read(0, s0);
        ulong first = offset / (ulong)ss, count = size / (ulong)ss;
        bool hasTable = s0[510] == 0x55 && s0[511] == 0xAA;
        var mbr = hasTable ? PartitionTableCodec.ReadMbr(s0) : null;

        if (mbr is { IsProtective: true })
        {
            var head = new byte[(2 + PartitionTableCodec.EntryArraySectors(ss)) * ss];
            dev.Read(0, head);
            var gpt = PartitionTableCodec.ReadGpt(head, ss);
            if (gpt.Entries.Any(e => first <= e.LastLba && e.FirstLba <= first + count - 1))
                throw new RawOperationException("Rec.Error.Overlap", "overlaps an existing partition");
            if (gpt.Entries.Count >= gpt.EntryCount) throw new RawOperationException("Op.Error.GptLimit", "no free GPT entry");
            var entry = new GptEntry(PartitionTypes.GptBasicData, Guid.NewGuid(), first, first + count - 1, 0, "Recovered");
            var updated = gpt with { Entries = gpt.Entries.Append(entry).OrderBy(e => e.FirstLba).ToList() };
            var (newHead, tail, tailLba) = PartitionTableCodec.BuildGpt(updated, (ulong)(dev.Length / ss), ss, s0);
            if (restoreBootSector) RestoreBoot(dev, offset, size);
            dev.Write((long)tailLba * ss, tail);
            dev.Write(0, newHead);
        }
        else
        {
            if (first + count > uint.MaxValue) throw new RawOperationException("Op.Error.Mbr2TB", "beyond 2 TB");
            mbr ??= new MbrDisk((uint)Random.Shared.Next(1, int.MaxValue), new byte[440], Array.Empty<MbrEntry>());
            var entries = Enumerable.Range(0, 4).Select(i => i < mbr.Entries.Count ? mbr.Entries[i] : new MbrEntry(false, 0, 0, 0)).ToList();
            if (mbr.HasExtended) throw new RawOperationException("Rec.Error.Extended", "disks with extended partitions are not supported");
            if (entries.Any(e => !e.IsEmpty && first <= e.LastLba && e.FirstLba <= first + count - 1))
                throw new RawOperationException("Rec.Error.Overlap", "overlaps an existing partition");
            int slot = entries.FindIndex(e => e.IsEmpty);
            if (slot < 0) throw new RawOperationException("Op.Error.MbrPrimaryLimit", "all four MBR entries are used");
            byte t = fileSystem.Equals("FAT32", StringComparison.OrdinalIgnoreCase) ? (byte)0x0C : (byte)0x07; // 0x07 = NTFS/exFAT
            entries[slot] = new MbrEntry(false, t, (uint)first, (uint)count);
            if (!hasTable) s0.AsSpan().Clear();
            PartitionTableCodec.WriteMbr(s0, mbr with { Entries = entries });
            if (restoreBootSector) RestoreBoot(dev, offset, size);
            dev.Write(0, s0);
        }
        dev.Flush();
    }

    private static void RestoreBoot(IBlockDevice dev, ulong offset, ulong size)
    {
        int ss = dev.SectorSize;
        var backup = new byte[ss];
        dev.Read((long)(offset + size) - ss, backup);
        if (!backup.AsSpan(3, 8).SequenceEqual("NTFS    "u8)) throw new RawOperationException("Rec.Error.NoBackupBoot", "no NTFS backup boot sector at the end");
        dev.Write((long)offset, backup);
    }
}
