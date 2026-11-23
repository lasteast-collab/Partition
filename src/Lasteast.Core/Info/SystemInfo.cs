namespace Lasteast.Core.Info;

public enum BitLockerProtection { Unknown, Off, On }

public sealed record BitLockerVolume(string VolumeGuidPath, char? DriveLetter, BitLockerProtection Protection, string Conversion, bool? Locked, string? Method, int? PercentEncrypted);

public sealed record StoragePoolInfo(string Name, string Health, ulong Size, ulong Allocated, int PhysicalDisks, bool ReadOnly);
public sealed record StorageSpaceInfo(string Name, string Resiliency, ulong Size, string Health, string Operational);
public sealed record StorageSpacesInfo(IReadOnlyList<StoragePoolInfo> Pools, IReadOnlyList<StorageSpaceInfo> Spaces);

public enum ShrinkBlockerKind { PageFile, Hibernation, SwapFile, ShadowCopies, Other }

public sealed record ShrinkBlocker(ShrinkBlockerKind Kind, ulong Bytes, string? Path, string AdviceKey);

/// <summary>Why a volume cannot shrink further: Windows' own limit and the usual immovable files.</summary>
public sealed record ShrinkReport(char Letter, ulong CurrentSize, ulong? WindowsMinimumSize, ulong UsedBytes, IReadOnlyList<ShrinkBlocker> Blockers);

/// <summary>Read-only system information beyond the disk inventory (Windows: WMI).</summary>
public interface ISystemInfoProvider
{
    Task<IReadOnlyList<BitLockerVolume>> BitLockerAsync();
    Task<StorageSpacesInfo> StorageSpacesAsync();
    Task<ShrinkReport?> ShrinkAsync(Models.DiskInfo disk, Models.PartitionInfo partition);
}

public sealed class NullSystemInfoProvider : ISystemInfoProvider
{
    public Task<IReadOnlyList<BitLockerVolume>> BitLockerAsync() => Task.FromResult<IReadOnlyList<BitLockerVolume>>(Array.Empty<BitLockerVolume>());
    public Task<StorageSpacesInfo> StorageSpacesAsync() => Task.FromResult(new StorageSpacesInfo(Array.Empty<StoragePoolInfo>(), Array.Empty<StorageSpaceInfo>()));
    public Task<ShrinkReport?> ShrinkAsync(Models.DiskInfo disk, Models.PartitionInfo partition) => Task.FromResult<ShrinkReport?>(null);
}

/// <summary>VHD/VHDX management (Windows: virtdisk.dll).</summary>
public interface IVirtualDiskManager
{
    void Create(string path, ulong sizeBytes, bool dynamic);
    /// <summary>Returns the device path of the attached disk (\\.\PhysicalDriveN).</summary>
    string Attach(string path);
    void Detach(string path);
    string? BackingFile(string devicePath);
}
