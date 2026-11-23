using System.Collections.ObjectModel;
using System.Globalization;
using Lasteast.Core.Insights;
using Lasteast.Core.Localization;
using Lasteast.Core.Models;
using Lasteast.Core.Operations;
using Lasteast.Core.Settings;
using Lasteast.Presentation.Mvvm;
using Lasteast.Presentation.Services;
using static Lasteast.Presentation.ViewModels.Text;

namespace Lasteast.Presentation.ViewModels;

public abstract class PageViewModel : ObservableObject
{
    public abstract string TitleKey { get; }
    public string Title => L(TitleKey);
    /// <summary>Called whenever the inventory or language changes.</summary>
    public virtual void Refresh(AppState state) => OnPropertyChanged(nameof(Title));
}

/// <summary>Shared state handed to pages on refresh.</summary>
public sealed record AppState(StorageInventory? Inventory, IReadOnlyList<DiskViewModel> Disks, AppSettings Settings);

public sealed record FindingViewModel(string Message, string SeverityText, string SeverityKind);

public sealed record StatTile(string Label, string Value, string? Caption = null);

// ---------------------------------------------------------------------------------------------

public sealed class DashboardViewModel : PageViewModel
{
    private SegmentViewModel? _selected;
    private AppSettings _settings = new();
    private readonly OperationQueue _queue;
    private OperationEditorViewModel? _editor;

    public override string TitleKey => "Nav.Overview";

    public ObservableCollection<DiskViewModel> Disks { get; } = new();
    public ObservableCollection<StatTile> Stats { get; } = new();
    public ObservableCollection<FindingViewModel> Findings { get; } = new();
    public ObservableCollection<PropertyRow> SelectedDetails { get; } = new();
    public RelayCommand SelectSegmentCommand { get; }
    public ObservableCollection<ActionItem> Actions { get; } = new();

    public DashboardViewModel(OperationQueue? queue = null)
    {
        _queue = queue ?? new OperationQueue();
        SelectSegmentCommand = new RelayCommand(o => Select(o as SegmentViewModel));
    }

    /// <summary>The open operation editor for the selected segment, if any.</summary>
    public OperationEditorViewModel? Editor
    {
        get => _editor;
        private set { Set(ref _editor, value); OnPropertyChanged(nameof(HasEditor)); }
    }

    public bool HasEditor => _editor is not null;

    public void OpenEditor(EditorMode mode)
    {
        if (_selected is null) return;
        Editor = new OperationEditorViewModel(mode, _selected, _queue, () => Editor = null);
    }

    private void BuildActions()
    {
        Actions.Clear();
        Editor = null;
        if (_selected is null) return;
        void A(EditorMode m, string glyph, bool destructive = false) =>
            Actions.Add(new ActionItem(L("Action." + m), glyph, destructive, new RelayCommand(() => OpenEditor(m))));
        var style = _selected.Disk.Disk.Style;
        void Convert()
        {
            if (style == PartitionStyle.Mbr) A(EditorMode.ConvertToGpt, "\uE8AB");
            else if (style == PartitionStyle.Gpt) A(EditorMode.ConvertToMbr, "\uE8AB");
        }
        if (LayoutSimulator.IsUninitialized(_selected.Disk.Disk))
        {
            A(EditorMode.InitGpt, "\uE8F4");
            A(EditorMode.InitMbr, "\uE8F4");
            return;
        }
        if (_selected.IsUnallocated)
        {
            A(EditorMode.Create, "\uE710");
            Convert();
            return;
        }
        A(EditorMode.Resize, "\uE740");
        A(EditorMode.Move, "\uE7C2");
        A(EditorMode.Label, "\uE8AC");
        A(EditorMode.Letter, "\uE8B7");
        var part = _selected.Partition!;
        A(part.IsHidden ? EditorMode.Unhide : EditorMode.Hide, part.IsHidden ? "\uE7B3" : "\uED1A");
        if (style == PartitionStyle.Mbr && !part.MbrActive) A(EditorMode.SetActive, "\uE7E8");
        A(EditorMode.SetType, "\uE8EC");
        Convert();
        A(EditorMode.Format, "\uE7A8", destructive: true);
        A(EditorMode.Delete, "\uE74D", destructive: true);
    }

    public SegmentViewModel? SelectedSegment
    {
        get => _selected;
        private set
        {
            if (_selected == value) return;
            if (_selected is not null) _selected.IsSelected = false;
            _selected = value;
            if (_selected is not null) _selected.IsSelected = true;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasSelection));
            SelectedDetails.Clear();
            if (_selected is not null)
                foreach (var r in _selected.BuildDetails(_settings.MaskSerialNumbers)) SelectedDetails.Add(r);
            BuildActions();
        }
    }

    public bool HasSelection => _selected is not null;
    public bool HasFindings => Findings.Count > 0;
    public bool IsEmpty => Disks.Count == 0;

    public void Select(SegmentViewModel? segment) => SelectedSegment = segment;

    public override void Refresh(AppState state)
    {
        base.Refresh(state);
        _settings = state.Settings;
        // Preserve the user's selection across refreshes when the same region still exists.
        var prev = _selected is { } s ? (s.Disk.Number, s.Segment.OffsetBytes) : ((int, ulong)?)null;

        _selected = null;
        Disks.Clear();
        foreach (var d in state.Disks) Disks.Add(d);

        Stats.Clear();
        Findings.Clear();
        var inv = state.Inventory;
        if (inv is not null)
        {
            ulong total = 0, used = 0, free = 0;
            foreach (var d in state.Disks) { total += d.Disk.SizeBytes; used += d.UsedBytes; free += d.FreeBytes; }
            var ssd = inv.Disks.Count(d => d.Media == MediaKind.Ssd);
            var hdd = inv.Disks.Count(d => d.Media == MediaKind.Hdd);
            var ext = inv.Disks.Count(d => d.IsExternal);

            Stats.Add(new(L("Stat.Disks"), inv.Disks.Count.ToString(CultureInfo.CurrentCulture),
                Loc.Instance.Format("Stat.DisksCaption", ssd, hdd, ext)));
            Stats.Add(new(L("Stat.Capacity"), Size(total)));
            Stats.Add(new(L("Stat.Used"), Size(used), total > 0 ? Percent((double)used / total) : null));
            Stats.Add(new(L("Stat.Free"), Size(free)));
            var win = inv.Disks.FirstOrDefault(d => d.IsWindowsDisk);
            Stats.Add(new(L("Stat.WindowsDisk"), win is null ? L("Common.NotDetected") : $"{L("Common.Disk")} {win.Number}",
                Firmware(inv.Firmware)));

            foreach (var f in StorageInsights.Analyze(inv))
                Findings.Add(new FindingViewModel(Finding(f), Severity(f.Severity), f.Severity.ToString()));
        }

        if (prev is { } p)
        {
            var match = Disks.FirstOrDefault(d => d.Number == p.Item1)?.Segments.FirstOrDefault(x => x.Segment.OffsetBytes == p.Item2);
            SelectedSegment = match;
        }
        else
        {
            SelectedSegment = null;
        }
        OnPropertyChanged(nameof(HasFindings));
        OnPropertyChanged(nameof(IsEmpty));
    }
}

// ---------------------------------------------------------------------------------------------

public sealed class DisksPageViewModel : PageViewModel
{
    private DiskViewModel? _selected;
    private AppSettings _settings = new();

    public override string TitleKey => "Nav.AllDisks";
    public ObservableCollection<DiskViewModel> Disks { get; } = new();
    public ObservableCollection<PropertyRow> Properties { get; } = new();

    public DiskViewModel? SelectedDisk
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value)) return;
            Properties.Clear();
            if (value is not null) foreach (var r in value.BuildProperties(_settings.MaskSerialNumbers)) Properties.Add(r);
        }
    }

    public override void Refresh(AppState state)
    {
        base.Refresh(state);
        _settings = state.Settings;
        var prev = _selected?.Number;
        Disks.Clear();
        foreach (var d in state.Disks) Disks.Add(d);
        _selected = null;
        SelectedDisk = Disks.FirstOrDefault(d => d.Number == prev) ?? Disks.FirstOrDefault();
    }
}

// ---------------------------------------------------------------------------------------------

public sealed record PartitionRow(
    string Disk, string Partition, string Label, string FileSystem, string Capacity, string Used, string Free,
    string UsedPercent, double UsedFraction, string Type, string Style, string Offset, string Alignment,
    string Flags, string ColorKey, bool IsUnallocated, ulong SortCapacity, ulong SortOffset, int SortDisk);

public sealed class PartitionsPageViewModel : PageViewModel
{
    public override string TitleKey => "Nav.AllPartitions";
    public ObservableCollection<PartitionRow> Rows { get; } = new();

    public override void Refresh(AppState state)
    {
        base.Refresh(state);
        Rows.Clear();
        foreach (var d in state.Disks)
        foreach (var s in d.Segments)
        {
            var p = s.Partition;
            var v = p?.Volume;
            var flags = new List<string>();
            if (v?.IsWindowsVolume == true) flags.Add(L("Badge.Windows"));
            if (v?.IsSystemPartitionVolume == true) flags.Add(L("Badge.Boot"));
            if (p?.IsHidden == true) flags.Add(L("Badge.Hidden"));
            if (p?.MbrActive == true) flags.Add(L("Badge.Active"));

            Rows.Add(new PartitionRow(
                Disk: $"{L("Common.Disk")} {d.Number}",
                Partition: p is null ? "—" : s.Title,
                Label: s.Subtitle,
                FileSystem: s.FileSystemText,
                Capacity: s.SizeText,
                Used: v?.UsedBytes is { } u ? Size(u) : "—",
                Free: v?.FreeBytes is { } f ? Size(f) : "—",
                UsedPercent: v?.UsedBytes is not null ? Percent(s.UsedFraction) : "",
                UsedFraction: s.UsedFraction,
                Type: p is null ? L("Seg.Unallocated") : Role(p.Role),
                Style: Style(d.Disk.Style),
                Offset: Size(s.Segment.OffsetBytes),
                Alignment: p is null ? "" : Alignment(p.Alignment),
                Flags: string.Join(" · ", flags),
                ColorKey: s.ColorKey,
                IsUnallocated: p is null,
                SortCapacity: s.SizeBytes,
                SortOffset: s.Segment.OffsetBytes,
                SortDisk: d.Number));
        }
    }
}

// ---------------------------------------------------------------------------------------------

/// <summary>Honest placeholder for modules scheduled in later phases. Nothing here pretends to work.</summary>
public sealed class PlannedModuleViewModel : PageViewModel
{
    public PlannedModuleViewModel(string titleKey, int phase, string descriptionKey, string glyph)
    {
        TitleKey = titleKey;
        Phase = phase;
        DescriptionKey = descriptionKey;
        Glyph = glyph;
    }

    public override string TitleKey { get; }
    public int Phase { get; }
    public string DescriptionKey { get; }
    public string Glyph { get; }
    public string Description => L(DescriptionKey);
    public string PhaseText => Loc.Instance.Format("Planned.Phase", Phase);

    public override void Refresh(AppState state)
    {
        base.Refresh(state);
        OnPropertyChanged(nameof(Description));
        OnPropertyChanged(nameof(PhaseText));
    }
}

// ---------------------------------------------------------------------------------------------

public sealed record AccentOption(string NameKey, string Hex)
{
    public string Name => L(NameKey);
    public override string ToString() => Name;
}

public sealed record ThemeOption(ThemeMode Mode, string NameKey)
{
    public string Name => L(NameKey);
    public override string ToString() => Name;
}

public sealed class SettingsPageViewModel : PageViewModel
{
    private readonly JsonSettingsStore _store;
    private readonly IThemeService _theme;
    private readonly IShellService _shell;
    private readonly Action _onLanguageChanged;
    private bool _loading;

    public SettingsPageViewModel(AppSettings settings, JsonSettingsStore store, IThemeService theme, IShellService shell,
        string logDirectory, Action onLanguageChanged)
    {
        Settings = settings;
        _store = store;
        _theme = theme;
        _shell = shell;
        LogDirectory = logDirectory;
        _onLanguageChanged = onLanguageChanged;
        OpenLogFolderCommand = new RelayCommand(() => _shell.OpenFolder(LogDirectory));
    }

    public override string TitleKey => "Nav.Settings";
    public AppSettings Settings { get; }
    public string LogDirectory { get; }
    public RelayCommand OpenLogFolderCommand { get; }
    public string Version => typeof(SettingsPageViewModel).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";

    public IReadOnlyList<LanguageInfo> Languages => Loc.Languages;
    public static IReadOnlyList<ThemeOption> ThemeOptions { get; } = new[]
    {
        new ThemeOption(ThemeMode.Dark, "Theme.Dark"),
        new ThemeOption(ThemeMode.Light, "Theme.Light"),
        new ThemeOption(ThemeMode.System, "Theme.System"),
    };

    public static IReadOnlyList<AccentOption> AccentOptions { get; } = new[]
    {
        new AccentOption("Accent.Blue", "#1E88E5"),
        new AccentOption("Accent.Green", "#2DB84C"),
        new AccentOption("Accent.Orange", "#F57C00"),
        new AccentOption("Accent.Teal", "#00897B"),
        new AccentOption("Accent.Violet", "#7E57C2"),
        new AccentOption("Accent.Silver", "#90A4AE"),
    };

    // Instance wrappers: WPF bindings resolve instance properties only.
    public IReadOnlyList<ThemeOption> Themes => ThemeOptions;
    public IReadOnlyList<AccentOption> Accents => AccentOptions;

    public LanguageInfo SelectedLanguage
    {
        get => Loc.Languages.FirstOrDefault(l => l.Code == Settings.Language) ?? Loc.Languages[0];
        set
        {
            if (value is null || value.Code == Settings.Language) return;
            Settings.Language = value.Code;
            Loc.Instance.SetLanguage(value.Code);
            Save();
            OnPropertyChanged();
            _onLanguageChanged();
        }
    }

    public ThemeOption SelectedTheme
    {
        get => ThemeOptions.First(t => t.Mode == Settings.Theme);
        set
        {
            if (value is null || Settings.Theme == value.Mode) return;
            Settings.Theme = value.Mode;
            ApplyTheme();
            OnPropertyChanged();
        }
    }

    public AccentOption SelectedAccent
    {
        get => AccentOptions.FirstOrDefault(a => string.Equals(a.Hex, Settings.AccentColor, StringComparison.OrdinalIgnoreCase)) ?? AccentOptions[0];
        set
        {
            if (value is null) return;
            Settings.AccentColor = value.Hex;
            ApplyTheme();
            OnPropertyChanged();
        }
    }

    public bool MaskSerialNumbers
    {
        get => Settings.MaskSerialNumbers;
        set
        {
            if (Settings.MaskSerialNumbers == value) return;
            Settings.MaskSerialNumbers = value;
            Save();
            OnPropertyChanged();
            _onLanguageChanged(); // rebuild views so serials re-render
        }
    }

    /// <summary>Opt-in for applying operations to physical disks (default: virtual disks only).</summary>
    public bool AllowPhysicalDisks
    {
        get => Settings.AllowPhysicalDiskOperations;
        set
        {
            if (Settings.AllowPhysicalDiskOperations == value) return;
            Settings.AllowPhysicalDiskOperations = value;
            Save();
            OnPropertyChanged();
            _onLanguageChanged(); // re-evaluate Apply availability
        }
    }

    public bool MonitorEnabled
    {
        get => Settings.MonitorEnabled;
        set { if (Settings.MonitorEnabled == value) return; Settings.MonitorEnabled = value; Save(); OnPropertyChanged(); MonitorChanged?.Invoke(); }
    }

    public IReadOnlyList<int> MonitorIntervals { get; } = new[] { 10, 30, 60, 240 };
    public int MonitorIntervalMinutes
    {
        get => Settings.MonitorIntervalMinutes;
        set { if (Settings.MonitorIntervalMinutes == value) return; Settings.MonitorIntervalMinutes = value; Save(); OnPropertyChanged(); MonitorChanged?.Invoke(); }
    }

    public IReadOnlyList<int> LowSpaceOptions { get; } = new[] { 5, 10, 15, 20 };
    public int LowSpacePercent
    {
        get => Settings.LowSpacePercent;
        set { if (Settings.LowSpacePercent == value) return; Settings.LowSpacePercent = value; Save(); OnPropertyChanged(); }
    }

    // ---- license, portable mode, assistant
    public RelayCommand? LoadLicenseCommand { get; set; }
    public string LicenseText { get; private set; } = "";
    public void SetLicense(Core.Licensing.LicenseStatus status, Core.Licensing.License? l)
    {
        var edition = Core.Licensing.Licenses.Effective(status, l);
        LicenseText = Loc.Instance.Format("License.Status", L("License.Edition." + edition), L("License.State." + status))
                      + (l is not null ? $" · {l.Licensee}" + (l.Expires is { } e ? $" · {Loc.Instance.Format("License.Expires", e.LocalDateTime.ToString("d"))}" : "") : "");
        OnPropertyChanged(nameof(LicenseText));
        OnPropertyChanged(nameof(LoadLicenseCommand));
    }
    public string PortableText => Core.AppPaths.IsPortable ? Loc.Instance.Format("Settings.PortableOn", Core.AppPaths.Root) : Loc.Instance.Format("Settings.PortableOff", Core.AppPaths.Root);

    public bool AssistantAiEnabled
    {
        get => Settings.AssistantAiEnabled;
        set { if (Settings.AssistantAiEnabled == value) return; Settings.AssistantAiEnabled = value; Save(); OnPropertyChanged(); }
    }
    public string AssistantAiEndpoint
    {
        get => Settings.AssistantAiEndpoint;
        set { if (Settings.AssistantAiEndpoint == value) return; Settings.AssistantAiEndpoint = value; Save(); OnPropertyChanged(); }
    }
    public string AssistantAiModel
    {
        get => Settings.AssistantAiModel;
        set { if (Settings.AssistantAiModel == value) return; Settings.AssistantAiModel = value; Save(); OnPropertyChanged(); }
    }
    public bool AiKeyPresent => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("LASTEAST_LLM_KEY"));

    /// <summary>Raised when monitoring settings change, so the monitor restarts with them.</summary>
    public event Action? MonitorChanged;

    public string BackupFolder => Loc.Instance.Format("Settings.BackupFolder",
        Core.AppPaths.Backups);

    public bool AutoRefresh
    {
        get => Settings.AutoRefreshOnDeviceChange;
        set
        {
            if (Settings.AutoRefreshOnDeviceChange == value) return;
            Settings.AutoRefreshOnDeviceChange = value;
            Save();
            OnPropertyChanged();
        }
    }

    public void ApplyTheme()
    {
        _theme.Apply(Settings.Theme, Settings.AccentColor);
        Save();
        if (!_loading) _onLanguageChanged(); // rebuild items whose brushes are resolved from data keys
    }

    private void Save()
    {
        if (_loading) return;
        try { _store.Save(Settings); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    public override void Refresh(AppState state)
    {
        _loading = true;
        base.Refresh(state);
        OnPropertyChanged(nameof(SelectedLanguage));
        OnPropertyChanged(nameof(SelectedAccent));
        OnPropertyChanged(nameof(BackupFolder));
        _loading = false;
    }
}
