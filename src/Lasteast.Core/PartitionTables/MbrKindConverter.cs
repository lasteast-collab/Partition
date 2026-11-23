using System.Buffers.Binary;
using Lasteast.Core.Models;
using Lasteast.Core.RawDisk;

namespace Lasteast.Core.PartitionTables;

/// <summary>
/// Turns an MBR primary partition into a logical one, or back, by rewriting the MBR and the chain of extended boot
/// records (EBRs). Partition data is never moved: each logical partition needs one free sector before it for its EBR,
/// logical partitions must form one run that no primary interrupts, and at most four MBR slots exist.
/// </summary>
public static class MbrKindConverter
{
    public sealed record Entry(ulong Start, ulong Size, byte Type, bool Active, bool Logical)
    {
        public ulong End => Start + Size;
    }

    public sealed record Layout(IReadOnlyList<Entry> Primaries, IReadOnlyList<(Entry Part, ulong Ebr)> Logicals, ulong ExtStart, ulong ExtEnd);

    private static bool IsExtended(byte t) => t is 0x05 or 0x0F or 0x85;

    /// <summary>Plans the new layout; returns an error key, or null with <paramref name="layout"/> set.</summary>
    public static string? Plan(IReadOnlyList<Entry> current, ulong offset, bool toLogical, ulong sector, out Layout? layout)
    {
        layout = null;
        var parts = current.Where(e => !IsExtended(e.Type)).OrderBy(e => e.Start).ToList();
        var target = parts.FirstOrDefault(e => e.Start == offset);
        if (target is null) return "Op.Error.NotFound";
        if (target.Logical == toLogical) return "Op.Error.NoChange";
        if (toLogical && target.Active) return "Op.Error.Protected";
        parts = parts.Select(e => e.Start == offset ? e with { Logical = toLogical, Active = false } : e).ToList();
        var logicals = parts.Where(e => e.Logical).ToList();
        var primaries = parts.Where(e => !e.Logical).ToList();
        int slots = primaries.Count + (logicals.Count > 0 ? 1 : 0);
        if (slots > 4) return "Kind.Error.Slots";
        if (logicals.Count == 0) { layout = new Layout(primaries, Array.Empty<(Entry, ulong)>(), 0, 0); return null; }
        ulong first = logicals[0].Start, last = logicals[^1].End;
        if (primaries.Any(p => p.Start < last && p.End > first)) return "Kind.Error.Interleaved";
        var chain = new List<(Entry, ulong)>();
        for (int i = 0; i < logicals.Count; i++)
        {
            // the EBR goes into the first free sector after whatever precedes this logical partition
            ulong prevEnd = i > 0 ? logicals[i - 1].End : primaries.Where(p => p.End <= logicals[i].Start).Select(p => p.End).DefaultIfEmpty(sector).Max();
            ulong ebr = (prevEnd + sector - 1) / sector * sector;
            if (ebr == 0) ebr = sector;
            if (ebr + sector > logicals[i].Start) return "Kind.Error.NoGap";
            chain.Add((logicals[i], ebr));
        }
        layout = new Layout(primaries, chain, chain[0].Item2, last);
        if (last - layout.ExtStart > uint.MaxValue * (ulong)sector || last / sector > uint.MaxValue) return "Op.Error.Mbr2TB";
        return null;
    }

    /// <summary>Validation against an inventory snapshot (used by the simulator); null when possible.</summary>
    public static string? Check(DiskInfo d, ulong offset, bool toLogical)
    {
        var entries = d.Partitions.Where(p => p.Role != PartitionRole.MbrExtended && !p.IsPending)
            .Select(p => new Entry(p.OffsetBytes, p.SizeBytes, p.MbrType ?? 0x07, p.MbrActive, p.Role == PartitionRole.MbrLogical)).ToList();
        return Plan(entries, offset, toLogical, d.BytesPerLogicalSector == 0 ? 512 : d.BytesPerLogicalSector, out _);
    }

    /// <summary>Reads the MBR and the EBR chain from the device.</summary>
    public static List<Entry> Read(IBlockDevice dev, out List<ulong> ebrSectors)
    {
        int ss = Math.Max(512, dev.SectorSize);
        ebrSectors = new List<ulong>();
        var list = new List<Entry>();
        var mbr = new byte[ss];
        dev.Read(0, mbr);
        if (mbr[510] != 0x55 || mbr[511] != 0xAA) throw new RawOperationException("Kind.Error.NoMbr", "no MBR signature");
        ulong? extLba = null;
        for (int i = 0; i < 4; i++)
        {
            int o = 446 + i * 16;
            byte type = mbr[o + 4];
            uint lba = BinaryPrimitives.ReadUInt32LittleEndian(mbr.AsSpan(o + 8)), cnt = BinaryPrimitives.ReadUInt32LittleEndian(mbr.AsSpan(o + 12));
            if (type == 0 || cnt == 0) continue;
            if (IsExtended(type)) { extLba = lba; continue; }
            list.Add(new Entry((ulong)lba * (ulong)ss, (ulong)cnt * (ulong)ss, type, mbr[o] == 0x80, false));
        }
        var seen = new HashSet<ulong>();
        for (ulong? ebr = extLba; ebr is { } e && seen.Add(e) && seen.Count < 128;)
        {
            var s = new byte[ss];
            dev.Read((long)(e * (ulong)ss), s);
            if (s[510] != 0x55 || s[511] != 0xAA) break;
            ebrSectors.Add(e * (ulong)ss);
            byte t = s[446 + 4];
            uint rel = BinaryPrimitives.ReadUInt32LittleEndian(s.AsSpan(446 + 8)), cnt = BinaryPrimitives.ReadUInt32LittleEndian(s.AsSpan(446 + 12));
            if (t != 0 && cnt != 0) list.Add(new Entry((e + rel) * (ulong)ss, (ulong)cnt * (ulong)ss, t, false, true));
            byte nt = s[462 + 4];
            uint nrel = BinaryPrimitives.ReadUInt32LittleEndian(s.AsSpan(462 + 8));
            ebr = IsExtended(nt) && nrel != 0 ? extLba + nrel : null;
        }
        return list.OrderBy(x => x.Start).ToList();
    }

    private static void Put(Span<byte> sector, int slot, byte type, bool active, ulong lba, ulong count)
    {
        var e = sector.Slice(446 + slot * 16, 16);
        e.Clear();
        e[0] = active ? (byte)0x80 : (byte)0;
        e[1] = 0xFE; e[2] = 0xFF; e[3] = 0xFF; // CHS: beyond addressable, LBA is authoritative
        e[4] = type;
        e[5] = 0xFE; e[6] = 0xFF; e[7] = 0xFF;
        BinaryPrimitives.WriteUInt32LittleEndian(e[8..], (uint)lba);
        BinaryPrimitives.WriteUInt32LittleEndian(e[12..], (uint)count);
    }

    /// <summary>Rewrites the MBR partition slots and EBR chain for the conversion. Boot code and disk signature are kept.</summary>
    public static void Apply(IBlockDevice dev, ulong offset, bool toLogical)
    {
        int ss = Math.Max(512, dev.SectorSize);
        var current = Read(dev, out var oldEbrs);
        var err = Plan(current, offset, toLogical, (ulong)ss, out var layout);
        if (err is not null) throw new RawOperationException(err, $"cannot convert the partition at {offset}");
        var mbr = new byte[ss];
        dev.Read(0, mbr);
        for (int i = 0; i < 4; i++) mbr.AsSpan(446 + i * 16, 16).Clear();
        int slot = 0;
        ulong s = (ulong)ss;
        var slotsInOrder = layout!.Primaries.Select(p => (p.Start, Ext: false, P: p)).ToList();
        if (layout.Logicals.Count > 0) slotsInOrder.Add((layout.ExtStart, true, null!));
        foreach (var (start, ext, p) in slotsInOrder.OrderBy(x => x.Start))
        {
            if (ext) Put(mbr, slot++, 0x0F, false, layout.ExtStart / s, (layout.ExtEnd - layout.ExtStart) / s);
            else Put(mbr, slot++, p.Type, p.Active, p.Start / s, p.Size / s);
        }
        var writes = new List<(ulong Offset, byte[] Data)> { (0, mbr) };
        for (int i = 0; i < layout.Logicals.Count; i++)
        {
            var (part, ebr) = layout.Logicals[i];
            var sec = new byte[ss];
            sec[510] = 0x55; sec[511] = 0xAA;
            Put(sec, 0, part.Type, false, (part.Start - ebr) / s, part.Size / s);
            if (i + 1 < layout.Logicals.Count)
            {
                var (np, nebr) = layout.Logicals[i + 1];
                Put(sec, 1, 0x05, false, (nebr - layout.ExtStart) / s, (np.End - nebr) / s);
            }
            writes.Add((ebr, sec));
        }
        // old EBR sectors that are no longer part of the chain are cleared (they lie in gaps, never inside a partition)
        foreach (var old in oldEbrs.Where(o => writes.All(w => w.Offset != o)))
            if (!current.Any(p => old >= p.Start && old < p.End)) writes.Add((old, new byte[ss]));
        foreach (var (off, data) in writes.OrderBy(w => w.Offset == 0)) dev.Write((long)off, data); // EBRs first, the MBR last
        dev.Flush();
        var check = Read(dev, out _);
        var want = layout.Primaries.Concat(layout.Logicals.Select(l => l.Part)).OrderBy(x => x.Start).ToList();
        if (check.Count != want.Count || check.Zip(want).Any(z => z.First.Start != z.Second.Start || z.First.Size != z.Second.Size || z.First.Logical != z.Second.Logical))
            throw new RawOperationException("Kind.Error.Verify", "the partition table reads back differently");
    }
}
