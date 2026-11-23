using System.Collections.ObjectModel;
using Lasteast.Core.Localization;
using Lasteast.Core.Models;
using Lasteast.Core.Operations;
using Lasteast.Core.RawDisk;
using Lasteast.Core.Recovery;
using Lasteast.Presentation.Mvvm;
using Lasteast.Presentation.Services;
using static Lasteast.Presentation.ViewModels.Text;

namespace Lasteast.Presentation.ViewModels;

/// <summary>Read-only access to a disk's sectors (Windows: \\.\PhysicalDriveN opened for reading).</summary>
public delegate IBlockDevice DiskReader(DiskInfo disk);

/// <summary>A place to recover files from: an existing partition or a lost one found by the scanner.</summary>
/// <param name="ImagePath">Set when the source is a disk-image file (DiskNumber is then -1).</param>
public sealed record RecoverySource(int DiskNumber, ulong OffsetBytes, ulong SizeBytes, string Title, string? ImagePath = null)
{
    public override string ToString() => Title;
}

public sealed class FoundPartitionViewModel
{
    public FoundPartitionViewModel(FoundPartition f, RelayCommand recover, RelayCommand browse)
    {
        Found = f;
        Title = $"{f.FileSystem} · {Size(f.SizeBytes)}" + (f.Label is { Length: > 0 } l ? $" \"{l}\"" : "");
        Detail = Loc.Instance.Format("LostP.At", Size(f.OffsetBytes)) + " · " + L("LostP.Conf." + f.Confidence)
                 + (f.FromBackupBootSector ? " · " + L("LostP.FromBackup") : "") + (f.Conflicts ? " · " + L("LostP.Conflict") : "");
        RecoverCommand = recover;
        BrowseCommand = browse;
    }

    public FoundPartition Found { get; }
    public string Title { get; }
    public string Detail { get; }
    public RelayCommand RecoverCommand { get; }
    public RelayCommand BrowseCommand { get; }
}

/// <summary>
/// A partition that is in the table but that Windows cannot read (RAW): checks its boot sector against the file
/// system's own backup and, when that backup is valid, queues the repair.
/// </summary>
public sealed class UnreadablePartitionViewModel : ObservableObject
{
    private readonly DiskInfo _disk;
    private readonly PartitionInfo _part;
    private readonly DiskReader? _reader;
    private readonly Func<DiskOperation, bool> _queue;
    private string? _result;
    private RepairBootSectorOp? _op;

    public UnreadablePartitionViewModel(DiskInfo disk, PartitionInfo part, DiskReader? reader, Func<DiskOperation, bool> queue, Action<RecoverySource> browse)
    {
        _disk = disk;
        _part = part;
        _reader = reader;
        _queue = queue;
        Title = (part.Volume?.DriveLetter is { } l ? $"{l}: · " : "") + $"{Size(part.SizeBytes)} · RAW · " + Loc.Instance.Format("LostP.At", Size(part.OffsetBytes));
        CheckCommand = new AsyncRelayCommand(CheckAsync, ex => Result = ErrorText(ex));
        RepairCommand = new RelayCommand(() => { if (_op is not null && !_queue(_op)) Result = L("BootFix.QueueFailed"); });
        // copying files off first is always the safest step; it writes nothing to this disk
        BrowseCommand = new RelayCommand(() => browse(new RecoverySource(disk.Number, part.OffsetBytes, part.SizeBytes,
            $"{L("Common.Disk")} {disk.Number} · {Title}")));
    }

    public RelayCommand BrowseCommand { get; }

    public string Title { get; }
    public string? Result { get => _result; private set { Set(ref _result, value); OnPropertyChanged(nameof(HasResult)); } }
    public bool HasResult => !string.IsNullOrEmpty(_result);
    public bool CanRepair => _op is not null;
    public AsyncRelayCommand CheckCommand { get; }
    public RelayCommand RepairCommand { get; }

    private async Task CheckAsync()
    {
        if (_reader is null) { Result = L("Rec.Unavailable"); return; }
        Result = L("BootFix.Checking");
        var d = await Task.Run(() =>
        {
            using var dev = _reader(_disk);
            return BootSectorRepair.Diagnose(new OffsetDevice(dev, (long)_part.OffsetBytes, (long)_part.SizeBytes));
        });
        _op = d.State == BootSectorState.Repairable ? new RepairBootSectorOp(_disk.Number, _part.OffsetBytes, d.FileSystem!) : null;
        OnPropertyChanged(nameof(CanRepair));
        Result = d.State switch
        {
            BootSectorState.Repairable => Loc.Instance.Format("BootFix.Result.Repairable", d.FileSystem, d.Evidence, Size((ulong)d.BytesToWrite)),
            BootSectorState.PrimaryValid => L("BootFix.Result.PrimaryValid"),
            _ => L("BootFix.Result.NoBackup"),
        };
    }
}

/// <summary>Partition recovery: scan a disk for lost partitions and restore their table entries (spec module "Partition Recovery").</summary>
public sealed class PartitionRecoveryPageViewModel : PageViewModel
{
    private readonly OperationQueue _queue;
    private readonly DiskReader? _reader;
    private readonly Action _openPending;
    private readonly Action<RecoverySource> _browse;
    private StorageInventory? _inv;
    private DiskChoice? _disk;
    private bool _deep;
    private string? _status;
    private CancellationTokenSource? _cts;

    public PartitionRecoveryPageViewModel(OperationQueue queue, DiskReader? reader, Action openPending, Action<RecoverySource> browse)
    {
        _queue = queue;
        _reader = reader;
        _openPending = openPending;
        _browse = browse;
        ScanCommand = new AsyncRelayCommand(ScanAsync, ex => { Status = ErrorText(ex); Scanning = false; });
        CancelCommand = new RelayCommand(() => _cts?.Cancel());
    }

    public override string TitleKey => "Nav.PartitionRecovery";
    public ObservableCollection<DiskChoice> Disks { get; } = new();
    public ObservableCollection<FoundPartitionViewModel> Found { get; } = new();
    /// <summary>Partitions of the chosen disk that are in the table but unreadable: the scan skips them (they are not lost).</summary>
    public ObservableCollection<UnreadablePartitionViewModel> Unreadable { get; } = new();
    public bool HasUnreadable => Unreadable.Count > 0;
    public AsyncRelayCommand ScanCommand { get; }
    public RelayCommand CancelCommand { get; }
    public DiskChoice? Disk { get => _disk; set { if (Set(ref _disk, value)) { Found.Clear(); Status = null; FillUnreadable(); } } }

    private void FillUnreadable()
    {
        Unreadable.Clear();
        var disk = _inv?.Disks.FirstOrDefault(d => d.Number == _disk?.Number);
        if (disk is not null && _inv is { IsSampleData: false })
            foreach (var p in disk.Partitions.Where(p => p.Volume?.AccessError is not null && VolumeProblems.Classify(p.Volume.AccessErrorCode) != VolumeProblem.NoMedia
                                                         && p.Role is PartitionRole.BasicData or PartitionRole.Unknown or PartitionRole.Other or PartitionRole.MbrLogical))
                Unreadable.Add(new UnreadablePartitionViewModel(disk, p, _reader, op =>
                {
                    if (_queue.TryAdd(op).All(i => !i.IsError)) { _openPending(); return true; }
                    Status = string.Join(" ", _queue.Preview(op).Where(i => i.IsError).Select(OperationText.Issue));
                    return false;
                }, _browse));
        OnPropertyChanged(nameof(HasUnreadable));
    }
    public bool DeepScan { get => _deep; set => Set(ref _deep, value); }
    public string? Status { get => _status; private set { Set(ref _status, value); OnPropertyChanged(nameof(HasStatus)); } }
    public bool HasStatus => !string.IsNullOrEmpty(_status);
    private bool _scanning;
    public bool Scanning { get => _scanning; private set => Set(ref _scanning, value); }
    public bool Available => _reader is not null && _inv is { IsSampleData: false };

    private async Task ScanAsync()
    {
        var disk = _inv?.Disks.FirstOrDefault(d => d.Number == _disk?.Number);
        if (disk is null || _reader is null) { Status = L(_reader is null ? "Rec.Unavailable" : "LostP.PickDisk"); return; }
        Scanning = true;
        Found.Clear();
        _cts = new CancellationTokenSource();
        var progress = new Progress<RecoveryScanProgress>(p => Status = Loc.Instance.Format("LostP.Progress", Percent(p.Total == 0 ? 0 : (double)p.Done / p.Total), p.Found));
        var existing = disk.Partitions.Where(p => p.Role != PartitionRole.MbrExtended).Select(p => (p.OffsetBytes, p.SizeBytes)).ToList();
        var mode = _deep ? LostScanMode.Deep : LostScanMode.Quick;
        IReadOnlyList<FoundPartition> found;
        try
        {
            found = await Task.Run(() =>
            {
                using var dev = _reader(disk);
                return LostPartitionScanner.Scan(dev, existing, mode, progress, _cts.Token);
            });
        }
        catch (OperationCanceledException) { Status = L("LostP.Cancelled"); Scanning = false; return; }
        foreach (var f in found)
        {
            var op = new RecoverPartitionOp(disk.Number, f.OffsetBytes, f.SizeBytes, f.FileSystem, f.FromBackupBootSector);
            var source = new RecoverySource(disk.Number, f.OffsetBytes, f.SizeBytes, $"{L("Common.Disk")} {disk.Number} · {L("LostP.Lost")} {f.FileSystem} @ {Size(f.OffsetBytes)}");
            Found.Add(new FoundPartitionViewModel(f,
                new RelayCommand(() => { if (_queue.TryAdd(op).All(i => !i.IsError)) _openPending(); else Status = string.Join(" ", _queue.Preview(op).Where(i => i.IsError).Select(OperationText.Issue)); }),
                new RelayCommand(() => _browse(source))));
        }
        Status = (found.Count == 0 ? L("LostP.None") : Loc.Instance.Format("LostP.FoundCount", found.Count))
                 + (Unreadable.Count > 0 ? " " + L("LostP.SeeUnreadable") : "");
        Scanning = false;
    }

    public override void Refresh(AppState state)
    {
        base.Refresh(state);
        _inv = state.Inventory;
        var keep = _disk?.Number;
        Disks.Clear();
        foreach (var d in _inv?.Disks ?? Array.Empty<DiskInfo>())
            Disks.Add(new DiskChoice(d.Number, $"{L("Common.Disk")} {d.Number} — {d.Model} · {Size(d.SizeBytes)}"));
        _disk = Disks.FirstOrDefault(x => x.Number == keep)
                ?? Disks.FirstOrDefault(x => _inv!.Disks.First(d => d.Number == x.Number).Partitions.Any(p => p.Volume?.AccessError is not null));
        OnPropertyChanged(nameof(Disk));
        OnPropertyChanged(nameof(Available));
        FillUnreadable();
    }
}

public sealed class RecoverableFileViewModel : ObservableObject
{
    private bool _isSelected;
    public RecoverableFileViewModel(RecoverableFile f)
    {
        File = f;
        _isSelected = f.IsDeleted && f.Recoverability == Recoverability.Good;
        SizeText = Size((ulong)f.Size);
        DateText = f.Modified?.ToLocalTime().ToString("g") ?? "";
        StatusText = L("Rec.State." + f.Recoverability) + (f.Note is { } n ? $" ({n})" : "");
    }

    public RecoverableFile File { get; }
    public string Path => File.FullPath;
    public string SizeText { get; }
    public string DateText { get; }
    public string StatusText { get; }
    public string StatusKind => File.Recoverability.ToString();
    public bool CanRecover => File.Recoverability != Recoverability.Unrecoverable;
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value && CanRecover); }
}

/// <summary>File recovery: list deleted (and optionally existing) files of a volume straight from its sectors and copy them to another disk.</summary>
public sealed class FileRecoveryPageViewModel : PageViewModel
{
    private readonly DiskReader? _reader;
    private readonly IShellService _shell;
    private StorageInventory? _inv;
    private RecoverySource? _source;
    private bool _includeExisting;
    private bool _byContent;
    private string _filter = "";
    private string? _destination;
    private string? _status;
    private bool _busy;
    private CancellationTokenSource? _cts;
    private IRecoveryReader? _openReader;
    private IBlockDevice? _openDevice;
    private List<RecoverableFileViewModel> _all = new();

    private readonly Core.Info.IVirtualDiskManager? _vd;
    private readonly Func<Task>? _refresh;

    public FileRecoveryPageViewModel(DiskReader? reader, IShellService shell, Core.Info.IVirtualDiskManager? virtualDisks = null, Func<Task>? refresh = null)
    {
        _reader = reader;
        _shell = shell;
        _vd = virtualDisks;
        _refresh = refresh;
        OpenImageCommand = new AsyncRelayCommand(OpenImageAsync, ex => { Status = ErrorText(ex); Busy = false; });
        ScanCommand = new AsyncRelayCommand(ScanAsync, ex => { Status = ErrorText(ex); Busy = false; });
        RecoverCommand = new AsyncRelayCommand(RecoverAsync, ex => { Status = ErrorText(ex); Busy = false; });
        CancelCommand = new RelayCommand(() => _cts?.Cancel());
        BrowseDestinationCommand = new RelayCommand(() => { if (_shell.PickFolder() is { } f) Destination = f; });
        SelectAllCommand = new RelayCommand(() => { foreach (var f in Files) f.IsSelected = true; });
        SelectNoneCommand = new RelayCommand(() => { foreach (var f in Files) f.IsSelected = false; });
    }

    public override string TitleKey => "Nav.FileRecovery";
    public ObservableCollection<RecoverySource> Sources { get; } = new();
    public ObservableCollection<RecoverableFileViewModel> Files { get; } = new();
    public AsyncRelayCommand ScanCommand { get; }
    public AsyncRelayCommand RecoverCommand { get; }
    public AsyncRelayCommand OpenImageCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand BrowseDestinationCommand { get; }
    public RelayCommand SelectAllCommand { get; }
    public RelayCommand SelectNoneCommand { get; }

    public RecoverySource? Source { get => _source; set { if (Set(ref _source, value)) { CloseReader(); Files.Clear(); _all.Clear(); Status = null; } } }
    public bool IncludeExisting { get => _includeExisting; set => Set(ref _includeExisting, value); }
    /// <summary>Search the raw data for known file types instead of reading the file system (works after a format or on unknown file systems).</summary>
    public bool ByContent { get => _byContent; set => Set(ref _byContent, value); }
    public string Filter { get => _filter; set { if (Set(ref _filter, value)) ApplyFilter(); } }
    public string? Destination { get => _destination; set { if (Set(ref _destination, value)) OnPropertyChanged(nameof(DestinationText)); } }
    public string DestinationText => _destination ?? L("Rec.NoDestination");
    public string? Status { get => _status; private set { Set(ref _status, value); OnPropertyChanged(nameof(HasStatus)); } }
    public bool HasStatus => !string.IsNullOrEmpty(_status);
    public bool Busy { get => _busy; private set => Set(ref _busy, value); }
    public bool Available => _reader is not null && _inv is { IsSampleData: false };

    /// <summary>Opens the page on a specific source (e.g. a lost partition from the scanner).</summary>
    public void Show(RecoverySource source)
    {
        if (!Sources.Contains(source)) Sources.Insert(0, source);
        Source = source;
    }

    private void CloseReader()
    {
        _openReader = null;
        _openDevice?.Dispose();
        _openDevice = null;
    }

    private async Task ScanAsync()
    {
        var src = _source;
        var disk = _inv?.Disks.FirstOrDefault(d => d.Number == src?.DiskNumber);
        if (src is null || (src.ImagePath is null && (disk is null || _reader is null)))
        { Status = L(src is not null && _reader is null ? "Rec.Unavailable" : "Rec.PickSource"); return; }
        Busy = true;
        Files.Clear();
        _all.Clear();
        CloseReader();
        _cts = new CancellationTokenSource();
        var progress = new Progress<RecoveryScanProgress>(p => Status = Loc.Instance.Format("Rec.Scanning", Percent(p.Total == 0 ? 0 : (double)p.Done / p.Total), p.Found));
        bool byContent = _byContent;
        try
        {
            var (reader, dev, files) = await Task.Run(() =>
            {
                var dev = src.ImagePath is { } image ? DiskImages.Open(image) : _reader!(disk!);
                try
                {
                    var vol = new OffsetDevice(dev, (long)src.OffsetBytes, (long)src.SizeBytes);
                    // unknown or damaged file system → fall back to searching by content
                    var r = (byContent ? null : RecoveryReaders.Open(vol)) ?? new CarvingRecoveryReader(vol);
                    return (r, dev, r.Scan(_includeExisting, progress, _cts.Token));
                }
                catch { dev.Dispose(); throw; }
            });
            _openReader = reader;
            _openDevice = dev;
            _all = files.Select(f => new RecoverableFileViewModel(f)).ToList();
            ApplyFilter();
            Status = reader is CarvingRecoveryReader
                ? Loc.Instance.Format(byContent ? "Rec.Carved" : "Rec.CarvedFallback", files.Count)
                : Loc.Instance.Format("Rec.Found", files.Count(f => f.IsDeleted), files.Count, reader.FileSystem)
                  + (reader.ThroughBackupBootSector ? " " + L("Rec.ThroughBackup") : "");
        }
        catch (OperationCanceledException) { Status = L("LostP.Cancelled"); }
        catch (RawOperationException ex) { Status = Loc.Instance.Format(ex.Key, ex.Message); }
        Busy = false;
    }

    private void ApplyFilter()
    {
        Files.Clear();
        foreach (var f in _all.Where(f => _filter.Length == 0 || f.Path.Contains(_filter, StringComparison.OrdinalIgnoreCase)).Take(5000))
            Files.Add(f);
    }

    private async Task RecoverAsync()
    {
        if (_openReader is null || _source is null) { Status = L("Rec.ScanFirst"); return; }
        if (_destination is null) { Status = L("Rec.NoDestination"); return; }
        var selected = _all.Where(f => f.IsSelected).Select(f => f.File).ToList();
        if (selected.Count == 0) { Status = L("Rec.NothingSelected"); return; }
        int? destDisk = _inv is null ? null : VolumeLocator.DiskOfPath(_inv, _destination);
        // an image file is never written, so any destination outside the image works; a disk must not receive its own files
        if (_source.ImagePath is null && destDisk == _source.DiskNumber) { Status = L("Rec.Error.SameDisk"); return; }
        Busy = true;
        _cts = new CancellationTokenSource();
        var reader = _openReader;
        var progress = new Progress<RecoveryScanProgress>(p => Status = Loc.Instance.Format("Rec.Copying", p.Done, p.Total));
        try
        {
            var rep = await Task.Run(() => RecoveryWriter.Recover(reader, selected, _destination, _source.DiskNumber, destDisk, progress, _cts.Token));
            Status = Loc.Instance.Format("Rec.Done", rep.Recovered, Size((ulong)rep.Bytes), rep.Failed) + (rep.Errors.Count > 0 ? "\n" + string.Join("\n", rep.Errors.Take(5).Select(e => $"{e.File}: {e.Error}")) : "");
            if (rep.Recovered > 0) _shell.OpenFolder(_destination);
        }
        catch (OperationCanceledException) { Status = L("LostP.Cancelled"); }
        catch (RawOperationException ex) { Status = L(ex.Key); }
        Busy = false;
    }

    /// <summary>Adds a disk-image file as a source: raw images and fixed VHDs are read directly; VHDX/dynamic VHD are attached read-only.</summary>
    private async Task OpenImageAsync()
    {
        if (_shell.PickOpenFile(DiskImages.FileFilter) is not { } path) return;
        Busy = true;
        try
        {
            if (await Task.Run(() => DiskImages.Classify(path)) == DiskImageKind.NeedsAttach)
            {
                if (_vd is null) { Status = L("Rec.Image.NeedsAttach"); return; }
                var device = await Task.Run(() => _vd.AttachReadOnly(path));
                if (_refresh is not null) await _refresh();
                int n = int.TryParse(new string(device.Reverse().TakeWhile(char.IsDigit).Reverse().ToArray()), out var x) ? x : -1;
                Source = Sources.FirstOrDefault(s => s.DiskNumber == n && s.OffsetBytes == 0 && s.ImagePath is null) ?? Source;
                Status = Loc.Instance.Format("Rec.Image.Attached", Path.GetFileName(path), n);
                return;
            }
            var regions = await Task.Run(() => { using var dev = DiskImages.Open(path); return DiskImages.Regions(dev); });
            var name = Path.GetFileName(path);
            RecoverySource? firstSrc = null;
            foreach (var r in regions)
            {
                var what = r.PartitionNumber is { } pn ? $"{L("Rec.Image.Partition")} {pn}" : r.FileSystem.Length > 0 ? L("Rec.Image.Volume") : L("Rec.WholeDisk");
                var src = new RecoverySource(-1, (ulong)r.Offset, (ulong)r.Length,
                    $"{L("Rec.Image")} {name} · {what} · {(r.FileSystem.Length > 0 ? r.FileSystem : "RAW")} · {Size((ulong)r.Length)}", path);
                if (!Sources.Contains(src)) Sources.Insert(0, src);
                firstSrc ??= src;
            }
            Source = firstSrc;
            Status = Loc.Instance.Format("Rec.Image.Opened", name, regions.Count);
        }
        catch (RawOperationException ex) { Status = Loc.Instance.Format(ex.Key, ex.Message); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { Status = ex.Message; }
        finally { Busy = false; }
    }

    public override void Refresh(AppState state)
    {
        base.Refresh(state);
        _inv = state.Inventory;
        var keep = _source;
        var lost = Sources.Where(s => s.ImagePath is not null || s.Title.Contains(L("LostP.Lost"))).ToList();
        Sources.Clear();
        foreach (var s in lost) Sources.Add(s);
        foreach (var d in _inv?.Disks ?? Array.Empty<DiskInfo>())
        {
            foreach (var p in d.Partitions.Where(p => p.Role is not (PartitionRole.MicrosoftReserved or PartitionRole.MbrExtended) && p.SizeBytes > 0))
                Sources.Add(new RecoverySource(d.Number, p.OffsetBytes, p.SizeBytes,
                    $"{L("Common.Disk")} {d.Number} · {PartitionTitle(p)} · {p.Volume?.FileSystem ?? "RAW"} · {Size(p.SizeBytes)}" + (p.Volume?.Label is { Length: > 0 } l ? $" \"{l}\"" : "")));
            // the whole disk: for searching by content when the partition table itself is gone
            Sources.Add(new RecoverySource(d.Number, 0, d.SizeBytes, $"{L("Common.Disk")} {d.Number} · {L("Rec.WholeDisk")} · {Size(d.SizeBytes)}"));
        }
        _source = Sources.FirstOrDefault(s => s == keep);
        OnPropertyChanged(nameof(Source));
        OnPropertyChanged(nameof(Available));
    }
}
