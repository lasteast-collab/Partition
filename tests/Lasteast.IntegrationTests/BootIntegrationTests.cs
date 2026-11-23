using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Lasteast.Core.Logging;
using Lasteast.Core.Models;
using Lasteast.Core.Operations;
using Lasteast.Storage.Windows.Detection;
using Lasteast.Storage.Windows.Operations;
using Microsoft.Win32;

namespace Lasteast.IntegrationTests;

/// <summary>Phase 6: boot preparation (bcdboot) on a virtual disk and offline registry editing on a scratch hive.</summary>
[SupportedOSPlatform("windows")]
public static class BootIntegrationTests
{
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegSaveKey(IntPtr hKey, string file, IntPtr security);

    [Test]
    public static void Offline_hive_maps_c_to_new_partition_and_removes_duplicate_letter()
    {
        var dir = Path.Combine(Path.GetTempPath(), "lasteast-itest", "hive-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var hive = Path.Combine(dir, "SYSTEM");
        var keyName = @"Software\LasteastHiveTest" + Guid.NewGuid().ToString("N");
        var oldC = new byte[] { 1, 2, 3, 4, 0, 0, 16, 0, 0, 0, 0, 0 };
        var newId = OfflineHive.GptIdentity(Guid.NewGuid());
        try
        {
            using (var k = Registry.CurrentUser.CreateSubKey(keyName + @"\MountedDevices"))
            {
                k.SetValue(@"\DosDevices\C:", oldC, RegistryValueKind.Binary);
                k.SetValue(@"\DosDevices\E:", newId, RegistryValueKind.Binary);
            }
            Privileges.Enable("SeBackupPrivilege", "SeRestorePrivilege");
            using (var root = Registry.CurrentUser.OpenSubKey(keyName)!)
                Assert.Equal(0, RegSaveKey(root.Handle.DangerousGetHandle(), hive, IntPtr.Zero));

            var previous = OfflineHive.SetDriveLetter(hive, 'C', newId);
            Assert.True(previous is not null && previous.AsSpan().SequenceEqual(oldC), "previous C: value returned");
            Assert.True(OfflineHive.ReadDriveLetter(hive, 'C')!.AsSpan().SequenceEqual(newId), "C: now maps to the new partition");
            Assert.Equal(null, OfflineHive.ReadDriveLetter(hive, 'E'));

            // never the running system
            var running = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "config", "SYSTEM");
            bool refused = false;
            try { OfflineHive.SetDriveLetter(running, 'C', newId); }
            catch (OperationFailedException ex) { refused = ex.Key == "Boot.Error.RunningSystem"; }
            Assert.True(refused, "running hive refused");
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(keyName, false);
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Test]
    public static async Task Prepare_boot_writes_bcd_to_esp_of_virtual_disk()
    {
        using var vd = await VirtualDisk.CreateAsync(1807, "convert gpt", "create partition efi size=100", "format fs=fat32 quick label=LE-ESP",
            "create partition primary size=300", "format fs=ntfs quick label=LE-WIN");
        var dir = Path.Combine(Path.GetTempPath(), "lasteast-itest", "boot-" + Guid.NewGuid().ToString("N"));
        var log = new FileLog(Path.Combine(dir, "logs"));
        var priv = new WindowsPrivilegeService();
        var provider = new WindowsStorageInventoryProvider(log, priv);
        var engine = new GuardedExecutionEngine(provider, new WindowsOperationExecutor(provider, log, Path.Combine(dir, "journal")),
            new PartitionTableBackupService(Path.Combine(dir, "backups")), priv, new JsonlOperationHistory(Path.Combine(dir, "h.jsonl")), log);

        // Boot files come from the runner's Windows folder (only read); the registry is not touched in override mode.
        var op = new PrepareBootOp(vd.Number, FixDriveLetter: false, WindowsDirectoryOverride: Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        var r = await engine.ExecuteAsync(new DiskOperation[] { op }, await provider.GetInventoryAsync(), new ExecutionPolicy(false), "APPLY");
        Assert.True(r.Succeeded, $"{r.FailureKey} {r.FailureDetail}");

        var esp = (await vd.ReadAsync()).Partitions.Single(p => p.Role == PartitionRole.EfiSystem);
        Assert.Equal(null, esp.Volume?.DriveLetter); // the temporary letter was removed
        var letter = 'Y';
        while (DriveInfo.GetDrives().Any(d => char.ToUpperInvariant(d.Name[0]) == letter)) letter--;
        VirtualDisk.DiskPart($"select disk {vd.Number}", $"select partition {esp.PartitionNumber}", $"assign letter={letter}");
        try
        {
            Assert.True(File.Exists($@"{letter}:\EFI\Microsoft\Boot\BCD"), "BCD store on the ESP");
            Assert.True(File.Exists($@"{letter}:\EFI\Microsoft\Boot\bootmgfw.efi"), "boot manager on the ESP");
        }
        finally
        {
            VirtualDisk.DiskPart($"select disk {vd.Number}", $"select partition {esp.PartitionNumber}", $"remove letter={letter}");
        }
    }
}
