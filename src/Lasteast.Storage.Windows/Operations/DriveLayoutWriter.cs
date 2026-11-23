using System.Buffers.Binary;
using System.Runtime.Versioning;
using System.Text;
using Lasteast.Core.Boot;
using Lasteast.Core.Models;
using Lasteast.Core.PartitionTables;
using Lasteast.Core.RawDisk;
using Lasteast.Storage.Windows.Interop;
using Microsoft.Win32.SafeHandles;

namespace Lasteast.Storage.Windows.Operations;

/// <summary>
/// Writes a partition table through Windows (IOCTL_DISK_SET_DRIVE_LAYOUT_EX) instead of raw sectors, so the partition
/// manager's in-memory copy and the disk change together and Windows cannot later write its stale copy back.
/// </summary>
[SupportedOSPlatform("windows")]
public static class DriveLayoutWriter
{
    private const uint IOCTL_DISK_SET_DRIVE_LAYOUT_EX = 0x0007C054;
    private const int Header = 48, Entry = 144;

    /// <summary>True when the backup can be applied this way (GPT, or MBR with primary partitions only).</summary>
    public static bool Supports(TableBackup b) => b.Style == PartitionStyle.Gpt || (b.Style == PartitionStyle.Mbr && b.Ebrs.Count == 0);

    public static byte[] Build(TableBackup b, int ss)
    {
        if (b.Style == PartitionStyle.Gpt)
        {
            var gpt = PartitionTableCodec.ReadGpt(b.Head.AsSpan(0, (2 + PartitionTableCodec.EntryArraySectors(ss)) * ss).ToArray(), ss);
            var buf = new byte[Header + gpt.Entries.Count * Entry];
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0), 1);
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4), (uint)gpt.Entries.Count);
            gpt.DiskGuid.TryWriteBytes(buf.AsSpan(8));
            BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(24), (long)gpt.FirstUsableLba * ss);
            BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(32), (long)(gpt.LastUsableLba - gpt.FirstUsableLba + 1) * ss);
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(40), (uint)gpt.EntryCount);
            for (int i = 0; i < gpt.Entries.Count; i++)
            {
                var e = gpt.Entries[i];
                var s = buf.AsSpan(Header + i * Entry, Entry);
                BinaryPrimitives.WriteUInt32LittleEndian(s, 1);
                BinaryPrimitives.WriteInt64LittleEndian(s[8..], (long)e.FirstLba * ss);
                BinaryPrimitives.WriteInt64LittleEndian(s[16..], (long)e.Sectors * ss);
                BinaryPrimitives.WriteUInt32LittleEndian(s[24..], (uint)(i + 1));
                s[28] = 1; // RewritePartition
                e.Type.TryWriteBytes(s[32..]);
                e.Id.TryWriteBytes(s[48..]);
                BinaryPrimitives.WriteUInt64LittleEndian(s[64..], e.Attributes);
                var name = Encoding.Unicode.GetBytes(e.Name.Length > 36 ? e.Name[..36] : e.Name);
                name.CopyTo(s[72..]);
            }
            return buf;
        }
        else
        {
            var mbr = PartitionTableCodec.ReadMbr(b.Head.AsSpan(0, ss).ToArray());
            var entries = mbr.Entries.Where(e => !e.IsEmpty).ToList();
            if (entries.Any(e => PartitionTypes.IsMbrExtended(e.Type))) throw new RawOperationException("Restore.Error.Extended", "extended partitions");
            var buf = new byte[Header + 4 * Entry];
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0), 0);
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4), 4);
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(8), mbr.Signature);
            for (int i = 0; i < 4; i++)
            {
                var s = buf.AsSpan(Header + i * Entry, Entry);
                s[28] = 1;
                if (i >= entries.Count) continue;
                var e = entries[i];
                BinaryPrimitives.WriteInt64LittleEndian(s[8..], (long)e.FirstLba * ss);
                BinaryPrimitives.WriteInt64LittleEndian(s[16..], (long)e.Sectors * ss);
                BinaryPrimitives.WriteUInt32LittleEndian(s[24..], (uint)(i + 1));
                s[32] = e.Type;
                s[33] = (byte)(e.Active ? 1 : 0);
                s[34] = 1;
                BinaryPrimitives.WriteUInt32LittleEndian(s[36..], e.FirstLba);
            }
            return buf;
        }
    }

    public static void Apply(DiskInfo disk, TableBackup b)
    {
        var buf = Build(b, (int)disk.BytesPerLogicalSector);
        using SafeFileHandle h = RawNative.Open(disk.DevicePath, write: true);
        if (!NativeMethods.DeviceIoControl(h, IOCTL_DISK_SET_DRIVE_LAYOUT_EX, buf, buf.Length, null, 0, out _, IntPtr.Zero))
            throw new RawOperationException("Raw.Error.IO", NativeMethods.LastError("IOCTL_DISK_SET_DRIVE_LAYOUT_EX").Message);
        RawNative.Control(h, RawNative.IOCTL_DISK_UPDATE_PROPERTIES);
    }
}
