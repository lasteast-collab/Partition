using Lasteast.Core.Operations;
using Lasteast.Core.Abstractions;
using Lasteast.Core.Localization;
using Lasteast.Core.Logging;
using Lasteast.Core.Models;
using Lasteast.Core.Settings;
using Lasteast.Presentation.Services;
using Lasteast.Presentation.ViewModels;

namespace Lasteast.Tests;

public static class OperationUiTests
{
    private sealed class Provider(StorageInventory inv) : IStorageInventoryProvider
    {
        public Task<StorageInventory> GetInventoryAsync(CancellationToken ct = default) => Task.FromResult(inv);
    }

    private sealed class Privileges(bool elevated) : IPrivilegeService
    {
        public bool IsElevated => elevated;
        public bool TryRestartElevated(string[] args) => false;
    }

    private static async Task<MainViewModel> Create(bool elevated = false, bool virtualDisks = true)
    {
        Loc.Instance.SetLanguage("en");
        var dir = Path.Combine(Path.GetTempPath(), "lasteast-ui-" + Guid.NewGuid().ToString("N"));
        var vm = new MainViewModel(new Provider(Fixtures.Real(virtualDisks)), new Privileges(elevated), new AppSettings(),
            new JsonSettingsStore(Path.Combine(dir, "s.json")), new NullThemeService(), new NullShellService(), NullLog.Instance, dir,
            Array.Empty<string>());
        await vm.RefreshAsync();
        return vm;
    }

    [Test]
    public static async Task Create_from_unallocated_goes_to_queue_and_preview()
    {
        var vm = await Create();
        var free = vm.Dashboard.Disks[1].Segments.First(s => s.IsUnallocated);
        vm.Dashboard.Select(free);
        Assert.Equal(2, vm.Dashboard.Actions.Count); // create + convert disk style
        vm.Dashboard.Actions[0].Command.Execute(null);
        var editor = vm.Dashboard.Editor!;
        Assert.Equal(EditorMode.Create, editor.Mode);
        editor.SizeText = "100";
        editor.Label = "Games";
        Assert.True(editor.Issues.All(i => !i.IsError), string.Join("; ", editor.Issues.Select(i => i.Text)));
        Assert.True(editor.AddCommand.CanExecute(null));
        editor.AddCommand.Execute(null);

        Assert.Equal(1, vm.Queue.Count);
        Assert.Equal(null, vm.Dashboard.Editor);
        Assert.Equal("Apply (1)", vm.ApplyText);
        Assert.True(vm.OpenPendingCommand.CanExecute(null));
        Assert.Equal(1, vm.PendingPage.Items.Count);
        Assert.True(vm.PendingPage.Items[0].Text.StartsWith("Create 100.00 GB NTFS partition"), vm.PendingPage.Items[0].Text);
        var preview = vm.PendingPage.Previews.Single();
        Assert.Equal(3, preview.Before.Segments.Count);
        Assert.Equal(4, preview.After.Segments.Count); // D:, new, remaining free, E:
        Assert.True(preview.After.Segments.Any(s => s.IsPending));
    }

    [Test]
    public static async Task Merge_split_and_wipe_editors_queue_validated_groups()
    {
        var vm = await Create();
        var d = vm.Dashboard.Disks[1].Segments.First(s => !s.IsUnallocated);
        vm.Dashboard.Select(d);
        Assert.True(vm.Dashboard.Actions.Any(a => a.Title == "Merge with next") && vm.Dashboard.Actions.Any(a => a.Title == "Split"));
        vm.Dashboard.OpenEditor(EditorMode.Merge);
        var merge = vm.Dashboard.Editor!;
        Assert.True(merge.SelectedChoice is not null, "merge partner offered");
        var ops = merge.BuildOperations()!;
        Assert.True(ops.Count == 3 && ops[0] is CopyFilesOp && ops[1] is DeletePartitionOp && ops[2] is ResizePartitionOp);
        Assert.True(merge.Issues.All(i => !i.IsError), string.Join("; ", merge.Issues.Select(i => i.Text)));
        merge.AddCommand.Execute(null);
        Assert.Equal(3, vm.Queue.Count);
        // reordering that would break the dependency is refused
        Assert.True(!vm.Queue.Move(2, -1), "resize cannot run before the delete");
        vm.Queue.Clear();

        vm.Dashboard.Select(d);
        vm.Dashboard.OpenEditor(EditorMode.Split);
        var split = vm.Dashboard.Editor!;
        Assert.True(split.ShowSlider);
        split.SliderValue = split.SliderMax;
        Assert.True(split.BuildOperations()!.Count == 2);

        vm.Dashboard.Select(d);
        vm.Dashboard.OpenEditor(EditorMode.WipeDisk);
        var wipe = vm.Dashboard.Editor!;
        Assert.True(!wipe.AddCommand.CanExecute(null), "serial confirmation required");
        Assert.True(wipe.Issues.Any(i => i.IsError));
    }

    [Test]
    public static async Task Protected_partition_editor_shows_error_and_cannot_add()
    {
        var vm = await Create();
        var c = vm.Dashboard.Disks[0].Segments.First(s => s.Title == "C:");
        vm.Dashboard.Select(c);
        vm.Dashboard.OpenEditor(EditorMode.Delete);
        var e = vm.Dashboard.Editor!;
        Assert.True(e.Issues.Any(i => i.IsError && i.Text.Contains("protected")));
        Assert.True(!e.AddCommand.CanExecute(null));
        Assert.Equal(0, vm.Queue.Count);
    }

    [Test]
    public static async Task Apply_is_blocked_with_a_clear_reason()
    {
        var vm = await Create(elevated: false);
        var d = vm.Dashboard.Disks[1].Segments.First(s => s.Title == "D:");
        vm.Dashboard.Select(d);
        vm.Dashboard.OpenEditor(EditorMode.Label);
        vm.Dashboard.Editor!.Label = "Archive";
        vm.Dashboard.Editor.AddCommand.Execute(null);

        var page = vm.PendingPage;
        Assert.True(page.BlockReason!.Contains("administrator"));
        page.BackupAcknowledged = true;
        page.Confirmation = "APPLY";
        Assert.True(!page.CanApply);

        var physical = await Create(elevated: true, virtualDisks: false);
        physical.Dashboard.Select(physical.Dashboard.Disks[1].Segments.First(s => s.Title == "D:"));
        physical.Dashboard.OpenEditor(EditorMode.Label);
        physical.Dashboard.Editor!.Label = "Archive";
        physical.Dashboard.Editor.AddCommand.Execute(null);
        Assert.True(physical.PendingPage.BlockReason!.Contains("physical disk"));
        // there is no engine in this test, but with the policy on, the remaining block is only the missing engine
        physical.SettingsPage.AllowPhysicalDisks = true;
        Assert.Equal(Loc.Instance["Pending.Block.NoEngine"], physical.PendingPage.BlockReason);
    }

    [Test]
    public static async Task Resize_editor_offers_valid_range()
    {
        var vm = await Create();
        var d = vm.Dashboard.Disks[1].Segments.First(s => s.Title == "D:");
        vm.Dashboard.Select(d);
        vm.Dashboard.OpenEditor(EditorMode.Resize);
        var e = vm.Dashboard.Editor!;
        Assert.True(e.MinBytes < d.SizeBytes && e.MaxBytes > d.SizeBytes);
        e.SizeText = "1200";
        Assert.True(e.Issues.All(i => !i.IsError));
        e.SizeText = "9999";
        Assert.True(e.Issues.Any(i => i.IsError));
        e.SizeText = "abc";
        Assert.True(e.Issues.Any(i => i.IsError) && !e.AddCommand.CanExecute(null));
    }
}
