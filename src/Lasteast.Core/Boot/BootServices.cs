namespace Lasteast.Core.Boot;

/// <summary>Read-mostly boot tools of the running Windows (bcdedit, the EFI system partition).</summary>
public interface IBootTools
{
    Task<IReadOnlyList<BcdEntry>> EnumerateBcdAsync();
    /// <summary>Exports the BCD store into <paramref name="folder"/>; returns the file path.</summary>
    Task<string> ExportBcdAsync(string folder);
    /// <summary>Files on the EFI system partition of the running Windows (relative paths, sizes).</summary>
    Task<IReadOnlyList<(string Path, long Size)>> ListEspAsync();
    /// <summary>Copies the EFI system partition's files into <paramref name="folder"/>; returns the number of files.</summary>
    Task<int> BackupEspAsync(string folder);
}

public sealed record AdkStatus(bool AdkInstalled, bool WinPeInstalled, string? KitsRoot, string? RescuePayload);

/// <summary>Builds the Lasteast Rescue Boot ISO from the user's own Windows ADK + WinPE add-on (licensed by Microsoft to them).</summary>
public interface IRescueMediaBuilder
{
    AdkStatus Detect();
    Task<string> BuildIsoAsync(string isoPath, IProgress<string> log, CancellationToken ct);
}

