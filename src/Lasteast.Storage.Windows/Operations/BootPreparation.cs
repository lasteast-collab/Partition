using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Lasteast.Core.Abstractions;
using Lasteast.Core.Logging;
using Lasteast.Core.Models;
using Lasteast.Core.Operations;
using Microsoft.Win32;

namespace Lasteast.Storage.Windows.Operations;

/// <summary>
/// Offline editing of another Windows installation's SYSTEM hive (never the running one).
/// Used to point the copy's C: at its own partition after a clone with new identifiers.
/// </summary>
[SupportedOSPlatform("windows")]
public static class OfflineHive
{
    private const string MountName = "LasteastOffline";
    private static readonly UIntPtr HKEY_LOCAL_MACHINE = new(0x80000002u);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] private static extern int RegLoadKey(UIntPtr hKey, string subKey, string file);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] private static extern int RegUnLoadKey(UIntPtr hKey, string subKey);

    /// <summary>MountedDevices identity of a GPT partition: "DMIO:ID:" + partition GUID.</summary>
    public static byte[] GptIdentity(Guid partitionId) => Encoding.ASCII.GetBytes("DMIO:ID:").Concat(partitionId.ToByteArray()).ToArray();

    /// <summary>MountedDevices identity of an MBR partition: disk signature + byte offset.</summary>
    public static byte[] MbrIdentity(uint signature, ulong offset) => BitConverter.GetBytes(signature).Concat(BitConverter.GetBytes(offset)).ToArray();

    /// <summary>
    /// In the hive file, maps <c>\DosDevices\{letter}:</c> to <paramref name="identity"/> and removes any other letter
    /// pointing at the same partition. Returns the previous value of that letter (null if none).
    /// </summary>
    public static byte[]? SetDriveLetter(string hiveFile, char letter, byte[] identity)
    {
        if (!File.Exists(hiveFile)) throw new OperationFailedException("Boot.Error.Hive", hiveFile);
        var running = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "config", "SYSTEM");
        if (string.Equals(Path.GetFullPath(hiveFile), Path.GetFullPath(running), StringComparison.OrdinalIgnoreCase))
            throw new OperationFailedException("Boot.Error.RunningSystem", "refusing to edit the running system hive");

        Privileges.Enable("SeBackupPrivilege", "SeRestorePrivilege");
        int rc = RegLoadKey(HKEY_LOCAL_MACHINE, MountName, hiveFile);
        if (rc != 0) throw new OperationFailedException("Boot.Error.Hive", $"RegLoadKey {rc}");
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"{MountName}\MountedDevices", writable: true)
                            ?? Registry.LocalMachine.CreateSubKey($@"{MountName}\MountedDevices", true);
            var name = $@"\DosDevices\{char.ToUpperInvariant(letter)}:";
            var previous = key.GetValue(name) as byte[];
            foreach (var other in key.GetValueNames().Where(n => n.StartsWith(@"\DosDevices\", StringComparison.OrdinalIgnoreCase) && n != name))
                if (key.GetValue(other) is byte[] v && v.AsSpan().SequenceEqual(identity)) key.DeleteValue(other);
            key.SetValue(name, identity, RegistryValueKind.Binary);
            key.Flush();
            return previous;
        }
        finally
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            RegUnLoadKey(HKEY_LOCAL_MACHINE, MountName);
        }
    }

    public static byte[]? ReadDriveLetter(string hiveFile, char letter)
    {
        Privileges.Enable("SeBackupPrivilege", "SeRestorePrivilege");
        int rc = RegLoadKey(HKEY_LOCAL_MACHINE, MountName, hiveFile);
        if (rc != 0) throw new OperationFailedException("Boot.Error.Hive", $"RegLoadKey {rc}");
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"{MountName}\MountedDevices");
            return key?.GetValue($@"\DosDevices\{char.ToUpperInvariant(letter)}:") as byte[];
        }
        finally
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            RegUnLoadKey(HKEY_LOCAL_MACHINE, MountName);
        }
    }
}

[SupportedOSPlatform("windows")]
public static class Privileges
{
    [StructLayout(LayoutKind.Sequential)]
    private struct LUID { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_PRIVILEGES { public uint Count; public LUID Luid; public uint Attributes; }

    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool LookupPrivilegeValue(string? system, string name, out LUID luid);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TOKEN_PRIVILEGES state, uint len, IntPtr prev, IntPtr retLen);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);

    public static void Enable(params string[] names)
    {
        if (!OpenProcessToken(GetCurrentProcess(), 0x0020 | 0x0008, out var token)) return;
        try
        {
            foreach (var n in names)
            {
                if (!LookupPrivilegeValue(null, n, out var luid)) continue;
                var tp = new TOKEN_PRIVILEGES { Count = 1, Luid = luid, Attributes = 0x2 };
                AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
            }
        }
        finally { CloseHandle(token); }
    }
}

/// <summary>Executes <see cref="PrepareBootOp"/>: temporary drive letters, bcdboot, offline MountedDevices, cleanup.</summary>
[SupportedOSPlatform("windows")]
public sealed class BootPreparer
{
    private readonly IAppLog _log;
    public BootPreparer(IAppLog? log = null) => _log = log ?? NullLog.Instance;

    [DllImport("kernel32.dll", EntryPoint = "SetVolumeMountPointW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetVolumeMountPoint(string mountPoint, string volumeName);
    [DllImport("kernel32.dll", EntryPoint = "DeleteVolumeMountPointW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool DeleteVolumeMountPoint(string mountPoint);

    private static string Root(string guidPath) => guidPath.EndsWith('\\') ? guidPath : guidPath + "\\";

    private static char FreeLetter(ISet<char> taken)
    {
        var used = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
        for (char c = 'Z'; c >= 'H'; c--) if (!used.Contains(c) && !taken.Contains(c)) return c;
        throw new OperationFailedException("Boot.Error.NoLetter");
    }

    public void Prepare(DiskInfo disk, PrepareBootOp op, string? backupFolder = null)
    {
        var parts = disk.Partitions.Where(p => p.Role != PartitionRole.MbrExtended).ToList();
        var system = disk.Style == PartitionStyle.Gpt
            ? parts.FirstOrDefault(p => p.Role == PartitionRole.EfiSystem)
            : parts.FirstOrDefault(p => p.MbrActive) ?? parts.FirstOrDefault(p => p.Volume?.FileSystem is not null);
        if (system?.Volume?.VolumeGuidPath is not { } sysVol || !sysVol.StartsWith(@"\\?\Volume{"))
            throw new OperationFailedException("Boot.Error.NoEsp", "system partition has no volume");

        PartitionInfo? winPart = null;
        if (op.WindowsDirectoryOverride is null)
        {
            winPart = parts.FirstOrDefault(p => p.Volume?.VolumeGuidPath is { } v && v.StartsWith(@"\\?\Volume{")
                                                && File.Exists(Root(v) + @"Windows\System32\config\SYSTEM"));
            if (winPart is null) throw new OperationFailedException("Boot.Error.NoWindows");
        }

        var mounted = new List<string>();
        var taken = new HashSet<char>();
        try
        {
            string Mount(string guid)
            {
                var l = FreeLetter(taken);
                taken.Add(l);
                var mp = $"{l}:\\";
                if (!SetVolumeMountPoint(mp, Root(guid))) throw new OperationFailedException("Boot.Error.Mount", $"SetVolumeMountPoint {Marshal.GetLastWin32Error()}");
                mounted.Add(mp);
                return mp;
            }

            var s = Mount(sysVol);
            string windowsDir;
            if (winPart is not null)
            {
                var w = winPart.Volume!.DriveLetter is { } dl ? $"{dl}:\\" : Mount(winPart.Volume.VolumeGuidPath);
                windowsDir = w + "Windows";
            }
            else windowsDir = op.WindowsDirectoryOverride!;

            var firmware = disk.Style == PartitionStyle.Gpt ? "UEFI" : "BIOS";
            if (backupFolder is not null)
            {
                // keep the existing boot files before bcdboot replaces them
                foreach (var dir in new[] { s + @"EFI\Microsoft\Boot", s + "Boot" }.Where(Directory.Exists))
                foreach (var f in Directory.EnumerateFiles(dir, "BCD*", SearchOption.TopDirectoryOnly))
                {
                    var target = Path.Combine(backupFolder, Path.GetRelativePath(s, f));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(f, target, overwrite: false);
                }
            }
            var (code, output) = Run("bcdboot.exe", $"\"{windowsDir}\" /s {s.TrimEnd('\\')} /f {firmware}");
            _log.Info($"bcdboot exit {code}: {output}");
            if (code != 0) throw new OperationFailedException("Boot.Error.Bcdboot", output.Trim());
            var bcd = disk.Style == PartitionStyle.Gpt ? s + @"EFI\Microsoft\Boot\BCD" : s + @"Boot\BCD";
            if (!File.Exists(bcd)) throw new OperationFailedException("Boot.Error.Bcdboot", "BCD store not found after bcdboot: " + bcd);

            if (op.FixDriveLetter && winPart is not null)
            {
                var identity = disk.Style == PartitionStyle.Gpt
                    ? OfflineHive.GptIdentity(winPart.GptId ?? throw new OperationFailedException("Boot.Error.Hive", "partition GUID unknown"))
                    : OfflineHive.MbrIdentity(disk.MbrSignature ?? throw new OperationFailedException("Boot.Error.Hive", "disk signature unknown"), winPart.OffsetBytes);
                var hive = Path.Combine(windowsDir, "System32", "config", "SYSTEM");
                OfflineHive.SetDriveLetter(hive, 'C', identity);
                _log.Info($"MountedDevices C: of the copy now points at its own partition ({Convert.ToHexString(identity)})");
            }
        }
        finally
        {
            foreach (var mp in mounted) DeleteVolumeMountPoint(mp);
        }
    }

    private static (int Code, string Output) Run(string exe, string args)
    {
        var psi = new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit(120_000);
        return (p.ExitCode, o);
    }
}
