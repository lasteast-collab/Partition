using System.Globalization;
using Lasteast.Core.Formatting;
using Lasteast.Core.Insights;
using Lasteast.Core.Layout;
using Lasteast.Core.Models;
using Lasteast.Core.Settings;
using Lasteast.Presentation.Sample;

namespace Lasteast.Tests;

public static class LayoutTests
{
    private const ulong MiB = 1024 * 1024;
    private const ulong GiB = 1024 * MiB;

    private static PartitionInfo P(int n, ulong off, ulong size, PartitionRole role = PartitionRole.BasicData, PartitionStyle style = PartitionStyle.Gpt) => new()
    {
        DiskNumber = 0, PartitionNumber = n, OffsetBytes = off, SizeBytes = size, Style = style, Role = role,
    };

    [Test]
    public static void Gpt_segments_include_gaps_and_trailing_free_space()
    {
        var disk = new DiskInfo
        {
            Number = 0, DevicePath = "x", SizeBytes = 100 * GiB, Style = PartitionStyle.Gpt,
            UsableStartBytes = 17408, UsableEndBytes = 100 * GiB - 16896,
            Partitions = new[] { P(1, MiB, 100 * MiB, PartitionRole.EfiSystem), P(2, 10 * GiB, 20 * GiB) },
        };
        var segs = DiskMapLayout.BuildSegments(disk, out var problems);
        Assert.Equal(0, problems.Count);
        Assert.Equal(4, segs.Count);
        // leading slack (17 KiB..1 MiB) is below the 1 MiB threshold → not shown
        Assert.Equal(SegmentKind.Partition, segs[0].Kind);
        Assert.Equal(SegmentKind.Unallocated, segs[1].Kind);
        Assert.Equal(MiB + 100 * MiB, segs[1].OffsetBytes);
        Assert.Equal(10 * GiB - 101 * MiB, segs[1].SizeBytes);
        Assert.Equal(SegmentKind.Unallocated, segs[3].Kind);
        Assert.Equal(disk.UsableEndBytes, segs[3].EndBytes);
    }

    [Test]
    public static void Mbr_extended_container_is_hidden_and_logicals_shown()
    {
        var disk = new DiskInfo
        {
            Number = 0, DevicePath = "x", SizeBytes = 50 * GiB, Style = PartitionStyle.Mbr,
            Partitions = new[]
            {
                P(1, MiB, 10 * GiB, style: PartitionStyle.Mbr),
                P(0, 10 * GiB + MiB, 40 * GiB - MiB, PartitionRole.MbrExtended, PartitionStyle.Mbr),
                P(5, 10 * GiB + 2 * MiB, 5 * GiB, PartitionRole.MbrLogical, PartitionStyle.Mbr),
            },
        };
        var segs = DiskMapLayout.BuildSegments(disk, out var problems);
        Assert.Equal(0, problems.Count);
        Assert.True(segs.All(s => s.Partition?.Role != PartitionRole.MbrExtended));
        Assert.Equal(3, segs.Count); // primary, logical, trailing free
        Assert.Equal(5, segs[1].Partition!.PartitionNumber);
    }

    [Test]
    public static void Overlapping_partitions_are_reported_not_hidden()
    {
        var disk = new DiskInfo
        {
            Number = 3, DevicePath = "x", SizeBytes = 10 * GiB, Style = PartitionStyle.Mbr,
            Partitions = new[] { P(1, MiB, 5 * GiB, style: PartitionStyle.Mbr), P(2, 4 * GiB, 2 * GiB, style: PartitionStyle.Mbr) },
        };
        var segs = DiskMapLayout.BuildSegments(disk, out var problems);
        Assert.Equal(1, problems.Count);
        Assert.Equal(2, segs.Count(s => s.Kind == SegmentKind.Partition));
    }

    [Test]
    public static void Widths_are_proportional_sum_to_total_and_respect_minimum()
    {
        var w = DiskMapLayout.ComputeWidths(new ulong[] { 100, 16, 900_000, 750 }, 1000, 60);
        Assert.Near(1000, w.Sum(), 1e-6);
        Assert.True(w.All(x => x >= 60 - 1e-9));
        Assert.True(w[2] > w[0] && w[2] > w[3]);
        Assert.Near(60, w[0]);
    }

    [Test]
    public static void Widths_split_equally_when_minimums_do_not_fit()
    {
        var w = DiskMapLayout.ComputeWidths(new ulong[] { 1, 2, 3 }, 90, 50);
        Assert.True(w.All(x => Math.Abs(x - 30) < 1e-9));
    }

    [Test]
    public static void Widths_handle_empty_and_zero_sizes()
    {
        Assert.Equal(0, DiskMapLayout.ComputeWidths(Array.Empty<ulong>(), 100, 10).Length);
        var w = DiskMapLayout.ComputeWidths(new ulong[] { 0, 0 }, 100, 10);
        Assert.Near(100, w.Sum());
    }
}

public static class FormattingTests
{
    [Test]
    public static void Formats_binary_units_with_culture()
    {
        var inv = CultureInfo.InvariantCulture;
        Assert.Equal("512 B", ByteSize.Format(512, inv));
        Assert.Equal("1.00 KB", ByteSize.Format(1024, inv));
        Assert.Equal("931.51 GB", ByteSize.Format(1_000_204_886_016, inv));
        Assert.Equal("1,5 ГБ", ByteSize.Format(1536UL * 1024 * 1024, CultureInfo.GetCultureInfo("ru"), u => u == "GB" ? "ГБ" : u, decimals: 1).Replace(' ', ' '));
        Assert.Equal("2.00 TB", ByteSize.Format(2UL << 40, inv));
    }

    [Test]
    public static void Alignment_classification()
    {
        Assert.Equal(AlignmentStatus.Aligned1MiB, PartitionTypes.GetAlignment(1048576, 4096));
        Assert.Equal(AlignmentStatus.AlignedPhysical, PartitionTypes.GetAlignment(4096 * 3, 4096));
        Assert.Equal(AlignmentStatus.Misaligned, PartitionTypes.GetAlignment(63 * 512, 512));
    }

    [Test]
    public static void Partition_type_classification()
    {
        Assert.Equal(PartitionRole.EfiSystem, PartitionTypes.Classify(PartitionTypes.GptEfiSystem));
        Assert.Equal(PartitionRole.Recovery, PartitionTypes.Classify(PartitionTypes.GptWindowsRecovery));
        Assert.Equal(PartitionRole.Other, PartitionTypes.Classify(Guid.NewGuid()));
        Assert.Equal(PartitionRole.MbrExtended, PartitionTypes.Classify(0x0F, false));
        Assert.Equal(PartitionRole.MbrLogical, PartitionTypes.Classify(0x07, true));
        Assert.Equal(PartitionRole.BasicData, PartitionTypes.Classify(0x07, false));
        Assert.Equal(PartitionRole.Recovery, PartitionTypes.Classify(0x27, false));
        Assert.True(PartitionTypes.IsMbrHiddenType(0x17));
    }

    [Test]
    public static void Critical_partitions_are_flagged()
    {
        var esp = new PartitionInfo { DiskNumber = 0, PartitionNumber = 1, OffsetBytes = 0, SizeBytes = 1, Style = PartitionStyle.Gpt, Role = PartitionRole.EfiSystem };
        var data = esp with { Role = PartitionRole.BasicData };
        var win = data with { Volume = new VolumeInfo { VolumeGuidPath = "v", IsWindowsVolume = true } };
        Assert.True(esp.IsCritical);
        Assert.True(!data.IsCritical);
        Assert.True(win.IsCritical);
    }
}

public static class SettingsTests
{
    [Test]
    public static void Settings_round_trip_and_survive_corruption()
    {
        var dir = Path.Combine(Path.GetTempPath(), "lasteast-test-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "settings.json");
        var store = new JsonSettingsStore(path);
        Assert.Equal("en", store.Load().Language);

        store.Save(new AppSettings { Language = "he", Theme = ThemeMode.Light, AccentColor = "#F57C00", MaskSerialNumbers = false });
        var back = store.Load();
        Assert.Equal("he", back.Language);
        Assert.Equal(ThemeMode.Light, back.Theme);
        Assert.Equal(false, back.MaskSerialNumbers);

        File.WriteAllText(path, "{ not json");
        var recovered = store.Load();
        Assert.Equal("en", recovered.Language);
        Assert.True(Directory.GetFiles(dir, "settings.json.corrupt-*").Length == 1);
        Directory.Delete(dir, true);
    }
}

public static class InsightTests
{
    [Test]
    public static void Sample_inventory_produces_expected_findings()
    {
        var inv = SampleInventoryProvider.Create();
        var findings = StorageInsights.Analyze(inv);
        Assert.True(findings.Any(f => f.MessageKey == "Finding.Unallocated" && f.DiskNumber == 1));
        Assert.True(findings.All(f => f.MessageKey != "Finding.NotElevated"), "sample data must not claim anything about elevation");
        Assert.True(findings.All(f => f.MessageKey != "Finding.LayoutProblem"));
    }

    [Test]
    public static void Low_space_and_mbr_limit_detected()
    {
        const ulong GiB = 1UL << 30;
        var disk = new DiskInfo
        {
            Number = 0, DevicePath = "x", SizeBytes = 3000 * GiB, Style = PartitionStyle.Mbr,
            Partitions = new[]
            {
                new PartitionInfo
                {
                    DiskNumber = 0, PartitionNumber = 1, OffsetBytes = 1 << 20, SizeBytes = 100 * GiB, Style = PartitionStyle.Mbr,
                    Volume = new VolumeInfo { VolumeGuidPath = "v", DriveLetter = 'C', CapacityBytes = 100 * GiB, FreeBytes = 2 * GiB, IsWindowsVolume = true },
                },
            },
        };
        var f = StorageInsights.Analyze(new StorageInventory { CapturedAt = default, Disks = new[] { disk }, IsElevated = true });
        Assert.True(f.Any(x => x.MessageKey == "Finding.LowSpace" && x.Severity == FindingSeverity.High));
        Assert.True(f.Any(x => x.MessageKey == "Finding.Mbr2TB"));
        Assert.Equal(FindingSeverity.High, f[0].Severity); // ordered by severity
    }
}
