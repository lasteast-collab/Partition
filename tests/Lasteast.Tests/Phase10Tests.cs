using System.Security.Cryptography;
using System.Text.Json;
using Lasteast.Core.Assistant;
using Lasteast.Core.Licensing;
using Lasteast.Core.Models;
using Lasteast.Core.Operations;
using Lasteast.Core.Reports;

namespace Lasteast.Tests;

public static class Phase10Tests
{
    private const string DevPrivateKey = "MIGHAgEAMBMGByqGSM49AgEGCCqGSM49AwEHBG0wawIBAQQgwsHW1CgLaIaOhTZGcmdSbTLNK31SzEs+zLcJxlq0UuahRANCAAT8HiOn+STFwduF1rGOFeZ7oVOPiCvdwZUfuQDYdDRidEe++bzSdnOY66vH3w0SLeL8rWqWUDYjlgnv8H6hYgb6";

    [Test]
    public static void Operation_parser_reads_units_and_kinds()
    {
        Assert.Equal(1UL << 20, OperationParser.ParseSize("1MiB"));
        Assert.Equal(20UL << 30, OperationParser.ParseSize("20GB"));
        Assert.Equal(512UL, OperationParser.ParseSize("512"));
        var op = (CreatePartitionOp)OperationParser.Parse(2, "create", OperationParser.Pairs(new[] { "offset=1MiB", "size=20GB", "fs=NTFS", "label=\"My Data\"", "letter=G:" }));
        Assert.True(op.OffsetBytes == 1UL << 20 && op.SizeBytes == 20UL << 30 && op.Label == "My Data" && op.DriveLetter == 'G');
        Assert.True(OperationParser.Parse(1, "convert", new Dictionary<string, string> { ["style"] = "gpt" }) is ConvertStyleOp { Target: PartitionStyle.Gpt });
        Assert.Throws<FormatException>(() => OperationParser.Parse(1, "explode", new Dictionary<string, string>()));
        Assert.Throws<FormatException>(() => OperationParser.Parse(1, "resize", new Dictionary<string, string> { ["offset"] = "1MiB" }));
    }

    [Test]
    public static void Intent_matcher_understands_several_languages()
    {
        Assert.Equal(Goal.ExpandC, IntentMatcher.Match("My C drive full, need more space on C"));
        Assert.Equal(Goal.RecoverFiles, IntentMatcher.Match("ich habe eine Datei gelöscht, bitte wiederherstellen"));
        Assert.Equal(Goal.MigrateOs, IntentMatcher.Match("хочу перенести windows на новый диск"));
        Assert.Equal(Goal.AccountLocked, IntentMatcher.Match("שכחתי את הסיסמה"));
        Assert.Equal(null, IntentMatcher.Match("what's the weather"));
    }

    [Test]
    public static void Rule_planner_proposes_only_validated_operations()
    {
        var inv = Fixtures.Real();
        var free = AssistantPlanner.Plan(Goal.UseFreeSpace, inv);
        Assert.Equal(1, free.Operations.Count);
        Assert.True(free.Operations[0] is CreatePartitionOp { DiskNumber: 1 });
        // the sample MBR disks end with a partition in the last sectors, where GPT needs its backup table: no conversion, reasons given
        var conv = AssistantPlanner.Plan(Goal.ConvertToGpt, inv);
        Assert.True(conv.Operations.Count == 0 && conv.Issues.Any(i => i.Key == "Conv.Error.NoRoomEnd") && conv.NavigateTo == "Nav.DiskConversion");
        var roomy = inv with { Disks = inv.Disks.Select(d => d.Number == 2 ? d with { Partitions = d.Partitions.Select(p => p with { SizeBytes = p.SizeBytes - (16UL << 20) }).ToList() } : d).ToList() };
        var ok = AssistantPlanner.Plan(Goal.ConvertToGpt, roomy);
        Assert.True(ok.Operations.Count == 1 && ok.Operations[0] is ConvertStyleOp { DiskNumber: 2 });
        var c = AssistantPlanner.Plan(Goal.ExpandC, inv);
        Assert.Equal(0, c.Operations.Count);
        Assert.Equal("Nav.Cleanup", c.NavigateTo);
        Assert.Equal("Nav.OsMigration", AssistantPlanner.Plan(Goal.MigrateOs, inv).NavigateTo);
    }

    private sealed class FakeLlm(string answer) : ILlmClient
    {
        public string? LastPrompt;
        public Task<string> CompleteAsync(string system, string user, CancellationToken ct) { LastPrompt = user; return Task.FromResult(answer); }
    }

    [Test]
    public static async Task Llm_plans_are_parsed_validated_and_never_include_rejected_steps()
    {
        var inv = Fixtures.Real();
        var c = Fixtures.Letter(inv, 'C');
        var d1 = Fixtures.Disk(inv, 1);
        var gap = Fixtures.Letter(inv, 'D').EndBytes;
        var llm = new FakeLlm($$"""
            Sure! {"explanation":"Create data partition and (wrongly) delete Windows",
             "operations":[{"kind":"create","disk":1,"offset":{{gap}},"size":"10GB","fs":"NTFS","letter":"G"},
                           {"kind":"delete","disk":0,"offset":{{c.OffsetBytes}}},
                           {"kind":"teleport","disk":1}]}
            """);
        var plan = await LlmPlanner.PlanAsync(llm, "make a new partition", inv, null, CancellationToken.None);
        Assert.Equal(1, plan.Operations.Count);
        Assert.True(plan.Operations[0] is CreatePartitionOp);
        Assert.True(plan.Issues.Any(i => i.Key == "Op.Error.Protected"), "deleting Windows was rejected by the simulator");
        Assert.True(plan.Issues.Any(i => i.Key == "Asst.ModelBadOp"));
        Assert.True(!llm.LastPrompt!.Contains(d1.SerialNumber ?? "SAMPLE0000005678"), "no serial numbers are sent");
        var junk = await LlmPlanner.PlanAsync(new FakeLlm("I cannot help"), "x", inv, null, CancellationToken.None);
        Assert.Equal("Asst.ModelNoPlan", junk.ExplanationKey);
    }

    [Test]
    public static void Licenses_verify_signature_expiry_and_tampering()
    {
        using var key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(Convert.FromBase64String(DevPrivateKey), out _);
        var lic = new License(Edition.Technician, "Contoso Repair", "ABC123", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddYears(1));
        var json = Licenses.Sign(lic, key);
        var (status, l) = Licenses.Verify(json, LicenseKeys.Trusted, DateTimeOffset.Now);
        Assert.Equal(LicenseStatus.Valid, status);
        Assert.Equal(Edition.Technician, Licenses.Effective(status, l));
        Assert.Equal(LicenseStatus.Invalid, Licenses.Verify(json.Replace("Contoso Repair", "Fabrikam"), LicenseKeys.Trusted, DateTimeOffset.Now).Status);
        Assert.Equal(LicenseStatus.Invalid, Licenses.Verify(json.Replace("Technician", "Pro"), LicenseKeys.Trusted, DateTimeOffset.Now).Status);
        var expired = Licenses.Verify(json, LicenseKeys.Trusted, DateTimeOffset.Now.AddYears(2));
        Assert.Equal(LicenseStatus.Expired, expired.Status);
        Assert.Equal(Edition.Free, Licenses.Effective(expired.Status, expired.License));
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.Equal(LicenseStatus.Invalid, Licenses.Verify(Licenses.Sign(lic, other), LicenseKeys.Trusted, DateTimeOffset.Now).Status);
        Assert.Equal(LicenseStatus.Invalid, Licenses.Verify("not json", LicenseKeys.Trusted, DateTimeOffset.Now).Status);
    }

    [Test]
    public static void Reports_are_valid_json_safe_csv_and_escaped_html()
    {
        var inv = Fixtures.Real();
        inv = inv with { Disks = inv.Disks.Select(d => d with { Partitions = d.Partitions.Select(p => p.Volume is null ? p : p with { Volume = p.Volume with { Label = "=HYPERLINK(\"x\")<script>" } }).ToList() }).ToList() };
        var input = new ReportInput { Inventory = inv, MaskSerials = true, ComputerName = "PC1" };
        using var doc = JsonDocument.Parse(TechnicianReport.Json(input));
        Assert.Equal(inv.Disks.Count, doc.RootElement.GetProperty("Disks").GetArrayLength());
        Assert.True(!TechnicianReport.Json(input).Contains("SAMPLE0000001234"), "serials masked");
        var csv = TechnicianReport.Csv(input);
        Assert.True(csv.Contains("\"'=HYPERLINK(\"\"x\"\")<script>\""), "formula neutralized and quoted");
        var html = TechnicianReport.Html(input);
        Assert.True(!html.Contains("<script>") && html.Contains("&lt;script&gt;"), "html escaped");
    }

    [Test]
    public static void Initialize_raw_disk_simulation()
    {
        var raw = new DiskInfo { Number = 9, DevicePath = "x", SizeBytes = 64UL << 30, Style = PartitionStyle.Raw, Partitions = new List<PartitionInfo>() };
        var sim = LayoutSimulator.Simulate(raw, new[] { new InitializeDiskOp(9, PartitionStyle.Gpt) });
        Assert.True(sim.IsValid);
        Assert.Equal(PartitionStyle.Gpt, sim.After.Style);
        Assert.True(LayoutSimulator.Simulate(Fixtures.Disk(Fixtures.Real(), 1), new[] { new InitializeDiskOp(1, PartitionStyle.Gpt) }).AllIssues.Any(i => i.Key == "Init.Error.NotRaw"));
        // what Windows reports for a blank disk: MBR, signature 0, no partitions
        var blank = raw with { Style = PartitionStyle.Mbr, MbrSignature = 0 };
        Assert.True(LayoutSimulator.Simulate(blank, new[] { new InitializeDiskOp(9, PartitionStyle.Gpt) }).IsValid);
        var after = sim.After with { Partitions = new List<PartitionInfo>() };
        Assert.Equal(null, GuardedExecutionEngine.Verify(new InitializeDiskOp(9, PartitionStyle.Gpt), sim.After, after));
    }
}

public static class AttributeTests
{
    private static bool Err(SimulationResult r, string key) => r.AllIssues.Any(i => i.IsError && i.Key == key);

    [Test]
    public static void Set_active_only_on_mbr_data_disks_and_clears_the_others()
    {
        var inv = Fixtures.Real();
        var d1 = Fixtures.Disk(inv, 1);
        var e = d1.Partitions[1];
        var sim = LayoutSimulator.Simulate(d1, new[] { new SetActiveOp(1, e.OffsetBytes) });
        Assert.True(sim.IsValid, string.Join(",", sim.AllIssues.Select(i => i.Key)));
        Assert.Equal(1, sim.After.Partitions.Count(p => p.MbrActive));
        Assert.True(Err(LayoutSimulator.Simulate(Fixtures.Disk(inv, 0), new[] { new SetActiveOp(0, Fixtures.Disk(inv, 0).Partitions[2].OffsetBytes) }), "Attr.Error.MbrOnly"));
        Assert.Equal(null, GuardedExecutionEngine.Verify(new SetActiveOp(1, e.OffsetBytes), sim.After, sim.After));
    }

    [Test]
    public static void Hide_toggles_mbr_type_and_gpt_attribute_and_protects_windows()
    {
        var inv = Fixtures.Real();
        var d1 = Fixtures.Disk(inv, 1);
        var d = d1.Partitions[0];
        var sim = LayoutSimulator.Simulate(d1, new[] { new SetHiddenOp(1, d.OffsetBytes, true) });
        Assert.True(sim.IsValid);
        Assert.Equal((byte)0x17, sim.After.Partitions[0].MbrType);
        Assert.True(sim.After.Partitions[0].IsHidden);
        var back = LayoutSimulator.Simulate(sim.After, new[] { new SetHiddenOp(1, d.OffsetBytes, false) });
        Assert.Equal((byte)0x07, back.After.Partitions[0].MbrType);
        var d0 = Fixtures.Disk(inv, 0);
        Assert.True(Err(LayoutSimulator.Simulate(d0, new[] { new SetHiddenOp(0, Fixtures.Letter(inv, 'C').OffsetBytes, true) }), "Op.Error.Protected"));
        var gptData = d0 with { IsWindowsDisk = false, Partitions = d0.Partitions.Select(p => p.Volume?.IsWindowsVolume == true ? p with { Volume = p.Volume with { IsWindowsVolume = false } } : p).ToList() };
        var g = LayoutSimulator.Simulate(gptData, new[] { new SetHiddenOp(0, Fixtures.Letter(inv, 'C').OffsetBytes, true) });
        Assert.True(g.IsValid && g.After.Partitions.Single(p => p.OffsetBytes == Fixtures.Letter(inv, 'C').OffsetBytes).IsHidden);
    }

    [Test]
    public static void Change_type_refuses_system_types()
    {
        var inv = Fixtures.Real();
        var d1 = Fixtures.Disk(inv, 1);
        var ok = LayoutSimulator.Simulate(d1, new[] { new SetTypeOp(1, d1.Partitions[0].OffsetBytes, null, 0x83) });
        Assert.True(ok.IsValid && ok.After.Partitions[0].Role == PartitionRole.Linux);
        Assert.True(Err(LayoutSimulator.Simulate(d1, new[] { new SetTypeOp(1, d1.Partitions[0].OffsetBytes, null, 0x05) }), "Attr.Error.TypeNotAllowed"));
        var d0 = Fixtures.Disk(inv, 0);
        var rec = d0.Partitions.Single(p => p.Role == PartitionRole.Recovery);
        Assert.True(Err(LayoutSimulator.Simulate(d0, new[] { new SetTypeOp(0, rec.OffsetBytes, PartitionTypes.GptBasicData, null) }), "Op.Error.Protected"));
        var esp = d0.Partitions.Single(p => p.Role == PartitionRole.EfiSystem);
        Assert.True(Err(LayoutSimulator.Simulate(d0, new[] { new SetTypeOp(0, esp.OffsetBytes, PartitionTypes.GptBasicData, null) }), "Op.Error.Protected"));
    }
}

public static class BootSectorOpTests
{
    private static bool Err(SimulationResult r, string key) => r.AllIssues.Any(i => i.IsError && i.Key == key);

    [Test]
    public static void Boot_sector_repair_only_on_unreadable_data_volumes()
    {
        var inv = Fixtures.Real();
        var d1 = Fixtures.Disk(inv, 1);
        var p = d1.Partitions[0];
        // readable volume: refused
        Assert.True(Err(LayoutSimulator.Simulate(d1, new[] { new RepairBootSectorOp(1, p.OffsetBytes, "NTFS") }), "BootFix.Error.Readable"));
        // RAW volume: allowed, and afterwards the volume is expected to read as NTFS
        var raw = d1 with { Partitions = d1.Partitions.Select(x => x == p ? x with { Volume = x.Volume! with { FileSystem = null, AccessError = "RAW", AccessErrorCode = 1005 } } : x).ToList() };
        var op = new RepairBootSectorOp(1, p.OffsetBytes, "NTFS");
        var sim = LayoutSimulator.Simulate(raw, new[] { op });
        Assert.True(sim.IsValid, string.Join(",", sim.AllIssues.Select(i => i.Key)));
        Assert.True(sim.AllIssues.Any(i => i.Key == "BootFix.Warn.Sectors"));
        Assert.Equal("NTFS", sim.After.Partitions[0].Volume?.FileSystem);
        Assert.Equal(null, GuardedExecutionEngine.Verify(op, sim.After, sim.After));
        Assert.True(GuardedExecutionEngine.Verify(op, sim.After, raw) is not null, "still RAW = verification failure");
        Assert.True(Err(LayoutSimulator.Simulate(raw, new[] { new RepairBootSectorOp(1, p.OffsetBytes, "ReFS") }), "BootFix.Error.FileSystem"));
        // never on the running Windows' volume
        var d0 = Fixtures.Disk(inv, 0);
        var c = Fixtures.Letter(inv, 'C');
        var d0raw = d0 with { Partitions = d0.Partitions.Select(x => x == c ? x with { Volume = x.Volume! with { AccessError = "x" } } : x).ToList() };
        Assert.True(Err(LayoutSimulator.Simulate(d0raw, new[] { new RepairBootSectorOp(0, c.OffsetBytes, "NTFS") }), "Op.Error.Protected"));
    }
}

public static class WindowFitTests
{
    [Test]
    public static void Main_window_always_fits_the_screen_with_its_title_bar_visible()
    {
        foreach (var (w, h) in new[] { (1366.0, 728.0), (1280.0, 680.0), (1093.0, 582.0), (1920.0, 1040.0), (2560.0, 1400.0), (800.0, 560.0) })
        {
            var f = Lasteast.Presentation.Services.WindowFit.Compute(0, 0, w, h);
            Assert.True(f.Left >= 0 && f.Top >= 0, $"{w}x{h}: origin {f.Left},{f.Top}");
            Assert.True(f.Left + f.Width <= w && f.Top + f.Height <= h, $"{w}x{h}: {f.Width}x{f.Height} at {f.Left},{f.Top}");
            Assert.True(f.MinWidth <= w && f.MinHeight <= h, $"{w}x{h}: minimum {f.MinWidth}x{f.MinHeight}");
        }
        Assert.True(Lasteast.Presentation.Services.WindowFit.Compute(0, 0, 1366, 728).Maximize, "small laptop screen opens maximized");
        var big = Lasteast.Presentation.Services.WindowFit.Compute(0, 0, 2560, 1400);
        Assert.True(!big.Maximize && big.Width == 1440 && big.Height == 900 && big.Left == 560, "large screen: preferred size, centred");
        var second = Lasteast.Presentation.Services.WindowFit.Compute(-1920, 0, 1920, 1040);
        Assert.True(second.Left >= -1920 && second.Left + second.Width <= 0, "work area not at the origin");
    }
}
