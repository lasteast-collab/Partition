using System.Security.Cryptography;

namespace Lasteast.Core.Analysis;

public sealed record DuplicateGroup(ulong FileSize, string Hash, IReadOnlyList<string> Paths)
{
    /// <summary>Space that would be freed by keeping one copy.</summary>
    public ulong WastedBytes => FileSize * (ulong)(Paths.Count - 1);
}

public sealed record DuplicateProgress(int GroupsChecked, int GroupsTotal, ulong BytesHashed);

/// <summary>
/// Finds byte-identical files: same size → same hash of the first 64 KiB → same full SHA-256.
/// Read-only. Files that disappear or are locked are skipped.
/// </summary>
public static class DuplicateFinder
{
    private const int PrefixBytes = 64 * 1024;

    public static Task<IReadOnlyList<DuplicateGroup>> FindAsync(IReadOnlyDictionary<ulong, List<string>> sizeGroups,
        IProgress<DuplicateProgress>? progress = null, CancellationToken ct = default)
        => Task.Run(() => Find(sizeGroups, progress, ct), ct);

    public static IReadOnlyList<DuplicateGroup> Find(IReadOnlyDictionary<ulong, List<string>> sizeGroups,
        IProgress<DuplicateProgress>? progress, CancellationToken ct)
    {
        var result = new List<DuplicateGroup>();
        int done = 0;
        ulong hashed = 0;
        foreach (var (size, paths) in sizeGroups.OrderByDescending(kv => kv.Key))
        {
            if (ct.IsCancellationRequested) break;
            done++;
            if (paths.Count < 2) continue;

            var byPrefix = paths.GroupBy(p => Hash(p, PrefixBytes, ref hashed)).Where(g => g.Key is not null && g.Count() > 1);
            foreach (var pg in byPrefix)
            {
                var candidates = pg.ToList();
                var byFull = size <= PrefixBytes
                    ? candidates.GroupBy(_ => pg.Key)
                    : candidates.GroupBy(p => Hash(p, long.MaxValue, ref hashed));
                foreach (var fg in byFull.Where(g => g.Key is not null && g.Count() > 1))
                    result.Add(new DuplicateGroup(size, fg.Key!, fg.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList()));
            }
            progress?.Report(new DuplicateProgress(done, sizeGroups.Count, hashed));
        }
        return result.OrderByDescending(g => g.WastedBytes).ToList();
    }

    private static string? Hash(string path, long maxBytes, ref ulong hashed)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);
            using var sha = SHA256.Create();
            var buffer = new byte[1 << 16];
            long remaining = maxBytes;
            int n;
            while (remaining > 0 && (n = fs.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining))) > 0)
            {
                sha.TransformBlock(buffer, 0, n, null, 0);
                remaining -= n;
                hashed += (ulong)n;
            }
            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            return Convert.ToHexString(sha.Hash!);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

public readonly record struct TreemapRect(int Index, double X, double Y, double Width, double Height);

/// <summary>Squarified treemap layout (Bruls, Huizing, van Wijk). Pure; sizes need not be sorted.</summary>
public static class Treemap
{
    public static IReadOnlyList<TreemapRect> Layout(IReadOnlyList<double> sizes, double x, double y, double width, double height)
    {
        var result = new List<TreemapRect>();
        if (width <= 0 || height <= 0) return result;
        var items = sizes.Select((s, i) => (Size: Math.Max(0, s), Index: i)).Where(t => t.Size > 0).OrderByDescending(t => t.Size).ToList();
        double total = items.Sum(t => t.Size);
        if (total <= 0) return result;
        double scale = width * height / total;
        var queue = new Queue<(double Area, int Index)>(items.Select(t => (t.Size * scale, t.Index)));
        Squarify(queue, new List<(double Area, int Index)>(), x, y, width, height, result);
        return result;
    }

    private static void Squarify(Queue<(double Area, int Index)> rest, List<(double Area, int Index)> row,
        double x, double y, double w, double h, List<TreemapRect> output)
    {
        while (true)
        {
            double side = Math.Min(w, h);
            if (rest.Count == 0)
            {
                if (row.Count > 0) LayoutRow(row, x, y, w, h, output);
                return;
            }
            var next = rest.Peek();
            if (row.Count == 0 || Worst(row, side) >= Worst(row.Append(next), side))
            {
                row.Add(rest.Dequeue());
                continue;
            }
            (x, y, w, h) = LayoutRow(row, x, y, w, h, output);
            row = new List<(double, int)>();
        }
    }

    private static double Worst(IEnumerable<(double Area, int Index)> row, double side)
    {
        double s = 0, max = double.MinValue, min = double.MaxValue;
        foreach (var (a, _) in row) { s += a; max = Math.Max(max, a); min = Math.Min(min, a); }
        if (s <= 0 || side <= 0) return double.MaxValue;
        double s2 = s * s, side2 = side * side;
        return Math.Max(side2 * max / s2, s2 / (side2 * min));
    }

    /// <summary>Places a row along the shorter side; returns the remaining free rectangle.</summary>
    private static (double, double, double, double) LayoutRow(List<(double Area, int Index)> row, double x, double y, double w, double h, List<TreemapRect> output)
    {
        double sum = row.Sum(r => r.Area);
        if (w >= h)
        {
            double colWidth = h > 0 ? sum / h : 0;
            double cy = y;
            foreach (var (a, i) in row)
            {
                double rh = colWidth > 0 ? a / colWidth : 0;
                output.Add(new TreemapRect(i, x, cy, colWidth, rh));
                cy += rh;
            }
            return (x + colWidth, y, Math.Max(0, w - colWidth), h);
        }
        else
        {
            double rowHeight = w > 0 ? sum / w : 0;
            double cx = x;
            foreach (var (a, i) in row)
            {
                double rw = rowHeight > 0 ? a / rowHeight : 0;
                output.Add(new TreemapRect(i, cx, y, rw, rowHeight));
                cx += rw;
            }
            return (x, y + rowHeight, w, Math.Max(0, h - rowHeight));
        }
    }
}
