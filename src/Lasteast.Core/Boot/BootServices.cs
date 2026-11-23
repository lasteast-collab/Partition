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

    // ---- editing (each call exports the BCD store first and returns the export's path)
    Task<string> SetDefaultAsync(string identifier) => throw new NotSupportedException();
    Task<string> SetTimeoutAsync(int seconds) => throw new NotSupportedException();
    Task<string> SetDisplayOrderAsync(IReadOnlyList<string> identifiers) => throw new NotSupportedException();
    Task<string> SetDescriptionAsync(string identifier, string description) => throw new NotSupportedException();

    /// <summary>Copies a previous ESP backup back onto the ESP (the current ESP is backed up first). Returns the number of files.</summary>
    Task<int> RestoreEspAsync(string backupFolder) => throw new NotSupportedException();
    Task<WinReStatus> WinReAsync() => Task.FromResult(new WinReStatus(null, null));
    /// <summary>reagentc /enable or /disable.</summary>
    Task<string> SetWinReAsync(bool enable) => throw new NotSupportedException();
}

public sealed record WinReStatus(bool? Enabled, string? Location);

/// <summary>What a Windows boot needs on the EFI system partition, checked against its file list.</summary>
public static class EspAnalyzer
{
    public static readonly IReadOnlyList<string> Required = new[] { @"EFI\Microsoft\Boot\bootmgfw.efi", @"EFI\Microsoft\Boot\BCD" };
    public const string Fallback = @"EFI\Boot\bootx64.efi";

    /// <summary>Required files that are missing; the removable-media fallback loader is reported separately (optional).</summary>
    public static (IReadOnlyList<string> Missing, bool HasFallback) Check(IEnumerable<string> relativePaths)
    {
        var set = relativePaths.Select(p => p.Replace('/', '\\').TrimStart('\\')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return (Required.Where(r => !set.Contains(r)).ToList(), set.Contains(Fallback));
    }
}

public sealed record AdkStatus(bool AdkInstalled, bool WinPeInstalled, string? KitsRoot, string? RescuePayload);

/// <summary>Builds the Lasteast Rescue Boot ISO from the user's own Windows ADK + WinPE add-on (licensed by Microsoft to them).</summary>
public interface IRescueMediaBuilder
{
    AdkStatus Detect();
    Task<string> BuildIsoAsync(string isoPath, IProgress<string> log, CancellationToken ct);
}

