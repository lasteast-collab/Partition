namespace Lasteast.Core;

/// <summary>
/// Where Lasteast keeps its own files (settings, logs, history, partition-table backups, move journals).
/// Normally %LOCALAPPDATA%\Lasteast\Partition. In portable mode — a file named "portable" next to the program —
/// everything lives in a "Data" folder beside the program instead, so it can run from a USB drive without traces.
/// </summary>
public static class AppPaths
{
    public const string PortableMarker = "portable";
    private static string? _root;

    public static bool IsPortable => File.Exists(Path.Combine(AppContext.BaseDirectory, PortableMarker));

    public static string Root
    {
        get => _root ??= IsPortable
            ? Path.Combine(AppContext.BaseDirectory, "Data")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lasteast", "Partition");
        set => _root = value; // tests
    }

    public static string Settings => Path.Combine(Root, "settings.json");
    public static string Logs => Path.Combine(Root, "logs");
    public static string History => Path.Combine(Root, "history.jsonl");
    public static string Backups => Path.Combine(Root, "backups");
    public static string Journal => Path.Combine(Root, "journal");
    public static string Bcd => Path.Combine(Root, "bcd");
    public static string License => Path.Combine(Root, "license.json");
}
