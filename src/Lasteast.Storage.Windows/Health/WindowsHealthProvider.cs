using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Lasteast.Core.Abstractions;
using Lasteast.Core.Health;
using Lasteast.Core.Logging;
using Lasteast.Core.Models;
using Lasteast.Storage.Windows.Interop;
using Microsoft.Win32.SafeHandles;

namespace Lasteast.Storage.Windows.Health;

/// <summary>
/// Reads what the drive itself reports: the NVMe SMART/Health log page (IOCTL_STORAGE_QUERY_PROPERTY, protocol-specific),
/// ATA SMART attributes and thresholds (SMART_RCV_DRIVE_DATA), and Windows' reliability counters (MSFT_StorageReliabilityCounter).
/// Each source is optional: drives behind USB bridges or RAID controllers often expose none, and that is reported as unknown.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsHealthProvider : IDiskHealthProvider
{
    private const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;
    private const uint SMART_RCV_DRIVE_DATA = 0x0007C088;
    private readonly IAppLog _log;

    public WindowsHealthProvider(IAppLog? log = null) => _log = log ?? NullLog.Instance;

    public Task<DiskHealthReport> ReadAsync(DiskInfo disk, CancellationToken ct = default) => Task.Run(() =>
    {
        var sources = new List<string>();
        NvmeHealthLog? nvme = null;
        IReadOnlyList<SmartAttribute>? ata = null;
        using (var h = Open(disk.DevicePath))
        {
            if (h is not null)
            {
                if (disk.Bus == BusKind.Nvme || disk.Media == MediaKind.Ssd)
                    try { if (ReadNvme(h) is { } page) { nvme = NvmeHealthLog.Parse(page); sources.Add("NVMe health log"); } }
                    catch (Exception ex) { _log.Info($"NVMe log on disk {disk.Number}: {ex.Message}"); }
                if (nvme is null && disk.Bus != BusKind.Nvme)
                    try { ata = ReadAta(h); if (ata?.Count > 0) sources.Add("ATA SMART"); }
                    catch (Exception ex) { _log.Info($"ATA SMART on disk {disk.Number}: {ex.Message}"); }
            }
        }
        WindowsReliability? win = null;
        try { win = ReadWindows(disk.Number); if (win is not null) sources.Add("Windows reliability counters"); }
        catch (Exception ex) { _log.Info($"Reliability counters of disk {disk.Number}: {ex.Message}"); }
        return HealthAssessor.Assess(disk, nvme, ata, win, sources);
    }, ct);

    private static SafeFileHandle? Open(string path)
    {
        foreach (var access in new[] { 0xC0000000u, 0x80000000u, 0u })
        {
            var h = NativeMethods.CreateFile(path, access, NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE, IntPtr.Zero,
                NativeMethods.OPEN_EXISTING, NativeMethods.FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);
            if (!h.IsInvalid) return h;
            h.Dispose();
        }
        return null;
    }

    /// <summary>STORAGE_PROPERTY_QUERY(StorageDeviceProtocolSpecificProperty) + STORAGE_PROTOCOL_SPECIFIC_DATA(NVMe, log page 2).</summary>
    private static byte[]? ReadNvme(SafeFileHandle h)
    {
        const int Header = 8, Psd = 40, Page = 512;
        var buf = new byte[Header + Psd + Page];
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0), 50); // StorageDeviceProtocolSpecificProperty
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4), 0);  // PropertyStandardQuery
        var p = buf.AsSpan(Header);
        BinaryPrimitives.WriteUInt32LittleEndian(p[0..], 3);     // ProtocolTypeNvme
        BinaryPrimitives.WriteUInt32LittleEndian(p[4..], 2);     // NVMeDataTypeLogPage
        BinaryPrimitives.WriteUInt32LittleEndian(p[8..], 2);     // SMART / Health Information
        BinaryPrimitives.WriteUInt32LittleEndian(p[12..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(p[16..], Psd);  // ProtocolDataOffset
        BinaryPrimitives.WriteUInt32LittleEndian(p[20..], Page); // ProtocolDataLength
        if (!NativeMethods.DeviceIoControl(h, IOCTL_STORAGE_QUERY_PROPERTY, buf, buf.Length, buf, buf.Length, out var returned, IntPtr.Zero)) return null;
        // output: STORAGE_PROTOCOL_DATA_DESCRIPTOR { Version, Size, STORAGE_PROTOCOL_SPECIFIC_DATA }
        var d = buf.AsSpan(8);
        int off = (int)BinaryPrimitives.ReadUInt32LittleEndian(d[16..]);
        int len = (int)BinaryPrimitives.ReadUInt32LittleEndian(d[20..]);
        if (len < Page || 8 + off + Page > returned) return null;
        return buf.AsSpan(8 + off, Page).ToArray();
    }

    private static byte[]? SmartCommand(SafeFileHandle h, byte feature, byte driveNumber)
    {
        var input = new byte[33];
        BinaryPrimitives.WriteUInt32LittleEndian(input, 512);
        input[4] = feature;               // bFeaturesReg: D0 = READ DATA, D1 = READ THRESHOLDS
        input[5] = 1;                     // bSectorCountReg
        input[6] = 1;                     // bSectorNumberReg
        input[7] = 0x4F;                  // bCylLowReg
        input[8] = 0xC2;                  // bCylHighReg
        input[9] = (byte)(0xA0 | ((driveNumber & 1) << 4));
        input[10] = 0xB0;                 // SMART
        input[12] = driveNumber;
        var output = new byte[16 + 512];
        if (!NativeMethods.DeviceIoControl(h, SMART_RCV_DRIVE_DATA, input, input.Length, output, output.Length, out var returned, IntPtr.Zero) || returned < 16 + 362)
            return null;
        return output.AsSpan(16, 512).ToArray();
    }

    private static IReadOnlyList<SmartAttribute>? ReadAta(SafeFileHandle h)
    {
        var data = SmartCommand(h, 0xD0, 0);
        if (data is null) return null;
        var thr = SmartCommand(h, 0xD1, 0) ?? new byte[512];
        return AtaSmart.Parse(data, thr);
    }

    private static WindowsReliability? ReadWindows(int disk)
    {
        var t = Type.GetTypeFromProgID("WbemScripting.SWbemLocator", true)!;
        dynamic locator = Activator.CreateInstance(t)!;
        dynamic svc = locator.ConnectServer(".", @"root\Microsoft\Windows\Storage");
        try
        {
            string? health = null;
            foreach (dynamic pd in svc.ExecQuery($"SELECT HealthStatus FROM MSFT_PhysicalDisk WHERE DeviceId = '{disk}'"))
            {
                int hs = Convert.ToInt32(pd.Properties_.Item("HealthStatus").Value);
                health = hs switch { 0 => "Healthy", 1 => "Warning", 2 => "Unhealthy", _ => "Unknown" };
            }
            foreach (dynamic rc in svc.ExecQuery($"SELECT * FROM MSFT_StorageReliabilityCounter WHERE DeviceId = '{disk}'"))
            {
                long? Num(string n) { var v = rc.Properties_.Item(n).Value; return v is null ? null : Convert.ToInt64(v); }
                int? I(string n) => Num(n) is { } x and > 0 ? (int)x : null;
                return new WindowsReliability
                {
                    HealthStatus = health, Temperature = I("Temperature"), TemperatureMax = I("TemperatureMax"), WearPercent = (int?)Num("Wear"),
                    PowerOnHours = Num("PowerOnHours"), ReadErrorsUncorrected = Num("ReadErrorsUncorrected"), WriteErrorsUncorrected = Num("WriteErrorsUncorrected"),
                    ReadErrorsTotal = Num("ReadErrorsTotal"), StartStopCycles = Num("StartStopCycleCount"),
                };
            }
            return health is null ? null : new WindowsReliability { HealthStatus = health };
        }
        finally { Marshal.FinalReleaseComObject(svc); }
    }
}
