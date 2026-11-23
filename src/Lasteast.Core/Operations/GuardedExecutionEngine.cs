using System.Text.Json;
using System.Text.Json.Serialization;
using Lasteast.Core.Abstractions;
using Lasteast.Core.Models;

namespace Lasteast.Core.Operations;

/// <summary>Performs exactly one operation on the real disk. Implementations must throw on any failure.</summary>
public interface IOperationExecutor
{
    Task ExecuteAsync(DiskOperation operation, DiskInfo liveDisk, CancellationToken cancellationToken);
}

public sealed record BackupRecord(int DiskNumber, string Folder, string ManifestSha256, DateTimeOffset CreatedAt, bool StoredOnSameDisk);

/// <summary>Saves partition-table metadata (MBR/GPT/EBRs + layout) before any write. Not a data backup.</summary>
public interface IPartitionTableBackup
{
    Task<BackupRecord> BackupAsync(DiskInfo disk, StorageInventory inventory, CancellationToken cancellationToken);
}

public interface IOperationHistory
{
    void Append(ExecutionReport report);
}

/// <summary>A failure carrying a localization key, so messages reach the user in their language.</summary>
public sealed class OperationFailedException : Exception
{
    public OperationFailedException(string key, string? detail = null, params object[] args)
        : base(detail ?? key)
    {
        Key = key;
        Detail = detail;
        Args = args;
    }

    public string Key { get; }
    public string? Detail { get; }
    public object[] Args { get; }
}

public sealed record ExecutionPolicy(bool AllowPhysicalDisks);

public enum StepState { Pending, Running, Succeeded, Failed, NotRun }

public sealed record ExecutionProgress(int StepIndex, int StepCount, DiskOperation Operation, StepState State, string? Detail = null);

public sealed record StepOutcome(DiskOperation Operation, StepState State, TimeSpan Duration, string? ErrorKey = null, string? ErrorDetail = null);

public sealed record ExecutionReport
{
    public required bool Succeeded { get; init; }
    /// <summary>True when nothing was written (failure happened during pre-flight checks).</summary>
    public required bool NothingWritten { get; init; }
    public string? FailureKey { get; init; }
    public object[] FailureArgs { get; init; } = Array.Empty<object>();
    public string? FailureDetail { get; init; }
    public required IReadOnlyList<StepOutcome> Steps { get; init; }
    public IReadOnlyList<BackupRecord> Backups { get; init; } = Array.Empty<BackupRecord>();
    public required DateTimeOffset Started { get; init; }
    public required DateTimeOffset Finished { get; init; }
}

/// <summary>
/// The only component allowed to change disks. It wraps an <see cref="IOperationExecutor"/> with the
/// safety contract from docs/SAFETY.md:
///  1. elevated; 2. typed confirmation; 3. virtual-disks-only policy unless the user opted in;
///  4. live layout must match the plan's fingerprint; 5. plan re-simulated on the live layout must be valid;
///  6. partition-table backup of every affected disk before the first write;
///  7. one step at a time, re-reading and verifying the disk after each step; stop at the first problem;
///  8. cancellation only between steps; 9. every run is written to the history.
/// </summary>
public sealed class GuardedExecutionEngine
{
    public const string ConfirmationWord = "APPLY";

    private readonly IStorageInventoryProvider _inventory;
    private readonly IOperationExecutor _executor;
    private readonly IPartitionTableBackup _backup;
    private readonly IPrivilegeService _privileges;
    private readonly IOperationHistory _history;
    private readonly IAppLog _log;

    public GuardedExecutionEngine(IStorageInventoryProvider inventory, IOperationExecutor executor, IPartitionTableBackup backup,
        IPrivilegeService privileges, IOperationHistory history, IAppLog log)
    {
        _inventory = inventory;
        _executor = executor;
        _backup = backup;
        _privileges = privileges;
        _history = history;
        _log = log;
    }

    public async Task<ExecutionReport> ExecuteAsync(IReadOnlyList<DiskOperation> operations, StorageInventory planBase,
        ExecutionPolicy policy, string confirmation, IProgress<ExecutionProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var started = DateTimeOffset.Now;
        var outcomes = operations.Select(o => new StepOutcome(o, StepState.NotRun, TimeSpan.Zero)).ToList();
        var backups = new List<BackupRecord>();
        bool anyWritten = false;

        ExecutionReport Finish(bool ok, string? key = null, string? detail = null, params object[] args)
        {
            var report = new ExecutionReport
            {
                Succeeded = ok, NothingWritten = !anyWritten, FailureKey = key, FailureDetail = detail, FailureArgs = args,
                Steps = outcomes, Backups = backups, Started = started, Finished = DateTimeOffset.Now,
            };
            try { _history.Append(report); } catch (Exception ex) { _log.Error("Could not write operation history", ex); }
            _log.Info($"Execution finished: ok={ok} key={key} detail={detail} written={anyWritten}");
            return report;
        }

        // ---------------- pre-flight (nothing has been written) ----------------
        if (operations.Count == 0) return Finish(false, "Exec.Error.Empty");
        if (planBase.IsSampleData) return Finish(false, "Op.Error.SampleData");
        if (!_privileges.IsElevated) return Finish(false, "Exec.Error.NotElevated");
        if (!string.Equals(confirmation?.Trim(), ConfirmationWord, StringComparison.Ordinal)) return Finish(false, "Exec.Error.Confirmation");

        StorageInventory live;
        try { live = await _inventory.GetInventoryAsync(cancellationToken); }
        catch (Exception ex) { return Finish(false, "Exec.Error.Inventory", ex.Message); }

        var diskNumbers = operations.Select(o => o.DiskNumber).Distinct().ToList();
        foreach (var n in diskNumbers)
        {
            var planned = planBase.Disks.FirstOrDefault(d => d.Number == n);
            var now = live.Disks.FirstOrDefault(d => d.Number == n);
            if (planned is null || now is null) return Finish(false, "Exec.Error.DiskMissing", null, n);
            if (DiskFingerprint.Compute(planned) != DiskFingerprint.Compute(now)) return Finish(false, "Exec.Error.LayoutChanged", null, n);
            if (!now.IsVirtual && !policy.AllowPhysicalDisks) return Finish(false, "Exec.Error.PhysicalPolicy", null, n);
            var sim = LayoutSimulator.Simulate(now, operations.Where(o => o.DiskNumber == n).ToList(), LettersElsewhere(live, n, operations), Lookup(live));
            if (!sim.IsValid) return Finish(false, "Exec.Error.Invalid", null, n);
        }

        // Clone sources are only read, but they must still be exactly what the user reviewed and not changed by this plan.
        foreach (var c in operations.OfType<CloneDiskOp>())
        {
            var planned = planBase.Disks.FirstOrDefault(d => d.Number == c.SourceDiskNumber);
            var now = live.Disks.FirstOrDefault(d => d.Number == c.SourceDiskNumber);
            if (planned is null || now is null) return Finish(false, "Exec.Error.DiskMissing", null, c.SourceDiskNumber);
            if (DiskFingerprint.Compute(planned) != DiskFingerprint.Compute(now)) return Finish(false, "Exec.Error.LayoutChanged", null, c.SourceDiskNumber);
            if (diskNumbers.Contains(c.SourceDiskNumber)) return Finish(false, "Clone.Error.SourcePending", null, c.SourceDiskNumber);
        }

        foreach (var n in diskNumbers)
        {
            try
            {
                var rec = await _backup.BackupAsync(live.Disks.First(d => d.Number == n), live, cancellationToken);
                backups.Add(rec);
                _log.Info($"Partition table of disk {n} backed up to {rec.Folder}");
            }
            catch (Exception ex)
            {
                _log.Error($"Backup of disk {n} failed", ex);
                return Finish(false, "Exec.Error.Backup", ex.Message, n);
            }
        }

        // ---------------- execution ----------------
        for (int i = 0; i < operations.Count; i++)
        {
            var op = operations[i];
            if (cancellationToken.IsCancellationRequested) return Finish(false, "Exec.Error.Cancelled", null, i);

            var disk = live.Disks.First(d => d.Number == op.DiskNumber);
            var expected = LayoutSimulator.Simulate(disk, new[] { op }, LettersElsewhere(live, op.DiskNumber, operations.Skip(i + 1).ToList()), Lookup(live));
            if (!expected.IsValid)
            {
                outcomes[i] = outcomes[i] with { State = StepState.Failed, ErrorKey = "Exec.Error.Invalid" };
                return Finish(false, "Exec.Error.Invalid", null, op.DiskNumber);
            }

            progress?.Report(new ExecutionProgress(i, operations.Count, op, StepState.Running));
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                anyWritten = true;
                _log.Info($"Executing step {i + 1}/{operations.Count}: {op}");
                // The step itself is never cancelled midway: an interrupted partition write is worse than a finished one.
                await _executor.ExecuteAsync(op, disk, CancellationToken.None);
            }
            catch (Exception ex)
            {
                var (key, detail) = ex is OperationFailedException ofe ? (ofe.Key, ofe.Detail) : ("Exec.Error.Step", ex.Message);
                _log.Error($"Step {i + 1} failed", ex);
                outcomes[i] = outcomes[i] with { State = StepState.Failed, Duration = sw.Elapsed, ErrorKey = key, ErrorDetail = detail };
                progress?.Report(new ExecutionProgress(i, operations.Count, op, StepState.Failed, detail));
                return Finish(false, key, detail, i + 1);
            }

            // Re-read and verify before doing anything else.
            try { live = await _inventory.GetInventoryAsync(CancellationToken.None); }
            catch (Exception ex) { return FailStep(i, op, sw.Elapsed, "Exec.Error.Inventory", ex.Message); }

            var after = live.Disks.FirstOrDefault(d => d.Number == op.DiskNumber);
            bool rewritesTable = op is ConvertStyleOp or CloneDiskOp or RecoverPartitionOp or RestoreTableOp;
            if (after is null || (rewritesTable
                    ? DiskFingerprint.Hardware(after) != DiskFingerprint.Hardware(disk)
                    : DiskFingerprint.Identity(after) != DiskFingerprint.Identity(disk)))
                return FailStep(i, op, sw.Elapsed, "Exec.Error.DiskChanged", null);
            var mismatch = Verify(op, expected.After, after);
            if (mismatch is not null) return FailStep(i, op, sw.Elapsed, "Exec.Error.Verify", mismatch);

            outcomes[i] = outcomes[i] with { State = StepState.Succeeded, Duration = sw.Elapsed };
            progress?.Report(new ExecutionProgress(i, operations.Count, op, StepState.Succeeded));
        }

        return Finish(true);

        ExecutionReport FailStep(int i, DiskOperation op, TimeSpan t, string key, string? detail)
        {
            outcomes[i] = outcomes[i] with { State = StepState.Failed, Duration = t, ErrorKey = key, ErrorDetail = detail };
            progress?.Report(new ExecutionProgress(i, operations.Count, op, StepState.Failed, detail));
            return Finish(false, key, detail, i + 1);
        }
    }

    private static Func<int, DiskInfo?> Lookup(StorageInventory inv) => n => inv.Disks.FirstOrDefault(d => d.Number == n);

    private static HashSet<char> LettersElsewhere(StorageInventory inv, int diskNumber, IEnumerable<DiskOperation> laterOps)
    {
        var set = new HashSet<char>();
        foreach (var d in inv.Disks.Where(d => d.Number != diskNumber)) set.UnionWith(LayoutSimulator.LettersOn(d));
        foreach (var v in inv.UnmappedVolumes) if (v.DriveLetter is { } l) set.Add(char.ToUpperInvariant(l));
        return set;
    }

    /// <summary>
    /// Compares the live disk after a step with the simulated expectation. Returns a description of the
    /// first mismatch, or null. Sizes may differ by up to 1 MiB (Windows rounds to its own granularity).
    /// </summary>
    private static IEnumerable<PartitionInfo> Real(DiskInfo d) => d.Partitions.Where(p => p.Role != PartitionRole.MbrExtended);

    public static string? Verify(DiskOperation op, DiskInfo expected, DiskInfo actual)
    {
        const ulong Tolerance = LayoutSimulator.MiB;
        PartitionInfo? At(DiskInfo d, ulong off) => d.Partitions.FirstOrDefault(p => p.OffsetBytes == off && p.Role != PartitionRole.MbrExtended);
        bool Near(ulong a, ulong b) => (a > b ? a - b : b - a) <= Tolerance;

        switch (op)
        {
            case CreatePartitionOp c:
            {
                var p = At(actual, c.OffsetBytes);
                if (p is null) return $"no partition at offset {c.OffsetBytes}";
                if (!Near(p.SizeBytes, c.SizeBytes)) return $"size {p.SizeBytes} != {c.SizeBytes}";
                if (!string.Equals(p.Volume?.FileSystem, c.FileSystem, StringComparison.OrdinalIgnoreCase)) return $"file system {p.Volume?.FileSystem} != {c.FileSystem}";
                if (c.DriveLetter is { } l && p.Volume?.DriveLetter != char.ToUpperInvariant(l)) return $"drive letter {p.Volume?.DriveLetter} != {l}";
                break;
            }
            case DeletePartitionOp x:
                if (At(actual, x.OffsetBytes) is not null) return $"partition at {x.OffsetBytes} still exists";
                break;
            case FormatPartitionOp f:
            {
                var p = At(actual, f.OffsetBytes);
                if (p is null) return "partition disappeared";
                if (!string.Equals(p.Volume?.FileSystem, f.FileSystem, StringComparison.OrdinalIgnoreCase)) return $"file system {p.Volume?.FileSystem} != {f.FileSystem}";
                break;
            }
            case ResizePartitionOp r:
            {
                var p = At(actual, r.OffsetBytes);
                if (p is null) return "partition disappeared";
                if (!Near(p.SizeBytes, r.NewSizeBytes)) return $"size {p.SizeBytes} != {r.NewSizeBytes}";
                break;
            }
            case SetLabelOp l:
            {
                var p = At(actual, l.OffsetBytes);
                if (!string.Equals(p?.Volume?.Label ?? "", l.Label, StringComparison.Ordinal)) return $"label '{p?.Volume?.Label}' != '{l.Label}'";
                break;
            }
            case SetDriveLetterOp s:
            {
                var p = At(actual, s.OffsetBytes);
                var want = s.Letter is { } c ? char.ToUpperInvariant(c) : (char?)null;
                if (p?.Volume?.DriveLetter != want) return $"drive letter {p?.Volume?.DriveLetter} != {want}";
                break;
            }
            case ConvertStyleOp cs:
                if (actual.Style != cs.Target) return $"partition style {actual.Style} != {cs.Target}";
                break;
            case MovePartitionOp m:
            {
                var p = At(actual, m.NewOffsetBytes);
                if (p is null) return $"no partition at new offset {m.NewOffsetBytes}";
                var orig = expected.Partitions.First(x => x.OffsetBytes == m.NewOffsetBytes);
                if (p.SizeBytes != orig.SizeBytes) return $"moved partition size {p.SizeBytes} != {orig.SizeBytes}";
                break;
            }
            case RestoreTableOp rt:
            {
                var got = Real(actual).Select(p => (p.OffsetBytes, p.SizeBytes)).OrderBy(x => x.OffsetBytes).ToList();
                var want = rt.Partitions.OrderBy(x => x.Offset).ToList();
                if (!got.SequenceEqual(want)) return $"layout after restore differs: {got.Count} partitions, expected {want.Count}";
                return null;
            }
            case RecoverPartitionOp rp:
            {
                var p = At(actual, rp.OffsetBytes);
                if (p is null) return $"no partition at {rp.OffsetBytes}";
                if (!Near(p.SizeBytes, rp.SizeBytes)) return $"size {p.SizeBytes} != {rp.SizeBytes}";
                break;
            }
            case CloneDiskOp:
                if (actual.Style != expected.Style) return $"partition style {actual.Style} != {expected.Style}";
                if (Real(actual).Count() != Real(expected).Count()) return $"{Real(actual).Count()} partitions, expected {Real(expected).Count()}";
                break;
        }

        // Every other partition must be untouched.
        var touched = op switch
        {
            CreatePartitionOp c => c.OffsetBytes,
            DeletePartitionOp x => x.OffsetBytes,
            FormatPartitionOp f => f.OffsetBytes,
            ResizePartitionOp r => r.OffsetBytes,
            SetLabelOp l => l.OffsetBytes,
            SetDriveLetterOp s => s.OffsetBytes,
            MovePartitionOp m => m.NewOffsetBytes,
            RecoverPartitionOp rp => rp.OffsetBytes,
            _ => ulong.MaxValue,
        };
        // (After a clone every destination partition is checked here: none is "touched", all must match.)
        foreach (var e in expected.Partitions.Where(p => p.OffsetBytes != touched && (!p.IsPending || op is CloneDiskOp) && p.Role != PartitionRole.MbrExtended))
        {
            var a = At(actual, e.OffsetBytes);
            if (a is null || a.SizeBytes != e.SizeBytes) return $"unrelated partition at {e.OffsetBytes} changed";
        }
        return null;
    }
}

/// <summary>Append-only JSON-lines history of every execution attempt (including refused ones).</summary>
public sealed class JsonlOperationHistory : IOperationHistory
{
    private static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new JsonStringEnumConverter() },
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
    };

    private readonly string _path;
    private readonly object _gate = new();

    public JsonlOperationHistory(string path) => _path = path;

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lasteast", "Partition", "history.jsonl");

    public void Append(ExecutionReport report)
    {
        var line = JsonSerializer.Serialize(new
        {
            report.Started, report.Finished, report.Succeeded, report.NothingWritten, report.FailureKey, report.FailureDetail,
            Steps = report.Steps.Select(s => new { Operation = s.Operation.ToString(), s.State, s.Duration, s.ErrorKey, s.ErrorDetail }),
            Backups = report.Backups,
        }, Options);
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.AppendAllText(_path, line + Environment.NewLine);
        }
    }

    public IReadOnlyList<string> ReadAll() => File.Exists(_path) ? File.ReadAllLines(_path) : Array.Empty<string>();
}
