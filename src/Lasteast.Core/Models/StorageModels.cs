namespace Lasteast.Core.Models;

/// <summary>Partition table style of a physical disk.</summary>
public enum PartitionStyle
{
    Unknown = -1,
    Mbr = 0,
    Gpt = 1,
    Raw = 2,
}

/// <summary>Rotational or solid-state media. Determined from the seek-penalty property.</summary>
public enum MediaKind
{
    Unknown,
    Hdd,
    Ssd,
}

/// <summary>Bus/connection type as reported by the Windows storage stack (STORAGE_BUS_TYPE).</summary>
public enum BusKind
{
    Unknown = 0,
    Scsi = 1,
    Atapi = 2,
    Ata = 3,
    Ieee1394 = 4,
    Ssa = 5,
    Fibre = 6,
    Usb = 7,
    Raid = 8,
    IScsi = 9,
    Sas = 10,
    Sata = 11,
    Sd = 12,
    Mmc = 13,
    Virtual = 14,
    FileBackedVirtual = 15,
    Spaces = 16,
    Nvme = 17,
    Scm = 18,
    Ufs = 19,
}

/// <summary>
/// Health classification. In Phase 1 SMART is not read yet, so detection reports
/// <see cref="Unknown"/> rather than inventing a value.
/// </summary>
public enum HealthState
{
    Unknown,
    Healthy,
    Caution,
    Warning,
    Critical,
}

/// <summary>What a partition is used for, derived from its GPT type GUID or MBR type byte.</summary>
public enum PartitionRole
{
    Unknown,
    BasicData,
    EfiSystem,
    MicrosoftReserved,
    Recovery,
    MbrExtended,
    MbrLogical,
    LdmMetadata,
    LdmData,
    StorageSpaces,
    Linux,
    LinuxSwap,
    AppleHfs,
    Other,
}

public enum AlignmentStatus
{
    /// <summary>Starts on a 1 MiB boundary (Windows default, ideal for SSD/AF disks).</summary>
    Aligned1MiB,
    /// <summary>Starts on a physical-sector boundary (e.g. 4 KiB) but not 1 MiB.</summary>
    AlignedPhysical,
    /// <summary>Does not start on a physical-sector boundary — read-modify-write penalty on AF/SSD media.</summary>
    Misaligned,
}

public sealed record VolumeInfo
{
    /// <summary>\\?\Volume{GUID}\ path.</summary>
    public required string VolumeGuidPath { get; init; }
    public IReadOnlyList<string> MountPoints { get; init; } = Array.Empty<string>();
    public char? DriveLetter { get; init; }
    public string? Label { get; init; }
    public string? FileSystem { get; init; }
    public ulong? CapacityBytes { get; init; }
    public ulong? FreeBytes { get; init; }
    public uint? ClusterSizeBytes { get; init; }
    /// <summary>The volume that contains the running Windows directory.</summary>
    public bool IsWindowsVolume { get; init; }
    /// <summary>The firmware "system partition" (ESP on UEFI, active partition on BIOS) as recorded by Windows Setup.</summary>
    public bool IsSystemPartitionVolume { get; init; }
    public bool IsReadOnly { get; init; }
    /// <summary>Set when the volume exists but could not be queried (no media, locked, unsupported FS…).</summary>
    public string? AccessError { get; init; }
    /// <summary>Windows error code behind <see cref="AccessError"/> (see <see cref="VolumeProblems.Classify"/>).</summary>
    public int? AccessErrorCode { get; init; }

    public ulong? UsedBytes => CapacityBytes is { } c && FreeBytes is { } f && c >= f ? c - f : null;
}

public sealed record PartitionInfo
{
    public required int DiskNumber { get; init; }
    /// <summary>Windows partition number (1-based). 0 for MBR extended containers.</summary>
    public required int PartitionNumber { get; init; }
    public required ulong OffsetBytes { get; init; }
    public required ulong SizeBytes { get; init; }
    public required PartitionStyle Style { get; init; }
    public PartitionRole Role { get; init; }

    // GPT
    public Guid? GptType { get; init; }
    public Guid? GptId { get; init; }
    public string? GptName { get; init; }
    public ulong GptAttributes { get; init; }

    // MBR
    public byte? MbrType { get; init; }
    public bool MbrActive { get; init; }
    public bool MbrRecognized { get; init; }

    public bool IsServicePartition { get; init; }
    /// <summary>True for a partition that exists only in a simulated (pending) layout.</summary>
    public bool IsPending { get; init; }
    public VolumeInfo? Volume { get; init; }
    public AlignmentStatus Alignment { get; init; }

    public ulong EndBytes => OffsetBytes + SizeBytes;

    /// <summary>GPT attribute bit 62: hidden. MBR hidden types are detected via <see cref="PartitionTypes"/>.</summary>
    public bool IsHidden => Style == PartitionStyle.Gpt
        ? (GptAttributes & PartitionTypes.GptAttributeHidden) != 0
        : MbrType is { } t && PartitionTypes.IsMbrHiddenType(t);

    /// <summary>Partitions that a careless operation could make a machine unbootable with.</summary>
    public bool IsCritical => Role is PartitionRole.EfiSystem or PartitionRole.MicrosoftReserved or PartitionRole.Recovery
        || Volume is { IsWindowsVolume: true } or { IsSystemPartitionVolume: true }
        || MbrActive;
}

public sealed record DiskInfo
{
    public required int Number { get; init; }
    public required string DevicePath { get; init; }
    public string Model { get; init; } = "";
    public string? Vendor { get; init; }
    public string? SerialNumber { get; init; }
    public string? FirmwareRevision { get; init; }
    public required ulong SizeBytes { get; init; }
    public uint BytesPerLogicalSector { get; init; } = 512;
    public uint BytesPerPhysicalSector { get; init; } = 512;
    public PartitionStyle Style { get; init; } = PartitionStyle.Unknown;
    public Guid? GptDiskId { get; init; }
    public uint? MbrSignature { get; init; }
    /// <summary>First usable byte (GPT) — partitions cannot start before this.</summary>
    public ulong UsableStartBytes { get; init; }
    /// <summary>One past the last usable byte (GPT backup header excluded).</summary>
    public ulong UsableEndBytes { get; init; }
    public MediaKind Media { get; init; }
    public BusKind Bus { get; init; }
    public bool IsRemovable { get; init; }
    public bool? TrimEnabled { get; init; }
    /// <summary>Holds the running Windows installation.</summary>
    public bool IsWindowsDisk { get; init; }
    /// <summary>Holds the partition firmware boots from (ESP / active partition), per Windows Setup records.</summary>
    public bool IsBootDisk { get; init; }
    public bool IsDynamic { get; init; }
    public HealthState Health { get; init; } = HealthState.Unknown;
    public IReadOnlyList<PartitionInfo> Partitions { get; init; } = Array.Empty<PartitionInfo>();
    /// <summary>Non-fatal problems hit while reading this disk (shown to the user, never hidden).</summary>
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();

    public bool IsVirtual => Bus is BusKind.FileBackedVirtual or BusKind.Virtual;
    public bool IsExternal => Bus is BusKind.Usb or BusKind.Ieee1394 || (IsRemovable && Bus != BusKind.Nvme);

    public ulong AllocatedBytes => Partitions.Where(p => p.Role != PartitionRole.MbrExtended)
                                             .Aggregate(0UL, (s, p) => s + p.SizeBytes);
}

public enum FirmwareKind { Unknown, Bios, Uefi }

public sealed record StorageInventory
{
    public required DateTimeOffset CapturedAt { get; init; }
    public required IReadOnlyList<DiskInfo> Disks { get; init; }
    public FirmwareKind Firmware { get; init; }
    public bool IsElevated { get; init; }
    /// <summary>True only for the labelled sample provider used for UI development (--demo).</summary>
    public bool IsSampleData { get; init; }
    /// <summary>Volumes that could not be mapped to a physical disk (e.g. network-backed, spanned).</summary>
    public IReadOnlyList<VolumeInfo> UnmappedVolumes { get; init; } = Array.Empty<VolumeInfo>();
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
}

/// <summary>Why a volume cannot be read — decides what advice is correct (chkdsk cannot repair a RAW volume, for instance).</summary>
public enum VolumeProblem { Unknown, NotRecognized, Damaged, Locked, NoMedia, ReadErrors }

public static class VolumeProblems
{
    public static VolumeProblem Classify(int? code) => code switch
    {
        1005 => VolumeProblem.NotRecognized,                 // ERROR_UNRECOGNIZED_VOLUME: RAW
        1392 or 1393 => VolumeProblem.Damaged,               // ERROR_FILE_CORRUPT, ERROR_DISK_CORRUPT
        unchecked((int)0x80310000) => VolumeProblem.Locked,  // FVE_E_LOCKED_VOLUME (BitLocker)
        21 or 1112 => VolumeProblem.NoMedia,                 // ERROR_NOT_READY, ERROR_NO_MEDIA_IN_DRIVE
        23 or 483 or 1117 => VolumeProblem.ReadErrors,       // ERROR_CRC, ERROR_DEVICE_HARDWARE_ERROR, ERROR_IO_DEVICE
        _ => VolumeProblem.Unknown,
    };
}
