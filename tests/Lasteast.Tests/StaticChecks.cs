using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Lasteast.Core.Insights;
using Lasteast.Core.Localization;
using Lasteast.Core.Models;
using Lasteast.Presentation.ViewModels;

namespace Lasteast.Tests;

/// <summary>
/// Source-level checks that catch the classic runtime-only WPF failures (missing resource keys,
/// misspelled binding paths, missing translations) without needing a Windows build machine.
/// </summary>
public static partial class StaticChecks
{
    private static readonly string Root = FindRoot();

    private static string FindRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "LasteastPartition.sln"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("Repository root not found");
    }

    private static IEnumerable<string> Files(string pattern) =>
        Directory.EnumerateFiles(Path.Combine(Root, "src"), pattern, SearchOption.AllDirectories)
                 .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                          && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

    private static Dictionary<string, string> Table(string code) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(
            File.ReadAllText(Path.Combine(Root, "src/Lasteast.Core/Localization/Strings", code + ".json")))!;

    [GeneratedRegex(@"\{(\d+)\}")] private static partial Regex Placeholder();

    [Test]
    public static void All_languages_have_identical_keys_and_placeholders()
    {
        var en = Table("en");
        foreach (var lang in Loc.Languages)
        {
            var t = Table(lang.Code);
            var missing = en.Keys.Except(t.Keys).ToList();
            var extra = t.Keys.Except(en.Keys).ToList();
            Assert.True(missing.Count == 0 && extra.Count == 0, $"{lang.Code}: missing [{string.Join(", ", missing)}] extra [{string.Join(", ", extra)}]");
            foreach (var (k, v) in t)
            {
                Assert.True(!string.IsNullOrWhiteSpace(v), $"{lang.Code}:{k} is empty");
                var a = Placeholder().Matches(en[k]).Select(m => m.Value).ToHashSet();
                var b = Placeholder().Matches(v).Select(m => m.Value).ToHashSet();
                Assert.True(a.SetEquals(b), $"{lang.Code}:{k} placeholders differ");
            }
        }
    }

    [Test]
    public static void Every_key_used_in_code_and_xaml_exists()
    {
        var en = Table("en");
        var used = new HashSet<string>();
        foreach (var f in Files("*.xaml"))
            foreach (Match m in Regex.Matches(File.ReadAllText(f), @"\{ui:Tr ([A-Za-z0-9_.]+)\}")) used.Add(m.Groups[1].Value);
        foreach (var f in Files("*.cs"))
        {
            var t = File.ReadAllText(f);
            foreach (Match m in Regex.Matches(t, @"\bL\(""([A-Za-z0-9_.]+)""\)")) used.Add(m.Groups[1].Value);
            foreach (Match m in Regex.Matches(t, @"Format\(""([A-Za-z0-9_.]+)""")) used.Add(m.Groups[1].Value);
            foreach (Match m in Regex.Matches(t, @"""((?:Finding|Nav|NavGroup|Accent|Theme|Op|Exec|OpText|Action|Editor|Pending|SF|An|Clean|Guard)\.[A-Za-z0-9_.]+)""")) used.Add(m.Groups[1].Value);
        }

        // keys built from enum values at runtime
        void Enum<T>(string prefix) where T : struct, System.Enum { foreach (var v in System.Enum.GetNames<T>()) used.Add(prefix + v); }
        Enum<PartitionStyle>("Style."); Enum<MediaKind>("Media."); Enum<BusKind>("Bus."); Enum<PartitionRole>("Role.");
        Enum<AlignmentStatus>("Align."); Enum<HealthState>("Health."); Enum<FirmwareKind>("Firmware."); Enum<FindingSeverity>("Severity.");
        foreach (var u in new[] { "B", "KB", "MB", "GB", "TB", "PB" }) used.Add("Unit." + u);
        used.Add("Common.No");
        foreach (var m in System.Enum.GetNames<Lasteast.Presentation.ViewModels.EditorMode>()) used.Add("Action." + m);
        foreach (var m in System.Enum.GetNames<Lasteast.Core.SmartFix.SmartSeverity>()) used.Add("SF.Group." + m);
        foreach (var m in System.Enum.GetNames<Lasteast.Core.SmartFix.FixRisk>()) used.Add("SF.Risk." + m);
        foreach (var m in System.Enum.GetNames<Lasteast.Core.Analysis.FileCategory>()) used.Add("Cat." + m);
        used.Add("Status.Admin"); used.Add("Status.Standard");
        foreach (var n in used.Where(k => k.StartsWith("Nav.") && k != "Nav.PhaseBadge").ToList()) used.Add(n.Replace("Nav.", "Planned.Desc."));
        // only planned modules have descriptions
        used.RemoveWhere(k => k is "Planned.Desc.Overview" or "Planned.Desc.AllDisks" or "Planned.Desc.AllPartitions" or "Planned.Desc.Settings" or "Planned.Desc.Pending");
        used.RemoveWhere(k => k.EndsWith('.')); // prefixes like "Action." built at runtime

        var missing = used.Where(k => !en.ContainsKey(k)).OrderBy(k => k).ToList();
        Assert.True(missing.Count == 0, "Missing keys: " + string.Join(", ", missing));
    }

    [Test]
    public static void Xaml_files_are_well_formed()
    {
        foreach (var f in Files("*.xaml")) XDocument.Load(f);
    }

    private static HashSet<string> ResourceKeys(string file)
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        return XDocument.Load(file).Descendants().Select(e => (string?)e.Attribute(x + "Key")).OfType<string>()
                        .Where(k => !k.StartsWith('{')).ToHashSet();
    }

    [Test]
    public static void Themes_define_the_same_keys_and_every_referenced_resource_exists()
    {
        var themes = Path.Combine(Root, "src/Lasteast.App/Themes");
        var dark = ResourceKeys(Path.Combine(themes, "Dark.xaml"));
        var light = ResourceKeys(Path.Combine(themes, "Light.xaml"));
        Assert.True(dark.SetEquals(light), "Dark/Light differ: " + string.Join(", ", dark.Except(light).Concat(light.Except(dark))));

        var available = new HashSet<string>(dark);
        available.UnionWith(ResourceKeys(Path.Combine(themes, "Base.xaml")));
        foreach (var f in Files("*.xaml")) available.UnionWith(ResourceKeys(f)); // locally declared keys

        var missing = new List<string>();
        foreach (var f in Files("*.xaml"))
            foreach (Match m in Regex.Matches(File.ReadAllText(f), @"\{(?:Static|Dynamic)Resource ([A-Za-z0-9_.]+)\}"))
                if (!available.Contains(m.Groups[1].Value)) missing.Add($"{Path.GetFileName(f)}: {m.Groups[1].Value}");
        Assert.True(missing.Count == 0, "Missing resources: " + string.Join("; ", missing));

        // keys resolved from data at runtime by converters
        var dataKeys = new[] { "NTFS", "FAT", "exFAT", "ReFS", "EFI", "MSR", "Recovery", "Foreign", "Managed", "Other", "Unallocated" }.Select(k => "Seg." + k)
            .Concat(System.Enum.GetNames<FindingSeverity>().Select(k => "Sev." + k))
            .Concat(System.Enum.GetNames<Lasteast.Core.SmartFix.SmartSeverity>().Select(k => "Sev." + k))
            .Concat(System.Enum.GetNames<HealthState>().Select(k => "Health." + k))
            .Concat(new[] { "Windows", "Boot", "Removable", "Virtual", "Dynamic", "Problem" }.Select(k => "Badge." + k))
            .Concat(System.Enum.GetNames<Lasteast.Core.Analysis.FileCategory>().Concat(new[] { "Folder" }).Select(k => "Tm." + k));
        var missingData = dataKeys.Where(k => !dark.Contains(k)).ToList();
        Assert.True(missingData.Count == 0, "Missing data brushes: " + string.Join(", ", missingData));
    }

    [Test]
    public static void Segment_color_keys_match_theme_brushes()
    {
        var produced = new[]
        {
            PartitionRole.EfiSystem, PartitionRole.MicrosoftReserved, PartitionRole.Recovery, PartitionRole.Linux,
            PartitionRole.LdmData, PartitionRole.BasicData,
        }.Select(r => SegmentViewModel.ColorFor(new PartitionInfo
        {
            DiskNumber = 0, PartitionNumber = 1, OffsetBytes = 0, SizeBytes = 1, Style = PartitionStyle.Gpt, Role = r,
        })).ToHashSet();
        var dark = ResourceKeys(Path.Combine(Root, "src/Lasteast.App/Themes/Dark.xaml"));
        Assert.True(produced.All(k => dark.Contains("Seg." + k)));
    }

    [Test]
    public static void Binding_paths_refer_to_existing_properties()
    {
        var known = new HashSet<string>(StringComparer.Ordinal)
        {
            // WPF / framework properties used with RelativeSource bindings
            "DataContext", "IsDropDownOpen", "ActualWidth", "SelectCommand", "MapHeight",
        };
        foreach (var asm in new[] { typeof(MainViewModel).Assembly, typeof(DiskInfo).Assembly })
            foreach (var t in asm.GetExportedTypes())
                foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance)) known.Add(p.Name);

        var bad = new List<string>();
        foreach (var f in Files("*.xaml"))
        {
            var text = File.ReadAllText(f);
            var paths = Regex.Matches(text, @"\{Binding (?:Path=)?([A-Za-z_][A-Za-z0-9_.\[\]]*)").Select(m => m.Groups[1].Value)
                .Concat(Regex.Matches(text, @"<Binding Path=""([A-Za-z_][A-Za-z0-9_.\[\]]*)""").Select(m => m.Groups[1].Value));
            foreach (var path in paths)
            {
                if (path.StartsWith("Converter") || path.StartsWith("RelativeSource") || path.StartsWith("Mode")) continue;
                foreach (var part in path.Split('.'))
                {
                    var name = part.Split('[')[0];
                    if (name.Length > 0 && !known.Contains(name)) bad.Add($"{Path.GetFileName(f)}: {path}");
                }
            }
        }
        Assert.True(bad.Count == 0, "Unknown binding paths: " + string.Join("; ", bad.Distinct()));
    }
}
