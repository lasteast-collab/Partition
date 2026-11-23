using System.Security.Cryptography;

namespace Lasteast.Core.Backup;

public sealed record BackupProgress(long Files, long Bytes, string Current);
public sealed record BackupResult(int Copied, int Skipped, int Failed, long Bytes, bool Verified, IReadOnlyList<(string Path, string Error)> Errors);

/// <summary>
/// Plain, verifiable data backup (spec: "back up data before repairs"): copies folders file by file into
/// destination\{folder name}, never follows links/junctions, never overwrites, optionally re-reads both sides and
/// compares SHA-256. Refuses a destination inside a source (it would copy itself).
/// </summary>
public static class FolderBackup
{
    public static BackupResult Run(IReadOnlyList<string> sources, string destination, bool verify, IProgress<BackupProgress>? progress, CancellationToken ct)
    {
        var dest = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var s in sources)
        {
            var src = Path.GetFullPath(s).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (dest.StartsWith(src, StringComparison.OrdinalIgnoreCase)) throw new IOException($"Backup.Error.Inside|{s}");
        }
        int copied = 0, skipped = 0;
        long files = 0, bytes = 0;
        var errors = new List<(string, string)>();
        foreach (var s in sources)
        {
            var src = Path.GetFullPath(s);
            if (!Directory.Exists(src)) { errors.Add((s, "not found")); continue; }
            var targetRoot = Path.Combine(destination, Path.GetFileName(src.TrimEnd(Path.DirectorySeparatorChar)) is { Length: > 0 } n ? n : "root");
            var stack = new Stack<string>();
            stack.Push(src);
            while (stack.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var dir = stack.Pop();
                IEnumerable<string> entries;
                try { entries = Directory.EnumerateFileSystemEntries(dir).ToList(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { errors.Add((dir, ex.Message)); continue; }
                foreach (var e in entries)
                {
                    FileAttributes attr;
                    try { attr = File.GetAttributes(e); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { errors.Add((e, ex.Message)); continue; }
                    if ((attr & FileAttributes.ReparsePoint) != 0) { skipped++; continue; } // links, junctions, cloud placeholders
                    if ((attr & FileAttributes.Directory) != 0) { stack.Push(e); continue; }
                    var target = Path.Combine(targetRoot, Path.GetRelativePath(src, e));
                    try
                    {
                        if (File.Exists(target)) { skipped++; continue; }
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        File.Copy(e, target);
                        File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(e));
                        if (verify && !Same(e, target)) { errors.Add((e, "verification failed")); File.Delete(target); continue; }
                        copied++;
                        bytes += new FileInfo(target).Length;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { errors.Add((e, ex.Message)); }
                    files++;
                    if ((files & 31) == 0) progress?.Report(new BackupProgress(files, bytes, e));
                }
            }
        }
        progress?.Report(new BackupProgress(files, bytes, ""));
        return new BackupResult(copied, skipped, errors.Count, bytes, verify, errors);
    }

    private static bool Same(string a, string b)
    {
        using var fa = File.OpenRead(a);
        using var fb = File.OpenRead(b);
        return fa.Length == fb.Length && SHA256.HashData(fa).AsSpan().SequenceEqual(SHA256.HashData(fb));
    }
}
