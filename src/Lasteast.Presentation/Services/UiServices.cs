using Lasteast.Core.Settings;

namespace Lasteast.Presentation.Services;

/// <summary>Implemented by the UI layer (WPF) to swap theme dictionaries and accent brushes.</summary>
public interface IThemeService
{
    void Apply(ThemeMode mode, string accentHex);
}

/// <summary>Implemented by the UI layer for shell integration (all read-only actions).</summary>
public interface IShellService
{
    void OpenFolder(string path);
    /// <summary>Opens Explorer with the file selected.</summary>
    void RevealFile(string path);
    void CopyToClipboard(string text);
    /// <summary>Yes/No confirmation. Must default to "No".</summary>
    bool Confirm(string title, string message);
    string? PickFolder();
    /// <summary>Save dialog; <paramref name="filter"/> uses the Windows "Name|*.ext" syntax.</summary>
    string? PickSaveFile(string defaultName, string filter) => null;
    string? PickOpenFile(string filter) => null;
}

public sealed class NullThemeService : IThemeService
{
    public void Apply(ThemeMode mode, string accentHex) { }
}

public sealed class NullShellService : IShellService
{
    public bool ConfirmAnswer { get; set; }
    public List<string> Confirmations { get; } = new();
    public void OpenFolder(string path) { }
    public void RevealFile(string path) { }
    public void CopyToClipboard(string text) { }
    public bool Confirm(string title, string message) { Confirmations.Add(message); return ConfirmAnswer; }
    public string? PickFolder() => null;
    public string? SaveFileAnswer { get; set; }
    public string? PickSaveFile(string defaultName, string filter) => SaveFileAnswer;
    public string? PickOpenFile(string filter) => SaveFileAnswer;
}
