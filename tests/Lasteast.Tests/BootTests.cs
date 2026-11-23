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
