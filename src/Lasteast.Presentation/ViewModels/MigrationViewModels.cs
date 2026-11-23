using System.Collections.ObjectModel;
using Lasteast.Core.Localization;
using Lasteast.Core.Models;
using Lasteast.Core.Operations;
using Lasteast.Core.RawDisk;
using Lasteast.Presentation.Mvvm;
using static Lasteast.Presentation.ViewModels.Text;

namespace Lasteast.Presentation.ViewModels;

public sealed record DiskChoice(int Number, string Title)
{
    public override string ToString() => Title;
}

public sealed class PartitionChoice : ObservableObject
{
    private bool _isSelected = true;
    private readonly Action _changed;

    public PartitionChoice(PartitionInfo p, Action changed)
    {
        Partition = p;
        _changed = changed;
        Title = PartitionTitle(p) + (p.Volume?.Label is { Length: > 0 } l ? $" \"{l}\"" : "");
        Detail = $"{Size(p.SizeBytes)} · {p.Volume?.FileSystem ?? L("Common.NotAvailable")}"
                 + (p.Volume?.UsedBytes is { } u ? $" · {L("Prop.Used")} {Size(u)}" : "");
    }

    public PartitionInfo Partition { get; }
    public string Title { get; }
    public string Detail { get; }
    public bool IsSelected { get => _isSelected; set { if (Set(ref _isSelected, value)) _changed(); } }
}

/// <summary>
/// Disk clone wizard (Module 11): pick source and destination, see the compatibility check (the same simulator the
/// engine runs), then queue a <see cref="CloneDiskOp"/>. Nothing is written until the user applies the queue.
/// </summary>
public sealed class ClonePageViewModel : PageViewModel
{
    private readonly OperationQueue _queue;
    private readonly Action _openPending;
    private StorageInventory? _inv;
    private DiskChoice? _source;
    private DiskChoice? _dest;
    private bool _usedSpace = true;
    private bool _newIds = true;
    private bool _verify = true;
    private string? _addedText;

    public ClonePageViewModel(OperationQueue queue, Action openPending)
    {
        _queue = queue;
        _openPending = openPending;
        AddCommand = new RelayCommand(Add, () => BuildOperation() is { } op && _queue.Preview(op).All(i => !i.IsError));
        SwapCommand = new RelayCommand(() => (Source, Destination) = (Destination, Source), () => _source is not null || _dest is not null);
    }

    public override string TitleKey => "Nav.DiskClone";

    public ObservableCollection<DiskChoice> Disks { get; } = new();
    public ObservableCollection<PartitionChoice> Partitions { get; } = new();
    public ObservableCollection<IssueViewModel> Issues { get; } = new();
    public RelayCommand AddCommand { get; }
    public RelayCommand SwapCommand { get; }

    public DiskChoice? Source
    {
        get => _source;
        set
        {
            if (!Set(ref _source, value)) return;
            Partitions.Clear();
            var d = SourceDisk;
            if (d is not null)
                foreach (var p in d.Partitions.Where(p => p.Role != PartitionRole.MbrExtended))
                    Partitions.Add(new PartitionChoice(p, Revalidate));
            Revalidate();
        }
    }

    public DiskChoice? Destination { get => _dest; set { if (Set(ref _dest, value)) Revalidate(); } }
    public bool UsedSpace { get => _usedSpace; set { if (Set(ref _usedSpace, value)) Revalidate(); } }
    public bool NewIdentifiers { get => _newIds; set { if (Set(ref _newIds, value)) Revalidate(); } }
    public bool Verify { get => _verify; set { if (Set(ref _verify, value)) Revalidate(); } }
    public string? AddedText { get => _addedText; private set { Set(ref _addedText, value); OnPropertyChanged(nameof(HasAdded)); } }
    public bool HasAdded => _addedText is not null;
    public bool HasPartitions => Partitions.Count > 0;

    private DiskInfo? SourceDisk => _inv?.Disks.FirstOrDefault(d => d.Number == _source?.Number);
    private DiskInfo? DestDisk => _inv?.Disks.FirstOrDefault(d => d.Number == _dest?.Number);

    public string SummaryText
    {
        get
        {
            var s = SourceDisk;
            var d = DestDisk;
            if (s is null || d is null) return L("Clone.PickBoth");
            return Loc.Instance.Format("Clone.Summary", Size(EstimateBytes()), Size(s.SizeBytes), Size(d.SizeBytes));
        }
    }

    private ulong EstimateBytes()
    {
        var selected = Partitions.Where(p => p.IsSelected).Select(p => p.Partition);
        return selected.Aggregate(0UL, (sum, p) => sum + (_usedSpace && p.Volume?.UsedBytes is { } u && string.Equals(p.Volume.FileSystem, "NTFS", StringComparison.OrdinalIgnoreCase) ? u : p.SizeBytes));
    }

    public CloneDiskOp? BuildOperation()
    {
        if (_source is null || _dest is null) return null;
        var selected = Partitions.Where(p => p.IsSelected).Select(p => p.Partition.OffsetBytes).ToList();
        if (selected.Count == 0) return null;
        IReadOnlyList<ulong>? only = selected.Count == Partitions.Count ? null : selected;
        return new CloneDiskOp(_dest.Number, _source.Number, _usedSpace ? CloneMode.UsedSpace : CloneMode.SectorBySector, _newIds, _verify, only, EstimateBytes());
    }

    private void Revalidate()
    {
        Issues.Clear();
        AddedText = null;
        var op = BuildOperation();
        if (op is not null)
            foreach (var i in _queue.Preview(op)) Issues.Add(new IssueViewModel(OperationText.Issue(i), i.IsError));
        else if (_source is not null && _dest is not null)
            Issues.Add(new IssueViewModel(L("Clone.Error.Nothing"), true));
        if (op is not null && Issues.All(i => !i.IsError)) Issues.Insert(0, new IssueViewModel(L("Clone.Compatible"), false));
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(HasPartitions));
        AddCommand.RaiseCanExecuteChanged();
        SwapCommand.RaiseCanExecuteChanged();
    }

    private void Add()
    {
        var op = BuildOperation();
        if (op is null) return;
        var result = _queue.TryAdd(op);
        if (result.Any(i => i.IsError))
        {
            Issues.Clear();
            foreach (var i in result) Issues.Add(new IssueViewModel(OperationText.Issue(i), i.IsError));
            return;
        }
        AddedText = L("Editor.Added");
        _openPending();
    }

    public override void Refresh(AppState state)
    {
        base.Refresh(state);
        _inv = state.Inventory;
        var (s, d) = (_source?.Number, _dest?.Number);
        Disks.Clear();
        foreach (var disk in _inv?.Disks ?? Array.Empty<DiskInfo>())
            Disks.Add(new DiskChoice(disk.Number, $"{L("Common.Disk")} {disk.Number} — {disk.Model} · {Size(disk.SizeBytes)}"
                + (disk.IsVirtual ? $" · {L("Badge.Virtual")}" : "") + (disk.IsWindowsDisk ? $" · {L("Badge.Windows")}" : "")));
        _source = null;
        _dest = null;
        Source = Disks.FirstOrDefault(x => x.Number == s);
        Destination = Disks.FirstOrDefault(x => x.Number == d);
        OnPropertyChanged(nameof(Source));
        OnPropertyChanged(nameof(Destination));
        Revalidate();
    }
}

public sealed record ConversionRow(DiskChoice Disk, string StyleText, string TargetText, IReadOnlyList<IssueViewModel> Issues, bool CanConvert, RelayCommand ConvertCommand);

/// <summary>Disk conversion page: MBR↔GPT without data loss for every disk, with the reasons a disk cannot be converted.</summary>
public sealed class ConversionPageViewModel : PageViewModel
{
    private readonly OperationQueue _queue;
    private readonly Action _openPending;
    private StorageInventory? _inv;

    public ConversionPageViewModel(OperationQueue queue, Action openPending)
    {
        _queue = queue;
        _openPending = openPending;
        _queue.Changed += (_, _) => Build();
    }

    public override string TitleKey => "Nav.DiskConversion";
    public ObservableCollection<ConversionRow> Rows { get; } = new();

    public override void Refresh(AppState state)
    {
        base.Refresh(state);
        _inv = state.Inventory;
        Build();
    }

    private void Build()
    {
        Rows.Clear();
        foreach (var d in _inv?.Disks ?? Array.Empty<DiskInfo>())
        {
            if (d.Style is not (PartitionStyle.Gpt or PartitionStyle.Mbr)) continue;
            var target = d.Style == PartitionStyle.Gpt ? PartitionStyle.Mbr : PartitionStyle.Gpt;
            var op = new ConvertStyleOp(d.Number, target);
            var issues = _queue.Preview(op).Select(i => new IssueViewModel(OperationText.Issue(i), i.IsError)).ToList();
            bool ok = issues.All(i => !i.IsError);
            var choice = new DiskChoice(d.Number, $"{L("Common.Disk")} {d.Number} — {d.Model} · {Size(d.SizeBytes)}");
            Rows.Add(new ConversionRow(choice, d.Style == PartitionStyle.Gpt ? "GPT" : "MBR",
                Loc.Instance.Format("Conv.ConvertTo", target == PartitionStyle.Gpt ? "GPT" : "MBR"), issues, ok,
                new RelayCommand(() => { if (_queue.TryAdd(op).All(i => !i.IsError)) _openPending(); }, () => ok)));
        }
    }
}

/// <summary>OS migration wizard: copy Windows to another disk (spec module "Migrate OS").</summary>
public sealed class OsMigrationPageViewModel : PageViewModel
{
    private readonly OperationQueue _queue;
    private readonly Action _openPending;
    private StorageInventory? _inv;
    private DiskChoice? _dest;
    private bool _replace = true;
    private Lasteast.Core.Migration.MigrationPlan? _plan;
    private string? _queuedText;

    public OsMigrationPageViewModel(OperationQueue queue, Action openPending)
    {
        _queue = queue;
        _openPending = openPending;
        QueuePreparationCommand = new RelayCommand(() => QueueOps(_plan?.Preparation), () => _plan?.Preparation.Count > 0);
        QueueMigrationCommand = new RelayCommand(() => QueueOps(_plan?.Migration), () => _plan?.Migration.Count > 0);
    }

    public override string TitleKey => "Nav.OsMigration";
    public ObservableCollection<DiskChoice> Disks { get; } = new();
    public ObservableCollection<string> SystemPartitions { get; } = new();
    public ObservableCollection<IssueViewModel> Issues { get; } = new();
    public RelayCommand QueuePreparationCommand { get; }
    public RelayCommand QueueMigrationCommand { get; }

    public DiskChoice? Destination { get => _dest; set { if (Set(ref _dest, value)) Replan(); } }
    public bool ReplaceOldDisk { get => _replace; set { if (Set(ref _replace, value)) { OnPropertyChanged(nameof(KeepBoth)); Replan(); } } }
    public bool KeepBoth { get => !_replace; set => ReplaceOldDisk = !value; }
    public string? QueuedText { get => _queuedText; private set { Set(ref _queuedText, value); OnPropertyChanged(nameof(HasQueued)); } }
    public bool HasQueued => _queuedText is not null;

    public string SourceText => _inv?.Disks.FirstOrDefault(d => d.IsWindowsDisk) is { } w
        ? $"{L("Common.Disk")} {w.Number} — {w.Model} · {Size(w.SizeBytes)}" : L("Mig.NoWindows");

    public string FitText => _plan?.Fit switch
    {
        null => L("Mig.PickDestination"),
        Lasteast.Core.Migration.MigrationFit.Fits => Loc.Instance.Format("Mig.Fit.Fits", Size(_plan.DataBytes), Size(_plan.RequiredBytes)),
        Lasteast.Core.Migration.MigrationFit.NeedsPreparation => Loc.Instance.Format("Mig.Fit.Prepare", Size(_plan.RequiredBytes), Size(_plan.ShrinkCTo ?? 0)),
        Lasteast.Core.Migration.MigrationFit.TooSmall => Loc.Instance.Format("Mig.Fit.TooSmall", Size(_plan.DataBytes)),
        Lasteast.Core.Migration.MigrationFit.NoWindowsDisk => L("Mig.NoWindows"),
        _ => L("Mig.Fit.Invalid"),
    };

    public bool NeedsPreparation => _plan?.Fit == Lasteast.Core.Migration.MigrationFit.NeedsPreparation;
    public bool CanMigrate => _plan?.Fit == Lasteast.Core.Migration.MigrationFit.Fits;
    public string NextStepsText => L(_replace ? "Mig.Next.Replace" : "Mig.Next.KeepBoth");

    private void QueueOps(IReadOnlyList<DiskOperation>? ops)
    {
        if (ops is null || ops.Count == 0) return;
        var added = new List<DiskOperation>();
        foreach (var op in ops)
        {
            var r = _queue.TryAdd(op);
            if (r.Any(i => i.IsError))
            {
                foreach (var a in added) _queue.RemoveRange(new[] { a });
                Issues.Clear();
                foreach (var i in r) Issues.Add(new IssueViewModel(OperationText.Issue(i), i.IsError));
                return;
            }
            added.Add(op);
        }
        QueuedText = L("Editor.Added");
        _openPending();
    }

    private void Replan()
    {
        QueuedText = null;
        Issues.Clear();
        SystemPartitions.Clear();
        _plan = _inv is null || _dest is null ? null
            : Lasteast.Core.Migration.OsMigrationPlanner.Plan(_inv, _dest.Number,
                _replace ? Lasteast.Core.Migration.MigrationTarget.ReplaceOldDisk : Lasteast.Core.Migration.MigrationTarget.KeepBoth);
        if (_plan is not null)
        {
            foreach (var p in _plan.SystemPartitions)
                SystemPartitions.Add($"{PartitionTitle(p)} · {Size(p.SizeBytes)}" + (p.Volume?.DriveLetter is { } l ? $" ({l}:)" : ""));
            foreach (var i in _plan.Issues) Issues.Add(new IssueViewModel(OperationText.Issue(i), i.IsError));
        }
        OnPropertyChanged(nameof(FitText));
        OnPropertyChanged(nameof(NeedsPreparation));
        OnPropertyChanged(nameof(CanMigrate));
        OnPropertyChanged(nameof(NextStepsText));
        QueuePreparationCommand.RaiseCanExecuteChanged();
        QueueMigrationCommand.RaiseCanExecuteChanged();
    }

    public override void Refresh(AppState state)
    {
        base.Refresh(state);
        _inv = state.Inventory;
        var keep = _dest?.Number;
        Disks.Clear();
        foreach (var d in _inv?.Disks.Where(d => !d.IsWindowsDisk) ?? Enumerable.Empty<DiskInfo>())
            Disks.Add(new DiskChoice(d.Number, $"{L("Common.Disk")} {d.Number} — {d.Model} · {Size(d.SizeBytes)}" + (d.IsVirtual ? $" · {L("Badge.Virtual")}" : "")));
        _dest = Disks.FirstOrDefault(x => x.Number == keep);
        OnPropertyChanged(nameof(Destination));
        OnPropertyChanged(nameof(SourceText));
        Replan();
    }
}
