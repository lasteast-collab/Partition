using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Lasteast.Storage.Windows.Interop;

/// <summary>
/// Win32 declarations used by the read-only detection layer.
/// Every handle opened here uses dwDesiredAccess = 0 (query-only): it cannot read or write sectors.
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class NativeMethods
{
    public const uint IOCTL_DISK_GET_DRIVE_GEOMETRY_EX = 0x000700A0;
    public const uint IOCTL_DISK_GET_DRIVE_LAYOUT_EX = 0x00070050;
    public const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;
    public const uint IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS = 0x00560000;
    public const uint IOCTL_DISK_IS_WRITABLE = 0x00070024;

    public const uint FILE_SHARE_READ = 0x1;
    public const uint FILE_SHARE_WRITE = 0x2;
    public const uint OPEN_EXISTING = 3;
    public const uint FILE_ATTRIBUTE_NORMAL = 0x80;

    public const int ERROR_FILE_NOT_FOUND = 2;
    public const int ERROR_PATH_NOT_FOUND = 3;
    public const int ERROR_INSUFFICIENT_BUFFER = 122;
    public const int ERROR_MORE_DATA = 234;
    public const int ERROR_NO_MORE_FILES = 18;
    public const int ERROR_WRITE_PROTECT = 19;

    public const uint SEM_FAILCRITICALERRORS = 0x0001;
    public const uint FILE_READ_ONLY_VOLUME = 0x00080000;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeviceIoControl(SafeFileHandle device, uint ioControlCode,
        byte[]? inBuffer, int inBufferSize, byte[]? outBuffer, int outBufferSize,
        out int bytesReturned, IntPtr overlapped);

    [DllImport("kernel32.dll", EntryPoint = "FindFirstVolumeW", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr FindFirstVolume(StringBuilder volumeName, int bufferLength);

    [DllImport("kernel32.dll", EntryPoint = "FindNextVolumeW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool FindNextVolume(IntPtr findVolume, StringBuilder volumeName, int bufferLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool FindVolumeClose(IntPtr findVolume);

    [DllImport("kernel32.dll", EntryPoint = "GetVolumePathNamesForVolumeNameW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetVolumePathNamesForVolumeName(string volumeName, char[]? pathNames, int bufferLength, out int returnLength);

    [DllImport("kernel32.dll", EntryPoint = "GetVolumeInformationW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetVolumeInformation(string rootPathName, StringBuilder? volumeNameBuffer, int volumeNameSize,
        out uint volumeSerialNumber, out uint maximumComponentLength, out uint fileSystemFlags,
        StringBuilder? fileSystemNameBuffer, int fileSystemNameSize);

    [DllImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetDiskFreeSpaceEx(string directoryName, out ulong freeBytesAvailable,
        out ulong totalNumberOfBytes, out ulong totalNumberOfFreeBytes);

    [DllImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetDiskFreeSpace(string rootPathName, out uint sectorsPerCluster, out uint bytesPerSector,
        out uint numberOfFreeClusters, out uint totalNumberOfClusters);

    [DllImport("kernel32.dll", EntryPoint = "GetVolumePathNameW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetVolumePathName(string fileName, StringBuilder volumePathName, int bufferLength);

    [DllImport("kernel32.dll", EntryPoint = "GetVolumeNameForVolumeMountPointW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetVolumeNameForVolumeMountPoint(string volumeMountPoint, StringBuilder volumeName, int bufferLength);

    [DllImport("kernel32.dll", EntryPoint = "QueryDosDeviceW", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern int QueryDosDevice(string deviceName, char[] targetPath, int max);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetThreadErrorMode(uint newMode, out uint oldMode);

    /// <summary>FIRMWARE_TYPE: 1 = BIOS, 2 = UEFI.</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetFirmwareType(out uint firmwareType);

    public static Win32Exception LastError(string context)
    {
        var code = Marshal.GetLastWin32Error();
        return new Win32Exception(code, $"{context}: {new Win32Exception(code).Message} (0x{code:X8})");
    }

    /// <summary>Opens a device for IOCTL queries only (no read/write access to data).</summary>
    public static SafeFileHandle OpenForQuery(string path) =>
        CreateFile(path, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);

    /// <summary>Issues an IOCTL, growing the output buffer until the driver accepts it.</summary>
    public static byte[]? Ioctl(SafeFileHandle handle, uint code, byte[]? input, int initialSize, out int error)
    {
        int size = initialSize;
        for (int attempt = 0; attempt < 8; attempt++)
        {
            var buffer = new byte[size];
            if (DeviceIoControl(handle, code, input, input?.Length ?? 0, buffer, buffer.Length, out var returned, IntPtr.Zero))
            {
                error = 0;
                if (returned < buffer.Length) Array.Resize(ref buffer, Math.Max(returned, 0));
                return buffer;
            }
            error = Marshal.GetLastWin32Error();
            if (error is not (ERROR_INSUFFICIENT_BUFFER or ERROR_MORE_DATA)) return null;
            size *= 2;
        }
        error = ERROR_INSUFFICIENT_BUFFER;
        return null;
    }
}
