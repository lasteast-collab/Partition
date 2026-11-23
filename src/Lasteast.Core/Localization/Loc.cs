using System.ComponentModel;
using System.Globalization;
using System.Text.Json;

namespace Lasteast.Core.Localization;

public sealed record LanguageInfo(string Code, string NativeName, string EnglishName, bool IsRightToLeft)
{
    public CultureInfo Culture => CultureInfo.GetCultureInfo(Code);
    public override string ToString() => NativeName;
}

/// <summary>
/// String-table localization. Tables are JSON files embedded in this assembly
/// (Localization/Strings/{code}.json). XAML binds through the indexer
/// (<c>{Binding [Nav.Home], Source={x:Static loc:Loc.Instance}}</c>); switching language raises
/// a change notification for "Item[]" so the whole UI re-renders in place.
/// Missing keys fall back to English, then to the key itself in brackets — never to an empty string.
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    public static IReadOnlyList<LanguageInfo> Languages { get; } = new[]
    {
        new LanguageInfo("en", "English", "English", false),
        new LanguageInfo("he", "עברית", "Hebrew", true),
        new LanguageInfo("ar", "العربية", "Arabic", true),
        new LanguageInfo("fr", "Français", "French", false),
        new LanguageInfo("es", "Español", "Spanish", false),
        new LanguageInfo("de", "Deutsch", "German", false),
        new LanguageInfo("ru", "Русский", "Russian", false),
    };

    // Declared after Languages: static initializers run in textual order and the constructor reads Languages.
    public static Loc Instance { get; } = new();

    private readonly Dictionary<string, IReadOnlyDictionary<string, string>> _tables = new();
    private IReadOnlyDictionary<string, string> _current;
    private readonly IReadOnlyDictionary<string, string> _english;

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? LanguageChanged;

    private Loc()
    {
        _english = LoadTable("en");
        _current = _english;
        Language = Languages[0];
    }

    public LanguageInfo Language { get; private set; }

    public string this[string key] =>
        _current.TryGetValue(key, out var v) ? v :
        _english.TryGetValue(key, out var e) ? e : $"[{key}]";

    public string Format(string key, params object?[] args) => string.Format(Language.Culture, this[key], args);

    public void SetLanguage(string code)
    {
        var lang = Languages.FirstOrDefault(l => l.Code == code) ?? Languages[0];
        _current = GetTable(lang.Code);
        Language = lang;
        CultureInfo.CurrentUICulture = lang.Culture;
        CultureInfo.DefaultThreadCurrentUICulture = lang.Culture;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    public IReadOnlyDictionary<string, string> GetTable(string code)
    {
        lock (_tables)
        {
            if (!_tables.TryGetValue(code, out var table))
                _tables[code] = table = LoadTable(code);
            return table;
        }
    }

    private static IReadOnlyDictionary<string, string> LoadTable(string code)
    {
        var asm = typeof(Loc).Assembly;
        using var stream = asm.GetManifestResourceStream($"Lasteast.Strings.{code}.json");
        if (stream is null) return new Dictionary<string, string>();
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? new Dictionary<string, string>();
    }
}
