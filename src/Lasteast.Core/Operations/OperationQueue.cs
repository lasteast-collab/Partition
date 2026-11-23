using System.Security.Cryptography;
using System.Text;
using Lasteast.Core.Models;

namespace Lasteast.Core.Operations;

/// <summary>Identity + layout hash of a disk. Execution aborts if the live disk no longer matches the plan.</summary>
public static class DiskFingerprint
{
    public static string Identity(DiskInfo d) =>
        $"{d.Number}|{d.SizeBytes}|{d.Style}|{d.GptDiskId}|{d.MbrSignature}|{d.SerialNumber}|{d.Model}";

    public static string Compute(DiskInfo d)
    {
        var sb = new StringBuilder(Identity(d));
        foreach (var p in d.Partitions.Where(p => !p.IsPending).OrderBy(p => p.OffsetBytes))
            sb.Append($";{p.OffsetBytes}:{p.SizeBytes}:{p.Role}:{p.GptType}:{p.MbrType}:{p.Volume?.FileSystem}:{p.Volume?.DriveLetter}");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }
}

/// <summary>
/// Pending operations, in execution order. Adding an operation simulates the whole queue first;
/// an operation that would fail validation is rejected and never enters the queue.
/// When the inventory changes (refresh, device change), the queue is re-validated against it and
/// any operation that no longer fits is flagged — Apply is blocked until it is removed.
/// </summary>
public sealed class OperationQueue
{
    private readonly List<DiskOperation> _items = new();
    private StorageInventory? _base;

    public event EventHandler? Changed;

    public IReadOnlyList<DiskOperation> Items => _items;
    public StorageInventory? BaseInventory => _base;
    public int Count => _items.Count;

    public void Rebase(StorageInventory? inventory)
    {
        _base = inventory;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Tries to append <paramref name="op"/>. Returns the issues for that operation; it is added only if none is an error.</summary>
    public IReadOnlyList<ValidationIssue> TryAdd(DiskOperation op)
    {
        var issues = Preview(op);
        if (issues.Any(i => i.IsError)) return issues;
        _items.Add(op);
        Changed?.Invoke(this, EventArgs.Empty);
        return issues;
    }

    /// <summary>Issues <paramref name="op"/> would have if appended now (nothing is changed).</summary>
    public IReadOnlyList<ValidationIssue> Preview(DiskOperation op)
    {
        if (_base is null) return new[] { new ValidationIssue(IssueLevel.Error, "Op.Error.NoInventory") };
        if (_base.IsSampleData) return new[] { new ValidationIssue(IssueLevel.Error, "Op.Error.SampleData") };
        var sim = SimulateDisk(op.DiskNumber, _items.Append(op).ToList());
        return sim?.Steps[^1].Issues ?? new[] { new ValidationIssue(IssueLevel.Error, "Op.Error.NotFound") };
    }

    public void RemoveAt(int index)
    {
        if (index < 0 || index >= _items.Count) return;
        _items.RemoveAt(index);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool UndoLast()
    {
        if (_items.Count == 0) return false;
        _items.RemoveAt(_items.Count - 1);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void Clear()
    {
        if (_items.Count == 0) return;
        _items.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Removes the given operations (e.g. after they were executed).</summary>
    public void RemoveRange(IEnumerable<DiskOperation> done)
    {
        var set = done.ToHashSet(ReferenceEqualityComparer.Instance);
        if (_items.RemoveAll(o => set.Contains(o)) > 0) Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Simulates every affected disk. Disks are listed in the order they first appear in the queue.</summary>
    public IReadOnlyList<SimulationResult> SimulateAll()
    {
        if (_base is null) return Array.Empty<SimulationResult>();
        return _items.Select(o => o.DiskNumber).Distinct()
            .Select(n => SimulateDisk(n, _items))
            .OfType<SimulationResult>().ToList();
    }

    public bool IsValid => _base is { IsSampleData: false } && _items.Count > 0
                           && _items.Select(o => o.DiskNumber).Distinct().All(n => _base.Disks.Any(d => d.Number == n))
                           && SimulateAll().All(s => s.IsValid);

    private SimulationResult? SimulateDisk(int diskNumber, IReadOnlyList<DiskOperation> ops)
    {
        var disk = _base?.Disks.FirstOrDefault(d => d.Number == diskNumber);
        if (disk is null) return null;
        var sim = LayoutSimulator.Simulate(disk, ops.Where(o => o.DiskNumber == diskNumber).ToList(), LettersElsewhere(diskNumber),
            n => _base!.Disks.FirstOrDefault(d => d.Number == n));
        // A clone reads its source when it runs, so the source must not be changed by the same plan.
        var steps = sim.Steps.Select(s => s.Operation is CloneDiskOp c && ops.Any(o => o.DiskNumber == c.SourceDiskNumber)
            ? s with { Issues = s.Issues.Append(new ValidationIssue(IssueLevel.Error, "Clone.Error.SourcePending")).ToList() }
            : s).ToList();
        return sim with { Steps = steps };
    }

    /// <summary>Letters used on other disks now, plus letters other disks' pending operations would assign.</summary>
    private HashSet<char> LettersElsewhere(int diskNumber)
    {
        var set = new HashSet<char>();
        if (_base is null) return set;
        foreach (var d in _base.Disks.Where(d => d.Number != diskNumber))
            set.UnionWith(LayoutSimulator.LettersOn(d));
        foreach (var v in _base.UnmappedVolumes)
            if (v.DriveLetter is { } l) set.Add(char.ToUpperInvariant(l));
        foreach (var o in _items.Where(o => o.DiskNumber != diskNumber))
        {
            if (o is CreatePartitionOp { DriveLetter: { } a }) set.Add(char.ToUpperInvariant(a));
            if (o is SetDriveLetterOp { Letter: { } b }) set.Add(char.ToUpperInvariant(b));
        }
        return set;
    }
}
