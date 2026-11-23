using System.Buffers.Binary;
using Lasteast.Core.RawDisk;

namespace Lasteast.Core.Recovery;

public enum BootSectorState
{
    /// <summary>The boot sector at the start is valid: if the volume is still unreadable, the damage is elsewhere.</summary>
    PrimaryValid,
    /// <summary>The start is damaged and a valid backup was found: <see cref="BootSectorDiagnosis.Writes"/> repairs it.</summary>
    Repairable,
    /// <summary>The start is damaged and no usable backup exists.</summary>
    NoBackup,
}

/// <param name="Writes">Volume-relative offsets and the sectors to put there (copied from the backup).</param>
/// <param name="Evidence">What made the backup trustworthy, for the preview (e.g. "MFT record 0 found at cluster 786432").</param>
public sealed record BootSectorDiagnosis(BootSectorState State, string? FileSystem, IReadOnlyList<(long Offset, byte[] Data)> Writes, string Evidence)
{
    public long BytesToWrite => Writes.Sum(w => (long)w.Data.Length);
}

/// <summary>
/// Repairs a damaged boot sector from the copy every major file system keeps: NTFS stores one in the volume's last
/// sector, FAT32 in sectors 6–8, exFAT a whole backup boot region in sectors 12–23. A backup is used only when it is
/// consistent with the partition it sits in and, for NTFS, when the MFT it points to really is there — so a stale copy
/// from an older, larger volume is never written. Only the boot region at the start of the volume changes.
/// </summary>
public static class BootSectorRepair
{
    public static BootSectorDiagnosis Diagnose(IBlockDevice volume)
    {
        int ss = Math.Max(512, volume.SectorSize);
        var s0 = Read(volume, 0, ss);
        if (NtfsValid(volume, s0, 0, out _) || Fat32Valid(volume, s0) || ExfatRegionValid(volume, 0, ss))
            return new BootSectorDiagnosis(BootSectorState.PrimaryValid, Kind(s0), Array.Empty<(long, byte[])>(), "");

        // NTFS: the backup sits at sector "total sectors" (normally the partition's last sector; a volume smaller than
        // its partition puts it earlier), so look at the end first and then walk back a little.
        long sectors = volume.Length / ss;
        int tailSectors = (int)Math.Min(sectors - 1, 16384);
        if (tailSectors > 0)
        {
            var tail = Read(volume, (sectors - tailSectors) * ss, tailSectors * ss);
            for (int k = tailSectors - 1; k >= 0; k--)
            {
                var b = tail.AsSpan(k * ss, ss);
                if (!b.Slice(3, 8).SequenceEqual("NTFS    "u8)) continue;
                long i = sectors - tailSectors + k;
                if (BinaryPrimitives.ReadInt64LittleEndian(b[0x28..]) != i) continue; // a backup sits exactly "total sectors" after the start
                var copy = b.ToArray();
                if (NtfsValid(volume, copy, i * ss, out var evidence))
                    return new BootSectorDiagnosis(BootSectorState.Repairable, "NTFS", new[] { (0L, copy) }, evidence);
            }
        }

        // exFAT: backup boot region at sectors 12–23 with its own checksum.
        if (ExfatRegionValid(volume, 12, ss))
        {
            var writes = Enumerable.Range(0, 12).Select(k => ((long)k * ss, Read(volume, (12L + k) * ss, ss))).ToList();
            return new BootSectorDiagnosis(BootSectorState.Repairable, "exFAT", writes, "backup boot region checksum matches");
        }

        // FAT32: backup boot sector at sector 6 (with FSInfo at 7 and the third boot sector at 8).
        if (sectors > 9)
        {
            var b6 = Read(volume, 6L * ss, ss);
            if (Fat32Valid(volume, b6) && BinaryPrimitives.ReadUInt16LittleEndian(b6.AsSpan(0x32)) == 6)
            {
                var writes = Enumerable.Range(0, 3).Select(k => ((long)k * ss, Read(volume, (6L + k) * ss, ss))).ToList();
                // the backup FSInfo's free-space hints are older than the volume: mark them unknown so Windows recounts
                writes[1].Item2.AsSpan(488, 8).Fill(0xFF);
                // the backup's FSInfo pointer must be 1 for the copied FSInfo to land where it is expected
                if (BinaryPrimitives.ReadUInt16LittleEndian(b6.AsSpan(0x30)) == 1)
                    return new BootSectorDiagnosis(BootSectorState.Repairable, "FAT32", writes, "backup boot sector at sector 6 matches the partition size");
            }
        }
        return new BootSectorDiagnosis(BootSectorState.NoBackup, null, Array.Empty<(long, byte[])>(), "");
    }

    /// <summary>Writes the repair and reads it back. The caller saves the sectors being replaced first.</summary>
    public static void Apply(IBlockDevice disk, long volumeOffset, BootSectorDiagnosis d)
    {
        if (d.State != BootSectorState.Repairable) throw new RawOperationException("BootFix.Error.NotRepairable", d.State.ToString());
        foreach (var (off, data) in d.Writes) disk.Write(volumeOffset + off, data);
        disk.Flush();
        foreach (var (off, data) in d.Writes)
        {
            var back = Read(disk, volumeOffset + off, data.Length);
            if (!back.AsSpan().SequenceEqual(data)) throw new RawOperationException("BootFix.Error.Verify", $"sector at {volumeOffset + off} reads back differently");
        }
    }

    /// <summary>The sectors a repair would replace, in the same layout as <see cref="BootSectorDiagnosis.Writes"/> (for the undo file).</summary>
    public static byte[] Current(IBlockDevice disk, long volumeOffset, BootSectorDiagnosis d)
    {
        using var ms = new MemoryStream();
        foreach (var (off, data) in d.Writes) ms.Write(Read(disk, volumeOffset + off, data.Length));
        return ms.ToArray();
    }

    // ---------------------------------------------------------------- checks

    private static byte[] Read(IBlockDevice dev, long offset, int length)
    {
        var b = new byte[length];
        dev.Read(offset, b);
        return b;
    }

    private static string? Kind(byte[] s) =>
        s.AsSpan(3, 8).SequenceEqual("NTFS    "u8) ? "NTFS" : s.AsSpan(3, 8).SequenceEqual("EXFAT   "u8) ? "exFAT"
        : s.AsSpan(82, 8).SequenceEqual("FAT32   "u8) ? "FAT32" : null;

    private static bool NtfsValid(IBlockDevice vol, byte[] s, long at, out string evidence)
    {
        evidence = "";
        if (s[510] != 0x55 || s[511] != 0xAA || !s.AsSpan(3, 8).SequenceEqual("NTFS    "u8)) return false;
        int bps = BinaryPrimitives.ReadUInt16LittleEndian(s.AsSpan(0x0B));
        if (bps != Math.Max(512, vol.SectorSize)) return false;
        int spc = s[0x0D] > 0x80 ? 1 << (256 - s[0x0D]) : s[0x0D];
        if (spc <= 0 || (spc & (spc - 1)) != 0) return false;
        long total = BinaryPrimitives.ReadInt64LittleEndian(s.AsSpan(0x28));
        long mft = BinaryPrimitives.ReadInt64LittleEndian(s.AsSpan(0x30));
        long mirr = BinaryPrimitives.ReadInt64LittleEndian(s.AsSpan(0x38));
        long volSectors = vol.Length / bps;
        if (total <= 0 || total >= volSectors + 1 || total < volSectors - 16384) return false; // must fit this partition
        long cluster = (long)bps * spc;
        bool Record(long lcn) => lcn > 0 && (lcn + 1) * cluster <= total * bps && Read(vol, lcn * cluster, 4).AsSpan().SequenceEqual("FILE"u8);
        if (Record(mft)) { evidence = $"MFT found at cluster {mft}"; return true; }
        if (Record(mirr)) { evidence = $"MFT mirror found at cluster {mirr}"; return true; }
        return false;
    }

    private static bool Fat32Valid(IBlockDevice vol, byte[] s)
    {
        if (s[510] != 0x55 || s[511] != 0xAA || !s.AsSpan(82, 8).SequenceEqual("FAT32   "u8)) return false;
        int bps = BinaryPrimitives.ReadUInt16LittleEndian(s.AsSpan(0x0B));
        int spc = s[0x0D];
        long total = BinaryPrimitives.ReadUInt32LittleEndian(s.AsSpan(0x20));
        int fats = s[0x10];
        return bps == Math.Max(512, vol.SectorSize) && spc > 0 && (spc & (spc - 1)) == 0 && fats is 1 or 2
               && total > 0 && total <= vol.Length / bps && total >= vol.Length / bps - 16384;
    }

    private static bool ExfatRegionValid(IBlockDevice vol, long firstSector, int ss)
    {
        if (vol.Length < (firstSector + 12) * ss) return false;
        var region = Read(vol, firstSector * ss, 12 * ss);
        if (!region.AsSpan(3, 8).SequenceEqual("EXFAT   "u8) || region[510] != 0x55 || region[511] != 0xAA) return false;
        if (1 << region[0x6C] != ss) return false;
        long volLen = BinaryPrimitives.ReadInt64LittleEndian(region.AsSpan(0x48));
        if (volLen <= 0 || volLen > vol.Length / ss || volLen < vol.Length / ss - 16384) return false;
        uint chk = 0;
        for (int i = 0; i < 11 * ss; i++)
        {
            if (i is 106 or 107 or 112) continue; // VolumeFlags and PercentInUse change at run time
            chk = ((chk & 1) != 0 ? 0x80000000u : 0) + (chk >> 1) + region[i];
        }
        for (int i = 11 * ss; i < 12 * ss; i += 4)
            if (BinaryPrimitives.ReadUInt32LittleEndian(region.AsSpan(i)) != chk) return false;
        return true;
    }
}
