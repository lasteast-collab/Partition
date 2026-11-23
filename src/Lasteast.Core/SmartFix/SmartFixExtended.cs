using Lasteast.Core.Health;
using Lasteast.Core.Info;
using Lasteast.Core.Models;
using Lasteast.Core.Operations;
using Lasteast.Core.Optimization;

namespace Lasteast.Core.SmartFix;

/// <summary>Scan categories that rely on probes beyond the layout: health, temperature, wear, BitLocker, dirty file systems, optimisation, downloads.</summary>
public static partial class SmartFixAnalyzer
{
    private static void AddHealth(List<SmartFinding> list, StorageInventory inv, SystemSnapshot? sys)
    {
        var reports = sys?.Health;
        if (reports is null || reports.Count == 0 || reports.All(r => !r.HasData))
        {
            list.Add(new SmartFinding
            {
                Id = "health", Severity = SmartSeverity.Optional, Category = "Health",
                TitleKey = "SF.Health.Title", ExplanationKey = "SF.Health.Explain", RecommendationKey = "SF.Health.Fix",
                Fix = FixKind.Navigate, NavigateTo = "Nav.DiskHealth", Risk = FixRisk.None,
            });
            return;
        }
        bool allGood = true;
        foreach (var r in reports.Where(r => r.HasData))
        {
            var disk = inv.Disks.FirstOrDefault(d => d.Number == r.DiskNumber);
            string name = $"{r.DiskNumber}" + (disk is null ? "" : $" ({disk.Model})");
            if (r.Verdict is HealthVerdict.Critical or HealthVerdict.Warning)
            {
                allGood = false;
                list.Add(new SmartFinding
                {
                    Id = $"health-{r.DiskNumber}", Severity = r.Verdict == HealthVerdict.Critical ? SmartSeverity.Critical : SmartSeverity.High,
                    Category = "Health", TitleKey = "SF.HealthBad.Title", TitleArgs = new object[] { name },
                    ExplanationKey = "SF.HealthBad.Explain", RecommendationKey = "SF.HealthBad.Fix",
                    Fix = FixKind.Navigate, NavigateTo = "Nav.DiskHealth", Risk = FixRisk.None, BackupRecommended = true, DiskNumber = r.DiskNumber,
                });
            }
            int hot = disk?.Media == MediaKind.Hdd ? 55 : 70;
            if (r.Temperature is { } t && t >= hot)
            {
                allGood = false;
                list.Add(new SmartFinding
                {
                    Id = $"temp-{r.DiskNumber}", Severity = t >= hot + 10 ? SmartSeverity.High : SmartSeverity.Recommended, Category = "Health",
                    TitleKey = "SF.Hot.Title", TitleArgs = new object[] { name, t }, ExplanationKey = "SF.Hot.Explain", RecommendationKey = "SF.Hot.Fix",
                    Fix = FixKind.Guidance, Risk = FixRisk.None, DiskNumber = r.DiskNumber,
                });
            }
            if (r.WearPercent is { } w && w >= 70)
            {
                allGood = false;
                list.Add(new SmartFinding
                {
                    Id = $"wear-{r.DiskNumber}", Severity = w >= 90 ? SmartSeverity.High : SmartSeverity.Recommended, Category = "Health",
                    TitleKey = "SF.Wear.Title", TitleArgs = new object[] { name, w }, ExplanationKey = "SF.Wear.Explain", RecommendationKey = "SF.Wear.Fix",
                    Fix = FixKind.Navigate, NavigateTo = "Nav.Backup", Risk = FixRisk.None, BackupRecommended = true, DiskNumber = r.DiskNumber,
                });
            }
        }
        if (allGood) list.Add(Healthy("health-ok", "Health", "SF.HealthOk.Title"));
    }

    private static void AddExtended(List<SmartFinding> list, StorageInventory inv, SystemSnapshot? sys)
    {
        if (sys is null) return;
        // dirty file systems: Windows itself asked for a check
        foreach (var letter in sys.DirtyVolumes)
        {
            var (disk, part) = Find(inv, letter);
            bool system = part?.Volume?.IsWindowsVolume == true || letter == sys.WindowsDrive;
            var op = disk is not null && part is not null && !system ? new RepairFileSystemOp(disk.Number, part.OffsetBytes) : null;
            list.Add(new SmartFinding
            {
                Id = $"dirty-{letter}", Severity = SmartSeverity.High, Category = "FileSystem",
                TitleKey = "SF.Dirty.Title", TitleArgs = new object[] { $"{letter}:" },
                ExplanationKey = "SF.Dirty.Explain", RecommendationKey = system ? "SF.Dirty.FixSystem" : "SF.Dirty.Fix", RecommendationArgs = new object[] { $"{letter}:" },
                Fix = op is null ? FixKind.Guidance : FixKind.QueueOperations, Operations = op is null ? Array.Empty<DiskOperation>() : new DiskOperation[] { op },
                Risk = FixRisk.Low, BackupRecommended = true, DiskNumber = disk?.Number,
            });
        }
        // BitLocker: facts, and conversions in progress
        foreach (var b in sys.BitLocker ?? Array.Empty<BitLockerVolume>())
        {
            if (b.Protection == BitLockerProtection.Off && b.PercentEncrypted is null or 0) continue;
            string where = b.DriveLetter is { } l ? $"{l}:" : b.VolumeGuidPath;
            bool converting = b.PercentEncrypted is > 0 and < 100;
            list.Add(new SmartFinding
            {
                Id = $"bitlocker-{where}", Severity = converting ? SmartSeverity.Recommended : SmartSeverity.Optional, Category = "Encryption",
                TitleKey = converting ? "SF.BitLockerConverting.Title" : b.Locked == true ? "SF.BitLockerLocked.Title" : "SF.BitLockerOn.Title",
                TitleArgs = new object[] { where, b.PercentEncrypted ?? 0 },
                ExplanationKey = "SF.BitLocker.Explain", RecommendationKey = "SF.BitLocker.Fix",
                Fix = FixKind.Navigate, NavigateTo = "Nav.Tools", Risk = FixRisk.None,
            });
        }
        // optimisation: defragment HDDs that need it, TRIM SSDs (never defragment them)
        foreach (var d in inv.Disks)
        foreach (var p in d.Partitions)
        {
            var action = OptimizePlanner.For(d, p);
            if (action == OptimizeAction.None || p.Volume?.DriveLetter is not { } l) continue;
            if (action == OptimizeAction.Defragment)
            {
                if (sys.Fragmentation is null || !sys.Fragmentation.TryGetValue(l, out var pct) || pct < 10) continue;
                list.Add(new SmartFinding
                {
                    Id = $"defrag-{l}", Severity = pct >= 25 ? SmartSeverity.Recommended : SmartSeverity.Optional, Category = "Performance",
                    TitleKey = "SF.Fragmented.Title", TitleArgs = new object[] { $"{l}:", pct }, ExplanationKey = "SF.Fragmented.Explain",
                    RecommendationKey = "SF.Fragmented.Fix", Fix = FixKind.Optimize, OptimizeLetter = l, OptimizeAction = action, Risk = FixRisk.Low, DiskNumber = d.Number,
                });
            }
            else
                list.Add(new SmartFinding
                {
                    Id = $"trim-run-{l}", Severity = SmartSeverity.Optional, Category = "Performance",
                    TitleKey = "SF.Retrim.Title", TitleArgs = new object[] { $"{l}:" }, ExplanationKey = "SF.Retrim.Explain",
                    RecommendationKey = "SF.Retrim.Fix", Fix = FixKind.Optimize, OptimizeLetter = l, OptimizeAction = action, Risk = FixRisk.None, DiskNumber = d.Number,
                });
        }
        // large and old downloads: pointers to the analyzer and cleanup (nothing is deleted from here)
        if (sys.OldDownloadsBytes is >= 1UL << 30)
            list.Add(new SmartFinding
            {
                Id = "old-downloads", Severity = SmartSeverity.Optional, Category = "Cleanup",
                TitleKey = "SF.OldDownloads.Title", TitleArgs = new object[] { sys.OldDownloadsBytes.Value },
                ExplanationKey = "SF.OldDownloads.Explain", RecommendationKey = "SF.OldDownloads.Fix",
                BenefitKey = "SF.Benefit.Space", BenefitArgs = new object[] { sys.OldDownloadsBytes.Value },
                Fix = FixKind.Navigate, NavigateTo = "Nav.Cleanup", Risk = FixRisk.Low,
            });
        list.Add(new SmartFinding
        {
            Id = "analyze-files", Severity = SmartSeverity.Optional, Category = "Cleanup",
            TitleKey = "SF.FindLarge.Title", ExplanationKey = "SF.FindLarge.Explain", RecommendationKey = "SF.FindLarge.Fix",
            Fix = FixKind.Navigate, NavigateTo = "Nav.StorageAnalyzer", Risk = FixRisk.None,
        });
    }

    private static (DiskInfo? Disk, PartitionInfo? Part) Find(StorageInventory inv, char letter)
    {
        foreach (var d in inv.Disks)
        foreach (var p in d.Partitions)
            if (p.Volume?.DriveLetter is { } l && char.ToUpperInvariant(l) == char.ToUpperInvariant(letter)) return (d, p);
        return (null, null);
    }
}
