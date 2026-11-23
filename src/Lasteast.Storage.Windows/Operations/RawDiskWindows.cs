using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Lasteast.Core.Abstractions;
using Lasteast.Core.Logging;
using Lasteast.Core.Models;
using Lasteast.Core.RawDisk;
using Lasteast.Storage.Windows.Interop;
using Microsoft.Win32.SafeHandles;

namespace Lasteast.Storage.Windows.Operations;

[SupportedOSPlatform("windows")]
internal static class RawNative
{
    public const uint GENERIC_READ = 0x80000000;
    public const uint GENERIC_WRITE = 0x40000000;
    public const uint FILE_FLAG_WRITE_THROUGH = 0x80000000;
    public const uint FSCTL_LOCK_VOLUME = 0x00090018;
    public const uint FSCTL_UNLOCK_VOLUME = 0x0009001C;
    public const uint FSCTL_DISMOUNT_VOLUME = 0x00090020;
    public const uint FSCTL_GET_VOLUME_BITMAP = 0x0009006F;
    public const uint IOCTL_DISK_UPDATE_PROPERTIES = 0x00070140;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool FlushFileBuffers(SafeFileHandle h);

    public static SafeFileHandle Open(string path, bool write, bool writeThrough = false)
    {
        var h = NativeMethods.CreateFile(path, write ? GENERIC_READ | GENERIC_WRITE : GENERIC_READ,
            NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE, IntPtr.Zero, NativeMethods.OPEN_EXISTING,
            writeThrough ? FILE_FLAG_WRITE_THROUGH : NativeMethods.FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);
        if (h.IsInvalid) throw new RawOperationException("Raw.Error.Open", NativeMethods.LastError("open " + path).Message);
        return h;
    }

    public static bool Control(SafeFileHandle h, uint code) =>
        NativeMethods.DeviceIoControl(h, code, null, 0, null, 0, out _, IntPtr.Zero);
}

/// <summary>\\.\PhysicalDriveN as an <see cref="IBlockDevice"/> (write-through when writable).</summary>
[SupportedOSPlatform("windows")]
public sealed class PhysicalDiskDevice : IBlockDevice
{
    private readonly SafeFileHandle _h;
    private readonly bool _writable;

    public PhysicalDiskDevice(DiskInfo disk, bool writable)
    {
        _writable = writable;
        _h = RawNative.Open(disk.DevicePath, writable, writeThrough: writable);
        Length = (long)disk.SizeBytes;
        SectorSize = (int)disk.BytesPerLogicalSector;
        Number = disk.Number;
    }

    public int Number { get; }
    public long Length { get; }
    public int SectorSize { get; }

    public void Read(long offset, Span<byte> buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = RandomAccess.Read(_h, buffer[total..], offset + total);
            if (n <= 0) throw new IOException($"short read at {offset + total} on disk {Number}");
            total += n;
        }
    }

    public void Write(long offset, ReadOnlySpan<byte> buffer)
    {
        if (!_writable) throw new InvalidOperationException("device opened read-only");
        RandomAccess.Write(_h, buffer, offset);
    }

    public void Flush()
    {
        if (_writable && !RawNative.FlushFileBuffers(_h)) throw NativeMethods.LastError("FlushFileBuffers");
    }

    /// <summary>Tells Windows to re-read the partition table.</summary>
    public void Rescan() => RawNative.Control(_h, RawNative.IOCTL_DISK_UPDATE_PROPERTIES);

    public void Dispose() => _h.Dispose();
}

/// <summary>
/// Locks and dismounts volumes for the duration of a raw operation. Windows refuses raw writes to sectors of a
/// mounted volume, and a lock guarantees no program writes while we copy. Disposing unlocks (Windows remounts on demand).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class VolumeLocks : IDisposable
{
    private readonly List<SafeFileHandle> _handles = new();

    /// <summary>Locks every volume in <paramref name="volumeGuidPaths"/>; throws (after releasing) if any lock fails.</summary>
    public static VolumeLocks Acquire(IEnumerable<string> volumeGuidPaths, bool dismount)
    {
        var locks = new VolumeLocks();
        try
        {
            foreach (var v in volumeGuidPaths.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var h = RawNative.Open(v.TrimEnd('\\'), write: true);
                bool locked = false;
                for (int i = 0; i < 10 && !locked; i++)
                {
                    locked = RawNative.Control(h, RawNative.FSCTL_LOCK_VOLUME);
                    if (!locked) Thread.Sleep(300);
                }
                if (!locked)
                {
                    h.Dispose();
                    throw new RawOperationException("Raw.Error.Lock", $"volume {v} is in use (open files, page file or system volume) and cannot be locked");
                }
                if (dismount) RawNative.Control(h, RawNative.FSCTL_DISMOUNT_VOLUME);
                locks._handles.Add(h);
            }
            return locks;
        }
        catch
        {
            locks.Dispose();
            throw;
        }
    }

    /// <summary>Tries to lock one volume; returns null when it is in use.</summary>
    public static VolumeLocks? TryAcquire(string volumeGuidPath)
    {
        try { return Acquire(new[] { volumeGuidPath }, dismount: false); }
        catch (RawOperationException) { return null; }
    }

    public void Dispose()
    {
        foreach (var h in _handles)
        {
            RawNative.Control(h, RawNative.FSCTL_UNLOCK_VOLUME);
            h.Dispose();
        }
        _handles.Clear();
    }
}

/// <summary>Allocated clusters of an NTFS volume (FSCTL_GET_VOLUME_BITMAP), as disk byte ranges.</summary>
[SupportedOSPlatform("windows")]
public static class VolumeBitmap
{
    public static IReadOnlyList<ByteRange>? UsedRanges(string volumeDevicePath, long partitionOffset, uint clusterSize)
    {
        if (clusterSize == 0) return null;
        try
        {
            using var h = RawNative.Open(volumeDevicePath.TrimEnd('\\'), write: false);
            var ranges = new List<ByteRange>();
            long lcn = 0;
            var input = new byte[8];
            var output = new byte[1 << 20];
            while (true)
            {
                BinaryPrimitives.WriteInt64LittleEndian(input, lcn);
                bool ok = NativeMethods.DeviceIoControl(h, RawNative.FSCTL_GET_VOLUME_BITMAP, input, 8, output, output.Length, out var returned, IntPtr.Zero);
                int err = ok ? 0 : Marshal.GetLastWin32Error();
                if (!ok && err != NativeMethods.ERROR_MORE_DATA) return null;
                long start = BinaryPrimitives.ReadInt64LittleEndian(output);
                long bits = BinaryPrimitives.ReadInt64LittleEndian(output.AsSpan(8));
                long available = Math.Min(bits, (long)(returned - 16) * 8);
                long runStart = -1;
                for (long i = 0; i < available; i++)
                {
                    bool used = (output[16 + (i >> 3)] & (1 << (int)(i & 7))) != 0;
                    if (used && runStart < 0) runStart = i;
                    if (!used && runStart >= 0) { Add(start + runStart, i - runStart); runStart = -1; }
                }
                if (runStart >= 0) Add(start + runStart, available - runStart);
                if (ok) break;
                lcn = start + available;
            }
            return ranges;

            void Add(long firstLcn, long count) =>
                ranges.Add(new ByteRange(partitionOffset + firstLcn * clusterSize, count * clusterSize));
        }
        catch (RawOperationException) { return null; }
    }
}

/// <summary>Volume Shadow Copy via WMI (Win32_ShadowCopy) for consistent reads of volumes in use, such as C:.</summary>
[SupportedOSPlatform("windows")]
public sealed class ShadowCopy : IDisposable
{
    private readonly string _id;
    public string DevicePath { get; }

    private ShadowCopy(string id, string device)
    {
        _id = id;
        DevicePath = device;
    }

    public static ShadowCopy Create(string volumeGuidPath)
    {
        var t = Type.GetTypeFromProgID("WbemScripting.SWbemLocator", true)!;
        dynamic locator = Activator.CreateInstance(t)!;
        dynamic svc = locator.ConnectServer(".", @"root\cimv2");
        dynamic cls = svc.Get("Win32_ShadowCopy");
        dynamic inParams = cls.Methods_.Item("Create").InParameters.SpawnInstance_();
        inParams.Properties_.Item("Volume").Value = volumeGuidPath.EndsWith('\\') ? volumeGuidPath : volumeGuidPath + "\\";
        inParams.Properties_.Item("Context").Value = "ClientAccessible";
        dynamic outParams = cls.ExecMethod_("Create", inParams);
        uint rv = Convert.ToUInt32(outParams.Properties_.Item("ReturnValue").Value);
        if (rv != 0) throw new RawOperationException("Clone.Error.Snapshot", $"Win32_ShadowCopy.Create returned {rv}");
        string id = outParams.Properties_.Item("ShadowID").Value;
        dynamic set = svc.ExecQuery($"SELECT * FROM Win32_ShadowCopy WHERE ID = '{id}'");
        dynamic sc = set.ItemIndex(0);
        string device = sc.Properties_.Item("DeviceObject").Value;
        return new ShadowCopy(id, device);
    }

    public void Dispose()
    {
        try
        {
            var t = Type.GetTypeFromProgID("WbemScripting.SWbemLocator", true)!;
            dynamic locator = Activator.CreateInstance(t)!;
            dynamic svc = locator.ConnectServer(".", @"root\cimv2");
            dynamic set = svc.ExecQuery($"SELECT * FROM Win32_ShadowCopy WHERE ID = '{_id}'");
            if ((int)set.Count > 0) set.ItemIndex(0).Delete_();
        }
        catch { /* best effort: orphaned client-accessible snapshots are removed by Windows over time */ }
    }
}

/// <summary>Executes raw operations (conversion, move, clone) with locking, snapshots and rescans.</summary>
[SupportedOSPlatform("windows")]
public sealed class RawDiskExecutor
{
    private readonly IStorageInventoryProvider _inventory;
    private readonly IAppLog _log;
    private readonly string _journalDir;

    public RawDiskExecutor(IStorageInventoryProvider inventory, IAppLog? log = null, string? journalDir = null)
    {
        _inventory = inventory;
        _log = log ?? NullLog.Instance;
        _journalDir = journalDir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lasteast", "Partition", "journal");
    }

    public static string JournalPathFor(string dir, int disk) => Path.Combine(dir, $"move-disk{disk}.json");

    private static IEnumerable<string> VolumesOn(DiskInfo d) =>
        d.Partitions.Select(p => p.Volume?.VolumeGuidPath).OfType<string>().Where(v => v.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase));

    public void ConvertStyle(DiskInfo disk, PartitionStyle target)
    {
        using var locks = VolumeLocks.Acquire(VolumesOn(disk), dismount: true);
        using var dev = new PhysicalDiskDevice(disk, writable: true);
        if (target == PartitionStyle.Gpt) StyleConverter.ConvertMbrToGpt(dev);
        else
        {
            var fs = disk.Partitions.Where(p => p.Volume?.FileSystem is not null)
                .ToDictionary(p => p.OffsetBytes / disk.BytesPerLogicalSector, p => p.Volume!.FileSystem);
            StyleConverter.ConvertGptToMbr(dev, fs);
        }
        dev.Rescan();
        _log.Info($"Disk {disk.Number} converted to {target}");
    }

    public void Move(DiskInfo disk, PartitionInfo partition, ulong newOffset, IProgress<RawProgress>? progress)
    {
        Directory.CreateDirectory(_journalDir);
        var journalPath = JournalPathFor(_journalDir, disk.Number);
        var identity = Lasteast.Core.Operations.DiskFingerprint.Identity(disk);
        var journal = MoveJournal.Load(journalPath) is { } existing && existing.DiskIdentity == identity
                      && existing.SourceOffset == (long)partition.OffsetBytes && existing.DestOffset == (long)newOffset
            ? existing
            : PartitionMover.NewJournal(identity, (long)partition.OffsetBytes, (long)newOffset, (long)partition.SizeBytes, (int)disk.BytesPerLogicalSector);

        bool winre = partition.Role == PartitionRole.Recovery && disk.IsWindowsDisk;
        if (winre) RunTool("reagentc.exe", "/disable");
        try
        {
            using var locks = partition.Volume?.VolumeGuidPath is { } v && v.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase)
                ? VolumeLocks.Acquire(new[] { v }, dismount: true) : null;
            using var dev = new PhysicalDiskDevice(disk, writable: true);
            PartitionMover.Run(dev, journal, journalPath, progress, CancellationToken.None);
            dev.Rescan();
        }
        finally
        {
            if (winre) RunTool("reagentc.exe", "/enable");
        }
        File.Delete(journalPath);
        _log.Info($"Moved partition on disk {disk.Number} from {partition.OffsetBytes} to {newOffset}");
    }

    public CloneResult Clone(DiskInfo source, DiskInfo dest, CloneMode mode, bool newIds, bool verify,
        IReadOnlyCollection<ulong>? only, IProgress<RawProgress>? progress, CancellationToken ct)
    {
        var snapshots = new List<(PartitionInfo Part, ShadowCopy Snap)>();
        var sourceLocks = new List<VolumeLocks>();
        try
        {
            // Source consistency: lock volumes that are not in use; snapshot NTFS volumes that are (e.g. C:).
            foreach (var p in source.Partitions.Where(p => p.Volume?.VolumeGuidPath?.StartsWith(@"\\?\Volume{") == true))
            {
                if (only is not null && !only.Contains(p.OffsetBytes)) continue;
                var l = VolumeLocks.TryAcquire(p.Volume!.VolumeGuidPath);
                if (l is not null) { sourceLocks.Add(l); continue; }
                if (string.Equals(p.Volume.FileSystem, "NTFS", StringComparison.OrdinalIgnoreCase))
                {
                    snapshots.Add((p, ShadowCopy.Create(p.Volume.VolumeGuidPath)));
                    _log.Info($"Snapshot of {p.Volume.VolumeGuidPath} for consistent cloning");
                }
                else _log.Warn($"Volume {p.Volume.VolumeGuidPath} is in use and cannot be snapshotted; copying it live");
            }

            UsedRangeProvider used = p =>
            {
                if (!string.Equals(p.Volume?.FileSystem, "NTFS", StringComparison.OrdinalIgnoreCase) || p.Volume?.ClusterSizeBytes is not { } cs) return null;
                var snap = snapshots.FirstOrDefault(s => s.Part.OffsetBytes == p.OffsetBytes).Snap;
                return VolumeBitmap.UsedRanges(snap?.DevicePath ?? p.Volume.VolumeGuidPath, (long)p.OffsetBytes, cs);
            };
            var plan = ClonePlanner.Plan(source, (long)dest.SizeBytes, (int)dest.BytesPerLogicalSector, mode, newIds, used, only);

            using var destLocks = VolumeLocks.Acquire(VolumesOn(dest), dismount: true);
            using var src = new PhysicalDiskDevice(source, writable: false);
            using var dst = new PhysicalDiskDevice(dest, writable: true);

            // Reads inside a snapshotted partition come from the snapshot device.
            var snapHandles = snapshots.Select(s => (s.Part, Handle: RawNative.Open(s.Snap.DevicePath, write: false))).ToList();
            try
            {
                RangeReader reader = (offset, buffer) =>
                {
                    foreach (var (part, h) in snapHandles)
                    {
                        long ps = (long)part.OffsetBytes, pe = (long)part.EndBytes;
                        if (offset >= ps && offset + buffer.Length <= pe)
                        {
                            try
                            {
                                int total = 0;
                                while (total < buffer.Length)
                                {
                                    int n = RandomAccess.Read(h, buffer[total..], offset - ps + total);
                                    if (n <= 0) break;
                                    total += n;
                                }
                                if (total == buffer.Length) return;
                            }
                            catch (IOException) { }
                            break; // beyond the snapshot's end (backup boot sector): fall back to the live disk
                        }
                    }
                    src.Read(offset, buffer);
                };
                var result = Cloner.Run(src, dst, plan, verify, progress, ct, reader);
                dst.Rescan();
                return result;
            }
            finally
            {
                foreach (var (_, h) in snapHandles) h.Dispose();
            }
        }
        finally
        {
            foreach (var l in sourceLocks) l.Dispose();
            foreach (var (_, s) in snapshots) s.Dispose();
        }
    }

    private void RunTool(string exe, string args)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
            using var p = System.Diagnostics.Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(60_000);
            _log.Info($"{exe} {args}: exit {p.ExitCode} {output.Trim()}");
        }
        catch (Exception ex) { _log.Warn($"{exe} {args} failed: {ex.Message}"); }
    }
}

/// <summary>
/// The executor used by the app: table/volume operations go through the Windows Storage Management API,
/// raw operations (conversion, move, clone) through <see cref="RawDiskExecutor"/>. Only
/// <see cref="Lasteast.Core.Operations.GuardedExecutionEngine"/> may call it.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsOperationExecutor : Lasteast.Core.Operations.IOperationExecutor
{
    private readonly StorageManagementExecutor _wmi = new();
    private readonly RawDiskExecutor _raw;
    private readonly IStorageInventoryProvider _inventory;

    public WindowsOperationExecutor(IStorageInventoryProvider inventory, IAppLog? log = null, string? journalDir = null)
    {
        _inventory = inventory;
        _raw = new RawDiskExecutor(inventory, log, journalDir);
    }

    /// <summary>Progress of long raw steps (copy/verify), for the progress bar.</summary>
    public IProgress<RawProgress>? RawProgress { get; set; }

    public async Task ExecuteAsync(Lasteast.Core.Operations.DiskOperation operation, DiskInfo liveDisk, CancellationToken cancellationToken)
    {
        switch (operation)
        {
            case Lasteast.Core.Operations.ConvertStyleOp c:
                await Raw(() => _raw.ConvertStyle(liveDisk, c.Target));
                break;
            case Lasteast.Core.Operations.MovePartitionOp m:
            {
                var part = liveDisk.Partitions.FirstOrDefault(p => p.OffsetBytes == m.OffsetBytes)
                           ?? throw new Lasteast.Core.Operations.OperationFailedException("Op.Error.NotFound");
                await Raw(() => _raw.Move(liveDisk, part, m.NewOffsetBytes, RawProgress));
                break;
            }
            case Lasteast.Core.Operations.CloneDiskOp k:
            {
                var inv = await _inventory.GetInventoryAsync(CancellationToken.None);
                var src = inv.Disks.FirstOrDefault(d => d.Number == k.SourceDiskNumber)
                          ?? throw new Lasteast.Core.Operations.OperationFailedException("Exec.Error.DiskMissing", null, k.SourceDiskNumber);
                await Raw(() => _raw.Clone(src, liveDisk, k.Mode, k.NewIdentifiers, k.Verify, k.OnlyPartitions, RawProgress, CancellationToken.None));
                break;
            }
            default:
                await _wmi.ExecuteAsync(operation, liveDisk, cancellationToken);
                break;
        }
        // Give Windows' partition manager a moment to publish the new layout before the engine re-reads it.
        if (operation is Lasteast.Core.Operations.ConvertStyleOp or Lasteast.Core.Operations.MovePartitionOp or Lasteast.Core.Operations.CloneDiskOp)
            await Task.Delay(2000, CancellationToken.None);
    }

    private static Task Raw(Action a) => Task.Run(() =>
    {
        try { a(); }
        catch (RawOperationException ex) { throw new Lasteast.Core.Operations.OperationFailedException(ex.Key, ex.Message); }
        catch (IOException ex) { throw new Lasteast.Core.Operations.OperationFailedException("Raw.Error.IO", ex.Message); }
        catch (UnauthorizedAccessException ex) { throw new Lasteast.Core.Operations.OperationFailedException("Raw.Error.Open", ex.Message); }
    });
}
