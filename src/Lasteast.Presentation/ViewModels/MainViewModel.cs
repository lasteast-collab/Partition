using System.Globalization;
using Lasteast.Core.Abstractions;
using Lasteast.Core.Localization;
using Lasteast.Core.Models;
using Lasteast.Core.Operations;
using Lasteast.Core.Settings;
using Lasteast.Presentation.Mvvm;
using Lasteast.Presentation.Services;
using static Lasteast.Presentation.ViewModels.Text;

namespace Lasteast.Presentation.ViewModels;

public sealed class NavItem : ObservableObject
{
    public NavItem(string titleKey, string glyph, PageViewModel page, int? plannedPhase = null)
    {
        TitleKey = titleKey;
        Glyph = glyph;
        Page = page;
        PlannedPhase = plannedPhase;
    }

    public string TitleKey { get; }
    public string Glyph { get; }
    public PageViewModel Page { get; }
    public int? PlannedPhase { get; }
    public bool IsPlanned => PlannedPhase is not null;
    public string Title => L(TitleKey);
    public string PhaseBadge => PlannedPhase is { } p ? Loc.Instance.Format("Nav.PhaseBadge", p) : "";

    public void Relocalize()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(PhaseBadge));
    }
}

public sealed class NavGroup : ObservableObject
{
    public NavGroup(string titleKey, params NavItem[] items)
    {
        TitleKey = titleKey;
        Items = items;
    }

    public string TitleKey { get; }
    public string Title => L(TitleKey);
    public IReadOnlyList<NavItem> Items { get; }
    public void Relocalize() => OnPropertyChanged(nameof(Title));
}

public sealed class MainViewModel : ObservableObject
{
    private readonly IStorageInventoryProvider _provider;
    private readonly IPrivilegeService _privileges;
    private readonly IAppLog _log;
    private readonly string[] _args;
    private StorageInventory? _inventory;
    private NavItem _selectedNav;
    private bool _isBusy;
    private string? _errorText;

    public MainViewModel(IStorageInventoryProvider provider, IPrivilegeService privileges, AppSettings settings,
        JsonSettingsStore store, IThemeService theme, IShellService shell, IAppLog log, string logDirectory,
        string[] args, GuardedExecutionEngine? engine = null, Core.SmartFix.ISystemProbe? systemProbe = null,
        Core.Cleanup.IFileRemover? remover = null, IReadOnlyList<Core.Cleanup.CleanupTarget>? cleanupTargets = null)
    {
        _provider = provider;
        _privileges = privileges;
        _log = log;
        _args = args;
        AppSettings = settings;

        Queue = new OperationQueue();
        Dashboard = new DashboardViewModel(Queue);
        PendingPage = new PendingPageViewModel(Queue, engine, privileges, RefreshAsync);
        DisksPage = new DisksPageViewModel();
        PartitionsPage = new PartitionsPageViewModel();
        SettingsPage = new SettingsPageViewModel(settings, store, theme, shell, logDirectory, Relocalize);

        NavItem Planned(string key, string glyph, int phase) =>
            new(key, glyph, new PlannedModuleViewModel(key, phase, key.Replace("Nav.", "Planned.Desc."), glyph), phase);

        var overview = new NavItem("Nav.Overview", "\uE80F", Dashboard);
        RestartElevatedCommand = new RelayCommand(() =>
        {
            if (_privileges.TryRestartElevated(_args)) RequestShutdown?.Invoke(this, EventArgs.Empty);
        }, () => !_privileges.IsElevated && !IsSampleData);
        SmartFixPage = new SmartFixPageViewModel(Queue, systemProbe ?? new Core.SmartFix.NullSystemProbe(), RefreshAsync,
            () => SelectedNav = PendingNav, RestartElevatedCommand,
            key => { var n = NavGroups!.SelectMany(g => g.Items).FirstOrDefault(i => i.TitleKey == key); if (n is not null) SelectedNav = n; });
        var fileRemover = remover ?? new Core.Cleanup.PermanentFileRemover();
        AnalyzerPage = new StorageAnalyzerViewModel(shell, fileRemover);
        CleanupPage = new CleanupPageViewModel(new Core.Cleanup.CleanupService(fileRemover), cleanupTargets ?? Array.Empty<Core.Cleanup.CleanupTarget>(), privileges);
        CleanupPage.Attach(settings, () => { try { store.Save(settings); } catch (IOException) { } catch (UnauthorizedAccessException) { } });
        ClonePage = new ClonePageViewModel(Queue, () => SelectedNav = PendingNav);
        ConversionPage = new ConversionPageViewModel(Queue, () => SelectedNav = PendingNav);
        MigrationPage = new OsMigrationPageViewModel(Queue, () => SelectedNav = PendingNav);
        var smartFixNav = new NavItem("Nav.SmartFix", "\uE945", SmartFixPage);
        NavGroups = new[]
        {
            new NavGroup("NavGroup.Home", overview, smartFixNav),
            new NavGroup("NavGroup.Disks",
                new NavItem("Nav.AllDisks", "\uEDA2", DisksPage),
                new NavItem("Nav.AllPartitions", "\uE8FD", PartitionsPage),
                PendingNav = new NavItem("Nav.Pending", "\uE8FB", PendingPage),
                new NavItem("Nav.DiskConversion", "\uE8AB", ConversionPage)),
            new NavGroup("NavGroup.Migrate",
                new NavItem("Nav.DiskClone", "\uE8C8", ClonePage),
                new NavItem("Nav.OsMigration", "\uE895", MigrationPage)),
            new NavGroup("NavGroup.Recover",
                Planned("Nav.PartitionRecovery", "\uE81C", 7),
                Planned("Nav.BootRecovery", "\uE7E8", 8)),
            new NavGroup("NavGroup.Analyze",
                new NavItem("Nav.StorageAnalyzer", "\uE9D9", AnalyzerPage),
                new NavItem("Nav.Cleanup", "\uEA99", CleanupPage),
                Planned("Nav.DiskHealth", "\uE95E", 9),
                Planned("Nav.PerformanceLab", "\uEC4A", 9)),
            new NavGroup("NavGroup.Advanced",
                Planned("Nav.VirtualDisks", "\uE8F1", 10),
                Planned("Nav.FileOperations", "\uE8B7", 10),
                Planned("Nav.Technician", "\uE90F", 10),
                Planned("Nav.Tools", "\uEC7A", 9)),
        };
        SettingsNav = new NavItem("Nav.Settings", "\uE713", SettingsPage);
        _selectedNav = overview;

        NavItem Find(string key) => NavGroups.SelectMany(g => g.Items).First(i => i.TitleKey == key);
        // Top bar: Home, Disk Management, Partition Recovery, Migration, Tools, Settings
        TopTargets = new[]
        {
            overview, Find("Nav.AllDisks"), Find("Nav.PartitionRecovery"), Find("Nav.OsMigration"), Find("Nav.Tools"), SettingsNav,
        };

        RefreshCommand = new AsyncRelayCommand(RefreshAsync, ex =>
        {
            _log.Error("Refresh failed", ex);
            ErrorText = Loc.Instance.Format("Error.Refresh", ex.Message);
            IsBusy = false;
        });
        DismissErrorCommand = new RelayCommand(() => ErrorText = null);
        NavigateCommand = new RelayCommand(o => { if (o is NavItem n) SelectedNav = n; });
        OpenPendingCommand = new RelayCommand(() => SelectedNav = PendingNav, () => Queue.Count > 0);
        ScanMyComputerCommand = new AsyncRelayCommand(async () =>
        {
            SelectedNav = smartFixNav;
            await SmartFixPage.ScanCommand.ExecuteAsync();
        });
        Queue.Changed += (_, _) =>
        {
            OnPropertyChanged(nameof(PendingCount));
            OnPropertyChanged(nameof(ApplyText));
            OpenPendingCommand.RaiseCanExecuteChanged();
        };
    }

    public event EventHandler? RequestShutdown;

    public AppSettings AppSettings { get; }
    public Loc Loc => Loc.Instance;
    public IReadOnlyList<NavGroup> NavGroups { get; }
    public NavItem SettingsNav { get; }
    public IReadOnlyList<NavItem> TopTargets { get; }
    public DashboardViewModel Dashboard { get; }
    public DisksPageViewModel DisksPage { get; }
    public PartitionsPageViewModel PartitionsPage { get; }
    public SettingsPageViewModel SettingsPage { get; }

    public AsyncRelayCommand RefreshCommand { get; }
    public RelayCommand RestartElevatedCommand { get; }
    public RelayCommand DismissErrorCommand { get; }
    public RelayCommand NavigateCommand { get; }
    public RelayCommand OpenPendingCommand { get; }
    public AsyncRelayCommand ScanMyComputerCommand { get; }
    public SmartFixPageViewModel SmartFixPage { get; }
    public StorageAnalyzerViewModel AnalyzerPage { get; }
    public CleanupPageViewModel CleanupPage { get; }
    public ClonePageViewModel ClonePage { get; }
    public ConversionPageViewModel ConversionPage { get; }
    public OsMigrationPageViewModel MigrationPage { get; }
    public OperationQueue Queue { get; }
    public PendingPageViewModel PendingPage { get; }
    public NavItem PendingNav { get; } = null!;
    public int PendingCount => Queue.Count;
    public string ApplyText => Loc.Instance.Format("Top.ApplyCount", Queue.Count);

    public NavItem SelectedNav
    {
        get => _selectedNav;
        set
        {
            if (value is null || !Set(ref _selectedNav, value)) return;
            OnPropertyChanged(nameof(CurrentPage));
        }
    }

    public PageViewModel CurrentPage => _selectedNav.Page;

    public bool IsBusy
    {
        get => _isBusy;
        private set => Set(ref _isBusy, value);
    }

    public string? ErrorText
    {
        get => _errorText;
        private set
        {
            Set(ref _errorText, value);
            OnPropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => !string.IsNullOrEmpty(_errorText);
    public bool IsElevated => _privileges.IsElevated;
    public bool IsSampleData => _inventory?.IsSampleData ?? false;
    public bool IsRightToLeft => Loc.Instance.Language.IsRightToLeft;

    public string StatusText => _inventory is null
        ? L("Status.NotScanned")
        : Loc.Instance.Format("Status.Scanned", _inventory.Disks.Count,
            _inventory.CapturedAt.ToLocalTime().ToString("T", Loc.Instance.Language.Culture));

    public string PrivilegeText => L(IsElevated ? "Status.Admin" : "Status.Standard");

    public async Task RefreshAsync()
    {
        IsBusy = true;
        ErrorText = null;
        _inventory = await _provider.GetInventoryAsync();
        IsBusy = false;
        Rebuild();
    }

    /// <summary>Rebuilds every view from the cached inventory (used after language/setting changes).</summary>
    public void Relocalize()
    {
        foreach (var g in NavGroups)
        {
            g.Relocalize();
            foreach (var i in g.Items) i.Relocalize();
        }
        SettingsNav.Relocalize();
        Rebuild();
    }

    private void Rebuild()
    {
        var disks = _inventory?.Disks.Select(d => new DiskViewModel(d, AppSettings.MaskSerialNumbers)).ToList()
                    ?? new List<DiskViewModel>();
        var state = new AppState(_inventory, disks, AppSettings);
        Dashboard.Refresh(state);
        DisksPage.Refresh(state);
        PartitionsPage.Refresh(state);
        Queue.Rebase(_inventory);
        PendingPage.Refresh(state);
        SmartFixPage.Refresh(state);
        AnalyzerPage.Refresh(state);
        CleanupPage.Refresh(state);
        ClonePage.Refresh(state);
        ConversionPage.Refresh(state);
        MigrationPage.Refresh(state);
        SettingsPage.Refresh(state);
        foreach (var g in NavGroups)
        foreach (var i in g.Items)
            if (i.Page is PlannedModuleViewModel pm) pm.Refresh(state);

        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(PrivilegeText));
        OnPropertyChanged(nameof(IsSampleData));
        OnPropertyChanged(nameof(IsRightToLeft));
        OnPropertyChanged(nameof(ApplyText));
        RestartElevatedCommand.RaiseCanExecuteChanged();
    }
}
