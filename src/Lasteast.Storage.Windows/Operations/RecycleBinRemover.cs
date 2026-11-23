using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Lasteast.Core.Cleanup;

namespace Lasteast.Storage.Windows.Operations;

/// <summary>
/// Removes files through the Windows shell: <see cref="RemovalMode.RecycleBin"/> uses FOF_ALLOWUNDO so the
/// file can be restored from the Recycle Bin. No UI, no confirmation prompts from the shell (Lasteast already asked).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RecycleBinRemover : IFileRemover
{
    private const uint FO_DELETE = 0x0003;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOERRORUI = 0x0400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT op);

    public void Remove(string path, RemovalMode mode)
    {
        if (mode == RemovalMode.Permanent)
        {
            File.Delete(path);
            return;
        }
        var op = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE,
            pFrom = path + "\0\0", // double-null-terminated list
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI,
        };
        int rc = SHFileOperation(ref op);
        if (rc != 0 || op.fAnyOperationsAborted)
            throw new IOException($"Could not move to the Recycle Bin (code 0x{rc:X}).");
    }
}
