using Lasteast.Core.Abstractions;
using Lasteast.Core.Models;

namespace Lasteast.Presentation.Sample;

/// <summary>
/// Fixed, clearly-labelled sample inventory for UI development (<c>--demo</c>) and tests.
/// <see cref="StorageInventory.IsSampleData"/> is always true so the UI shows a "sample data" banner;
/// it is never used to represent the user's real disks.
/// </summary>
public sealed class SampleInventoryProvider : IStorageInventoryProvider
{
    private const ulong MiB = 1024UL * 1024;
    private const ulong GiB = 1024 * MiB;

    public Task<StorageInventory> GetInventoryAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(Create());

    public static StorageInventory Create()
    {
        // Disk 0: 1 TB NVMe, GPT, typical Windows 11 layout.
        ulong d0Size = 953_869UL * MiB;
        ulong off = MiB;
        var d0 = new List<PartitionInfo>();
        PartitionInfo Gpt(int disk, int n, ulong offset, ulong size, Guid type, string name, VolumeInfo? v = null) => new()
        {
            DiskNumber = disk, PartitionNumber = n, OffsetBytes = offset, SizeBytes = size, Style = PartitionStyle.Gpt,
            GptType = type, GptId = Guid.NewGuid(), GptName = name, Role = PartitionTypes.Classify(type), Volume = v,
            Alignment = PartitionTypes.GetAlignment(offset, 512),
            GptAttributes = type == PartitionTypes.GptWindowsRecovery ? PartitionTypes.GptAttributeRequired | PartitionTypes.GptAttributeNoDriveLetter : 0,
        };
        d0.Add(Gpt(0, 1, off, 100 * MiB, PartitionTypes.GptEfiSystem, "EFI system partition",
            new VolumeInfo { VolumeGuidPath = @"\\?\Volume{00000000-0000-0000-0000-000000000001}\", FileSystem = "FAT32",
                CapacityBytes = 96 * MiB, FreeBytes = 64 * MiB, IsSystemPartitionVolume = true, ClusterSizeBytes = 4096 }));
        off += 100 * MiB;
        d0.Add(Gpt(0, 2, off, 16 * MiB, PartitionTypes.GptMicrosoftReserved, "Microsoft reserved partition"));
        off += 16 * MiB;
        ulong cSize = d0Size - off - 750 * MiB - MiB;
        d0.Add(Gpt(0, 3, off, cSize, PartitionTypes.GptBasicData, "Basic data partition",
            new VolumeInfo { VolumeGuidPath = @"\\?\Volume{00000000-0000-0000-0000-000000000003}\", DriveLetter = 'C',
                MountPoints = new[] { @"C:\" }, Label = "Windows", FileSystem = "NTFS", CapacityBytes = cSize,
                FreeBytes = 38 * GiB, IsWindowsVolume = true, ClusterSizeBytes = 4096 }));
        off += cSize;
        d0.Add(Gpt(0, 4, off, 750 * MiB, PartitionTypes.GptWindowsRecovery, "",
            new VolumeInfo { VolumeGuidPath = @"\\?\Volume{00000000-0000-0000-0000-000000000004}\", FileSystem = "NTFS",
                CapacityBytes = 749 * MiB, FreeBytes = 120 * MiB, ClusterSizeBytes = 4096 }));

        // Disk 1: 2 TB HDD, MBR, with unallocated space between partitions.
        ulong d1Size = 1_907_729UL * MiB;
        PartitionInfo Mbr(int n, ulong offset, ulong size, byte type, VolumeInfo? v) => new()
        {
            DiskNumber = 1, PartitionNumber = n, OffsetBytes = offset, SizeBytes = size, Style = PartitionStyle.Mbr,
            MbrType = type, MbrRecognized = true, Role = PartitionTypes.Classify(type, false), Volume = v,
            Alignment = PartitionTypes.GetAlignment(offset, 4096),
        };
        var d1 = new List<PartitionInfo>
        {
            Mbr(1, MiB, 1000 * GiB, 0x07, new VolumeInfo { VolumeGuidPath = @"\\?\Volume{00000000-0000-0000-0000-000000000011}\",
                DriveLetter = 'D', MountPoints = new[] { @"D:\" }, Label = "Data", FileSystem = "NTFS",
                CapacityBytes = 1000 * GiB, FreeBytes = 412 * GiB, ClusterSizeBytes = 4096 }),
            Mbr(2, MiB + 1500 * GiB, d1Size - MiB - 1500 * GiB, 0x07, new VolumeInfo { VolumeGuidPath = @"\\?\Volume{00000000-0000-0000-0000-000000000012}\",
                DriveLetter = 'E', MountPoints = new[] { @"E:\" }, Label = "Media", FileSystem = "exFAT",
                CapacityBytes = d1Size - MiB - 1500 * GiB, FreeBytes = 96 * GiB, ClusterSizeBytes = 131072 }),
        };

        // Disk 2: 32 GB USB stick, MBR, FAT32.
        ulong d2Size = 29_664UL * MiB;
        var d2 = new List<PartitionInfo>
        {
            new()
            {
                DiskNumber = 2, PartitionNumber = 1, OffsetBytes = MiB, SizeBytes = d2Size - MiB, Style = PartitionStyle.Mbr,
                MbrType = 0x0C, MbrActive = true, MbrRecognized = true, Role = PartitionRole.BasicData,
                Alignment = AlignmentStatus.Aligned1MiB,
                Volume = new VolumeInfo { VolumeGuidPath = @"\\?\Volume{00000000-0000-0000-0000-000000000021}\", DriveLetter = 'F',
                    MountPoints = new[] { @"F:\" }, Label = "USB", FileSystem = "FAT32", CapacityBytes = d2Size - MiB,
                    FreeBytes = 21 * GiB, ClusterSizeBytes = 16384 },
            },
        };

        return new StorageInventory
        {
            CapturedAt = DateTimeOffset.Now,
            IsSampleData = true,
            Firmware = FirmwareKind.Uefi,
            Disks = new[]
            {
                new DiskInfo
                {
                    Number = 0, DevicePath = @"\\.\PhysicalDrive0", Model = "Sample NVMe SSD 1TB", SerialNumber = "SAMPLE0000001234",
                    FirmwareRevision = "1.0", SizeBytes = d0Size, Style = PartitionStyle.Gpt, Media = MediaKind.Ssd, Bus = BusKind.Nvme,
                    TrimEnabled = true, IsWindowsDisk = true, IsBootDisk = true, GptDiskId = Guid.NewGuid(),
                    UsableStartBytes = 17408, UsableEndBytes = d0Size - 16896, Partitions = d0,
                },
                new DiskInfo
                {
                    Number = 1, DevicePath = @"\\.\PhysicalDrive1", Model = "Sample SATA HDD 2TB", SerialNumber = "SAMPLE0000005678",
                    SizeBytes = d1Size, BytesPerPhysicalSector = 4096, Style = PartitionStyle.Mbr, Media = MediaKind.Hdd,
                    Bus = BusKind.Sata, TrimEnabled = false, MbrSignature = 0x1A2B3C4D, Partitions = d1,
                },
                new DiskInfo
                {
                    Number = 2, DevicePath = @"\\.\PhysicalDrive2", Model = "Sample USB Flash 32GB", SizeBytes = d2Size,
                    Style = PartitionStyle.Mbr, Media = MediaKind.Ssd, Bus = BusKind.Usb, IsRemovable = true,
                    MbrSignature = 0x00C0FFEE, Partitions = d2,
                },
            },
        };
    }
}
