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
        Core.Cleanup.IFileRemover? remover = null, IReadOnlyList<Core.Cleanup.CleanupTarget>? cleanupTargets = null,
        DiskReader? diskReader = null, Core.Boot.IBootTools? bootTools = null, Core.Boot.IRescueMediaBuilder? rescueBuilder = null,
        string? tableBackupRoot = null, Core.Health.IDiskHealthProvider? healthProvider = null,
        Core.Info.IVirtualDiskManager? virtualDisks = null, Core.Info.ISystemInfoProvider? systemInfo = null, Core.Licensing.LicenseStore? licenses = null)
    {
        _ui = SynchronizationContext.Current;
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
        FileRecoveryPage = new FileRecoveryPageViewModel(diskReader, shell);
        BootPage = new BootRecoveryPageViewModel(bootTools ?? new NullBootTools(), rescueBuilder ?? new NullRescueMediaBuilder(), Queue, shell,
            tableBackupRoot ?? Path.Combine(Path.GetTempPath(), "lasteast-none"), () => SelectedNav = PendingNav);
        BackupPage = new BackupPageViewModel(shell);
        HealthPage = new DiskHealthPageViewModel(healthProvider);
        SettingsPage.MonitorChanged += StartMonitor;
        PerformancePage = new PerformanceLabPageViewModel(diskReader);
        _licenses = licenses ?? new Core.Licensing.LicenseStore(Path.Combine(Path.GetTempPath(), "lasteast-no-license.json"));
        License = _licenses.Load();
        void Go(string key) { var n = NavGroups!.SelectMany(g => g.Items).FirstOrDefault(i => i.TitleKey == key); if (n is not null) SelectedNav = n; }
        VirtualDisksPage = new VirtualDisksPageViewModel(virtualDisks, shell, Queue, RefreshAsync, () => SelectedNav = PendingNav);
        FileOpsPage = new FileOperationsPageViewModel(systemInfo ?? new Core.Info.NullSystemInfoProvider());
        TechnicianPage = new TechnicianPageViewModel(systemInfo ?? new Core.Info.NullSystemInfoProvider(), shell, () => HealthPage.Reports, () => Edition);
        AssistantPage = new AssistantPageViewModel(Queue, () => SelectedNav = PendingNav, Go, CreateLlmClient);
        ToolsPage = new ToolsPageViewModel(Go);
        SettingsPage.LoadLicenseCommand = new RelayCommand(() =>
        {
            if (shell.PickOpenFile("License|*.json;*.lic") is not { } f) return;
            License = _licenses.Install(f);
            SettingsPage.SetLicense(License.Status, License.License);
            Rebuild();
        });
        SettingsPage.SetLicense(License.Status, License.License);
        _healthProvider = healthProvider;
        DismissAlertsCommand = new RelayCommand(() => { _dismissed.UnionWith(_alertIds); Alerts.Clear(); OnPropertyChanged(nameof(HasAlerts)); });
        NavItem? backupNav = null;
        AccountPage = new AccountRecoveryPageViewModel(shell, () => SelectedNav = backupNav!);
        NavItem? fileRecoveryNav = null;
        PartitionRecoveryPage = new PartitionRecoveryPageViewModel(Queue, diskReader, () => SelectedNav = PendingNav,
            src => { FileRecoveryPage.Show(src); SelectedNav = fileRecoveryNav!; });
        var smartFixNav = new NavItem("Nav.SmartFix", "\uE945", SmartFixPage);
        var assistantNav = new NavItem("Nav.Assistant", "\uE99A", AssistantPage);
        NavGroups = new[]
        {
            new NavGroup("NavGroup.Home", overview, smartFixNav, assistantNav),
            new NavGroup("NavGroup.Disks",
                new NavItem("Nav.AllDisks", "\uEDA2", DisksPage),
                new NavItem("Nav.AllPartitions", "\uE8FD", PartitionsPage),
                PendingNav = new NavItem("Nav.Pending", "\uE8FB", PendingPage),
                new NavItem("Nav.DiskConversion", "\uE8AB", ConversionPage)),
            new NavGroup("NavGroup.Migrate",
                new NavItem("Nav.DiskClone", "\uE8C8", ClonePage),
                new NavItem("Nav.OsMigration", "\uE895", MigrationPage)),
            new NavGroup("NavGroup.Recover",
                new NavItem("Nav.PartitionRecovery", "\uE81C", PartitionRecoveryPage),
                fileRecoveryNav = new NavItem("Nav.FileRecovery", "\uE8E5", FileRecoveryPage),
                new NavItem("Nav.BootRecovery", "\uE7E8", BootPage),
                new NavItem("Nav.AccountRecovery", "\uE8D7", AccountPage),
                backupNav = new NavItem("Nav.Backup", "\uE8F7", BackupPage)),
            new NavGroup("NavGroup.Analyze",
                new NavItem("Nav.StorageAnalyzer", "\uE9D9", AnalyzerPage),
                new NavItem("Nav.Cleanup", "\uEA99", CleanupPage),
                new NavItem("Nav.DiskHealth", "\uE95E", HealthPage),
                new NavItem("Nav.PerformanceLab", "\uEC4A", PerformancePage)),
            new NavGroup("NavGroup.Advanced",
                new NavItem("Nav.VirtualDisks", "\uE8F1", VirtualDisksPage),
                new NavItem("Nav.FileOperations", "\uE8B7", FileOpsPage),
                new NavItem("Nav.Technician", "\uE90F", TechnicianPage),
                new NavItem("Nav.Tools", "\uEC7A", ToolsPage)),
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
        _store = store;
        DismissWelcomeCommand = new RelayCommand(() => CloseWelcome());
        WelcomeScanCommand = new AsyncRelayCommand(async () => { CloseWelcome(); SelectedNav = smartFixNav; await SmartFixPage.ScanCommand.ExecuteAsync(); });
        WelcomeVirtualCommand = new RelayCommand(() => { CloseWelcome(); var n = NavGroups.SelectMany(g => g.Items).First(i => i.TitleKey == "Nav.VirtualDisks"); SelectedNav = n; });
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
    public PartitionRecoveryPageViewModel PartitionRecoveryPage { get; }
    public FileRecoveryPageViewModel FileRecoveryPage { get; }
    public BootRecoveryPageViewModel BootPage { get; }
    public AccountRecoveryPageViewModel AccountPage { get; }
    public BackupPageViewModel BackupPage { get; }
    public DiskHealthPageViewModel HealthPage { get; }
    public PerformanceLabPageViewModel PerformancePage { get; }
    public VirtualDisksPageViewModel VirtualDisksPage { get; }
    public FileOperationsPageViewModel FileOpsPage { get; }
    public TechnicianPageViewModel TechnicianPage { get; }
    public AssistantPageViewModel AssistantPage { get; }
    public ToolsPageViewModel ToolsPage { get; }

    private readonly Core.Licensing.LicenseStore _licenses;
    public (Core.Licensing.LicenseStatus Status, Core.Licensing.License? License) License { get; private set; }
    public Core.Licensing.Edition Edition => Core.Licensing.Licenses.Effective(License.Status, License.License);

    /// <summary>The optional AI planner client (Settings › Assistant); null unless enabled.</summary>
    private Core.Assistant.ILlmClient? CreateLlmClient()
    {
        if (!AppSettings.AssistantAiEnabled || !Uri.TryCreate(AppSettings.AssistantAiEndpoint, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return null;
        return new Core.Assistant.OpenAiCompatibleClient(_http, uri, AppSettings.AssistantAiModel, Environment.GetEnvironmentVariable("LASTEAST_LLM_KEY"));
    }

    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(60) };

    // ---- background monitor (free space, health, temperature)
    private readonly SynchronizationContext? _ui;
    private readonly Core.Health.IDiskHealthProvider? _healthProvider;
    private readonly HashSet<string> _dismissed = new();
    private HashSet<string> _alertIds = new();
    private CancellationTokenSource? _monitorCts;
    public System.Collections.ObjectModel.ObservableCollection<AlertViewModel> Alerts { get; } = new();
    public bool HasAlerts => Alerts.Count > 0;
    public RelayCommand DismissAlertsCommand { get; }

    // ---- first-run welcome
    private readonly JsonSettingsStore _store;
    public bool ShowWelcome => !AppSettings.WelcomeShown;
    public RelayCommand DismissWelcomeCommand { get; }
    public AsyncRelayCommand WelcomeScanCommand { get; }
    public RelayCommand WelcomeVirtualCommand { get; }
    public string VersionText => "v" + (typeof(MainViewModel).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
        .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion.Split('+')[0] ?? "?");

    private void CloseWelcome()
    {
        AppSettings.WelcomeShown = true;
        try { _store.Save(AppSettings); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        OnPropertyChanged(nameof(ShowWelcome));
    }

    /// <summary>Starts the periodic check (Settings › Monitoring). Safe to call again after settings change.</summary>
    public void StartMonitor()
    {
        _monitorCts?.Cancel();
        if (!AppSettings.MonitorEnabled) return;
        _monitorCts = new CancellationTokenSource();
        var ct = _monitorCts.Token;
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(20), ct); // let the first inventory finish
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(Math.Clamp(AppSettings.MonitorIntervalMinutes, 5, 24 * 60)));
            do { await CheckOnceAsync(); } while (await timer.WaitForNextTickAsync(ct));
        }, ct);
    }

    /// <summary>One monitor pass; also used by tests.</summary>
    public async Task CheckOnceAsync()
    {
        var inv = _inventory;
        if (inv is null || inv.IsSampleData) return;
        var health = new Dictionary<int, Core.Health.DiskHealthReport>();
        if (_healthProvider is not null && inv.IsElevated)
            foreach (var d in inv.Disks)
                try { health[d.Number] = await _healthProvider.ReadAsync(d); } catch (Exception ex) { _log.Error("Monitor health read", ex); }
        var alerts = Core.Health.MonitorRules.Evaluate(inv, health, AppSettings).Where(a => !_dismissed.Contains(a.Id)).ToList();
        void Apply(object? _)
        {
            _alertIds = alerts.Select(a => a.Id).ToHashSet();
            Alerts.Clear();
            foreach (var a in alerts)
                Alerts.Add(new AlertViewModel(Loc.Instance.Format(a.Key, a.Args.Select(x => x is ulong u ? (object)Size(u) : x).ToArray()), a.Level.ToString()));
            OnPropertyChanged(nameof(HasAlerts));
        }
        if (_ui is not null) _ui.Post(Apply, null); else Apply(null);
    }
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
        PartitionRecoveryPage.Refresh(state);
        FileRecoveryPage.Refresh(state);
        BootPage.Refresh(state);
        AccountPage.Refresh(state);
        BackupPage.Refresh(state);
        HealthPage.Refresh(state);
        PerformancePage.Refresh(state);
        VirtualDisksPage.Refresh(state);
        FileOpsPage.Refresh(state);
        TechnicianPage.Refresh(state);
        AssistantPage.Refresh(state);
        ToolsPage.Refresh(state);
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
