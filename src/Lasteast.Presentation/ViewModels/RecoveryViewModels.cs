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
public sealed record RecoverySource(int DiskNumber, ulong OffsetBytes, ulong SizeBytes, string Title)
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
        ScanCommand = new AsyncRelayCommand(ScanAsync, ex => { Status = ex.Message; Scanning = false; });
        CancelCommand = new RelayCommand(() => _cts?.Cancel());
    }

    public override string TitleKey => "Nav.PartitionRecovery";
    public ObservableCollection<DiskChoice> Disks { get; } = new();
    public ObservableCollection<FoundPartitionViewModel> Found { get; } = new();
    public AsyncRelayCommand ScanCommand { get; }
    public RelayCommand CancelCommand { get; }
    public DiskChoice? Disk { get => _disk; set { if (Set(ref _disk, value)) { Found.Clear(); Status = null; } } }
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
        Status = found.Count == 0 ? L("LostP.None") : Loc.Instance.Format("LostP.FoundCount", found.Count);
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
        _disk = Disks.FirstOrDefault(x => x.Number == keep);
        OnPropertyChanged(nameof(Disk));
        OnPropertyChanged(nameof(Available));
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
    private string _filter = "";
    private string? _destination;
    private string? _status;
    private bool _busy;
    private CancellationTokenSource? _cts;
    private IRecoveryReader? _openReader;
    private IBlockDevice? _openDevice;
    private List<RecoverableFileViewModel> _all = new();

    public FileRecoveryPageViewModel(DiskReader? reader, IShellService shell)
    {
        _reader = reader;
        _shell = shell;
        ScanCommand = new AsyncRelayCommand(ScanAsync, ex => { Status = ex.Message; Busy = false; });
        RecoverCommand = new AsyncRelayCommand(RecoverAsync, ex => { Status = ex.Message; Busy = false; });
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
    public RelayCommand CancelCommand { get; }
    public RelayCommand BrowseDestinationCommand { get; }
    public RelayCommand SelectAllCommand { get; }
    public RelayCommand SelectNoneCommand { get; }

    public RecoverySource? Source { get => _source; set { if (Set(ref _source, value)) { CloseReader(); Files.Clear(); _all.Clear(); Status = null; } } }
    public bool IncludeExisting { get => _includeExisting; set => Set(ref _includeExisting, value); }
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
        if (src is null || disk is null || _reader is null) { Status = L(_reader is null ? "Rec.Unavailable" : "Rec.PickSource"); return; }
        Busy = true;
        Files.Clear();
        _all.Clear();
        CloseReader();
        _cts = new CancellationTokenSource();
        var progress = new Progress<RecoveryScanProgress>(p => Status = Loc.Instance.Format("Rec.Scanning", Percent(p.Total == 0 ? 0 : (double)p.Done / p.Total), p.Found));
        try
        {
            var (reader, dev, files) = await Task.Run(() =>
            {
                var dev = _reader(disk);
                try
                {
                    var vol = new OffsetDevice(dev, (long)src.OffsetBytes, (long)src.SizeBytes);
                    var r = RecoveryReaders.Open(vol) ?? throw new RawOperationException("Rec.Error.UnknownFs", "");
                    return (r, dev, r.Scan(_includeExisting, progress, _cts.Token));
                }
                catch { dev.Dispose(); throw; }
            });
            _openReader = reader;
            _openDevice = dev;
            _all = files.Select(f => new RecoverableFileViewModel(f)).ToList();
            ApplyFilter();
            Status = Loc.Instance.Format("Rec.Found", files.Count(f => f.IsDeleted), files.Count, reader.FileSystem);
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
        if (destDisk == _source.DiskNumber) { Status = L("Rec.Error.SameDisk"); return; }
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

    public override void Refresh(AppState state)
    {
        base.Refresh(state);
        _inv = state.Inventory;
        var keep = _source;
        var lost = Sources.Where(s => s.Title.Contains(L("LostP.Lost"))).ToList();
        Sources.Clear();
        foreach (var s in lost) Sources.Add(s);
        foreach (var d in _inv?.Disks ?? Array.Empty<DiskInfo>())
        foreach (var p in d.Partitions.Where(p => p.Volume?.FileSystem is "NTFS" or "FAT32"))
            Sources.Add(new RecoverySource(d.Number, p.OffsetBytes, p.SizeBytes,
                $"{L("Common.Disk")} {d.Number} · {PartitionTitle(p)} · {p.Volume!.FileSystem} · {Size(p.SizeBytes)}" + (p.Volume.Label is { Length: > 0 } l ? $" \"{l}\"" : "")));
        _source = Sources.FirstOrDefault(s => s == keep);
        OnPropertyChanged(nameof(Source));
        OnPropertyChanged(nameof(Available));
    }
}
