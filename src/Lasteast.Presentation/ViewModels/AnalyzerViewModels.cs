using System.Collections.ObjectModel;
using System.Globalization;
using Lasteast.Core.Analysis;
using Lasteast.Core.Cleanup;
using Lasteast.Core.Localization;
using Lasteast.Core.Models;
using Lasteast.Core.Settings;
using Lasteast.Presentation.Mvvm;
using Lasteast.Presentation.Services;
using static Lasteast.Presentation.ViewModels.Text;

namespace Lasteast.Presentation.ViewModels;

public sealed record LocationOption(string Path, string Label)
{
    public override string ToString() => Label;
}

public sealed record FileRow(string Name, string Folder, string Path, string SizeText, ulong Size, string Modified, DateTime ModifiedUtc, string Category, string ColorKey);

public sealed record FolderRow(string Name, string Path, string SizeText, ulong Size, string Files, double Fraction);

public sealed record CategoryRow(string Name, string SizeText, ulong Size, string CountText, double Fraction, string ColorKey);

public sealed record DuplicateRow(string Header, string WastedText, IReadOnlyList<string> Paths);

public sealed class TreemapItem
{
    public required string Name { get; init; }
    public required string SizeText { get; init; }
    public required double X { get; init; }
    public required double Y { get; init; }
    public required double Width { get; init; }
    public required double Height { get; init; }
    public required string ColorKey { get; init; }
    public FolderNode? Folder { get; init; }
    public string? FilePath { get; init; }
    public bool IsFolder => Folder is not null;
    public bool ShowLabel => Width > 70 && Height > 34;
    public string ToolTip => $"{Name}{Environment.NewLine}{SizeText}";
}

/// <summary>
/// Storage Analyzer (module 7): read-only scan of a drive or folder with treemap drill-down,
/// largest/old/recent files, folders, types, empty folders and duplicates. The only action that
/// changes anything is "Move to Recycle Bin" on a single file, after an explicit Yes/No confirmation.
/// </summary>
public sealed class StorageAnalyzerViewModel : PageViewModel
{
    private readonly IShellService _shell;
    private readonly IFileRemover _remover;
    private AppSettings _settings = new();
    private ScanResult? _result;
    private CancellationTokenSource? _cts;
    private LocationOption? _location;
    private bool _scanning;
    private string _progress = "";
    private FolderNode? _current;
    private double _vw = 800, _vh = 420;
    private string _minSizeMb = "";
    private string _extFilter = "";
    private string _tab = "Treemap";
    private bool _findingDupes;
    private string? _message;

    public StorageAnalyzerViewModel(IShellService shell, IFileRemover remover)
    {
        _shell = shell;
        _remover = remover;
        ScanCommand = new AsyncRelayCommand(ScanAsync, ex => { Message = ex.Message; Scanning = false; });
        CancelCommand = new RelayCommand(() => _cts?.Cancel(), () => _scanning || _findingDupes);
        UpCommand = new RelayCommand(() => { if (_current?.Parent is { } p) SetCurrent(p); }, () => _current?.Parent is not null);
        OpenItemCommand = new RelayCommand(o =>
        {
            if (o is TreemapItem { Folder: { } f } && (f.Children.Count > 0 || f.TopFiles.Count > 0)) SetCurrent(f);
            else if (o is TreemapItem { FilePath: { } fp }) _shell.RevealFile(fp);
        });
        NavigateCommand = new RelayCommand(o => { if (o is FolderNode f) SetCurrent(f); });
        RevealCommand = new RelayCommand(o => { if (o is FileRow r) _shell.RevealFile(r.Path); else if (o is string s) _shell.RevealFile(s); else if (o is FolderRow fr) _shell.OpenFolder(fr.Path); });
        RecycleCommand = new RelayCommand(o => { if (o is FileRow r) Recycle(r); });
        FindDuplicatesCommand = new AsyncRelayCommand(FindDuplicatesAsync, ex => { Message = ex.Message; FindingDuplicates = false; });
        BrowseCommand = new RelayCommand(() =>
        {
            if (_shell.PickFolder() is { } p)
            {
                var opt = new LocationOption(p, p);
                Locations.Add(opt);
                SelectedLocation = opt;
            }
        });
        SetTabCommand = new RelayCommand(o => { if (o is string t) Tab = t; });
    }

    public override string TitleKey => "Nav.StorageAnalyzer";
    public ObservableCollection<LocationOption> Locations { get; } = new();
    public ObservableCollection<TreemapItem> TreemapItems { get; } = new();
    public ObservableCollection<FolderNode> Breadcrumb { get; } = new();
    public ObservableCollection<FileRow> Files { get; } = new();
    public ObservableCollection<FolderRow> Folders { get; } = new();
    public ObservableCollection<CategoryRow> Categories { get; } = new();
    public ObservableCollection<CategoryRow> Extensions { get; } = new();
    public ObservableCollection<string> EmptyFolders { get; } = new();
    public ObservableCollection<DuplicateRow> Duplicates { get; } = new();

    public AsyncRelayCommand ScanCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand UpCommand { get; }
    public RelayCommand OpenItemCommand { get; }
    public RelayCommand NavigateCommand { get; }
    public RelayCommand RevealCommand { get; }
    public RelayCommand RecycleCommand { get; }
    public AsyncRelayCommand FindDuplicatesCommand { get; }
    public RelayCommand BrowseCommand { get; }
    public RelayCommand SetTabCommand { get; }

    public LocationOption? SelectedLocation { get => _location; set => Set(ref _location, value); }
    public bool Scanning { get => _scanning; private set { if (Set(ref _scanning, value)) { CancelCommand.RaiseCanExecuteChanged(); OnPropertyChanged(nameof(ShowIntro)); } } }
    public bool FindingDuplicates { get => _findingDupes; private set { if (Set(ref _findingDupes, value)) CancelCommand.RaiseCanExecuteChanged(); } }
    public string ProgressText { get => _progress; private set => Set(ref _progress, value); }
    public bool HasResult => _result is not null;
    public bool ShowIntro => _result is null && !_scanning;
    public string SummaryText { get; private set; } = "";
    public string? Message { get => _message; private set { Set(ref _message, value); OnPropertyChanged(nameof(HasMessage)); } }
    public bool HasMessage => !string.IsNullOrEmpty(_message);
    public bool DuplicatesSearched { get; private set; }

    public string Tab
    {
        get => _tab;
        set { if (Set(ref _tab, value)) RebuildLists(); }
    }

    public string MinSizeMb { get => _minSizeMb; set { if (Set(ref _minSizeMb, value)) RebuildLists(); } }
    public string ExtensionFilter { get => _extFilter; set { if (Set(ref _extFilter, value)) RebuildLists(); } }

    public override void Refresh(AppState state)
    {
        base.Refresh(state);
        _settings = state.Settings;
        var keep = _location?.Path;
        var custom = Locations.Where(l => l.Path.Length > 3).ToList(); // folders added with Browse
        Locations.Clear();
        if (state.Inventory is not null)
            foreach (var v in state.Inventory.Disks.SelectMany(d => d.Partitions).Select(p => p.Volume)
                         .Where(v => v?.DriveLetter is not null && v.AccessError is null).OrderBy(v => v!.DriveLetter))
                Locations.Add(new LocationOption($"{v!.DriveLetter}:\\", $"{v.DriveLetter}:  {v.Label}  ({Size(v.UsedBytes)} / {Size(v.CapacityBytes)})"));
        foreach (var c in custom) Locations.Add(c);
        _location = Locations.FirstOrDefault(l => l.Path == keep) ?? Locations.FirstOrDefault();
        OnPropertyChanged(nameof(SelectedLocation));
        if (_result is not null) { RebuildTreemap(); RebuildLists(); BuildSummary(); }
    }

    /// <summary>Called by the view when the treemap area is resized.</summary>
    public void SetViewport(double width, double height)
    {
        if (width < 10 || height < 10 || (Math.Abs(width - _vw) < 1 && Math.Abs(height - _vh) < 1)) return;
        _vw = width;
        _vh = height;
        RebuildTreemap();
    }

    private async Task ScanAsync()
    {
        if (_location is null || _scanning) return;
        _cts = new CancellationTokenSource();
        Scanning = true;
        Message = null;
        var progress = new Progress<ScanProgress>(p => ProgressText = Loc.Instance.Format("An.Progress",
            Number(p.Files), Number(p.Folders), Size(p.Bytes), p.CurrentFolder));
        _result = await StorageScanner.ScanAsync(_location.Path, new ScanOptions { Exclusions = _settings.Exclusions }, progress, _cts.Token);
        Scanning = false;
        Duplicates.Clear();
        DuplicatesSearched = false;
        OnPropertyChanged(nameof(DuplicatesSearched));
        SetCurrent(_result.Root);
        BuildSummary();
        RebuildLists();
        OnPropertyChanged(nameof(HasResult));
        OnPropertyChanged(nameof(ShowIntro));
    }

    private void BuildSummary()
    {
        if (_result is null) return;
        SummaryText = Loc.Instance.Format(_result.Cancelled ? "An.SummaryPartial" : "An.Summary",
            Size(_result.Root.Size), Number(_result.Files), Number(_result.Folders), _result.Duration.TotalSeconds.ToString("0.0", CultureInfo.CurrentCulture))
            + (_result.InaccessibleFolders > 0 ? "  ·  " + Loc.Instance.Format("An.Inaccessible", Number(_result.InaccessibleFolders)) : "");
        OnPropertyChanged(nameof(SummaryText));
    }

    private void SetCurrent(FolderNode node)
    {
        _current = node;
        Breadcrumb.Clear();
        for (var n = node; n is not null; n = n.Parent) Breadcrumb.Insert(0, n);
        UpCommand.RaiseCanExecuteChanged();
        RebuildTreemap();
    }

    private void RebuildTreemap()
    {
        TreemapItems.Clear();
        if (_current is null) return;
        var entries = new List<(string Name, ulong Size, string Color, FolderNode? Folder, string? File)>();
        foreach (var c in _current.Children) entries.Add((c.Name, c.Size, "Folder", c, null));
        ulong listed = 0;
        foreach (var f in _current.TopFiles) { entries.Add((f.Name, f.Size, f.Category.ToString(), null, f.Path)); listed += f.Size; }
        // bytes of small sub-folders and files not individually listed
        var shown = entries.Aggregate(0UL, (a, e) => a + e.Size);
        if (_current.Size > shown) entries.Add((L("An.Other"), _current.Size - shown, "Other", null, null));

        var rects = Treemap.Layout(entries.Select(e => (double)e.Size).ToList(), 0, 0, _vw, _vh);
        foreach (var r in rects)
        {
            var e = entries[r.Index];
            TreemapItems.Add(new TreemapItem
            {
                Name = e.Name, SizeText = Size(e.Size), X = r.X, Y = r.Y, Width = Math.Max(0, r.Width - 2), Height = Math.Max(0, r.Height - 2),
                ColorKey = e.Color, Folder = e.Folder, FilePath = e.File,
            });
        }
    }

    private void RebuildLists()
    {
        Files.Clear(); Folders.Clear(); Categories.Clear(); Extensions.Clear(); EmptyFolders.Clear();
        if (_result is null) return;
        ulong min = double.TryParse(_minSizeMb, NumberStyles.Float, CultureInfo.CurrentCulture, out var mb) && mb > 0 ? (ulong)(mb * 1024 * 1024) : 0;
        var exts = _extFilter.Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.StartsWith('.') ? x : "." + x).ToHashSet(StringComparer.OrdinalIgnoreCase);

        IEnumerable<FileEntry> source = _tab switch
        {
            "Old" => _result.OldLargeFiles,
            "Recent" => _result.RecentFiles,
            _ => _result.LargestFiles,
        };
        foreach (var f in source.Where(f => f.Size >= min && (exts.Count == 0 || exts.Contains(f.Extension))).Take(500))
            Files.Add(new FileRow(f.Name, f.Folder, f.Path, Size(f.Size), f.Size, f.LastWriteUtc.ToLocalTime().ToString("d", CultureInfo.CurrentCulture),
                f.LastWriteUtc, L("Cat." + f.Category), f.Category.ToString()));

        var total = Math.Max(1UL, _result.Root.Size);
        foreach (var d in _result.LargestFolders(200))
            Folders.Add(new FolderRow(d.Name, d.Path, Size(d.Size), d.Size, Number(d.FileCount), (double)d.Size / total));
        foreach (var (cat, v) in _result.ByCategory.OrderByDescending(kv => kv.Value.Bytes))
            Categories.Add(new CategoryRow(L("Cat." + cat), Size(v.Bytes), v.Bytes, Number(v.Count), (double)v.Bytes / total, cat.ToString()));
        foreach (var (ext, v) in _result.ByExtension.OrderByDescending(kv => kv.Value.Bytes).Take(25))
            Extensions.Add(new CategoryRow(ext, Size(v.Bytes), v.Bytes, Number(v.Count), (double)v.Bytes / total, FileCategories.Classify("x" + ext).ToString()));
        foreach (var e in _result.EmptyFolders.Take(2000)) EmptyFolders.Add(e);
    }

    private async Task FindDuplicatesAsync()
    {
        if (_result is null) return;
        _cts = new CancellationTokenSource();
        FindingDuplicates = true;
        var progress = new Progress<DuplicateProgress>(p => ProgressText = Loc.Instance.Format("An.DupProgress", p.GroupsChecked, p.GroupsTotal, Size(p.BytesHashed)));
        var groups = await DuplicateFinder.FindAsync(_result.SizeGroups, progress, _cts.Token);
        Duplicates.Clear();
        foreach (var g in groups.Take(500))
            Duplicates.Add(new DuplicateRow(Loc.Instance.Format("An.DupGroup", g.Paths.Count, Size(g.FileSize)), Loc.Instance.Format("An.DupWasted", Size(g.WastedBytes)), g.Paths));
        DuplicatesSearched = true;
        OnPropertyChanged(nameof(DuplicatesSearched));
        ProgressText = Loc.Instance.Format("An.DupDone", groups.Count, Size(groups.Aggregate(0UL, (a, g) => a + g.WastedBytes)));
        FindingDuplicates = false;
    }

    /// <summary>Moves one file to the Recycle Bin after confirmation and safety re-checks.</summary>
    private void Recycle(FileRow row)
    {
        var why = UserFileGuard.Check(row.Path, row.Size, row.ModifiedUtc);
        if (why is not null) { Message = Loc.Instance.Format("An.CannotRemove", row.Name, L(why)); return; }
        if (!_shell.Confirm(L("An.RecycleTitle"), Loc.Instance.Format("An.RecycleConfirm", row.Path, row.SizeText))) return;
        try
        {
            _remover.Remove(row.Path, RemovalMode.RecycleBin);
            Files.Remove(row);
            Message = Loc.Instance.Format("An.Recycled", row.Name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Message = Loc.Instance.Format("An.CannotRemove", row.Name, ex.Message);
        }
    }
}

/// <summary>Rules for removing a single user-chosen file from the analyzer.</summary>
public static class UserFileGuard
{
    /// <summary>Returns a localization key explaining why the file must not be removed, or null.</summary>
    public static string? Check(string path, ulong expectedSize, DateTime expectedWriteUtc)
    {
        string full;
        try { full = Path.GetFullPath(path); } catch { return "Guard.Invalid"; }
        foreach (var sys in ProtectedRoots())
            if (full.StartsWith(sys, StringComparison.OrdinalIgnoreCase)) return "Guard.SystemFolder";
        var name = Path.GetFileName(full);
        if (name.Equals("pagefile.sys", StringComparison.OrdinalIgnoreCase) || name.Equals("hiberfil.sys", StringComparison.OrdinalIgnoreCase)
            || name.Equals("swapfile.sys", StringComparison.OrdinalIgnoreCase)) return "Guard.SystemFolder";
        var info = new FileInfo(full);
        if (!info.Exists) return "Guard.Missing";
        if ((info.Attributes & (FileAttributes.System | FileAttributes.ReparsePoint)) != 0) return "Guard.SystemFolder";
        if ((ulong)info.Length != expectedSize || info.LastWriteTimeUtc != expectedWriteUtc) return "Guard.Changed";
        return null;
    }

    private static IEnumerable<string> ProtectedRoots()
    {
        foreach (var f in new[] { Environment.SpecialFolder.Windows, Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86,
                     Environment.SpecialFolder.CommonApplicationData })
        {
            var p = Environment.GetFolderPath(f);
            if (!string.IsNullOrEmpty(p)) yield return p.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        }
    }
}

// =============================================================================================

public sealed class CleanupTargetViewModel : ObservableObject
{
    private bool _selected;
    public CleanupTargetViewModel(CleanupTarget target, bool enabled)
    {
        Target = target;
        IsEnabled = enabled;
        _selected = target.DefaultSelected && enabled;
    }

    public CleanupTarget Target { get; }
    public string Name => L(Target.NameKey);
    public string Description => L(Target.DescriptionKey);
    public bool IsEnabled { get; }
    public string? DisabledReason => IsEnabled ? null : L("Clean.NeedsAdmin");
    public CleanupScanResult? Result { get; set; }
    public string SizeText => Result is null ? "—" : Size(Result.TotalBytes);
    public string CountText => Result is null ? "" : Loc.Instance.Format("Clean.Files", Number(Result.Files.Count));
    public bool IsSelected { get => _selected; set => Set(ref _selected, value && IsEnabled); }
    public void Notify() { OnPropertyChanged(nameof(SizeText)); OnPropertyChanged(nameof(CountText)); }
}

/// <summary>Cleanup (module 21): scan → review → confirm → remove (Recycle Bin by default) → report.</summary>
public sealed class CleanupPageViewModel : PageViewModel
{
    private readonly CleanupService _service;
    private readonly IReadOnlyList<CleanupTarget> _targets;
    private readonly Core.Abstractions.IPrivilegeService _privileges;
    private AppSettings _settings = new();
    private bool _scanning, _cleaning, _confirmed, _scanned;
    private string? _report;
    private CleanupTargetViewModel? _preview;
    private string _exclusionsText = "";
    private Action? _saveSettings;

    public CleanupPageViewModel(CleanupService service, IReadOnlyList<CleanupTarget> targets, Core.Abstractions.IPrivilegeService privileges)
    {
        _service = service;
        _targets = targets;
        _privileges = privileges;
        foreach (var t in targets) Targets.Add(new CleanupTargetViewModel(t, !t.RequiresAdmin || privileges.IsElevated));
        foreach (var t in Targets) t.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(CleanupTargetViewModel.IsSelected)) UpdateTotals(); };
        ScanCommand = new AsyncRelayCommand(() => ScanAsync(clearReport: true), ex => { Report = ex.Message; Scanning = false; });
        CleanCommand = new AsyncRelayCommand(CleanAsync, ex => { Report = ex.Message; Cleaning = false; });
        ShowFilesCommand = new RelayCommand(o => Preview = o as CleanupTargetViewModel);
    }

    public override string TitleKey => "Nav.Cleanup";
    public ObservableCollection<CleanupTargetViewModel> Targets { get; } = new();
    public ObservableCollection<string> PreviewFiles { get; } = new();
    public AsyncRelayCommand ScanCommand { get; }
    public AsyncRelayCommand CleanCommand { get; }
    public RelayCommand ShowFilesCommand { get; }

    public bool Scanning { get => _scanning; private set { if (Set(ref _scanning, value)) OnPropertyChanged(nameof(CanClean)); } }
    public bool Cleaning { get => _cleaning; private set { if (Set(ref _cleaning, value)) OnPropertyChanged(nameof(CanClean)); } }
    public bool Scanned => _scanned;
    public bool Confirmed { get => _confirmed; set { if (Set(ref _confirmed, value)) OnPropertyChanged(nameof(CanClean)); } }
    public string? Report { get => _report; private set { Set(ref _report, value); OnPropertyChanged(nameof(HasReport)); } }
    public bool HasReport => !string.IsNullOrEmpty(_report);
    public string SelectedTotalText { get; private set; } = "";
    public bool CanClean => _scanned && !_scanning && !_cleaning && _confirmed && SelectedFileCount > 0;
    public int SelectedFileCount => Targets.Where(t => t.IsSelected && t.Result is not null).Sum(t => t.Result!.Files.Count);

    public bool UseRecycleBin
    {
        get => _settings.CleanupToRecycleBin;
        set { if (_settings.CleanupToRecycleBin == value) return; _settings.CleanupToRecycleBin = value; _saveSettings?.Invoke(); OnPropertyChanged(); }
    }

    /// <summary>One folder per line; shared with the analyzer.</summary>
    public string ExclusionsText
    {
        get => _exclusionsText;
        set
        {
            if (!Set(ref _exclusionsText, value)) return;
            _settings.Exclusions = value.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            _saveSettings?.Invoke();
        }
    }

    public CleanupTargetViewModel? Preview
    {
        get => _preview;
        set
        {
            Set(ref _preview, value);
            PreviewFiles.Clear();
            if (value?.Result is { } r)
                foreach (var f in r.Files.OrderByDescending(f => f.Size).Take(300)) PreviewFiles.Add($"{Size(f.Size),12}   {f.Path}");
            OnPropertyChanged(nameof(PreviewTitle));
        }
    }

    public string PreviewTitle => _preview is null ? "" : Loc.Instance.Format("Clean.PreviewTitle", _preview.Name);

    public void Attach(AppSettings settings, Action save)
    {
        _settings = settings;
        _saveSettings = save;
        _exclusionsText = string.Join(Environment.NewLine, settings.Exclusions);
        OnPropertyChanged(nameof(ExclusionsText));
        OnPropertyChanged(nameof(UseRecycleBin));
    }

    public override void Refresh(AppState state)
    {
        base.Refresh(state);
        foreach (var t in Targets) { t.Notify(); }
        UpdateTotals();
    }

    private async Task ScanAsync(bool clearReport)
    {
        Scanning = true;
        if (clearReport) Report = null;
        var results = await _service.ScanAsync(Targets.Where(t => t.IsEnabled).Select(t => t.Target), _settings.Exclusions);
        foreach (var r in results)
        {
            var vm = Targets.First(t => t.Target.Id == r.Target.Id);
            vm.Result = r;
            vm.Notify();
        }
        _scanned = true;
        OnPropertyChanged(nameof(Scanned));
        Scanning = false;
        UpdateTotals();
    }

    private void UpdateTotals()
    {
        var bytes = Targets.Where(t => t.IsSelected && t.Result is not null).Aggregate(0UL, (a, t) => a + t.Result!.TotalBytes);
        SelectedTotalText = Loc.Instance.Format("Clean.Selected", Size(bytes), Number(SelectedFileCount));
        OnPropertyChanged(nameof(SelectedTotalText));
        OnPropertyChanged(nameof(CanClean));
    }

    private async Task CleanAsync()
    {
        if (!CanClean) return;
        Cleaning = true;
        var items = Targets.Where(t => t.IsSelected && t.Result is not null)
            .SelectMany(t => t.Result!.Files.Select(f => (t.Target, f))).ToList();
        var mode = _settings.CleanupToRecycleBin ? RemovalMode.RecycleBin : RemovalMode.Permanent;
        var report = await _service.CleanAsync(items, mode, _settings.Exclusions);
        Report = Loc.Instance.Format("Clean.Report", Number(report.Removed), Size(report.FreedBytes), Number(report.Skipped))
                 + (mode == RemovalMode.RecycleBin ? Environment.NewLine + L("Clean.InRecycleBin") : "");
        Confirmed = false;
        Cleaning = false;
        await ScanAsync(clearReport: false); // show what is left, keep the report visible
    }
}
