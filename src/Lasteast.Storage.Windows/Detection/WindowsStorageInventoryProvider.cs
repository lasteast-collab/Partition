using System.ComponentModel;
using System.Runtime.Versioning;
using System.Text;
using Lasteast.Core.Abstractions;
using Lasteast.Core.Logging;
using Lasteast.Core.Models;
using Lasteast.Storage.Windows.Interop;
using Microsoft.Win32;
using static Lasteast.Storage.Windows.Interop.NativeMethods;

namespace Lasteast.Storage.Windows.Detection;

/// <summary>
/// Enumerates physical disks, partitions and volumes using native storage IOCTLs.
/// Strictly read-only: every handle is opened with zero access rights (query only),
/// so this class is incapable of modifying a disk.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsStorageInventoryProvider : IStorageInventoryProvider
{
    /// <summary>PhysicalDrive numbers are dense in practice but may have gaps after hot-unplug; probe a generous range.</summary>
    private const int MaxDiskNumber = 64;

    private readonly IAppLog _log;
    private readonly IPrivilegeService? _privileges;

    public WindowsStorageInventoryProvider(IAppLog? log = null, IPrivilegeService? privileges = null)
    {
        _log = log ?? NullLog.Instance;
        _privileges = privileges;
    }

    public Task<StorageInventory> GetInventoryAsync(CancellationToken cancellationToken = default)
        => Task.Run(() => Capture(cancellationToken), cancellationToken);

    private StorageInventory Capture(CancellationToken ct)
    {
        // Suppress "There is no disk in the drive" system dialogs for empty card readers.
        SetThreadErrorMode(SEM_FAILCRITICALERRORS, out var oldMode);
        try
        {
            var notes = new List<string>();
            var volumes = EnumerateVolumes(notes, ct);
            var disks = new List<DiskInfo>();

            for (int n = 0; n < MaxDiskNumber; n++)
            {
                ct.ThrowIfCancellationRequested();
                var probe = ProbeDisk(n);
                if (probe is null) continue;
                disks.Add(DiskAssembler.Assemble(probe, volumes));
            }

            var mapped = new HashSet<string>(disks.SelectMany(d => d.Partitions)
                .Select(p => p.Volume?.VolumeGuidPath).OfType<string>(), StringComparer.OrdinalIgnoreCase);

            _log.Info($"Inventory captured: {disks.Count} disk(s), {volumes.Count} volume(s).");
            return new StorageInventory
            {
                CapturedAt = DateTimeOffset.Now,
                Disks = disks,
                Firmware = GetFirmware(),
                IsElevated = _privileges?.IsElevated ?? false,
                UnmappedVolumes = volumes.Select(v => v.Info).Where(v => !mapped.Contains(v.VolumeGuidPath)).ToList(),
                Notes = notes,
            };
        }
        finally
        {
            SetThreadErrorMode(oldMode, out _);
        }
    }

    // ------------------------------------------------------------------ disks

    private DiskProbe? ProbeDisk(int number)
    {
        var path = $@"\\.\PhysicalDrive{number}";
        using var h = OpenForQuery(path);
        if (h.IsInvalid)
        {
            var err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            if (err is not (ERROR_FILE_NOT_FOUND or ERROR_PATH_NOT_FOUND))
                _log.Warn($"Cannot open {path}: Win32 error {err}");
            return null;
        }

        var notes = new List<string>();

        ulong size = 0;
        uint logical = 512;
        var geo = Ioctl(h, IOCTL_DISK_GET_DRIVE_GEOMETRY_EX, null, 256, out var gErr);
        if (geo is not null)
        {
            var g = StructParsers.ParseGeometryEx(geo);
            size = g.DiskSize;
            logical = g.BytesPerSector == 0 ? 512 : g.BytesPerSector;
        }
        else
        {
            notes.Add($"Geometry unavailable ({new Win32Exception(gErr).Message}). The device may have no media.");
        }

        StructParsers.DeviceDescriptor? descriptor = null;
        var dd = Ioctl(h, IOCTL_STORAGE_QUERY_PROPERTY, StructParsers.PropertyQuery(StructParsers.StorageDeviceProperty), 1024, out _);
        if (dd is not null) descriptor = StructParsers.ParseDeviceDescriptor(dd);

        bool? seek = null, trim = null;
        var sp = Ioctl(h, IOCTL_STORAGE_QUERY_PROPERTY, StructParsers.PropertyQuery(StructParsers.StorageDeviceSeekPenaltyProperty), 64, out _);
        if (sp is not null) seek = StructParsers.ParseBooleanDescriptor(sp);
        var tp = Ioctl(h, IOCTL_STORAGE_QUERY_PROPERTY, StructParsers.PropertyQuery(StructParsers.StorageDeviceTrimProperty), 64, out _);
        if (tp is not null) trim = StructParsers.ParseBooleanDescriptor(tp);

        uint physical = logical;
        var al = Ioctl(h, IOCTL_STORAGE_QUERY_PROPERTY, StructParsers.PropertyQuery(StructParsers.StorageAccessAlignmentProperty), 64, out _);
        if (al is not null && StructParsers.ParseAlignment(al) is { } a && a.Physical > 0)
        {
            physical = a.Physical;
            if (a.Logical > 0) logical = a.Logical;
        }

        StructParsers.RawLayout? layout = null;
        var lb = Ioctl(h, IOCTL_DISK_GET_DRIVE_LAYOUT_EX, null,
            StructParsers.LayoutHeaderSize + 16 * StructParsers.PartitionEntrySize, out var lErr);
        if (lb is not null)
        {
            try { layout = StructParsers.ParseDriveLayout(lb); }
            catch (FormatException ex) { notes.Add("Partition table could not be decoded: " + ex.Message); }
        }
        else if (size > 0)
        {
            notes.Add($"Partition layout unavailable ({new Win32Exception(lErr).Message}).");
        }

        return new DiskProbe(number, path, size, logical, physical, descriptor, seek, trim, layout, notes);
    }

    // ---------------------------------------------------------------- volumes

    private List<VolumeRecord> EnumerateVolumes(List<string> notes, CancellationToken ct)
    {
        var result = new List<VolumeRecord>();
        string? windowsVolume = GetWindowsVolumeGuid();
        string? systemPartitionDevice = GetSetupSystemPartitionDevice();

        var name = new StringBuilder(1024);
        var find = FindFirstVolume(name, name.Capacity);
        if (find == IntPtr.Zero || find == new IntPtr(-1))
        {
            notes.Add("Volume enumeration failed: " + LastError("FindFirstVolume").Message);
            return result;
        }

        try
        {
            do
            {
                ct.ThrowIfCancellationRequested();
                var guidPath = name.ToString(); // \\?\Volume{GUID}\
                try
                {
                    result.Add(ReadVolume(guidPath, windowsVolume, systemPartitionDevice));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.Warn($"Volume {guidPath}: {ex.Message}");
                    result.Add(new VolumeRecord(new VolumeInfo { VolumeGuidPath = guidPath, AccessError = ex.Message },
                        Array.Empty<(int, ulong, ulong)>()));
                }
                name.Clear();
            }
            while (FindNextVolume(find, name, name.Capacity));
        }
        finally
        {
            FindVolumeClose(find);
        }
        return result;
    }

    private VolumeRecord ReadVolume(string guidPath, string? windowsVolume, string? systemPartitionDevice)
    {
        var mounts = GetMountPoints(guidPath);
        char? letter = mounts.Select(m => m.Length == 3 && m[1] == ':' ? char.ToUpperInvariant(m[0]) : (char?)null)
                             .FirstOrDefault(c => c is not null);

        string? label = null, fs = null, error = null;
        int? errorCode = null;
        bool readOnly = false;
        var lb = new StringBuilder(261);
        var fb = new StringBuilder(261);
        if (GetVolumeInformation(guidPath, lb, lb.Capacity, out _, out _, out var flags, fb, fb.Capacity))
        {
            label = lb.Length > 0 ? lb.ToString() : null;
            fs = fb.Length > 0 ? fb.ToString() : null;
            readOnly = (flags & FILE_READ_ONLY_VOLUME) != 0;
        }
        else
        {
            // RAW, locked BitLocker volume, no media, or unknown file system.
            var ex = LastError("GetVolumeInformation");
            error = ex.Message;
            errorCode = ex.NativeErrorCode;
        }

        ulong? capacity = null, free = null;
        uint? cluster = null;
        if (error is null && GetDiskFreeSpaceEx(guidPath, out _, out var total, out var totalFree))
        {
            capacity = total;
            free = totalFree;
        }
        if (error is null && GetDiskFreeSpace(guidPath, out var spc, out var bps, out _, out _))
            cluster = spc * bps;

        // Extents: open \\?\Volume{GUID} (no trailing slash) for query only.
        var extents = (IReadOnlyList<(int, ulong, ulong)>)Array.Empty<(int, ulong, ulong)>();
        using (var vh = OpenForQuery(guidPath.TrimEnd('\\')))
        {
            if (!vh.IsInvalid)
            {
                var eb = Ioctl(vh, IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS, null, 8 + 24 * 4, out _);
                if (eb is not null) extents = StructParsers.ParseVolumeExtents(eb);
            }
        }

        bool isSystemPartition = false;
        if (systemPartitionDevice is not null)
        {
            var dos = QueryDosDeviceFor(guidPath);
            isSystemPartition = dos is not null && string.Equals(dos, systemPartitionDevice, StringComparison.OrdinalIgnoreCase);
        }

        var info = new VolumeInfo
        {
            VolumeGuidPath = guidPath,
            MountPoints = mounts,
            DriveLetter = letter,
            Label = label,
            FileSystem = fs,
            CapacityBytes = capacity,
            FreeBytes = free,
            ClusterSizeBytes = cluster,
            IsReadOnly = readOnly,
            IsWindowsVolume = windowsVolume is not null && string.Equals(windowsVolume, guidPath, StringComparison.OrdinalIgnoreCase),
            IsSystemPartitionVolume = isSystemPartition,
            AccessError = error,
            AccessErrorCode = errorCode,
        };
        return new VolumeRecord(info, extents);
    }

    private static IReadOnlyList<string> GetMountPoints(string guidPath)
    {
        GetVolumePathNamesForVolumeName(guidPath, null, 0, out var needed);
        if (needed <= 1) return Array.Empty<string>();
        var buffer = new char[needed];
        if (!GetVolumePathNamesForVolumeName(guidPath, buffer, buffer.Length, out _)) return Array.Empty<string>();
        return new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    private static string? GetWindowsVolumeGuid()
    {
        var root = new StringBuilder(261);
        if (!GetVolumePathName(Environment.SystemDirectory, root, root.Capacity)) return null;
        var guid = new StringBuilder(64);
        return GetVolumeNameForVolumeMountPoint(root.ToString(), guid, guid.Capacity) ? guid.ToString() : null;
    }

    /// <summary>
    /// Windows Setup records the firmware system partition (ESP on UEFI) as an NT device path,
    /// e.g. "\Device\HarddiskVolume1", in HKLM\SYSTEM\Setup\SystemPartition. Readable without elevation.
    /// </summary>
    private string? GetSetupSystemPartitionDevice()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\Setup");
            return key?.GetValue("SystemPartition") as string;
        }
        catch (Exception ex)
        {
            _log.Warn("Could not read SystemPartition: " + ex.Message);
            return null;
        }
    }

    private static string? QueryDosDeviceFor(string guidPath)
    {
        // "\\?\Volume{GUID}\" -> "Volume{GUID}"
        var name = guidPath.TrimEnd('\\');
        if (name.StartsWith(@"\\?\", StringComparison.Ordinal)) name = name[4..];
        var buffer = new char[1024];
        var len = QueryDosDevice(name, buffer, buffer.Length);
        if (len <= 0) return null;
        return new string(buffer, 0, len).Split('\0', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
    }

    private static FirmwareKind GetFirmware()
    {
        try
        {
            if (GetFirmwareType(out var t))
                return t switch { 1 => FirmwareKind.Bios, 2 => FirmwareKind.Uefi, _ => FirmwareKind.Unknown };
        }
        catch (EntryPointNotFoundException) { }
        return FirmwareKind.Unknown;
    }
}
