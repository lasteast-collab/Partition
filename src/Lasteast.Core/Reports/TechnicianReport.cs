using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Lasteast.Core.Formatting;
using Lasteast.Core.Health;
using Lasteast.Core.Info;
using Lasteast.Core.Models;

namespace Lasteast.Core.Reports;

public sealed record ReportInput
{
    public required StorageInventory Inventory { get; init; }
    public IReadOnlyDictionary<int, DiskHealthReport> Health { get; init; } = new Dictionary<int, DiskHealthReport>();
    public IReadOnlyList<BitLockerVolume> BitLocker { get; init; } = Array.Empty<BitLockerVolume>();
    public StorageSpacesInfo? Spaces { get; init; }
    public string? Technician { get; init; }
    public string? Customer { get; init; }
    public string? Notes { get; init; }
    public bool MaskSerials { get; init; } = true;
    public string ComputerName { get; init; } = Environment.MachineName;
    public DateTimeOffset Created { get; init; } = DateTimeOffset.Now;
}

/// <summary>Technician reports of the storage state: JSON (complete), CSV (one row per partition), HTML (printable, self-contained).</summary>
public static class TechnicianReport
{
    private static string? Mask(string? s, bool mask) => !mask || string.IsNullOrEmpty(s) || s.Length <= 4 ? s : new string('*', s.Length - 4) + s[^4..];

    private static object Model(ReportInput r) => new
    {
        Tool = "Lasteast Partition", r.Created, Computer = r.ComputerName, r.Technician, r.Customer, r.Notes,
        Firmware = r.Inventory.Firmware.ToString(),
        Disks = r.Inventory.Disks.Select(d => new
        {
            d.Number, d.Model, Serial = Mask(d.SerialNumber, r.MaskSerials), d.FirmwareRevision, d.SizeBytes, Style = d.Style.ToString(),
            Media = d.Media.ToString(), Bus = d.Bus.ToString(), d.IsWindowsDisk, d.IsBootDisk, d.IsVirtual, d.TrimEnabled,
            Health = r.Health.TryGetValue(d.Number, out var h) ? new
            {
                Verdict = h.Verdict.ToString(), h.Temperature, h.PowerOnHours, h.WearPercent, h.Sources,
                Findings = h.Findings.Select(f => new { Level = f.Level.ToString(), f.Key, Text = Localization.Loc.Instance.Format(f.Key, f.Args) }),
            } : null,
            Partitions = d.Partitions.Select(p => new
            {
                p.PartitionNumber, p.OffsetBytes, p.SizeBytes, Role = p.Role.ToString(), Alignment = p.Alignment.ToString(),
                Letter = p.Volume?.DriveLetter?.ToString(), p.Volume?.Label, p.Volume?.FileSystem, p.Volume?.CapacityBytes, p.Volume?.FreeBytes,
                BitLocker = r.BitLocker.FirstOrDefault(b => p.Volume is { } v && string.Equals(b.VolumeGuidPath.TrimEnd('\\'), v.VolumeGuidPath.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) is { } bl
                    ? $"{bl.Protection} / {bl.Conversion}" : null,
            }),
        }),
        StorageSpaces = r.Spaces,
    };

    public static string Json(ReportInput r) =>
        JsonSerializer.Serialize(Model(r), new JsonSerializerOptions { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull });

    public static string Csv(ReportInput r)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Disk,Model,Serial,Style,Media,Health,Partition,OffsetBytes,SizeBytes,Role,Letter,Label,FileSystem,FreeBytes,Alignment");
        static string Q(object? v)
        {
            var s = Convert.ToString(v, CultureInfo.InvariantCulture) ?? "";
            // quote everything with separators; neutralize spreadsheet formulas
            if (s.Length > 0 && "=+-@".Contains(s[0])) s = "'" + s;
            return s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 || s.StartsWith('\'') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        }
        foreach (var d in r.Inventory.Disks)
        {
            var health = r.Health.TryGetValue(d.Number, out var h) ? h.Verdict.ToString() : "";
            foreach (var p in d.Partitions)
                sb.AppendLine(string.Join(",", new object?[] { d.Number, d.Model, Mask(d.SerialNumber, r.MaskSerials), d.Style, d.Media, health, p.PartitionNumber, p.OffsetBytes, p.SizeBytes, p.Role,
                    p.Volume?.DriveLetter, p.Volume?.Label, p.Volume?.FileSystem, p.Volume?.FreeBytes, p.Alignment }.Select(Q)));
            if (d.Partitions.Count == 0)
                sb.AppendLine(string.Join(",", new object?[] { d.Number, d.Model, Mask(d.SerialNumber, r.MaskSerials), d.Style, d.Media, health }.Select(Q)));
        }
        return sb.ToString();
    }

    public static string Html(ReportInput r)
    {
        var c = Localization.Loc.Instance.Language.Culture;
        string E(object? v) => WebUtility.HtmlEncode(Convert.ToString(v, c) ?? "");
        string Sz(ulong? b) => b is { } x ? ByteSize.Format(x, c) : "—";
        var L = Localization.Loc.Instance;
        var sb = new StringBuilder();
        sb.Append($"<!DOCTYPE html><html lang=\"{L.Language.Code}\" dir=\"{(L.Language.IsRightToLeft ? "rtl" : "ltr")}\"><head><meta charset=\"utf-8\"><title>Lasteast Partition — {E(r.ComputerName)}</title><style>");
        sb.Append("body{font-family:Segoe UI,Arial,sans-serif;margin:24px;color:#1b2430}h1{font-size:20px;margin:0}h2{font-size:16px;margin:24px 0 6px}" +
                  "table{border-collapse:collapse;width:100%;font-size:12px}th,td{border:1px solid #d5dbe3;padding:4px 6px;text-align:start}th{background:#eef2f6}" +
                  ".muted{color:#5b6675}.Healthy{color:#15803d}.Warning{color:#c2410c}.Critical{color:#b91c1c;font-weight:bold}.Unknown{color:#64748b}");
        sb.Append("</style></head><body>");
        sb.Append($"<h1>Lasteast Partition — {E(L["Tech.ReportTitle"])}</h1><p class=\"muted\">{E(r.ComputerName)} · {E(r.Created.ToString("g", c))} · {E(r.Inventory.Firmware)}");
        if (r.Technician is { Length: > 0 }) sb.Append($" · {E(L["Tech.Technician"])}: {E(r.Technician)}");
        if (r.Customer is { Length: > 0 }) sb.Append($" · {E(L["Tech.Customer"])}: {E(r.Customer)}");
        sb.Append("</p>");
        if (r.Notes is { Length: > 0 }) sb.Append($"<p>{E(r.Notes)}</p>");
        foreach (var d in r.Inventory.Disks)
        {
            r.Health.TryGetValue(d.Number, out var h);
            sb.Append($"<h2>{E(L["Common.Disk"])} {d.Number}: {E(d.Model)} · {Sz(d.SizeBytes)} · {E(d.Style)} · {E(d.Media)} · {E(d.Bus)}");
            if (h is not null) sb.Append($" · <span class=\"{h.Verdict}\">{E(L["Health.Verdict." + h.Verdict])}</span>");
            sb.Append("</h2>");
            sb.Append($"<p class=\"muted\">{E(L["Prop.Serial"])}: {E(Mask(d.SerialNumber, r.MaskSerials) ?? "—")}");
            if (h?.Temperature is { } t) sb.Append($" · {t} °C");
            if (h?.PowerOnHours is { } ph) sb.Append($" · {ph} h");
            if (h?.WearPercent is { } w) sb.Append($" · {E(L["Health.M.Wear"])} {w} %");
            sb.Append("</p>");
            if (h is not null && h.Findings.Count > 0)
            {
                sb.Append("<ul>");
                foreach (var f in h.Findings) sb.Append($"<li class=\"{f.Level}\">{E(L.Format(f.Key, f.Args))}</li>");
                sb.Append("</ul>");
            }
            sb.Append("<table><tr><th>#</th><th>").Append(E(L["Prop.Offset"])).Append("</th><th>").Append(E(L["Col.Capacity"])).Append("</th><th>")
              .Append(E(L["Prop.Type"])).Append("</th><th>").Append(E(L["Editor.Letter"])).Append("</th><th>").Append(E(L["Prop.Label"])).Append("</th><th>")
              .Append(E(L["Prop.FileSystem"])).Append("</th><th>").Append(E(L["Stat.Free"])).Append("</th><th>BitLocker</th></tr>");
            foreach (var p in d.Partitions)
            {
                var bl = r.BitLocker.FirstOrDefault(b => p.Volume is { } v && string.Equals(b.VolumeGuidPath.TrimEnd('\\'), v.VolumeGuidPath.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
                sb.Append($"<tr><td>{p.PartitionNumber}</td><td>{Sz(p.OffsetBytes)}</td><td>{Sz(p.SizeBytes)}</td><td>{E(p.Role)}</td><td>{E(p.Volume?.DriveLetter)}</td>" +
                          $"<td>{E(p.Volume?.Label)}</td><td>{E(p.Volume?.FileSystem)}</td><td>{Sz(p.Volume?.FreeBytes)}</td><td>{E(bl is null ? "" : $"{bl.Protection} / {bl.Conversion}")}</td></tr>");
            }
            sb.Append("</table>");
        }
        if (r.Spaces is { } sp && (sp.Pools.Count > 0 || sp.Spaces.Count > 0))
        {
            sb.Append($"<h2>Storage Spaces</h2><table><tr><th>{E(L["An.Col.Name"])}</th><th>{E(L["Col.Capacity"])}</th><th>{E(L["Health.M.WindowsStatus"])}</th></tr>");
            foreach (var p in sp.Pools) sb.Append($"<tr><td>{E(p.Name)}</td><td>{Sz(p.Allocated)} / {Sz(p.Size)}</td><td>{E(p.Health)}</td></tr>");
            foreach (var v in sp.Spaces) sb.Append($"<tr><td>{E(v.Name)} ({E(v.Resiliency)})</td><td>{Sz(v.Size)}</td><td>{E(v.Health)} · {E(v.Operational)}</td></tr>");
            sb.Append("</table>");
        }
        sb.Append($"<p class=\"muted\">{E(L["Tech.Footer"])}</p></body></html>");
        return sb.ToString();
    }
}
