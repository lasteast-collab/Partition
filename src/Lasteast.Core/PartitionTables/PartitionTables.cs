using System.Buffers.Binary;
using System.Text;

namespace Lasteast.Core.PartitionTables;

/// <summary>CRC-32 (IEEE 802.3, reflected 0xEDB88320) as used by GPT headers and entry arrays.</summary>
public static class Crc32
{
    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[i] = c;
        }
        return t;
    }

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (var b in data) crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }
}

public sealed record GptEntry(Guid Type, Guid Id, ulong FirstLba, ulong LastLba, ulong Attributes, string Name)
{
    public ulong Sectors => LastLba - FirstLba + 1;
}

/// <summary>Decoded GPT. Only non-empty entries are kept; <see cref="EntryCount"/> is the array capacity on disk.</summary>
public sealed record GptDisk(Guid DiskGuid, ulong FirstUsableLba, ulong LastUsableLba, int EntryCount, int EntrySize, IReadOnlyList<GptEntry> Entries);

public sealed record MbrEntry(bool Active, byte Type, uint FirstLba, uint Sectors)
{
    public ulong LastLba => (ulong)FirstLba + Sectors - 1;
    public bool IsEmpty => Type == 0 || Sectors == 0;
}

public sealed record MbrDisk(uint Signature, byte[] BootCode, IReadOnlyList<MbrEntry> Entries)
{
    public bool IsProtective => Entries.Any(e => e.Type == 0xEE);
    public bool HasExtended => Entries.Any(e => e.Type is 0x05 or 0x0F or 0x85);
}

public sealed class PartitionTableException(string message) : Exception(message);

/// <summary>
/// Pure encoder/decoder for MBR and GPT structures (UEFI spec 2.x §5). Works on raw byte buffers so
/// every transformation can be tested on image files before it ever touches a disk.
/// </summary>
public static class PartitionTableCodec
{
    public const ulong GptSignature = 0x5452415020494645UL; // "EFI PART"
    public const int GptHeaderSize = 92;
    public const int DefaultEntryCount = 128;
    public const int DefaultEntrySize = 128;

    /// <summary>Sectors occupied by one GPT entry array (128 × 128 bytes).</summary>
    public static int EntryArraySectors(int sectorSize) => (DefaultEntryCount * DefaultEntrySize + sectorSize - 1) / sectorSize;

    // ------------------------------------------------------------------ MBR

    public static MbrDisk ReadMbr(ReadOnlySpan<byte> sector0)
    {
        if (sector0.Length < 512) throw new PartitionTableException("MBR sector too short");
        if (sector0[510] != 0x55 || sector0[511] != 0xAA) throw new PartitionTableException("no MBR signature (55 AA)");
        var entries = new List<MbrEntry>(4);
        for (int i = 0; i < 4; i++)
        {
            var e = sector0.Slice(446 + i * 16, 16);
            entries.Add(new MbrEntry(e[0] == 0x80, e[4], BinaryPrimitives.ReadUInt32LittleEndian(e[8..]), BinaryPrimitives.ReadUInt32LittleEndian(e[12..])));
        }
        return new MbrDisk(BinaryPrimitives.ReadUInt32LittleEndian(sector0[440..]), sector0[..440].ToArray(), entries);
    }

    /// <summary>Writes the table into <paramref name="sector0"/>, keeping boot code untouched unless <paramref name="writeBootCode"/>.</summary>
    public static void WriteMbr(Span<byte> sector0, MbrDisk mbr, bool writeBootCode = false)
    {
        if (mbr.Entries.Count > 4) throw new PartitionTableException("MBR holds at most 4 entries");
        if (writeBootCode) mbr.BootCode.AsSpan(0, Math.Min(440, mbr.BootCode.Length)).CopyTo(sector0);
        BinaryPrimitives.WriteUInt32LittleEndian(sector0[440..], mbr.Signature);
        sector0[444] = 0; sector0[445] = 0;
        for (int i = 0; i < 4; i++)
        {
            var e = sector0.Slice(446 + i * 16, 16);
            e.Clear();
            if (i >= mbr.Entries.Count || mbr.Entries[i].IsEmpty) continue;
            var m = mbr.Entries[i];
            e[0] = (byte)(m.Active ? 0x80 : 0x00);
            WriteChs(e.Slice(1, 3), m.FirstLba);
            e[4] = m.Type;
            WriteChs(e.Slice(5, 3), (uint)Math.Min(m.LastLba, uint.MaxValue));
            BinaryPrimitives.WriteUInt32LittleEndian(e[8..], m.FirstLba);
            BinaryPrimitives.WriteUInt32LittleEndian(e[12..], m.Sectors);
        }
        sector0[510] = 0x55; sector0[511] = 0xAA;
    }

    /// <summary>CHS from LBA with the 255-head/63-sector convention; values beyond 1023 cylinders use FE FF FF.</summary>
    private static void WriteChs(Span<byte> chs, uint lba)
    {
        const uint heads = 255, spt = 63;
        uint c = lba / (heads * spt);
        if (c > 1023) { chs[0] = 0xFE; chs[1] = 0xFF; chs[2] = 0xFF; return; }
        uint h = lba / spt % heads, s = lba % spt + 1;
        chs[0] = (byte)h;
        chs[1] = (byte)((s & 0x3F) | ((c >> 2) & 0xC0));
        chs[2] = (byte)(c & 0xFF);
    }

    // ------------------------------------------------------------------ GPT

    /// <summary>Reads the primary GPT from the first sectors of a disk (LBA 0.. ≥ 34).</summary>
    public static GptDisk ReadGpt(ReadOnlySpan<byte> head, int sectorSize) => ReadGptHeaderAt(head, sectorSize, sectorSize, head);

    /// <summary>Reads a GPT header at <paramref name="headerOffset"/> inside <paramref name="buf"/>; entries are read from <paramref name="entriesBuf"/>
    /// using the header's EntriesLba relative to <paramref name="entriesBaseLba"/>.</summary>
    public static GptDisk ReadGptHeaderAt(ReadOnlySpan<byte> buf, int headerOffset, int sectorSize, ReadOnlySpan<byte> entriesBuf, ulong entriesBaseLba = 0)
    {
        var h = buf.Slice(headerOffset, sectorSize);
        if (BinaryPrimitives.ReadUInt64LittleEndian(h) != GptSignature) throw new PartitionTableException("no GPT header signature");
        var headerSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(h[12..]);
        if (headerSize < GptHeaderSize || headerSize > sectorSize) throw new PartitionTableException("bad GPT header size");
        var hdr = h[..headerSize].ToArray();
        var storedCrc = BinaryPrimitives.ReadUInt32LittleEndian(hdr.AsSpan(16));
        BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(16), 0);
        if (Crc32.Compute(hdr) != storedCrc) throw new PartitionTableException("GPT header CRC mismatch");

        var firstUsable = BinaryPrimitives.ReadUInt64LittleEndian(h[40..]);
        var lastUsable = BinaryPrimitives.ReadUInt64LittleEndian(h[48..]);
        var diskGuid = new Guid(h.Slice(56, 16));
        var entriesLba = BinaryPrimitives.ReadUInt64LittleEndian(h[72..]);
        var count = (int)BinaryPrimitives.ReadUInt32LittleEndian(h[80..]);
        var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(h[84..]);
        var entriesCrc = BinaryPrimitives.ReadUInt32LittleEndian(h[88..]);
        if (count is <= 0 or > 1024 || size is < 128 or > 4096) throw new PartitionTableException("implausible GPT entry array");

        var start = checked((int)((entriesLba - entriesBaseLba) * (ulong)sectorSize));
        var array = entriesBuf.Slice(start, count * size);
        if (Crc32.Compute(array) != entriesCrc) throw new PartitionTableException("GPT entry array CRC mismatch");

        var entries = new List<GptEntry>();
        for (int i = 0; i < count; i++)
        {
            var e = array.Slice(i * size, size);
            var type = new Guid(e[..16]);
            if (type == Guid.Empty) continue;
            var name = Encoding.Unicode.GetString(e.Slice(56, 72)).TrimEnd('\0');
            var nul = name.IndexOf('\0');
            entries.Add(new GptEntry(type, new Guid(e.Slice(16, 16)), BinaryPrimitives.ReadUInt64LittleEndian(e[32..]),
                BinaryPrimitives.ReadUInt64LittleEndian(e[40..]), BinaryPrimitives.ReadUInt64LittleEndian(e[48..]), nul >= 0 ? name[..nul] : name));
        }
        return new GptDisk(diskGuid, firstUsable, lastUsable, count, size, entries);
    }

    /// <summary>
    /// Builds complete GPT structures for a disk of <paramref name="totalSectors"/>:
    /// <c>Head</c> = LBA 0 (protective MBR, keeping <paramref name="existingSector0"/>'s boot code/signature) + LBA 1 header + entries;
    /// <c>Tail</c> = backup entries + backup header, to be written at <c>TailLba</c>.
    /// </summary>
    public static (byte[] Head, byte[] Tail, ulong TailLba) BuildGpt(GptDisk gpt, ulong totalSectors, int sectorSize, ReadOnlySpan<byte> existingSector0 = default)
    {
        int arraySectors = EntryArraySectors(sectorSize);
        ulong lastLba = totalSectors - 1;
        ulong backupEntriesLba = lastLba - (ulong)arraySectors;
        if (gpt.Entries.Count > DefaultEntryCount) throw new PartitionTableException("too many GPT entries");
        foreach (var e in gpt.Entries)
        {
            if (e.FirstLba < gpt.FirstUsableLba || e.LastLba > gpt.LastUsableLba || e.LastLba < e.FirstLba)
                throw new PartitionTableException($"entry {e.Name} [{e.FirstLba}..{e.LastLba}] outside usable range");
        }
        var sorted = gpt.Entries.OrderBy(e => e.FirstLba).ToList();
        for (int i = 1; i < sorted.Count; i++)
            if (sorted[i].FirstLba <= sorted[i - 1].LastLba) throw new PartitionTableException("GPT entries overlap");

        var array = new byte[DefaultEntryCount * DefaultEntrySize];
        for (int i = 0; i < gpt.Entries.Count; i++)
        {
            var e = array.AsSpan(i * DefaultEntrySize, DefaultEntrySize);
            var p = gpt.Entries[i];
            p.Type.TryWriteBytes(e[..16]);
            p.Id.TryWriteBytes(e.Slice(16, 16));
            BinaryPrimitives.WriteUInt64LittleEndian(e[32..], p.FirstLba);
            BinaryPrimitives.WriteUInt64LittleEndian(e[40..], p.LastLba);
            BinaryPrimitives.WriteUInt64LittleEndian(e[48..], p.Attributes);
            var name = Encoding.Unicode.GetBytes(p.Name.Length > 36 ? p.Name[..36] : p.Name);
            name.CopyTo(e[56..]);
        }
        uint arrayCrc = Crc32.Compute(array);

        byte[] Header(ulong myLba, ulong altLba, ulong entriesLba)
        {
            var h = new byte[GptHeaderSize];
            BinaryPrimitives.WriteUInt64LittleEndian(h, GptSignature);
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(8), 0x00010000);
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(12), GptHeaderSize);
            BinaryPrimitives.WriteUInt64LittleEndian(h.AsSpan(24), myLba);
            BinaryPrimitives.WriteUInt64LittleEndian(h.AsSpan(32), altLba);
            BinaryPrimitives.WriteUInt64LittleEndian(h.AsSpan(40), gpt.FirstUsableLba);
            BinaryPrimitives.WriteUInt64LittleEndian(h.AsSpan(48), gpt.LastUsableLba);
            gpt.DiskGuid.TryWriteBytes(h.AsSpan(56, 16));
            BinaryPrimitives.WriteUInt64LittleEndian(h.AsSpan(72), entriesLba);
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(80), DefaultEntryCount);
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(84), DefaultEntrySize);
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(88), arrayCrc);
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(16), Crc32.Compute(h));
            return h;
        }

        // Head: LBA0 protective MBR, LBA1 header, LBA2.. entries
        var head = new byte[(2 + arraySectors) * sectorSize];
        if (existingSector0.Length >= 512) existingSector0[..440].CopyTo(head);
        var sig = existingSector0.Length >= 512 ? BinaryPrimitives.ReadUInt32LittleEndian(existingSector0[440..]) : 0u;
        var protSectors = (uint)Math.Min(totalSectors - 1, uint.MaxValue);
        WriteMbr(head, new MbrDisk(sig, Array.Empty<byte>(), new[] { new MbrEntry(false, 0xEE, 1, protSectors) }));
        Header(1, lastLba, 2).CopyTo(head, sectorSize);
        array.CopyTo(head, 2 * sectorSize);

        // Tail: backup entries then backup header (last LBA)
        var tail = new byte[(arraySectors + 1) * sectorSize];
        array.CopyTo(tail, 0);
        Header(lastLba, 1, backupEntriesLba).CopyTo(tail, arraySectors * sectorSize);
        return (head, tail, backupEntriesLba);
    }

    /// <summary>Default usable range for a fresh GPT on a disk of <paramref name="totalSectors"/>.</summary>
    public static (ulong First, ulong Last) DefaultUsable(ulong totalSectors, int sectorSize)
    {
        int arraySectors = EntryArraySectors(sectorSize);
        return ((ulong)(2 + arraySectors), totalSectors - 2 - (ulong)arraySectors);
    }
}

/// <summary>Partition-type mapping between MBR type bytes and GPT type GUIDs.</summary>
public static class TypeMap
{
    public static Guid? ToGpt(byte mbrType) => mbrType switch
    {
        0x07 or 0x0B or 0x0C or 0x0E or 0x06 or 0x04 or 0x01 => Models.PartitionTypes.GptBasicData,
        0xEF => Models.PartitionTypes.GptEfiSystem,
        0x27 => Models.PartitionTypes.GptWindowsRecovery,
        0x83 => Models.PartitionTypes.GptLinuxFilesystem,
        0x82 => Models.PartitionTypes.GptLinuxSwap,
        _ => null,
    };

    /// <param name="fileSystem">Used to pick 0x07 vs 0x0C for basic data partitions.</param>
    public static byte? ToMbr(Guid type, string? fileSystem) => type switch
    {
        _ when type == Models.PartitionTypes.GptBasicData =>
            string.Equals(fileSystem, "FAT32", StringComparison.OrdinalIgnoreCase) ? (byte)0x0C : (byte)0x07,
        _ when type == Models.PartitionTypes.GptEfiSystem => 0xEF,
        _ when type == Models.PartitionTypes.GptWindowsRecovery => 0x27,
        _ when type == Models.PartitionTypes.GptLinuxFilesystem => 0x83,
        _ when type == Models.PartitionTypes.GptLinuxSwap => 0x82,
        _ => null,
    };
}
