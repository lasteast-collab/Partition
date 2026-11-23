using System.Runtime.Versioning;
using Lasteast.Core.Abstractions;
using Lasteast.Core.Models;

namespace Lasteast.Storage.Windows.Operations;

/// <summary>Read-only chkdsk: NTFS volumes are scanned online (/scan, no lock, no changes); others without /f.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsFileSystemTools : IFileSystemTools
{
    public Task<string> CheckAsync(PartitionInfo p, CancellationToken ct = default) => Task.Run(() =>
    {
        string target = p.Volume?.DriveLetter is { } l ? $"{l}:" : p.Volume?.VolumeGuidPath?.TrimEnd('\\') ?? throw new InvalidOperationException("no volume");
        bool ntfs = string.Equals(p.Volume?.FileSystem, "NTFS", StringComparison.OrdinalIgnoreCase);
        var (exit, output) = Tool.Run("chkdsk.exe", ntfs ? $"{target} /scan" : target, timeoutMs: 6 * 3600 * 1000);
        return $"chkdsk {target}{(ntfs ? " /scan" : "")} → {exit}\n{output}";
    }, ct);
}
