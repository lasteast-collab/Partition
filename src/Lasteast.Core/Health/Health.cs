using System.Buffers.Binary;
using System.Numerics;
using Lasteast.Core.Models;

namespace Lasteast.Core.Health;

public enum HealthVerdict { Unknown, Healthy, Warning, Critical }

/// <summary>NVMe SMART / Health Information log page (log identifier 02h), NVMe base specification layout.</summary>
public sealed record NvmeHealthLog
{
    public byte CriticalWarning { get; init; }
    public int? TemperatureCelsius { get; init; }
    public byte AvailableSparePercent { get; init; }
    public byte AvailableSpareThreshold { get; init; }
    public byte PercentageUsed { get; init; }
    public BigInteger DataUnitsRead { get; init; }
    public BigInteger DataUnitsWritten { get; init; }
    public BigInteger PowerCycles { get; init; }
    public BigInteger PowerOnHours { get; init; }
    public BigInteger UnsafeShutdowns { get; init; }
    public BigInteger MediaErrors { get; init; }
    public BigInteger ErrorLogEntries { get; init; }
    public uint WarningTemperatureMinutes { get; init; }
    public uint CriticalTemperatureMinutes { get; init; }

    /// <summary>One data unit is 1000 × 512 bytes.</summary>
    public BigInteger BytesWritten => DataUnitsWritten * 512_000;
    public BigInteger BytesRead => DataUnitsRead * 512_000;

    public static NvmeHealthLog Parse(ReadOnlySpan<byte> page)
    {
        if (page.Length < 512) throw new ArgumentException("NVMe health log page is 512 bytes");
        static BigInteger U128(ReadOnlySpan<byte> s) => new(s[..16], isUnsigned: true, isBigEndian: false);
        int kelvin = BinaryPrimitives.ReadUInt16LittleEndian(page[1..]);
        return new NvmeHealthLog
        {
            CriticalWarning = page[0],
            TemperatureCelsius = kelvin > 0 ? kelvin - 273 : null,
            AvailableSparePercent = page[3],
            AvailableSpareThreshold = page[4],
            PercentageUsed = page[5],
            DataUnitsRead = U128(page[32..]),
            DataUnitsWritten = U128(page[48..]),
            PowerCycles = U128(page[112..]),
            PowerOnHours = U128(page[128..]),
            UnsafeShutdowns = U128(page[144..]),
            MediaErrors = U128(page[160..]),
            ErrorLogEntries = U128(page[176..]),
            WarningTemperatureMinutes = BinaryPrimitives.ReadUInt32LittleEndian(page[192..]),
            CriticalTemperatureMinutes = BinaryPrimitives.ReadUInt32LittleEndian(page[196..]),
        };
    }
}

public sealed record SmartAttribute(byte Id, string Name, byte Current, byte Worst, byte Threshold, long Raw, bool Prefail)
{
    public bool BelowThreshold => Threshold > 0 && Current > 0 && Current <= Threshold;
}

/// <summary>ATA SMART READ DATA (attributes) and READ THRESHOLDS sectors.</summary>
public static class AtaSmart
{
    public static readonly IReadOnlyDictionary<byte, string> Names = new Dictionary<byte, string>
    {
        [1] = "Raw read error rate", [3] = "Spin-up time", [4] = "Start/stop count", [5] = "Reallocated sectors",
        [7] = "Seek error rate", [9] = "Power-on hours", [10] = "Spin retry count", [12] = "Power cycle count",
        [170] = "Available reserved space", [171] = "Program fail count", [172] = "Erase fail count", [173] = "Wear leveling count",
        [174] = "Unexpected power loss", [177] = "Wear range delta", [179] = "Used reserved blocks", [181] = "Program fail count",
        [182] = "Erase fail count", [183] = "Runtime bad blocks", [184] = "End-to-end error", [187] = "Reported uncorrectable errors",
        [188] = "Command timeout", [189] = "High fly writes", [190] = "Airflow temperature", [194] = "Temperature",
        [195] = "Hardware ECC recovered", [196] = "Reallocation events", [197] = "Current pending sectors", [198] = "Offline uncorrectable",
        [199] = "UDMA CRC errors", [200] = "Write error rate", [231] = "SSD life left", [232] = "Available reserved space",
        [233] = "Media wearout indicator", [241] = "Total LBAs written", [242] = "Total LBAs read",
    };

    public static IReadOnlyList<SmartAttribute> Parse(ReadOnlySpan<byte> data, ReadOnlySpan<byte> thresholds)
    {
        var list = new List<SmartAttribute>();
        for (int i = 0; i < 30; i++)
        {
            int o = 2 + i * 12;
            if (o + 12 > data.Length) break;
            byte id = data[o];
            if (id == 0) continue;
            ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(data[(o + 1)..]);
            long raw = 0;
            for (int k = 0; k < 6; k++) raw |= (long)data[o + 5 + k] << (8 * k);
            byte thr = 0;
            for (int t = 0; t < 30 && thresholds.Length >= 2 + (t + 1) * 12; t++)
                if (thresholds[2 + t * 12] == id) { thr = thresholds[2 + t * 12 + 1]; break; }
            list.Add(new SmartAttribute(id, Names.TryGetValue(id, out var n) ? n : $"Attribute {id}", data[o + 3], data[o + 4], thr, raw, (flags & 1) != 0));
        }
        return list;
    }

    /// <summary>Temperature attributes keep the current value in the lowest raw byte.</summary>
    public static int? Temperature(IEnumerable<SmartAttribute> attrs) =>
        attrs.FirstOrDefault(a => a.Id is 194 or 190) is { } t && (t.Raw & 0xFF) is > 0 and < 120 ? (int)(t.Raw & 0xFF) : null;
}

/// <summary>Windows' own reliability counters (MSFT_StorageReliabilityCounter) and health status (MSFT_PhysicalDisk).</summary>
public sealed record WindowsReliability
{
    public string? HealthStatus { get; init; }     // Healthy / Warning / Unhealthy / Unknown
    public int? Temperature { get; init; }
    public int? TemperatureMax { get; init; }
    public int? WearPercent { get; init; }
    public long? PowerOnHours { get; init; }
    public long? ReadErrorsUncorrected { get; init; }
    public long? WriteErrorsUncorrected { get; init; }
    public long? ReadErrorsTotal { get; init; }
    public long? StartStopCycles { get; init; }
}

public sealed record HealthFinding(HealthVerdict Level, string Key, params object[] Args);

public sealed record DiskHealthReport
{
    public required int DiskNumber { get; init; }
    public required HealthVerdict Verdict { get; init; }
    public required IReadOnlyList<HealthFinding> Findings { get; init; }
    public NvmeHealthLog? Nvme { get; init; }
    public IReadOnlyList<SmartAttribute> Ata { get; init; } = Array.Empty<SmartAttribute>();
    public WindowsReliability? Windows { get; init; }
    public int? Temperature => Nvme?.TemperatureCelsius ?? AtaSmart.Temperature(Ata) ?? Windows?.Temperature;
    public long? PowerOnHours => Nvme is { } n ? (long)n.PowerOnHours : Ata.FirstOrDefault(a => a.Id == 9)?.Raw is { } h ? h & 0xFFFFFFFF : Windows?.PowerOnHours;
    /// <summary>Endurance used, 0–100+ (NVMe "percentage used", or Windows wear).</summary>
    public int? WearPercent => Nvme?.PercentageUsed ?? Windows?.WearPercent;
    public bool HasData => Nvme is not null || Ata.Count > 0 || Windows is not null;
    public IReadOnlyList<string> Sources { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Turns what the drive reported into a verdict. Only measured values are used; a value the drive did not report is
/// "unknown", never guessed — with no data at all the verdict is Unknown.
/// </summary>
public static class HealthAssessor
{
    public static DiskHealthReport Assess(DiskInfo disk, NvmeHealthLog? nvme, IReadOnlyList<SmartAttribute>? ata, WindowsReliability? win, IReadOnlyList<string> sources)
    {
        ata ??= Array.Empty<SmartAttribute>();
        var f = new List<HealthFinding>();
        bool ssd = disk.Media == MediaKind.Ssd || nvme is not null;

        if (nvme is { } n)
        {
            if ((n.CriticalWarning & 0x01) != 0) f.Add(new(HealthVerdict.Critical, "Health.Nvme.Spare"));
            if ((n.CriticalWarning & 0x02) != 0) f.Add(new(HealthVerdict.Warning, "Health.Nvme.Temp"));
            if ((n.CriticalWarning & 0x04) != 0) f.Add(new(HealthVerdict.Critical, "Health.Nvme.Reliability"));
            if ((n.CriticalWarning & 0x08) != 0) f.Add(new(HealthVerdict.Critical, "Health.Nvme.ReadOnly"));
            if ((n.CriticalWarning & 0x10) != 0) f.Add(new(HealthVerdict.Critical, "Health.Nvme.Backup"));
            if (n.AvailableSpareThreshold > 0 && n.AvailableSparePercent < n.AvailableSpareThreshold) f.Add(new(HealthVerdict.Critical, "Health.SpareLow", n.AvailableSparePercent, n.AvailableSpareThreshold));
            if (n.PercentageUsed >= 100) f.Add(new(HealthVerdict.Critical, "Health.WornOut", n.PercentageUsed));
            else if (n.PercentageUsed >= 80) f.Add(new(HealthVerdict.Warning, "Health.Wear", n.PercentageUsed));
            if (n.MediaErrors > 0) f.Add(new(HealthVerdict.Warning, "Health.MediaErrors", (long)n.MediaErrors));
        }

        foreach (var a in ata.Where(a => a.BelowThreshold))
            f.Add(new(a.Prefail ? HealthVerdict.Critical : HealthVerdict.Warning, "Health.Threshold", a.Name, a.Current, a.Threshold));
        long Raw(byte id) => ata.FirstOrDefault(a => a.Id == id)?.Raw & 0xFFFFFFFF ?? 0;
        if (Raw(5) > 0) f.Add(new(Raw(5) > 100 ? HealthVerdict.Critical : HealthVerdict.Warning, "Health.Reallocated", Raw(5)));
        if (Raw(197) > 0) f.Add(new(HealthVerdict.Warning, "Health.Pending", Raw(197)));
        if (Raw(198) > 0) f.Add(new(HealthVerdict.Warning, "Health.Uncorrectable", Raw(198)));
        if (Raw(187) > 0) f.Add(new(HealthVerdict.Warning, "Health.Reported", Raw(187)));

        if (win is { } w)
        {
            if (string.Equals(w.HealthStatus, "Unhealthy", StringComparison.OrdinalIgnoreCase)) f.Add(new(HealthVerdict.Critical, "Health.WindowsUnhealthy"));
            else if (string.Equals(w.HealthStatus, "Warning", StringComparison.OrdinalIgnoreCase)) f.Add(new(HealthVerdict.Warning, "Health.WindowsWarning"));
            if (nvme is null && w.WearPercent is >= 80) f.Add(new(w.WearPercent >= 100 ? HealthVerdict.Critical : HealthVerdict.Warning, "Health.Wear", w.WearPercent));
            if (w.ReadErrorsUncorrected is > 0 || w.WriteErrorsUncorrected is > 0)
                f.Add(new(HealthVerdict.Warning, "Health.Uncorrected", (w.ReadErrorsUncorrected ?? 0) + (w.WriteErrorsUncorrected ?? 0)));
        }

        var report = new DiskHealthReport { DiskNumber = disk.Number, Verdict = HealthVerdict.Unknown, Findings = f, Nvme = nvme, Ata = ata, Windows = win, Sources = sources };
        if (report.Temperature is { } t && t >= (ssd ? 70 : 55)) f.Add(new(t >= (ssd ? 80 : 60) ? HealthVerdict.Critical : HealthVerdict.Warning, "Health.Hot", t));

        var verdict = !report.HasData ? HealthVerdict.Unknown
            : f.Count == 0 ? HealthVerdict.Healthy
            : f.Max(x => x.Level);
        if (verdict == HealthVerdict.Healthy) f.Add(new(HealthVerdict.Healthy, "Health.Ok"));
        if (!report.HasData) f.Add(new(HealthVerdict.Unknown, "Health.NoData"));
        return report with { Verdict = verdict, Findings = f };
    }
}

/// <summary>Reads health data for one disk (Windows: NVMe log page, ATA SMART, reliability counters).</summary>
public interface IDiskHealthProvider
{
    Task<DiskHealthReport> ReadAsync(DiskInfo disk, CancellationToken ct = default);
}
