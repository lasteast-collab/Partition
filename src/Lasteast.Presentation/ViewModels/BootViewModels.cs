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

public sealed record BcdRow(string Type, string Description, string Identifier, string Device, string Path,
    RelayCommand? SetDefaultCommand = null, RelayCommand? MoveUpCommand = null, RelayCommand? MoveDownCommand = null, RelayCommand? RenameCommand = null,
    bool IsDefault = false, bool IsBootEntry = false);
public sealed record MbrBackupChoice(string Title, string? Folder)
{
    public override string ToString() => Title;
}

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
        _err = Err;
        SetTimeoutCommand = new AsyncRelayCommand(async () =>
        {
            if (!int.TryParse(_timeout, out var t) || t is < 0 or > 999) { Status = L("Editor.InvalidSize"); return; }
            if (!_shell.Confirm(L("Boot.EditTitle"), Loc.Instance.Format("Boot.ConfirmTimeout", t))) return;
            var b = await _boot.SetTimeoutAsync(t);
            Status = Loc.Instance.Format("Boot.Edited", b);
        }, Err);
        CheckEspCommand = new AsyncRelayCommand(async () =>
        {
            var files = await _boot.ListEspAsync();
            var (missing, fallback) = EspAnalyzer.Check(files.Select(f => f.Path));
            int esps = _inv?.Disks.Sum(d => d.Partitions.Count(p => p.Role == PartitionRole.EfiSystem)) ?? 0;
            EspReport = (missing.Count == 0 ? L("Boot.EspOk") : Loc.Instance.Format("Boot.EspMissing", string.Join(", ", missing)))
                        + " " + L(fallback ? "Boot.EspFallbackYes" : "Boot.EspFallbackNo")
                        + (esps > 1 ? " " + Loc.Instance.Format("Boot.EspMultiple", esps) : "");
        }, Err);
        RestoreEspCommand = new AsyncRelayCommand(async () =>
        {
            if (_shell.PickFolder() is not { } folder) return;
            if (!_shell.Confirm(L("Boot.EditTitle"), Loc.Instance.Format("Boot.ConfirmRestoreEsp", folder))) return;
            var n = await _boot.RestoreEspAsync(folder);
            Status = Loc.Instance.Format("Boot.EspRestored", n);
        }, Err);
        LoadWinReCommand = new AsyncRelayCommand(async () =>
        {
            var w = await _boot.WinReAsync();
            WinReText = w.Enabled is null ? L("Boot.WinReUnknown") : Loc.Instance.Format(w.Enabled == true ? "Boot.WinReOn" : "Boot.WinReOff", w.Location ?? "—");
        }, Err);
        EnableWinReCommand = new AsyncRelayCommand(() => SetWinReAsync(true), Err);
        DisableWinReCommand = new AsyncRelayCommand(() => SetWinReAsync(false), Err);
        RepairMbrCommand = new RelayCommand(() =>
        {
            if (_mbrDisk is not { } d) { Status = L("LostP.PickDisk"); return; }
            var r = _queue.TryAdd(new RepairMbrOp(d.Number, _mbrBackup?.Folder));
            if (r.Any(i => i.IsError)) Status = string.Join(" ", r.Where(i => i.IsError).Select(OperationText.Issue)); else _openPending();
        });
        CancelBuildCommand = new RelayCommand(() => _cts?.Cancel());
    }

    private Action<Exception> _err = _ => { };
    private string _timeout = "30";
    private string? _espReport, _winRe;
    private DiskChoice? _mbrDisk;
    private MbrBackupChoice? _mbrBackup;
    public string TimeoutText { get => _timeout; set => Set(ref _timeout, value); }
    public string? EspReport { get => _espReport; private set { Set(ref _espReport, value); OnPropertyChanged(nameof(HasEspReport)); } }
    public bool HasEspReport => !string.IsNullOrEmpty(_espReport);
    public string? WinReText { get => _winRe; private set => Set(ref _winRe, value); }
    public AsyncRelayCommand SetTimeoutCommand { get; }
    public AsyncRelayCommand CheckEspCommand { get; }
    public AsyncRelayCommand RestoreEspCommand { get; }
    public AsyncRelayCommand LoadWinReCommand { get; }
    public AsyncRelayCommand EnableWinReCommand { get; }
    public AsyncRelayCommand DisableWinReCommand { get; }
    public RelayCommand RepairMbrCommand { get; }
    public ObservableCollection<DiskChoice> MbrDisks { get; } = new();
    public ObservableCollection<MbrBackupChoice> MbrBackups { get; } = new();
    public DiskChoice? MbrDisk { get => _mbrDisk; set { if (Set(ref _mbrDisk, value)) FillMbrBackups(); } }
    public MbrBackupChoice? MbrBackup { get => _mbrBackup; set => Set(ref _mbrBackup, value); }

    private void FillMbrBackups()
    {
        MbrBackups.Clear();
        MbrBackups.Add(new MbrBackupChoice(L("Boot.MbrBootsect"), null));
        var disk = _inv?.Disks.FirstOrDefault(d => d.Number == _mbrDisk?.Number);
        if (disk is null) return;
        var list = new List<TableBackup>();
        if (Directory.Exists(_backupRoot))
            foreach (var folder in Directory.GetDirectories(_backupRoot).OrderByDescending(f => f).Take(60))
                try { list.Add(TableBackups.Load(folder)); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Core.RawDisk.RawOperationException or System.Text.Json.JsonException or FormatException) { }
        foreach (var b in list.Where(b => b.Style == PartitionStyle.Mbr && b.DiskSize == disk.SizeBytes
                     && (string.IsNullOrEmpty(b.Serial) || b.Serial == disk.SerialNumber)).Take(20))
            MbrBackups.Add(new MbrBackupChoice(Loc.Instance.Format("Boot.MbrFromBackup", b.Created.LocalDateTime.ToString("g")), b.Folder));
        _mbrBackup = MbrBackups[0];
        OnPropertyChanged(nameof(MbrBackup));
    }

    private async Task SetWinReAsync(bool enable)
    {
        if (!_shell.Confirm(L("Boot.EditTitle"), L(enable ? "Boot.ConfirmWinReOn" : "Boot.ConfirmWinReOff"))) return;
        Status = await _boot.SetWinReAsync(enable);
        await LoadWinReCommand.ExecuteAsync();
    }

    private async Task EditAsync(string confirmKey, object arg, Func<Task<string>> action)
    {
        if (!_shell.Confirm(L("Boot.EditTitle"), Loc.Instance.Format(confirmKey, arg))) return;
        try
        {
            var backup = await action();
            Status = Loc.Instance.Format("Boot.Edited", backup);
            await LoadBcdAsync();
        }
        catch (Exception ex) { _err(ex); }
    }

    public override string TitleKey => "Nav.BootRecovery";
    public ObservableCollection<BcdRow> BcdEntries { get; } = new();
    private string _rename = "";
    /// <summary>New description for "Rename" on a boot entry.</summary>
    public string RenameText { get => _rename; set => Set(ref _rename, value); }
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
        var all = await _boot.EnumerateBcdAsync();
        var manager = all.FirstOrDefault(e => e.Identifier == "{bootmgr}" || e.Get("displayorder") is not null);
        var order = (manager?.Get("displayorder") ?? "").Split(new[] { ' ', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries).ToList();
        string? def = manager?.Get("default");
        foreach (var e in all)
        {
            var id = e.Identifier;
            int idx = order.IndexOf(id);
            bool boot = idx >= 0;
            RelayCommand? Move(int delta) => !boot ? null : new RelayCommand(async () =>
            {
                int j = idx + delta;
                if (j < 0 || j >= order.Count) return;
                var o = order.ToList();
                (o[idx], o[j]) = (o[j], o[idx]);
                await EditAsync("Boot.ConfirmOrder", e.Description, () => _boot.SetDisplayOrderAsync(o));
            });
            BcdEntries.Add(new BcdRow(e.Type, e.Description, id, e.Get("device") ?? "", e.Get("path") ?? "",
                boot && id != def ? new RelayCommand(async () => await EditAsync("Boot.ConfirmDefault", e.Description, () => _boot.SetDefaultAsync(id))) : null,
                Move(-1), Move(+1),
                boot ? new RelayCommand(async () =>
                {
                    if (string.IsNullOrWhiteSpace(_rename)) { Status = L("Boot.TypeName"); return; }
                    var name = _rename.Trim();
                    await EditAsync("Boot.ConfirmRename", name, () => _boot.SetDescriptionAsync(id, name));
                }) : null,
                id == def, boot));
        }
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
        var keepMbr = _mbrDisk?.Number;
        MbrDisks.Clear();
        foreach (var d in _inv?.Disks.Where(d => d.Style == PartitionStyle.Mbr) ?? Enumerable.Empty<DiskInfo>())
            MbrDisks.Add(new DiskChoice(d.Number, $"{L("Common.Disk")} {d.Number} — {d.Model} · {Size(d.SizeBytes)}"));
        _mbrDisk = MbrDisks.FirstOrDefault(x => x.Number == keepMbr);
        OnPropertyChanged(nameof(MbrDisk));
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
