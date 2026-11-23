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
