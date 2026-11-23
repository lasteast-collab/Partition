using System.Buffers.Binary;
using System.Text;
using Lasteast.Core.RawDisk;

namespace Lasteast.Core.Recovery;

/// <summary>
/// exFAT read straight from sectors. A deleted file keeps its directory entry set with the InUse bit cleared
/// (0x85 → 0x05, 0xC0 → 0x40, 0xC1 → 0x41) and its clusters are freed in the allocation bitmap; Windows leaves the
/// FAT chain in place, and files stored contiguously (NoFatChain) need no chain at all — so deleted exFAT files are
/// often fully recoverable. Read-only.
/// </summary>
public sealed class ExfatRecoveryReader : IRecoveryReader
{
    private readonly IBlockDevice _vol;
    private readonly long _clusterSize;
    private readonly long _heapOffset;
    private readonly long _fatOffset;
    private readonly uint _clusterCount;
    private readonly uint _rootCluster;
    private readonly uint[] _fat;
    private byte[] _bitmap = Array.Empty<byte>();
    private readonly Dictionary<long, (uint Cluster, long Length, bool NoFatChain, bool Deleted)> _files = new();

    public ExfatRecoveryReader(IBlockDevice volume)
    {
        _vol = volume;
        var bs = new byte[512];
        volume.Read(0, bs);
        if (!bs.AsSpan(3, 8).SequenceEqual("EXFAT   "u8)) throw new RawOperationException("Rec.Error.NotFat", "not an exFAT boot sector");
        int bpsShift = bs[0x6C], spcShift = bs[0x6D];
        if (bpsShift is < 9 or > 12 || bpsShift + spcShift > 25) throw new RawOperationException("Rec.Error.NotFat", "implausible exFAT geometry");
        long bps = 1L << bpsShift;
        _clusterSize = bps << spcShift;
        _fatOffset = BinaryPrimitives.ReadUInt32LittleEndian(bs.AsSpan(0x50)) * bps;
        long fatLength = BinaryPrimitives.ReadUInt32LittleEndian(bs.AsSpan(0x54)) * bps;
        _heapOffset = BinaryPrimitives.ReadUInt32LittleEndian(bs.AsSpan(0x58)) * bps;
        _clusterCount = BinaryPrimitives.ReadUInt32LittleEndian(bs.AsSpan(0x5C));
        _rootCluster = BinaryPrimitives.ReadUInt32LittleEndian(bs.AsSpan(0x60));
        long entries = Math.Min(fatLength / 4, (long)_clusterCount + 2);
        var fatBytes = new byte[entries * 4];
        volume.Read(_fatOffset, fatBytes);
        _fat = new uint[entries];
        for (int i = 0; i < _fat.Length; i++) _fat[i] = BinaryPrimitives.ReadUInt32LittleEndian(fatBytes.AsSpan(i * 4));
        if (Valid(_rootCluster))
        {
            var first = new byte[_clusterSize];
            volume.Read(ClusterOffset(_rootCluster), first);
            for (int e = 0; e + 32 <= first.Length && first[e] != 0; e += 32)
                if (first[e] == 0x83 && first[e + 1] is > 0 and <= 11) { Label = Encoding.Unicode.GetString(first, e + 2, first[e + 1] * 2); break; }
        }
    }

    public string FileSystem => "exFAT";
    public string? Label { get; }

    private long ClusterOffset(uint c) => _heapOffset + (c - 2) * _clusterSize;
    private bool Valid(uint c) => c >= 2 && c < _clusterCount + 2;
    private bool InUse(uint c) => Valid(c) && (c - 2) / 8 < _bitmap.Length && (_bitmap[(c - 2) / 8] >> (int)((c - 2) % 8) & 1) != 0;

    private List<uint> Chain(uint start, long maxClusters)
    {
        var list = new List<uint>();
        var seen = new HashSet<uint>();
        for (uint c = start; Valid(c) && seen.Add(c) && list.Count < maxClusters; c = c < _fat.Length ? _fat[c] : 0)
            list.Add(c);
        return list;
    }

    private List<uint> Clusters(uint first, long length, bool noFatChain)
    {
        long n = Math.Max(1, (length + _clusterSize - 1) / _clusterSize);
        if (noFatChain) return Enumerable.Range(0, (int)Math.Min(n, int.MaxValue)).Select(i => first + (uint)i).Where(Valid).ToList();
        var chain = Chain(first, n);
        // a chain that stops early (FAT zeroed by another tool) is completed as contiguous — the best remaining guess
        for (uint c = chain.Count > 0 ? chain[^1] + 1 : first; chain.Count < n && Valid(c); c++) chain.Add(c);
        return chain;
    }

    private byte[] ReadClusters(IReadOnlyList<uint> clusters)
    {
        var buf = new byte[clusters.Count * _clusterSize];
        for (int i = 0; i < clusters.Count; i++) _vol.Read(ClusterOffset(clusters[i]), buf.AsSpan((int)(i * _clusterSize), (int)_clusterSize));
        return buf;
    }

    private static DateTime? Timestamp(uint ts)
    {
        if (ts == 0) return null;
        try
        {
            return new DateTime(1980 + (int)(ts >> 25), (int)Math.Max(1, ts >> 21 & 15), (int)Math.Max(1, ts >> 16 & 31),
                (int)(ts >> 11 & 31), (int)(ts >> 5 & 63), (int)Math.Min(59, (ts & 31) * 2), DateTimeKind.Local).ToUniversalTime();
        }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    public IReadOnlyList<RecoverableFile> Scan(bool includeExisting, IProgress<RecoveryScanProgress>? progress, CancellationToken ct)
    {
        _files.Clear();
        var result = new List<RecoverableFile>();
        long id = 0;
        var rootDir = ReadClusters(Chain(_rootCluster, 1 << 16));
        // the allocation bitmap (0x81) lives in the root
        for (int e = 0; e + 32 <= rootDir.Length && rootDir[e] != 0; e += 32)
        {
            if (rootDir[e] == 0x81 && _bitmap.Length == 0)
            {
                uint c = BinaryPrimitives.ReadUInt32LittleEndian(rootDir.AsSpan(e + 20));
                long len = (long)BinaryPrimitives.ReadUInt64LittleEndian(rootDir.AsSpan(e + 24));
                if (len > 0 && len <= (_clusterCount + 7) / 8 + _clusterSize)
                {
                    var bm = ReadClusters(Clusters(c, len, false));
                    _bitmap = bm.AsSpan(0, (int)len).ToArray();
                }
            }
        }

        var visited = new HashSet<uint> { _rootCluster };
        var queue = new Queue<(byte[] Dir, string Path, bool Deleted)>();
        queue.Enqueue((rootDir, "", false));
        int dirs = 0;
        while (queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (dir, path, dirDeleted) = queue.Dequeue();
            dirs++;
            for (int e = 0; e + 32 <= dir.Length; e += 32)
            {
                byte type = dir[e];
                if (type == 0x00) break; // end of directory
                if ((type & 0x7F) != 0x05) continue; // not a file entry (in use 0x85 or deleted 0x05)
                bool deleted = (type & 0x80) == 0;
                int secondary = dir[e + 1];
                if (secondary < 2 || e + 32 * (secondary + 1) > dir.Length) continue;
                int s = e + 32;
                if ((dir[s] & 0x7F) != 0x40) continue; // stream extension
                if (!deleted && !ChecksumOk(dir.AsSpan(e, 32 * (secondary + 1)))) continue;
                ushort attr = BinaryPrimitives.ReadUInt16LittleEndian(dir.AsSpan(e + 4));
                bool noFat = (dir[s + 1] & 0x02) != 0;
                int nameLen = dir[s + 3];
                uint first = BinaryPrimitives.ReadUInt32LittleEndian(dir.AsSpan(s + 20));
                long length = (long)BinaryPrimitives.ReadUInt64LittleEndian(dir.AsSpan(s + 24));
                var name = new StringBuilder(nameLen);
                for (int k = 2; k <= secondary && name.Length < nameLen; k++)
                {
                    int n = e + 32 * k;
                    if ((dir[n] & 0x7F) != 0x41) break;
                    for (int ch = 0; ch < 15 && name.Length < nameLen; ch++) name.Append((char)BinaryPrimitives.ReadUInt16LittleEndian(dir.AsSpan(n + 2 + ch * 2)));
                }
                e += 32 * secondary;
                if (name.Length == 0) continue;
                var fileName = RecoveryReaders.SafeName(name.ToString());
                var full = path.Length == 0 ? fileName : path + "\\" + fileName;
                bool isDeleted = deleted || dirDeleted;
                if ((attr & 0x10) != 0)
                {
                    if (Valid(first) && visited.Add(first) && length is > 0 and <= 256 * 1024 * 1024)
                        queue.Enqueue((ReadClusters(Clusters(first, length, noFat)), full, isDeleted));
                    continue;
                }
                if (!isDeleted && !includeExisting) continue;
                var clusters = length == 0 ? new List<uint>() : Clusters(first, length, noFat);
                var rec = !isDeleted ? Recoverability.Intact
                    : length == 0 ? Recoverability.Good
                    : !Valid(first) || clusters.Count * _clusterSize < length ? Recoverability.Unrecoverable
                    : clusters.Any(InUse) ? Recoverability.Poor // reused by another file since the delete
                    : Recoverability.Good;
                _files[id] = (first, length, noFat, isDeleted);
                result.Add(new RecoverableFile
                {
                    Id = id++, Folder = path, Name = fileName, Size = length, IsDeleted = isDeleted, Recoverability = rec,
                    Created = Timestamp(BinaryPrimitives.ReadUInt32LittleEndian(dir.AsSpan(e - 32 * secondary + 8))),
                    Modified = Timestamp(BinaryPrimitives.ReadUInt32LittleEndian(dir.AsSpan(e - 32 * secondary + 12))),
                });
            }
            progress?.Report(new RecoveryScanProgress(dirs, dirs + queue.Count, result.Count));
        }
        return result.OrderBy(f => f.Folder, StringComparer.OrdinalIgnoreCase).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool ChecksumOk(ReadOnlySpan<byte> set)
    {
        ushort chk = 0;
        for (int i = 0; i < set.Length; i++)
        {
            if (i is 2 or 3) continue;
            chk = (ushort)(((chk & 1) != 0 ? 0x8000 : 0) + (chk >> 1) + set[i]);
        }
        return chk == BinaryPrimitives.ReadUInt16LittleEndian(set[2..]);
    }

    public long Extract(RecoverableFile file, Stream output, CancellationToken ct)
    {
        if (!_files.TryGetValue(file.Id, out var f)) throw new RawOperationException("Rec.Error.Unknown", "scan the volume first");
        long remaining = f.Length, written = 0;
        if (remaining == 0) return 0;
        var buf = new byte[_clusterSize];
        foreach (var c in Clusters(f.Cluster, f.Length, f.NoFatChain))
        {
            ct.ThrowIfCancellationRequested();
            int len = (int)Math.Min(_clusterSize, remaining);
            _vol.Read(ClusterOffset(c), buf.AsSpan(0, len));
            output.Write(buf, 0, len);
            remaining -= len;
            written += len;
            if (remaining == 0) break;
        }
        if (remaining > 0) throw new RawOperationException("Rec.Error.Truncated", $"{remaining} bytes beyond the volume");
        return written;
    }
}
