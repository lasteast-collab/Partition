using Lasteast.Core.Abstractions;
using Lasteast.Core.Localization;
using Lasteast.Core.Logging;
using Lasteast.Core.Settings;
using Lasteast.Presentation.Sample;
using Lasteast.Presentation.Services;
using Lasteast.Presentation.ViewModels;

namespace Lasteast.Tests;

public static class PresentationTests
{
    private sealed class FakePrivileges : IPrivilegeService
    {
        public bool IsElevated => false;
        public bool TryRestartElevated(string[] args) => false;
    }

    private static MainViewModel Create(out string dir)
    {
        dir = Path.Combine(Path.GetTempPath(), "lasteast-vm-" + Guid.NewGuid().ToString("N"));
        var store = new JsonSettingsStore(Path.Combine(dir, "s.json"));
        return new MainViewModel(new SampleInventoryProvider(), new FakePrivileges(), new AppSettings(), store,
            new NullThemeService(), new NullShellService(), NullLog.Instance, dir, Array.Empty<string>());
    }

    [Test]
    public static async Task Refresh_populates_all_pages()
    {
        Loc.Instance.SetLanguage("en");
        var vm = Create(out _);
        await vm.RefreshAsync();

        Assert.True(vm.IsSampleData);
        Assert.Equal(3, vm.Dashboard.Disks.Count);
        Assert.Equal(5, vm.Dashboard.Stats.Count);
        Assert.Equal(4, vm.Dashboard.Disks[0].Segments.Count);           // ESP, MSR, C:, Recovery
        Assert.Equal(3, vm.Dashboard.Disks[1].Segments.Count);           // D:, unallocated, E:
        Assert.True(vm.Dashboard.Disks[1].Segments[1].IsUnallocated);
        Assert.Equal(8, vm.PartitionsPage.Rows.Count);
        Assert.Equal(vm.DisksPage.Disks[0], vm.DisksPage.SelectedDisk);
        Assert.True(vm.DisksPage.Properties.Count > 10);
        Assert.True(vm.StatusText.Contains('3'));
    }

    [Test]
    public static async Task Selecting_a_segment_shows_details_and_survives_refresh()
    {
        Loc.Instance.SetLanguage("en");
        var vm = Create(out _);
        await vm.RefreshAsync();
        var c = vm.Dashboard.Disks[0].Segments[2];
        vm.Dashboard.SelectSegmentCommand.Execute(c);
        Assert.True(c.IsSelected);
        Assert.True(vm.Dashboard.SelectedDetails.Any(r => r.Value.StartsWith("C:")));
        Assert.True(vm.Dashboard.SelectedDetails.Any(r => r.Value.Contains("Windows")));

        await vm.RefreshAsync();
        Assert.True(vm.Dashboard.SelectedSegment is { Title: "C:" }, "selection should be restored after refresh");
    }

    [Test]
    public static async Task Switching_to_hebrew_is_rtl_and_relocalizes()
    {
        var vm = Create(out _);
        await vm.RefreshAsync();
        vm.SettingsPage.SelectedLanguage = Loc.Languages.First(l => l.Code == "he");
        try
        {
            Assert.True(vm.IsRightToLeft);
            Assert.Equal("סקירה כללית", vm.NavGroups[0].Items[0].Title);
            Assert.True(vm.Dashboard.Disks[0].Title.StartsWith("דיסק"));
            Assert.True(!vm.PartitionsPage.Rows.Any(r => r.Type.StartsWith('[')), "no missing translations in rows");
        }
        finally
        {
            vm.SettingsPage.SelectedLanguage = Loc.Languages[0];
        }
        Assert.True(!vm.IsRightToLeft);
    }

    [Test]
    public static async Task Serial_masking_follows_setting()
    {
        Loc.Instance.SetLanguage("en");
        var vm = Create(out _);
        await vm.RefreshAsync();
        Assert.True(vm.Dashboard.Disks[0].SerialText.StartsWith("•"));
        vm.SettingsPage.MaskSerialNumbers = false;
        Assert.Equal("SAMPLE0000001234", vm.Dashboard.Disks[0].SerialText);
    }

    [Test]
    public static void Planned_modules_are_never_presented_as_available()
    {
        var vm = Create(out _);
        var planned = vm.NavGroups.SelectMany(g => g.Items).Where(i => i.IsPlanned).ToList();
        // (the number shrinks as phases land; every remaining one must be labelled)
        Assert.True(planned.All(p => p.Page is PlannedModuleViewModel));
        Assert.True(planned.All(p => !p.PhaseBadge.StartsWith('[')));
        Assert.Equal(6, vm.TopTargets.Count);
    }
}
