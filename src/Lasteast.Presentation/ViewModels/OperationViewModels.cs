using System.Collections.ObjectModel;
using System.Globalization;
using Lasteast.Core.Localization;
using Lasteast.Core.Models;
using Lasteast.Core.Operations;
using Lasteast.Core.Settings;
using Lasteast.Presentation.Mvvm;
using static Lasteast.Presentation.ViewModels.Text;

namespace Lasteast.Presentation.ViewModels;

/// <summary>Human-readable, localized descriptions of operations and issues.</summary>
public static class OperationText
{
    public static string Issue(ValidationIssue i) => Loc.Instance.Format(i.Key, i.Args.Select(a => a is ulong u ? (object)Size(u) : a).ToArray());

    public static string Describe(DiskOperation op, StorageInventory? inv)
    {
        var disk = $"{L("Common.Disk")} {op.DiskNumber}";
        string Target(ulong offset)
        {
            var p = inv?.Disks.FirstOrDefault(d => d.Number == op.DiskNumber)?.Partitions.FirstOrDefault(x => x.OffsetBytes == offset);
            return p is null ? $"@{Size(offset)}" : PartitionTitle(p) + (p.Volume?.Label is { Length: > 0 } l ? $" \"{l}\"" : "");
        }
        return op switch
        {
            CreatePartitionOp c => Loc.Instance.Format("OpText.Create", Size(c.SizeBytes), c.FileSystem,
                c.DriveLetter is { } l ? $"{l}:" : L("OpText.NoLetter"), disk),
            DeletePartitionOp d => Loc.Instance.Format("OpText.Delete", Target(d.OffsetBytes), disk),
            FormatPartitionOp f => Loc.Instance.Format("OpText.Format", Target(f.OffsetBytes), f.FileSystem, disk),
            ResizePartitionOp r => Loc.Instance.Format("OpText.Resize", Target(r.OffsetBytes), Size(r.NewSizeBytes), disk),
            SetLabelOp s => Loc.Instance.Format("OpText.Label", Target(s.OffsetBytes), s.Label, disk),
            ConvertStyleOp c => Loc.Instance.Format("OpText.Convert", disk, c.Target == PartitionStyle.Gpt ? "GPT" : "MBR"),
            MovePartitionOp m => Loc.Instance.Format("OpText.Move", Target(m.OffsetBytes), Size(m.NewOffsetBytes), disk),
            CloneDiskOp k => Loc.Instance.Format("OpText.Clone", $"{L("Common.Disk")} {k.SourceDiskNumber}", disk,
                L(k.Mode == Lasteast.Core.RawDisk.CloneMode.UsedSpace ? "Clone.Mode.UsedSpace" : "Clone.Mode.SectorBySector")),
            SetDriveLetterOp s => s.Letter is { } nl
                ? Loc.Instance.Format("OpText.Letter", Target(s.OffsetBytes), $"{nl}:", disk)
                : Loc.Instance.Format("OpText.RemoveLetter", Target(s.OffsetBytes), disk),
            _ => op.ToString(),
        };
    }
}

public sealed record IssueViewModel(string Text, bool IsError);

public sealed record ActionItem(string Title, string Glyph, bool IsDestructive, RelayCommand Command);

public enum EditorMode { Create, Resize, Format, Label, Letter, Delete, Move, ConvertToGpt, ConvertToMbr }

/// <summary>
/// Inline editor for one operation on the selected segment. Every change re-validates against the
/// queue (same simulator the engine uses), so the user sees errors before adding anything.
/// </summary>
public sealed class OperationEditorViewModel : ObservableObject
{
    private readonly OperationQueue _queue;
    private readonly SegmentViewModel _segment;
    private readonly Action _close;
    private string _sizeText = "";
    private string _fileSystem = "NTFS";
    private string _label = "";
    private string _letter = "";
    private string? _addedText;
    private string _offsetText = "";

    public OperationEditorViewModel(EditorMode mode, SegmentViewModel segment, OperationQueue queue, Action close)
    {
        Mode = mode;
        _segment = segment;
        _queue = queue;
        _close = close;
        var p = segment.Partition;
        var disk = segment.Disk.Disk;

        switch (mode)
        {
            case EditorMode.Create:
                MinBytes = LayoutSimulator.MinimumPartitionBytes;
                MaxBytes = segment.SizeBytes / LayoutSimulator.MiB * LayoutSimulator.MiB - AlignPad(segment.Segment.OffsetBytes);
                _sizeText = Gib(MaxBytes);
                _letter = FirstFreeLetter()?.ToString() ?? "";
                break;
            case EditorMode.Resize:
                MinBytes = LayoutSimulator.MinShrinkSize(p!);
                MaxBytes = LayoutSimulator.MaxExtendSize(disk, p!);
                _sizeText = Gib(p!.SizeBytes);
                break;
            case EditorMode.Format:
                _fileSystem = FileSystems.Supported.FirstOrDefault(f => f.Equals(p?.Volume?.FileSystem, StringComparison.OrdinalIgnoreCase)) ?? "NTFS";
                _label = p?.Volume?.Label ?? "";
                break;
            case EditorMode.Label:
                _label = p?.Volume?.Label ?? "";
                break;
            case EditorMode.Letter:
                _letter = p?.Volume?.DriveLetter?.ToString() ?? "";
                break;
            case EditorMode.Move:
                (MinBytes, MaxBytes) = MoveRange(disk, p!);
                // Default: the other end of the available range (the usual "move to make room" direction).
                _offsetText = Mib(p!.OffsetBytes == MaxBytes ? MinBytes : MaxBytes);
                break;
        }
        MoveToStartCommand = new RelayCommand(() => OffsetText = Mib(MinBytes));
        MoveToEndCommand = new RelayCommand(() => OffsetText = Mib(MaxBytes));

        AddCommand = new RelayCommand(Add, () => BuildOperation() is not null && Issues.All(i => !i.IsError));
        CancelCommand = new RelayCommand(_close);
        Revalidate();
    }

    public EditorMode Mode { get; }
    public string Title => L("Action." + Mode);
    public bool ShowSize => Mode is EditorMode.Create or EditorMode.Resize;
    public bool ShowFileSystem => Mode is EditorMode.Create or EditorMode.Format;
    public bool ShowLabel => Mode is EditorMode.Create or EditorMode.Format or EditorMode.Label;
    public bool ShowLetter => Mode is EditorMode.Create or EditorMode.Letter;
    public bool ShowOffset => Mode is EditorMode.Move;
    public bool IsDestructive => Mode is EditorMode.Delete or EditorMode.Format;
    public string OffsetText { get => _offsetText; set { if (Set(ref _offsetText, value)) Revalidate(); } }
    public RelayCommand MoveToStartCommand { get; }
    public RelayCommand MoveToEndCommand { get; }
    public string OffsetRangeText => ShowOffset ? Loc.Instance.Format("Editor.OffsetRange", Mib(MinBytes), Mib(MaxBytes)) : "";

    /// <summary>Lowest and highest start offsets (1 MiB aligned) the partition can move to.</summary>
    public static (ulong Min, ulong Max) MoveRange(DiskInfo disk, PartitionInfo p)
    {
        const ulong MiB = LayoutSimulator.MiB;
        ulong low = disk.Style == PartitionStyle.Gpt ? Math.Max(disk.UsableStartBytes, LayoutSimulator.GptHeadBytes(disk)) : MiB;
        low = Math.Max(low, MiB);
        ulong prevEnd = disk.Partitions.Where(x => !ReferenceEquals(x, p) && x.Role != PartitionRole.MbrExtended && x.EndBytes <= p.OffsetBytes)
            .Select(x => x.EndBytes).DefaultIfEmpty(low).Max();
        ulong min = (Math.Max(prevEnd, low) + MiB - 1) / MiB * MiB;
        ulong next = LayoutSimulator.NextBoundary(disk, p);
        ulong max = next >= p.SizeBytes ? (next - p.SizeBytes) / MiB * MiB : p.OffsetBytes;
        return (Math.Min(min, p.OffsetBytes), Math.Max(max, p.OffsetBytes));
    }

    private static string Mib(ulong bytes) => (bytes / LayoutSimulator.MiB).ToString(CultureInfo.CurrentCulture);
    public ulong MinBytes { get; }
    public ulong MaxBytes { get; }
    public string RangeText => ShowSize ? Loc.Instance.Format("Editor.Range", Size(MinBytes), Size(Math.Max(MinBytes, MaxBytes))) : "";
    public string TargetText => _segment.Partition is { } p ? $"{_segment.Title} — {_segment.SizeText} {_segment.FileSystemText}" : $"{L("Seg.Unallocated")} — {_segment.SizeText}";

    public IReadOnlyList<string> FileSystemOptions => FileSystems.Supported;
    public IReadOnlyList<string> LetterOptions { get; } =
        new[] { "" }.Concat(Enumerable.Range('C', 24).Select(c => ((char)c).ToString())).ToList();

    public ObservableCollection<IssueViewModel> Issues { get; } = new();
    public RelayCommand AddCommand { get; }
    public RelayCommand CancelCommand { get; }

    public string SizeText { get => _sizeText; set { if (Set(ref _sizeText, value)) Revalidate(); } }
    public string FileSystem { get => _fileSystem; set { if (Set(ref _fileSystem, value)) Revalidate(); } }
    public string Label { get => _label; set { if (Set(ref _label, value)) Revalidate(); } }
    public string Letter { get => _letter; set { if (Set(ref _letter, value)) Revalidate(); } }
    public string? AddedText { get => _addedText; private set => Set(ref _addedText, value); }

    /// <summary>Creates must start on a 1 MiB boundary.</summary>
    private static ulong AlignPad(ulong offset) => (LayoutSimulator.MiB - offset % LayoutSimulator.MiB) % LayoutSimulator.MiB;

    private static string Gib(ulong bytes) => (bytes / (1024.0 * 1024 * 1024)).ToString("0.##", CultureInfo.CurrentCulture);

    private ulong? ParseSize()
    {
        if (!double.TryParse(_sizeText, NumberStyles.Float, CultureInfo.CurrentCulture, out var gib) &&
            !double.TryParse(_sizeText, NumberStyles.Float, CultureInfo.InvariantCulture, out gib)) return null;
        if (gib <= 0 || double.IsNaN(gib) || gib > 1e7) return null;
        var bytes = (ulong)(gib * 1024 * 1024 * 1024);
        return bytes / LayoutSimulator.MiB * LayoutSimulator.MiB; // round down to 1 MiB
    }

    private char? FirstFreeLetter()
    {
        var used = _queue.BaseInventory?.Disks.SelectMany(d => d.Partitions).Select(p => p.Volume?.DriveLetter).OfType<char>().ToHashSet() ?? new();
        for (char c = 'D'; c <= 'Z'; c++) if (!used.Contains(c)) return c;
        return null;
    }

    public DiskOperation? BuildOperation()
    {
        var disk = _segment.Disk.Number;
        var p = _segment.Partition;
        char? letter = string.IsNullOrWhiteSpace(_letter) ? null : char.ToUpperInvariant(_letter.Trim()[0]);
        string? label = string.IsNullOrWhiteSpace(_label) ? null : _label.Trim();
        switch (Mode)
        {
            case EditorMode.Create:
                if (ParseSize() is not { } size) return null;
                var start = _segment.Segment.OffsetBytes + AlignPad(_segment.Segment.OffsetBytes);
                return new CreatePartitionOp(disk, start, size, _fileSystem, label, letter);
            case EditorMode.Resize:
                return ParseSize() is { } ns ? new ResizePartitionOp(disk, p!.OffsetBytes, ns) : null;
            case EditorMode.Format: return new FormatPartitionOp(disk, p!.OffsetBytes, _fileSystem, label);
            case EditorMode.Label: return new SetLabelOp(disk, p!.OffsetBytes, _label.Trim());
            case EditorMode.Letter: return new SetDriveLetterOp(disk, p!.OffsetBytes, letter);
            case EditorMode.Delete: return new DeletePartitionOp(disk, p!.OffsetBytes);
            case EditorMode.Move:
                return ulong.TryParse(_offsetText.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out var mib) && mib < ulong.MaxValue / LayoutSimulator.MiB
                    ? new MovePartitionOp(disk, p!.OffsetBytes, mib * LayoutSimulator.MiB, p.SizeBytes) : null;
            case EditorMode.ConvertToGpt: return new ConvertStyleOp(disk, PartitionStyle.Gpt);
            case EditorMode.ConvertToMbr: return new ConvertStyleOp(disk, PartitionStyle.Mbr);
        }
        return null;
    }

    private void Revalidate()
    {
        Issues.Clear();
        var op = BuildOperation();
        if (op is null)
            Issues.Add(new IssueViewModel(L(Mode == EditorMode.Move ? "Editor.InvalidOffset" : "Editor.InvalidSize"), true));
        else
            foreach (var i in _queue.Preview(op)) Issues.Add(new IssueViewModel(OperationText.Issue(i), i.IsError));
        AddCommand?.RaiseCanExecuteChanged();
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
        _close();
    }
}

// ---------------------------------------------------------------------------------------------

public sealed record PendingItem(int Index, string Text, string DiskText, bool DestroysData, RelayCommand RemoveCommand);

public sealed record DiskPreview(string Title, DiskViewModel Before, DiskViewModel After, string Summary, IReadOnlyList<IssueViewModel> Issues, bool DestroysData, string IdentityText);

public sealed record ProgressLine(string Text, string State);

/// <summary>Pending operations: queue, before/after preview, confirmation, execution and report.</summary>
public sealed class PendingPageViewModel : PageViewModel
{
    private readonly OperationQueue _queue;
    private readonly GuardedExecutionEngine? _engine;
    private readonly Func<Task> _refresh;
    private AppSettings _settings = new();
    private string _confirmation = "";
    private bool _ack;
    private bool _running;
    private string? _report;
    private bool _reportOk;
    private readonly Core.Abstractions.IPrivilegeService _privileges;

    public PendingPageViewModel(OperationQueue queue, GuardedExecutionEngine? engine, Core.Abstractions.IPrivilegeService privileges, Func<Task> refresh)
    {
        _queue = queue;
        _engine = engine;
        _privileges = privileges;
        _refresh = refresh;
        ApplyCommand = new AsyncRelayCommand(ApplyAsync, ex => { Report = ex.Message; ReportOk = false; Running = false; });
        UndoCommand = new RelayCommand(() => _queue.UndoLast(), () => _queue.Count > 0 && !_running);
        ClearCommand = new RelayCommand(() => _queue.Clear(), () => _queue.Count > 0 && !_running);
        DismissReportCommand = new RelayCommand(() => Report = null);
        _queue.Changed += (_, _) => Rebuild();
    }

    public override string TitleKey => "Nav.Pending";

    public ObservableCollection<PendingItem> Items { get; } = new();
    public ObservableCollection<DiskPreview> Previews { get; } = new();
    public ObservableCollection<ProgressLine> ProgressLines { get; } = new();
    public AsyncRelayCommand ApplyCommand { get; }
    public RelayCommand UndoCommand { get; }
    public RelayCommand ClearCommand { get; }
    public RelayCommand DismissReportCommand { get; }

    public bool IsEmpty => _queue.Count == 0;
    public bool HasItems => _queue.Count > 0;
    public string ConfirmationWord => GuardedExecutionEngine.ConfirmationWord;
    public string ConfirmationPrompt => Loc.Instance.Format("Pending.TypeToConfirm", ConfirmationWord);
    public string EstimateText => Loc.Instance.Format("Pending.Estimate", Math.Max(1, (int)Math.Ceiling(_queue.Items.Sum(o => o.EstimatedSeconds) / 60.0)));

    public string Confirmation { get => _confirmation; set { if (Set(ref _confirmation, value)) OnPropertyChanged(nameof(CanApply)); } }
    public bool BackupAcknowledged { get => _ack; set { if (Set(ref _ack, value)) OnPropertyChanged(nameof(CanApply)); } }
    public bool Running { get => _running; private set { if (Set(ref _running, value)) { OnPropertyChanged(nameof(CanApply)); UndoCommand.RaiseCanExecuteChanged(); ClearCommand.RaiseCanExecuteChanged(); } } }
    public string? Report { get => _report; private set { Set(ref _report, value); OnPropertyChanged(nameof(HasReport)); } }
    public bool ReportOk { get => _reportOk; private set => Set(ref _reportOk, value); }
    public bool HasReport => !string.IsNullOrEmpty(_report);

    /// <summary>Why Apply is disabled (null when it can run). Shown next to the button.</summary>
    public string? BlockReason
    {
        get
        {
            if (_queue.Count == 0) return null;
            if (_queue.BaseInventory?.IsSampleData == true) return L("Op.Error.SampleData");
            if (!_queue.IsValid) return L("Pending.Block.Invalid");
            if (!_privileges.IsElevated) return L("Exec.Error.NotElevated");
            var physical = _queue.Items.Select(o => o.DiskNumber).Distinct()
                .Select(n => _queue.BaseInventory?.Disks.FirstOrDefault(d => d.Number == n)).OfType<DiskInfo>().Where(d => !d.IsVirtual).ToList();
            if (physical.Count > 0 && !_settings.AllowPhysicalDiskOperations)
                return Loc.Instance.Format("Exec.Error.PhysicalPolicy", physical[0].Number);
            if (_engine is null) return L("Pending.Block.NoEngine");
            return null;
        }
    }

    public bool CanApply => !_running && BlockReason is null && _queue.Count > 0 && _ack
                            && string.Equals(_confirmation.Trim(), ConfirmationWord, StringComparison.Ordinal);

    public override void Refresh(AppState state)
    {
        base.Refresh(state);
        _settings = state.Settings;
        Rebuild();
    }

    private void Rebuild()
    {
        var inv = _queue.BaseInventory;
        Items.Clear();
        for (int i = 0; i < _queue.Items.Count; i++)
        {
            var op = _queue.Items[i];
            int index = i;
            Items.Add(new PendingItem(i + 1, OperationText.Describe(op, inv), $"{L("Common.Disk")} {op.DiskNumber}", op.DestroysData,
                new RelayCommand(() => _queue.RemoveAt(index), () => !_running)));
        }

        Previews.Clear();
        foreach (var sim in _queue.SimulateAll())
        {
            var issues = sim.Steps.SelectMany((s, i) => s.Issues.Select(x => new IssueViewModel($"{i + 1}. {OperationText.Issue(x)}", x.IsError))).ToList();
            var d = sim.Before;
            var summary = Loc.Instance.Format(sim.DestroysData ? "Pending.SummaryLoss" : "Pending.Summary", sim.Steps.Count,
                Math.Max(1, (int)Math.Ceiling(sim.EstimatedSeconds / 60.0)));
            var identity = $"{L("Common.Disk")} {d.Number} · {d.Model} · {Size(d.SizeBytes)} · {L("Prop.Serial")}: {MaskSerial(d.SerialNumber, _settings.MaskSerialNumbers)}"
                           + (d.IsVirtual ? $" · {L("Badge.Virtual")}" : "") + (d.IsWindowsDisk ? $" · {L("Badge.Windows")}" : "");
            Previews.Add(new DiskPreview($"{L("Common.Disk")} {d.Number} — {d.Model}",
                new DiskViewModel(d, _settings.MaskSerialNumbers), new DiskViewModel(sim.After, _settings.MaskSerialNumbers),
                summary, issues, sim.DestroysData, identity));
        }

        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(EstimateText));
        OnPropertyChanged(nameof(BlockReason));
        OnPropertyChanged(nameof(CanApply));
        OnPropertyChanged(nameof(ConfirmationPrompt));
        UndoCommand.RaiseCanExecuteChanged();
        ClearCommand.RaiseCanExecuteChanged();
    }

    private async Task ApplyAsync()
    {
        if (!CanApply || _engine is null || _queue.BaseInventory is null) return;
        Running = true;
        Report = null;
        ProgressLines.Clear();
        var ops = _queue.Items.ToList();
        var inv = _queue.BaseInventory;
        foreach (var op in ops) ProgressLines.Add(new ProgressLine(OperationText.Describe(op, inv), "Pending"));

        var progress = new Progress<ExecutionProgress>(e =>
        {
            if (e.StepIndex < ProgressLines.Count)
                ProgressLines[e.StepIndex] = ProgressLines[e.StepIndex] with { State = e.State.ToString() };
        });

        var report = await _engine.ExecuteAsync(ops, inv, new ExecutionPolicy(_settings.AllowPhysicalDiskOperations), _confirmation, progress);

        var done = report.Steps.Where(s => s.State == StepState.Succeeded).Select(s => s.Operation).ToList();
        _queue.RemoveRange(done);
        ReportOk = report.Succeeded;
        var backup = report.Backups.Count > 0
            ? Environment.NewLine + Loc.Instance.Format("Pending.BackupAt", string.Join(", ", report.Backups.Select(b => b.Folder)))
              + (report.Backups.Any(b => b.StoredOnSameDisk) ? Environment.NewLine + L("Pending.BackupSameDisk") : "")
            : "";
        Report = report.Succeeded
            ? Loc.Instance.Format("Pending.Done", done.Count) + backup
            : Loc.Instance.Format(report.NothingWritten ? "Pending.FailedNothing" : "Pending.FailedPartial",
                  Loc.Instance.Format(report.FailureKey ?? "Exec.Error.Step", report.FailureArgs), report.FailureDetail ?? "", done.Count)
              + backup + (report.NothingWritten ? "" : Environment.NewLine + L("Pending.RecoveryHint"));

        Confirmation = "";
        BackupAcknowledged = false;
        Running = false;
        await _refresh();
    }
}
