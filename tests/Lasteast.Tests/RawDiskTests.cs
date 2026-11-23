using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using Lasteast.Core.Models;
using Lasteast.Core.PartitionTables;
using Lasteast.Core.RawDisk;

namespace Lasteast.Tests;

/// <summary>Builds disk images with real NTFS/FAT32 volumes from tests/fixtures.</summary>
internal sealed class DiskImage : IDisposable
{
    public const long MiB = 1024 * 1024;
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "lasteast-img-" + Guid.NewGuid().ToString("N") + ".img");

    public DiskImage(long sizeMb)
    {
        using var fs = new FileStream(Path, FileMode.CreateNew);
        fs.SetLength(sizeMb * MiB);
    }

    public static string Fixtures
    {
        get
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d is not null && !Directory.Exists(System.IO.Path.Combine(d.FullName, "tests", "fixtures"))) d = d.Parent;
            return System.IO.Path.Combine(d!.FullName, "tests", "fixtures");
        }
    }

    public static byte[] Fixture(string gzName)
    {
        using var gz = new GZipStream(File.OpenRead(System.IO.Path.Combine(Fixtures, gzName)), CompressionMode.Decompress);
        using var ms = new MemoryStream();
        gz.CopyTo(ms);
        return ms.ToArray();
    }

    public FileBlockDevice Open() => new(Path);

    public void Put(long offset, byte[] data)
    {
        using var fs = new FileStream(Path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        fs.Position = offset;
        fs.Write(data);
    }

    public byte[] Get(long offset, int length)
    {
        using var fs = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        fs.Position = offset;
        var b = new byte[length];
        fs.ReadExactly(b);
        return b;
    }

    /// <summary>MBR disk: NTFS at 1 MiB (24 MiB), FAT32 at 32 MiB (40 MiB).</summary>
    public static DiskImage MbrWithVolumes(long sizeMb = 96)
    {
        var img = new DiskImage(sizeMb);
        var s0 = new byte[512];
        PartitionTableCodec.WriteMbr(s0, new MbrDisk(0x12345678, new byte[440], new[]
        {
            new MbrEntry(true, 0x07, 2048, 24 * 2048),
            new MbrEntry(false, 0x0C, 32 * 2048, 40 * 2048),
        }));
        img.Put(0, s0);
        img.Put(MiB, Fixture("ntfs24.img.gz"));
        img.Put(32 * MiB, Fixture("fat32-40.img.gz"));
        return img;
    }

    /// <summary>GPT disk: NTFS at 1 MiB (24 MiB), FAT32 at 32 MiB (40 MiB).</summary>
    public static DiskImage GptWithVolumes(long sizeMb = 96)
    {
        var img = new DiskImage(sizeMb);
        var total = (ulong)(sizeMb * 2048);
        var (first, last) = PartitionTableCodec.DefaultUsable(total, 512);
        var gpt = new GptDisk(Guid.NewGuid(), first, last, 128, 128, new[]
        {
            new GptEntry(PartitionTypes.GptBasicData, Guid.NewGuid(), 2048, 2048 + 24 * 2048 - 1, 0, "NTFS data"),
            new GptEntry(PartitionTypes.GptBasicData, Guid.NewGuid(), 32 * 2048, 32 * 2048 + 40 * 2048 - 1, 0, "FAT data"),
        });
        var (head, tail, tailLba) = PartitionTableCodec.BuildGpt(gpt, total, 512);
        img.Put(0, head);
        img.Put((long)tailLba * 512, tail);
        img.Put(MiB, Fixture("ntfs24.img.gz"));
        img.Put(32 * MiB, Fixture("fat32-40.img.gz"));
        return img;
    }

    public static DiskInfo Describe(IBlockDevice dev, int number = 7)
    {
        var s0 = new byte[512];
        dev.Read(0, s0);
        if (s0[510] != 0x55 || s0[511] != 0xAA)
            return new DiskInfo { Number = number, DevicePath = "img", SizeBytes = (ulong)dev.Length, Style = PartitionStyle.Raw, Partitions = new List<PartitionInfo>() };
        var mbr = PartitionTableCodec.ReadMbr(s0);
        var parts = new List<PartitionInfo>();
        PartitionStyle style;
        if (mbr.IsProtective)
        {
            style = PartitionStyle.Gpt;
            var head = new byte[34 * 512];
            dev.Read(0, head);
            var gpt = PartitionTableCodec.ReadGpt(head, 512);
            int n = 1;
            foreach (var e in gpt.Entries)
                parts.Add(new PartitionInfo { DiskNumber = number, PartitionNumber = n++, OffsetBytes = e.FirstLba * 512, SizeBytes = e.Sectors * 512, Style = style, Role = PartitionTypes.Classify(e.Type), GptType = e.Type, GptId = e.Id });
        }
        else
        {
            style = PartitionStyle.Mbr;
            int n = 1;
            foreach (var e in mbr.Entries.Where(e => !e.IsEmpty))
                parts.Add(new PartitionInfo { DiskNumber = number, PartitionNumber = n++, OffsetBytes = (ulong)e.FirstLba * 512, SizeBytes = (ulong)e.Sectors * 512, Style = style, Role = PartitionTypes.Classify(e.Type, false), MbrType = e.Type, MbrActive = e.Active });
        }
        return new DiskInfo { Number = number, DevicePath = "img", SizeBytes = (ulong)dev.Length, Style = style, Partitions = parts };
    }

    public void Dispose() { try { File.Delete(Path); } catch { } }
}

public static class PartitionTableTests
{
    [Test]
    public static void Gpt_round_trips_and_backup_is_valid()
    {
        using var img = DiskImage.GptWithVolumes();
        using var dev = img.Open();
        var head = new byte[34 * 512];
        dev.Read(0, head);
        var gpt = PartitionTableCodec.ReadGpt(head, 512);
        Assert.Equal(2, gpt.Entries.Count);
        Assert.Equal("NTFS data", gpt.Entries[0].Name);
        Assert.Equal(34UL, gpt.FirstUsableLba);
        Assert.Equal((ulong)(96 * 2048 - 34), gpt.LastUsableLba);

        // backup header at the last LBA, entries just before it
        var tailLba = (ulong)(96 * 2048 - 33);
        var tail = new byte[33 * 512];
        dev.Read((long)tailLba * 512, tail);
        var backup = PartitionTableCodec.ReadGptHeaderAt(tail, 32 * 512, 512, tail, tailLba);
        Assert.Equal(gpt.DiskGuid, backup.DiskGuid);
        Assert.Equal(gpt.Entries.Count, backup.Entries.Count);

        head[600] ^= 0xFF; // corrupt the header
        Assert.Throws<PartitionTableException>(() => PartitionTableCodec.ReadGpt(head, 512));
        Assert.Equal(0xCBF43926u, Crc32.Compute("123456789"u8)); // standard CRC-32 check value
    }

    [Test]
    public static void Mbr_round_trips()
    {
        var s0 = new byte[512];
        var mbr = new MbrDisk(0xDEADBEEF, new byte[440], new[] { new MbrEntry(true, 0x07, 2048, 100), new MbrEntry(false, 0xEF, 4096, 200) });
        PartitionTableCodec.WriteMbr(s0, mbr);
        var back = PartitionTableCodec.ReadMbr(s0);
        Assert.Equal(0xDEADBEEFu, back.Signature);
        Assert.True(back.Entries[0].Active && back.Entries[0].FirstLba == 2048 && back.Entries[1].Type == 0xEF);
        Assert.True(back.Entries[2].IsEmpty && back.Entries[3].IsEmpty);
    }
}

public static class ConversionTests
{
    private static byte[] Hash(DiskImage img, long off, int len) => SHA256.HashData(img.Get(off, len));

    [Test]
    public static void Mbr_to_gpt_and_back_keeps_data_and_positions()
    {
        using var img = DiskImage.MbrWithVolumes();
        var ntfsHash = Hash(img, DiskImage.MiB, (int)(24 * DiskImage.MiB));
        var fatHash = Hash(img, 32 * DiskImage.MiB, (int)(40 * DiskImage.MiB));

        using (var dev = img.Open()) StyleConverter.ConvertMbrToGpt(dev);
        using (var dev = img.Open())
        {
            var d = DiskImage.Describe(dev);
            Assert.Equal(PartitionStyle.Gpt, d.Style);
            Assert.Equal(2, d.Partitions.Count);
            Assert.Equal((ulong)DiskImage.MiB, d.Partitions[0].OffsetBytes);
            Assert.Equal((ulong)(24 * DiskImage.MiB), d.Partitions[0].SizeBytes);
            Assert.Equal(PartitionTypes.GptBasicData, d.Partitions[1].GptType);
        }
        Assert.True(Hash(img, DiskImage.MiB, (int)(24 * DiskImage.MiB)).SequenceEqual(ntfsHash), "NTFS data unchanged");
        Assert.True(Hash(img, 32 * DiskImage.MiB, (int)(40 * DiskImage.MiB)).SequenceEqual(fatHash), "FAT32 data unchanged");

        using (var dev = img.Open())
            StyleConverter.ConvertGptToMbr(dev, new Dictionary<ulong, string?> { [32 * 2048] = "FAT32" }, 0xCAFEBABE);
        using (var dev = img.Open())
        {
            var s0 = new byte[512];
            dev.Read(0, s0);
            var mbr = PartitionTableCodec.ReadMbr(s0);
            Assert.True(!mbr.IsProtective);
            Assert.Equal(0xCAFEBABEu, mbr.Signature);
            Assert.Equal((byte)0x07, mbr.Entries[0].Type);
            Assert.Equal((byte)0x0C, mbr.Entries[1].Type);
            Assert.Equal(2048u, mbr.Entries[0].FirstLba);
            var hdr = new byte[512];
            dev.Read(512, hdr);
            Assert.True(hdr.All(b => b == 0), "stale primary GPT header cleared");
            dev.Read(dev.Length - 512, hdr);
            Assert.True(hdr.All(b => b == 0), "stale backup GPT header cleared");
        }
        Assert.True(Hash(img, DiskImage.MiB, (int)(24 * DiskImage.MiB)).SequenceEqual(ntfsHash));
    }

    [Test]
    public static void Conversion_refuses_when_structures_do_not_fit()
    {
        using var img = new DiskImage(64);
        var s0 = new byte[512];
        PartitionTableCodec.WriteMbr(s0, new MbrDisk(1, new byte[440], new[] { new MbrEntry(false, 0x07, 1, 1000) }));
        img.Put(0, s0);
        using (var dev = img.Open())
            Assert.Equal("Conv.Error.NoRoomStart", Assert.Throws<RawOperationException>(() => StyleConverter.PlanMbrToGpt(dev)).Key);

        PartitionTableCodec.WriteMbr(s0, new MbrDisk(1, new byte[440], new[] { new MbrEntry(false, 0x07, 2048, (uint)(64 * 2048 - 2048)) }));
        img.Put(0, s0);
        using (var dev = img.Open())
            Assert.Equal("Conv.Error.NoRoomEnd", Assert.Throws<RawOperationException>(() => StyleConverter.PlanMbrToGpt(dev)).Key);

        PartitionTableCodec.WriteMbr(s0, new MbrDisk(1, new byte[440], new[] { new MbrEntry(false, 0x05, 2048, 4096) }));
        img.Put(0, s0);
        using (var dev = img.Open())
            Assert.Equal("Conv.Error.Extended", Assert.Throws<RawOperationException>(() => StyleConverter.PlanMbrToGpt(dev)).Key);
    }
}

public static class CloneTests
{
    [Test]
    public static void Sector_clone_to_larger_disk_rewrites_gpt_and_verifies()
    {
        using var src = DiskImage.GptWithVolumes(96);
        using var dst = new DiskImage(128);
        using var s = src.Open();
        using var d = dst.Open();
        var info = DiskImage.Describe(s);
        var plan = ClonePlanner.Plan(info, d.Length, 512, CloneMode.SectorBySector, newIdentifiers: true);
        var progress = new List<RawProgress>();
        var result = Cloner.Run(s, d, plan, verify: true, new SyncProgress<RawProgress>(progress), CancellationToken.None);
        Assert.True(result.Verified && result.BytesCopied >= 64 * DiskImage.MiB);
        Assert.True(progress.Any(p => p.Phase == "Verify"));

        var head = new byte[34 * 512];
        d.Read(0, head);
        var gpt = PartitionTableCodec.ReadGpt(head, 512);
        Assert.Equal((ulong)(128 * 2048 - 34), gpt.LastUsableLba);
        var srcHead = new byte[34 * 512];
        s.Read(0, srcHead);
        var srcGpt = PartitionTableCodec.ReadGpt(srcHead, 512);
        Assert.True(gpt.DiskGuid != srcGpt.DiskGuid && gpt.Entries[0].Id != srcGpt.Entries[0].Id, "new identifiers");
        Assert.Equal(srcGpt.Entries[1].FirstLba, gpt.Entries[1].FirstLba);
        Assert.True(dst.Get(DiskImage.MiB, (int)(24 * DiskImage.MiB)).AsSpan().SequenceEqual(src.Get(DiskImage.MiB, (int)(24 * DiskImage.MiB))));
    }

    [Test]
    public static void Clone_refuses_too_small_and_mismatched_sector_size()
    {
        using var src = DiskImage.GptWithVolumes(96);
        using var s = src.Open();
        var info = DiskImage.Describe(s);
        Assert.Equal("Clone.Error.TooSmall", Assert.Throws<RawOperationException>(() => ClonePlanner.Plan(info, 64 * DiskImage.MiB, 512, CloneMode.SectorBySector, false)).Key);
        Assert.Equal("Clone.Error.SectorSize", Assert.Throws<RawOperationException>(() => ClonePlanner.Plan(info, 256 * DiskImage.MiB, 4096, CloneMode.SectorBySector, false)).Key);
        // a smaller disk works when the partitions fit
        var ok = ClonePlanner.Plan(info, 73 * DiskImage.MiB, 512, CloneMode.SectorBySector, false);
        Assert.True(ok.BytesToCopy > 0);
    }

    [Test]
    public static void Used_space_clone_copies_only_requested_ranges()
    {
        using var src = DiskImage.MbrWithVolumes(96);
        using var dst = new DiskImage(96);
        using var s = src.Open();
        using var d = dst.Open();
        var info = DiskImage.Describe(s);
        // pretend only 2 MiB at +4 MiB of the NTFS volume are used
        IReadOnlyList<ByteRange>? Used(PartitionInfo p) => p.OffsetBytes == (ulong)DiskImage.MiB
            ? new[] { new ByteRange(5 * DiskImage.MiB, 2 * DiskImage.MiB) } : null;
        var plan = ClonePlanner.Plan(info, d.Length, 512, CloneMode.UsedSpace, false, Used);
        Assert.True(plan.BytesToCopy < 50 * DiskImage.MiB, $"copied {plan.BytesToCopy}");
        Cloner.Run(s, d, plan, true, null, CancellationToken.None);
        Assert.True(dst.Get(5 * DiskImage.MiB, (int)(2 * DiskImage.MiB)).AsSpan().SequenceEqual(src.Get(5 * DiskImage.MiB, (int)(2 * DiskImage.MiB))));
        Assert.True(dst.Get(10 * DiskImage.MiB, 4096).All(b => b == 0), "unused NTFS clusters were not copied");
        Assert.True(dst.Get(32 * DiskImage.MiB, 4096).AsSpan().SequenceEqual(src.Get(32 * DiskImage.MiB, 4096)), "non-NTFS partition copied in full");
        var s0 = PartitionTableCodec.ReadMbr(dst.Get(0, 512));
        Assert.Equal(0x12345678u, s0.Signature);
    }
}

public static class MoveTests
{
    private static uint Hidden(DiskImage img, long off) => BinaryPrimitives.ReadUInt32LittleEndian(img.Get(off + 0x1C, 4));

    [Test]
    public static void Move_ntfs_right_on_gpt_updates_table_and_boot_sectors()
    {
        using var img = DiskImage.GptWithVolumes(128);
        var before = img.Get(DiskImage.MiB, (int)(24 * DiskImage.MiB));
        var journal = Path.Combine(Path.GetTempPath(), $"lasteast-move-{Guid.NewGuid():N}.json");
        using (var dev = img.Open())
        {
            var j = PartitionMover.NewJournal("img", DiskImage.MiB, 76 * DiskImage.MiB, 24 * DiskImage.MiB, 512);
            PartitionMover.Run(dev, j, journal, null, CancellationToken.None);
            var d = DiskImage.Describe(dev);
            Assert.Equal((ulong)(76 * DiskImage.MiB), d.Partitions.First(p => p.SizeBytes == (ulong)(24 * DiskImage.MiB)).OffsetBytes);
        }
        var after = img.Get(76 * DiskImage.MiB, (int)(24 * DiskImage.MiB));
        // identical apart from the hidden-sectors field in the boot sector and its backup
        Assert.Equal((uint)(76 * 2048), Hidden(img, 76 * DiskImage.MiB));
        Assert.Equal((uint)(76 * 2048), BinaryPrimitives.ReadUInt32LittleEndian(after.AsSpan((int)(24 * DiskImage.MiB) - 512 + 0x1C)));
        BinaryPrimitives.WriteUInt32LittleEndian(after.AsSpan(0x1C), 2048);
        BinaryPrimitives.WriteUInt32LittleEndian(after.AsSpan((int)(24 * DiskImage.MiB) - 512 + 0x1C), 2048);
        Assert.True(after.AsSpan().SequenceEqual(before), "volume content preserved");
        Assert.True(MoveJournal.Load(journal)!.TableUpdated);
        File.Delete(journal);
    }

    [Test]
    public static void Interrupted_move_left_resumes_from_journal_on_mbr()
    {
        using var img = DiskImage.MbrWithVolumes(96);
        var fat = img.Get(32 * DiskImage.MiB, (int)(40 * DiskImage.MiB));
        var journal = Path.Combine(Path.GetTempPath(), $"lasteast-move-{Guid.NewGuid():N}.json");
        using var cts = new CancellationTokenSource();
        using (var dev = img.Open())
        {
            // move FAT32 from 32 MiB to 26 MiB (6 MiB shift → chunks of 4 MiB), crash after 3 chunks
            var j = PartitionMover.NewJournal("img", 32 * DiskImage.MiB, 26 * DiskImage.MiB, 40 * DiskImage.MiB, 512);
            int chunks = 0;
            var p = new SyncProgress<RawProgress>(new List<RawProgress>(), _ => { if (++chunks == 3) cts.Cancel(); });
            Assert.Throws<OperationCanceledException>(() => PartitionMover.Run(dev, j, journal, p, cts.Token));
        }
        var saved = MoveJournal.Load(journal)!;
        Assert.Equal(3L, saved.ChunksDone);
        Assert.True(!saved.DataCopied);
        using (var dev = img.Open()) PartitionMover.Run(dev, saved, journal, null, CancellationToken.None);

        var mbr = PartitionTableCodec.ReadMbr(img.Get(0, 512));
        Assert.Equal((uint)(26 * 2048), mbr.Entries[1].FirstLba);
        var moved = img.Get(26 * DiskImage.MiB, (int)(40 * DiskImage.MiB));
        Assert.Equal((uint)(26 * 2048), Hidden(img, 26 * DiskImage.MiB));
        // undo the hidden-sector patches (boot + backup at sector 6) and compare
        BinaryPrimitives.WriteUInt32LittleEndian(moved.AsSpan(0x1C), BinaryPrimitives.ReadUInt32LittleEndian(fat.AsSpan(0x1C)));
        BinaryPrimitives.WriteUInt32LittleEndian(moved.AsSpan(6 * 512 + 0x1C), BinaryPrimitives.ReadUInt32LittleEndian(fat.AsSpan(6 * 512 + 0x1C)));
        Assert.True(moved.AsSpan().SequenceEqual(fat), "FAT32 content preserved after resume");
        File.Delete(journal);
    }

    [Test]
    public static void Move_rejects_zero_shift()
    {
        Assert.Equal("Move.Error.NoChange", Assert.Throws<RawOperationException>(() => PartitionMover.NewJournal("x", 100, 100, 10, 512)).Key);
    }
}

internal sealed class SyncProgress<T>(List<T> list, Action<T>? on = null) : IProgress<T>
{
    public void Report(T value) { list.Add(value); on?.Invoke(value); }
}
