using System.Collections.ObjectModel;
using Lasteast.Core.Assistant;
using Lasteast.Core.Health;
using Lasteast.Core.Info;
using Lasteast.Core.Licensing;
using Lasteast.Core.Localization;
using Lasteast.Core.Models;
using Lasteast.Core.Operations;
using Lasteast.Core.Reports;
using Lasteast.Core.Settings;
using Lasteast.Presentation.Mvvm;
using Lasteast.Presentation.Services;
using static Lasteast.Presentation.ViewModels.Text;

namespace Lasteast.Presentation.ViewModels;

public sealed record VirtualDiskRow(string Title, string FileText, RelayCommand DetachCommand);

/// <summary>Virtual disks (VHD/VHDX): create, attach, detach. Virtual disks are the default safe place to try operations.</summary>
public sealed class VirtualDisksPageViewModel : PageViewModel
{
    private readonly IVirtualDiskManager? _vd;
    private readonly IShellService _shell;
    private readonly OperationQueue _queue;
    private readonly Func<Task> _refresh;
    private readonly Action _openPending;
    private StorageInventory? _inv;
    private string _sizeGb = "16";
    private bool _dynamic = true;
    private bool _initialize = true;
    private string? _status;

    public VirtualDisksPageViewModel(IVirtualDiskManager? vd, IShellService shell, OperationQueue queue, Func<Task> refresh, Action openPending)
    {
        _vd = vd;
        _shell = shell;
        _queue = queue;
        _refresh = refresh;
        _openPending = openPending;
        CreateCommand = new AsyncRelayCommand(CreateAsync, Err);
        AttachCommand = new AsyncRelayCommand(AttachAsync, Err);
    }

    private void Err(Exception ex) => Status = ex is OperationFailedException o ? Loc.Instance.Format(o.Key, o.Detail ?? "") : ex.Message;

    public override string TitleKey => "Nav.VirtualDisks";
    public ObservableCollection<VirtualDiskRow> Attached { get; } = new();
    public AsyncRelayCommand CreateCommand { get; }
    public AsyncRelayCommand AttachCommand { get; }
    public string SizeGb { get => _sizeGb; set => Set(ref _sizeGb, value); }
    public bool Dynamic { get => _dynamic; set => Set(ref _dynamic, value); }
    public bool InitializeGpt { get => _initialize; set => Set(ref _initialize, value); }
    public string? Status { get => _status; private set { Set(ref _status, value); OnPropertyChanged(nameof(HasStatus)); } }
    public bool HasStatus => !string.IsNullOrEmpty(_status);
    public bool Available => _vd is not null && _inv is { IsSampleData: false };

    private async Task CreateAsync()
    {
        if (_vd is null) { Status = L("Rec.Unavailable"); return; }
        if (!double.TryParse(_sizeGb, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.CurrentCulture, out var gb) || gb < 0.01 || gb > 64 * 1024)
        { Status = L("Editor.InvalidSize"); return; }
        var path = _shell.PickSaveFile("Lasteast.vhdx", "VHDX|*.vhdx|VHD|*.vhd");
        if (path is null) return;
        var bytes = (ulong)(gb * 1024 * 1024 * 1024) / (1024 * 1024) * (1024 * 1024);
        await Task.Run(() => _vd.Create(path, bytes, _dynamic));
        var device = await Task.Run(() => _vd.Attach(path));
        Status = Loc.Instance.Format("Vd.Created", path, device);
        await _refresh();
        if (_initialize && _inv?.Disks.FirstOrDefault(d => string.Equals(d.DevicePath, device, StringComparison.OrdinalIgnoreCase)) is { } disk)
            if (_queue.TryAdd(new InitializeDiskOp(disk.Number, PartitionStyle.Gpt)).All(i => !i.IsError)) _openPending();
    }

    private async Task AttachAsync()
    {
        if (_vd is null) { Status = L("Rec.Unavailable"); return; }
        var path = _shell.PickOpenFile("VHDX/VHD|*.vhdx;*.vhd");
        if (path is null) return;
        var device = await Task.Run(() => _vd.Attach(path));
        Status = Loc.Instance.Format("Vd.AttachedTo", path, device);
        await _refresh();
    }

    public override void Refresh(AppState state)
    {
        base.Refresh(state);
        _inv = state.Inventory;
        OnPropertyChanged(nameof(Available));
        Attached.Clear();
        if (_vd is null || _inv is null || _inv.IsSampleData) return;
        foreach (var d in _inv.Disks.Where(d => d.Bus == BusKind.FileBackedVirtual))
        {
            string? file = null;
            try { file = _vd.BackingFile(d.DevicePath); } catch { }
            Attached.Add(new VirtualDiskRow($"{L("Common.Disk")} {d.Number} — {Size(d.SizeBytes)} · {Style(d.Style)}", file ?? L("Vd.UnknownFile"),
                new RelayCommand(async () =>
                {
                    if (file is null) { Status = L("Vd.UnknownFile"); return; }
                    if (!_shell.Confirm(L("Vd.DetachTitle"), Loc.Instance.Format("Vd.DetachConfirm", file))) return;
                    try { _vd.Detach(file); Status = Loc.Instance.Format("Vd.Detached", file); await _refresh(); }
                    catch (Exception ex) { Err(ex); }
                }, () => file is not null && !d.IsWindowsDisk)));
        }
    }
}

public sealed record BlockerRow(string Title, string SizeText, string Advice);

/// <summary>File operations: why a volume will not shrink further (page file, hibernation, shadow copies) and what to do about it.</summary>
public sealed class FileOperationsPageViewModel : PageViewModel
{
    private readonly ISystemInfoProvider _info;
    private StorageInventory? _inv;
    private VolumeChoice? _volume;
    private string? _summary;

    public FileOperationsPageViewModel(ISystemInfoProvider info)
    {
        _info = info;
        AnalyzeCommand = new AsyncRelayCommand(AnalyzeAsync, ex => Summary = ex.Message);
    }

    public override string TitleKey => "Nav.FileOperations";
    public ObservableCollection<VolumeChoice> Volumes { get; } = new();
    public ObservableCollection<BlockerRow> Blockers { get; } = new();
    public AsyncRelayCommand AnalyzeCommand { get; }
    public VolumeChoice? Volume { get => _volume; set { if (Set(ref _volume, value)) { Blockers.Clear(); Summary = null; } } }
    public string? Summary { get => _summary; private set => Set(ref _summary, value); }

    private async Task AnalyzeAsync()
    {
        var hit = _inv?.Disks.SelectMany(d => d.Partitions.Select(p => (d, p))).FirstOrDefault(x => x.p.Volume?.DriveLetter == _volume?.Letter);
        if (hit is not { } h || h.d is null) { Summary = L("Perf.PickVolume"); return; }
        var r = await _info.ShrinkAsync(h.d, h.p);
        Blockers.Clear();
        if (r is null) { Summary = L("Rec.Unavailable"); return; }
        var ours = LayoutSimulator.MinShrinkSize(h.p);
        Summary = Loc.Instance.Format("FileOps.Summary", $"{r.Letter}:", Size(r.CurrentSize), Size(r.UsedBytes),
            r.WindowsMinimumSize is { } m ? Size(m) : L("Common.NotAvailable"), Size(ours));
        foreach (var b in r.Blockers)
            Blockers.Add(new BlockerRow(L("FileOps.Kind." + b.Kind) + (b.Path is { } p ? $" ({p})" : ""), Size(b.Bytes), L(b.AdviceKey)));
        if (r.Blockers.Count == 0) Blockers.Add(new BlockerRow(L("FileOps.None"), "", L("FileOps.NoneAdvice")));
    }

    public override void Refresh(AppState state)
    {
        base.Refresh(state);
        _inv = state.Inventory;
        var keep = _volume?.Letter;
        Volumes.Clear();
        foreach (var d in _inv?.Disks ?? Array.Empty<DiskInfo>())
        foreach (var p in d.Partitions.Where(p => p.Volume?.DriveLetter is not null && FileSystems.CanResize(p.Volume.FileSystem)))
            Volumes.Add(new VolumeChoice(p.Volume!.DriveLetter!.Value, $"{p.Volume.DriveLetter}: {p.Volume.Label} · {Size(p.SizeBytes)}"));
        _volume = Volumes.FirstOrDefault(v => v.Letter == keep) ?? Volumes.FirstOrDefault();
        OnPropertyChanged(nameof(Volume));
    }
}

public sealed record BitLockerRow(string Volume, string Protection, string Detail);
public sealed record SpaceRow(string Name, string Detail, string Health);

/// <summary>Technician tools: BitLocker and Storage Spaces overview, and reports to hand over to customers.</summary>
public sealed class TechnicianPageViewModel : PageViewModel
{
    private readonly ISystemInfoProvider _info;
    private readonly IShellService _shell;
    private readonly Func<IReadOnlyDictionary<int, DiskHealthReport>> _health;
    private readonly Func<Edition> _edition;
    private StorageInventory? _inv;
    private AppSettings _settings = new();
    private string? _customer;
    private string? _notes;
    private string? _status;
    private IReadOnlyList<BitLockerVolume> _bitlocker = Array.Empty<BitLockerVolume>();
    private StorageSpacesInfo? _spaces;

    public TechnicianPageViewModel(ISystemInfoProvider info, IShellService shell, Func<IReadOnlyDictionary<int, DiskHealthReport>> health, Func<Edition> edition)
    {
        _info = info;
        _shell = shell;
        _health = health;
        _edition = edition;
        LoadCommand = new AsyncRelayCommand(LoadAsync, ex => Status = ErrorText(ex));
        ExportHtmlCommand = new RelayCommand(() => Export("html"));
        ExportCsvCommand = new RelayCommand(() => Export("csv"));
        ExportJsonCommand = new RelayCommand(() => Export("json"));
    }

    public override string TitleKey => "Nav.Technician";
    public ObservableCollection<BitLockerRow> BitLocker { get; } = new();
    public ObservableCollection<SpaceRow> Spaces { get; } = new();
    public AsyncRelayCommand LoadCommand { get; }
    public RelayCommand ExportHtmlCommand { get; }
    public RelayCommand ExportCsvCommand { get; }
    public RelayCommand ExportJsonCommand { get; }
    public bool BrandingAllowed => Licenses.AllowsReportBranding(_edition());
    public string BrandingNote => BrandingAllowed ? "" : L("Tech.BrandingNote");
    public string? Technician { get => _settings.TechnicianName; set { _settings.TechnicianName = value; OnPropertyChanged(); } }
    public string? Customer { get => _customer; set => Set(ref _customer, value); }
    public string? Notes { get => _notes; set => Set(ref _notes, value); }
    public string? Status { get => _status; private set { Set(ref _status, value); OnPropertyChanged(nameof(HasStatus)); } }
    public bool HasStatus => !string.IsNullOrEmpty(_status);

    private async Task LoadAsync()
    {
        _bitlocker = await _info.BitLockerAsync();
        _spaces = await _info.StorageSpacesAsync();
        BitLocker.Clear();
        foreach (var b in _bitlocker)
            BitLocker.Add(new BitLockerRow(b.DriveLetter is { } l ? $"{l}:" : b.VolumeGuidPath, L("Tech.BL." + b.Protection),
                $"{b.Conversion}{(b.PercentEncrypted is { } p ? $" · {p} %" : "")}{(b.Method is { } m ? $" · {m}" : "")}{(b.Locked == true ? " · " + L("Tech.Locked") : "")}"));
        if (BitLocker.Count == 0) BitLocker.Add(new BitLockerRow("—", L("Tech.BL.Unknown"), L("Health.NeedAdmin")));
        Spaces.Clear();
        foreach (var p in _spaces.Pools) Spaces.Add(new SpaceRow(p.Name, $"{L("Tech.Pool")} · {Size(p.Allocated)} / {Size(p.Size)} · {p.PhysicalDisks} {L("Prop.Disk")}", p.Health));
        foreach (var v in _spaces.Spaces) Spaces.Add(new SpaceRow(v.Name, $"{v.Resiliency} · {Size(v.Size)} · {v.Operational}", v.Health));
        if (Spaces.Count == 0) Spaces.Add(new SpaceRow(L("Tech.NoSpaces"), "", ""));
    }

    private void Export(string kind)
    {
        if (_inv is null) return;
        var path = _shell.PickSaveFile($"Lasteast-{Environment.MachineName}-{DateTime.Now:yyyyMMdd}.{kind}", $"{kind.ToUpperInvariant()}|*.{kind}");
        if (path is null) return;
        var input = new ReportInput
        {
            Inventory = _inv, Health = _health(), BitLocker = _bitlocker, Spaces = _spaces, MaskSerials = _settings.MaskSerialNumbers,
            Technician = BrandingAllowed ? _settings.TechnicianName : null, Customer = BrandingAllowed ? _customer : null, Notes = _notes,
        };
        File.WriteAllText(path, kind switch { "html" => TechnicianReport.Html(input), "csv" => TechnicianReport.Csv(input), _ => TechnicianReport.Json(input) });
        Status = Loc.Instance.Format("Tech.Saved", path);
        _shell.RevealFile(path);
    }

    public override void Refresh(AppState state)
    {
        base.Refresh(state);
        _inv = state.Inventory;
        _settings = state.Settings;
        OnPropertyChanged(nameof(Technician));
        OnPropertyChanged(nameof(BrandingAllowed));
        OnPropertyChanged(nameof(BrandingNote));
    }
}

public sealed record GoalChoice(Goal Goal, string Title, RelayCommand Command);

/// <summary>Assistant: proposes validated plans; never applies anything (the user does, in Pending).</summary>
public sealed class AssistantPageViewModel : PageViewModel
{
    private readonly OperationQueue _queue;
    private readonly Action _openPending;
    private readonly Action<string> _navigate;
    private readonly Func<ILlmClient?> _llm;
    private StorageInventory? _inv;
    private string _request = "";
    private AssistantPlan? _plan;
    private string? _status;

    public AssistantPageViewModel(OperationQueue queue, Action openPending, Action<string> navigate, Func<ILlmClient?> llm)
    {
        _queue = queue;
        _openPending = openPending;
        _navigate = navigate;
        _llm = llm;
        foreach (var g in Enum.GetValues<Goal>())
            Goals.Add(new GoalChoice(g, "", new RelayCommand(() => Show(g))));
        SuggestCommand = new RelayCommand(() =>
        {
            if (IntentMatcher.Match(_request) is { } g) Show(g);
            else Status = L(_llm() is null ? "Asst.NotUnderstood" : "Asst.NotUnderstoodAi");
        });
        AskAiCommand = new AsyncRelayCommand(AskAiAsync, ex => Status = ErrorText(ex));
        QueueCommand = new RelayCommand(() =>
        {
            if (_plan is null) return;
            foreach (var op in _plan.Operations) if (_queue.TryAdd(op).Any(i => i.IsError)) { Status = L("Asst.Stale"); return; }
            _openPending();
        }, () => _plan?.Operations.Count > 0);
        OpenPageCommand = new RelayCommand(() => { if (_plan?.NavigateTo is { } n) _navigate(n); }, () => _plan?.NavigateTo is not null);
    }

    public override string TitleKey => "Nav.Assistant";
    public ObservableCollection<GoalChoice> Goals { get; } = new();
    public ObservableCollection<string> PlanSteps { get; } = new();
    public ObservableCollection<IssueViewModel> Issues { get; } = new();
    public RelayCommand SuggestCommand { get; }
    public AsyncRelayCommand AskAiCommand { get; }
    public RelayCommand QueueCommand { get; }
    public RelayCommand OpenPageCommand { get; }
    public string Request { get => _request; set => Set(ref _request, value); }
    public bool AiAvailable => _llm() is not null;
    public string? Explanation { get; private set; }
    public bool HasPlan => _plan is not null;
    public string? Status { get => _status; private set { Set(ref _status, value); OnPropertyChanged(nameof(HasStatus)); } }
    public bool HasStatus => !string.IsNullOrEmpty(_status);

    private void Show(Goal g)
    {
        if (_inv is null) return;
        Present(AssistantPlanner.Plan(g, _inv, _queue));
    }

    private void Present(AssistantPlan plan)
    {
        _plan = plan;
        Status = null;
        Explanation = Loc.Instance.Format(plan.ExplanationKey, plan.Args.Select(a => a is ulong u ? (object)Size(u) : a).ToArray())
                      + (plan.ModelText is { Length: > 0 } t ? "\n\n" + t : "");
        PlanSteps.Clear();
        foreach (var op in plan.Operations) PlanSteps.Add(OperationText.Describe(op, _inv));
        Issues.Clear();
        foreach (var i in plan.Issues) Issues.Add(new IssueViewModel(OperationText.Issue(i), i.IsError));
        OnPropertyChanged(nameof(Explanation));
        OnPropertyChanged(nameof(HasPlan));
        QueueCommand.RaiseCanExecuteChanged();
        OpenPageCommand.RaiseCanExecuteChanged();
    }

    private async Task AskAiAsync()
    {
        var client = _llm();
        if (client is null || _inv is null || string.IsNullOrWhiteSpace(_request)) return;
        Status = L("Asst.Thinking");
        Present(await LlmPlanner.PlanAsync(client, _request, _inv, _queue, CancellationToken.None));
    }

    public override void Refresh(AppState state)
    {
        base.Refresh(state);
        _inv = state.Inventory;
        var goals = Goals.ToList();
        Goals.Clear();
        foreach (var g in goals) Goals.Add(g with { Title = L("Asst.Goal." + g.Goal) });
        OnPropertyChanged(nameof(AiAvailable));
    }
}

public sealed record ToolLink(string Title, string Description, string Glyph, RelayCommand Command);

/// <summary>Tools hub: shortcuts to every tool page plus the command-line reference.</summary>
public sealed class ToolsPageViewModel : PageViewModel
{
    private readonly Action<string> _navigate;
    private static readonly (string Nav, string Glyph)[] Items =
    {
        ("Nav.Assistant", ""), ("Nav.StorageAnalyzer", ""), ("Nav.Cleanup", ""), ("Nav.DiskHealth", ""),
        ("Nav.PerformanceLab", ""), ("Nav.VirtualDisks", ""), ("Nav.FileOperations", ""), ("Nav.Technician", ""),
        ("Nav.Backup", ""), ("Nav.BootRecovery", ""),
    };

    public ToolsPageViewModel(Action<string> navigate) => _navigate = navigate;
    public override string TitleKey => "Nav.Tools";
    public ObservableCollection<ToolLink> Links { get; } = new();
    public string CliHelp => L("Tools.Cli");

    public override void Refresh(AppState state)
    {
        base.Refresh(state);
        Links.Clear();
        foreach (var (nav, glyph) in Items)
            Links.Add(new ToolLink(L(nav), L(nav.Replace("Nav.", "Planned.Desc.")), glyph, new RelayCommand(() => _navigate(nav))));
        OnPropertyChanged(nameof(CliHelp));
    }
}
