using System.Buffers.Binary;
using System.Text;
using Lasteast.Core.Models;
using Lasteast.Storage.Windows.Detection;
using Lasteast.Storage.Windows.Interop;

namespace Lasteast.Tests;

/// <summary>
/// Builds synthetic IOCTL buffers with the documented x64 layouts and checks the parsers.
/// These offsets are the riskiest part of the detection layer, so they are pinned by tests.
/// </summary>
public static class NativeParserTests
{
    private delegate void SpanWriter(Span<byte> span);

    private static byte[] Layout(uint style, IReadOnlyList<SpanWriter> entries, SpanWriter? header = null)
    {
        var buf = new byte[StructParsers.LayoutHeaderSize + entries.Count * StructParsers.PartitionEntrySize];
        BinaryPrimitives.WriteUInt32LittleEndian(buf, style);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4), (uint)entries.Count);
        header?.Invoke(buf);
        for (int i = 0; i < entries.Count; i++)
            entries[i](buf.AsSpan(StructParsers.LayoutHeaderSize + i * StructParsers.PartitionEntrySize, StructParsers.PartitionEntrySize));
        return buf;
    }

    private static SpanWriter GptEntry(ulong off, ulong len, int number, Guid type, Guid id, ulong attrs, string name) => e =>
    {
        BinaryPrimitives.WriteUInt32LittleEndian(e, 1);
        BinaryPrimitives.WriteUInt64LittleEndian(e[8..], off);
        BinaryPrimitives.WriteUInt64LittleEndian(e[16..], len);
        BinaryPrimitives.WriteUInt32LittleEndian(e[24..], (uint)number);
        type.TryWriteBytes(e.Slice(32, 16));
        id.TryWriteBytes(e.Slice(48, 16));
        BinaryPrimitives.WriteUInt64LittleEndian(e[64..], attrs);
        Encoding.Unicode.GetBytes(name).CopyTo(e[72..]);
    };

    private static SpanWriter MbrEntry(ulong off, ulong len, int number, byte type, bool boot = false) => e =>
    {
        BinaryPrimitives.WriteUInt32LittleEndian(e, 0);
        BinaryPrimitives.WriteUInt64LittleEndian(e[8..], off);
        BinaryPrimitives.WriteUInt64LittleEndian(e[16..], len);
        BinaryPrimitives.WriteUInt32LittleEndian(e[24..], (uint)number);
        e[32] = type;
        e[33] = (byte)(boot ? 1 : 0);
        e[34] = 1;
    };

    [Test]
    public static void Parses_gpt_layout()
    {
        var diskId = Guid.NewGuid();
        var espId = Guid.NewGuid();
        var buf = Layout(1, new[]
        {
            GptEntry(1 << 20, 100 << 20, 1, PartitionTypes.GptEfiSystem, espId, 0x8000000000000001, "EFI system partition"),
            GptEntry(117 << 20, 500UL << 30, 3, PartitionTypes.GptBasicData, Guid.NewGuid(), 0, "Basic data partition"),
        }, h =>
        {
            diskId.TryWriteBytes(h.Slice(8, 16));
            BinaryPrimitives.WriteUInt64LittleEndian(h[24..], 17408);
            BinaryPrimitives.WriteUInt64LittleEndian(h[32..], 999_000_000);
        });

        var l = StructParsers.ParseDriveLayout(buf);
        Assert.Equal(PartitionStyle.Gpt, l.Style);
        Assert.Equal(diskId, l.GptDiskId);
        Assert.Equal(17408UL, l.GptUsableStart);
        Assert.Equal(999_000_000UL, l.GptUsableLength);
        Assert.Equal(2, l.Partitions.Count);
        Assert.Equal(espId, l.Partitions[0].GptId);
        Assert.Equal(PartitionTypes.GptEfiSystem, l.Partitions[0].GptType);
        Assert.Equal("EFI system partition", l.Partitions[0].GptName);
        Assert.Equal(0x8000000000000001UL, l.Partitions[0].GptAttributes);
        Assert.Equal(3, l.Partitions[1].Number);
        Assert.Equal(500UL << 30, l.Partitions[1].Length);
    }

    [Test]
    public static void Parses_mbr_layout_skipping_empty_slots()
    {
        var buf = Layout(0, new[]
        {
            MbrEntry(1 << 20, 10UL << 30, 1, 0x07, boot: true),
            MbrEntry((10UL << 30) + (1 << 20), 20UL << 30, 0, 0x0F),
            MbrEntry(0, 0, 0, 0x00),
            MbrEntry(0, 0, 0, 0x00),
            MbrEntry((10UL << 30) + (2 << 20), 5UL << 30, 2, 0x07),
            MbrEntry(0, 0, 0, 0x00),
        }, h => BinaryPrimitives.WriteUInt32LittleEndian(h[8..], 0xDEADBEEF));

        var l = StructParsers.ParseDriveLayout(buf);
        Assert.Equal(PartitionStyle.Mbr, l.Style);
        Assert.Equal(0xDEADBEEFu, l.MbrSignature);
        Assert.Equal(3, l.Partitions.Count);
        Assert.True(l.Partitions[0].MbrBoot);
        Assert.Equal((byte)0x0F, l.Partitions[1].MbrType);
    }

    [Test]
    public static void Rejects_truncated_layout()
    {
        var buf = new byte[60];
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4), 3);
        Assert.Throws<FormatException>(() => StructParsers.ParseDriveLayout(buf));
    }

    [Test]
    public static void Parses_device_descriptor_strings_and_bus()
    {
        var buf = new byte[128];
        buf[10] = 1; // removable
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(12), 64);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(16), 76);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(20), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(24), 96);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(28), 7); // USB
        Encoding.ASCII.GetBytes("Kingston").CopyTo(buf, 64);
        Encoding.ASCII.GetBytes("DataTraveler 3.0   ").CopyTo(buf, 76);
        Encoding.ASCII.GetBytes("  ABC123  ").CopyTo(buf, 96);
        var d = StructParsers.ParseDeviceDescriptor(buf);
        Assert.Equal("Kingston", d.Vendor);
        Assert.Equal("DataTraveler 3.0", d.Product);
        Assert.Equal(null, d.Revision);
        Assert.Equal("ABC123", d.Serial);
        Assert.Equal(BusKind.Usb, d.Bus);
        Assert.True(d.Removable);
    }

    [Test]
    public static void Parses_geometry_alignment_extents()
    {
        var g = new byte[40];
        BinaryPrimitives.WriteUInt32LittleEndian(g.AsSpan(20), 512);
        BinaryPrimitives.WriteUInt64LittleEndian(g.AsSpan(24), 1_000_204_886_016);
        var geo = StructParsers.ParseGeometryEx(g);
        Assert.Equal(1_000_204_886_016UL, geo.DiskSize);
        Assert.Equal(512u, geo.BytesPerSector);

        var a = new byte[28];
        BinaryPrimitives.WriteUInt32LittleEndian(a.AsSpan(16), 512);
        BinaryPrimitives.WriteUInt32LittleEndian(a.AsSpan(20), 4096);
        Assert.Equal((512u, 4096u), StructParsers.ParseAlignment(a));

        var x = new byte[8 + 48];
        BinaryPrimitives.WriteUInt32LittleEndian(x, 2);
        BinaryPrimitives.WriteUInt32LittleEndian(x.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(x.AsSpan(16), 1 << 20);
        BinaryPrimitives.WriteUInt64LittleEndian(x.AsSpan(24), 4096);
        BinaryPrimitives.WriteUInt32LittleEndian(x.AsSpan(32), 2);
        var ext = StructParsers.ParseVolumeExtents(x);
        Assert.Equal(2, ext.Count);
        Assert.Equal((1, 1UL << 20, 4096UL), ext[0]);
        Assert.Equal(2, ext[1].Disk);

        Assert.Equal(true, StructParsers.ParseBooleanDescriptor(new byte[] { 0, 0, 0, 0, 0, 0, 0, 0, 1 }));
        Assert.Equal(null, StructParsers.ParseBooleanDescriptor(new byte[4]));
    }

    [Test]
    public static void Assembler_maps_volumes_logicals_and_flags()
    {
        var buf = Layout(0, new[]
        {
            MbrEntry(1 << 20, 10UL << 30, 1, 0x07, boot: true),
            MbrEntry((10UL << 30) + (1 << 20), 20UL << 30, 0, 0x0F),
            MbrEntry((10UL << 30) + (2 << 20), 5UL << 30, 2, 0x07),
        });
        var layout = StructParsers.ParseDriveLayout(buf);
        var win = new VolumeRecord(new VolumeInfo { VolumeGuidPath = "\\\\?\\Volume{a}\\", DriveLetter = 'C', IsWindowsVolume = true, IsSystemPartitionVolume = true },
            new[] { (4, 1UL << 20, 10UL << 30) });
        var other = new VolumeRecord(new VolumeInfo { VolumeGuidPath = "\\\\?\\Volume{b}\\", DriveLetter = 'D' },
            new[] { (4, (10UL << 30) + (2 << 20), 5UL << 30) });
        var foreign = new VolumeRecord(new VolumeInfo { VolumeGuidPath = "\\\\?\\Volume{c}\\", DriveLetter = 'E' },
            new[] { (5, 1UL << 20, 1UL << 30) });

        var probe = new DiskProbe(4, @"\\.\PhysicalDrive4", 40UL << 30, 512, 4096,
            new StructParsers.DeviceDescriptor("ACME", "Disk", "1.0", "SN1", false, BusKind.Sata), true, false, layout, Array.Empty<string>());
        var d = DiskAssembler.Assemble(probe, new[] { win, other, foreign });

        Assert.Equal("ACME Disk", d.Model);
        Assert.Equal(MediaKind.Hdd, d.Media);
        Assert.True(d.IsWindowsDisk);
        Assert.True(d.IsBootDisk);
        Assert.Equal('C', d.Partitions[0].Volume!.DriveLetter);
        Assert.Equal(PartitionRole.MbrExtended, d.Partitions[1].Role);
        Assert.Equal(PartitionRole.MbrLogical, d.Partitions[2].Role);
        Assert.Equal('D', d.Partitions[2].Volume!.DriveLetter);
        Assert.True(d.Partitions.All(p => p.Volume?.DriveLetter != 'E'), "volume on another disk must not be mapped here");
        Assert.True(d.Partitions[0].IsCritical);
    }

    [Test]
    public static void Assembler_reports_unreadable_table()
    {
        var probe = new DiskProbe(9, "p", 0, 512, 512, null, null, null, null, new[] { "no media" });
        var d = DiskAssembler.Assemble(probe, Array.Empty<VolumeRecord>());
        Assert.Equal("Disk 9", d.Model);
        Assert.Equal(PartitionStyle.Unknown, d.Style);
        Assert.Equal(MediaKind.Unknown, d.Media);
        Assert.True(d.Notes.Count == 2);
    }
}
