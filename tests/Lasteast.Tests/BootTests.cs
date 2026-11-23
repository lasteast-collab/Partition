using System.Security.Cryptography;
using System.Text.Json;
using Lasteast.Core.Backup;
using Lasteast.Core.Boot;
using Lasteast.Core.Models;
using Lasteast.Core.Operations;
using Lasteast.Core.PartitionTables;
using Lasteast.Core.RawDisk;

namespace Lasteast.Tests;

public static class BootTests
{
    private const long MiB = 1024 * 1024;

    private const string BcdSample = """
        Firmware Boot Manager
        ---------------------
        identifier              {fwbootmgr}
        displayorder            {bootmgr}
                                {c0a26d2e-1111-2222-3333-444455556666}
        timeout                 2

        Gestionnaire de démarrage Windows
        ---------------------------------
        identifier              {9dea862c-5cdd-4e70-acc1-f32b344d4795}
        device                  partition=\Device\HarddiskVolume1
        path                    \EFI\Microsoft\Boot\bootmgfw.efi
        description             Windows Boot Manager

        Windows Boot Loader
        -------------------
        identifier              {current}
        device                  partition=C:
        path                    \Windows\system32\winload.efi
        description             Windows 11
        recoverysequence        {abc}
        """;

    [Test]
    public static void Bcd_parser_handles_localized_headers_and_continuation_lines()
    {
        var e = BcdParser.Parse(BcdSample);
        Assert.Equal(3, e.Count);
        Assert.Equal("Gestionnaire de démarrage Windows", e[1].Type);
        Assert.Equal(@"\EFI\Microsoft\Boot\bootmgfw.efi", e[1].Get("path"));
        Assert.Equal("{bootmgr}\n{c0a26d2e-1111-2222-3333-444455556666}", e[0].Get("displayorder"));
        Assert.Equal("Windows 11", e[2].Description);
        Assert.Equal("{current}", e[2].Identifier);
    }

    /// <summary>Writes a backup folder in the format of PartitionTableBackupService.</summary>
    private static string MakeBackup(DiskImage img, DiskInfo d)
    {
        var folder = Path.Combine(Path.GetTempPath(), "lasteast-tb-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var files = new Dictionary<string, string>();
        void Save(string name, byte[] data) { File.WriteAllBytes(Path.Combine(folder, name), data); files[name] = Convert.ToHexString(SHA256.HashData(data)); }
        Save("head.bin", img.Get(0, (int)MiB));
        Save("tail.bin", img.Get((long)d.SizeBytes - MiB, (int)MiB));
        File.WriteAllBytes(Path.Combine(folder, "manifest.json"), JsonSerializer.SerializeToUtf8Bytes(new
        {
            Tool = "Lasteast Partition", Created = DateTimeOffset.Now, Disk = DiskFingerprint.Identity(d), Fingerprint = DiskFingerprint.Compute(d), Files = files,
        }));
        return folder;
    }

    [Test]
    public static void Table_backup_restores_a_deleted_gpt_partition_and_engine_verifier_agrees()
    {
        using var img = DiskImage.GptWithVolumes(96);
        DiskInfo before;
        using (var dev = img.Open()) before = DiskImage.Describe(dev);
        var folder = MakeBackup(img, before);
        try
        {
            // "accident": the FAT partition is removed from the table
            using (var dev = img.Open())
            {
                var head = new byte[34 * 512];
                dev.Read(0, head);
                var gpt = PartitionTableCodec.ReadGpt(head, 512);
                var (h, t, tl) = PartitionTableCodec.BuildGpt(gpt with { Entries = gpt.Entries.Where(e => e.FirstLba != 32 * 2048).ToList() }, (ulong)(dev.Length / 512), 512, head.AsSpan(0, 512));
                dev.Write((long)tl * 512, t);
                dev.Write(0, h);
            }
            DiskInfo now;
            using (var dev = img.Open()) now = DiskImage.Describe(dev);
            Assert.Equal(1, now.Partitions.Count);

            var b = TableBackups.Load(folder);
            Assert.Equal(PartitionStyle.Gpt, b.Style);
            Assert.Equal(2, b.Partitions.Count);
            var op = new RestoreTableOp(7, folder, b.DiskSize, b.Serial, b.Model, b.Style, b.Partitions, b.Created);
            var sim = LayoutSimulator.Simulate(now, new[] { op });
            Assert.True(sim.IsValid, string.Join(",", sim.AllIssues.Select(i => i.Key)));
            var fatBytes = img.Get(32 * MiB, 4096);
            using (var dev = img.Open()) TableBackups.Apply(dev, b);
            DiskInfo after;
            using (var dev = img.Open()) after = DiskImage.Describe(dev);
            Assert.Equal(null, GuardedExecutionEngine.Verify(op, sim.After, after));
            Assert.True(img.Get(32 * MiB, 4096).AsSpan().SequenceEqual(fatBytes), "partition contents untouched");

            // same layout again: nothing to do
            Assert.True(LayoutSimulator.Simulate(after, new[] { op }).AllIssues.Any(i => i.Key == "Op.Error.NoChange"));
            // a damaged backup is refused
            File.WriteAllBytes(Path.Combine(folder, "tail.bin"), new byte[MiB]);
            Assert.Equal("Restore.Error.Corrupt", Assert.Throws<RawOperationException>(() => TableBackups.Load(folder)).Key);
        }
        finally { Directory.Delete(folder, true); }
    }

    [Test]
    public static void Restore_refuses_other_disks_and_removing_running_windows()
    {
        var inv = Fixtures.Real();
        var win = Fixtures.Disk(inv, 0);
        var parts = win.Partitions.Where(p => p.Volume?.IsWindowsVolume != true).Select(p => (p.OffsetBytes, p.SizeBytes)).ToList();
        Assert.True(LayoutSimulator.Simulate(win, new[] { new RestoreTableOp(0, "x", win.SizeBytes, win.SerialNumber, win.Model, PartitionStyle.Gpt, parts, DateTimeOffset.Now) })
            .AllIssues.Any(i => i.Key == "Restore.Error.WindowsChanged"));
        Assert.True(LayoutSimulator.Simulate(win, new[] { new RestoreTableOp(0, "x", win.SizeBytes, "OTHER", win.Model, PartitionStyle.Gpt, parts, DateTimeOffset.Now) })
            .AllIssues.Any(i => i.Key == "Restore.Error.OtherDisk"));
        Assert.True(LayoutSimulator.Simulate(win, new[] { new RestoreTableOp(0, "x", win.SizeBytes + 1, null, null, PartitionStyle.Gpt, parts, DateTimeOffset.Now) })
            .AllIssues.Any(i => i.Key == "Restore.Error.Size"));
        Assert.True(LayoutSimulator.Simulate(win, new[] { new RepairBootOp(0) }).IsValid);
        Assert.True(LayoutSimulator.Simulate(Fixtures.Disk(inv, 1), new[] { new RepairBootOp(1) }).AllIssues.Any(i => i.Key == "Boot.Error.NotWindowsDisk"));
    }

    [Test]
    public static void Folder_backup_copies_verifies_skips_existing_and_refuses_destination_inside_source()
    {
        var root = Path.Combine(Path.GetTempPath(), "lasteast-fb-" + Guid.NewGuid().ToString("N"));
        var src = Path.Combine(root, "Documents");
        Directory.CreateDirectory(Path.Combine(src, "Taxes", "2025"));
        File.WriteAllText(Path.Combine(src, "a.txt"), "alpha");
        File.WriteAllBytes(Path.Combine(src, "Taxes", "2025", "b.bin"), RandomNumberGenerator.GetBytes(200_000));
        var dest = Path.Combine(root, "Backup");
        try
        {
            var r = FolderBackup.Run(new[] { src }, dest, verify: true, null, CancellationToken.None);
            Assert.Equal(2, r.Copied);
            Assert.True(File.ReadAllBytes(Path.Combine(dest, "Documents", "Taxes", "2025", "b.bin"))
                .AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(src, "Taxes", "2025", "b.bin"))));
            var again = FolderBackup.Run(new[] { src }, dest, verify: true, null, CancellationToken.None);
            Assert.True(again.Copied == 0 && again.Skipped == 2, "existing files are not overwritten");
            Assert.True(Assert.Throws<IOException>(() => FolderBackup.Run(new[] { root }, dest, true, null, CancellationToken.None)).Message.StartsWith("Backup.Error.Inside"));
        }
        finally { Directory.Delete(root, true); }
    }
}

public static class HealthTests
{
    private static byte[] NvmePage(byte critical = 0, int kelvin = 310, byte spare = 100, byte spareThr = 10, byte used = 3, ulong written = 1_000_000, ulong hours = 1234, ulong media = 0)
    {
        var p = new byte[512];
        p[0] = critical;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(1), (ushort)kelvin);
        p[3] = spare; p[4] = spareThr; p[5] = used;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(p.AsSpan(48), written);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(p.AsSpan(128), hours);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(p.AsSpan(160), media);
        return p;
    }

    private static DiskInfo Ssd => new() { Number = 0, DevicePath = "x", SizeBytes = 1, Media = MediaKind.Ssd, Bus = BusKind.Nvme, Partitions = new List<PartitionInfo>() };
    private static DiskInfo Hdd => new() { Number = 1, DevicePath = "x", SizeBytes = 1, Media = MediaKind.Hdd, Bus = BusKind.Sata, Partitions = new List<PartitionInfo>() };

    [Test]
    public static void Nvme_log_is_parsed_and_assessed()
    {
        var log = Lasteast.Core.Health.NvmeHealthLog.Parse(NvmePage());
        Assert.Equal(37, log.TemperatureCelsius);
        Assert.Equal((System.Numerics.BigInteger)1234, log.PowerOnHours);
        Assert.Equal((System.Numerics.BigInteger)512_000_000_000, log.BytesWritten);
        var ok = Lasteast.Core.Health.HealthAssessor.Assess(Ssd, log, null, null, new[] { "NVMe" });
        Assert.Equal(Lasteast.Core.Health.HealthVerdict.Healthy, ok.Verdict);

        var worn = Lasteast.Core.Health.HealthAssessor.Assess(Ssd, Lasteast.Core.Health.NvmeHealthLog.Parse(NvmePage(used: 85, media: 2)), null, null, new[] { "NVMe" });
        Assert.Equal(Lasteast.Core.Health.HealthVerdict.Warning, worn.Verdict);
        Assert.True(worn.Findings.Any(f => f.Key == "Health.Wear") && worn.Findings.Any(f => f.Key == "Health.MediaErrors"));

        var bad = Lasteast.Core.Health.HealthAssessor.Assess(Ssd, Lasteast.Core.Health.NvmeHealthLog.Parse(NvmePage(critical: 0x04, spare: 5)), null, null, new[] { "NVMe" });
        Assert.Equal(Lasteast.Core.Health.HealthVerdict.Critical, bad.Verdict);
        Assert.True(bad.Findings.Any(f => f.Key == "Health.SpareLow"));
    }

    [Test]
    public static void Ata_attributes_with_thresholds_and_no_data_means_unknown()
    {
        var data = new byte[512];
        var thr = new byte[512];
        void Attr(int slot, byte id, byte cur, long raw, byte threshold, bool prefail)
        {
            int o = 2 + slot * 12;
            data[o] = id; data[o + 1] = (byte)(prefail ? 1 : 0); data[o + 3] = cur; data[o + 4] = cur;
            for (int k = 0; k < 6; k++) data[o + 5 + k] = (byte)(raw >> (8 * k));
            thr[o] = id; thr[o + 1] = threshold;
        }
        Attr(0, 5, 100, 8, 36, true);
        Attr(1, 194, 64, 41, 0, false);
        Attr(2, 9, 90, 20000, 0, false);
        Attr(3, 1, 10, 0, 16, true); // below threshold
        var attrs = Lasteast.Core.Health.AtaSmart.Parse(data, thr);
        Assert.Equal(4, attrs.Count);
        Assert.Equal(41, Lasteast.Core.Health.AtaSmart.Temperature(attrs));
        var r = Lasteast.Core.Health.HealthAssessor.Assess(Hdd, null, attrs, null, new[] { "ATA" });
        Assert.Equal(Lasteast.Core.Health.HealthVerdict.Critical, r.Verdict);
        Assert.True(r.Findings.Any(f => f.Key == "Health.Threshold") && r.Findings.Any(f => f.Key == "Health.Reallocated"));
        Assert.Equal(20000L, r.PowerOnHours);

        var none = Lasteast.Core.Health.HealthAssessor.Assess(Hdd, null, null, null, Array.Empty<string>());
        Assert.Equal(Lasteast.Core.Health.HealthVerdict.Unknown, none.Verdict);
        Assert.Equal(null, none.Temperature);
    }

    [Test]
    public static void Monitor_rules_flag_low_space_health_and_heat()
    {
        var inv = Fixtures.Real();
        var s = new Lasteast.Core.Settings.AppSettings { LowSpacePercent = 10 };
        var hot = Lasteast.Core.Health.HealthAssessor.Assess(Fixtures.Disk(inv, 1), null, null,
            new Lasteast.Core.Health.WindowsReliability { HealthStatus = "Healthy", Temperature = 58 }, new[] { "Windows" });
        var alerts = Lasteast.Core.Health.MonitorRules.Evaluate(inv, new Dictionary<int, Lasteast.Core.Health.DiskHealthReport> { [1] = hot }, s);
        Assert.True(alerts.Any(a => a.Key == "Mon.LowSpace" && (string)a.Args[0] == "C:"), string.Join(",", alerts.Select(a => a.Id)));
        Assert.True(alerts.Any(a => a.Key == "Mon.Health" || a.Key == "Mon.Temp"));
    }

    private sealed class FlakyDevice(long length, params long[] badSectors) : IBlockDevice
    {
        public long Length => length;
        public int SectorSize => 512;
        public void Read(long offset, Span<byte> buffer)
        {
            foreach (var b in badSectors) if (b * 512 >= offset && b * 512 < offset + buffer.Length) throw new IOException("CRC error");
            buffer.Clear();
        }
        public void Write(long offset, ReadOnlySpan<byte> buffer) => throw new InvalidOperationException("surface scan must never write");
        public void Flush() { }
        public void Dispose() { }
    }

    [Test]
    public static void Surface_scan_locates_bad_sectors_without_writing()
    {
        var dev = new FlakyDevice(8 * 1024 * 1024, 5000, 5001, 12000);
        var r = Lasteast.Core.Performance.SurfaceScanner.Scan(dev, TimeSpan.FromSeconds(5), null, CancellationToken.None);
        Assert.True(r.Completed);
        Assert.Equal(8, r.Blocks.Count);
        Assert.Equal(2, r.ErrorBlocks);
        Assert.True(r.BadSectors.SequenceEqual(new long[] { 5000, 5001, 12000 }), string.Join(",", r.BadSectors));
    }

    [Test]
    public static void Benchmark_runs_on_a_temp_folder_and_cleans_up()
    {
        var dir = Path.Combine(Path.GetTempPath(), "lasteast-bench-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var r = Lasteast.Core.Performance.Benchmark.Run(dir, 16 * 1024 * 1024, TimeSpan.FromMilliseconds(200), bypassCache: false, null, CancellationToken.None);
            Assert.True(r.SeqReadMBps > 0 && r.SeqWriteMBps > 0 && r.Rnd4kReadIops > 0 && r.Rnd4kWriteIops > 0);
            Assert.Equal(0, Directory.GetFiles(dir).Length);
        }
        finally { Directory.Delete(dir, true); }
    }
}
