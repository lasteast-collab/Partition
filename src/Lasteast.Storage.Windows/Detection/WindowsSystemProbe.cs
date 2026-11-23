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

    private readonly IStorageInventoryProvider? _inventory;
    private readonly Lasteast.Core.Health.IDiskHealthProvider? _health;
    private readonly Lasteast.Core.Info.ISystemInfoProvider? _info;
    private readonly Lasteast.Core.Optimization.IVolumeOptimizer? _optimizer;

    public WindowsSystemProbe(IAppLog? log = null, IStorageInventoryProvider? inventory = null, Lasteast.Core.Health.IDiskHealthProvider? health = null,
        Lasteast.Core.Info.ISystemInfoProvider? info = null, Lasteast.Core.Optimization.IVolumeOptimizer? optimizer = null)
    {
        _log = log ?? NullLog.Instance;
        _inventory = inventory;
        _health = health;
        _info = info;
        _optimizer = optimizer;
    }

    public async Task<SystemSnapshot> CaptureAsync(CancellationToken cancellationToken = default)
    {
        var basic = await CaptureBasicAsync(cancellationToken);
        if (_inventory is null) return basic;
        var inv = await _inventory.GetInventoryAsync(cancellationToken);
        var health = new List<Lasteast.Core.Health.DiskHealthReport>();
        if (_health is not null)
            foreach (var d in inv.Disks)
                try { health.Add(await _health.ReadAsync(d, cancellationToken)); }
                catch (Exception ex) when (ex is not OperationCanceledException) { _log.Warn($"Health of disk {d.Number}: {ex.Message}"); }
        IReadOnlyList<Lasteast.Core.Info.BitLockerVolume>? bl = null;
        if (_info is not null)
            try { bl = await _info.BitLockerAsync(); } catch (Exception ex) when (ex is not OperationCanceledException) { _log.Warn("BitLocker: " + ex.Message); }
        var frag = new Dictionary<char, int>();
        var dirty = new List<char>();
        foreach (var d in inv.Disks)
        foreach (var p in d.Partitions)
        {
            if (p.Volume?.DriveLetter is not { } l || p.Volume.AccessError is not null) continue;
            if (IsDirty(l)) dirty.Add(l);
            if (_optimizer is not null && Lasteast.Core.Optimization.OptimizePlanner.For(d, p) == Lasteast.Core.Optimization.OptimizeAction.Defragment)
            {
                var analysis = _optimizer.AnalyzeAsync(l, cancellationToken);
                if (await Task.WhenAny(analysis, Task.Delay(TimeSpan.FromSeconds(45), cancellationToken)) == analysis && analysis.Result is { } r) frag[l] = r.TotalPercent;
            }
        }
        return basic with { Health = health, BitLocker = bl, DirtyVolumes = dirty, Fragmentation = frag };
    }

    private const uint FSCTL_IS_VOLUME_DIRTY = 0x00090078;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(Microsoft.Win32.SafeHandles.SafeFileHandle h, uint code, IntPtr inBuf, int inSize, out uint outBuf, int outSize, out int returned, IntPtr overlapped);

    /// <summary>The volume's "dirty bit": the file system asked for a check (FSCTL_IS_VOLUME_DIRTY, read-only query).</summary>
    private static bool IsDirty(char letter)
    {
        try
        {
            using var h = Lasteast.Storage.Windows.Operations.RawNative.Open($@"\\.\{letter}:", write: false);
            return DeviceIoControl(h, FSCTL_IS_VOLUME_DIRTY, IntPtr.Zero, 0, out uint flags, 4, out _, IntPtr.Zero) && (flags & 1) != 0;
        }
        catch (Exception) { return false; }
    }

    /// <summary>WinRE is configured when ReAgent.xml names a WinRE boot entry (locale-independent, unlike reagentc's output).</summary>
    private static bool? WinReConfigured()
    {
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "Recovery", "ReAgent.xml");
            if (!File.Exists(path)) return null;
            var x = System.Xml.Linq.XDocument.Load(path);
            var id = x.Descendants("WinreBCD").FirstOrDefault()?.Attribute("id")?.Value;
            return id is not null && Guid.TryParse(id.Trim('{', '}'), out var g) && g != Guid.Empty;
        }
        catch (Exception) { return null; }
    }

    private Task<SystemSnapshot> CaptureBasicAsync(CancellationToken cancellationToken) => Task.Run(() =>
    {
        var winRoot = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? @"C:\";
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        return new SystemSnapshot
        {
            RecoveryEnvironmentConfigured = WinReConfigured(),
            DownloadsBytes = FolderSize(downloads, cancellationToken),
            OldDownloadsBytes = FolderSize(downloads, cancellationToken, DateTime.UtcNow.AddDays(-90)),
            WindowsDrive = char.ToUpperInvariant(winRoot[0]),
            UserTempBytes = FolderSize(Path.GetTempPath(), cancellationToken),
            WindowsTempBytes = FolderSize(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp"), cancellationToken),
            RecycleBinBytes = RecycleBinSize(),
            HibernationFileBytes = RootFileSize(winRoot, "hiberfil.sys"),
            PageFileBytes = RootFileSize(winRoot, "pagefile.sys"),
        };
    }, cancellationToken);

    private ulong? FolderSize(string path, CancellationToken ct, DateTime? olderThan = null)
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
                try { if (olderThan is null || f.LastWriteTimeUtc < olderThan) total += (ulong)f.Length; } catch (IOException) { }
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
