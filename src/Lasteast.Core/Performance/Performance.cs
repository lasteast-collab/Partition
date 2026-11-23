using System.Diagnostics;
using System.Runtime.InteropServices;
using Lasteast.Core.RawDisk;

namespace Lasteast.Core.Performance;

public sealed record BenchmarkResult(double SeqWriteMBps, double SeqReadMBps, double Rnd4kReadIops, double Rnd4kWriteIops,
    double Rnd4kReadLatencyMs, double Rnd4kWriteLatencyMs, long TestFileBytes, bool CacheBypassed);

public sealed record BenchmarkProgress(string Phase, double Fraction);

/// <summary>
/// Measures a volume with a temporary test file: sequential 1 MiB write and read, then random 4 KiB reads and writes at
/// queue depth 1. With <paramref name="bypassCache"/> (Windows) the file is opened unbuffered and write-through so the
/// numbers reflect the drive, not RAM. The test file is always deleted.
/// </summary>
public static class Benchmark
{
    private const FileOptions NoBuffering = (FileOptions)0x20000000;

    public static unsafe BenchmarkResult Run(string folder, long fileBytes, TimeSpan randomDuration, bool bypassCache,
        IProgress<BenchmarkProgress>? progress, CancellationToken ct)
    {
        const int Block = 1024 * 1024, Small = 4096;
        fileBytes = Math.Max(Block * 16L, fileBytes / Block * Block);
        var path = Path.Combine(folder, $"lasteast-bench-{Guid.NewGuid():N}.tmp");
        var options = FileOptions.WriteThrough | (bypassCache ? NoBuffering : FileOptions.None);
        byte* mem = (byte*)NativeMemory.AlignedAlloc(Block, 4096);
        try
        {
            var buf = new Span<byte>(mem, Block);
            Random.Shared.NextBytes(buf); // incompressible
            double seqW, seqR, rr, rw, rrl, rwl;
            using (var h = File.OpenHandle(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, options, fileBytes))
            {
                var sw = Stopwatch.StartNew();
                for (long off = 0; off < fileBytes; off += Block)
                {
                    ct.ThrowIfCancellationRequested();
                    RandomAccess.Write(h, new ReadOnlySpan<byte>(mem, Block), off);
                    if ((off / Block & 15) == 0) progress?.Report(new("SeqWrite", (double)off / fileBytes));
                }
                if (!bypassCache) RandomAccess.FlushToDisk(h);
                seqW = fileBytes / sw.Elapsed.TotalSeconds / 1e6;

                sw.Restart();
                for (long off = 0; off < fileBytes; off += Block)
                {
                    ct.ThrowIfCancellationRequested();
                    RandomAccess.Read(h, new Span<byte>(mem, Block), off);
                    if ((off / Block & 15) == 0) progress?.Report(new("SeqRead", (double)off / fileBytes));
                }
                seqR = fileBytes / sw.Elapsed.TotalSeconds / 1e6;

                long blocks = fileBytes / Small;
                var rng = new Random(42);
                (rr, rrl) = Random4k(read: true);
                (rw, rwl) = Random4k(read: false);

                (double Iops, double LatencyMs) Random4k(bool read)
                {
                    long n = 0;
                    var t = Stopwatch.StartNew();
                    while (t.Elapsed < randomDuration)
                    {
                        ct.ThrowIfCancellationRequested();
                        long off = rng.NextInt64(blocks) * Small;
                        if (read) RandomAccess.Read(h, new Span<byte>(mem, Small), off);
                        else RandomAccess.Write(h, new ReadOnlySpan<byte>(mem, Small), off);
                        n++;
                        if ((n & 255) == 0) progress?.Report(new(read ? "RandRead" : "RandWrite", t.Elapsed / randomDuration));
                    }
                    if (!read && !bypassCache) RandomAccess.FlushToDisk(h);
                    var secs = t.Elapsed.TotalSeconds;
                    return (n / secs, secs * 1000 / Math.Max(1, n));
                }
            }
            progress?.Report(new("Done", 1));
            return new BenchmarkResult(seqW, seqR, rr, rw, rrl, rwl, fileBytes, bypassCache);
        }
        finally
        {
            NativeMemory.AlignedFree(mem);
            try { File.Delete(path); } catch { }
        }
    }
}

public enum SurfaceState : byte { Unread = 0, Ok = 1, Slow = 2, Error = 3 }

public sealed record SurfaceScanResult(long BytesScanned, long BlockBytes, IReadOnlyList<SurfaceState> Blocks, IReadOnlyList<long> BadSectors, int SlowBlocks, TimeSpan Duration, bool Completed)
{
    public int ErrorBlocks => Blocks.Count(b => b == SurfaceState.Error);
}

public sealed record SurfaceProgress(long Done, long Total, int Errors, int Slow);

/// <summary>
/// Read-only surface test: reads the whole disk in 1 MiB blocks, times each block, and on a read error retries sector
/// by sector to locate the bad sectors. It never writes and never "repairs" (remapping is the drive's job on a write).
/// </summary>
public static class SurfaceScanner
{
    public const int BlockBytes = 1024 * 1024;

    public static SurfaceScanResult Scan(IBlockDevice disk, TimeSpan slowThreshold, IProgress<SurfaceProgress>? progress, CancellationToken ct)
    {
        int ss = disk.SectorSize;
        long blocks = (disk.Length + BlockBytes - 1) / BlockBytes;
        var states = new SurfaceState[blocks];
        var bad = new List<long>();
        int slow = 0, errors = 0;
        var buf = new byte[BlockBytes];
        var sector = new byte[ss];
        var total = Stopwatch.StartNew();
        long b = 0;
        try
        {
            for (; b < blocks; b++)
            {
                ct.ThrowIfCancellationRequested();
                long off = b * BlockBytes;
                int len = (int)Math.Min(BlockBytes, disk.Length - off);
                var sw = Stopwatch.StartNew();
                try
                {
                    disk.Read(off, buf.AsSpan(0, len));
                    states[b] = sw.Elapsed > slowThreshold ? SurfaceState.Slow : SurfaceState.Ok;
                    if (states[b] == SurfaceState.Slow) slow++;
                }
                catch (IOException)
                {
                    states[b] = SurfaceState.Error;
                    errors++;
                    for (long s = off; s < off + len; s += ss)
                    {
                        try { disk.Read(s, sector); }
                        catch (IOException) { bad.Add(s / ss); if (bad.Count > 100_000) break; }
                    }
                }
                if ((b & 63) == 0) progress?.Report(new SurfaceProgress(off + len, disk.Length, errors, slow));
            }
        }
        catch (OperationCanceledException)
        {
            return new SurfaceScanResult(b * (long)BlockBytes, BlockBytes, states, bad, slow, total.Elapsed, false);
        }
        progress?.Report(new SurfaceProgress(disk.Length, disk.Length, errors, slow));
        return new SurfaceScanResult(disk.Length, BlockBytes, states, bad, slow, total.Elapsed, true);
    }
}
