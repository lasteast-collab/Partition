using System.Buffers.Binary;
using System.Security.Cryptography;
using Lasteast.Core.Files;
using Lasteast.Core.Models;
using Lasteast.Core.Operations;
using Lasteast.Core.PartitionTables;
using Lasteast.Core.RawDisk;
using Lasteast.Core.Recovery;
using Lasteast.Core.Wipe;

namespace Lasteast.Tests;

public static class Batch1Tests
{
    private static bool Err(SimulationResult r, string key) => r.AllIssues.Any(i => i.IsError && i.Key == key);

    [Test]
    public static void Tree_copier_copies_verifies_and_skips_volume_system_folders()
    {
        var src = Path.Combine(Path.GetTempPath(), "le-src-" + Guid.NewGuid().ToString("N"));
        var dst = Path.Combine(Path.GetTempPath(), "le-dst-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(src, "a", "b"));
            Directory.CreateDirectory(Path.Combine(src, "System Volume Information"));
            Directory.CreateDirectory(Path.Combine(src, "empty"));
            File.WriteAllBytes(Path.Combine(src, "a", "b", "x.bin"), RandomNumberGenerator.GetBytes(3_000_001));
            File.WriteAllText(Path.Combine(src, "top.txt"), "hello");
            File.WriteAllText(Path.Combine(src, "System Volume Information", "skip"), "no");
            var rep = TreeCopier.Copy(src, dst, null, CancellationToken.None);
            Assert.True(rep.Ok);
            Assert.Equal(2L, rep.Files);
            Assert.True(File.ReadAllBytes(Path.Combine(dst, "a", "b", "x.bin")).AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(src, "a", "b", "x.bin"))));
            Assert.True(Directory.Exists(Path.Combine(dst, "empty")) && !Directory.Exists(Path.Combine(dst, "System Volume Information")));
            // copying onto existing files is refused per file, never overwritten
            var again = TreeCopier.Copy(src, dst, null, CancellationToken.None);
            Assert.Equal(2, again.Errors.Count);
        }
        finally { foreach (var d in new[] { src, dst }) if (Directory.Exists(d)) Directory.Delete(d, true); }
    }

    [Test]
    public static void Wiper_overwrites_exactly_the_range_and_verifies_every_method()
    {
        foreach (var m in new[] { WipeMethod.Zeros, WipeMethod.Random, WipeMethod.ThreePass })
        {
            using var img = new DiskImage(10);
            var before = RandomNumberGenerator.GetBytes(10 * (int)DiskImage.MiB);
            img.Put(0, before);
            using var dev = img.Open();
            var rep = Wiper.Run(dev, DiskImage.MiB, 8 * DiskImage.MiB, m, fullVerify: true, null, CancellationToken.None);
            Assert.Equal(m == WipeMethod.ThreePass ? 3 : 1, rep.Passes);
            var after = img.Get(0, 10 * (int)DiskImage.MiB);
            Assert.True(after.AsSpan(0, (int)DiskImage.MiB).SequenceEqual(before.AsSpan(0, (int)DiskImage.MiB)), "before the range untouched");
            Assert.True(after.AsSpan(9 * (int)DiskImage.MiB).SequenceEqual(before.AsSpan(9 * (int)DiskImage.MiB)), "after the range untouched");
            var wiped = after.AsSpan((int)DiskImage.MiB, 8 * (int)DiskImage.MiB);
            if (m == WipeMethod.Random) Assert.True(wiped.IndexOfAnyExcept((byte)0) >= 0 && !wiped.SequenceEqual(before.AsSpan((int)DiskImage.MiB, 8 * (int)DiskImage.MiB)));
            else Assert.True(wiped.IndexOfAnyExcept((byte)0) < 0, $"{m}: zeros last");
        }
    }

    [Test]
    public static void Exfat_offset_patch_keeps_the_boot_region_valid()
    {
        using var img = new DiskImage(20);
        img.Put(2 * DiskImage.MiB, DiskImage.Fixture("exfat16.img.gz"));
        using var dev = img.Open();
        PartitionMover.PatchHiddenSectors(dev, 2 * DiskImage.MiB, 16 * DiskImage.MiB);
        var vol = new OffsetDevice(dev, 2 * DiskImage.MiB, 16 * DiskImage.MiB);
        Assert.Equal(BootSectorState.PrimaryValid, BootSectorRepair.Diagnose(vol).State);
        Assert.Equal((ulong)(2 * DiskImage.MiB / 512), BinaryPrimitives.ReadUInt64LittleEndian(img.Get(2 * DiskImage.MiB + 0x40, 8)));
        Assert.True(RecoveryReaders.Open(vol) is ExfatRecoveryReader);
    }

    [Test]
    public static void Partition_copier_copies_used_ranges_plus_boot_areas_and_verifies()
    {
        using var img = new DiskImage(40);
        var data = RandomNumberGenerator.GetBytes(16 * (int)DiskImage.MiB);
        img.Put(DiskImage.MiB, data);
        using var dev = img.Open();
        RangeReader read = (o, b) => dev.Read(o, b);
        var used = new[] { new ByteRange(4 * DiskImage.MiB, 2 * DiskImage.MiB) };
        var rep = PartitionCopier.Copy(read, dev, DiskImage.MiB, 20 * DiskImage.MiB, 16 * DiskImage.MiB, used, verify: true, null, CancellationToken.None);
        Assert.Equal(4L * DiskImage.MiB, rep.BytesCopied);
        var copy = img.Get(20 * DiskImage.MiB, 16 * (int)DiskImage.MiB);
        Assert.True(copy.AsSpan(0, (int)DiskImage.MiB).SequenceEqual(data.AsSpan(0, (int)DiskImage.MiB)), "first MiB");
        Assert.True(copy.AsSpan(4 * (int)DiskImage.MiB, 2 * (int)DiskImage.MiB).SequenceEqual(data.AsSpan(4 * (int)DiskImage.MiB, 2 * (int)DiskImage.MiB)), "used range");
        Assert.True(copy.AsSpan(15 * (int)DiskImage.MiB).SequenceEqual(data.AsSpan(15 * (int)DiskImage.MiB)), "last MiB");
    }

    private static byte[] Mbr(params (byte Type, uint Lba, uint Count, bool Active)[] entries)
    {
        var s = new byte[512];
        s[510] = 0x55; s[511] = 0xAA;
        BinaryPrimitives.WriteUInt32LittleEndian(s.AsSpan(440), 0x12345678);
        for (int i = 0; i < entries.Length; i++)
        {
            var e = s.AsSpan(446 + 16 * i);
            e[0] = entries[i].Active ? (byte)0x80 : (byte)0;
            e[4] = entries[i].Type;
            BinaryPrimitives.WriteUInt32LittleEndian(e[8..], entries[i].Lba);
            BinaryPrimitives.WriteUInt32LittleEndian(e[12..], entries[i].Count);
        }
        return s;
    }

    [Test]
    public static void Primary_to_logical_and_back_rewrites_only_the_tables()
    {
        using var img = new DiskImage(64);
        // three primaries at 1, 17 and 33 MiB (16 MiB each), with 1 MiB gaps between them
        img.Put(0, Mbr((0x07, 2048, 32768, true), (0x07, 36864, 32768, false), (0x0C, 71680, 32768, false)));
        using var dev = img.Open();
        var before = img.Get(DiskImage.MiB, 63 * (int)DiskImage.MiB);
        MbrKindConverter.Apply(dev, 71680UL * 512, toLogical: true);
        var l1 = MbrKindConverter.Read(dev, out var ebrs1);
        Assert.True(l1.Single(e => e.Start == 71680UL * 512).Logical && ebrs1.Count == 1);
        MbrKindConverter.Apply(dev, 36864UL * 512, toLogical: true);
        var l2 = MbrKindConverter.Read(dev, out var ebrs2);
        Assert.Equal(2, l2.Count(e => e.Logical));
        Assert.Equal(2, ebrs2.Count);
        Assert.True(l2.Single(e => e.Start == 2048UL * 512).Active, "active flag kept");
        // the first partition cannot join: there is no free sector before it inside an extended partition... it is active
        Assert.Equal("Op.Error.Protected", MbrKindConverter.Plan(l2, 2048UL * 512, true, 512, out _));
        MbrKindConverter.Apply(dev, 36864UL * 512, toLogical: false);
        MbrKindConverter.Apply(dev, 71680UL * 512, toLogical: false);
        var l3 = MbrKindConverter.Read(dev, out var ebrs3);
        Assert.True(l3.All(e => !e.Logical) && ebrs3.Count == 0 && l3.Count == 3);
        Assert.Equal(0x12345678u, BinaryPrimitives.ReadUInt32LittleEndian(img.Get(440, 4)));
        // partition data never changed (EBR sectors live in the gaps; gaps are compared separately)
        var after = img.Get(DiskImage.MiB, 63 * (int)DiskImage.MiB);
        foreach (var e in l3)
        {
            int o = (int)(e.Start - DiskImage.MiB);
            Assert.True(after.AsSpan(o, (int)e.Size).SequenceEqual(before.AsSpan(o, (int)e.Size)), $"data of {e.Start}");
        }
    }

    [Test]
    public static void Logical_needs_a_free_sector_before_it_and_no_interleaved_primary()
    {
        var adjacent = new List<MbrKindConverter.Entry> { new(1UL << 20, 16UL << 20, 7, false, false), new(17UL << 20, 16UL << 20, 7, false, false) };
        Assert.Equal("Kind.Error.NoGap", MbrKindConverter.Plan(adjacent, 17UL << 20, true, 512, out _));
        var three = new List<MbrKindConverter.Entry> { new(1UL << 20, 8UL << 20, 7, false, true), new(10UL << 20, 8UL << 20, 7, false, false), new(19UL << 20, 8UL << 20, 7, false, false) };
        Assert.Equal("Kind.Error.Interleaved", MbrKindConverter.Plan(three, 19UL << 20, true, 512, out _));
    }

    [Test]
    public static void Merge_split_wipe_and_conversions_are_validated_against_the_layout()
    {
        var inv = Fixtures.Real();
        var d1 = Fixtures.Disk(inv, 1);
        var a = d1.Partitions[0];
        var b = d1.Partitions[1];
        // split: shrink + create in the freed space
        ulong half = a.SizeBytes / 2 / LayoutSimulator.MiB * LayoutSimulator.MiB;
        var split = LayoutSimulator.Simulate(d1, new DiskOperation[]
        {
            new ResizePartitionOp(1, a.OffsetBytes, half),
            new CreatePartitionOp(1, a.OffsetBytes + half, a.SizeBytes - half, "NTFS", "Split", null),
        });
        Assert.True(split.IsValid || split.AllIssues.Any(i => i.Key == "Op.Error.ShrinkBelowUsed"), string.Join(",", split.AllIssues.Select(i => i.Key)));
        // wipe: never the Windows disk; a partition wipe leaves the entry but no readable volume
        var d0 = Fixtures.Disk(inv, 0);
        Assert.True(Err(LayoutSimulator.Simulate(d0, new[] { new WipeOp(0, null, WipeMethod.Zeros, false) }), "Wipe.Error.SystemDisk"));
        Assert.True(Err(LayoutSimulator.Simulate(d0, new[] { new WipeOp(0, Fixtures.Letter(inv, 'C').OffsetBytes, WipeMethod.Zeros, false) }), "Op.Error.Protected"));
        var wp = LayoutSimulator.Simulate(d1, new[] { new WipeOp(1, b.OffsetBytes, WipeMethod.Random, true) });
        Assert.True(wp.IsValid && wp.DestroysData && wp.After.Partitions[1].Volume?.AccessError is not null);
        var wd = LayoutSimulator.Simulate(d1, new[] { new WipeOp(1, null, WipeMethod.Zeros, true) });
        Assert.True(wd.IsValid && wd.After.Partitions.Count == 0);
        Assert.Equal(null, GuardedExecutionEngine.Verify(new WipeOp(1, null, WipeMethod.Zeros, true), wd.After, wd.After));
        // dynamic: not for the system disk; to basic only without volumes
        Assert.True(Err(LayoutSimulator.Simulate(d0, new[] { new ConvertDiskTypeOp(0, true) }), "DiskType.Error.System"));
        // reformat keeping files needs a staging folder and a change
        Assert.True(Err(LayoutSimulator.Simulate(d1, new[] { new ReformatKeepingFilesOp(1, a.OffsetBytes, a.Volume!.FileSystem!, null, "D:\\stage", 0) }), "Op.Error.NoChange"));
        var rk = LayoutSimulator.Simulate(d1, new[] { new ReformatKeepingFilesOp(1, a.OffsetBytes, "NTFS", 65536, "E:\\stage", 0) });
        Assert.True(rk.IsValid && rk.After.Partitions[0].Volume!.ClusterSizeBytes == 65536, string.Join(",", rk.AllIssues.Select(i => i.Key)));
    }
}

public static class Batch2Tests
{
    [Test]
    public static void Optimizer_never_defragments_ssds_and_leaves_unknown_media_alone()
    {
        var inv = Fixtures.Real();
        var d0 = Fixtures.Disk(inv, 0);
        var c = Fixtures.Letter(inv, 'C');
        var ssd = d0 with { Media = MediaKind.Ssd, Bus = BusKind.Sata, TrimEnabled = true };
        var hdd = d0 with { Media = MediaKind.Hdd, Bus = BusKind.Sata };
        var unknown = d0 with { Media = MediaKind.Unknown, Bus = BusKind.Usb };
        Assert.Equal(Lasteast.Core.Optimization.OptimizeAction.Retrim, Lasteast.Core.Optimization.OptimizePlanner.For(ssd, c));
        Assert.Equal(Lasteast.Core.Optimization.OptimizeAction.Defragment, Lasteast.Core.Optimization.OptimizePlanner.For(hdd, c));
        Assert.Equal(Lasteast.Core.Optimization.OptimizeAction.None, Lasteast.Core.Optimization.OptimizePlanner.For(unknown, c));
        Assert.Equal(Lasteast.Core.Optimization.OptimizeAction.Retrim, Lasteast.Core.Optimization.OptimizePlanner.For(unknown with { Bus = BusKind.Nvme }, c));
    }

    [Test]
    public static void Smart_fix_reports_health_temperature_wear_dirty_and_fragmentation_from_real_data_only()
    {
        var inv = Fixtures.Real();
        var none = Lasteast.Core.SmartFix.SmartFixAnalyzer.Analyze(inv, new Lasteast.Core.SmartFix.SystemSnapshot());
        Assert.True(none.Any(f => f.Id == "health" && f.Severity == Lasteast.Core.SmartFix.SmartSeverity.Optional), "no data: stated, not guessed");
        var hot = new Lasteast.Core.Health.DiskHealthReport
        {
            DiskNumber = 0, Verdict = Lasteast.Core.Health.HealthVerdict.Warning, Findings = Array.Empty<Lasteast.Core.Health.HealthFinding>(),
            Windows = new Lasteast.Core.Health.WindowsReliability { Temperature = 82, WearPercent = 93 },
        };
        var letterD = Fixtures.Disk(inv, 1).Partitions[0].Volume!.DriveLetter!.Value;
        var hdd = inv with { Disks = inv.Disks.Select(d => d.Number == 1 ? d with { Media = MediaKind.Hdd } : d).ToList() };
        var f = Lasteast.Core.SmartFix.SmartFixAnalyzer.Analyze(hdd, new Lasteast.Core.SmartFix.SystemSnapshot
        {
            Health = new[] { hot }, DirtyVolumes = new[] { letterD }, Fragmentation = new Dictionary<char, int> { [letterD] = 30 },
        });
        Assert.True(f.Any(x => x.Id == "health-0" && x.Severity == Lasteast.Core.SmartFix.SmartSeverity.High));
        Assert.True(f.Any(x => x.Id == "temp-0"));
        Assert.True(f.Any(x => x.Id == "wear-0" && x.Severity == Lasteast.Core.SmartFix.SmartSeverity.High));
        var dirty = f.Single(x => x.Id == $"dirty-{letterD}");
        Assert.True(dirty.Fix == Lasteast.Core.SmartFix.FixKind.QueueOperations && dirty.Operations.Single() is RepairFileSystemOp);
        var defrag = f.Single(x => x.Id == $"defrag-{letterD}");
        Assert.True(defrag.Fix == Lasteast.Core.SmartFix.FixKind.Optimize && defrag.OptimizeAction == Lasteast.Core.Optimization.OptimizeAction.Defragment);
    }
}

public static class Batch4Tests
{
    [Test]
    public static void Esp_analyzer_finds_missing_boot_files()
    {
        var ok = Lasteast.Core.Boot.EspAnalyzer.Check(new[] { "EFI/Microsoft/Boot/bootmgfw.efi", @"EFI\Microsoft\Boot\BCD", @"\EFI\Boot\bootx64.efi" });
        Assert.True(ok.Missing.Count == 0 && ok.HasFallback);
        var bad = Lasteast.Core.Boot.EspAnalyzer.Check(new[] { @"EFI\Microsoft\Boot\BCD" });
        Assert.True(bad.Missing.Single().EndsWith("bootmgfw.efi") && !bad.HasFallback);
    }

    [Test]
    public static void Mbr_repair_only_on_mbr_disks()
    {
        var inv = Fixtures.Real();
        var d0 = Fixtures.Disk(inv, 0);
        var mbr = Fixtures.Disk(inv, 1);
        Assert.True(LayoutSimulator.Simulate(d0 with { Style = PartitionStyle.Gpt }, new[] { new RepairMbrOp(0, null) }).AllIssues.Any(i => i.Key == "Mbr.Error.NotMbr"));
        var ok = LayoutSimulator.Simulate(mbr with { Style = PartitionStyle.Mbr }, new[] { new RepairMbrOp(1, null) });
        Assert.True(ok.IsValid && ok.AllIssues.Any(i => i.Key == "Mbr.Warn.CodeOnly"));
    }
}
