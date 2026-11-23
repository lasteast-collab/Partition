using Lasteast.Core.Models;
using Lasteast.Core.Settings;

namespace Lasteast.Core.Health;

public sealed record MonitorAlert(string Id, HealthVerdict Level, string Key, params object[] Args);

/// <summary>Rules for the background monitor: low free space, drive health verdicts, temperature.</summary>
public static class MonitorRules
{
    public static IReadOnlyList<MonitorAlert> Evaluate(StorageInventory inv, IReadOnlyDictionary<int, DiskHealthReport> health, AppSettings s)
    {
        var alerts = new List<MonitorAlert>();
        foreach (var d in inv.Disks)
        {
            foreach (var p in d.Partitions)
            {
                if (p.Volume is not { CapacityBytes: > 0, FreeBytes: { } free } v || v.DriveLetter is null) continue;
                double pct = 100.0 * free / v.CapacityBytes!.Value;
                if (pct < s.LowSpacePercent)
                    alerts.Add(new($"space-{v.DriveLetter}", pct < s.LowSpacePercent / 2.0 ? HealthVerdict.Critical : HealthVerdict.Warning,
                        "Mon.LowSpace", $"{v.DriveLetter}:", free, Math.Round(pct, 1)));
            }
            if (!health.TryGetValue(d.Number, out var h)) continue;
            if (h.Verdict is HealthVerdict.Warning or HealthVerdict.Critical)
                alerts.Add(new($"health-{d.Number}", h.Verdict, "Mon.Health", d.Number, d.Model ?? ""));
            int limit = s.TemperatureAlertCelsius > 0 ? s.TemperatureAlertCelsius : (d.Media == MediaKind.Ssd || h.Nvme is not null ? 70 : 55);
            if (h.Temperature is { } t && t >= limit && h.Verdict < HealthVerdict.Warning)
                alerts.Add(new($"temp-{d.Number}", HealthVerdict.Warning, "Mon.Temp", d.Number, t));
        }
        return alerts.OrderByDescending(a => a.Level).ToList();
    }
}
