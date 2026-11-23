using Lasteast.Core.Models;

namespace Lasteast.Core.Abstractions;

/// <summary>
/// Read-only storage detection layer. Implementations must never write to any disk.
/// </summary>
public interface IStorageInventoryProvider
{
    Task<StorageInventory> GetInventoryAsync(CancellationToken cancellationToken = default);
}

public interface IPrivilegeService
{
    bool IsElevated { get; }
    /// <summary>Relaunches the application elevated (UAC prompt). Returns false if the user declined.</summary>
    bool TryRestartElevated(string[] args);
}

public interface IAppLog
{
    void Info(string message);
    void Warn(string message);
    void Error(string message, Exception? ex = null);
}

/// <summary>Read-only file-system checks (chkdsk without /f): nothing is changed.</summary>
public interface IFileSystemTools
{
    /// <summary>Scans the volume and returns the tool's report.</summary>
    Task<string> CheckAsync(PartitionInfo partition, CancellationToken ct = default);
}

public sealed class NullFileSystemTools : IFileSystemTools
{
    public Task<string> CheckAsync(PartitionInfo partition, CancellationToken ct = default) => Task.FromResult("");
}
