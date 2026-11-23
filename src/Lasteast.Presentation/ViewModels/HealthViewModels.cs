using System.Collections.ObjectModel;
using Lasteast.Core.Health;
using Lasteast.Core.Localization;
using Lasteast.Core.Models;
using Lasteast.Core.Performance;
using Lasteast.Presentation.Mvvm;
using static Lasteast.Presentation.ViewModels.Text;

namespace Lasteast.Presentation.ViewModels;

public sealed record MetricRow(string Label, string Value);
public sealed record FindingRow(string Text, string Kind);
public sealed record AttributeRow(string Id, string Name, string Current, string Worst, string Threshold, string Raw, bool Failing);

public sealed class DiskHealthCard
{
    public DiskHealthCard(DiskInfo disk, DiskHealthReport r)
    {
        Title = $"{L("Common.Disk")} {disk.Number} — {disk.Model} · {Size(disk.SizeBytes)}";
        VerdictKind = r.Verdict.ToString();
        VerdictText = L("Health.Verdict." + r.Verdict);
        SourcesText = r.Sources.Count == 0 ? L("Health.NoSource") : Loc.Instance.Format("Health.Sources", string.Join(", ", r.Sources));
        void M(string key, string? value) { if (value is not null) Metrics.Add(new MetricRow(L(key), value)); }
        M("Health.M.Temperature", r.Temperature is { } t ? $"{t} °C" : null);
        M("Health.M.PowerOn", r.PowerOnHours is { } h ? Loc.Instance.Format("Health.Hours", h) : null);
        M("Health.M.Wear", r.WearPercent is { } w ? $"{w} %" : null);
        if (r.Nvme is { } n)
        {
            M("Health.M.Spare", $"{n.AvailableSparePercent} % (≥ {n.AvailableSpareThreshold} %)");
            M("Health.M.Written", Size((ulong)Math.Min((double)n.BytesWritten, ulong.MaxValue)));
            M("Health.M.Read", Size((ulong)Math.Min((double)n.BytesRead, ulong.MaxValue)));
            M("Health.M.MediaErrors", n.MediaErrors.ToString());
            M("Health.M.Unsafe", n.UnsafeShutdowns.ToString());
            M("Health.M.PowerCycles", n.PowerCycles.ToString());
        }
        if (r.Windows is { } win)
        {
            M("Health.M.WindowsStatus", win.HealthStatus);
            M("Health.M.Uncorrected", win.ReadErrorsUncorrected is null && win.WriteErrorsUncorrected is null ? null : ((win.ReadErrorsUncorrected ?? 0) + (win.WriteErrorsUncorrected ?? 0)).ToString());
        }
        foreach (var f in r.Findings) Findings.Add(new FindingRow(Loc.Instance.Format(f.Key, f.Args), f.Level.ToString()));
        foreach (var a in r.Ata)
            Attributes.Add(new AttributeRow(a.Id.ToString(), a.Name, a.Current.ToString(), a.Worst.ToString(), a.Threshold == 0 ? "—" : a.Threshold.ToString(), a.Raw.ToString(), a.BelowThreshold));
    }

    public string Title { get; }
    public string VerdictText { get; }
    public string VerdictKind { get; }
    public string SourcesText { get; }
    public List<MetricRow> Metrics { get; } = new();
    public List<FindingRow> Findings { get; } = new();
    public List<AttributeRow> Attributes { get; } = new();
    public bool HasAttributes => Attributes.Count > 0;
}

/// <summary>Disk health (SMART / NVMe / Windows reliability counters). Shows only what drives report; never estimates.</summary>
public sealed class DiskHealthPageViewModel : PageViewModel
{
    private readonly IDiskHealthProvider? _provider;
    private StorageInventory? _inv;
    private string? _status;

    public DiskHealthPageViewModel(IDiskHealthProvider? provider)
    {
        _provider = provider;
        RefreshCommand = new AsyncRelayCommand(ReadAllAsync, ex => Status = ErrorText(ex));
    }

    public override string TitleKey => "Nav.DiskHealth";
    public ObservableCollection<DiskHealthCard> Cards { get; } = new();
    public AsyncRelayCommand RefreshCommand { get; }
    public Dictionary<int, DiskHealthReport> Reports { get; } = new();
    public string? Status { get => _status; private set { Set(ref _status, value); OnPropertyChanged(nameof(HasStatus)); } }
    public bool HasStatus => !string.IsNullOrEmpty(_status);

    public async Task ReadAllAsync()
    {
        if (_provider is null || _inv is null || _inv.IsSampleData) { Status = L("Rec.Unavailable"); return; }
        if (!_inv.IsElevated) Status = L("Health.NeedAdmin");
        Cards.Clear();
        foreach (var d in _inv.Disks)
        {
            var r = await _provider.ReadAsync(d);
            Reports[d.Number] = r;
            Cards.Add(new DiskHealthCard(d, r));
        }
    }

    public override void Refresh(AppState state)
    {
        base.Refresh(state);
        bool first = _inv is null;
        _inv = state.Inventory;
        if (first && _provider is not null && _inv is { IsSampleData: false }) _ = RefreshCommand.ExecuteAsync();
    }
}

public sealed record VolumeChoice(char Letter, string Title)
{
    public override string ToString() => Title;
}

public sealed record SurfaceCell(string Kind);

/// <summary>Performance lab: volume benchmark and read-only disk surface test.</summary>
public sealed class PerformanceLabPageViewModel : PageViewModel
{
    private readonly DiskReader? _reader;
    private StorageInventory? _inv;
    private VolumeChoice? _volume;
    private int _sizeMb = 1024;
    private string? _benchText;
    private DiskChoice? _disk;
    private string? _surfaceText;
    private bool _busy;
    private CancellationTokenSource? _cts;

    public PerformanceLabPageViewModel(DiskReader? reader)
    {
        _reader = reader;
        BenchmarkCommand = new AsyncRelayCommand(RunBenchmarkAsync, ex => { BenchText = ex.Message; Busy = false; });
        SurfaceCommand = new AsyncRelayCommand(RunSurfaceAsync, ex => { SurfaceText = ex.Message; Busy = false; });
        CancelCommand = new RelayCommand(() => _cts?.Cancel());
    }

    public override string TitleKey => "Nav.PerformanceLab";
    public ObservableCollection<VolumeChoice> Volumes { get; } = new();
    public ObservableCollection<DiskChoice> Disks { get; } = new();
    public ObservableCollection<SurfaceCell> SurfaceMap { get; } = new();
    public IReadOnlyList<int> SizeOptions { get; } = new[] { 256, 1024, 4096 };
    public AsyncRelayCommand BenchmarkCommand { get; }
    public AsyncRelayCommand SurfaceCommand { get; }
    public RelayCommand CancelCommand { get; }
    public VolumeChoice? Volume { get => _volume; set => Set(ref _volume, value); }
    public int SizeMb { get => _sizeMb; set => Set(ref _sizeMb, value); }
    public DiskChoice? Disk { get => _disk; set => Set(ref _disk, value); }
    public string? BenchText { get => _benchText; private set => Set(ref _benchText, value); }
    public string? SurfaceText { get => _surfaceText; private set => Set(ref _surfaceText, value); }
    public bool Busy { get => _busy; private set => Set(ref _busy, value); }

    private async Task RunBenchmarkAsync()
    {
        if (_volume is null) { BenchText = L("Perf.PickVolume"); return; }
        var root = $"{_volume.Letter}:\\";
        var free = new DriveInfo(root).AvailableFreeSpace;
        long bytes = _sizeMb * 1024L * 1024;
        if (free < bytes * 2) { BenchText = Loc.Instance.Format("Perf.NoSpace", Size((ulong)bytes * 2)); return; }
        Busy = true;
        _cts = new CancellationTokenSource();
        var progress = new Progress<BenchmarkProgress>(p => BenchText = Loc.Instance.Format("Perf.Running", L("Perf.Phase." + p.Phase), Percent(p.Fraction)));
        try
        {
            var r = await Task.Run(() => Benchmark.Run(root, bytes, TimeSpan.FromSeconds(5), OperatingSystem.IsWindows(), progress, _cts.Token));
            BenchText = Loc.Instance.Format("Perf.Result", r.SeqReadMBps.ToString("0"), r.SeqWriteMBps.ToString("0"),
                r.Rnd4kReadIops.ToString("0"), r.Rnd4kWriteIops.ToString("0"), r.Rnd4kReadLatencyMs.ToString("0.00"), r.Rnd4kWriteLatencyMs.ToString("0.00"));
        }
        catch (OperationCanceledException) { BenchText = L("LostP.Cancelled"); }
        Busy = false;
    }

    private async Task RunSurfaceAsync()
    {
        var disk = _inv?.Disks.FirstOrDefault(d => d.Number == _disk?.Number);
        if (disk is null || _reader is null) { SurfaceText = L(_reader is null ? "Rec.Unavailable" : "LostP.PickDisk"); return; }
        Busy = true;
        _cts = new CancellationTokenSource();
        SurfaceMap.Clear();
        var progress = new Progress<SurfaceProgress>(p => SurfaceText = Loc.Instance.Format("Perf.SurfaceProgress", Percent((double)p.Done / p.Total), p.Errors, p.Slow));
        try
        {
            var r = await Task.Run(() =>
            {
                using var dev = _reader(disk);
                return SurfaceScanner.Scan(dev, TimeSpan.FromMilliseconds(500), progress, _cts.Token);
            });
            // downsample to at most 600 cells: each cell shows the worst block in its range
            int cells = Math.Min(600, r.Blocks.Count);
            for (int c = 0; c < cells; c++)
            {
                int a = (int)((long)c * r.Blocks.Count / cells), b = (int)((long)(c + 1) * r.Blocks.Count / cells);
                var worst = r.Blocks.Skip(a).Take(Math.Max(1, b - a)).Max();
                SurfaceMap.Add(new SurfaceCell(worst.ToString()));
            }
            SurfaceText = Loc.Instance.Format(r.Completed ? "Perf.SurfaceDone" : "Perf.SurfaceStopped", Size((ulong)r.BytesScanned), r.ErrorBlocks, r.BadSectors.Count, r.SlowBlocks,
                r.Duration.ToString(@"hh\:mm\:ss"));
        }
        finally { Busy = false; }
    }

    public override void Refresh(AppState state)
    {
        base.Refresh(state);
        _inv = state.Inventory;
        var (v, d) = (_volume?.Letter, _disk?.Number);
        Volumes.Clear();
        Disks.Clear();
        foreach (var disk in _inv?.Disks ?? Array.Empty<DiskInfo>())
        {
            Disks.Add(new DiskChoice(disk.Number, $"{L("Common.Disk")} {disk.Number} — {disk.Model} · {Size(disk.SizeBytes)}"));
            foreach (var p in disk.Partitions)
                if (p.Volume?.DriveLetter is { } l)
                    Volumes.Add(new VolumeChoice(l, $"{l}: {p.Volume.Label} · {p.Volume.FileSystem} · {Size(p.Volume.FreeBytes)} {L("Stat.Free")}"));
        }
        _volume = Volumes.FirstOrDefault(x => x.Letter == v);
        _disk = Disks.FirstOrDefault(x => x.Number == d);
        OnPropertyChanged(nameof(Volume));
        OnPropertyChanged(nameof(Disk));
    }
}

public sealed record AlertViewModel(string Text, string Kind);
