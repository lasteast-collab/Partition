using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Lasteast.Core.Abstractions;
using Lasteast.Core.Files;
using Lasteast.Core.Logging;
using Lasteast.Core.Models;
using Lasteast.Core.Operations;
using Lasteast.Core.PartitionTables;
using Lasteast.Core.RawDisk;
using Lasteast.Core.Recovery;
using Lasteast.Core.Wipe;
using Microsoft.Win32.SafeHandles;

namespace Lasteast.Storage.Windows.Operations;

/// <summary>Runs a Windows tool and captures its output (stdin optional, for tools that ask a question).</summary>
[SupportedOSPlatform("windows")]
public static class Tool
{
    public static (int Exit, string Output) Run(string exe, string args, string? stdin = null, int timeoutMs = 3_600_000)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            RedirectStandardInput = stdin is not null,
        };
        using var p = Process.Start(psi) ?? throw new OperationFailedException("Exec.Error.Tool", exe);
        if (stdin is not null) { p.StandardInput.Write(stdin); p.StandardInput.Close(); }
        var outTask = p.StandardOutput.ReadToEndAsync();
        var errTask = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch { } throw new OperationFailedException("Exec.Error.Tool", $"{exe} timed out"); }
        return (p.ExitCode, (outTask.Result + errTask.Result).Trim());
    }
}

/// <summary>
/// Executors for the operations beyond basic partition editing: file-level merge copies, partition copies, wipes,
/// device sanitize, file-system repair/conversion, reformat keeping files, disk type and primary/logical conversion.
/// Only <see cref="GuardedExecutionEngine"/> reaches these (through <see cref="WindowsOperationExecutor"/>).
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class ExtendedExecutor(IStorageInventoryProvider inventory, StorageManagementExecutor wmi, IAppLog log)
{
    public IProgress<RawProgress>? Progress { get; set; }

    private static PartitionInfo Part(DiskInfo d, ulong offset) =>
        d.Partitions.FirstOrDefault(p => p.OffsetBytes == offset && p.Role != PartitionRole.MbrExtended)
        ?? throw new OperationFailedException("Op.Error.NotFound");

    private static string Root(PartitionInfo p) =>
        (p.Volume?.VolumeGuidPath is { } g && g.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase) ? g.TrimEnd('\\') + "\\" : null)
        ?? throw new OperationFailedException("Op.Error.Unreadable");

    private static string ToolTarget(PartitionInfo p) => p.Volume?.DriveLetter is { } l ? $"{l}:" : Root(p).TrimEnd('\\');

    private async Task<DiskInfo> Disk(int number) =>
        (await inventory.GetInventoryAsync(CancellationToken.None)).Disks.FirstOrDefault(d => d.Number == number)
        ?? throw new OperationFailedException("Exec.Error.DiskMissing", null, number);

    private IProgress<TreeCopyProgress> CopyProgress(string phase) =>
        new Progress<TreeCopyProgress>(p => Progress?.Report(new RawProgress(phase, p.Bytes, Math.Max(1, p.TotalBytes))));

    private void RequireClean(TreeCopyReport r, string what)
    {
        if (r.Ok) return;
        foreach (var (path, err) in r.Errors.Take(50)) log.Warn($"{what}: {path}: {err}");
        throw new OperationFailedException("Merge.Error.Copy", $"{r.Errors.Count} file(s) could not be copied: " + string.Join("; ", r.Errors.Take(3).Select(e => $"{e.Path}: {e.Error}")));
    }

    public async Task CopyFilesAsync(CopyFilesOp c, DiskInfo destDisk, CancellationToken ct)
    {
        var src = Part(c.SourceDiskNumber == destDisk.Number ? destDisk : await Disk(c.SourceDiskNumber), c.SourceOffsetBytes);
        var dst = Part(destDisk, c.DestOffsetBytes);
        var target = Path.Combine(Root(dst), c.Folder);
        if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any()) throw new OperationFailedException("Merge.Error.FolderExists", target);
        var report = await Task.Run(() => TreeCopier.Copy(Root(src), target, CopyProgress("Copy files"), ct));
        log.Info($"Copied {report.Files} file(s), {report.Bytes} bytes from {Root(src)} to {target}; {report.Errors.Count} error(s)");
        RequireClean(report, "Copy");
    }

    public async Task CopyPartitionAsync(CopyPartitionOp c, DiskInfo destDisk, CancellationToken ct)
    {
        var srcDisk = c.SourceDiskNumber == destDisk.Number ? destDisk : await Disk(c.SourceDiskNumber);
        var src = Part(srcDisk, c.SourceOffsetBytes);
        await Task.Run(() =>
        {
            VolumeLocks? srcLock = null;
            ShadowCopy? snap = null;
            SafeFileHandle? snapHandle = null;
            try
            {
                if (src.Volume?.VolumeGuidPath is { } g && g.StartsWith(@"\\?\Volume{"))
                {
                    srcLock = VolumeLocks.TryAcquire(g);
                    if (srcLock is null && string.Equals(src.Volume.FileSystem, "NTFS", StringComparison.OrdinalIgnoreCase))
                    {
                        snap = ShadowCopy.Create(g);
                        snapHandle = RawNative.Open(snap.DevicePath, write: false);
                    }
                }
                IReadOnlyList<ByteRange>? used = null;
                if (string.Equals(src.Volume?.FileSystem, "NTFS", StringComparison.OrdinalIgnoreCase) && src.Volume?.ClusterSizeBytes is { } cs)
                    used = VolumeBitmap.UsedRanges(snap?.DevicePath ?? src.Volume.VolumeGuidPath, (long)src.OffsetBytes, cs)
                           ?.Select(r => new ByteRange(r.Offset - (long)src.OffsetBytes, r.Length)).ToList();
                using var sdev = new PhysicalDiskDevice(srcDisk, writable: false);
                using var ddev = new PhysicalDiskDevice(destDisk, writable: true);
                RangeReader read = (offset, buffer) =>
                {
                    if (snapHandle is not null && offset - (long)src.OffsetBytes + buffer.Length <= (long)src.SizeBytes)
                    {
                        int total = 0;
                        try
                        {
                            while (total < buffer.Length)
                            {
                                int n = RandomAccess.Read(snapHandle, buffer[total..], offset - (long)src.OffsetBytes + total);
                                if (n <= 0) break;
                                total += n;
                            }
                        }
                        catch (IOException) { total = 0; }
                        if (total == buffer.Length) return;
                    }
                    sdev.Read(offset, buffer);
                };
                // the copy is written into unallocated space; Windows only sees it once the table entry exists
                var rep = PartitionCopier.Copy(read, ddev, (long)src.OffsetBytes, (long)c.DestOffsetBytes, (long)src.SizeBytes, used, verify: true, Progress, ct);
                PartitionMover.PatchHiddenSectors(ddev, (long)c.DestOffsetBytes, (long)src.SizeBytes);
                ulong size = (c.SizeBytes + 1024 * 1024 - 1) / (1024 * 1024) * (1024 * 1024);
                PartitionTableEditor.AddEntry(ddev, c.DestOffsetBytes, size, src.Volume?.FileSystem ?? "NTFS");
                ddev.Rescan();
                log.Info($"Copied partition {src.OffsetBytes} of disk {srcDisk.Number} to {c.DestOffsetBytes} on disk {destDisk.Number}: {rep.BytesCopied} bytes, verified");
            }
            catch (RawOperationException ex) { throw new OperationFailedException(ex.Key, ex.Message); }
            finally
            {
                snapHandle?.Dispose();
                snap?.Dispose();
                srcLock?.Dispose();
            }
        }, ct);
        await Task.Delay(2000, CancellationToken.None);
    }

    public Task WipeAsync(WipeOp w, DiskInfo disk, CancellationToken ct) => Task.Run(() =>
    {
        var volumes = (w.OffsetBytes is { } off ? disk.Partitions.Where(p => p.OffsetBytes == off) : disk.Partitions)
            .Select(p => p.Volume?.VolumeGuidPath).OfType<string>().Where(g => g.StartsWith(@"\\?\Volume{")).ToList();
        using var locks = VolumeLocks.Acquire(volumes, dismount: true);
        using var dev = new PhysicalDiskDevice(disk, writable: true);
        long offset = w.OffsetBytes is { } o ? (long)o : 0;
        long length = w.OffsetBytes is { } o2 ? (long)Part(disk, o2).SizeBytes : (long)disk.SizeBytes / disk.BytesPerLogicalSector * disk.BytesPerLogicalSector;
        try
        {
            var rep = Wiper.Run(dev, offset, length, w.Method, w.Verify, Progress, ct);
            log.Info($"Wiped disk {disk.Number} {(w.OffsetBytes is null ? "(whole disk)" : $"partition @ {offset}")}: {rep.Bytes} bytes, {rep.Passes} pass(es), verified {rep.VerifiedBytes} bytes");
        }
        catch (RawOperationException ex) { throw new OperationFailedException(ex.Key, ex.Message); }
        dev.Rescan();
    }, ct);

    private const uint IOCTL_STORAGE_REINITIALIZE_MEDIA = 0x002D9640;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle h, uint code, byte[] inBuf, int inSize, IntPtr outBuf, int outSize, out int returned, IntPtr overlapped);

    /// <summary>NVMe/SSD sanitize through Windows' reinitialize-media request; the drive firmware does the erasing.</summary>
    public Task SanitizeAsync(DiskInfo disk) => Task.Run(() =>
    {
        var volumes = disk.Partitions.Select(p => p.Volume?.VolumeGuidPath).OfType<string>().Where(g => g.StartsWith(@"\\?\Volume{")).ToList();
        using var locks = VolumeLocks.Acquire(volumes, dismount: true);
        using var h = RawNative.Open(disk.DevicePath, write: true);
        var input = new byte[16];
        BitConverter.GetBytes(16).CopyTo(input, 0);   // Version
        BitConverter.GetBytes(16).CopyTo(input, 4);   // Size
        BitConverter.GetBytes(3600).CopyTo(input, 8); // TimeoutInSeconds
        // SanitizeOption = 0: the device's default sanitize/secure-format action
        if (!DeviceIoControl(h, IOCTL_STORAGE_REINITIALIZE_MEDIA, input, input.Length, IntPtr.Zero, 0, out _, IntPtr.Zero))
        {
            int err = Marshal.GetLastWin32Error();
            throw new OperationFailedException(err is 1 or 50 or 87 ? "Sanitize.Error.NotSupported" : "Sanitize.Error.Failed", $"0x{err:X8}");
        }
        log.Info($"Sanitize completed on disk {disk.Number} ({disk.Model}, {disk.SerialNumber})");
        RawNative.Control(h, RawNative.IOCTL_DISK_UPDATE_PROPERTIES);
    });

    public Task RepairFileSystemAsync(RepairFileSystemOp r, DiskInfo disk) => Task.Run(() =>
    {
        var p = Part(disk, r.OffsetBytes);
        var (exit, output) = Tool.Run("chkdsk.exe", $"{ToolTarget(p)} /f /x");
        log.Info($"chkdsk {ToolTarget(p)} /f /x: exit {exit}\n{output}");
        // 0: no errors, 1: errors found and fixed, 2: cleanup done; 3: could not check / fix
        if (exit >= 3) throw new OperationFailedException("Chk.Error.Failed", LastLines(output));
    });

    private static string LastLines(string s) => string.Join(" ", s.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).TakeLast(3));

    public Task ConvertFileSystemAsync(ConvertFileSystemOp c, DiskInfo disk) => Task.Run(() =>
    {
        var p = Part(disk, c.OffsetBytes);
        if (p.Volume?.DriveLetter is not { } letter) throw new OperationFailedException("ConvFs.Error.Letter");
        // convert.exe asks for the current label to confirm the volume: answer with the label we read
        var (exit, output) = Tool.Run("convert.exe", $"{letter}: /fs:ntfs /x", stdin: (p.Volume.Label ?? "") + "\r\n");
        log.Info($"convert {letter}: /fs:ntfs /x: exit {exit}\n{output}");
        if (exit != 0) throw new OperationFailedException("ConvFs.Error.Failed", LastLines(output));
    });

    public async Task ReformatKeepingFilesAsync(ReformatKeepingFilesOp r, DiskInfo disk, CancellationToken ct)
    {
        var p = Part(disk, r.OffsetBytes);
        var root = Root(p);
        var staging = Path.Combine(r.StagingFolder, $"Lasteast-reformat-disk{disk.Number}-{r.OffsetBytes}-{DateTime.Now:yyyyMMdd-HHmmss}");
        if (Path.GetFullPath(staging).StartsWith(root, StringComparison.OrdinalIgnoreCase)
            || p.Volume?.DriveLetter is { } l && Path.GetFullPath(staging).StartsWith($"{l}:", StringComparison.OrdinalIgnoreCase))
            throw new OperationFailedException("Reformat.Error.Staging");
        var outRep = await Task.Run(() => TreeCopier.Copy(root, staging, CopyProgress("Copy files out"), ct), ct);
        RequireClean(outRep, "Copy out");
        log.Info($"Reformat: {outRep.Files} file(s), {outRep.Bytes} bytes safe in {staging}");
        try
        {
            var label = p.Volume?.Label is { } lb ? lb[..Math.Min(lb.Length, FileSystems.MaxLabelLength(r.FileSystem))] : null;
            await wmi.ExecuteAsync(new FormatPartitionOp(disk.Number, r.OffsetBytes, r.FileSystem, label, r.ClusterSizeBytes), disk, CancellationToken.None);
            await Task.Delay(1500, CancellationToken.None);
            var back = await Task.Run(() => TreeCopier.Copy(staging, root, CopyProgress("Copy files back"), ct), ct);
            RequireClean(back, "Copy back");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // the files are still in the staging folder: say where
            throw new OperationFailedException("Reformat.Error.KeptIn", staging + " — " + ex.Message);
        }
        try { Directory.Delete(staging, true); } catch (IOException ex) { log.Warn($"Staging folder {staging} not removed: {ex.Message}"); }
    }

    public Task ConvertDiskTypeAsync(ConvertDiskTypeOp c, DiskInfo disk) => Task.Run(() =>
    {
        var script = Path.Combine(Path.GetTempPath(), $"lasteast-disktype-{Guid.NewGuid():N}.txt");
        File.WriteAllText(script, $"select disk {disk.Number}\r\nconvert {(c.ToDynamic ? "dynamic" : "basic")}\r\n");
        try
        {
            var (exit, output) = Tool.Run("diskpart.exe", $"/s \"{script}\"");
            log.Info($"diskpart convert {(c.ToDynamic ? "dynamic" : "basic")} on disk {disk.Number}: exit {exit}\n{output}");
            if (exit != 0) throw new OperationFailedException("DiskType.Error.Failed", LastLines(output));
        }
        finally { File.Delete(script); }
    });

    /// <summary>bootsect.exe ships with Windows PE / WinRE and the ADK, not with every Windows: look in the usual places.</summary>
    private static string? FindBootsect()
    {
        var candidates = new List<string> { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "bootsect.exe") };
        foreach (var pf in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) })
            candidates.Add(Path.Combine(pf, "Windows Kits", "10", "Assessment and Deployment Kit", "Deployment Tools", "amd64", "BCDBoot", "bootsect.exe"));
        return candidates.FirstOrDefault(File.Exists);
    }

    public Task RepairMbrAsync(RepairMbrOp r, DiskInfo disk) => Task.Run(() =>
    {
        if (r.BackupFolder is { } folder)
        {
            var backup = Lasteast.Core.Boot.TableBackups.Load(folder, (int)disk.BytesPerLogicalSector);
            if (backup.Style != PartitionStyle.Mbr || backup.Head.Length < 512 || backup.Head[510] != 0x55 || backup.Head[511] != 0xAA)
                throw new OperationFailedException("Mbr.Error.Backup", folder);
            using var dev = new PhysicalDiskDevice(disk, writable: true);
            var s0 = new byte[disk.BytesPerLogicalSector];
            dev.Read(0, s0);
            backup.Head.AsSpan(0, 440).CopyTo(s0); // boot code only; signature and partition entries stay as they are now
            dev.Write(0, s0);
            dev.Flush();
            var check = new byte[s0.Length];
            dev.Read(0, check);
            if (!check.AsSpan().SequenceEqual(s0)) throw new OperationFailedException("Mbr.Error.Verify");
            log.Info($"MBR boot code of disk {disk.Number} restored from {folder}");
            return;
        }
        var tool = FindBootsect() ?? throw new OperationFailedException("Mbr.Error.NoBootsect");
        var target = disk.Partitions.Select(p => p.Volume?.DriveLetter).FirstOrDefault(l => l is not null)
                     ?? throw new OperationFailedException("Mbr.Error.NoLetter");
        var (exit, output) = Tool.Run(tool, $"/nt60 {target}: /mbr");
        log.Info($"bootsect /nt60 {target}: /mbr: exit {exit}\n{output}");
        if (exit != 0) throw new OperationFailedException("Mbr.Error.Bootsect", LastLines(output));
    });

    public Task ConvertPartitionKindAsync(ConvertPartitionKindOp c, DiskInfo disk) => Task.Run(() =>
    {
        using var locks = VolumeLocks.Acquire(disk.Partitions.Select(p => p.Volume?.VolumeGuidPath).OfType<string>()
            .Where(g => g.StartsWith(@"\\?\Volume{") && disk.Partitions.Any(p => p.Volume?.VolumeGuidPath == g && p.Volume?.IsWindowsVolume != true)), dismount: true);
        using var dev = new PhysicalDiskDevice(disk, writable: true);
        try { MbrKindConverter.Apply(dev, c.OffsetBytes, c.ToLogical); }
        catch (RawOperationException ex) { throw new OperationFailedException(ex.Key, ex.Message); }
        dev.Rescan();
    });
}
