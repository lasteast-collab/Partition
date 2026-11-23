using Lasteast.Core.Models;

namespace Lasteast.Core.Layout;

public enum SegmentKind
{
    Partition,
    Unallocated,
}

/// <summary>One contiguous region of a disk as drawn on the graphical disk map.</summary>
public sealed record DiskSegment(SegmentKind Kind, ulong OffsetBytes, ulong SizeBytes, PartitionInfo? Partition)
{
    public ulong EndBytes => OffsetBytes + SizeBytes;
}

/// <summary>
/// Pure functions that turn a disk's partition table into an ordered list of map segments
/// and compute on-screen widths. No UI or OS dependencies — unit tested.
/// </summary>
public static class DiskMapLayout
{
    /// <summary>
    /// Gaps up to this size are alignment/EBR slack (Windows leaves exactly 1 MiB before each MBR logical
    /// partition), not useful unallocated space. Only strictly larger gaps are shown.
    /// </summary>
    public const ulong DefaultMinimumGapBytes = 1024 * 1024; // 1 MiB

    /// <summary>
    /// Builds the ordered segment list. MBR extended containers are omitted (their logical
    /// partitions are shown instead); free space inside them is reported as unallocated.
    /// Overlapping entries (corrupt tables) are kept in offset order and reported via <paramref name="problems"/>.
    /// </summary>
    public static IReadOnlyList<DiskSegment> BuildSegments(DiskInfo disk, out IReadOnlyList<string> problems,
        ulong minimumGapBytes = DefaultMinimumGapBytes)
    {
        var issues = new List<string>();
        var segments = new List<DiskSegment>();

        var parts = disk.Partitions
            .Where(p => p.Role != PartitionRole.MbrExtended && p.SizeBytes > 0)
            .OrderBy(p => p.OffsetBytes)
            .ToList();

        ulong start = disk.Style == PartitionStyle.Gpt && disk.UsableStartBytes > 0 ? disk.UsableStartBytes : 0;
        ulong end = disk.Style == PartitionStyle.Gpt && disk.UsableEndBytes > start ? disk.UsableEndBytes : disk.SizeBytes;

        ulong cursor = start;
        foreach (var p in parts)
        {
            if (p.OffsetBytes > cursor)
            {
                var gap = p.OffsetBytes - cursor;
                if (gap > minimumGapBytes && !IsLeadingSlack(cursor, gap, start))
                    segments.Add(new DiskSegment(SegmentKind.Unallocated, cursor, gap, null));
            }
            else if (p.OffsetBytes < cursor)
            {
                issues.Add($"Partition {p.PartitionNumber} overlaps the previous partition by {cursor - p.OffsetBytes} bytes.");
            }

            segments.Add(new DiskSegment(SegmentKind.Partition, p.OffsetBytes, p.SizeBytes, p));
            if (p.EndBytes > end)
                issues.Add($"Partition {p.PartitionNumber} extends beyond the usable end of the disk.");
            cursor = Math.Max(cursor, p.EndBytes);
        }

        if (end > cursor && end - cursor > minimumGapBytes)
            segments.Add(new DiskSegment(SegmentKind.Unallocated, cursor, end - cursor, null));

        problems = issues;
        return segments;
    }

    /// <summary>
    /// On MBR disks the region before the first partition (traditionally 63 sectors or 1 MiB) is not usable space.
    /// </summary>
    private static bool IsLeadingSlack(ulong cursor, ulong gap, ulong start)
        => cursor == start && start == 0 && gap <= 2 * 1024 * 1024;

    /// <summary>
    /// Distributes <paramref name="totalWidth"/> across segments proportionally to their sizes,
    /// guaranteeing each segment at least <paramref name="minWidth"/> so tiny partitions stay clickable.
    /// If the minimums alone exceed the width, space is split equally.
    /// </summary>
    public static double[] ComputeWidths(IReadOnlyList<ulong> sizes, double totalWidth, double minWidth)
    {
        int n = sizes.Count;
        var widths = new double[n];
        if (n == 0 || totalWidth <= 0) return widths;

        if (minWidth * n >= totalWidth)
        {
            Array.Fill(widths, totalWidth / n);
            return widths;
        }

        var pinned = new bool[n];
        while (true)
        {
            double freeWidth = totalWidth;
            double freeWeight = 0;
            for (int i = 0; i < n; i++)
            {
                if (pinned[i]) freeWidth -= minWidth;
                else freeWeight += sizes[i];
            }

            bool changed = false;
            for (int i = 0; i < n; i++)
            {
                if (pinned[i]) { widths[i] = minWidth; continue; }
                widths[i] = freeWeight > 0 ? freeWidth * sizes[i] / freeWeight : freeWidth / CountUnpinned(pinned);
                if (widths[i] < minWidth)
                {
                    pinned[i] = true;
                    changed = true;
                }
            }
            if (!changed) break;
        }
        return widths;
    }

    private static int CountUnpinned(bool[] pinned) => Math.Max(1, pinned.Count(p => !p));
}
