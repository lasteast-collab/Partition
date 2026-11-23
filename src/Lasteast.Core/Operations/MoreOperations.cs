using Lasteast.Core.Models;

namespace Lasteast.Core.Operations;

/// <summary>How a wipe overwrites data.</summary>
public enum WipeMethod
{
    /// <summary>One pass of zeros: enough for HDDs against software recovery (NIST SP 800-88 "Clear").</summary>
    Zeros,
    /// <summary>One pass of pseudo-random data.</summary>
    Random,
    /// <summary>Random, random, zeros (for policies that ask for several passes; no benefit on modern HDDs).</summary>
    ThreePass,
}

/// <summary>
/// Copies every file of one volume into a folder of another, verifying each copy by SHA-256. The source is only read.
/// It is the first step of a merge (followed by deleting the source partition and extending the destination).
/// </summary>
/// <param name="DiskNumber">Disk holding the destination volume.</param>
public sealed record CopyFilesOp(int DiskNumber, ulong DestOffsetBytes, int SourceDiskNumber, ulong SourceOffsetBytes, string Folder, ulong UsedBytes = 0)
    : DiskOperation(DiskNumber)
{
    public override OperationKind Kind => OperationKind.CopyFiles;
    public override int EstimatedSeconds => (int)Math.Max(10, UsedBytes * 2 / (100UL * 1024 * 1024));
}

/// <summary>
/// Copies a partition (all its sectors, or only the used clusters of NTFS) into unallocated space on the same or another
/// disk and adds a table entry for the copy. The source is only read (locked, or read from a snapshot when in use).
/// </summary>
public sealed record CopyPartitionOp(int DiskNumber, ulong DestOffsetBytes, int SourceDiskNumber, ulong SourceOffsetBytes, ulong SizeBytes)
    : DiskOperation(DiskNumber)
{
    public override OperationKind Kind => OperationKind.CopyPartition;
    public override int EstimatedSeconds => (int)Math.Max(15, SizeBytes / (150UL * 1024 * 1024));
}

/// <summary>Overwrites a partition (OffsetBytes set) or a whole disk (null). Everything there is destroyed.</summary>
public sealed record WipeOp(int DiskNumber, ulong? OffsetBytes, WipeMethod Method, bool Verify, ulong SizeBytes = 0) : DiskOperation(DiskNumber)
{
    public override OperationKind Kind => OperationKind.Wipe;
    public override bool DestroysData => true;
    public override int EstimatedSeconds => (int)Math.Max(10, SizeBytes * (ulong)(Method == WipeMethod.ThreePass ? 3 : 1) * (Verify ? 2UL : 1UL) / (120UL * 1024 * 1024));
}

/// <summary>
/// Device-level sanitize of a whole SSD/NVMe drive through Windows' "reinitialize media" request (NVMe Sanitize /
/// Format with secure erase). The drive's own firmware erases every cell, including ones overwriting cannot reach.
/// </summary>
public sealed record SanitizeDiskOp(int DiskNumber) : DiskOperation(DiskNumber)
{
    public override OperationKind Kind => OperationKind.Sanitize;
    public override bool DestroysData => true;
    public override int EstimatedSeconds => 120;
}

/// <summary>Repairs file-system errors (chkdsk /f) on a volume that is not in use.</summary>
public sealed record RepairFileSystemOp(int DiskNumber, ulong OffsetBytes) : DiskOperation(DiskNumber)
{
    public override OperationKind Kind => OperationKind.RepairFileSystem;
    public override int EstimatedSeconds => 120;
}

/// <summary>Converts FAT32 to NTFS in place with Windows' own convert.exe (files kept).</summary>
public sealed record ConvertFileSystemOp(int DiskNumber, ulong OffsetBytes, string TargetFileSystem) : DiskOperation(DiskNumber)
{
    public override OperationKind Kind => OperationKind.ConvertFileSystem;
    public override int EstimatedSeconds => 60;
}

/// <summary>
/// Changes a volume's file system or cluster size while keeping its files: copies them to a staging folder on another
/// disk (verified), formats, copies them back (verified), and only then removes the staging copy.
/// </summary>
public sealed record ReformatKeepingFilesOp(int DiskNumber, ulong OffsetBytes, string FileSystem, uint? ClusterSizeBytes, string StagingFolder, ulong UsedBytes)
    : DiskOperation(DiskNumber)
{
    public override OperationKind Kind => OperationKind.ReformatKeepingFiles;
    /// <summary>A format runs in the middle; the files exist in the verified staging copy during it.</summary>
    public override bool DestroysData => true;
    public override int EstimatedSeconds => (int)Math.Max(30, UsedBytes * 4 / (100UL * 1024 * 1024));
}

/// <summary>Basic ↔ dynamic disk (Windows' own conversion). Dynamic → basic only for disks without volumes.</summary>
public sealed record ConvertDiskTypeOp(int DiskNumber, bool ToDynamic) : DiskOperation(DiskNumber)
{
    public override OperationKind Kind => OperationKind.ConvertDiskType;
    public override int EstimatedSeconds => 10;
}

/// <summary>
/// Rewrites the boot code of an MBR disk (bytes 0–439) — from a Lasteast partition-table backup of this disk, or with
/// Microsoft's bootsect.exe when <see cref="BackupFolder"/> is null. The disk signature and partition entries are kept.
/// </summary>
public sealed record RepairMbrOp(int DiskNumber, string? BackupFolder) : DiskOperation(DiskNumber)
{
    public override OperationKind Kind => OperationKind.RepairMbr;
    public override int EstimatedSeconds => 3;
}

/// <summary>MBR: turns a primary partition into a logical one inside the extended partition, or back. Data untouched.</summary>
public sealed record ConvertPartitionKindOp(int DiskNumber, ulong OffsetBytes, bool ToLogical) : DiskOperation(DiskNumber)
{
    public override OperationKind Kind => OperationKind.ConvertPartitionKind;
    public override int EstimatedSeconds => 3;
}
