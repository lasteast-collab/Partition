using Lasteast.Core.Abstractions;
using Lasteast.Core.Cleanup;
using Lasteast.Core.Localization;
using Lasteast.Core.Settings;
using Lasteast.Presentation.Services;
using Lasteast.Presentation.ViewModels;

namespace Lasteast.Tests;

public static class AnalyzerUiTests
{
    private sealed class Priv(bool e) : IPrivilegeService
    {
        public bool IsElevated => e;
        public bool TryRestartElevated(string[] a) => false;
    }

    private sealed class RecordingRemover : IFileRemover
    {
        public readonly List<(string, RemovalMode)> Calls = new();
        public void Remove(string path, RemovalMode mode) { Calls.Add((path, mode)); File.Delete(path); }
    }

    [Test]
    public static async Task Analyzer_scans_folder_builds_treemap_and_lists()
    {
        Loc.Instance.SetLanguage("en");
        using var t = new TempTree();
        t.File("Movies/film.mp4", 3 * 1024 * 1024);
        t.File("Docs/a.pdf", 1024 * 1024 + 5);
        t.File("Docs/b.pdf", 1024 * 1024 + 5);
        var shell = new NullShellService();
        var vm = new StorageAnalyzerViewModel(shell, new RecordingRemover());
        vm.Locations.Add(new LocationOption(t.Root, t.Root));
        vm.SelectedLocation = vm.Locations[0];
        vm.SetViewport(600, 300);
        await vm.ScanCommand.ExecuteAsync();

        Assert.True(vm.HasResult && vm.SummaryText.Contains("3 files"), vm.SummaryText);
        Assert.Equal(2, vm.TreemapItems.Count(i => i.IsFolder));
        Assert.Near(600 * 300, vm.TreemapItems.Sum(i => (i.Width + 2) * (i.Height + 2)), 600 * 300 * 0.05);

        vm.OpenItemCommand.Execute(vm.TreemapItems.First(i => i.Name == "Docs"));
        Assert.Equal(2, vm.Breadcrumb.Count);
        Assert.Equal(2, vm.TreemapItems.Count(i => !i.IsFolder));
        vm.UpCommand.Execute(null);
        Assert.Equal(1, vm.Breadcrumb.Count);

        vm.Tab = "Largest";
        Assert.Equal("film.mp4", vm.Files[0].Name);
        vm.ExtensionFilter = "pdf";
        Assert.Equal(2, vm.Files.Count);
        vm.MinSizeMb = "2";
        Assert.Equal(0, vm.Files.Count);

        vm.Tab = "Duplicates";
        await vm.FindDuplicatesCommand.ExecuteAsync();
        Assert.Equal(1, vm.Duplicates.Count);
        Assert.Equal(2, vm.Duplicates[0].Paths.Count);
    }

    [Test]
    public static async Task Recycle_requires_confirmation_and_uses_recycle_bin()
    {
        Loc.Instance.SetLanguage("en");
        using var t = new TempTree();
        var f = t.File("x/big.bin", 2 * 1024 * 1024);
        var shell = new NullShellService { ConfirmAnswer = false };
        var remover = new RecordingRemover();
        var vm = new StorageAnalyzerViewModel(shell, remover);
        vm.Locations.Add(new LocationOption(t.Root, t.Root));
        vm.SelectedLocation = vm.Locations[0];
        await vm.ScanCommand.ExecuteAsync();
        vm.Tab = "Largest";
        var row = vm.Files.Single();

        vm.RecycleCommand.Execute(row);
        Assert.Equal(1, shell.Confirmations.Count);
        Assert.True(File.Exists(f), "declined confirmation must not remove anything");

        shell.ConfirmAnswer = true;
        vm.RecycleCommand.Execute(row);
        Assert.True(!File.Exists(f));
        Assert.Equal(RemovalMode.RecycleBin, remover.Calls.Single().Item2);
        Assert.Equal(0, vm.Files.Count);
    }

    [Test]
    public static async Task Cleanup_page_scan_confirm_clean()
    {
        Loc.Instance.SetLanguage("en");
        using var t = new TempTree();
        t.File("junk1.tmp", 1000, mtime: DateTime.UtcNow.AddDays(-5));
        t.File("junk2.tmp", 3000, mtime: DateTime.UtcNow.AddDays(-5));
        var target = new CleanupTarget("t", "Clean.UserTemp", "Clean.UserTemp.Desc", t.Root, TimeSpan.FromDays(1), false, true);
        var adminOnly = new CleanupTarget("a", "Clean.WinTemp", "Clean.WinTemp.Desc", t.Root, TimeSpan.Zero, true, true);
        var remover = new RecordingRemover();
        var vm = new CleanupPageViewModel(new CleanupService(remover), new[] { target, adminOnly }, new Priv(false));
        var settings = new AppSettings();
        vm.Attach(settings, () => { });

        Assert.True(!vm.Targets[1].IsEnabled && !vm.Targets[1].IsSelected, "admin-only target disabled without elevation");
        await vm.ScanCommand.ExecuteAsync();
        Assert.Equal("3.91 KB", vm.Targets[0].SizeText);
        Assert.True(!vm.CanClean, "needs explicit confirmation");
        vm.Confirmed = true;
        Assert.True(vm.CanClean);
        await vm.CleanCommand.ExecuteAsync();
        Assert.Equal(2, remover.Calls.Count);
        Assert.True(remover.Calls.All(c => c.Item2 == RemovalMode.RecycleBin));
        Assert.True(vm.Report!.Contains("Removed 2 files"));
        Assert.True(!vm.Confirmed, "confirmation resets after cleaning");

        vm.ExclusionsText = "C:\\Keep\nD:\\Photos\n";
        Assert.Equal(2, settings.Exclusions.Count);
    }
}
