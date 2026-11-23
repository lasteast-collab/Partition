using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Lasteast.Core.Abstractions;
using Lasteast.Core.Logging;
using Lasteast.Core.SmartFix;

namespace Lasteast.Storage.Windows.Detection;

/// <summary>
/// Read-only measurements for Smart Fix: temp folder sizes, Recycle Bin size, hibernation and page file sizes.
/// Nothing is deleted or changed. Folder walks are time-boxed and skip reparse points.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsSystemProbe : ISystemProbe
{
    private static readonly TimeSpan WalkBudget = TimeSpan.FromSeconds(8);
    private readonly IAppLog _log;

    public WindowsSystemProbe(IAppLog? log = null) => _log = log ?? NullLog.Instance;

    public Task<SystemSnapshot> CaptureAsync(CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        var winRoot = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? @"C:\";
        return new SystemSnapshot
        {
            WindowsDrive = char.ToUpperInvariant(winRoot[0]),
            UserTempBytes = FolderSize(Path.GetTempPath(), cancellationToken),
            WindowsTempBytes = FolderSize(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp"), cancellationToken),
            RecycleBinBytes = RecycleBinSize(),
            HibernationFileBytes = RootFileSize(winRoot, "hiberfil.sys"),
            PageFileBytes = RootFileSize(winRoot, "pagefile.sys"),
        };
    }, cancellationToken);

    private ulong? FolderSize(string path, CancellationToken ct)
    {
        if (!Directory.Exists(path)) return null;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        ulong total = 0;
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint,
            ReturnSpecialDirectories = false,
        };
        try
        {
            foreach (var f in new DirectoryInfo(path).EnumerateFiles("*", options))
            {
                if (ct.IsCancellationRequested || sw.Elapsed > WalkBudget) break;
                try { total += (ulong)f.Length; } catch (IOException) { }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            _log.Warn($"Size of {path}: {ex.Message}");
            return total == 0 ? null : total;
        }
        return total;
    }

    private static ulong? RootFileSize(string root, string name)
    {
        try
        {
            var options = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = true };
            var f = new DirectoryInfo(root).EnumerateFiles(name, options).FirstOrDefault();
            return f is null ? null : (ulong)f.Length;
        }
        catch { return null; }
    }

    [StructLayout(LayoutKind.Sequential)] // natural alignment on x64 (shellapi.h packs to 1 only on 32-bit)
    private struct SHQUERYRBINFO
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBinW(string? pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);

    private static ulong? RecycleBinSize()
    {
        try
        {
            var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
            return SHQueryRecycleBinW(null, ref info) == 0 ? (ulong)Math.Max(0, info.i64Size) : null;
        }
        catch { return null; }
    }
}
