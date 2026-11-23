using System.Diagnostics;

namespace Lasteast.Core.Analysis;

public enum FileCategory { Videos, Images, Audio, Documents, Archives, Applications, Games, Temporary, System, Other }

public static class FileCategories
{
    private static readonly Dictionary<string, FileCategory> ByExtension = Build();

    private static Dictionary<string, FileCategory> Build()
    {
        var d = new Dictionary<string, FileCategory>(StringComparer.OrdinalIgnoreCase);
        void Add(FileCategory c, params string[] exts) { foreach (var e in exts) d["." + e] = c; }
        Add(FileCategory.Videos, "mp4", "mkv", "avi", "mov", "wmv", "m4v", "webm", "flv", "mpg", "mpeg", "ts", "m2ts", "3gp");
        Add(FileCategory.Images, "jpg", "jpeg", "png", "gif", "bmp", "tif", "tiff", "webp", "heic", "heif", "raw", "cr2", "nef", "arw", "dng", "psd", "svg", "ico");
        Add(FileCategory.Audio, "mp3", "wav", "flac", "aac", "m4a", "ogg", "wma", "opus", "aiff");
        Add(FileCategory.Documents, "pdf", "doc", "docx", "xls", "xlsx", "ppt", "pptx", "odt", "ods", "odp", "txt", "rtf", "md", "csv", "epub", "one", "pages", "key");
        Add(FileCategory.Archives, "zip", "rar", "7z", "tar", "gz", "bz2", "xz", "zst", "cab", "iso", "img", "vhd", "vhdx", "vmdk", "wim", "esd");
        Add(FileCategory.Applications, "exe", "msi", "msix", "appx", "dll", "sys", "apk", "dmg", "jar");
        Add(FileCategory.Temporary, "tmp", "temp", "bak", "old", "dmp", "etl", "chk");
        return d;
    }

    private static readonly string[] GameFolders = { @"\steamapps\", @"\Epic Games\", @"\GOG Galaxy\Games\", @"\XboxGames\", @"\Riot Games\", @"\Origin Games\", @"\EA Games\", @"\Ubisoft Game Launcher\games\" };
    private static readonly string[] TempFolders = { @"\Temp\", @"\Tmp\", @"\INetCache\", @"\Cache\", @"\CrashDumps\" };

    public static FileCategory Classify(string path)
    {
        var p = path.Replace('/', '\\');
        if (GameFolders.Any(g => p.Contains(g, StringComparison.OrdinalIgnoreCase))) return FileCategory.Games;
        var ext = Path.GetExtension(path);
        if (ByExtension.TryGetValue(ext, out var c)) return c;
        if (TempFolders.Any(t => p.Contains(t, StringComparison.OrdinalIgnoreCase))) return FileCategory.Temporary;
        var name = Path.GetFileName(path);
        if (name.Equals("pagefile.sys", StringComparison.OrdinalIgnoreCase) || name.Equals("hiberfil.sys", StringComparison.OrdinalIgnoreCase)
            || name.Equals("swapfile.sys", StringComparison.OrdinalIgnoreCase)) return FileCategory.System;
        if (name.StartsWith("~$", StringComparison.Ordinal)) return FileCategory.Temporary;
        return FileCategory.Other;
    }
}

public sealed record FileEntry(string Path, ulong Size, DateTime LastWriteUtc, FileCategory Category)
{
    public string Name => System.IO.Path.GetFileName(Path);
    public string Folder => System.IO.Path.GetDirectoryName(Path) ?? "";
    public string Extension => System.IO.Path.GetExtension(Path).ToLowerInvariant();
}

/// <summary>A folder with aggregated size. Children are kept only for folders large enough to matter (see <see cref="ScanOptions.MinFolderBytesToKeep"/>).</summary>
public sealed class FolderNode
{
    public FolderNode(string path, FolderNode? parent)
    {
        Path = path;
        Parent = parent;
    }

    public string Path { get; }
    public string Name => System.IO.Path.GetFileName(Path.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : Path;
    public FolderNode? Parent { get; }
    public ulong Size { get; internal set; }
    /// <summary>Bytes of files directly in this folder.</summary>
    public ulong OwnFilesSize { get; internal set; }
    public long FileCount { get; internal set; }
    public List<FolderNode> Children { get; } = new();
    /// <summary>Largest files directly in this folder (for the treemap's last level).</summary>
    public List<FileEntry> TopFiles { get; } = new();
    public bool Inaccessible { get; internal set; }
}

public sealed record ScanOptions
{
    public int TopFilesCount { get; init; } = 500;
    public int TopFilesPerFolder { get; init; } = 20;
    public ulong MinFolderBytesToKeep { get; init; } = 1024 * 1024;
    /// <summary>Files at least this large are tracked as duplicate candidates.</summary>
    public ulong DuplicateCandidateMinBytes { get; init; } = 1024 * 1024;
    public TimeSpan OldFileAge { get; init; } = TimeSpan.FromDays(365);
    public int MaxEmptyFolders { get; init; } = 5000;
    /// <summary>Paths never entered (case-insensitive prefix match).</summary>
    public IReadOnlyList<string> Exclusions { get; init; } = Array.Empty<string>();
}

public sealed record ScanProgress(long Files, long Folders, ulong Bytes, string CurrentFolder);

public sealed class ScanResult
{
    public required FolderNode Root { get; init; }
    public required IReadOnlyList<FileEntry> LargestFiles { get; init; }
    public required IReadOnlyList<FileEntry> OldLargeFiles { get; init; }
    public required IReadOnlyList<FileEntry> RecentFiles { get; init; }
    public required IReadOnlyList<string> EmptyFolders { get; init; }
    public required IReadOnlyDictionary<FileCategory, (ulong Bytes, long Count)> ByCategory { get; init; }
    public required IReadOnlyDictionary<string, (ulong Bytes, long Count)> ByExtension { get; init; }
    /// <summary>Files grouped by exact size (≥ the candidate threshold) — input for <see cref="DuplicateFinder"/>.</summary>
    public required IReadOnlyDictionary<ulong, List<string>> SizeGroups { get; init; }
    public long Files { get; init; }
    public long Folders { get; init; }
    public long InaccessibleFolders { get; init; }
    public bool Cancelled { get; init; }
    public TimeSpan Duration { get; init; }
    public DateTime ScannedAtUtc { get; init; }

    public IEnumerable<FolderNode> LargestFolders(int count)
    {
        var all = new List<FolderNode>();
        void Walk(FolderNode n) { foreach (var c in n.Children) { all.Add(c); Walk(c); } }
        Walk(Root);
        return all.OrderByDescending(f => f.Size).Take(count);
    }
}

/// <summary>
/// Read-only recursive scan of a folder or drive. Never follows reparse points (junctions, symlinks,
/// OneDrive placeholders are not recalled), skips inaccessible folders, honours exclusions and cancellation.
/// Memory stays bounded: only top-N lists, aggregated folder sizes and duplicate candidates are kept.
/// </summary>
public static class StorageScanner
{
    public static Task<ScanResult> ScanAsync(string root, ScanOptions? options = null, IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
        => Task.Run(() => Scan(root, options ?? new ScanOptions(), progress, cancellationToken), cancellationToken);

    public static ScanResult Scan(string root, ScanOptions o, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var now = DateTime.UtcNow;
        var largest = new TopN<FileEntry>(o.TopFilesCount, f => f.Size);
        var oldLarge = new TopN<FileEntry>(o.TopFilesCount, f => f.Size);
        var recent = new TopN<FileEntry>(200, f => (ulong)f.LastWriteUtc.Ticks);
        var empty = new List<string>();
        var byCat = new Dictionary<FileCategory, (ulong, long)>();
        var byExt = new Dictionary<string, (ulong, long)>(StringComparer.OrdinalIgnoreCase);
        var sizeGroups = new Dictionary<ulong, List<string>>();
        long files = 0, folders = 0, inaccessible = 0;
        ulong bytes = 0;
        var lastReport = Stopwatch.StartNew();
        bool cancelled = false;

        var rootNode = new FolderNode(root, null);
        var enumOptions = new EnumerationOptions
        {
            RecurseSubdirectories = false, IgnoreInaccessible = false, AttributesToSkip = 0, ReturnSpecialDirectories = false,
        };

        // Iterative post-order walk (no recursion depth limits on very deep trees).
        var stack = new Stack<(FolderNode Node, bool Visited)>();
        stack.Push((rootNode, false));
        while (stack.Count > 0)
        {
            var (node, visited) = stack.Pop();
            if (visited)
            {
                // children are done: aggregate, prune tiny children
                node.Size = node.OwnFilesSize + node.Children.Aggregate(0UL, (a, c) => a + c.Size);
                node.FileCount += node.Children.Sum(c => c.FileCount);
                node.Children.RemoveAll(c => c.Size < o.MinFolderBytesToKeep); // already counted in node.Size
                node.Children.Sort((a, b) => b.Size.CompareTo(a.Size));
                continue;
            }
            if (ct.IsCancellationRequested) { cancelled = true; break; }

            stack.Push((node, true));
            folders++;
            bool hasEntries = false;
            var topLocal = new TopN<FileEntry>(o.TopFilesPerFolder, f => f.Size);
            try
            {
                var dir = new DirectoryInfo(node.Path);
                foreach (var info in dir.EnumerateFileSystemInfos("*", enumOptions))
                {
                    hasEntries = true;
                    if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue; // never follow links / cloud placeholders
                    if (info is DirectoryInfo sub)
                    {
                        if (IsExcluded(sub.FullName, o.Exclusions)) continue;
                        var child = new FolderNode(sub.FullName, node);
                        node.Children.Add(child);
                        stack.Push((child, false));
                    }
                    else if (info is FileInfo f)
                    {
                        if (IsExcluded(f.FullName, o.Exclusions)) continue;
                        ulong len;
                        try { len = (ulong)f.Length; } catch (IOException) { continue; }
                        var cat = FileCategories.Classify(f.FullName);
                        var entry = new FileEntry(f.FullName, len, f.LastWriteTimeUtc, cat);
                        files++;
                        bytes += len;
                        node.OwnFilesSize += len;
                        node.FileCount++;
                        largest.Add(entry);
                        topLocal.Add(entry);
                        recent.Add(entry);
                        if (now - entry.LastWriteUtc > o.OldFileAge) oldLarge.Add(entry);
                        byCat[cat] = byCat.TryGetValue(cat, out var c) ? (c.Item1 + len, c.Item2 + 1) : (len, 1);
                        var ext = string.IsNullOrEmpty(entry.Extension) ? "(none)" : entry.Extension;
                        byExt[ext] = byExt.TryGetValue(ext, out var e) ? (e.Item1 + len, e.Item2 + 1) : (len, 1);
                        if (len >= o.DuplicateCandidateMinBytes)
                        {
                            if (!sizeGroups.TryGetValue(len, out var list)) sizeGroups[len] = list = new List<string>(2);
                            list.Add(f.FullName);
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
            {
                node.Inaccessible = true;
                inaccessible++;
                hasEntries = true; // unknown, not empty
            }
            node.TopFiles.AddRange(topLocal.Sorted());
            if (!hasEntries && node.Parent is not null && empty.Count < o.MaxEmptyFolders) empty.Add(node.Path);

            if (progress is not null && lastReport.ElapsedMilliseconds > 200)
            {
                progress.Report(new ScanProgress(files, folders, bytes, node.Path));
                lastReport.Restart();
            }
        }

        if (cancelled) FinishPartial(rootNode, o);
        progress?.Report(new ScanProgress(files, folders, bytes, root));

        foreach (var key in sizeGroups.Where(kv => kv.Value.Count < 2).Select(kv => kv.Key).ToList()) sizeGroups.Remove(key);

        return new ScanResult
        {
            Root = rootNode,
            LargestFiles = largest.Sorted(),
            OldLargeFiles = oldLarge.Sorted(),
            RecentFiles = recent.Sorted(),
            EmptyFolders = empty,
            ByCategory = byCat,
            ByExtension = byExt,
            SizeGroups = sizeGroups,
            Files = files, Folders = folders, InaccessibleFolders = inaccessible,
            Cancelled = cancelled, Duration = sw.Elapsed, ScannedAtUtc = now,
        };
    }

    /// <summary>After cancellation some folders were never aggregated; compute sizes bottom-up for what was read.</summary>
    private static void FinishPartial(FolderNode n, ScanOptions o)
    {
        foreach (var c in n.Children) FinishPartial(c, o);
        n.Size = n.OwnFilesSize + n.Children.Aggregate(0UL, (a, c) => a + c.Size);
        n.Children.Sort((a, b) => b.Size.CompareTo(a.Size));
    }

    public static bool IsExcluded(string path, IReadOnlyList<string> exclusions)
        => exclusions.Any(x => x.Length > 0 && path.StartsWith(x.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)
                               && (path.Length == x.TrimEnd('\\', '/').Length || path[x.TrimEnd('\\', '/').Length] is '\\' or '/'));
}

/// <summary>Keeps the N largest items by key with O(log N) inserts.</summary>
internal sealed class TopN<T>
{
    private readonly int _n;
    private readonly Func<T, ulong> _key;
    private readonly PriorityQueue<T, ulong> _heap = new();

    public TopN(int n, Func<T, ulong> key) { _n = n; _key = key; }

    public void Add(T item)
    {
        var k = _key(item);
        if (_heap.Count < _n) _heap.Enqueue(item, k);
        else if (_heap.TryPeek(out _, out var min) && k > min) _heap.EnqueueDequeue(item, k);
    }

    public List<T> Sorted() => _heap.UnorderedItems.OrderByDescending(x => x.Priority).Select(x => x.Element).ToList();
}
