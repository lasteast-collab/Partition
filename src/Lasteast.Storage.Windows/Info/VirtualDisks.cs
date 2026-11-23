using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Lasteast.Core.Operations;
using Microsoft.Win32.SafeHandles;

namespace Lasteast.Storage.Windows.Info;

/// <summary>VHD/VHDX files through Windows' Virtual Disk API (virtdisk.dll): create, attach, detach, and find the file behind an attached disk.</summary>
[SupportedOSPlatform("windows")]
public static class VirtualDiskService
{
    private static readonly Guid VendorMicrosoft = new("EC984AEC-A0F9-47e9-901F-71415A66345B");

    [StructLayout(LayoutKind.Sequential)]
    private struct VIRTUAL_STORAGE_TYPE { public uint DeviceId; public Guid VendorId; }

    [StructLayout(LayoutKind.Explicit, Size = 128)]
    private struct CREATE_PARAMS_V2
    {
        [FieldOffset(0)] public uint Version;
        [FieldOffset(8)] public Guid UniqueId;
        [FieldOffset(24)] public ulong MaximumSize;
        [FieldOffset(32)] public uint BlockSizeInBytes;
        [FieldOffset(36)] public uint SectorSizeInBytes;
        [FieldOffset(40)] public uint PhysicalSectorSizeInBytes;
        [FieldOffset(48)] public IntPtr ParentPath;
        [FieldOffset(56)] public IntPtr SourcePath;
        [FieldOffset(64)] public uint OpenFlags;
        [FieldOffset(88)] public uint SourceDeviceId;
        [FieldOffset(92)] public Guid SourceVendorId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RESIZE_PARAMS { public uint Version; public uint Pad; public ulong NewSize; }

    [StructLayout(LayoutKind.Sequential)]
    private struct COMPACT_PARAMS { public uint Version; public uint Reserved; }

    [DllImport("virtdisk.dll")]
    private static extern uint ResizeVirtualDisk(SafeFileHandle h, uint flags, ref RESIZE_PARAMS parameters, IntPtr overlapped);
    [DllImport("virtdisk.dll")]
    private static extern uint CompactVirtualDisk(SafeFileHandle h, uint flags, ref COMPACT_PARAMS parameters, IntPtr overlapped);

    /// <summary>Grows the virtual disk file's capacity (new space appears unallocated inside it). The file must not be attached.</summary>
    public static void Expand(string path, ulong newSizeBytes)
    {
        using var h = Open(path);
        var p = new RESIZE_PARAMS { Version = 1, NewSize = newSizeBytes / 512 * 512 };
        Check(ResizeVirtualDisk(h, 0, ref p, IntPtr.Zero), "ResizeVirtualDisk");
    }

    /// <summary>
    /// Shrinks a VHDX down to <paramref name="newSizeBytes"/>, or to the smallest size that keeps every partition when
    /// null. Windows refuses any shrink that would cut into a partition (the unsafe flag is never used).
    /// </summary>
    public static void Shrink(string path, ulong? newSizeBytes)
    {
        if (!path.EndsWith(".vhdx", StringComparison.OrdinalIgnoreCase)) throw new OperationFailedException("Vd.Error.ShrinkVhd", path);
        using var h = Open(path);
        var p = new RESIZE_PARAMS { Version = 1, NewSize = (newSizeBytes ?? 0) / 512 * 512 };
        Check(ResizeVirtualDisk(h, newSizeBytes is null ? 2u : 0u, ref p, IntPtr.Zero), "ResizeVirtualDisk");
    }

    /// <summary>Reclaims unused space inside a dynamically expanding file. Contents are unchanged.</summary>
    public static void Compact(string path)
    {
        using var h = Open(path);
        var p = new COMPACT_PARAMS { Version = 1 };
        Check(CompactVirtualDisk(h, 0, ref p, IntPtr.Zero), "CompactVirtualDisk");
    }

    /// <summary>Creates <paramref name="destination"/> (VHD or VHDX by extension) as a block-level copy of <paramref name="source"/>: clone or format conversion. The source is only read.</summary>
    public static void Convert(string source, string destination, bool dynamic)
    {
        if (File.Exists(destination)) throw new OperationFailedException("Vd.Error.Exists", destination);
        var type = TypeFor(destination);
        var src = TypeFor(source);
        var sp = Marshal.StringToHGlobalUni(source);
        try
        {
            var p = new CREATE_PARAMS_V2 { Version = 2, UniqueId = Guid.NewGuid(), SourcePath = sp, SourceDeviceId = src.DeviceId, SourceVendorId = src.VendorId };
            Check(CreateVirtualDisk(ref type, destination, 0, IntPtr.Zero, dynamic ? 0u : 1u, 0, ref p, IntPtr.Zero, out var h), "CreateVirtualDisk");
            h.Dispose();
        }
        finally { Marshal.FreeHGlobal(sp); }
    }

    [StructLayout(LayoutKind.Explicit, Size = 28)]
    private struct OPEN_PARAMS_V2
    {
        [FieldOffset(0)] public uint Version;
        [FieldOffset(4)] public int GetInfoOnly;
        [FieldOffset(8)] public int ReadOnly;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ATTACH_PARAMS { public uint Version; public uint Reserved; }

    [DllImport("virtdisk.dll", CharSet = CharSet.Unicode)]
    private static extern uint CreateVirtualDisk(ref VIRTUAL_STORAGE_TYPE type, string path, uint access, IntPtr security, uint flags, uint providerFlags,
        ref CREATE_PARAMS_V2 parameters, IntPtr overlapped, out SafeFileHandle handle);
    [DllImport("virtdisk.dll", CharSet = CharSet.Unicode)]
    private static extern uint OpenVirtualDisk(ref VIRTUAL_STORAGE_TYPE type, string path, uint access, uint flags, ref OPEN_PARAMS_V2 parameters, out SafeFileHandle handle);
    [DllImport("virtdisk.dll")]
    private static extern uint AttachVirtualDisk(SafeFileHandle h, IntPtr security, uint flags, uint providerFlags, ref ATTACH_PARAMS parameters, IntPtr overlapped);
    [DllImport("virtdisk.dll")]
    private static extern uint DetachVirtualDisk(SafeFileHandle h, uint flags, uint providerFlags);
    [DllImport("virtdisk.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetVirtualDiskPhysicalPath(SafeFileHandle h, ref int size, [Out] char[] path);
    [DllImport("virtdisk.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetStorageDependencyInformation(SafeFileHandle h, uint flags, int size, IntPtr info, out int used);

    private static VIRTUAL_STORAGE_TYPE TypeFor(string path) => new()
    {
        DeviceId = path.EndsWith(".vhd", StringComparison.OrdinalIgnoreCase) ? 2u : 3u, // VHD : VHDX
        VendorId = VendorMicrosoft,
    };

    private static void Check(uint rc, string what)
    {
        if (rc != 0) throw new OperationFailedException("Vd.Error", $"{what}: {new System.ComponentModel.Win32Exception((int)rc).Message} ({rc})");
    }

    /// <summary>Creates a VHD/VHDX file. Dynamic files grow as data is written; fixed files allocate everything now.</summary>
    public static void Create(string path, ulong sizeBytes, bool dynamic)
    {
        if (File.Exists(path)) throw new OperationFailedException("Vd.Error.Exists", path);
        var type = TypeFor(path);
        var p = new CREATE_PARAMS_V2 { Version = 2, UniqueId = Guid.NewGuid(), MaximumSize = sizeBytes / 512 * 512 };
        Check(CreateVirtualDisk(ref type, path, 0, IntPtr.Zero, dynamic ? 0u : 1u, 0, ref p, IntPtr.Zero, out var h), "CreateVirtualDisk");
        h.Dispose();
    }

    private static SafeFileHandle Open(string path, bool readOnly = false)
    {
        var type = TypeFor(path);
        var p = new OPEN_PARAMS_V2 { Version = 2, ReadOnly = readOnly ? 1 : 0 };
        Check(OpenVirtualDisk(ref type, path, 0, 0, ref p, out var h), "OpenVirtualDisk");
        return h;
    }

    /// <summary>Attaches (mounts) the file as a disk that stays attached until detached or restart. Returns \\.\PhysicalDriveN.</summary>
    public static string Attach(string path, bool readOnly = false)
    {
        using var h = Open(path, readOnly);
        var a = new ATTACH_PARAMS { Version = 1 };
        uint flags = 0x4 | (readOnly ? 0x1u : 0u); // PERMANENT_LIFETIME | READ_ONLY
        Check(AttachVirtualDisk(h, IntPtr.Zero, flags, 0, ref a, IntPtr.Zero), "AttachVirtualDisk");
        var buf = new char[512];
        int size = buf.Length * 2;
        Check(GetVirtualDiskPhysicalPath(h, ref size, buf), "GetVirtualDiskPhysicalPath");
        return new string(buf).TrimEnd('\0');
    }

    public static void Detach(string path)
    {
        using var h = Open(path);
        Check(DetachVirtualDisk(h, 0, 0), "DetachVirtualDisk");
    }

    /// <summary>The file behind an attached virtual disk (\\.\PhysicalDriveN), or null.</summary>
    public static string? BackingFile(string devicePath)
    {
        using var h = Interop.NativeMethods.CreateFile(devicePath, 0, Interop.NativeMethods.FILE_SHARE_READ | Interop.NativeMethods.FILE_SHARE_WRITE,
            IntPtr.Zero, Interop.NativeMethods.OPEN_EXISTING, Interop.NativeMethods.FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);
        if (h.IsInvalid) return null;
        int size = 4096;
        var mem = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.WriteInt32(mem, 0, 2); // STORAGE_DEPENDENCY_INFO_VERSION_2
            // GET_STORAGE_DEPENDENCY_FLAG_HOST_VOLUMES (1) | DISK_HANDLE (2)
            if (GetStorageDependencyInformation(h, 0x3, size, mem, out _) != 0) return null;
            int count = Marshal.ReadInt32(mem, 4);
            if (count == 0) return null;
            var entry = mem + 8;                                  // first STORAGE_DEPENDENCY_INFO_TYPE_2
            var host = Marshal.PtrToStringUni(Marshal.ReadIntPtr(entry, 40));
            var rel = Marshal.PtrToStringUni(Marshal.ReadIntPtr(entry, 56));
            if (host is null || rel is null) return null;
            return Path.Combine(host, rel.TrimStart('\\'));
        }
        finally { Marshal.FreeHGlobal(mem); }
    }
}

[SupportedOSPlatform("windows")]
public sealed class WindowsVirtualDiskManager : Lasteast.Core.Info.IVirtualDiskManager
{
    public void Create(string path, ulong sizeBytes, bool dynamic) => VirtualDiskService.Create(path, sizeBytes, dynamic);
    public string Attach(string path) => VirtualDiskService.Attach(path);
    public string AttachReadOnly(string path) => VirtualDiskService.Attach(path, readOnly: true);
    public void Detach(string path) => VirtualDiskService.Detach(path);
    public string? BackingFile(string devicePath) => VirtualDiskService.BackingFile(devicePath);
    public void Expand(string path, ulong newSizeBytes) => VirtualDiskService.Expand(path, newSizeBytes);
    public void Shrink(string path, ulong? newSizeBytes) => VirtualDiskService.Shrink(path, newSizeBytes);
    public void Compact(string path) => VirtualDiskService.Compact(path);
    public void Convert(string source, string destination, bool dynamic) => VirtualDiskService.Convert(source, destination, dynamic);
}
