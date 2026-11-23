using System.Collections.ObjectModel;
using System.Windows.Input;
using Lasteast.Core.Localization;
using Lasteast.Core.Models;
using Lasteast.Core.Operations;
using Lasteast.Core.SmartFix;
using Lasteast.Presentation.Mvvm;
using Lasteast.Presentation.Services;
using static Lasteast.Presentation.ViewModels.Text;

namespace Lasteast.Presentation.ViewModels;

public sealed class SmartFindingViewModel
{
    public SmartFindingViewModel(SmartFinding f, ICommand? fixCommand)
    {
        Finding = f;
        Title = Fmt(f.TitleKey, f.TitleArgs)!;
        Explanation = Fmt(f.ExplanationKey, f.ExplanationArgs);
        Recommendation = Fmt(f.RecommendationKey, f.RecommendationArgs);
        Benefit = Fmt(f.BenefitKey, f.BenefitArgs);
        SeverityKind = f.Severity.ToString();
        RiskText = f.Severity == SmartSeverity.Healthy ? "" : Loc.Instance.Format("SF.Risk", L("SF.Risk." + f.Risk));
        BackupText = f.BackupRecommended ? L("SF.BackupRecommended") : "";
        FixCommand = fixCommand;
        FixLabel = f.Fix switch
        {
            FixKind.QueueOperations => L("SF.FixNow"),
            FixKind.Elevate => L("Top.RunAsAdmin"),
            FixKind.Planned => Loc.Instance.Format("Planned.Phase", f.PlannedPhase ?? 0),
            FixKind.Navigate => Loc.Instance.Format("SF.Open", L(f.NavigateTo ?? "")),
            _ => "",
        };
    }

    public SmartFinding Finding { get; }
    public string Title { get; }
    public string? Explanation { get; }
    public string? Recommendation { get; }
    public string? Benefit { get; }
    public string SeverityKind { get; }
    public string RiskText { get; }
    public string BackupText { get; }
    public string FixLabel { get; }
    public ICommand? FixCommand { get; }
    public bool HasFixButton => FixCommand is not null;
    public bool HasPlannedLabel => Finding.Fix == FixKind.Planned;
    public bool IsHealthy => Finding.Severity == SmartSeverity.Healthy;

    private static string? Fmt(string? key, object[] args) => key is null ? null
        : Loc.Instance.Format(key, args.Select(a => a is ulong u ? (object)Size(u) : a).ToArray());
}

public sealed record SeverityGroup(string Title, string Kind, IReadOnlyList<SmartFindingViewModel> Items)
{
    public int Count => Items.Count;
}

/// <summary>
/// Smart Fix: SCAN MY COMPUTER → UNDERSTAND → RECOMMEND → PREVIEW → CONFIRM → EXECUTE → VERIFY.
/// This page covers the first three steps; "Fix now" puts operations in the pending queue, where the
/// existing preview/confirmation/engine take over. Nothing here changes a disk directly.
/// </summary>
public sealed class SmartFixPageViewModel : PageViewModel
{
    private readonly OperationQueue _queue;
    private readonly ISystemProbe _probe;
    private readonly Func<Task> _refreshInventory;
    private readonly Action _openPending;
    private readonly ICommand _elevate;
    private readonly Action<string>? _navigate;
    private StorageInventory? _inventory;
    private SystemSnapshot? _snapshot;
    private bool _scanning;
    private DateTimeOffset? _scannedAt;
    private string? _message;
    private CDrivePlan? _cplan;

    private readonly Core.Optimization.IVolumeOptimizer? _optimizer;
    private readonly IShellService? _shell;

    public SmartFixPageViewModel(OperationQueue queue, ISystemProbe probe, Func<Task> refreshInventory, Action openPending, ICommand elevate,
        Action<string>? navigate = null, Core.Optimization.IVolumeOptimizer? optimizer = null, IShellService? shell = null)
    {
        _optimizer = optimizer;
        _shell = shell;
        _navigate = navigate;
        _queue = queue;
        _probe = probe;
        _refreshInventory = refreshInventory;
        _openPending = openPending;
        _elevate = elevate;
        ScanCommand = new AsyncRelayCommand(ScanAsync, ex => { Message = ex.Message; Scanning = false; });
        ExpandCCommand = new RelayCommand(() => Queue(_cplan?.Operations ?? Array.Empty<DiskOperation>()),
            () => _cplan?.CanExpand == true);
    }

    public override string TitleKey => "Nav.SmartFix";
    public AsyncRelayCommand ScanCommand { get; }
    public RelayCommand ExpandCCommand { get; }
    public ObservableCollection<SeverityGroup> Groups { get; } = new();

    public bool Scanning { get => _scanning; private set { if (Set(ref _scanning, value)) OnPropertyChanged(nameof(ShowIntro)); } }
    public bool HasScanned => _scannedAt is not null;
    public bool ShowIntro => !HasScanned && !_scanning;
    public string? Message { get => _message; private set { Set(ref _message, value); OnPropertyChanged(nameof(HasMessage)); } }
    public bool HasMessage => !string.IsNullOrEmpty(_message);
    public string ScanSummary { get; private set; } = "";

    // ---- C: drive card
    public bool HasCDrive => _cplan?.Partition is not null;
    public bool CanExpandC => _cplan?.CanExpand == true;
    public string CDriveSize => _cplan?.Partition is { } p ? Size(p.SizeBytes) : "";
    public string CDriveFree => _cplan?.Partition?.Volume?.FreeBytes is { } f ? Size(f) : "";
    public double CDriveUsedFraction => _cplan?.Partition?.Volume is { CapacityBytes: > 0, UsedBytes: { } u } v ? (double)u / v.CapacityBytes!.Value : 0;
    public string CDriveStatus => _cplan is null ? "" : _cplan.Blocker switch
    {
        CDriveBlocker.None when _cplan.MoveFirst is not null => Loc.Instance.Format("SF.C.CanExpandAfterMove", Size(_cplan.AdjacentFreeBytes), Size(_cplan.MaxSizeBytes)),
        CDriveBlocker.None => Loc.Instance.Format("SF.C.CanExpand", Size(_cplan.AdjacentFreeBytes), Size(_cplan.MaxSizeBytes)),
        CDriveBlocker.PartitionInTheWay => Loc.Instance.Format("SF.C.Blocked",
            _cplan.BlockingPartition is { } b ? Role(b.Role) + (b.Volume?.DriveLetter is { } l ? $" ({l}:)" : "") : "?", Size(_cplan.OtherFreeBytesOnDisk)),
        CDriveBlocker.NoFreeSpace => L("SF.C.NoFree"),
        CDriveBlocker.NotResizable => L("SF.C.NotResizable"),
        CDriveBlocker.Unreadable => L("SF.C.Unreadable"),
        CDriveBlocker.DiskNotEditable => L("SF.C.NotEditable"),
        _ => L("SF.C.NotFound"),
    };

    private async Task ScanAsync()
    {
        Scanning = true;
        Message = null;
        await _refreshInventory();               // fresh disk inventory (MainViewModel pushes it back via Refresh)
        _snapshot = await _probe.CaptureAsync(); // read-only system measurements
        _scannedAt = DateTimeOffset.Now;
        Scanning = false;
        Rebuild();
    }

    public override void Refresh(AppState state)
    {
        base.Refresh(state);
        _inventory = state.Inventory;
        if (HasScanned) Rebuild();
    }

    private void Rebuild()
    {
        Groups.Clear();
        if (_inventory is null) return;
        var findings = SmartFixAnalyzer.Analyze(_inventory, _snapshot);
        _cplan = CDriveExpansion.Analyze(_inventory);

        foreach (var sev in Enum.GetValues<SmartSeverity>())
        {
            var items = findings.Where(f => f.Severity == sev).Select(f => new SmartFindingViewModel(f, CommandFor(f))).ToList();
            if (items.Count > 0) Groups.Add(new SeverityGroup(L("SF.Group." + sev), sev.ToString(), items));
        }

        var problems = findings.Count(f => f.Severity is not SmartSeverity.Healthy && f.Id != "health");
        ScanSummary = Loc.Instance.Format("SF.Summary", problems, findings.Count(f => f.Severity == SmartSeverity.Healthy),
            _scannedAt?.ToLocalTime().ToString("T", Loc.Instance.Language.Culture) ?? "");
        foreach (var n in new[] { nameof(HasScanned), nameof(ShowIntro), nameof(ScanSummary), nameof(HasCDrive), nameof(CanExpandC),
                     nameof(CDriveSize), nameof(CDriveFree), nameof(CDriveUsedFraction), nameof(CDriveStatus) })
            OnPropertyChanged(n);
        ExpandCCommand.RaiseCanExecuteChanged();
    }

    private ICommand? CommandFor(SmartFinding f) => f.Fix switch
    {
        FixKind.QueueOperations when f.Operations.Count > 0 => new RelayCommand(() => Queue(f.Operations)),
        FixKind.Elevate => _elevate,
        FixKind.Navigate when f.NavigateTo is { } key && _navigate is not null => new RelayCommand(() => _navigate(key)),
        FixKind.Optimize when f.OptimizeLetter is { } l && _optimizer is not null => new AsyncRelayCommand(() => OptimizeAsync(l, f.OptimizeAction), ex => Message = ErrorText(ex)),
        _ => null,
    };

    /// <summary>Defragments (HDD) or re-TRIMs (SSD) one volume after an explicit confirmation; nothing is deleted.</summary>
    private async Task OptimizeAsync(char letter, Core.Optimization.OptimizeAction action)
    {
        var key = action == Core.Optimization.OptimizeAction.Retrim ? "Opt.ConfirmTrim" : "Opt.ConfirmDefrag";
        if (_shell is not null && !_shell.Confirm(L("Opt.Title"), Loc.Instance.Format(key, $"{letter}:"))) return;
        Message = Loc.Instance.Format("Opt.Running", $"{letter}:");
        await _optimizer!.OptimizeAsync(letter, action);
        Message = Loc.Instance.Format("Opt.Done", $"{letter}:");
    }

    /// <summary>Adds the fix's operations to the pending queue, then opens the preview/confirmation page.</summary>
    private void Queue(IReadOnlyList<DiskOperation> ops)
    {
        if (ops.Count == 0) return;
        var errors = new List<string>();
        foreach (var op in ops)
        {
            if (_queue.Items.Contains(op)) continue; // already queued (records compare by value)
            var issues = _queue.TryAdd(op);
            errors.AddRange(issues.Where(i => i.IsError).Select(OperationText.Issue));
        }
        if (errors.Count > 0) { Message = string.Join(Environment.NewLine, errors); return; }
        Message = null;
        _openPending();
    }
}
