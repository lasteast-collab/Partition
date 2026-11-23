using Lasteast.Core.Layout;
using Lasteast.Core.Models;

namespace Lasteast.Core.Operations;

public static partial class LayoutSimulator
{
    private static bool IsProtected(DiskInfo d, PartitionInfo p) =>
        p.Volume?.IsWindowsVolume == true || p.Volume?.IsSystemPartitionVolume == true || p.MbrActive && d.IsBootDisk
        || p.Role is PartitionRole.EfiSystem or PartitionRole.MicrosoftReserved or PartitionRole.Recovery;

    private static DiskInfo ReplacePartition(DiskInfo d, PartitionInfo target, PartitionInfo? replacement) => d with
    {
        Partitions = d.Partitions.Select(p => ReferenceEquals(p, target) ? replacement : p).OfType<PartitionInfo>().ToList(),
    };

    private static DiskInfo ApplyCopyFiles(DiskInfo d, PartitionInfo dest, CopyFilesOp c, Func<int, DiskInfo?>? otherDisk, List<ValidationIssue> issues)
    {
        var srcDisk = c.SourceDiskNumber == d.Number ? d : otherDisk?.Invoke(c.SourceDiskNumber);
        var src = srcDisk?.Partitions.FirstOrDefault(p => p.OffsetBytes == c.SourceOffsetBytes);
        if (src is null) return Fail(issues, "Op.Error.NotFound", d);
        if (src.Volume is not { AccessError: null, FileSystem: not null } sv) return Fail(issues, "Op.Error.Unreadable", d);
        if (dest.Volume is not { AccessError: null, FileSystem: not null } dv) return Fail(issues, "Op.Error.Unreadable", d);
        if (c.Folder.Length == 0 || c.Folder.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return Fail(issues, "Merge.Error.Folder", d);
        ulong used = (sv.CapacityBytes ?? 0) - (sv.FreeBytes ?? 0);
        // the copy needs the source's used space plus some working room on the destination
        if (dv.FreeBytes is { } free && free < used + used / 20 + 64 * MiB) return Fail(issues, "Merge.Error.Space", d, used, free);
        Warn(issues, "Merge.Warn.Copy", used);
        return d with { Partitions = d.Partitions.Select(p => ReferenceEquals(p, dest) ? p with { Volume = dv with { FreeBytes = (dv.FreeBytes ?? 0) - Math.Min(dv.FreeBytes ?? 0, used) } } : p).ToList() };
    }

    private static DiskInfo ApplyCopyPartition(DiskInfo d, CopyPartitionOp c, IReadOnlySet<char> lettersElsewhere, Func<int, DiskInfo?>? otherDisk, List<ValidationIssue> issues)
    {
        var srcDisk = c.SourceDiskNumber == d.Number ? d : otherDisk?.Invoke(c.SourceDiskNumber);
        var src = srcDisk?.Partitions.FirstOrDefault(p => p.OffsetBytes == c.SourceOffsetBytes && !p.IsPending);
        if (srcDisk is null || src is null) return Fail(issues, "Op.Error.NotFound", d);
        if (src.Role is PartitionRole.MbrExtended) return Fail(issues, "Op.Error.NotFound", d);
        if (srcDisk.BytesPerLogicalSector != d.BytesPerLogicalSector) return Fail(issues, "CopyP.Error.SectorSize", d);
        if (c.SizeBytes < src.SizeBytes) return Fail(issues, "CopyP.Error.TooSmall", d);
        var fs = src.Volume?.FileSystem;
        if (fs is not ("NTFS" or "FAT32" or "exFAT")) return Fail(issues, "CopyP.Error.FileSystem", d, fs ?? "RAW");
        // the free-space checks of a new partition apply; the copy keeps the source's file system and label
        var create = new CreatePartitionOp(d.Number, c.DestOffsetBytes, (c.SizeBytes + MiB - 1) / MiB * MiB, fs, null, null);
        var after = ApplyCreate(d, create, lettersElsewhere, issues);
        if (issues.Any(i => i.IsError)) return d;
        if (src.Volume?.IsWindowsVolume == true) Warn(issues, "Clone.Warn.Snapshot");
        Warn(issues, "CopyP.Warn.SameSerial");
        return after with
        {
            Partitions = after.Partitions.Select(p => p.IsPending && p.OffsetBytes == c.DestOffsetBytes
                ? p with { Volume = p.Volume! with { Label = src.Volume?.Label, FreeBytes = src.Volume?.FreeBytes, CapacityBytes = src.Volume?.CapacityBytes } }
                : p).ToList(),
        };
    }

    private static DiskInfo ApplyWipe(DiskInfo d, WipeOp w, List<ValidationIssue> issues)
    {
        if (w.OffsetBytes is null)
        {
            if (d.IsWindowsDisk || d.IsBootDisk) return Fail(issues, "Wipe.Error.SystemDisk", d);
            if (d.Partitions.Any(p => p.IsPending)) return Fail(issues, "Op.Error.PendingTarget", d);
            Warn(issues, "Wipe.Warn.Disk", d.Number);
            if (d.Media is MediaKind.Ssd) Warn(issues, "Wipe.Warn.Ssd");
            return d with { Style = PartitionStyle.Raw, Partitions = Array.Empty<PartitionInfo>(), GptDiskId = null, MbrSignature = null };
        }
        var target = d.Partitions.FirstOrDefault(p => p.OffsetBytes == w.OffsetBytes && p.Role != PartitionRole.MbrExtended);
        if (target is null) return Fail(issues, "Op.Error.NotFound", d);
        if (target.IsPending) return Fail(issues, "Op.Error.PendingTarget", d);
        if (IsProtected(d, target)) return Fail(issues, "Op.Error.Protected", d);
        Warn(issues, "Wipe.Warn.Partition");
        if (d.Media is MediaKind.Ssd) Warn(issues, "Wipe.Warn.Ssd");
        return ReplacePartition(d, target, target with
        {
            Volume = (target.Volume ?? new VolumeInfo { VolumeGuidPath = $"pending:{d.Number}:{target.OffsetBytes}" })
                with { FileSystem = null, Label = null, FreeBytes = null, AccessError = "wiped", AccessErrorCode = 1005 },
        });
    }

    private static DiskInfo ApplySanitize(DiskInfo d, SanitizeDiskOp s, List<ValidationIssue> issues)
    {
        if (d.IsWindowsDisk || d.IsBootDisk) return Fail(issues, "Wipe.Error.SystemDisk", d);
        if (d.Media is not MediaKind.Ssd && d.Bus is not BusKind.Nvme) return Fail(issues, "Sanitize.Error.NotSsd", d);
        if (d.Partitions.Any(p => p.IsPending)) return Fail(issues, "Op.Error.PendingTarget", d);
        Warn(issues, "Sanitize.Warn.Device", d.Number);
        return d with { Style = PartitionStyle.Raw, Partitions = Array.Empty<PartitionInfo>(), GptDiskId = null, MbrSignature = null };
    }

    private static DiskInfo ApplyRepairFileSystem(DiskInfo d, PartitionInfo target, List<ValidationIssue> issues)
    {
        if (target.Volume?.IsWindowsVolume == true) return Fail(issues, "Chk.Error.InUse", d);
        var fs = target.Volume?.FileSystem;
        if (fs is not ("NTFS" or "FAT32" or "exFAT" or "FAT")) return Fail(issues, "Chk.Error.FileSystem", d, fs ?? "RAW");
        Warn(issues, "Chk.Warn.Dismount");
        return d;
    }

    private static DiskInfo ApplyConvertFileSystem(DiskInfo d, PartitionInfo target, ConvertFileSystemOp c, List<ValidationIssue> issues)
    {
        if (!c.TargetFileSystem.Equals("NTFS", StringComparison.OrdinalIgnoreCase)) return Fail(issues, "ConvFs.Error.Target", d);
        if (target.Volume is not { AccessError: null, FileSystem: "FAT32" or "FAT" } v) return Fail(issues, "ConvFs.Error.Source", d, target.Volume?.FileSystem ?? "RAW");
        if (v.IsWindowsVolume) return Fail(issues, "Op.Error.Protected", d);
        if (v.DriveLetter is null) return Fail(issues, "ConvFs.Error.Letter", d);
        if (v.FreeBytes is { } free && free < (v.CapacityBytes ?? 0) / 100 + 16 * MiB) return Fail(issues, "ConvFs.Error.Space", d);
        Warn(issues, "ConvFs.Warn.OneWay");
        return ReplacePartition(d, target, target with
        {
            MbrType = d.Style == PartitionStyle.Mbr ? (byte)0x07 : target.MbrType,
            Volume = v with { FileSystem = "NTFS" },
        });
    }

    private static DiskInfo ApplyReformatKeepingFiles(DiskInfo d, PartitionInfo target, ReformatKeepingFilesOp r, List<ValidationIssue> issues)
    {
        if (IsProtected(d, target)) return Fail(issues, "Op.Error.Protected", d);
        if (target.Volume is not { AccessError: null, FileSystem: not null } v) return Fail(issues, "Op.Error.Unreadable", d);
        if (!FileSystems.IsSupported(r.FileSystem)) return Fail(issues, "Op.Error.FileSystem", d, r.FileSystem);
        if (r.FileSystem.Equals("FAT32", StringComparison.OrdinalIgnoreCase) && target.SizeBytes > FileSystems.Fat32MaxBytes)
            return Fail(issues, "Op.Error.Fat32Limit", d);
        if (r.ClusterSizeBytes is { } cs && (cs < 512 || (cs & (cs - 1)) != 0 || cs > 2 * 1024 * 1024)) return Fail(issues, "Reformat.Error.Cluster", d);
        if (string.Equals(v.FileSystem, r.FileSystem, StringComparison.OrdinalIgnoreCase) && (r.ClusterSizeBytes is null || r.ClusterSizeBytes == v.ClusterSizeBytes))
            return Fail(issues, "Op.Error.NoChange", d);
        if (string.IsNullOrWhiteSpace(r.StagingFolder)) return Fail(issues, "Reformat.Error.Staging", d);
        Warn(issues, "Reformat.Warn.Staging", r.StagingFolder, r.UsedBytes);
        Warn(issues, "Op.Warn.DataLoss");
        return ReplacePartition(d, target, target with
        {
            MbrType = d.Style == PartitionStyle.Mbr ? FileSystems.MbrTypeFor(r.FileSystem) : target.MbrType,
            Volume = v with { FileSystem = r.FileSystem, ClusterSizeBytes = r.ClusterSizeBytes ?? v.ClusterSizeBytes },
        });
    }

    private static DiskInfo ApplyRepairMbr(DiskInfo d, RepairMbrOp r, List<ValidationIssue> issues)
    {
        if (d.Style != PartitionStyle.Mbr) return Fail(issues, "Mbr.Error.NotMbr", d);
        if (r.BackupFolder is { } f && !Directory.Exists(f)) return Fail(issues, "Mbr.Error.Backup", d, f);
        Warn(issues, "Mbr.Warn.CodeOnly");
        return d;
    }

    private static DiskInfo ApplyConvertDiskType(DiskInfo d, ConvertDiskTypeOp c, List<ValidationIssue> issues)
    {
        if (c.ToDynamic == d.IsDynamic) return Fail(issues, "Op.Error.NoChange", d);
        if (d.IsWindowsDisk || d.IsBootDisk) return Fail(issues, "DiskType.Error.System", d);
        if (d.IsExternal) return Fail(issues, "DiskType.Error.Removable", d);
        if (c.ToDynamic)
        {
            if (d.Style is not (PartitionStyle.Gpt or PartitionStyle.Mbr)) return Fail(issues, "Op.Error.DiskStyle", d, d.Number);
            Warn(issues, "DiskType.Warn.Deprecated");
            Warn(issues, "DiskType.Warn.OneWay");
            return d with { IsDynamic = true };
        }
        // Windows converts dynamic → basic only when the disk holds no volumes; with volumes it would destroy them.
        if (d.Partitions.Any(p => p.Volume is not null && p.Role is not (PartitionRole.LdmMetadata or PartitionRole.MicrosoftReserved)))
            return Fail(issues, "DiskType.Error.HasVolumes", d);
        return d with { IsDynamic = false, Partitions = d.Partitions.Where(p => p.Role is not (PartitionRole.LdmMetadata or PartitionRole.LdmData)).ToList() };
    }

    private static DiskInfo ApplyConvertPartitionKind(DiskInfo d, PartitionInfo target, ConvertPartitionKindOp c, List<ValidationIssue> issues)
    {
        if (d.Style != PartitionStyle.Mbr) return Fail(issues, "Kind.Error.MbrOnly", d);
        bool isLogical = target.Role == PartitionRole.MbrLogical;
        if (isLogical == c.ToLogical) return Fail(issues, "Op.Error.NoChange", d);
        if (target.MbrActive || target.Volume?.IsWindowsVolume == true || target.Volume?.IsSystemPartitionVolume == true) return Fail(issues, "Op.Error.Protected", d);
        var plan = Lasteast.Core.PartitionTables.MbrKindConverter.Check(d, target.OffsetBytes, c.ToLogical);
        if (plan is not null) return Fail(issues, plan, d);
        Warn(issues, "Kind.Warn.TableOnly");
        return ReplacePartition(d, target, target with { Role = c.ToLogical ? PartitionRole.MbrLogical : PartitionRole.BasicData });
    }
}
