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
        var after = sim.After with { Partitions = new List<PartitionInfo>() };
        Assert.Equal(null, GuardedExecutionEngine.Verify(new InitializeDiskOp(9, PartitionStyle.Gpt), sim.After, after));
    }
}
