using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Lasteast.Core.Layout;
using Lasteast.Core.Models;
using Lasteast.Core.Operations;
using Lasteast.Core.SmartFix;

namespace Lasteast.Core.Assistant;

public enum Goal { ExpandC, UseFreeSpace, ConvertToGpt, MigrateOs, RecoverFiles, RecoverPartition, CheckHealth, FreeUpSpace, BackupData, BootProblem, AccountLocked }

/// <summary>A proposal. Operations have already passed the simulator; the user still reviews and applies them in Pending.</summary>
public sealed record AssistantPlan
{
    public required Goal Goal { get; init; }
    public required string ExplanationKey { get; init; }
    public object[] Args { get; init; } = Array.Empty<object>();
    public IReadOnlyList<DiskOperation> Operations { get; init; } = Array.Empty<DiskOperation>();
    /// <summary>Navigation key of the page that handles this goal (e.g. "Nav.OsMigration").</summary>
    public string? NavigateTo { get; init; }
    public IReadOnlyList<ValidationIssue> Issues { get; init; } = Array.Empty<ValidationIssue>();
    /// <summary>Free text from an AI model (shown as its explanation, never executed).</summary>
    public string? ModelText { get; init; }
}

/// <summary>Recognizes what the user wants from a sentence, in any of the seven app languages (keyword based).</summary>
public static class IntentMatcher
{
    private static readonly (Goal Goal, string[] Words)[] Table =
    {
        (Goal.ExpandC, new[] { "c: full", "c drive full", "more space on c", "extend c", "enlarge c", "expand c", "low disk space", "c: voll", "c: plein", "agrandir c", "ampliar c", "c: lleno", "увеличить c", "мало места", "הגדל את c", "c: מלא", "توسيع c", "c: ممتلئ" }),
        (Goal.UseFreeSpace, new[] { "unallocated", "free space", "new partition", "create partition", "nicht zugeordnet", "neue partition", "non alloué", "nouvelle partition", "sin asignar", "nueva partición", "нераспредел", "новый раздел", "לא מוקצה", "מחיצה חדשה", "غير مخصص", "قسم جديد" }),
        (Goal.ConvertToGpt, new[] { "gpt", "mbr to gpt", "convert to gpt", "in gpt", "en gpt", "a gpt", "в gpt", "ל-gpt", "إلى gpt" }),
        (Goal.MigrateOs, new[] { "ssd", "migrate", "move windows", "new disk", "clone windows", "migrieren", "neue ssd", "migrer", "nouveau disque", "migrar", "disco nuevo", "перенести windows", "новый диск", "העבר את windows", "דיסק חדש", "ترحيل", "قرص جديد" }),
        (Goal.RecoverFiles, new[] { "deleted", "undelete", "recover file", "lost file", "gelöscht", "wiederherstellen", "supprimé", "récupérer", "eliminado", "borrado", "recuperar", "удален", "восстановить файл", "נמחק", "שחזר קובץ", "محذوف", "استرداد ملف" }),
        (Goal.RecoverPartition, new[] { "lost partition", "partition disappeared", "missing partition", "raw", "verlorene partition", "partition perdue", "partición perdida", "пропал раздел", "потерян", "מחיצה אבודה", "מחיצה נעלמה", "قسم مفقود" }),
        (Goal.CheckHealth, new[] { "health", "smart", "failing", "noise", "clicking", "slow disk", "zustand", "défaillant", "santé", "salud", "здоров", "smart", "בריאות", "تالف", "صحة" }),
        (Goal.FreeUpSpace, new[] { "clean", "junk", "temp files", "free up", "aufräumen", "bereinigen", "nettoyer", "limpiar", "очист", "ניקוי", "פנה מקום", "تنظيف" }),
        (Goal.BackupData, new[] { "backup", "back up", "save my files", "sicherung", "sauvegard", "copia de seguridad", "резервн", "גיבוי", "نسخ احتياطي" }),
        (Goal.BootProblem, new[] { "boot", "doesn't start", "won't start", "startet nicht", "ne démarre", "no arranca", "не загружается", "לא עולה", "אתחול", "لا يقلع", "إقلاع" }),
        (Goal.AccountLocked, new[] { "password", "locked out", "forgot", "pin", "kennwort", "passwort", "mot de passe", "contraseña", "пароль", "סיסמה", "ננעלתי", "كلمة المرور" }),
    };

    public static Goal? Match(string text)
    {
        var t = " " + text.ToLowerInvariant() + " ";
        var best = Table.Select(e => (e.Goal, Score: e.Words.Count(w => t.Contains(w)))).Where(x => x.Score > 0).OrderByDescending(x => x.Score).FirstOrDefault();
        return best.Score > 0 ? best.Goal : null;
    }
}

/// <summary>Rule-based planner: turns a goal into simulator-validated operations or a pointer to the right page.</summary>
public static class AssistantPlanner
{
    private const ulong GiB = 1UL << 30;

    public static AssistantPlan Plan(Goal goal, StorageInventory inv, OperationQueue? queue = null)
    {
        AssistantPlan Nav(string key, string page) => new() { Goal = goal, ExplanationKey = key, NavigateTo = page };
        switch (goal)
        {
            case Goal.ExpandC:
            {
                var c = CDriveExpansion.Analyze(inv);
                if (c.CanExpand)
                    return Validate(new AssistantPlan
                    {
                        Goal = goal, ExplanationKey = c.MoveFirst is null ? "Asst.ExpandC" : "Asst.ExpandCMove",
                        Args = new object[] { c.AdjacentFreeBytes, c.MaxSizeBytes }, Operations = c.Operations,
                    }, inv, queue);
                return Nav(c.Blocker == CDriveBlocker.NoFreeSpace ? "Asst.ExpandCNoSpace" : "Asst.ExpandCBlocked", "Nav.Cleanup");
            }
            case Goal.UseFreeSpace:
            {
                var used = inv.Disks.SelectMany(LayoutSimulator.LettersOn).Concat(inv.UnmappedVolumes.Select(v => v.DriveLetter).OfType<char>()).ToHashSet();
                char? letter = Enumerable.Range('D', 23).Select(x => (char)x).FirstOrDefault(x => !used.Contains(x));
                var gap = inv.Disks.Where(d => d.Style is PartitionStyle.Gpt or PartitionStyle.Mbr && !d.IsDynamic)
                    .SelectMany(d => DiskMapLayout.BuildSegments(d, out _).Where(s => s.Kind == SegmentKind.Unallocated).Select(s => (Disk: d, Seg: s)))
                    .OrderByDescending(x => x.Seg.SizeBytes).FirstOrDefault();
                if (gap.Disk is null || gap.Seg.SizeBytes < GiB) return Nav("Asst.NoFreeSpace", "Nav.AllDisks");
                var start = (gap.Seg.OffsetBytes + LayoutSimulator.MiB - 1) / LayoutSimulator.MiB * LayoutSimulator.MiB;
                var size = (gap.Seg.EndBytes - start) / LayoutSimulator.MiB * LayoutSimulator.MiB;
                return Validate(new AssistantPlan
                {
                    Goal = goal, ExplanationKey = "Asst.UseFree", Args = new object[] { size, gap.Disk.Number },
                    Operations = new DiskOperation[] { new CreatePartitionOp(gap.Disk.Number, start, size, "NTFS", "Data", letter == default(char) ? null : letter) },
                }, inv, queue);
            }
            case Goal.ConvertToGpt:
            {
                AssistantPlan? last = null;
                foreach (var disk in inv.Disks.Where(d => d.Style == PartitionStyle.Mbr && !d.IsWindowsDisk && !d.IsBootDisk))
                {
                    last = Validate(new AssistantPlan
                    {
                        Goal = goal, ExplanationKey = "Asst.Convert", Args = new object[] { disk.Number },
                        Operations = new DiskOperation[] { new ConvertStyleOp(disk.Number, PartitionStyle.Gpt) },
                    }, inv, queue);
                    if (last.Operations.Count > 0) return last;
                }
                return last is null ? Nav("Asst.NoMbrDisk", "Nav.DiskConversion") : last with { NavigateTo = "Nav.DiskConversion" };
            }
            case Goal.MigrateOs: return Nav("Asst.Migrate", "Nav.OsMigration");
            case Goal.RecoverFiles: return Nav("Asst.RecoverFiles", "Nav.FileRecovery");
            case Goal.RecoverPartition: return Nav("Asst.RecoverPartition", "Nav.PartitionRecovery");
            case Goal.CheckHealth: return Nav("Asst.Health", "Nav.DiskHealth");
            case Goal.FreeUpSpace: return Nav("Asst.FreeUp", "Nav.Cleanup");
            case Goal.BackupData: return Nav("Asst.Backup", "Nav.Backup");
            case Goal.BootProblem: return Nav("Asst.Boot", "Nav.BootRecovery");
            default: return Nav("Asst.Account", "Nav.AccountRecovery");
        }
    }

    /// <summary>Keeps only operations the simulator accepts (against the queue's current plan, if given) and reports the rest.</summary>
    public static AssistantPlan Validate(AssistantPlan plan, StorageInventory inv, OperationQueue? queue)
    {
        var q = new OperationQueue();
        q.Rebase(inv);
        if (queue is not null) foreach (var op in queue.Items) q.TryAdd(op);
        var ok = new List<DiskOperation>();
        var issues = new List<ValidationIssue>();
        foreach (var op in plan.Operations)
        {
            var r = q.TryAdd(op);
            issues.AddRange(r);
            if (r.All(i => !i.IsError)) ok.Add(op);
        }
        return plan with { Operations = ok, Issues = issues };
    }
}

/// <summary>Any OpenAI-compatible chat endpoint. The key comes from the environment (LASTEAST_LLM_KEY), never from settings files.</summary>
public interface ILlmClient
{
    Task<string> CompleteAsync(string system, string user, CancellationToken ct);
}

public sealed class OpenAiCompatibleClient(HttpClient http, Uri endpoint, string model, string? apiKey) : ILlmClient
{
    public async Task<string> CompleteAsync(string system, string user, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(new
            {
                model, temperature = 0,
                messages = new object[] { new { role = "system", content = system }, new { role = "user", content = user } },
            }),
        };
        if (!string.IsNullOrEmpty(apiKey)) req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
        using var resp = await http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
    }
}

/// <summary>
/// Optional AI planner. The model sees a summary of the layout (no serial numbers, no file names) and may only answer
/// with operations in Lasteast's own vocabulary; every operation is then checked by the simulator, and only the user can
/// queue and apply them. The model can never execute anything.
/// </summary>
public static class LlmPlanner
{
    public const string SystemPrompt =
        "You help plan disk partition changes on Windows for the Lasteast Partition tool. You never execute anything. " +
        "Reply ONLY with JSON: {\"explanation\":\"...\",\"operations\":[{\"kind\":\"create|delete|format|resize|label|letter|move|convert|initialize\",\"disk\":N,...}]}. " +
        "Fields: offset, size, to (bytes), fs (NTFS|FAT32|exFAT|ReFS), label, letter, style (gpt|mbr). Never touch the Windows partition, EFI or recovery " +
        "partitions unless asked, never delete data unless the user explicitly asked, and prefer no operation over a risky one.";

    public static string Describe(StorageInventory inv)
    {
        var sb = new StringBuilder();
        foreach (var d in inv.Disks)
        {
            sb.Append($"Disk {d.Number}: {d.SizeBytes} bytes, {d.Style}, {d.Media}{(d.IsWindowsDisk ? ", Windows disk" : "")}{(d.IsVirtual ? ", virtual" : "")}\n");
            foreach (var s in DiskMapLayout.BuildSegments(d, out _))
                sb.Append(s.Partition is { } p
                    ? $"  partition offset={p.OffsetBytes} size={p.SizeBytes} role={p.Role} fs={p.Volume?.FileSystem} letter={p.Volume?.DriveLetter} used={p.Volume?.UsedBytes}{(p.Volume?.IsWindowsVolume == true ? " WINDOWS" : "")}\n"
                    : $"  unallocated offset={s.OffsetBytes} size={s.SizeBytes}\n");
        }
        return sb.ToString();
    }

    public static async Task<AssistantPlan> PlanAsync(ILlmClient client, string request, StorageInventory inv, OperationQueue? queue, CancellationToken ct)
    {
        var text = await client.CompleteAsync(SystemPrompt, $"Layout:\n{Describe(inv)}\nRequest: {request}", ct);
        var json = text.Trim();
        int a = json.IndexOf('{'), b = json.LastIndexOf('}');
        if (a < 0 || b <= a) return new AssistantPlan { Goal = Goal.UseFreeSpace, ExplanationKey = "Asst.ModelNoPlan", ModelText = text };
        var ops = new List<DiskOperation>();
        var issues = new List<ValidationIssue>();
        string? explanation = null;
        try
        {
            using var doc = JsonDocument.Parse(json[a..(b + 1)]);
            explanation = doc.RootElement.TryGetProperty("explanation", out var e) ? e.GetString() : null;
            if (doc.RootElement.TryGetProperty("operations", out var arr))
                foreach (var o in arr.EnumerateArray())
                {
                    try
                    {
                        var dict = o.EnumerateObject().ToDictionary(p => p.Name.ToLowerInvariant(), p => p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()! : p.Value.GetRawText());
                        ops.Add(OperationParser.Parse(int.Parse(dict["disk"]), dict["kind"], dict));
                    }
                    catch (Exception ex) when (ex is FormatException or KeyNotFoundException or OverflowException)
                    {
                        issues.Add(new ValidationIssue(IssueLevel.Error, "Asst.ModelBadOp", ex.Message));
                    }
                }
        }
        catch (JsonException) { return new AssistantPlan { Goal = Goal.UseFreeSpace, ExplanationKey = "Asst.ModelNoPlan", ModelText = text }; }
        var plan = AssistantPlanner.Validate(new AssistantPlan { Goal = Goal.UseFreeSpace, ExplanationKey = "Asst.Model", Operations = ops, ModelText = explanation ?? text }, inv, queue);
        return plan with { Issues = issues.Concat(plan.Issues).ToList() };
    }
}
