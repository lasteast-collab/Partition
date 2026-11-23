using System.Collections.ObjectModel;
using System.Globalization;
using Lasteast.Core.Layout;
using Lasteast.Core.Localization;
using Lasteast.Core.Models;
using Lasteast.Core.Operations;
using Lasteast.Core.Recovery;
using Lasteast.Presentation.Mvvm;
using static Lasteast.Presentation.ViewModels.Text;

namespace Lasteast.Presentation.ViewModels;

/// <summary>The editor modes beyond single-step editing: merge, split, copy, wipe, file-system tools, conversions.</summary>
public sealed partial class OperationEditorViewModel
{
    public ObservableCollection<ChoiceItem> ChoiceOptions { get; } = new();
    public ObservableCollection<ChoiceItem> Choice2Options { get; } = new();
    public ChoiceItem? SelectedChoice { get => _choice; set { if (Set(ref _choice, value)) Revalidate(); } }
    public ChoiceItem? SelectedChoice2 { get => _choice2; set { if (Set(ref _choice2, value)) Revalidate(); } }
    public string TextValue { get => _text; set { if (Set(ref _text, value)) Revalidate(); } }
    public bool CheckValue { get => _check; set { if (Set(ref _check, value)) Revalidate(); } }
    public string? ToolOutput { get => _toolOutput; private set { Set(ref _toolOutput, value); OnPropertyChanged(nameof(HasToolOutput)); } }
    public bool HasToolOutput => !string.IsNullOrEmpty(_toolOutput);
    public AsyncRelayCommand RunToolCommand { get; }
    public RelayCommand BrowseCommand { get; }

    public bool ShowChoice => ChoiceLabel is not null;
    public bool ShowChoice2 => Choice2Label is not null;
    public bool ShowText => TextLabel is not null;
    public bool ShowCheck => CheckLabel is not null;
    public bool ShowBrowse => Mode == EditorMode.Reformat;
    public string? ChoiceLabel { get; private set; }
    public string? Choice2Label { get; private set; }
    public string? TextLabel { get; private set; }
    public string? CheckLabel { get; private set; }
    public string? ModeNote { get; private set; }
    public bool HasModeNote => ModeNote is not null;
    /// <summary>Tools run read-only right away instead of adding an operation.</summary>
    public bool IsTool => Mode is EditorMode.CheckFs or EditorMode.Explore;
    public bool IsNotTool => !IsTool;

    // ----- slider: drag the partition boundary instead of typing a size
    public bool ShowSlider => ShowSize && MaxBytes > MinBytes;
    public double SliderMin => MinBytes / (double)(1024 * 1024 * 1024);
    public double SliderMax => Math.Max(MinBytes, MaxBytes) / (double)(1024 * 1024 * 1024);
    public double SliderValue
    {
        get => ParseSize() is { } b ? b / (double)(1024 * 1024 * 1024) : SliderMin;
        set => SizeText = Math.Clamp(value, SliderMin, SliderMax).ToString("0.##", CultureInfo.CurrentCulture);
    }

    private static readonly uint[] ClusterSizes = { 4096, 8192, 16384, 32768, 65536, 131072, 262144, 524288, 1048576, 2097152 };

    private PartitionInfo? NextPartition(DiskInfo disk, PartitionInfo p) =>
        disk.Partitions.Where(x => x.Role != PartitionRole.MbrExtended && x.OffsetBytes >= p.EndBytes).OrderBy(x => x.OffsetBytes).FirstOrDefault();

    private static string Describe(PartitionInfo p) =>
        (p.Volume?.DriveLetter is { } l ? $"{l}: " : "") + (p.Volume?.Label is { Length: > 0 } lb ? $"\"{lb}\" " : "") + $"{p.Volume?.FileSystem ?? "RAW"} · {Size(p.SizeBytes)}";

    private string SerialTail(DiskInfo d) => d.SerialNumber is { Length: >= 4 } s ? s.Trim()[^4..] : d.Number.ToString(CultureInfo.InvariantCulture);

    private void SetupExtended(EditorMode mode, PartitionInfo? p, DiskInfo disk)
    {
        switch (mode)
        {
            case EditorMode.Merge:
                ChoiceLabel = L("Editor.MergeWith");
                if (NextPartition(disk, p!) is { } next && next.Volume is { AccessError: null, FileSystem: not null })
                    ChoiceOptions.Add(new ChoiceItem(Describe(next), next));
                _choice = ChoiceOptions.FirstOrDefault();
                TextLabel = L("Editor.MergeFolder");
                _text = "Merged " + (_choice?.Value is PartitionInfo n && n.Volume?.DriveLetter is { } nl ? $"{nl}" : "partition");
                ModeNote = L("Editor.MergeNote");
                break;
            case EditorMode.Split:
                MinBytesOverride = LayoutSimulator.MinShrinkSize(p!);
                MaxBytesOverride = p!.SizeBytes > LayoutSimulator.MinimumPartitionBytes ? p.SizeBytes - LayoutSimulator.MinimumPartitionBytes : 0;
                _sizeText = (p.SizeBytes / 2 / (1024.0 * 1024 * 1024)).ToString("0.##", CultureInfo.CurrentCulture);
                _label = "";
                _letter = FirstFreeLetter()?.ToString() ?? "";
                ModeNote = L("Editor.SplitNote");
                break;
            case EditorMode.Copy:
                ChoiceLabel = L("Editor.CopyTo");
                foreach (var d in _queue.BaseInventory?.Disks ?? Array.Empty<DiskInfo>())
                {
                    if (d.IsDynamic || d.Style is not (PartitionStyle.Gpt or PartitionStyle.Mbr) || d.BytesPerLogicalSector != disk.BytesPerLogicalSector) continue;
                    foreach (var seg in DiskMapLayout.BuildSegments(d, out _).Where(sg => sg.Kind == SegmentKind.Unallocated))
                    {
                        ulong start = (seg.OffsetBytes + LayoutSimulator.MiB - 1) / LayoutSimulator.MiB * LayoutSimulator.MiB;
                        if (start + p!.SizeBytes <= seg.EndBytes)
                            ChoiceOptions.Add(new ChoiceItem($"{L("Common.Disk")} {d.Number} · {Loc.Instance.Format("LostP.At", Size(start))} · {Size(seg.EndBytes - start)}", (d.Number, start)));
                    }
                }
                _choice = ChoiceOptions.FirstOrDefault();
                ModeNote = L("Editor.CopyNote");
                break;
            case EditorMode.Wipe:
            case EditorMode.WipeDisk:
                ChoiceLabel = L("Editor.WipeMethod");
                foreach (var m in Enum.GetValues<WipeMethod>()) ChoiceOptions.Add(new ChoiceItem(L("Wipe.Method." + m), m));
                _choice = ChoiceOptions[0];
                CheckLabel = L("Editor.WipeVerify");
                ModeNote = disk.Media == MediaKind.Ssd || disk.Bus == BusKind.Nvme ? L("Wipe.Warn.Ssd") : null;
                if (mode == EditorMode.WipeDisk) TextLabel = Loc.Instance.Format("Editor.TypeSerialLabel", disk.Number, disk.Model, SerialTail(disk));
                break;
            case EditorMode.Sanitize:
                TextLabel = Loc.Instance.Format("Editor.TypeSerialLabel", disk.Number, disk.Model, SerialTail(disk));
                ModeNote = L("Editor.SanitizeNote");
                break;
            case EditorMode.Reformat:
                ChoiceLabel = L("Prop.FileSystem");
                foreach (var f in FileSystems.Supported) ChoiceOptions.Add(new ChoiceItem(f, f));
                _choice = ChoiceOptions.FirstOrDefault(c => string.Equals((string)c.Value!, p?.Volume?.FileSystem, StringComparison.OrdinalIgnoreCase)) ?? ChoiceOptions[0];
                Choice2Label = L("Editor.ClusterSize");
                Choice2Options.Add(new ChoiceItem(L("Editor.ClusterDefault"), null));
                foreach (var c in ClusterSizes) Choice2Options.Add(new ChoiceItem(Size((ulong)c), c));
                _choice2 = Choice2Options.FirstOrDefault(c => c.Value is uint u && u != p?.Volume?.ClusterSizeBytes && u >= 4096) ?? Choice2Options[0];
                TextLabel = L("Editor.StagingFolder");
                ModeNote = L("Editor.ReformatNote");
                break;
            case EditorMode.CheckFs:
                ModeNote = L("Editor.CheckNote");
                break;
            case EditorMode.Explore:
                ModeNote = p?.Volume?.DriveLetter is null ? L("Editor.ExploreNoLetter") : null;
                break;
        }
    }

    private ulong? MinBytesOverride, MaxBytesOverride;

    private IEnumerable<string> PreChecks()
    {
        var p = _segment.Partition;
        var disk = _segment.Disk.Disk;
        if (Mode is EditorMode.WipeDisk or EditorMode.Sanitize && !string.Equals(_text.Trim(), SerialTail(disk), StringComparison.OrdinalIgnoreCase))
            yield return Loc.Instance.Format("Editor.SerialMismatch", SerialTail(disk));
        if (Mode == EditorMode.Reformat && p is not null && _text.Trim().Length > 0)
        {
            var staging = _text.Trim();
            if (!Directory.Exists(staging)) yield return L("Editor.StagingMissing");
            else
            {
                if (_queue.BaseInventory is { } inv && VolumeLocator.DiskOfPath(inv, staging) == disk.Number) yield return L("Reformat.Error.Staging");
                ulong used = (p.Volume?.CapacityBytes ?? 0) - (p.Volume?.FreeBytes ?? 0);
                long free = -1;
                try { free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(staging))!).AvailableFreeSpace; } catch (Exception) { }
                if (free >= 0 && (ulong)free < used + used / 20) yield return Loc.Instance.Format("Editor.StagingSpace", Size(used), Size((ulong)free));
            }
        }
    }

    /// <summary>Operations for the extended modes; null when this mode is a basic one.</summary>
    private IReadOnlyList<DiskOperation>? BuildExtended()
    {
        var p = _segment.Partition;
        var dn = _segment.Disk.Number;
        var disk = _segment.Disk.Disk;
        const ulong MiB = LayoutSimulator.MiB;
        switch (Mode)
        {
            case EditorMode.Merge:
                if (_choice?.Value is not PartitionInfo b || string.IsNullOrWhiteSpace(_text)) return Array.Empty<DiskOperation>();
                ulong used = (b.Volume?.CapacityBytes ?? 0) - (b.Volume?.FreeBytes ?? 0);
                return new DiskOperation[]
                {
                    new CopyFilesOp(dn, p!.OffsetBytes, dn, b.OffsetBytes, _text.Trim(), used),
                    new DeletePartitionOp(dn, b.OffsetBytes),
                    new ResizePartitionOp(dn, p.OffsetBytes, (b.EndBytes - p.OffsetBytes) / MiB * MiB),
                };
            case EditorMode.Split:
            {
                if (ParseSize() is not { } first) return Array.Empty<DiskOperation>();
                ulong newStart = (p!.OffsetBytes + first + MiB - 1) / MiB * MiB;
                if (newStart >= p.EndBytes) return Array.Empty<DiskOperation>();
                ulong size = (p.EndBytes - newStart) / MiB * MiB;
                char? letter = string.IsNullOrWhiteSpace(_letter) ? null : char.ToUpperInvariant(_letter.Trim()[0]);
                return new DiskOperation[]
                {
                    new ResizePartitionOp(dn, p.OffsetBytes, first),
                    new CreatePartitionOp(dn, newStart, size, _fileSystem, string.IsNullOrWhiteSpace(_label) ? null : _label.Trim(), letter),
                };
            }
            case EditorMode.Copy:
                return _choice?.Value is (int dd, ulong start) ? new[] { new CopyPartitionOp(dd, start, dn, p!.OffsetBytes, p.SizeBytes) } : Array.Empty<DiskOperation>();
            case EditorMode.Wipe:
                return new[] { new WipeOp(dn, p!.OffsetBytes, (WipeMethod)_choice!.Value!, _check, p.SizeBytes) };
            case EditorMode.WipeDisk:
                return string.Equals(_text.Trim(), SerialTail(disk), StringComparison.OrdinalIgnoreCase)
                    ? new[] { new WipeOp(dn, null, (WipeMethod)_choice!.Value!, _check, disk.SizeBytes) } : Array.Empty<DiskOperation>();
            case EditorMode.Sanitize:
                return string.Equals(_text.Trim(), SerialTail(disk), StringComparison.OrdinalIgnoreCase) ? new[] { new SanitizeDiskOp(dn) } : Array.Empty<DiskOperation>();
            case EditorMode.RepairFs: return new[] { new RepairFileSystemOp(dn, p!.OffsetBytes) };
            case EditorMode.ToNtfs: return new[] { new ConvertFileSystemOp(dn, p!.OffsetBytes, "NTFS") };
            case EditorMode.Reformat:
                if (string.IsNullOrWhiteSpace(_text) || _choice?.Value is not string fs) return Array.Empty<DiskOperation>();
                return new[] { new ReformatKeepingFilesOp(dn, p!.OffsetBytes, fs, _choice2?.Value as uint?, _text.Trim(), (p.Volume?.CapacityBytes ?? 0) - (p.Volume?.FreeBytes ?? 0)) };
            case EditorMode.ToLogical: return new[] { new ConvertPartitionKindOp(dn, p!.OffsetBytes, true) };
            case EditorMode.ToPrimary: return new[] { new ConvertPartitionKindOp(dn, p!.OffsetBytes, false) };
            case EditorMode.ToDynamic: return new[] { new ConvertDiskTypeOp(dn, true) };
            case EditorMode.ToBasic: return new[] { new ConvertDiskTypeOp(dn, false) };
            case EditorMode.Align:
            {
                var (min, max) = MoveRange(disk, p!);
                ulong up = (p!.OffsetBytes + MiB - 1) / MiB * MiB, down = p.OffsetBytes / MiB * MiB;
                ulong? to = up <= max && up != p.OffsetBytes ? up : down >= min && down != p.OffsetBytes ? down : null;
                return to is { } t ? new[] { new MovePartitionOp(dn, p.OffsetBytes, t, p.SizeBytes) } : Array.Empty<DiskOperation>();
            }
            case EditorMode.CheckFs:
            case EditorMode.Explore:
                return Array.Empty<DiskOperation>();
            default:
                return null;
        }
    }

    private async Task RunToolAsync()
    {
        var p = _segment.Partition;
        if (p is null) return;
        if (Mode == EditorMode.Explore)
        {
            if (p.Volume?.DriveLetter is { } l) { _services.Shell?.OpenFolder($"{l}:\\"); ToolOutput = null; }
            else ToolOutput = L("Editor.ExploreNoLetter");
            return;
        }
        if (_services.FsTools is null) { ToolOutput = L("Rec.Unavailable"); return; }
        ToolOutput = L("BootFix.Checking");
        ToolOutput = await _services.FsTools.CheckAsync(p);
    }
}
