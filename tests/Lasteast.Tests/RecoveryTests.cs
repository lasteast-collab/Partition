using System.Buffers.Binary;
using Lasteast.Core.Localization;
using Lasteast.Core.PartitionTables;
using Lasteast.Presentation.Services;
using Lasteast.Presentation.ViewModels;
using System.Security.Cryptography;
using Lasteast.Core.Models;
using Lasteast.Core.Operations;
using Lasteast.Core.RawDisk;
using Lasteast.Core.Recovery;

namespace Lasteast.Tests;

/// <summary>Phase 7 against real NTFS/FAT32/exFAT images made by mkntfs/ntfs-3g, mkfs.fat/mtools and mkfs.exfat (tools/make-fixtures.sh).</summary>
public static class RecoveryTests
{
    private const long MiB = 1024 * 1024;

    private static Dictionary<string, string> Expected() =>
        File.ReadAllLines(Path.Combine(DiskImage.Fixtures, "checksums-deleted.txt"))
            .Select(l => l.Split("  ", 2)).ToDictionary(p => p[1], p => p[0].ToUpperInvariant());

    private static string Hash(IRecoveryReader r, RecoverableFile f)
    {
        using var ms = new MemoryStream();
        r.Extract(f, ms, CancellationToken.None);
        return Convert.ToHexString(SHA256.HashData(ms.ToArray()));
    }

    private static DiskImage Volume(string gz, long sizeMb)
    {
        var img = new DiskImage(sizeMb);
        img.Put(0, DiskImage.Fixture(gz));
        return img;
    }

    [Test]
    public static void Ntfs_deleted_files_are_found_with_paths_and_recovered_byte_exact()
    {
        var exp = Expected();
        using var img = Volume("ntfs-deleted32.img.gz", 32);
        using var dev = img.Open();
        var r = RecoveryReaders.Open(dev)!;
        Assert.Equal("NTFS", r.FileSystem);
        Assert.Equal("LE-DEL", r.Label);
        var deleted = r.Scan(includeExisting: false, null, CancellationToken.None);
        Assert.Equal(4, deleted.Count);
        Assert.True(deleted.All(f => f.IsDeleted && f.Recoverability == Recoverability.Good), string.Join(",", deleted.Select(f => f.Recoverability)));
        Assert.Equal(@"Photos\2024", deleted.Single(f => f.Name == "nested-deleted.dat").Folder);
        foreach (var f in deleted) Assert.Equal(exp[f.Name], Hash(r, f), f.Name);
        Assert.True(deleted.Single(f => f.Name == "small-deleted.txt").Size == 36); // resident data

        var all = r.Scan(includeExisting: true, null, CancellationToken.None);
        var keep = all.Single(f => f.Name == "keep.txt");
        Assert.True(!keep.IsDeleted && keep.Recoverability == Recoverability.Intact);
        Assert.Equal(exp["keep.txt"], Hash(r, keep));
    }

    [Test]
    public static void Ntfs_files_are_listed_through_the_backup_boot_sector_and_the_mft_mirror()
    {
        var exp = Expected();
        using var img = Volume("ntfs-deleted32.img.gz", 32);
        var original = img.Get(0, 512);
        int spc = original[0x0D];
        long mft = BitConverter.ToInt64(original, 0x30);
        img.Put(0, new byte[512]);                       // boot sector gone
        img.Put(mft * spc * 512, new byte[1024]);        // and MFT record 0 too
        using var dev = img.Open();
        var r = RecoveryReaders.Open(dev)!;
        Assert.True(r.ThroughBackupBootSector, "through backup boot sector");
        var deleted = r.Scan(includeExisting: false, null, CancellationToken.None);
        Assert.Equal(4, deleted.Count);
        foreach (var f in deleted) Assert.Equal(exp[f.Name], Hash(r, f), f.Name);
        var keep = r.Scan(includeExisting: true, null, CancellationToken.None).Single(f => f.Name == "keep.txt");
        Assert.Equal(exp["keep.txt"], Hash(r, keep));
        Assert.True(img.Get(0, 512).All(b => b == 0), "the volume was not written");
    }

    [Test]
    public static void Fat32_deleted_files_recover_long_names_and_content()
    {
        var exp = Expected();
        using var img = Volume("fat32-deleted40.img.gz", 40);
        using var dev = img.Open();
        var r = RecoveryReaders.Open(dev)!;
        Assert.Equal("LE-FATDEL", r.Label);
        var deleted = r.Scan(false, null, CancellationToken.None);
        var lfn = deleted.Single(f => f.Name == "Long file name deleted.bin");
        Assert.Equal(exp["deleted.bin"], Hash(r, lfn));
        var nested = deleted.Single(f => f.Folder == "Docs");
        Assert.Equal("_ESTED.DAT", nested.Name); // 8.3 entry without long name: first letter is lost by design of FAT
        Assert.Equal(exp["nested-deleted.dat"], Hash(r, nested));
        Assert.True(deleted.All(f => f.Recoverability == Recoverability.Good));
    }

    [Test]
    public static void Recovery_writer_refuses_source_disk_and_writes_tree_with_unique_names()
    {
        using var img = Volume("ntfs-deleted32.img.gz", 32);
        using var dev = img.Open();
        var r = RecoveryReaders.Open(dev)!;
        var files = r.Scan(false, null, CancellationToken.None);
        var dest = Path.Combine(Path.GetTempPath(), "lasteast-rec-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.Equal("Rec.Error.SameDisk", Assert.Throws<RawOperationException>(() => RecoveryWriter.Recover(r, files, dest, 3, 3, null, CancellationToken.None)).Key);
            var rep = RecoveryWriter.Recover(r, files, dest, 3, 1, null, CancellationToken.None);
            Assert.Equal(4, rep.Recovered);
            Assert.True(File.Exists(Path.Combine(dest, "Photos", "2024", "nested-deleted.dat")));
            var again = RecoveryWriter.Recover(r, files.Take(1), dest, 3, null, null, CancellationToken.None);
            Assert.Equal(1, again.Recovered);
            Assert.True(Directory.GetFiles(dest).Any(p => p.Contains(" (1)")), "second copy gets a unique name");
        }
        finally { try { Directory.Delete(dest, true); } catch { } }
    }

    [Test]
    public static void Quick_scan_finds_partitions_after_the_table_was_wiped_and_restores_them()
    {
        using var img = DiskImage.MbrWithVolumes(96);
        img.Put(0, new byte[512]); // partition table destroyed
        DiskInfo d;
        IReadOnlyList<FoundPartition> found;
        using (var dev = img.Open())
        {
            d = DiskImage.Describe(dev);
            Assert.Equal(PartitionStyle.Raw, d.Style);
            found = LostPartitionScanner.Scan(dev, Array.Empty<(ulong, ulong)>(), LostScanMode.Quick, null, CancellationToken.None);
        }
        Assert.Equal(2, found.Count);
        var ntfs = found.Single(f => f.FileSystem == "NTFS");
        var fat = found.Single(f => f.FileSystem == "FAT32");
        Assert.True(ntfs.OffsetBytes == (ulong)MiB && ntfs.SizeBytes == 24UL * (ulong)MiB && ntfs.Confidence == FoundConfidence.High);
        Assert.True(fat.OffsetBytes == 32UL * (ulong)MiB && fat.SizeBytes == 40UL * (ulong)MiB && fat.Label == "LE-FAT32" && fat.Confidence == FoundConfidence.High);

        // simulate + restore both through the operation model, then compare with the engine's verifier
        var ops = found.Select(f => (DiskOperation)new RecoverPartitionOp(7, f.OffsetBytes, f.SizeBytes, f.FileSystem)).ToList();
        var sim = LayoutSimulator.Simulate(d, ops);
        Assert.True(sim.IsValid, string.Join(",", sim.AllIssues.Select(i => i.Key)));
        Assert.True(sim.AllIssues.Any(i => i.Key == "Rec.Warn.NewMbr"));
        using (var dev = img.Open())
            foreach (var f in found) PartitionTableEditor.AddEntry(dev, f.OffsetBytes, f.SizeBytes, f.FileSystem);
        DiskInfo after;
        using (var dev = img.Open()) after = DiskImage.Describe(dev);
        Assert.Equal(PartitionStyle.Mbr, after.Style);
        foreach (var op in ops) Assert.Equal(null, GuardedExecutionEngine.Verify(op, sim.After, after));

        // the restored NTFS volume is readable again
        using var dev2 = img.Open();
        var reader = RecoveryReaders.Open(new OffsetDevice(dev2, MiB, 24 * MiB))!;
        Assert.True(reader.Scan(true, null, CancellationToken.None).Any(f => f.Name == "big.bin" && !f.IsDeleted));
    }

    [Test]
    public static void Ntfs_with_destroyed_boot_sector_is_found_through_its_backup_and_repaired()
    {
        using var img = DiskImage.GptWithVolumes(96);
        DiskInfo d;
        using (var dev = img.Open()) d = DiskImage.Describe(dev);
        // delete the NTFS entry from the table and destroy its boot sector
        using (var dev = img.Open())
        {
            var head = new byte[34 * 512];
            dev.Read(0, head);
            var gpt = Lasteast.Core.PartitionTables.PartitionTableCodec.ReadGpt(head, 512);
            var g2 = gpt with { Entries = gpt.Entries.Where(e => e.FirstLba != 2048).ToList() };
            var (h, t, tl) = Lasteast.Core.PartitionTables.PartitionTableCodec.BuildGpt(g2, (ulong)(dev.Length / 512), 512, head.AsSpan(0, 512));
            dev.Write((long)tl * 512, t);
            dev.Write(0, h);
        }
        img.Put(MiB, new byte[512]);

        using (var dev = img.Open())
        {
            d = DiskImage.Describe(dev);
            var existing = d.Partitions.Select(p => (p.OffsetBytes, p.SizeBytes)).ToList();
            var found = LostPartitionScanner.Scan(dev, existing, LostScanMode.Quick, null, CancellationToken.None);
            var ntfs = found.Single();
            Assert.True(ntfs.FromBackupBootSector && ntfs.OffsetBytes == (ulong)MiB && ntfs.SizeBytes == 24UL * (ulong)MiB);
            var op = new RecoverPartitionOp(7, ntfs.OffsetBytes, ntfs.SizeBytes, "NTFS", RestoreBootSector: true);
            Assert.True(LayoutSimulator.Simulate(d, new[] { op }).IsValid);
            PartitionTableEditor.AddEntry(dev, ntfs.OffsetBytes, ntfs.SizeBytes, "NTFS", restoreBootSector: true);
        }
        using var dev3 = img.Open();
        Assert.Equal(2, DiskImage.Describe(dev3).Partitions.Count);
        var reader = RecoveryReaders.Open(new OffsetDevice(dev3, MiB, 24 * MiB))!;
        Assert.Equal("LE-NTFS", reader.Label);
    }

    [Test]
    public static void Deep_scan_finds_unaligned_exfat_that_quick_scan_misses()
    {
        using var img = DiskImage.MbrWithVolumes(96);
        long at = 74 * MiB + 63 * 512;
        img.Put(at, DiskImage.Fixture("exfat16.img.gz"));
        using var dev = img.Open();
        var d = DiskImage.Describe(dev);
        var existing = d.Partitions.Select(p => (p.OffsetBytes, p.SizeBytes)).ToList();
        Assert.True(LostPartitionScanner.Scan(dev, existing, LostScanMode.Quick, null, CancellationToken.None).All(f => f.FileSystem != "exFAT"));
        var deep = LostPartitionScanner.Scan(dev, existing, LostScanMode.Deep, null, CancellationToken.None);
        var ex = deep.Single(f => f.FileSystem == "exFAT");
        Assert.Equal((ulong)at, ex.OffsetBytes);
        Assert.Equal(16UL * (ulong)MiB, ex.SizeBytes);
    }

    [Test]
    public static void Recover_partition_simulation_rejects_overlap_and_misalignment()
    {
        using var img = DiskImage.MbrWithVolumes(96);
        using var dev = img.Open();
        var d = DiskImage.Describe(dev);
        Assert.True(LayoutSimulator.Simulate(d, new[] { new RecoverPartitionOp(7, 10 * (ulong)MiB, 8 * (ulong)MiB, "NTFS") }).AllIssues.Any(i => i.Key == "Rec.Error.Overlap"));
        Assert.True(LayoutSimulator.Simulate(d, new[] { new RecoverPartitionOp(7, 80 * (ulong)MiB + 100, 8 * (ulong)MiB, "NTFS") }).AllIssues.Any(i => i.Key == "Op.Error.Granularity"));
        Assert.True(LayoutSimulator.Simulate(d, new[] { new RecoverPartitionOp(7, 80 * (ulong)MiB, 8 * (ulong)MiB, "NTFS") }).IsValid);
    }
}

public static class CarvingTests
{
    [Test]
    public static void Carver_finds_every_planted_file_with_exact_length_and_no_false_positives()
    {
        var expected = File.ReadAllLines(Path.Combine(DiskImage.Fixtures, "carve-checksums.txt"))
            .Select(l => l.Split(' ')).Select(p => (Offset: long.Parse(p[0]), Ext: p[1], Hash: p[2].ToUpperInvariant())).ToList();
        using var img = new DiskImage(4);
        img.Put(0, DiskImage.Fixture("carve4.img.gz"));
        using var dev = img.Open();
        var reader = new CarvingRecoveryReader(dev);
        var files = reader.Scan(false, null, CancellationToken.None);
        var got = files.Select(f =>
        {
            using var ms = new MemoryStream();
            reader.Extract(f, ms, CancellationToken.None);
            var off = Convert.ToInt64(f.Name.Split('-')[2].Split('.')[0], 16);
            return (Offset: off, Ext: Path.GetExtension(f.Name)[1..], Hash: Convert.ToHexString(SHA256.HashData(ms.ToArray())));
        }).ToList();
        foreach (var e in expected)
            Assert.True(got.Contains(e), $"missing {e.Ext} at {e.Offset}; got [{string.Join(", ", got.Select(g => $"{g.Ext}@{g.Offset}:{g.Hash[..8]}"))}]");
        Assert.Equal(expected.Count, got.Count);
    }
}

public static class ExfatRecoveryTests
{
    [Test]
    public static void Exfat_lists_and_restores_deleted_files_byte_exact()
    {
        var expected = File.ReadAllLines(Path.Combine(DiskImage.Fixtures, "exfat-checksums.txt"))
            .Select(l => l.Split('\t')).Select(p => (Path: p[0], Deleted: p[1] == "1", Hash: p[2].ToUpperInvariant())).ToList();
        using var img = new DiskImage(16);
        img.Put(0, DiskImage.Fixture("exfat16.img.gz"));
        using var dev = img.Open();
        var reader = RecoveryReaders.Open(dev);
        Assert.True(reader is ExfatRecoveryReader, "exFAT not detected");
        Assert.Equal("LE-EXFAT", reader!.Label);
        var files = reader.Scan(true, null, CancellationToken.None);
        foreach (var e in expected)
        {
            var f = files.SingleOrDefault(x => x.FullPath == e.Path);
            Assert.True(f is not null, $"missing {e.Path}; got [{string.Join(", ", files.Select(x => x.FullPath))}]");
            Assert.Equal(e.Deleted, f!.IsDeleted);
            Assert.True((e.Deleted ? Recoverability.Good : Recoverability.Intact) == f.Recoverability, $"{e.Path}: {f.Recoverability}");
            using var ms = new MemoryStream();
            reader.Extract(f, ms, CancellationToken.None);
            Assert.Equal(e.Hash, Convert.ToHexString(SHA256.HashData(ms.ToArray())));
        }
        Assert.Equal(expected.Count, files.Count);
        // without "include existing", only deleted files are listed
        Assert.Equal(expected.Count(e => e.Deleted), reader.Scan(false, null, CancellationToken.None).Count);
    }
}

public static class DiskImageTests
{
    private static DiskImage MbrImageWithExfat()
    {
        var img = new DiskImage(18);
        var s0 = new byte[512];
        PartitionTableCodec.WriteMbr(s0, new MbrDisk(0x1234, new byte[446], new[] { new MbrEntry(false, 0x07, 2048, 16 * 2048) }));
        img.Put(0, s0);
        img.Put(DiskImage.MiB, DiskImage.Fixture("exfat16.img.gz"));
        return img;
    }

    [Test]
    public static void Raw_image_lists_its_partitions_and_the_whole_image()
    {
        using var img = MbrImageWithExfat();
        Assert.Equal(DiskImageKind.Raw, DiskImages.Classify(img.Path));
        using var dev = DiskImages.Open(img.Path);
        var regions = DiskImages.Regions(dev);
        Assert.Equal(2, regions.Count);
        Assert.Equal(new ImageRegion(DiskImage.MiB, 16 * DiskImage.MiB, "exFAT", 1), regions[0]);
        Assert.Equal(new ImageRegion(0, 18 * DiskImage.MiB, "", null), regions[1]);
        Assert.Throws<InvalidOperationException>(() => dev.Write(0, new byte[512]));
    }

    [Test]
    public static void Fixed_vhd_excludes_its_footer_and_vhdx_needs_attaching()
    {
        using var img = new DiskImage(16);
        img.Put(0, DiskImage.Fixture("exfat16.img.gz"));
        var footer = new byte[512];
        "conectix"u8.CopyTo(footer);
        BinaryPrimitives.WriteUInt32BigEndian(footer.AsSpan(0x3C), 2);
        using (var fs = new FileStream(img.Path, FileMode.Append)) fs.Write(footer);
        Assert.Equal(DiskImageKind.FixedVhd, DiskImages.Classify(img.Path));
        using (var dev = DiskImages.Open(img.Path))
        {
            Assert.Equal(16 * DiskImage.MiB, dev.Length);
            Assert.Equal("exFAT", DiskImages.Regions(dev).Single().FileSystem); // the image is one volume
        }
        BinaryPrimitives.WriteUInt32BigEndian(footer.AsSpan(0x3C), 3); // dynamic
        img.Put(16 * DiskImage.MiB, footer);
        Assert.Equal(DiskImageKind.NeedsAttach, DiskImages.Classify(img.Path));
        img.Put(0, "vhdxfile"u8.ToArray());
        Assert.Equal(DiskImageKind.NeedsAttach, DiskImages.Classify(img.Path));
        var ex = Assert.Throws<RawOperationException>(() => DiskImages.Open(img.Path));
        Assert.Equal("Rec.Image.NeedsAttach", ex.Key);
    }

    [Test]
    public static async Task File_recovery_page_recovers_from_an_image_file()
    {
        Loc.Instance.SetLanguage("en");
        using var img = MbrImageWithExfat();
        var dest = Path.Combine(Path.GetTempPath(), "lasteast-rec-" + Guid.NewGuid().ToString("N"));
        try
        {
            var shell = new NullShellService { SaveFileAnswer = img.Path };
            var vm = new FileRecoveryPageViewModel(null, shell);
            await vm.OpenImageCommand.ExecuteAsync();
            Assert.True(vm.Source?.ImagePath == img.Path && vm.Source.OffsetBytes == (ulong)DiskImage.MiB, $"source: {vm.Source?.Title} / {vm.Status}");
            await vm.ScanCommand.ExecuteAsync();
            Assert.Equal(4, vm.Files.Count);
            foreach (var f in vm.Files) f.IsSelected = true;
            Directory.CreateDirectory(dest);
            vm.Destination = dest;
            await vm.RecoverCommand.ExecuteAsync();
            var expected = File.ReadAllLines(Path.Combine(DiskImage.Fixtures, "exfat-checksums.txt")).Select(l => l.Split('\t')).Where(p => p[1] == "1");
            foreach (var p in expected)
            {
                var file = Path.Combine(dest, p[0].Replace('\\', Path.DirectorySeparatorChar));
                Assert.True(File.Exists(file), $"not recovered: {p[0]} ({vm.Status})");
                Assert.Equal(p[2].ToUpperInvariant(), Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))));
            }
            // the image itself is unchanged
            Assert.True(DiskImage.Fixture("exfat16.img.gz").AsSpan().SequenceEqual(img.Get(DiskImage.MiB, 16 * (int)DiskImage.MiB)), "image changed");
        }
        finally { if (Directory.Exists(dest)) Directory.Delete(dest, true); }
    }
}

public static class BootSectorRepairTests
{
    private static void Roundtrip(string fixture, int sizeMb, int damagedSectors, string fs, long extraBytes = 0)
    {
        using var img = new DiskImage(sizeMb + (extraBytes > 0 ? 1 : 0));
        var original = DiskImage.Fixture(fixture);
        img.Put(0, original);
        img.Put(0, new byte[damagedSectors * 512]); // the damage: start of the volume zeroed
        using var dev = img.Open();
        var vol = new OffsetDevice(dev, 0, original.Length + extraBytes);
        Assert.True(RecoveryReaders.Open(vol)?.ThroughBackupBootSector == true, "damaged volume is read through its backup, without writing");
        Assert.True(img.Get(0, damagedSectors * 512).All(b => b == 0), "nothing written by reading");
        var d = BootSectorRepair.Diagnose(vol);
        Assert.Equal(BootSectorState.Repairable, d.State);
        Assert.Equal(fs, d.FileSystem);
        var undo = BootSectorRepair.Current(dev, 0, d);
        Assert.True(undo.All(b => b == 0), "undo data holds the damaged sectors");
        BootSectorRepair.Apply(dev, 0, d);
        var repaired = img.Get(0, damagedSectors * 512);
        // NTFS's backup is a copy of sector 0 only; FAT32 and exFAT restore their whole boot region
        int compare = fs == "NTFS" ? 512 : damagedSectors * 512;
        if (fs == "FAT32")
        {
            // FSInfo (sector 1) comes from the backup with its free-space hints marked unknown
            Assert.True(repaired.AsSpan(0, 512).SequenceEqual(original.AsSpan(0, 512)) && repaired.AsSpan(1024, 512).SequenceEqual(original.AsSpan(1024, 512)), "boot sectors restored");
            Assert.True(repaired.AsSpan(512, 4).SequenceEqual("RRaA"u8) && BitConverter.ToUInt32(repaired, 512 + 488) == uint.MaxValue, "FSInfo restored, hints unknown");
        }
        else
            Assert.True(repaired.AsSpan(0, compare).SequenceEqual(original.AsSpan(0, compare)), "boot region restored byte-exact");
        Assert.True(RecoveryReaders.Open(vol) is not null, "readable again");
        Assert.Equal(BootSectorState.PrimaryValid, BootSectorRepair.Diagnose(vol).State);
    }

    [Test] public static void Ntfs_boot_sector_is_restored_from_the_backup_in_the_last_sector() => Roundtrip("ntfs24.img.gz", 24, 1, "NTFS");
    [Test] public static void Ntfs_backup_is_found_when_the_partition_is_larger_than_the_volume() => Roundtrip("ntfs24.img.gz", 24, 1, "NTFS", extraBytes: 512 * 1024);
    [Test] public static void Fat32_boot_sectors_are_restored_from_sector_6() => Roundtrip("fat32-40.img.gz", 40, 3, "FAT32");
    [Test] public static void Exfat_boot_region_is_restored_from_its_backup_region() => Roundtrip("exfat16.img.gz", 16, 12, "exFAT");

    [Test]
    public static void A_backup_whose_mft_is_gone_is_not_used()
    {
        using var img = new DiskImage(24);
        var original = DiskImage.Fixture("ntfs24.img.gz");
        img.Put(0, original);
        int spc = original[0x0D];
        long mft = BitConverter.ToInt64(original, 0x30), mirr = BitConverter.ToInt64(original, 0x38);
        img.Put(0, new byte[512]);
        img.Put(mft * spc * 512, new byte[4096]);
        img.Put(mirr * spc * 512, new byte[4096]);
        using var dev = img.Open();
        Assert.Equal(BootSectorState.NoBackup, BootSectorRepair.Diagnose(dev).State);
    }

    [Test]
    public static void A_healthy_volume_needs_no_repair()
    {
        using var img = new DiskImage(24);
        img.Put(0, DiskImage.Fixture("ntfs24.img.gz"));
        using var dev = img.Open();
        var d = BootSectorRepair.Diagnose(dev);
        Assert.Equal(BootSectorState.PrimaryValid, d.State);
        Assert.Throws<RawOperationException>(() => BootSectorRepair.Apply(dev, 0, d));
    }
}
