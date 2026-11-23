using Lasteast.Core.Operations;
using Lasteast.Core.RawDisk;

namespace Lasteast.Core.Wipe;

public sealed record WipeReport(long Bytes, int Passes, bool Verified, long VerifiedBytes);

/// <summary>
/// Overwrites a byte range of a device. Random passes use a fast generator seeded per pass and position, so the
/// verification can regenerate what should be there instead of keeping it. Verification reads everything back
/// (or a sample when not requested) and fails on the first difference.
/// </summary>
public static class Wiper
{
    private const int Chunk = 4 * 1024 * 1024;

    private static void Fill(Span<byte> buf, long position, int pass, bool random)
    {
        if (!random) { buf.Clear(); return; }
        // xorshift64* per 8 bytes, seeded by pass and absolute position: reproducible for verification
        for (int i = 0; i < buf.Length; i += 8)
        {
            ulong x = (ulong)(position + i) * 0x9E3779B97F4A7C15UL ^ (ulong)(pass + 1) * 0xD1B54A32D192ED03UL;
            x ^= x >> 12; x ^= x << 25; x ^= x >> 27;
            ulong v = x * 0x2545F4914F6CDD1DUL;
            for (int k = 0; k < 8 && i + k < buf.Length; k++) buf[i + k] = (byte)(v >> (8 * k));
        }
    }

    public static WipeReport Run(IBlockDevice dev, long offset, long length, WipeMethod method, bool fullVerify, IProgress<RawProgress>? progress, CancellationToken ct)
    {
        var passes = method switch { WipeMethod.Zeros => new[] { false }, WipeMethod.Random => new[] { true }, _ => new[] { true, true, false } };
        int ss = Math.Max(512, dev.SectorSize);
        if (offset % ss != 0 || length % ss != 0) throw new RawOperationException("Wipe.Error.Alignment", "range not sector-aligned");
        var buf = new byte[Chunk];
        long total = length * passes.Length;
        for (int p = 0; p < passes.Length; p++)
        {
            for (long done = 0; done < length;)
            {
                ct.ThrowIfCancellationRequested();
                int n = (int)Math.Min(Chunk, length - done);
                Fill(buf.AsSpan(0, n), offset + done, p, passes[p]);
                dev.Write(offset + done, buf.AsSpan(0, n));
                done += n;
                progress?.Report(new RawProgress($"Wipe pass {p + 1}/{passes.Length}", p * length + done, total));
            }
            dev.Flush();
        }
        // verify the last pass: everything, or one chunk in every 64 plus the first and last
        int last = passes.Length - 1;
        var expect = new byte[Chunk];
        long verified = 0;
        long chunks = (length + Chunk - 1) / Chunk;
        for (long c = 0; c < chunks; c++)
        {
            if (!fullVerify && c != 0 && c != chunks - 1 && c % 64 != 0) continue;
            ct.ThrowIfCancellationRequested();
            long pos = c * Chunk;
            int n = (int)Math.Min(Chunk, length - pos);
            dev.Read(offset + pos, buf.AsSpan(0, n));
            Fill(expect.AsSpan(0, n), offset + pos, last, passes[last]);
            if (!buf.AsSpan(0, n).SequenceEqual(expect.AsSpan(0, n)))
                throw new RawOperationException("Wipe.Error.Verify", $"data at {offset + pos} differs from what was written");
            verified += n;
            progress?.Report(new RawProgress("Verify", pos + n, length));
        }
        return new WipeReport(length, passes.Length, true, verified);
    }
}
