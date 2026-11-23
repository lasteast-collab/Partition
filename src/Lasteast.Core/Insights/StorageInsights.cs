using Lasteast.Core.Layout;
using Lasteast.Core.Models;

namespace Lasteast.Core.Insights;

public enum FindingSeverity { Critical, High, Recommended, Optional, Info }

/// <summary>
/// A fact-based observation about the current storage configuration.
/// <see cref="MessageKey"/> is a localization key; <see cref="Args"/> are format arguments.
/// This is the read-only seed of Smart Fix (Phase 3): findings never trigger actions by themselves.
/// </summary>
public sealed record Finding(FindingSeverity Severity, string MessageKey, object[] Args, int? DiskNumber = null, int? PartitionNumber = null);

public static class StorageInsights
{
    public const double LowSpaceFraction = 0.10;
    public const ulong LowSpaceAbsolute = 10UL * 1024 * 1024 * 1024; // 10 GiB

    public static IReadOnlyList<Finding> Analyze(StorageInventory inv)
    {
        var list = new List<Finding>();

        if (!inv.IsElevated && !inv.IsSampleData)
            list.Add(new Finding(FindingSeverity.Info, "Finding.NotElevated", Array.Empty<object>()));

        foreach (var d in inv.Disks)
        {
            foreach (var p in d.Partitions)
            {
                var v = p.Volume;
                if (v is { CapacityBytes: > 0, FreeBytes: { } free } && v.DriveLetter is { } letter)
                {
                    var cap = v.CapacityBytes.Value;
                    if (free < cap * LowSpaceFraction && free < LowSpaceAbsolute)
                        list.Add(new Finding(v.IsWindowsVolume ? FindingSeverity.High : FindingSeverity.Recommended,
                            "Finding.LowSpace", new object[] { $"{letter}:", free, (double)free / cap }, d.Number, p.PartitionNumber));
                }

                if (p.Alignment == AlignmentStatus.Misaligned && p.Role != PartitionRole.MbrExtended)
                    list.Add(new Finding(d.Media == MediaKind.Ssd ? FindingSeverity.Recommended : FindingSeverity.Optional,
                        "Finding.Misaligned", new object[] { d.Number, p.PartitionNumber }, d.Number, p.PartitionNumber));

                if (v?.AccessError is not null && v.DriveLetter is { } l2)
                    list.Add(new Finding(FindingSeverity.Recommended, "Finding.VolumeUnreadable", new object[] { $"{l2}:" }, d.Number, p.PartitionNumber));
            }

            DiskMapLayout.BuildSegments(d, out var problems);
            if (problems.Count > 0)
                list.Add(new Finding(FindingSeverity.Critical, "Finding.LayoutProblem", new object[] { d.Number, problems.Count }, d.Number));

            var unalloc = DiskMapLayout.BuildSegments(d, out _)
                .Where(s => s.Kind == SegmentKind.Unallocated).Aggregate(0UL, (a, s) => a + s.SizeBytes);
            if (unalloc >= 1024UL * 1024 * 1024)
                list.Add(new Finding(FindingSeverity.Optional, "Finding.Unallocated", new object[] { d.Number, unalloc }, d.Number));

            if (d.Style == PartitionStyle.Mbr && d.SizeBytes > 2UL * 1024 * 1024 * 1024 * 1024)
                list.Add(new Finding(FindingSeverity.Recommended, "Finding.Mbr2TB", new object[] { d.Number }, d.Number));

            if (d.IsDynamic)
                list.Add(new Finding(FindingSeverity.Info, "Finding.Dynamic", new object[] { d.Number }, d.Number));

            if (d.Media == MediaKind.Ssd && d.TrimEnabled == false)
                list.Add(new Finding(FindingSeverity.Recommended, "Finding.TrimOff", new object[] { d.Number }, d.Number));
        }

        return list.OrderBy(f => f.Severity).ToList();
    }
}
