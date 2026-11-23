using Lasteast.Core.Abstractions;
using Lasteast.Core.Localization;
using Lasteast.Core.Logging;
using Lasteast.Core.Models;
using Lasteast.Core.Operations;
using Lasteast.Core.Settings;
using Lasteast.Core.SmartFix;
using Lasteast.Presentation.Services;
using Lasteast.Presentation.ViewModels;

namespace Lasteast.Tests;

public static class SmartFixTests
{
    private const ulong GiB = Fixtures.GiB;

    /// <summary>Windows disk where C: is followed by 50 GiB of free space, then the recovery partition.</summary>
    private static StorageInventory WithFreeAfterC(ulong cFree = 3 * GiB)
    {
        var inv = Fixtures.Real();
        var d0 = Fixtures.Disk(inv, 0);
        var c = d0.Partitions.First(p => p.Volume?.IsWindowsVolume == true);
        var rec = d0.Partitions.First(p => p.Role == PartitionRole.Recovery);
        var newC = c with { SizeBytes = c.SizeBytes - 50 * GiB, Volume = c.Volume! with { CapacityBytes = c.SizeBytes - 50 * GiB, FreeBytes = cFree } };
        var parts = d0.Partitions.Select(p => p == c ? newC : p).ToList();
        return inv with { Disks = inv.Disks.Select(d => d.Number == 0 ? d with { Partitions = parts } : d).ToList() };
    }

    [Test]
    public static void C_expansion_uses_adjacent_space()
    {
        var plan = CDriveExpansion.Analyze(WithFreeAfterC());
        Assert.Equal(CDriveBlocker.None, plan.Blocker);
        Assert.True(plan.CanExpand);
        Assert.Equal(50 * GiB, plan.AdjacentFreeBytes);
        Assert.True(LayoutSimulator.Simulate(plan.Disk!, new[] { plan.Operation! }).IsValid);
    }

    [Test]
    public static void C_expansion_moves_recovery_partition_blocker()
    {
        var inv = Fixtures.Real();
        // free space at the very end, after the recovery partition
        var d0 = Fixtures.Disk(inv, 0);
        var grown = d0 with { SizeBytes = d0.SizeBytes + 20 * GiB, UsableEndBytes = d0.UsableEndBytes + 20 * GiB };
        inv = inv with { Disks = inv.Disks.Select(d => d.Number == 0 ? grown : d).ToList() };
        var plan = CDriveExpansion.Analyze(inv);
        // Since Phase 5 the recovery partition is moved to the end and C: is extended.
        Assert.Equal(CDriveBlocker.None, plan.Blocker);
        Assert.Equal(PartitionRole.Recovery, plan.BlockingPartition!.Role);
        Assert.True(plan.OtherFreeBytesOnDisk >= 20 * GiB);
        Assert.True(plan.CanExpand && plan.MoveFirst is not null);
        Assert.True(plan.AdjacentFreeBytes >= 20 * GiB - 2 * 1024 * 1024);

        // A non-recovery partition in the way is still only explained.
        var other = grown with { Partitions = grown.Partitions.Select(p => p.Role == PartitionRole.Recovery ? p with { Role = PartitionRole.BasicData, GptType = PartitionTypes.GptBasicData } : p).ToList() };
        var plan2 = CDriveExpansion.Analyze(inv with { Disks = inv.Disks.Select(d => d.Number == 0 ? other : d).ToList() });
        Assert.Equal(CDriveBlocker.PartitionInTheWay, plan2.Blocker);
        Assert.True(!plan2.CanExpand);

        Assert.Equal(CDriveBlocker.NoFreeSpace, CDriveExpansion.Analyze(Fixtures.Real()).Blocker);
    }

    [Test]
    public static void Analyzer_reports_low_c_with_queueable_fix_and_healthy_checks()
    {
        var f = SmartFixAnalyzer.Analyze(WithFreeAfterC(), new SystemSnapshot { UserTempBytes = 3 * GiB, RecycleBinBytes = 2 * GiB });
        var c = f.Single(x => x.Id == "c-low");
        Assert.Equal(SmartSeverity.High, c.Severity);
        Assert.Equal(FixKind.QueueOperations, c.Fix);
        Assert.True(c.Operations.Single() is ResizePartitionOp);
        Assert.True(f.Any(x => x.Id == "temp" && x.Fix == FixKind.Navigate && x.NavigateTo == "Nav.Cleanup"));
        Assert.True(f.Any(x => x.Id == "recycle"));
        Assert.True(f.Any(x => x.Severity == SmartSeverity.Healthy && x.Id == "layout-ok"));
        Assert.True(f.Any(x => x.Id == "health" && x.Fix == FixKind.Planned)); // stated, not guessed
        // no duplicate offer for the gap after C:
        Assert.True(!f.Any(x => x.Id.StartsWith("unalloc-0-")));
        // ordered by severity
        Assert.True(f.Select(x => (int)x.Severity).SequenceEqual(f.Select(x => (int)x.Severity).OrderBy(x => x)));
    }

    [Test]
    public static void Unallocated_space_gets_a_valid_fix()
    {
        var f = SmartFixAnalyzer.Analyze(Fixtures.Real());
        var u = f.Single(x => x.Id.StartsWith("unalloc-1-"));
        Assert.Equal(FixKind.QueueOperations, u.Fix);
        var op = u.Operations.Single();
        Assert.True(op is ResizePartitionOp, "D: ends where the gap begins, so extending it is the fix");
        Assert.True(LayoutSimulator.Simulate(Fixtures.Disk(Fixtures.Real(), 1), new[] { op }).IsValid);
    }

    [Test]
    public static void Every_queued_fix_passes_the_simulator()
    {
        foreach (var inv in new[] { Fixtures.Real(), WithFreeAfterC() })
        foreach (var f in SmartFixAnalyzer.Analyze(inv).Where(x => x.Fix == FixKind.QueueOperations))
        {
            var disk = inv.Disks.First(d => d.Number == f.Operations[0].DiskNumber);
            Assert.True(LayoutSimulator.Simulate(disk, f.Operations).IsValid, f.Id);
        }
    }

    private sealed class Provider(StorageInventory inv) : IStorageInventoryProvider
    {
        public Task<StorageInventory> GetInventoryAsync(CancellationToken ct = default) => Task.FromResult(inv);
    }

    private sealed class Probe : ISystemProbe
    {
        public Task<SystemSnapshot> CaptureAsync(CancellationToken ct = default) => Task.FromResult(new SystemSnapshot { UserTempBytes = 2 * GiB });
    }

    private sealed class Priv : IPrivilegeService
    {
        public bool IsElevated => true;
        public bool TryRestartElevated(string[] a) => false;
    }

    [Test]
    public static async Task Scan_my_computer_then_fix_now_opens_pending_with_operation()
    {
        Loc.Instance.SetLanguage("en");
        var dir = Path.Combine(Path.GetTempPath(), "lasteast-sf-" + Guid.NewGuid().ToString("N"));
        var vm = new MainViewModel(new Provider(WithFreeAfterC()), new Priv(), new AppSettings(), new JsonSettingsStore(Path.Combine(dir, "s.json")),
            new NullThemeService(), new NullShellService(), NullLog.Instance, dir, Array.Empty<string>(), null, new Probe());
        await vm.ScanMyComputerCommand.ExecuteAsync();

        Assert.Equal(vm.SmartFixPage, vm.CurrentPage);
        var sf = vm.SmartFixPage;
        Assert.True(sf.HasScanned && sf.CanExpandC);
        Assert.True(sf.Groups.Count >= 3);
        Assert.Equal("High priority", sf.Groups[0].Title);
        Assert.True(sf.Groups.SelectMany(g => g.Items).All(i => !i.Title.StartsWith('[')), "missing translation");

        sf.ExpandCCommand.Execute(null);
        Assert.Equal(1, vm.Queue.Count);
        Assert.Equal(vm.PendingPage, vm.CurrentPage);
        Assert.True(vm.PendingPage.Items[0].Text.StartsWith("Resize C:"), vm.PendingPage.Items[0].Text);

        // pressing the same fix again does not duplicate it
        sf.Groups.SelectMany(g => g.Items).First(i => i.Finding.Id == "c-low").FixCommand!.Execute(null);
        Assert.Equal(1, vm.Queue.Count);

        // Hebrew re-renders the report
        vm.SettingsPage.SelectedLanguage = Loc.Languages.First(l => l.Code == "he");
        Assert.Equal("עדיפות גבוהה", sf.Groups[0].Title);
        vm.SettingsPage.SelectedLanguage = Loc.Languages[0];
    }
}
