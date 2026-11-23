using System.Runtime.Versioning;
using Lasteast.Core.Logging;
using Lasteast.Core.Models;
using Lasteast.Core.Operations;
using Lasteast.Storage.Windows.Detection;
using Lasteast.Storage.Windows.Operations;

namespace Lasteast.IntegrationTests;

[SupportedOSPlatform("windows")]
public static class AttributeIntegrationTests
{
    private static (GuardedExecutionEngine, WindowsStorageInventoryProvider) Engine()
    {
        var dir = Path.Combine(Path.GetTempPath(), "lasteast-itest", "attr-" + Guid.NewGuid().ToString("N"));
        var log = new FileLog(Path.Combine(dir, "logs"));
        var priv = new WindowsPrivilegeService();
        var provider = new WindowsStorageInventoryProvider(log, priv);
        return (new GuardedExecutionEngine(provider, new WindowsOperationExecutor(provider, log, Path.Combine(dir, "journal")),
            new PartitionTableBackupService(Path.Combine(dir, "backups")), priv, new JsonlOperationHistory(Path.Combine(dir, "h.jsonl")), log), provider);
    }

    [Test]
    public static async Task Mbr_active_hidden_and_type_change_through_windows()
    {
        using var vd = await VirtualDisk.CreateAsync(1513, "convert mbr", "create partition primary size=200", "format fs=ntfs quick label=LE-A1",
            "create partition primary size=200", "format fs=ntfs quick label=LE-A2");
        var (engine, provider) = Engine();
        var d = await vd.ReadAsync();
        var second = d.Partitions.Single(p => p.Volume?.Label == "LE-A2").OffsetBytes;
        var first = d.Partitions.Single(p => p.Volume?.Label == "LE-A1").OffsetBytes;

        async Task Run(DiskOperation op)
        {
            var r = await engine.ExecuteAsync(new[] { op }, await provider.GetInventoryAsync(), new ExecutionPolicy(false), "APPLY");
            Assert.True(r.Succeeded, $"{op}: {r.FailureKey} {r.FailureDetail}");
        }
        await Run(new SetActiveOp(vd.Number, second));
        Assert.True((await vd.ReadAsync()).Partitions.Single(p => p.OffsetBytes == second).MbrActive);
        await Run(new SetActiveOp(vd.Number, first));
        var after = await vd.ReadAsync();
        Assert.True(after.Partitions.Single(p => p.OffsetBytes == first).MbrActive && !after.Partitions.Single(p => p.OffsetBytes == second).MbrActive);

        await Run(new SetHiddenOp(vd.Number, second, true));
        Assert.True((await vd.ReadAsync()).Partitions.Single(p => p.OffsetBytes == second).IsHidden);
        await Run(new SetHiddenOp(vd.Number, second, false));
        await Run(new SetTypeOp(vd.Number, second, null, 0x83));
        Assert.Equal((byte?)0x83, (await vd.ReadAsync()).Partitions.Single(p => p.OffsetBytes == second).MbrType);
    }

    [Test]
    public static async Task Gpt_hidden_and_type_change_through_windows()
    {
        using var vd = await VirtualDisk.CreateAsync(1614, "convert gpt", "create partition primary size=200", "format fs=ntfs quick label=LE-G1");
        var (engine, provider) = Engine();
        var off = (await vd.ReadAsync()).Partitions.Single(p => p.Volume?.Label == "LE-G1").OffsetBytes;
        async Task Run(DiskOperation op)
        {
            var r = await engine.ExecuteAsync(new[] { op }, await provider.GetInventoryAsync(), new ExecutionPolicy(false), "APPLY");
            Assert.True(r.Succeeded, $"{op}: {r.FailureKey} {r.FailureDetail}");
        }
        await Run(new SetHiddenOp(vd.Number, off, true));
        Assert.True((await vd.ReadAsync()).Partitions.Single(p => p.OffsetBytes == off).IsHidden);
        await Run(new SetHiddenOp(vd.Number, off, false));
        await Run(new SetTypeOp(vd.Number, off, PartitionTypes.GptLinuxFilesystem, null));
        Assert.Equal((Guid?)PartitionTypes.GptLinuxFilesystem, (await vd.ReadAsync()).Partitions.Single(p => p.OffsetBytes == off).GptType);
    }
}
