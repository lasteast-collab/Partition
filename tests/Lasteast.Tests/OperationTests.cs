using Lasteast.Core.Abstractions;
using Lasteast.Core.Logging;
using Lasteast.Core.Models;
using Lasteast.Core.Operations;
using Lasteast.Presentation.Sample;

namespace Lasteast.Tests;

internal static class Fixtures
{
    public const ulong MiB = 1024 * 1024;
    public const ulong GiB = 1024 * MiB;

    /// <summary>The sample inventory re-labelled as real, virtual disks (operations refuse sample data).</summary>
    public static StorageInventory Real(bool virtualDisks = true)
    {
        var inv = SampleInventoryProvider.Create();
        return inv with
        {
            IsSampleData = false,
            IsElevated = true,
            Disks = inv.Disks.Select(d => d with { Bus = virtualDisks ? BusKind.FileBackedVirtual : d.Bus }).ToList(),
        };
    }

    public static DiskInfo Disk(StorageInventory inv, int n) => inv.Disks.First(d => d.Number == n);
    public static PartitionInfo Letter(StorageInventory inv, char l) => inv.Disks.SelectMany(d => d.Partitions).First(p => p.Volume?.DriveLetter == l);
}

public static class SimulatorTests
{
    private static SimulationResult Sim(DiskInfo d, params DiskOperation[] ops) => LayoutSimulator.Simulate(d, ops);
    private static bool HasError(SimulationResult r, string key) => r.AllIssues.Any(i => i.IsError && i.Key == key);

    [Test]
    public static void Create_in_free_space_adds_pending_partition()
    {
        var inv = Fixtures.Real();
        var d1 = Fixtures.Disk(inv, 1);
        var gapStart = Fixtures.Letter(inv, 'D').EndBytes;
        var r = Sim(d1, new CreatePartitionOp(1, gapStart, 100 * Fixtures.GiB, "NTFS", "Games", 'G'));
        Assert.True(r.IsValid, string.Join(",", r.AllIssues.Select(i => i.Key)));
        var created = r.After.Partitions.Single(p => p.IsPending);
        Assert.Equal('G', created.Volume!.DriveLetter);
        Assert.Equal((byte)0x07, created.MbrType);
        Assert.Equal(3, r.After.Partitions.Count);
        Assert.Equal(2, Fixtures.Disk(inv, 1).Partitions.Count); // input untouched
    }

    [Test]
    public static void Create_rejects_overlap_misalignment_fat32_and_letter_conflicts()
    {
        var inv = Fixtures.Real();
        var d1 = Fixtures.Disk(inv, 1);
        var gap = Fixtures.Letter(inv, 'D').EndBytes;
        Assert.True(HasError(Sim(d1, new CreatePartitionOp(1, Fixtures.MiB, Fixtures.GiB, "NTFS", null, null)), "Op.Error.NoSpace"));
        Assert.True(HasError(Sim(d1, new CreatePartitionOp(1, gap + 4096, Fixtures.GiB, "NTFS", null, null)), "Op.Error.Granularity"));
        Assert.True(HasError(Sim(d1, new CreatePartitionOp(1, gap, 64 * Fixtures.GiB, "FAT32", null, null)), "Op.Error.Fat32Limit"));
        Assert.True(HasError(Sim(d1, new CreatePartitionOp(1, gap, Fixtures.GiB, "NTFS", null, 'D')), "Op.Error.LetterInUse"));
        Assert.True(HasError(LayoutSimulator.Simulate(d1, new[] { new CreatePartitionOp(1, gap, Fixtures.GiB, "NTFS", null, 'C') }, new HashSet<char> { 'C' }), "Op.Error.LetterInUse"));
        Assert.True(HasError(Sim(d1, new CreatePartitionOp(1, gap, 8 * Fixtures.MiB, "NTFS", null, null)), "Op.Error.TooSmall"));
        Assert.True(HasError(Sim(d1, new CreatePartitionOp(1, gap, Fixtures.GiB, "HFS+", null, null)), "Op.Error.FileSystem"));
    }

    [Test]
    public static void System_partitions_are_protected()
    {
        var inv = Fixtures.Real();
        var d0 = Fixtures.Disk(inv, 0);
        var c = Fixtures.Letter(inv, 'C');
        var esp = d0.Partitions.First(p => p.Role == PartitionRole.EfiSystem);
        var msr = d0.Partitions.First(p => p.Role == PartitionRole.MicrosoftReserved);
        var rec = d0.Partitions.First(p => p.Role == PartitionRole.Recovery);

        Assert.True(HasError(Sim(d0, new DeletePartitionOp(0, c.OffsetBytes)), "Op.Error.Protected"));
        Assert.True(HasError(Sim(d0, new FormatPartitionOp(0, c.OffsetBytes, "NTFS", null)), "Op.Error.Protected"));
        Assert.True(HasError(Sim(d0, new SetDriveLetterOp(0, c.OffsetBytes, 'W')), "Op.Error.Protected"));
        foreach (var p in new[] { esp, msr, rec })
        {
            Assert.True(HasError(Sim(d0, new DeletePartitionOp(0, p.OffsetBytes)), "Op.Error.Protected"));
            Assert.True(HasError(Sim(d0, new ResizePartitionOp(0, p.OffsetBytes, p.SizeBytes + Fixtures.GiB)), "Op.Error.Protected"));
        }
    }

    [Test]
    public static void Windows_volume_can_shrink_with_warning_but_not_below_used_space()
    {
        var inv = Fixtures.Real();
        var d0 = Fixtures.Disk(inv, 0);
        var c = Fixtures.Letter(inv, 'C');
        var used = c.Volume!.UsedBytes!.Value;

        var ok = Sim(d0, new ResizePartitionOp(0, c.OffsetBytes, (used + 10 * Fixtures.GiB) / Fixtures.MiB * Fixtures.MiB));
        Assert.True(ok.IsValid);
        Assert.True(ok.AllIssues.Any(i => i.Key == "Op.Warn.WindowsVolume"));

        Assert.True(HasError(Sim(d0, new ResizePartitionOp(0, c.OffsetBytes, used / Fixtures.MiB * Fixtures.MiB)), "Op.Error.ShrinkBelowUsed"));
        // C: is followed directly by the recovery partition: no room to extend.
        Assert.True(HasError(Sim(d0, new ResizePartitionOp(0, c.OffsetBytes, c.SizeBytes + Fixtures.GiB)), "Op.Error.NoAdjacentSpace"));
    }

    [Test]
    public static void Extend_into_following_free_space_only()
    {
        var inv = Fixtures.Real();
        var d1 = Fixtures.Disk(inv, 1);
        var dPart = Fixtures.Letter(inv, 'D');
        var max = LayoutSimulator.MaxExtendSize(d1, dPart);
        Assert.Equal(1500 * Fixtures.GiB, max); // up to E:'s start
        Assert.True(Sim(d1, new ResizePartitionOp(1, dPart.OffsetBytes, max)).IsValid);
        Assert.True(HasError(Sim(d1, new ResizePartitionOp(1, dPart.OffsetBytes, max + Fixtures.MiB)), "Op.Error.NoAdjacentSpace"));

        var e = Fixtures.Letter(inv, 'E');
        Assert.True(HasError(Sim(d1, new ResizePartitionOp(1, e.OffsetBytes, e.SizeBytes - Fixtures.GiB)), "Op.Error.ResizeFileSystem")); // exFAT
    }

    [Test]
    public static void Steps_are_judged_against_earlier_steps()
    {
        var inv = Fixtures.Real();
        var d1 = Fixtures.Disk(inv, 1);
        var dPart = Fixtures.Letter(inv, 'D');
        // delete D:, then create in the space it occupied
        var r = Sim(d1, new DeletePartitionOp(1, dPart.OffsetBytes), new CreatePartitionOp(1, dPart.OffsetBytes, 200 * Fixtures.GiB, "exFAT", "New", null));
        Assert.True(r.IsValid);
        Assert.True(r.DestroysData);
        Assert.True(r.AllIssues.Any(i => i.Key == "Op.Warn.DataLoss"));
        // the reverse order is invalid, and a failing step does not change the layout
        var bad = Sim(d1, new CreatePartitionOp(1, dPart.OffsetBytes, 200 * Fixtures.GiB, "exFAT", null, null), new DeletePartitionOp(1, dPart.OffsetBytes));
        Assert.True(!bad.Steps[0].IsValidStep());
        Assert.Equal(1, bad.After.Partitions.Count);
    }

    private static bool IsValidStep(this StepResult s) => !s.HasErrors;

    [Test]
    public static void Label_and_letter_rules()
    {
        var inv = Fixtures.Real();
        var d2 = Fixtures.Disk(inv, 2);
        var f = Fixtures.Letter(inv, 'F');
        Assert.True(HasError(Sim(d2, new SetLabelOp(2, f.OffsetBytes, "TWELVE_CHARS")), "Op.Error.LabelTooLong")); // FAT32: 11
        Assert.True(Sim(d2, new SetLabelOp(2, f.OffsetBytes, "BACKUP")).IsValid);
        Assert.True(HasError(Sim(d2, new SetLabelOp(2, f.OffsetBytes, "a:b")), "Op.Error.LabelChars"));
        Assert.True(Sim(d2, new SetDriveLetterOp(2, f.OffsetBytes, 'U')).IsValid);
        Assert.True(HasError(Sim(d2, new SetDriveLetterOp(2, f.OffsetBytes, 'F')), "Op.Error.NoChange"));
        Assert.True(HasError(Sim(d2, new SetDriveLetterOp(2, f.OffsetBytes, 'A')), "Op.Error.LetterInvalid"));
        var removed = Sim(d2, new SetDriveLetterOp(2, f.OffsetBytes, null));
        Assert.True(removed.IsValid);
        Assert.Equal(null, removed.After.Partitions[0].Volume!.DriveLetter);
    }

    [Test]
    public static void Mbr_primary_limit_and_dynamic_disks()
    {
        const ulong G = Fixtures.GiB;
        PartitionInfo P(int n, ulong off) => new()
        {
            DiskNumber = 5, PartitionNumber = n, OffsetBytes = off, SizeBytes = G, Style = PartitionStyle.Mbr, Role = PartitionRole.BasicData, MbrType = 7,
        };
        var disk = new DiskInfo
        {
            Number = 5, DevicePath = "x", SizeBytes = 100 * G, Style = PartitionStyle.Mbr,
            Partitions = new[] { P(1, G), P(2, 3 * G), P(3, 5 * G), P(4, 7 * G) },
        };
        Assert.True(HasError(Sim(disk, new CreatePartitionOp(5, 20 * G, G, "NTFS", null, null)), "Op.Error.MbrPrimaryLimit"));
        Assert.True(HasError(Sim(disk with { IsDynamic = true }, new DeletePartitionOp(5, G)), "Op.Error.Dynamic"));
        Assert.True(HasError(Sim(disk with { Style = PartitionStyle.Raw }, new DeletePartitionOp(5, G)), "Op.Error.DiskStyle"));
    }
}

public static class QueueTests
{
    [Test]
    public static void Queue_rejects_invalid_and_sample_operations()
    {
        var q = new OperationQueue();
        Assert.True(q.TryAdd(new DeletePartitionOp(1, 0)).Any(i => i.Key == "Op.Error.NoInventory"));
        q.Rebase(SampleInventoryProvider.Create());
        Assert.True(q.TryAdd(new DeletePartitionOp(1, Fixtures.MiB)).Any(i => i.Key == "Op.Error.SampleData"));

        var inv = Fixtures.Real();
        q.Rebase(inv);
        var c = Fixtures.Letter(inv, 'C');
        Assert.True(q.TryAdd(new DeletePartitionOp(0, c.OffsetBytes)).Any(i => i.IsError));
        Assert.Equal(0, q.Count);

        var gap = Fixtures.Letter(inv, 'D').EndBytes;
        Assert.True(!q.TryAdd(new CreatePartitionOp(1, gap, 10 * Fixtures.GiB, "NTFS", "X", 'X')).Any(i => i.IsError));
        Assert.Equal(1, q.Count);
        Assert.True(q.IsValid);
    }

    [Test]
    public static void Letters_reserved_by_other_disks_pending_ops_conflict()
    {
        var inv = Fixtures.Real();
        var q = new OperationQueue();
        q.Rebase(inv);
        var gap = Fixtures.Letter(inv, 'D').EndBytes;
        q.TryAdd(new CreatePartitionOp(1, gap, 10 * Fixtures.GiB, "NTFS", null, 'K'));
        var f = Fixtures.Letter(inv, 'F');
        Assert.True(q.TryAdd(new SetDriveLetterOp(2, f.OffsetBytes, 'K')).Any(i => i.Key == "Op.Error.LetterInUse"));
    }

    [Test]
    public static void Removing_an_earlier_step_flags_dependents_and_undo_works()
    {
        var inv = Fixtures.Real();
        var q = new OperationQueue();
        q.Rebase(inv);
        var d = Fixtures.Letter(inv, 'D');
        q.TryAdd(new DeletePartitionOp(1, d.OffsetBytes));
        q.TryAdd(new CreatePartitionOp(1, d.OffsetBytes, 50 * Fixtures.GiB, "NTFS", null, null));
        Assert.Equal(2, q.Count);
        Assert.True(q.IsValid);
        q.RemoveAt(0);
        Assert.Equal(1, q.Count);
        Assert.True(!q.IsValid, "create now overlaps D: and must block Apply");
        Assert.True(q.UndoLast());
        Assert.Equal(0, q.Count);
    }

    [Test]
    public static void Fingerprint_detects_layout_changes()
    {
        var d = Fixtures.Disk(Fixtures.Real(), 1);
        var same = Fixtures.Disk(Fixtures.Real(), 1);
        Assert.Equal(DiskFingerprint.Compute(d), DiskFingerprint.Compute(same));
        var changed = d with { Partitions = d.Partitions.Skip(1).ToList() };
        Assert.True(DiskFingerprint.Compute(d) != DiskFingerprint.Compute(changed));
    }
}

public static class EngineTests
{
    private sealed class Privileges(bool elevated) : IPrivilegeService
    {
        public bool IsElevated => elevated;
        public bool TryRestartElevated(string[] args) => false;
    }

    /// <summary>A fake disk: executing an operation applies the simulator's result to the fake inventory.</summary>
    private sealed class FakeWorld : IStorageInventoryProvider, IOperationExecutor, IPartitionTableBackup, IOperationHistory
    {
        public StorageInventory Inventory;
        public readonly List<DiskOperation> Executed = new();
        public readonly List<ExecutionReport> History = new();
        public bool FailBackup, FailExecute, Misbehave;
        public int BackupCount;

        public FakeWorld(StorageInventory inv) => Inventory = inv;

        public Task<StorageInventory> GetInventoryAsync(CancellationToken ct = default) => Task.FromResult(Inventory);

        public Task ExecuteAsync(DiskOperation op, DiskInfo liveDisk, CancellationToken ct)
        {
            if (FailExecute) throw new OperationFailedException("Exec.Error.Wmi", "Resize returned 2");
            Executed.Add(op);
            var after = LayoutSimulator.Simulate(liveDisk, new[] { op }).After;
            // pending → real
            after = after with { Partitions = after.Partitions.Select(p => p with { IsPending = false }).ToList() };
            if (Misbehave) after = after with { Partitions = after.Partitions.Select(p => p with { SizeBytes = p.SizeBytes + 64 * Fixtures.MiB }).ToList() };
            Inventory = Inventory with { Disks = Inventory.Disks.Select(d => d.Number == after.Number ? after : d).ToList() };
            return Task.CompletedTask;
        }

        public Task<BackupRecord> BackupAsync(DiskInfo disk, StorageInventory inventory, CancellationToken ct)
        {
            if (FailBackup) throw new IOException("disk full");
            BackupCount++;
            return Task.FromResult(new BackupRecord(disk.Number, "/tmp/b", "HASH", DateTimeOffset.Now, false));
        }

        public void Append(ExecutionReport report) => History.Add(report);
    }

    private static (GuardedExecutionEngine, FakeWorld) Make(StorageInventory inv, bool elevated = true)
    {
        var w = new FakeWorld(inv);
        return (new GuardedExecutionEngine(w, w, w, new Privileges(elevated), w, NullLog.Instance), w);
    }

    private static DiskOperation[] Plan(StorageInventory inv)
    {
        var d = Fixtures.Letter(inv, 'D');
        var gap = d.EndBytes;
        return new DiskOperation[]
        {
            new SetLabelOp(1, d.OffsetBytes, "Archive"),
            new CreatePartitionOp(1, gap, 100 * Fixtures.GiB, "NTFS", "Games", 'G'),
            new ResizePartitionOp(1, d.OffsetBytes, d.SizeBytes - 100 * Fixtures.GiB),
        };
    }

    [Test]
    public static async Task Happy_path_backs_up_then_executes_and_verifies_each_step()
    {
        var inv = Fixtures.Real();
        var (engine, w) = Make(inv);
        var progress = new List<ExecutionProgress>();
        var report = await engine.ExecuteAsync(Plan(inv), inv, new ExecutionPolicy(false), "APPLY", new SyncProgress(progress));
        Assert.True(report.Succeeded, report.FailureKey + " " + report.FailureDetail);
        Assert.Equal(1, w.BackupCount);
        Assert.Equal(3, w.Executed.Count);
        Assert.True(report.Steps.All(s => s.State == StepState.Succeeded));
        Assert.Equal(6, progress.Count);
        Assert.Equal(1, w.History.Count);
        Assert.True(Fixtures.Disk(w.Inventory, 1).Partitions.Any(p => p.Volume?.DriveLetter == 'G'));
    }

    [Test]
    public static async Task Preflight_refusals_write_nothing()
    {
        var inv = Fixtures.Real();
        async Task Expect(string key, StorageInventory planBase, bool elevated = true, string confirm = "APPLY", bool physical = false, Action<FakeWorld>? tweak = null)
        {
            var (engine, w) = Make(planBase, elevated);
            tweak?.Invoke(w);
            var r = await engine.ExecuteAsync(Plan(inv), planBase, new ExecutionPolicy(physical), confirm);
            Assert.Equal(key, r.FailureKey);
            Assert.True(r.NothingWritten && w.Executed.Count == 0, key + " must not write");
            Assert.Equal(1, w.History.Count);
        }

        await Expect("Exec.Error.NotElevated", inv, elevated: false);
        await Expect("Exec.Error.Confirmation", inv, confirm: "apply");
        await Expect("Op.Error.SampleData", inv with { IsSampleData = true });
        await Expect("Exec.Error.PhysicalPolicy", Fixtures.Real(virtualDisks: false));
        await Expect("Exec.Error.Backup", inv, tweak: w => w.FailBackup = true);
        // layout changed between planning and Apply
        await Expect("Exec.Error.LayoutChanged", inv, tweak: w =>
            w.Inventory = inv with { Disks = inv.Disks.Select(d => d.Number == 1 ? d with { Partitions = d.Partitions.Take(1).ToList() } : d).ToList() });
    }

    [Test]
    public static async Task Physical_disks_allowed_only_with_policy()
    {
        var inv = Fixtures.Real(virtualDisks: false);
        var (engine, w) = Make(inv);
        var r = await engine.ExecuteAsync(Plan(inv), inv, new ExecutionPolicy(AllowPhysicalDisks: true), "APPLY");
        Assert.True(r.Succeeded);
    }

    [Test]
    public static async Task Failure_stops_at_first_failed_step()
    {
        var inv = Fixtures.Real();
        var (engine, w) = Make(inv);
        w.FailExecute = true;
        var r = await engine.ExecuteAsync(Plan(inv), inv, new ExecutionPolicy(false), "APPLY");
        Assert.True(!r.Succeeded && !r.NothingWritten);
        Assert.Equal(StepState.Failed, r.Steps[0].State);
        Assert.True(r.Steps.Skip(1).All(s => s.State == StepState.NotRun));
        Assert.Equal("Exec.Error.Wmi", r.FailureKey);
    }

    [Test]
    public static async Task Verification_mismatch_stops_execution()
    {
        var inv = Fixtures.Real();
        var (engine, w) = Make(inv);
        w.Misbehave = true;
        var r = await engine.ExecuteAsync(Plan(inv), inv, new ExecutionPolicy(false), "APPLY");
        Assert.Equal("Exec.Error.Verify", r.FailureKey);
        Assert.Equal(1, w.Executed.Count);
    }

    [Test]
    public static async Task Cancellation_happens_only_between_steps()
    {
        var inv = Fixtures.Real();
        var (engine, w) = Make(inv);
        using var cts = new CancellationTokenSource();
        var p = new SyncProgress(new List<ExecutionProgress>(), e => { if (e.State == StepState.Succeeded) cts.Cancel(); });
        var r = await engine.ExecuteAsync(Plan(inv), inv, new ExecutionPolicy(false), "APPLY", p, cts.Token);
        Assert.Equal("Exec.Error.Cancelled", r.FailureKey);
        Assert.Equal(1, w.Executed.Count);
        Assert.Equal(StepState.Succeeded, r.Steps[0].State);
    }

    [Test]
    public static void History_is_written_as_json_lines()
    {
        var path = Path.Combine(Path.GetTempPath(), "lasteast-hist-" + Guid.NewGuid().ToString("N") + ".jsonl");
        var h = new JsonlOperationHistory(path);
        h.Append(new ExecutionReport
        {
            Succeeded = true, NothingWritten = false, Started = DateTimeOffset.Now, Finished = DateTimeOffset.Now,
            Steps = new[] { new StepOutcome(new SetLabelOp(1, 1, "x"), StepState.Succeeded, TimeSpan.FromSeconds(1)) },
        });
        var lines = h.ReadAll();
        Assert.Equal(1, lines.Count);
        Assert.True(lines[0].Contains("SetLabelOp") && lines[0].Contains("Succeeded"));
        File.Delete(path);
    }

    private sealed class SyncProgress(List<ExecutionProgress> list, Action<ExecutionProgress>? on = null) : IProgress<ExecutionProgress>
    {
        public void Report(ExecutionProgress value) { list.Add(value); on?.Invoke(value); }
    }
}
