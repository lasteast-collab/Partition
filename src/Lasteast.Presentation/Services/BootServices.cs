using Lasteast.Core.Boot;

namespace Lasteast.Presentation.Services;

public sealed class NullBootTools : IBootTools
{
    public Task<IReadOnlyList<BcdEntry>> EnumerateBcdAsync() => Task.FromResult<IReadOnlyList<BcdEntry>>(Array.Empty<BcdEntry>());
    public Task<string> ExportBcdAsync(string folder) => throw new InvalidOperationException("not available");
    public Task<IReadOnlyList<(string Path, long Size)>> ListEspAsync() => Task.FromResult<IReadOnlyList<(string, long)>>(Array.Empty<(string, long)>());
    public Task<int> BackupEspAsync(string folder) => throw new InvalidOperationException("not available");
}

public sealed class NullRescueMediaBuilder : IRescueMediaBuilder
{
    public AdkStatus Detect() => new(false, false, null, null);
    public Task<string> BuildIsoAsync(string isoPath, IProgress<string> log, CancellationToken ct) => throw new InvalidOperationException("not available");
}
