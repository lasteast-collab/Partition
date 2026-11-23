namespace Lasteast.Core.Cleanup;

/// <summary>A place where junk accumulates. Only files under <see cref="Root"/> can ever be offered for removal.</summary>
public sealed record CleanupTarget(string Id, string NameKey, string DescriptionKey, string Root, TimeSpan MinAge, bool RequiresAdmin, bool DefaultSelected);

public sealed record CleanupFile(string Path, ulong Size, DateTime LastWriteUtc);

public sealed record CleanupScanResult(CleanupTarget Target, IReadOnlyList<CleanupFile> Files, bool Accessible, string? Error)
{
    public ulong TotalBytes => Files.Aggregate(0UL, (a, f) => a + f.Size);
}

public enum RemovalMode { RecycleBin, Permanent }

public sealed record CleanupReport(int Removed, ulong FreedBytes, int Skipped, IReadOnlyList<string> Errors, bool Cancelled);

/// <summary>Removes one file (to the Recycle Bin or permanently). Platform-specific.</summary>
public interface IFileRemover
{
    void Remove(string path, RemovalMode mode);
}

/// <summary>Managed fallback (permanent delete). Used by tests and non-Windows hosts.</summary>
public sealed class PermanentFileRemover : IFileRemover
{
    public void Remove(string path, RemovalMode mode) => File.Delete(path);
}

/// <summary>
/// Junk-file cleanup. Two strictly separate steps:
///  1. <see cref="ScanAsync"/> — read-only: lists files under each target root that are older than the target's minimum age;
///  2. <see cref="CleanAsync"/> — removes exactly the files the user confirmed, after re-checking every one:
///     still under its target root, not a reparse point, not excluded, not modified since the scan.
/// Locked or changed files are skipped and reported, never forced.
/// </summary>
public sealed class CleanupService
{
    private readonly IFileRemover _remover;

    public CleanupService(IFileRemover remover) => _remover = remover;

    /// <summary>Standard targets on Windows. Paths come from the OS, never from user input.</summary>
    public static IReadOnlyList<CleanupTarget> WindowsTargets()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var day = TimeSpan.FromDays(1);
        return new[]
        {
            new CleanupTarget("user-temp", "Clean.UserTemp", "Clean.UserTemp.Desc", Path.GetTempPath(), day, false, true),
            new CleanupTarget("windows-temp", "Clean.WinTemp", "Clean.WinTemp.Desc", Path.Combine(windows, "Temp"), day, true, true),
            new CleanupTarget("crash-dumps", "Clean.CrashDumps", "Clean.CrashDumps.Desc", Path.Combine(local, "CrashDumps"), TimeSpan.Zero, false, true),
            new CleanupTarget("wer-user", "Clean.Wer", "Clean.Wer.Desc", Path.Combine(local, "Microsoft", "Windows", "WER"), TimeSpan.Zero, false, true),
            new CleanupTarget("wer-system", "Clean.WerSystem", "Clean.Wer.Desc", Path.Combine(programData, "Microsoft", "Windows", "WER"), TimeSpan.Zero, true, false),
            new CleanupTarget("inet-cache", "Clean.InetCache", "Clean.InetCache.Desc", Path.Combine(local, "Microsoft", "Windows", "INetCache"), day, false, false),
            new CleanupTarget("delivery-opt", "Clean.DeliveryOpt", "Clean.DeliveryOpt.Desc",
                Path.Combine(windows, "ServiceProfiles", "NetworkService", "AppData", "Local", "Microsoft", "Windows", "DeliveryOptimization", "Cache"), day, true, false),
        };
    }

    public Task<IReadOnlyList<CleanupScanResult>> ScanAsync(IEnumerable<CleanupTarget> targets, IReadOnlyList<string> exclusions,
        CancellationToken ct = default) => Task.Run(() => (IReadOnlyList<CleanupScanResult>)targets.Select(t => ScanTarget(t, exclusions, ct)).ToList(), ct);

    public static CleanupScanResult ScanTarget(CleanupTarget t, IReadOnlyList<string> exclusions, CancellationToken ct = default)
    {
        if (!Directory.Exists(t.Root)) return new CleanupScanResult(t, Array.Empty<CleanupFile>(), true, null);
        var cutoff = DateTime.UtcNow - t.MinAge;
        var files = new List<CleanupFile>();
        var opts = new EnumerationOptions
        {
            RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System,
        };
        try
        {
            foreach (var f in new DirectoryInfo(t.Root).EnumerateFiles("*", opts))
            {
                if (ct.IsCancellationRequested) break;
                if (f.LastWriteTimeUtc > cutoff) continue;
                if (Analysis.StorageScanner.IsExcluded(f.FullName, exclusions)) continue;
                try { files.Add(new CleanupFile(f.FullName, (ulong)f.Length, f.LastWriteTimeUtc)); } catch (IOException) { }
            }
            return new CleanupScanResult(t, files, true, null);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return new CleanupScanResult(t, files, false, ex.Message);
        }
    }

    public Task<CleanupReport> CleanAsync(IReadOnlyList<(CleanupTarget Target, CleanupFile File)> confirmed, RemovalMode mode,
        IReadOnlyList<string> exclusions, IProgress<int>? progress = null, CancellationToken ct = default)
        => Task.Run(() => Clean(confirmed, mode, exclusions, progress, ct), ct);

    public CleanupReport Clean(IReadOnlyList<(CleanupTarget Target, CleanupFile File)> confirmed, RemovalMode mode,
        IReadOnlyList<string> exclusions, IProgress<int>? progress, CancellationToken ct)
    {
        int removed = 0, skipped = 0;
        ulong freed = 0;
        var errors = new List<string>();
        for (int i = 0; i < confirmed.Count; i++)
        {
            if (ct.IsCancellationRequested) return new CleanupReport(removed, freed, skipped + confirmed.Count - i, errors, true);
            var (t, f) = confirmed[i];
            var reason = Recheck(t, f, exclusions);
            if (reason is not null)
            {
                skipped++;
                if (errors.Count < 50) errors.Add($"{f.Path}: {reason}");
                continue;
            }
            try
            {
                _remover.Remove(f.Path, mode);
                removed++;
                freed += f.Size;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                skipped++; // in use or protected: skipped, never forced
                if (errors.Count < 50) errors.Add($"{f.Path}: {ex.Message}");
            }
            if (i % 25 == 0) progress?.Report(i);
        }
        progress?.Report(confirmed.Count);
        return new CleanupReport(removed, freed, skipped, errors, false);
    }

    /// <summary>Returns why a file must not be removed now, or null when it is safe to remove.</summary>
    public static string? Recheck(CleanupTarget t, CleanupFile f, IReadOnlyList<string> exclusions)
    {
        string full, root;
        try
        {
            full = Path.GetFullPath(f.Path);
            root = Path.GetFullPath(t.Root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        }
        catch (Exception) { return "invalid path"; }
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return "outside the cleanup folder";
        if (Analysis.StorageScanner.IsExcluded(full, exclusions)) return "excluded";
        var info = new FileInfo(full);
        if (!info.Exists) return "no longer exists";
        if ((info.Attributes & (FileAttributes.ReparsePoint | FileAttributes.System)) != 0) return "link or system file";
        // no parent folder between the file and the root may be a link (prevents junction redirection)
        for (var d = info.Directory; d is not null && d.FullName.Length + 1 > root.Length; d = d.Parent)
            if ((d.Attributes & FileAttributes.ReparsePoint) != 0) return "inside a linked folder";
        if (info.LastWriteTimeUtc != f.LastWriteUtc || (ulong)info.Length != f.Size) return "changed since the scan";
        return null;
    }
}
