using System.Buffers.Binary;
using Lasteast.Core.PartitionTables;
using Lasteast.Core.RawDisk;

namespace Lasteast.Core.Recovery;

public enum DiskImageKind { Raw, FixedVhd, NeedsAttach }

/// <summary>A region of a disk image to scan: a partition inside it, the whole image, or the image itself when it holds one volume.</summary>
public sealed record ImageRegion(long Offset, long Length, string FileSystem, int? PartitionNumber);

/// <summary>
/// Disk-image files as recovery sources — the safest way to recover from a failing disk is to image it once and work on
/// the copy. Raw images (.img/.dd/.raw/.bin) and fixed VHDs are read directly; VHDX and dynamic VHD are block-mapped
/// formats that Windows attaches read-only instead. Images are always opened read-only.
/// </summary>
public static class DiskImages
{
    public const string FileFilter = "Disk images|*.img;*.dd;*.raw;*.bin;*.ima;*.vhd;*.vhdx|All files|*.*";

    public static DiskImageKind Classify(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var head = new byte[8];
        if (fs.Length >= 8) { fs.ReadExactly(head); if (head.AsSpan().SequenceEqual("vhdxfile"u8)) return DiskImageKind.NeedsAttach; }
        if (fs.Length >= 512 + 512)
        {
            var foot = new byte[512];
            fs.Position = fs.Length - 512;
            fs.ReadExactly(foot);
            if (foot.AsSpan(0, 8).SequenceEqual("conectix"u8))
                return BinaryPrimitives.ReadUInt32BigEndian(foot.AsSpan(0x3C)) == 2 ? DiskImageKind.FixedVhd : DiskImageKind.NeedsAttach;
        }
        return DiskImageKind.Raw;
    }

    /// <summary>Read-only device over a raw image or fixed VHD (the VHD footer is excluded). Throws for formats that need attaching.</summary>
    public static IBlockDevice Open(string path)
    {
        var kind = Classify(path);
        if (kind == DiskImageKind.NeedsAttach) throw new RawOperationException("Rec.Image.NeedsAttach", path);
        var file = new FileBlockDevice(path, 512, writable: false);
        return new OffsetDevice(file, 0, kind == DiskImageKind.FixedVhd ? file.Length - 512 : file.Length, ownsInner: true); // read-only window
    }

    /// <summary>Partitions inside the image (MBR or GPT), or the image itself when it starts with a volume boot sector.</summary>
    public static IReadOnlyList<ImageRegion> Regions(IBlockDevice dev)
    {
        var list = new List<ImageRegion>();
        var head = new byte[Math.Min(dev.Length, 34 * 512)];
        dev.Read(0, head);
        string Fs(long off)
        {
            if (off < 0 || off + 512 > dev.Length) return "";
            var bs = new byte[512];
            dev.Read(off, bs);
            return bs.AsSpan(3, 8).SequenceEqual("NTFS    "u8) ? "NTFS" : bs.AsSpan(3, 8).SequenceEqual("EXFAT   "u8) ? "exFAT"
                : bs.AsSpan(82, 8).SequenceEqual("FAT32   "u8) ? "FAT32" : "";
        }
        var self = Fs(0);
        if (self.Length > 0) return new[] { new ImageRegion(0, dev.Length, self, null) };
        if (head.Length >= 512 && head[510] == 0x55 && head[511] == 0xAA)
        {
            try
            {
                var mbr = PartitionTableCodec.ReadMbr(head.AsSpan(0, 512));
                if (mbr.IsProtective && head.Length >= 34 * 512)
                {
                    var gpt = PartitionTableCodec.ReadGpt(head, 512);
                    int n = 0;
                    foreach (var e in gpt.Entries)
                    {
                        n++;
                        long off = (long)e.FirstLba * 512, len = (long)(e.LastLba - e.FirstLba + 1) * 512;
                        if (off + len <= dev.Length) list.Add(new ImageRegion(off, len, Fs(off), n));
                    }
                }
                else
                {
                    int n = 0;
                    foreach (var e in mbr.Entries)
                    {
                        n++;
                        if (e.IsEmpty || e.Type is 0x05 or 0x0F or 0x85) continue;
                        long off = (long)e.FirstLba * 512, len = Math.Min((long)e.Sectors * 512, dev.Length - off);
                        if (len > 0) list.Add(new ImageRegion(off, len, Fs(off), n));
                    }
                }
            }
            catch (PartitionTableException) { }
        }
        list.Add(new ImageRegion(0, dev.Length, "", null)); // whole image, for searching by content
        return list;
    }
}
