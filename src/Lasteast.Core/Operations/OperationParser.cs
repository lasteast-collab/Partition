using System.Globalization;
using Lasteast.Core.Models;

namespace Lasteast.Core.Operations;

/// <summary>
/// Turns "kind key=value ..." (CLI) or a flat key/value object (assistant) into a <see cref="DiskOperation"/>.
/// Sizes and offsets accept bytes or a unit suffix (MB, GB, TB, MiB, GiB, TiB). Parsing never validates layout —
/// that is the simulator's job, always run afterwards.
/// </summary>
public static class OperationParser
{
    public static readonly IReadOnlyList<string> Kinds = new[] { "create", "delete", "format", "resize", "label", "letter", "move", "convert", "initialize" };

    public static ulong ParseSize(string s)
    {
        s = s.Trim().Replace("_", "");
        var units = new (string Suffix, ulong Factor)[] { ("TIB", 1UL << 40), ("GIB", 1UL << 30), ("MIB", 1UL << 20), ("KIB", 1UL << 10),
            ("TB", 1UL << 40), ("GB", 1UL << 30), ("MB", 1UL << 20), ("KB", 1UL << 10), ("B", 1) };
        foreach (var (suffix, factor) in units)
            if (s.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return (ulong)Math.Round(double.Parse(s[..^suffix.Length], NumberStyles.Float, CultureInfo.InvariantCulture) * factor);
        return ulong.Parse(s, CultureInfo.InvariantCulture);
    }

    public static DiskOperation Parse(int disk, string kind, IReadOnlyDictionary<string, string> a)
    {
        string Req(string k) => a.TryGetValue(k, out var v) && v.Length > 0 ? v : throw new FormatException($"'{kind}' needs {k}=");
        string? Opt(string k) => a.TryGetValue(k, out var v) && v.Length > 0 ? v : null;
        char? Letter(string? v) => v is null || v.Equals("none", StringComparison.OrdinalIgnoreCase) ? null : char.ToUpperInvariant(v.TrimEnd(':')[0]);
        PartitionStyle Style(string v) => v.Trim().ToUpperInvariant() switch
        {
            "GPT" => PartitionStyle.Gpt, "MBR" => PartitionStyle.Mbr, _ => throw new FormatException("style must be gpt or mbr"),
        };
        return kind.ToLowerInvariant() switch
        {
            "create" => new CreatePartitionOp(disk, ParseSize(Req("offset")), ParseSize(Req("size")), Opt("fs") ?? "NTFS", Opt("label"), Letter(Opt("letter"))),
            "delete" => new DeletePartitionOp(disk, ParseSize(Req("offset"))),
            "format" => new FormatPartitionOp(disk, ParseSize(Req("offset")), Opt("fs") ?? "NTFS", Opt("label")),
            "resize" => new ResizePartitionOp(disk, ParseSize(Req("offset")), ParseSize(Req("size"))),
            "label" => new SetLabelOp(disk, ParseSize(Req("offset")), a.TryGetValue("label", out var l) ? l : throw new FormatException("'label' needs label=")),
            "letter" => new SetDriveLetterOp(disk, ParseSize(Req("offset")), Letter(Req("letter"))),
            "move" => new MovePartitionOp(disk, ParseSize(Req("offset")), ParseSize(Req("to"))),
            "convert" => new ConvertStyleOp(disk, Style(Req("style"))),
            "initialize" => new InitializeDiskOp(disk, Style(Opt("style") ?? "gpt")),
            _ => throw new FormatException($"unknown operation '{kind}' (use: {string.Join(", ", Kinds)})"),
        };
    }

    /// <summary>"key=value" tokens; values may be quoted.</summary>
    public static Dictionary<string, string> Pairs(IEnumerable<string> tokens) =>
        tokens.Where(t => t.Contains('=')).Select(t => t.Split('=', 2)).ToDictionary(p => p[0].Trim().ToLowerInvariant(), p => p[1].Trim().Trim('"'));
}
