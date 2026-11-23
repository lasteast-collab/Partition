using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lasteast.Core.Settings;

public enum ThemeMode { System, Dark, Light }

public sealed class AppSettings
{
    public string Language { get; set; } = "en";
    public ThemeMode Theme { get; set; } = ThemeMode.Dark;
    /// <summary>Accent colour as #RRGGBB.</summary>
    public string AccentColor { get; set; } = "#1E88E5";
    public bool MaskSerialNumbers { get; set; } = true;
    public bool AutoRefreshOnDeviceChange { get; set; } = true;
    /// <summary>
    /// Off by default: operations may only be applied to virtual disks (VHD/VHDX) until the user
    /// explicitly opts in, per the "test on virtual disks before physical disks" requirement.
    /// </summary>
    public bool AllowPhysicalDiskOperations { get; set; }
    /// <summary>Folders the analyzer and cleanup never enter or touch.</summary>
    public List<string> Exclusions { get; set; } = new();
    /// <summary>Cleanup moves files to the Recycle Bin unless the user switches this off.</summary>
    public bool CleanupToRecycleBin { get; set; } = true;

    /// <summary>Background checks of free space and drive health while the app is open.</summary>
    public bool MonitorEnabled { get; set; } = true;
    public int MonitorIntervalMinutes { get; set; } = 30;
    /// <summary>Alert when a volume has less free space than this percentage.</summary>
    public int LowSpacePercent { get; set; } = 10;
    /// <summary>Alert above this temperature (°C); 0 = defaults (SSD 70, HDD 55).</summary>
    public int TemperatureAlertCelsius { get; set; }

    /// <summary>Optional AI assistant (off by default). The API key is read from the LASTEAST_LLM_KEY environment variable, never stored here.</summary>
    public bool AssistantAiEnabled { get; set; }
    public string AssistantAiEndpoint { get; set; } = "https://api.openai.com/v1/chat/completions";
    public string AssistantAiModel { get; set; } = "gpt-4o-mini";

    /// <summary>Technician details printed on reports (Technician edition).</summary>
    public string? TechnicianName { get; set; }
}

/// <summary>
/// JSON settings store under %LOCALAPPDATA%\Lasteast. A corrupt file never prevents startup:
/// it is renamed aside and defaults are used.
/// SQLite is reserved for the operation history/log database introduced with the operation queue (Phase 2).
/// </summary>
public sealed class JsonSettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;

    public JsonSettingsStore(string path) => _path = path;

    public static string DefaultPath => Lasteast.Core.AppPaths.Settings;

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(_path)) return new AppSettings();
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), Options) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            try { File.Move(_path, _path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss"), true); } catch { /* best effort */ }
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Options));
        File.Move(tmp, _path, overwrite: true); // atomic replace
    }
}
