namespace Lasteast.Core.Models;

/// <summary>Well-known partition type identifiers and classification helpers.</summary>
public static class PartitionTypes
{
    public static readonly Guid GptEfiSystem = new("c12a7328-f81f-11d2-ba4b-00a0c93ec93b");
    public static readonly Guid GptMicrosoftReserved = new("e3c9e316-0b5c-4db8-817d-f92df00215ae");
    public static readonly Guid GptBasicData = new("ebd0a0a2-b9e5-4433-87c0-68b6b72699c7");
    public static readonly Guid GptWindowsRecovery = new("de94bba4-06d1-4d40-a16a-bfd50179d6ac");
    public static readonly Guid GptLdmMetadata = new("5808c8aa-7e8f-42e0-85d2-e1e90434cfb3");
    public static readonly Guid GptLdmData = new("af9b60a0-1431-4f62-bc68-3311714a69ad");
    public static readonly Guid GptStorageSpaces = new("e75caf8f-f680-4cee-afa3-b001e56efc2d");
    public static readonly Guid GptLinuxFilesystem = new("0fc63daf-8483-4772-8e79-3d69d8477de4");
    public static readonly Guid GptLinuxSwap = new("0657fd6d-a4ab-43c4-84e5-0933c84b4f4f");
    public static readonly Guid GptLinuxLvm = new("e6d6d379-f507-44c2-a23c-238f2a3df928");
    public static readonly Guid GptAppleHfs = new("48465300-0000-11aa-aa11-00306543ecac");
    public static readonly Guid GptAppleApfs = new("7c3457ef-0000-11aa-aa11-00306543ecac");

    /// <summary>GPT attribute: platform required (bit 0).</summary>
    public const ulong GptAttributeRequired = 0x0000000000000001UL;
    /// <summary>Basic data attribute: no drive letter (bit 63).</summary>
    public const ulong GptAttributeNoDriveLetter = 0x8000000000000000UL;
    /// <summary>Basic data attribute: hidden (bit 62).</summary>
    public const ulong GptAttributeHidden = 0x4000000000000000UL;
    /// <summary>Basic data attribute: shadow copy (bit 61).</summary>
    public const ulong GptAttributeShadowCopy = 0x2000000000000000UL;
    /// <summary>Basic data attribute: read-only (bit 60).</summary>
    public const ulong GptAttributeReadOnly = 0x1000000000000000UL;

    public static bool IsMbrExtended(byte type) => type is 0x05 or 0x0F or 0x85;

    public static bool IsMbrHiddenType(byte type) => type is 0x11 or 0x14 or 0x16 or 0x17 or 0x1B or 0x1C or 0x1E;

    public static PartitionRole Classify(Guid gptType) => gptType switch
    {
        _ when gptType == GptEfiSystem => PartitionRole.EfiSystem,
        _ when gptType == GptMicrosoftReserved => PartitionRole.MicrosoftReserved,
        _ when gptType == GptBasicData => PartitionRole.BasicData,
        _ when gptType == GptWindowsRecovery => PartitionRole.Recovery,
        _ when gptType == GptLdmMetadata => PartitionRole.LdmMetadata,
        _ when gptType == GptLdmData => PartitionRole.LdmData,
        _ when gptType == GptStorageSpaces => PartitionRole.StorageSpaces,
        _ when gptType == GptLinuxFilesystem || gptType == GptLinuxLvm => PartitionRole.Linux,
        _ when gptType == GptLinuxSwap => PartitionRole.LinuxSwap,
        _ when gptType == GptAppleHfs || gptType == GptAppleApfs => PartitionRole.AppleHfs,
        _ when gptType == Guid.Empty => PartitionRole.Unknown,
        _ => PartitionRole.Other,
    };

    public static PartitionRole Classify(byte mbrType, bool insideExtended) => mbrType switch
    {
        _ when IsMbrExtended(mbrType) => PartitionRole.MbrExtended,
        0xEF => PartitionRole.EfiSystem,
        0x27 => PartitionRole.Recovery,
        0x42 => PartitionRole.LdmData,
        0x83 => PartitionRole.Linux,
        0x82 => PartitionRole.LinuxSwap,
        0xAF => PartitionRole.AppleHfs,
        0x01 or 0x04 or 0x06 or 0x07 or 0x0B or 0x0C or 0x0E
            or 0x11 or 0x14 or 0x16 or 0x17 or 0x1B or 0x1C or 0x1E
            => insideExtended ? PartitionRole.MbrLogical : PartitionRole.BasicData,
        0x00 => PartitionRole.Unknown,
        _ => insideExtended ? PartitionRole.MbrLogical : PartitionRole.Other,
    };

    public static AlignmentStatus GetAlignment(ulong offsetBytes, uint physicalSectorBytes)
    {
        const ulong OneMiB = 1024 * 1024;
        if (offsetBytes % OneMiB == 0) return AlignmentStatus.Aligned1MiB;
        var phys = Math.Max(physicalSectorBytes, 4096u); // treat 512e/AF disks as 4K for alignment purposes
        return offsetBytes % phys == 0 ? AlignmentStatus.AlignedPhysical : AlignmentStatus.Misaligned;
    }
}
