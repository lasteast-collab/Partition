using System.Security.Cryptography;
using System.Text.Json;
using Lasteast.Core.Models;
using Lasteast.Core.PartitionTables;
using Lasteast.Core.RawDisk;

namespace Lasteast.Core.Boot;

public sealed record BcdEntry(string Type, IReadOnlyDictionary<string, string> Elements)
{
    public string? Get(string key) => Elements.TryGetValue(key, out var v) ? v : null;
    public string Identifier => Get("identifier") ?? "";
    public string Description => Get("description") ?? Type;
}

/// <summary>
/// Parses the text of <c>bcdedit /enum all /v</c>. Section headers are localized by Windows, element names are not;
/// so a section is "a line followed by a dashes line", and elements are "name  value" pairs (continuation lines start with spaces).
/// </summary>
public static class BcdParser
{
    public static IReadOnlyList<BcdEntry> Parse(string text)
    {
        var lines = text.Replace("\r", "").Split('\n');
        var result = new List<BcdEntry>();
        string? type = null;
        Dictionary<string, string>? cur = null;
        string? lastKey = null;
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (i + 1 < lines.Length && lines[i + 1].Length > 3 && lines[i + 1].Trim().All(c => c == '-') && line.Trim().Length > 0)
            {
                if (type is not null && cur is not null) result.Add(new BcdEntry(type, cur));
                type = line.Trim();
                cur = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                lastKey = null;
                i++;
                continue;
            }
            if (cur is null || line.Trim().Length == 0) continue;
            if (char.IsWhiteSpace(line[0]) && lastKey is not null)
            {
                cur[lastKey] += "\n" + line.Trim();
                continue;
            }
            var t = line.Trim();
            int sp = t.IndexOfAny(new[] { ' ', '\t' });
            if (sp < 0) { cur[t] = ""; lastKey = t; continue; }
            lastKey = t[..sp];
            cur[lastKey] = t[sp..].Trim();
        }
        if (type is not null && cur is not null) result.Add(new BcdEntry(type, cur));
        return result;
    }
}

/// <summary>A partition-table backup written by Lasteast before every change (see PartitionTableBackupService).</summary>
public sealed record TableBackup
{
    public required string Folder { get; init; }
    public required DateTimeOffset Created { get; init; }
    public required ulong DiskSize { get; init; }
    public string? Serial { get; init; }
    public string? Model { get; init; }
    public required PartitionStyle Style { get; init; }
    public required IReadOnlyList<(ulong Offset, ulong Size)> Partitions { get; init; }
    public required byte[] Head { get; init; }
    public required byte[] Tail { get; init; }
    public IReadOnlyList<(long Offset, byte[] Sector)> Ebrs { get; init; } = Array.Empty<(long, byte[])>();
}

public static class TableBackups
{
    private const int Chunk = 1024 * 1024;

    /// <summary>Loads and verifies a backup folder (every file's SHA-256 against the manifest). Throws on any mismatch.</summary>
    public static TableBackup Load(string folder, int sectorSize = 512)
    {
        var manifestPath = Path.Combine(folder, "manifest.json");
        if (!File.Exists(manifestPath)) throw new RawOperationException("Restore.Error.NotBackup", folder);
        using var doc = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
        var root = doc.RootElement;
        foreach (var f in root.GetProperty("Files").EnumerateObject())
        {
            var path = Path.Combine(folder, f.Name);
            if (!File.Exists(path) || !string.Equals(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), f.Value.GetString(), StringComparison.OrdinalIgnoreCase))
                throw new RawOperationException("Restore.Error.Corrupt", f.Name);
        }
        var identity = root.GetProperty("Disk").GetString()!.Split('|');
        var head = File.ReadAllBytes(Path.Combine(folder, "head.bin"));
        var tail = File.ReadAllBytes(Path.Combine(folder, "tail.bin"));
        var mbr = head[510] == 0x55 && head[511] == 0xAA ? PartitionTableCodec.ReadMbr(head.AsSpan(0, sectorSize).ToArray()) : null;
        var parts = new List<(ulong, ulong)>();
        PartitionStyle style;
        if (mbr is { IsProtective: true })
        {
            style = PartitionStyle.Gpt;
            var gpt = PartitionTableCodec.ReadGpt(head.AsSpan(0, (2 + PartitionTableCodec.EntryArraySectors(sectorSize)) * sectorSize).ToArray(), sectorSize);
            parts.AddRange(gpt.Entries.Select(e => (e.FirstLba * (ulong)sectorSize, e.Sectors * (ulong)sectorSize)));
        }
        else if (mbr is not null)
        {
            style = PartitionStyle.Mbr;
            parts.AddRange(mbr.Entries.Where(e => !e.IsEmpty && !PartitionTypes.IsMbrExtended(e.Type)).Select(e => ((ulong)e.FirstLba * (ulong)sectorSize, (ulong)e.Sectors * (ulong)sectorSize)));
        }
        else style = PartitionStyle.Raw;

        var ebrs = new List<(long, byte[])>();
        foreach (var file in Directory.GetFiles(folder, "ebr-*.bin"))
        {
            long logicalOffset = long.Parse(Path.GetFileNameWithoutExtension(file)[4..]);
            var data = File.ReadAllBytes(file);
            for (int s = 0; s + sectorSize <= data.Length; s += sectorSize)
            {
                var sec = data.AsSpan(s, sectorSize);
                if (sec[510] != 0x55 || sec[511] != 0xAA) continue;
                var ebr = PartitionTableCodec.ReadMbr(sec.ToArray());
                var first = ebr.Entries.FirstOrDefault();
                long ebrPos = logicalOffset - Chunk + s;
                // an EBR's first entry points at its logical partition, relative to the EBR itself
                if (first is not null && !first.IsEmpty && ebrPos + (long)first.FirstLba * sectorSize == logicalOffset)
                {
                    ebrs.Add((ebrPos, sec.ToArray()));
                    parts.Add(((ulong)logicalOffset, (ulong)first.Sectors * (ulong)sectorSize));
                }
            }
        }

        return new TableBackup
        {
            Folder = folder, Created = root.GetProperty("Created").GetDateTimeOffset(),
            DiskSize = ulong.Parse(identity[1]), Serial = identity.Length > 5 ? identity[5] : null, Model = identity.Length > 6 ? identity[6] : null,
            Style = style, Partitions = parts.OrderBy(p => p.Item1).ToList(), Head = head, Tail = tail, Ebrs = ebrs,
        };
    }

    /// <summary>
    /// Writes back only the partition-table structures (never partition contents): MBR sector 0, GPT header + entries at both
    /// ends, EBR sectors. Backup and disk must be the same size.
    /// </summary>
    public static void Apply(IBlockDevice dev, TableBackup b)
    {
        int ss = dev.SectorSize;
        if ((ulong)dev.Length != b.DiskSize) throw new RawOperationException("Restore.Error.Size", $"disk {dev.Length} vs backup {b.DiskSize}");
        if (b.Style == PartitionStyle.Gpt)
        {
            int headLen = (2 + PartitionTableCodec.EntryArraySectors(ss)) * ss;
            int tailLen = (1 + PartitionTableCodec.EntryArraySectors(ss)) * ss;
            dev.Write(dev.Length - tailLen, b.Tail.AsSpan(b.Tail.Length - tailLen, tailLen));
            dev.Write(0, b.Head.AsSpan(0, headLen));
        }
        else
        {
            foreach (var (off, sec) in b.Ebrs) dev.Write(off, sec);
            dev.Write(0, b.Head.AsSpan(0, ss));
        }
        dev.Flush();
    }

    public static IEnumerable<string> FoldersFor(string root, DiskInfo disk) =>
        Directory.Exists(root)
            ? Directory.GetDirectories(root, $"disk{disk.Number}-*").Concat(Directory.GetDirectories(root, "disk*-*")).Distinct().OrderByDescending(f => f)
            : Enumerable.Empty<string>();
}
