using Lasteast.Core.Models;

namespace Lasteast.Core.Operations;

public enum OperationKind { Create, Delete, Format, Resize, SetLabel, SetDriveLetter, ConvertStyle, Move, Clone, PrepareBoot, RecoverPartition }

/// <summary>
/// A planned change to one disk. Operations are pure data: they describe intent and are only
/// carried out by <see cref="GuardedExecutionEngine"/> after simulation, validation, backup and
/// explicit confirmation.
/// Partitions are identified by their start offset (stable across renumbering; resize keeps it).
/// </summary>
public abstract record DiskOperation(int DiskNumber)
{
    public abstract OperationKind Kind { get; }
    /// <summary>The operation erases existing data (delete, format).</summary>
    public virtual bool DestroysData => false;
    /// <summary>Rough duration for the preview; real time is reported during execution.</summary>
    public virtual int EstimatedSeconds => 5;
}

public sealed record CreatePartitionOp(int DiskNumber, ulong OffsetBytes, ulong SizeBytes, string FileSystem, string? Label, char? DriveLetter)
    : DiskOperation(DiskNumber)
{
    public override OperationKind Kind => OperationKind.Create;
    public override int EstimatedSeconds => 10;
}

public sealed record DeletePartitionOp(int DiskNumber, ulong OffsetBytes) : DiskOperation(DiskNumber)
{
    public override OperationKind Kind => OperationKind.Delete;
    public override bool DestroysData => true;
}

public sealed record FormatPartitionOp(int DiskNumber, ulong OffsetBytes, string FileSystem, string? Label, uint? ClusterSizeBytes = null)
    : DiskOperation(DiskNumber)
{
    public override OperationKind Kind => OperationKind.Format;
    public override bool DestroysData => true;
    public override int EstimatedSeconds => 15; // quick format
}

/// <summary>Shrink or extend by moving the partition's end. The start offset never moves.</summary>
public sealed record ResizePartitionOp(int DiskNumber, ulong OffsetBytes, ulong NewSizeBytes) : DiskOperation(DiskNumber)
{
    public override OperationKind Kind => OperationKind.Resize;
    public override int EstimatedSeconds => 60;
}

public sealed record SetLabelOp(int DiskNumber, ulong OffsetBytes, string Label) : DiskOperation(DiskNumber)
{
    public override OperationKind Kind => OperationKind.SetLabel;
    public override int EstimatedSeconds => 1;
}

/// <summary>Assigns <see cref="Letter"/>, or removes the current letter when null.</summary>
public sealed record SetDriveLetterOp(int DiskNumber, ulong OffsetBytes, char? Letter) : DiskOperation(DiskNumber)
{
    public override OperationKind Kind => OperationKind.SetDriveLetter;
    public override int EstimatedSeconds => 1;
}

/// <summary>Non-destructive MBR↔GPT conversion: partitions keep their sectors, only the table changes.</summary>
public sealed record ConvertStyleOp(int DiskNumber, PartitionStyle Target) : DiskOperation(DiskNumber)
{
    public override OperationKind Kind => OperationKind.ConvertStyle;
    public override int EstimatedSeconds => 5;
}

/// <summary>
/// Moves a partition to a new start offset (same size), copying its data with a crash-resumable journal.
/// Moving the Recovery partition is how "make room to extend C:" works.
/// </summary>
public sealed record MovePartitionOp(int DiskNumber, ulong OffsetBytes, ulong NewOffsetBytes, ulong SizeBytes = 0) : DiskOperation(DiskNumber)
{
    public override OperationKind Kind => OperationKind.Move;
    /// <summary>~150 MB/s read+write on the same disk.</summary>
    public override int EstimatedSeconds => (int)Math.Max(10, SizeBytes / (150UL * 1024 * 1024));
}

/// <summary>
/// Copies the partitions of <see cref="SourceDiskNumber"/> onto <see cref="DiskOperation.DiskNumber"/> (the destination).
/// Everything on the destination is overwritten. The source is only read.
/// </summary>
public sealed record CloneDiskOp(int DiskNumber, int SourceDiskNumber, Lasteast.Core.RawDisk.CloneMode Mode, bool NewIdentifiers, bool Verify,
    IReadOnlyList<ulong>? OnlyPartitions = null, ulong EstimatedBytes = 0) : DiskOperation(DiskNumber)
{
    public override OperationKind Kind => OperationKind.Clone;
    public override bool DestroysData => true;
    public override int EstimatedSeconds => (int)Math.Max(30, EstimatedBytes * (Verify ? 2UL : 1UL) / (200UL * 1024 * 1024));
}

/// <summary>
/// Makes a disk holding a copy of Windows bootable on its own: rebuilds the boot files with Microsoft's bcdboot
/// from the copy's Windows folder onto the disk's system partition, and points the copy's C: drive letter at its own
/// partition (offline registry, MountedDevices). Never touches the running Windows.
/// </summary>
/// <param name="WindowsDirectoryOverride">Technician/test use: take boot files from this Windows folder instead of the disk's own copy (no registry change then).</param>
public sealed record PrepareBootOp(int DiskNumber, bool FixDriveLetter = true, string? WindowsDirectoryOverride = null) : DiskOperation(DiskNumber)
{
    public override OperationKind Kind => OperationKind.PrepareBoot;
    public override int EstimatedSeconds => 20;
}

/// <summary>
/// Restores a lost partition found by the scanner by adding a partition-table entry for it. The data area is not
/// written (except, on request, copying an NTFS backup boot sector over a damaged primary one).
/// </summary>
public sealed record RecoverPartitionOp(int DiskNumber, ulong OffsetBytes, ulong SizeBytes, string FileSystem, bool RestoreBootSector = false)
    : DiskOperation(DiskNumber)
{
    public override OperationKind Kind => OperationKind.RecoverPartition;
    public override int EstimatedSeconds => 3;
}

public enum IssueLevel { Error, Warning }

/// <summary>Validation result. <see cref="Key"/> is a localization key.</summary>
public sealed record ValidationIssue(IssueLevel Level, string Key, params object[] Args)
{
    public bool IsError => Level == IssueLevel.Error;
}

public static class FileSystems
{
    public static readonly IReadOnlyList<string> Supported = new[] { "NTFS", "exFAT", "FAT32", "ReFS" };

    /// <summary>Windows' own format refuses FAT32 above 32 GiB.</summary>
    public const ulong Fat32MaxBytes = 32UL * 1024 * 1024 * 1024;

    public static bool IsSupported(string fs) => Supported.Contains(fs, StringComparer.OrdinalIgnoreCase);

    /// <summary>File systems Windows can shrink/extend online.</summary>
    public static bool CanResize(string? fs) => fs is not null && (fs.Equals("NTFS", StringComparison.OrdinalIgnoreCase) || fs.Equals("ReFS", StringComparison.OrdinalIgnoreCase));

    public static int MaxLabelLength(string? fs) => fs?.ToUpperInvariant() switch
    {
        "FAT32" or "FAT" or "FAT16" => 11,
        "EXFAT" => 15,
        _ => 32,
    };

    public static byte MbrTypeFor(string fs) => fs.Equals("FAT32", StringComparison.OrdinalIgnoreCase) ? (byte)0x0C : (byte)0x07;
}
