using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Lasteast.Core.Abstractions;
using Lasteast.Core.Info;
using Lasteast.Core.Logging;
using Lasteast.Core.Models;

namespace Lasteast.Storage.Windows.Info;

/// <summary>BitLocker status (Win32_EncryptableVolume), Storage Spaces (MSFT_StoragePool/VirtualDisk), shrink blockers. Read-only.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsSystemInfo : ISystemInfoProvider
{
    private readonly IAppLog _log;
    public WindowsSystemInfo(IAppLog? log = null) => _log = log ?? NullLog.Instance;

    private static dynamic Connect(string ns)
    {
        var t = Type.GetTypeFromProgID("WbemScripting.SWbemLocator", true)!;
        dynamic locator = Activator.CreateInstance(t)!;
        return locator.ConnectServer(".", ns);
    }

    private static object? Prop(dynamic o, string name)
    {
        try { return o.Properties_.Item(name).Value; } catch { return null; }
    }

    public Task<IReadOnlyList<BitLockerVolume>> BitLockerAsync() => Task.Run<IReadOnlyList<BitLockerVolume>>(() =>
    {
        var list = new List<BitLockerVolume>();
        try
        {
            dynamic svc = Connect(@"root\CIMV2\Security\MicrosoftVolumeEncryption");
            foreach (dynamic v in svc.ExecQuery("SELECT * FROM Win32_EncryptableVolume"))
            {
                string id = (string)(Prop(v, "DeviceID") ?? "");
                var letter = Prop(v, "DriveLetter") as string;
                int prot = Convert.ToInt32(Prop(v, "ProtectionStatus") ?? 2);
                int conv = Convert.ToInt32(Prop(v, "ConversionStatus") ?? -1);
                bool? locked = null;
                int? pct = null;
                string? method = null;
                try
                {
                    dynamic r = v.ExecMethod_("GetLockStatus");
                    locked = Convert.ToInt32(r.Properties_.Item("LockStatus").Value) == 1;
                }
                catch { }
                try
                {
                    dynamic r = v.ExecMethod_("GetConversionStatus");
                    pct = Convert.ToInt32(r.Properties_.Item("EncryptionPercentage").Value);
                }
                catch { }
                try
                {
                    dynamic r = v.ExecMethod_("GetEncryptionMethod");
                    method = Convert.ToInt32(r.Properties_.Item("EncryptionMethod").Value) switch
                    {
                        0 => null, 1 => "AES-128 + diffuser", 2 => "AES-256 + diffuser", 3 => "AES-128", 4 => "AES-256", 5 => "Hardware", 6 => "XTS-AES-128", 7 => "XTS-AES-256", var m => $"method {m}",
                    };
                }
                catch { }
                list.Add(new BitLockerVolume(id, letter is { Length: > 0 } ? char.ToUpperInvariant(letter[0]) : null,
                    prot switch { 0 => BitLockerProtection.Off, 1 => BitLockerProtection.On, _ => BitLockerProtection.Unknown },
                    conv switch { 0 => "FullyDecrypted", 1 => "FullyEncrypted", 2 => "EncryptionInProgress", 3 => "DecryptionInProgress", 4 => "EncryptionPaused", 5 => "DecryptionPaused", _ => "Unknown" },
                    locked, method, pct));
            }
        }
        catch (Exception ex) { _log.Info("BitLocker status unavailable: " + ex.Message); }
        return list;
    });

    public Task<StorageSpacesInfo> StorageSpacesAsync() => Task.Run(() =>
    {
        var pools = new List<StoragePoolInfo>();
        var spaces = new List<StorageSpaceInfo>();
        try
        {
            dynamic svc = Connect(@"root\Microsoft\Windows\Storage");
            static string Health(object? h) => Convert.ToInt32(h ?? 5) switch { 0 => "Healthy", 1 => "Warning", 2 => "Unhealthy", _ => "Unknown" };
            foreach (dynamic p in svc.ExecQuery("SELECT * FROM MSFT_StoragePool WHERE IsPrimordial = FALSE"))
            {
                int disks = 0;
                try { foreach (dynamic _ in svc.ExecQuery($"ASSOCIATORS OF {{MSFT_StoragePool.ObjectId=\"{((string)Prop(p, "ObjectId")!).Replace("\\", "\\\\").Replace("\"", "\\\"")}\"}} WHERE ResultClass = MSFT_PhysicalDisk")) disks++; }
                catch { }
                pools.Add(new StoragePoolInfo((string)(Prop(p, "FriendlyName") ?? "?"), Health(Prop(p, "HealthStatus")),
                    Convert.ToUInt64(Prop(p, "Size") ?? 0UL), Convert.ToUInt64(Prop(p, "AllocatedSize") ?? 0UL), disks, (bool)(Prop(p, "IsReadOnly") ?? false)));
            }
            foreach (dynamic v in svc.ExecQuery("SELECT * FROM MSFT_VirtualDisk"))
            {
                var op = Prop(v, "OperationalStatus") is Array a && a.Length > 0 ? Convert.ToInt32(a.GetValue(0)) : -1;
                spaces.Add(new StorageSpaceInfo((string)(Prop(v, "FriendlyName") ?? "?"), (string)(Prop(v, "ResiliencySettingName") ?? "?"),
                    Convert.ToUInt64(Prop(v, "Size") ?? 0UL), Health(Prop(v, "HealthStatus")),
                    op switch { 2 => "OK", 3 => "Degraded", 7 => "Lost communication", 10 => "Stopping", 53264 => "Online", 53265 => "Not ready", 53266 => "No redundancy", 53268 => "Detached", _ => op.ToString() }));
            }
        }
        catch (Exception ex) { _log.Info("Storage Spaces unavailable: " + ex.Message); }
        return new StorageSpacesInfo(pools, spaces);
    });

    public Task<ShrinkReport?> ShrinkAsync(DiskInfo disk, PartitionInfo partition) => Task.Run<ShrinkReport?>(() =>
    {
        if (partition.Volume?.DriveLetter is not { } letter) return null;
        var root = $"{letter}:\\";
        ulong? min = null;
        try
        {
            dynamic svc = Connect(@"root\Microsoft\Windows\Storage");
            foreach (dynamic p in svc.ExecQuery($"SELECT * FROM MSFT_Partition WHERE DiskNumber = {disk.Number} AND Offset = {partition.OffsetBytes}"))
            {
                dynamic r = p.ExecMethod_("GetSupportedSize");
                min = Convert.ToUInt64(r.Properties_.Item("SizeMin").Value);
            }
        }
        catch (Exception ex) { _log.Info("GetSupportedSize: " + ex.Message); }

        var blockers = new List<ShrinkBlocker>();
        void FileBlocker(string name, ShrinkBlockerKind kind, string advice)
        {
            try
            {
                var fi = new FileInfo(root + name);
                if (fi.Exists && fi.Length > 0) blockers.Add(new ShrinkBlocker(kind, (ulong)fi.Length, fi.FullName, advice));
            }
            catch { }
        }
        FileBlocker("pagefile.sys", ShrinkBlockerKind.PageFile, "Shrink.Adv.PageFile");
        FileBlocker("hiberfil.sys", ShrinkBlockerKind.Hibernation, "Shrink.Adv.Hiber");
        FileBlocker("swapfile.sys", ShrinkBlockerKind.SwapFile, "Shrink.Adv.PageFile");
        try
        {
            dynamic cim = Connect(@"root\cimv2");
            var guid = partition.Volume.VolumeGuidPath;
            foreach (dynamic s in cim.ExecQuery("SELECT * FROM Win32_ShadowStorage"))
            {
                var vol = Prop(s, "Volume") as string ?? "";
                if (guid.Length > 10 && vol.Contains(guid[10..48], StringComparison.OrdinalIgnoreCase))
                {
                    var used = Convert.ToUInt64(Prop(s, "UsedSpace") ?? 0UL);
                    if (used > 0) blockers.Add(new ShrinkBlocker(ShrinkBlockerKind.ShadowCopies, used, null, "Shrink.Adv.Shadow"));
                }
            }
        }
        catch (Exception ex) { _log.Info("Win32_ShadowStorage: " + ex.Message); }
        return new ShrinkReport(letter, partition.SizeBytes, min, partition.Volume.UsedBytes ?? 0, blockers);
    });
}
