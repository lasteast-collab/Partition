using System.Security.Cryptography;

namespace Lasteast.Core.Files;

public sealed record TreeCopyProgress(long Files, long Bytes, long TotalBytes, string Current);
public sealed record TreeCopyReport(long Files, long Bytes, IReadOnlyList<(string Path, string Error)> Errors)
{
    public bool Ok => Errors.Count == 0;
}

/// <summary>
/// Copies a folder tree file by file, hashing what is written and reading the copy back to compare (SHA-256), and
/// keeps timestamps and attributes. Used where files must survive a destructive step (merge, reformat): the caller
/// proceeds only when the report is clean.
/// </summary>
public static class TreeCopier
{
    /// <summary>System folders of a volume that are not user data and cannot be copied.</summary>
    public static bool IsVolumeSystemEntry(string name) =>
        name.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase) || name.Equals("$RECYCLE.BIN", StringComparison.OrdinalIgnoreCase)
        || name.Equals("$Extend", StringComparison.OrdinalIgnoreCase) || name.StartsWith("$Txf", StringComparison.OrdinalIgnoreCase);

    public static (long Files, long Bytes) Measure(string root)
    {
        long files = 0, bytes = 0;
        foreach (var f in Enumerate(root)) { files++; bytes += f.Length; }
        return (files, bytes);
    }

    private static IEnumerable<FileInfo> Enumerate(string root)
    {
        var stack = new Stack<DirectoryInfo>();
        stack.Push(new DirectoryInfo(root));
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            foreach (var e in dir.EnumerateFileSystemInfos())
            {
                if (dir.FullName.TrimEnd('\\', '/') == new DirectoryInfo(root).FullName.TrimEnd('\\', '/') && IsVolumeSystemEntry(e.Name)) continue;
                if (e is DirectoryInfo d)
                {
                    if ((d.Attributes & FileAttributes.ReparsePoint) != 0) continue; // junctions: not followed
                    stack.Push(d);
                }
                else if (e is FileInfo f) yield return f;
            }
        }
    }

    public static TreeCopyReport Copy(string sourceRoot, string destRoot, IProgress<TreeCopyProgress>? progress, CancellationToken ct)
    {
        var errors = new List<(string, string)>();
        long files = 0, bytes = 0;
        var src = new DirectoryInfo(sourceRoot).FullName;
        long total = Measure(src).Bytes;
        Directory.CreateDirectory(destRoot);
        var buf = new byte[1 << 20];
        foreach (var f in Enumerate(src))
        {
            ct.ThrowIfCancellationRequested();
            var rel = Path.GetRelativePath(src, f.FullName);
            var target = Path.Combine(destRoot, rel);
            progress?.Report(new TreeCopyProgress(files, bytes, total, rel));
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                byte[] written;
                using (var input = new FileStream(f.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan))
                using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.WriteThrough))
                using (var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
                {
                    int n;
                    while ((n = input.Read(buf, 0, buf.Length)) > 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        h.AppendData(buf, 0, n);
                        output.Write(buf, 0, n);
                    }
                    output.Flush(true);
                    written = h.GetHashAndReset();
                }
                byte[] readBack;
                using (var check = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan))
                    readBack = SHA256.HashData(check);
                if (!readBack.AsSpan().SequenceEqual(written)) throw new IOException("the copy reads back differently");
                File.SetCreationTimeUtc(target, f.CreationTimeUtc);
                File.SetLastWriteTimeUtc(target, f.LastWriteTimeUtc);
                File.SetAttributes(target, f.Attributes & ~(FileAttributes.Compressed | FileAttributes.Encrypted | FileAttributes.SparseFile));
                files++;
                bytes += f.Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add((rel, ex.Message));
            }
        }
        // empty folders are part of the tree too
        foreach (var d in Directory.EnumerateDirectories(src, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
        {
            var rel = Path.GetRelativePath(src, d);
            if (IsVolumeSystemEntry(rel.Split(Path.DirectorySeparatorChar)[0])) continue;
            Directory.CreateDirectory(Path.Combine(destRoot, rel));
        }
        progress?.Report(new TreeCopyProgress(files, bytes, total, ""));
        return new TreeCopyReport(files, bytes, errors);
    }
}
