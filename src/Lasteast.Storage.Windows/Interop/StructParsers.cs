using System.Buffers.Binary;
using System.Text;
using Lasteast.Core.Models;

namespace Lasteast.Storage.Windows.Interop;

/// <summary>
/// Decodes the raw buffers returned by storage IOCTLs using explicit x64 offsets
/// (documented next to each field). Kept free of P/Invoke so it can be unit-tested
/// on any OS with synthetic buffers.
/// </summary>
internal static class StructParsers
{
    // ---- DRIVE_LAYOUT_INFORMATION_EX --------------------------------------------------------
    //  0  DWORD PartitionStyle
    //  4  DWORD PartitionCount
    //  8  union { MBR {DWORD Signature; DWORD CheckSum}  GPT {GUID DiskId; LONGLONG StartingUsableOffset;
    //             LONGLONG UsableLength; DWORD MaxPartitionCount} }  (40 bytes incl. padding)
    // 48  PARTITION_INFORMATION_EX PartitionEntry[PartitionCount]   (144 bytes each)
    public const int LayoutHeaderSize = 48;
    public const int PartitionEntrySize = 144;

    // ---- PARTITION_INFORMATION_EX ------------------------------------------------------------
    //  0  DWORD PartitionStyle
    //  8  LONGLONG StartingOffset
    // 16  LONGLONG PartitionLength
    // 24  DWORD PartitionNumber
    // 28  BOOLEAN RewritePartition
    // 29  BOOLEAN IsServicePartition
    // 32  union { MBR {BYTE PartitionType; BOOLEAN BootIndicator; BOOLEAN RecognizedPartition; DWORD HiddenSectors@+4; GUID PartitionId@+8}
    //             GPT {GUID PartitionType; GUID PartitionId@+16; DWORD64 Attributes@+32; WCHAR Name[36]@+40} }

    public sealed record RawLayout(
        PartitionStyle Style,
        uint? MbrSignature,
        Guid? GptDiskId,
        ulong GptUsableStart,
        ulong GptUsableLength,
        IReadOnlyList<RawPartition> Partitions);

    public sealed record RawPartition(
        PartitionStyle Style,
        ulong Offset,
        ulong Length,
        int Number,
        bool IsServicePartition,
        byte MbrType,
        bool MbrBoot,
        bool MbrRecognized,
        Guid GptType,
        Guid GptId,
        ulong GptAttributes,
        string GptName);

    public static RawLayout ParseDriveLayout(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < LayoutHeaderSize) throw new FormatException("Drive layout buffer is too small.");

        var style = ToStyle(BinaryPrimitives.ReadUInt32LittleEndian(buf));
        var count = (int)BinaryPrimitives.ReadUInt32LittleEndian(buf[4..]);
        if (count < 0 || count > 4096) throw new FormatException($"Implausible partition count {count}.");
        if (buf.Length < LayoutHeaderSize + (long)count * PartitionEntrySize)
            throw new FormatException("Drive layout buffer is truncated.");

        uint? sig = null;
        Guid? diskId = null;
        ulong usableStart = 0, usableLength = 0;
        if (style == PartitionStyle.Mbr)
        {
            sig = BinaryPrimitives.ReadUInt32LittleEndian(buf[8..]);
        }
        else if (style == PartitionStyle.Gpt)
        {
            diskId = new Guid(buf.Slice(8, 16));
            usableStart = BinaryPrimitives.ReadUInt64LittleEndian(buf[24..]);
            usableLength = BinaryPrimitives.ReadUInt64LittleEndian(buf[32..]);
        }

        var parts = new List<RawPartition>(count);
        for (int i = 0; i < count; i++)
        {
            var e = buf.Slice(LayoutHeaderSize + i * PartitionEntrySize, PartitionEntrySize);
            var pStyle = ToStyle(BinaryPrimitives.ReadUInt32LittleEndian(e));
            var offset = BinaryPrimitives.ReadUInt64LittleEndian(e[8..]);
            var length = BinaryPrimitives.ReadUInt64LittleEndian(e[16..]);
            var number = (int)BinaryPrimitives.ReadUInt32LittleEndian(e[24..]);
            var service = e[29] != 0;
            var u = e[32..];

            if (pStyle == PartitionStyle.Mbr)
            {
                var type = u[0];
                // Windows reports all 4 primary slots on MBR (plus 4 per EBR); empty slots have type 0 and length 0.
                if (type == 0 && length == 0) continue;
                parts.Add(new RawPartition(pStyle, offset, length, number, service, type, u[1] != 0, u[2] != 0,
                    Guid.Empty, Guid.Empty, 0, ""));
            }
            else if (pStyle == PartitionStyle.Gpt)
            {
                var name = Encoding.Unicode.GetString(u.Slice(40, 72)).TrimEnd('\0');
                int nul = name.IndexOf('\0');
                if (nul >= 0) name = name[..nul];
                parts.Add(new RawPartition(pStyle, offset, length, number, service, 0, false, true,
                    new Guid(u[..16]), new Guid(u.Slice(16, 16)), BinaryPrimitives.ReadUInt64LittleEndian(u[32..]), name));
            }
        }

        return new RawLayout(style, sig, diskId, usableStart, usableLength, parts);
    }

    private static PartitionStyle ToStyle(uint v) => v switch
    {
        0 => PartitionStyle.Mbr,
        1 => PartitionStyle.Gpt,
        2 => PartitionStyle.Raw,
        _ => PartitionStyle.Unknown,
    };

    // ---- DISK_GEOMETRY_EX --------------------------------------------------------------------
    //  0 LARGE_INTEGER Cylinders; 8 MEDIA_TYPE; 12 TracksPerCylinder; 16 SectorsPerTrack; 20 BytesPerSector
    // 24 LARGE_INTEGER DiskSize
    public static (ulong DiskSize, uint BytesPerSector, uint MediaType) ParseGeometryEx(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < 32) throw new FormatException("Geometry buffer is too small.");
        return (BinaryPrimitives.ReadUInt64LittleEndian(buf[24..]),
                BinaryPrimitives.ReadUInt32LittleEndian(buf[20..]),
                BinaryPrimitives.ReadUInt32LittleEndian(buf[8..]));
    }

    // ---- STORAGE_DEVICE_DESCRIPTOR -----------------------------------------------------------
    //  0 Version; 4 Size; 8 DeviceType; 9 DeviceTypeModifier; 10 RemovableMedia; 11 CommandQueueing
    // 12 VendorIdOffset; 16 ProductIdOffset; 20 ProductRevisionOffset; 24 SerialNumberOffset
    // 28 STORAGE_BUS_TYPE BusType; 32 RawPropertiesLength
    public sealed record DeviceDescriptor(string? Vendor, string? Product, string? Revision, string? Serial, bool Removable, BusKind Bus);

    public static DeviceDescriptor ParseDeviceDescriptor(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < 32) throw new FormatException("Device descriptor buffer is too small.");
        static string? Str(ReadOnlySpan<byte> buf, int fieldOffset)
        {
            var off = (int)BinaryPrimitives.ReadUInt32LittleEndian(buf[fieldOffset..]);
            if (off <= 0 || off >= buf.Length) return null;
            var s = buf[off..];
            var end = s.IndexOf((byte)0);
            if (end < 0) end = s.Length;
            var text = Encoding.ASCII.GetString(s[..end]).Trim();
            return text.Length == 0 ? null : text;
        }

        var bus = BinaryPrimitives.ReadUInt32LittleEndian(buf[28..]);
        return new DeviceDescriptor(Str(buf, 12), Str(buf, 16), Str(buf, 20), Str(buf, 24), buf[10] != 0,
            Enum.IsDefined(typeof(BusKind), (int)bus) ? (BusKind)bus : BusKind.Unknown);
    }

    // ---- DEVICE_SEEK_PENALTY_DESCRIPTOR / DEVICE_TRIM_DESCRIPTOR: 0 Version; 4 Size; 8 BOOLEAN
    public static bool? ParseBooleanDescriptor(ReadOnlySpan<byte> buf) => buf.Length >= 9 ? buf[8] != 0 : null;

    // ---- STORAGE_ACCESS_ALIGNMENT_DESCRIPTOR: 16 BytesPerLogicalSector; 20 BytesPerPhysicalSector
    public static (uint Logical, uint Physical)? ParseAlignment(ReadOnlySpan<byte> buf) =>
        buf.Length >= 24
            ? (BinaryPrimitives.ReadUInt32LittleEndian(buf[16..]), BinaryPrimitives.ReadUInt32LittleEndian(buf[20..]))
            : null;

    // ---- VOLUME_DISK_EXTENTS: 0 DWORD Count; 8 DISK_EXTENT[] {0 DiskNumber; 8 StartingOffset; 16 ExtentLength} (24 each)
    public static IReadOnlyList<(int Disk, ulong Offset, ulong Length)> ParseVolumeExtents(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < 8) return Array.Empty<(int, ulong, ulong)>();
        var count = (int)BinaryPrimitives.ReadUInt32LittleEndian(buf);
        var list = new List<(int, ulong, ulong)>();
        for (int i = 0; i < count && 8 + (i + 1) * 24 <= buf.Length; i++)
        {
            var e = buf.Slice(8 + i * 24, 24);
            list.Add(((int)BinaryPrimitives.ReadUInt32LittleEndian(e),
                      BinaryPrimitives.ReadUInt64LittleEndian(e[8..]),
                      BinaryPrimitives.ReadUInt64LittleEndian(e[16..])));
        }
        return list;
    }

    /// <summary>STORAGE_PROPERTY_QUERY { DWORD PropertyId; DWORD QueryType = PropertyStandardQuery; BYTE AdditionalParameters[1] }.</summary>
    public static byte[] PropertyQuery(int propertyId)
    {
        var q = new byte[12];
        BinaryPrimitives.WriteInt32LittleEndian(q, propertyId);
        return q;
    }

    public const int StorageDeviceProperty = 0;
    public const int StorageAccessAlignmentProperty = 6;
    public const int StorageDeviceSeekPenaltyProperty = 7;
    public const int StorageDeviceTrimProperty = 8;
}
