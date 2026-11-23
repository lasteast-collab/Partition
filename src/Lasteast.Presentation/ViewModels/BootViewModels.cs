using System.Collections.ObjectModel;
using Lasteast.Core.Backup;
using Lasteast.Core.Boot;
using Lasteast.Core.Localization;
using Lasteast.Core.Models;
using Lasteast.Core.Operations;
using Lasteast.Presentation.Mvvm;
using Lasteast.Presentation.Services;
using static Lasteast.Presentation.ViewModels.Text;

namespace Lasteast.Presentation.ViewModels;

public sealed record BcdRow(string Type, string Description, string Identifier, string Device, string Path);
public sealed record BackupRow(string Title, string Detail, RelayCommand RestoreCommand);

/// <summary>Boot recovery (spec module "Boot Recovery") and the rescue media builder.</summary>
public sealed class BootRecoveryPageViewModel : PageViewModel
{
    private readonly IBootTools _boot;
    private readonly IRescueMediaBuilder _rescue;
    private readonly OperationQueue _queue;
    private readonly IShellService _shell;
    private readonly string _backupRoot;
    private readonly Action _openPending;
    private StorageInventory? _inv;
    private string? _status;
    private bool _building;
    private CancellationTokenSource? _cts;

    public BootRecoveryPageViewModel(IBootTools boot, IRescueMediaBuilder rescue, OperationQueue queue, IShellService shell, string backupRoot, Action openPending)
    {
        _boot = boot;
        _rescue = rescue;
        _queue = queue;
        _shell = shell;
        _backupRoot = backupRoot;
        _openPending = openPending;
        void Err(Exception ex) => Status = ex is OperationFailedException o ? Loc.Instance.Format(o.Key, o.Detail ?? "") : ex.Message;
        LoadBcdCommand = new AsyncRelayCommand(LoadBcdAsync, Err);
        ExportBcdCommand = new AsyncRelayCommand(async () =>
        {
            var f = await _boot.ExportBcdAsync(DocumentsFolder("BCD"));
            Status = Loc.Instance.Format("Boot.Exported", f);
            _shell.RevealFile(f);
        }, Err);
        ListEspCommand = new AsyncRelayCommand(async () =>
        {
            EspFiles.Clear();
            foreach (var (p, size) in (await _boot.ListEspAsync()).OrderBy(x => x.Path)) EspFiles.Add($"{p}  ·  {Size((ulong)size)}");
            Status = Loc.Instance.Format("Boot.EspCount", EspFiles.Count);
        }, Err);
        BackupEspCommand = new AsyncRelayCommand(async () =>
        {
            var folder = Path.Combine(DocumentsFolder("ESP"), DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            var n = await _boot.BackupEspAsync(folder);
            Status = Loc.Instance.Format("Boot.EspSaved", n, folder);
            _shell.OpenFolder(folder);
        }, Err);
        RepairCommand = new RelayCommand(() =>
        {
            var win = _inv?.Disks.FirstOrDefault(d => d.IsWindowsDisk);
            if (win is null) { Status = L("Mig.NoWindows"); return; }
            var r = _queue.TryAdd(new RepairBootOp(win.Number));
            if (r.Any(i => i.IsError)) Status = string.Join(" ", r.Where(i => i.IsError).Select(OperationText.Issue)); else _openPending();
        });
        BuildIsoCommand = new AsyncRelayCommand(BuildIsoAsync, ex => { Err(ex); Building = false; });
        CancelBuildCommand = new RelayCommand(() => _cts?.Cancel());
    }

    public override string TitleKey => "Nav.BootRecovery";
    public ObservableCollection<BcdRow> BcdEntries { get; } = new();
    public ObservableCollection<string> EspFiles { get; } = new();
    public ObservableCollection<BackupRow> Backups { get; } = new();
    public ObservableCollection<string> RescueLog { get; } = new();
    public AsyncRelayCommand LoadBcdCommand { get; }
    public AsyncRelayCommand ExportBcdCommand { get; }
    public AsyncRelayCommand ListEspCommand { get; }
    public AsyncRelayCommand BackupEspCommand { get; }
    public RelayCommand RepairCommand { get; }
    public AsyncRelayCommand BuildIsoCommand { get; }
    public RelayCommand CancelBuildCommand { get; }
    public string? Status { get => _status; private set { Set(ref _status, value); OnPropertyChanged(nameof(HasStatus)); } }
    public bool HasStatus => !string.IsNullOrEmpty(_status);
    public bool Building { get => _building; private set => Set(ref _building, value); }
    public string FirmwareText => _inv is null ? "" : Firmware(_inv.Firmware);

    public string RescueStatus
    {
        get
        {
            var s = _rescue.Detect();
            if (!s.AdkInstalled || !s.WinPeInstalled) return L("Rescue.NeedAdk");
            if (s.RescuePayload is null) return L("Rescue.NoPayload");
            return Loc.Instance.Format("Rescue.Ready", s.KitsRoot ?? "");
        }
    }

    private static string DocumentsFolder(string sub) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Lasteast", sub);

    private async Task LoadBcdAsync()
    {
        BcdEntries.Clear();
        foreach (var e in await _boot.EnumerateBcdAsync())
            BcdEntries.Add(new BcdRow(e.Type, e.Description, e.Identifier, e.Get("device") ?? "", e.Get("path") ?? ""));
        Status = Loc.Instance.Format("Boot.BcdCount", BcdEntries.Count);
    }

    private async Task BuildIsoAsync()
    {
        var folder = _shell.PickFolder();
        if (folder is null) return;
        Building = true;
        RescueLog.Clear();
        _cts = new CancellationTokenSource();
        var iso = Path.Combine(folder, "LasteastRescue.iso");
        var log = new Progress<string>(l => { RescueLog.Add(l); while (RescueLog.Count > 400) RescueLog.RemoveAt(0); });
        try
        {
            var path = await _rescue.BuildIsoAsync(iso, log, _cts.Token);
            Status = Loc.Instance.Format("Rescue.Built", path);
            _shell.RevealFile(path);
        }
        catch (OperationCanceledException) { Status = L("LostP.Cancelled"); }
        Building = false;
    }

    public override void Refresh(AppState state)
    {
        base.Refresh(state);
        _inv = state.Inventory;
        OnPropertyChanged(nameof(FirmwareText));
        OnPropertyChanged(nameof(RescueStatus));
        Backups.Clear();
        if (_inv is null || _inv.IsSampleData || !Directory.Exists(_backupRoot)) return;
        foreach (var folder in Directory.GetDirectories(_backupRoot).OrderByDescending(f => f).Take(30))
        {
            TableBackup b;
            try { b = TableBackups.Load(folder); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Core.RawDisk.RawOperationException or System.Text.Json.JsonException or FormatException) { continue; }
            var disk = _inv.Disks.FirstOrDefault(d => d.SizeBytes == b.DiskSize && (b.Serial is null or "" || b.Serial == d.SerialNumber) && (b.Model is null or "" || b.Model == d.Model));
            if (disk is null) continue;
            var op = new RestoreTableOp(disk.Number, folder, b.DiskSize, b.Serial, b.Model, b.Style, b.Partitions, b.Created);
            Backups.Add(new BackupRow($"{L("Common.Disk")} {disk.Number} — {disk.Model} · {b.Created.LocalDateTime:g}",
                Loc.Instance.Format("Boot.BackupDetail", b.Style, b.Partitions.Count, Path.GetFileName(folder)),
                new RelayCommand(() =>
                {
                    var r = _queue.TryAdd(op);
                    if (r.Any(i => i.IsError)) Status = string.Join(" ", r.Where(i => i.IsError).Select(OperationText.Issue)); else _openPending();
                })));
        }
    }
}

public sealed record AccountScenario(string TitleKey, string StepsKey, string? Link)
{
    public string Title => L(TitleKey);
    public string Steps => L(StepsKey);
}

/// <summary>
/// Module 39 — Windows account recovery, guidance only: every path is a supported Microsoft method. Lasteast never
/// bypasses Microsoft-account or domain authentication, never circumvents BitLocker and never reads credentials.
/// </summary>
public sealed class AccountRecoveryPageViewModel : PageViewModel
{
    private readonly Action _openBackup;
    private readonly IShellService _shell;

    public AccountRecoveryPageViewModel(IShellService shell, Action openBackup)
    {
        _shell = shell;
        _openBackup = openBackup;
        OpenBackupCommand = new RelayCommand(() => _openBackup());
        CopyLinkCommand = new RelayCommand(o => { if (o is string s) { _shell.CopyToClipboard(s); Copied = Loc.Instance.Format("Acc.Copied", s); } });
    }

    public override string TitleKey => "Nav.AccountRecovery";
    public RelayCommand OpenBackupCommand { get; }
    public RelayCommand CopyLinkCommand { get; }
    private string? _copied;
    public string? Copied { get => _copied; private set { Set(ref _copied, value); OnPropertyChanged(nameof(HasCopied)); } }
    public bool HasCopied => _copied is not null;

    public IReadOnlyList<AccountScenario> Scenarios { get; } = new[]
    {
        new AccountScenario("Acc.Msa.Title", "Acc.Msa.Steps", "https://account.live.com/password/reset"),
        new AccountScenario("Acc.Local.Title", "Acc.Local.Steps", null),
        new AccountScenario("Acc.Work.Title", "Acc.Work.Steps", "https://aka.ms/sspr"),
        new AccountScenario("Acc.Pin.Title", "Acc.Pin.Steps", null),
        new AccountScenario("Acc.BitLocker.Title", "Acc.BitLocker.Steps", "https://aka.ms/myrecoverykey"),
        new AccountScenario("Acc.Reset.Title", "Acc.Reset.Steps", null),
    };

    public override void Refresh(AppState state)
    {
        base.Refresh(state);
        OnPropertyChanged(nameof(Scenarios)); // re-read localized text
    }
}

public sealed class BackupSourceViewModel : ObservableObject
{
    private bool _isSelected;
    public BackupSourceViewModel(string path, bool selected) { Path = path; _isSelected = selected; }
    public string Path { get; }
    public bool Exists => Directory.Exists(Path);
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }
}

/// <summary>Data backup wizard: copy personal folders to another disk and verify, before repairs or resets.</summary>
public sealed class BackupPageViewModel : PageViewModel
{
    private readonly IShellService _shell;
    private StorageInventory? _inv;
    private string? _destination;
    private bool _verify = true;
    private string? _status;
    private bool _busy;
    private CancellationTokenSource? _cts;

    public BackupPageViewModel(IShellService shell)
    {
        _shell = shell;
        foreach (var f in new[] { Environment.SpecialFolder.Desktop, Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.MyPictures,
                     Environment.SpecialFolder.MyVideos, Environment.SpecialFolder.MyMusic })
        {
            var p = Environment.GetFolderPath(f);
            if (p.Length > 0) Sources.Add(new BackupSourceViewModel(p, true));
        }
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        if (Directory.Exists(downloads)) Sources.Add(new BackupSourceViewModel(downloads, false));
        AddFolderCommand = new RelayCommand(() => { if (_shell.PickFolder() is { } f && Sources.All(s => s.Path != f)) Sources.Add(new BackupSourceViewModel(f, true)); });
        DestinationCommand = new RelayCommand(() => { if (_shell.PickFolder() is { } f) Destination = f; });
        RunCommand = new AsyncRelayCommand(RunAsync, ex => { Status = ErrorText(ex); Busy = false; });
        CancelCommand = new RelayCommand(() => _cts?.Cancel());
    }

    public override string TitleKey => "Nav.Backup";
    public ObservableCollection<BackupSourceViewModel> Sources { get; } = new();
    public RelayCommand AddFolderCommand { get; }
    public RelayCommand DestinationCommand { get; }
    public AsyncRelayCommand RunCommand { get; }
    public RelayCommand CancelCommand { get; }
    public string? Destination { get => _destination; set { if (Set(ref _destination, value)) OnPropertyChanged(nameof(DestinationText)); } }
    public string DestinationText => _destination ?? L("Backup.NoDestination");
    public bool Verify { get => _verify; set => Set(ref _verify, value); }
    public string? Status { get => _status; private set { Set(ref _status, value); OnPropertyChanged(nameof(HasStatus)); } }
    public bool HasStatus => !string.IsNullOrEmpty(_status);
    public bool Busy { get => _busy; private set => Set(ref _busy, value); }

    private async Task RunAsync()
    {
        var sources = Sources.Where(s => s.IsSelected && s.Exists).Select(s => s.Path).ToList();
        if (sources.Count == 0) { Status = L("Rec.NothingSelected"); return; }
        if (_destination is null) { Status = L("Backup.NoDestination"); return; }
        if (_inv is not null)
        {
            var destDisk = Core.Recovery.VolumeLocator.DiskOfPath(_inv, _destination);
            if (destDisk is not null && sources.Any(s => Core.Recovery.VolumeLocator.DiskOfPath(_inv, s) == destDisk))
            {
                Status = L("Backup.SameDisk");
                return;
            }
        }
        Busy = true;
        _cts = new CancellationTokenSource();
        var target = Path.Combine(_destination, $"Lasteast Backup {DateTime.Now:yyyy-MM-dd HHmm}");
        var progress = new Progress<BackupProgress>(p => Status = Loc.Instance.Format("Backup.Progress", p.Files, Size((ulong)p.Bytes)));
        try
        {
            var r = await Task.Run(() => FolderBackup.Run(sources, target, _verify, progress, _cts.Token));
            Status = Loc.Instance.Format("Backup.Done", r.Copied, Size((ulong)r.Bytes), r.Skipped, r.Failed)
                     + (r.Errors.Count > 0 ? "\n" + string.Join("\n", r.Errors.Take(5).Select(e => $"{e.Path}: {e.Error}")) : "");
            _shell.OpenFolder(target);
        }
        catch (OperationCanceledException) { Status = L("LostP.Cancelled"); }
        catch (IOException ex) when (ex.Message.StartsWith("Backup.Error.Inside")) { Status = L("Backup.Error.Inside"); }
        Busy = false;
    }

    public override void Refresh(AppState state)
    {
        base.Refresh(state);
        _inv = state.Inventory;
    }
}
