using Lasteast.Core.Models;

namespace Lasteast.Core.Optimization;

public enum OptimizeAction
{
    /// <summary>Nothing suitable (unknown media, removable flash, unreadable volume).</summary>
    None,
    /// <summary>HDD: analyse fragmentation and defragment.</summary>
    Defragment,
    /// <summary>SSD / NVMe: send TRIM for all free space (never defragment).</summary>
    Retrim,
}

public sealed record FragmentationReport(int TotalPercent, int FilePercent, bool DefragRecommended);

/// <summary>Windows' own volume optimisation (the engine behind "Defragment and Optimize Drives").</summary>
public interface IVolumeOptimizer
{
    Task<FragmentationReport?> AnalyzeAsync(char driveLetter, CancellationToken ct = default);
    Task<string> OptimizeAsync(char driveLetter, OptimizeAction action, CancellationToken ct = default);
}

public sealed class NullVolumeOptimizer : IVolumeOptimizer
{
    public Task<FragmentationReport?> AnalyzeAsync(char driveLetter, CancellationToken ct = default) => Task.FromResult<FragmentationReport?>(null);
    public Task<string> OptimizeAsync(char driveLetter, OptimizeAction action, CancellationToken ct = default) => Task.FromResult("");
}

public static class OptimizePlanner
{
    /// <summary>
    /// The right optimisation for a volume's disk: defragment only spinning disks; TRIM for SSDs and NVMe drives;
    /// nothing when the media type is unknown (USB sticks, SD cards, virtual disks report it so) — a wrong guess
    /// would wear flash for no benefit.
    /// </summary>
    public static OptimizeAction For(DiskInfo disk, PartitionInfo partition)
    {
        if (partition.Volume is not { AccessError: null, DriveLetter: not null } v) return OptimizeAction.None;
        if (v.FileSystem is not ("NTFS" or "ReFS")) return OptimizeAction.None;
        if (disk.Bus == BusKind.Nvme || disk.Media == MediaKind.Ssd) return disk.TrimEnabled == false ? OptimizeAction.None : OptimizeAction.Retrim;
        if (disk.Media == MediaKind.Hdd) return OptimizeAction.Defragment;
        return OptimizeAction.None;
    }
}
