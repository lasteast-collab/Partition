using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Lasteast.Core.Models;
using Lasteast.Core.Operations;
using Lasteast.Storage.Windows.Interop;
using Microsoft.Win32.SafeHandles;

namespace Lasteast.Storage.Windows.Operations;

/// <summary>
/// Saves partition-table metadata before any write:
///  head.bin — first 1 MiB (MBR, GPT header + entries, boot code)
///  tail.bin — last 1 MiB (GPT backup header + entries)
///  ebr-{offset}.bin — 1 MiB before each MBR logical partition (EBR chain)
///  layout.json — the decoded layout; manifest.json — SHA-256 of every file + disk identity.
/// Reads only (GENERIC_READ). Every file is re-read and hash-verified after writing.
/// This protects partition *metadata*; it is not a backup of user data.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PartitionTableBackupService : IPartitionTableBackup
{
    private const int Chunk = 1024 * 1024;
    private const uint GENERIC_READ = 0x80000000;
    private readonly string _root;

    public PartitionTableBackupService(string? root = null) => _root = root ?? DefaultRoot;

    public static string DefaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lasteast", "Partition", "backups");

    public Task<BackupRecord> BackupAsync(DiskInfo disk, StorageInventory inventory, CancellationToken cancellationToken)
        => Task.Run(() => Backup(disk, inventory), cancellationToken);

    private BackupRecord Backup(DiskInfo disk, StorageInventory inventory)
    {
        if (disk.SizeBytes < 2UL * Chunk) throw new OperationFailedException("Exec.Error.Backup", "disk too small");
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var folder = Path.Combine(_root, $"disk{disk.Number}-{stamp}");
        Directory.CreateDirectory(folder);

        var files = new Dictionary<string, string>();
        using (var h = NativeMethods.CreateFile(disk.DevicePath, GENERIC_READ, NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
                   IntPtr.Zero, NativeMethods.OPEN_EXISTING, NativeMethods.FILE_ATTRIBUTE_NORMAL, IntPtr.Zero))
        {
            if (h.IsInvalid) throw NativeMethods.LastError($"Open {disk.DevicePath} for reading");
            void Save(string name, long offset) => files[name] = WriteVerified(Path.Combine(folder, name), ReadAt(h, offset));

            Save("head.bin", 0);
            Save("tail.bin", (long)(disk.SizeBytes - Chunk));
            foreach (var p in disk.Partitions.Where(p => p.Role == PartitionRole.MbrLogical && p.OffsetBytes >= Chunk))
                Save($"ebr-{p.OffsetBytes}.bin", (long)(p.OffsetBytes - Chunk));
        }

        var json = new JsonSerializerOptions { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
        files["layout.json"] = WriteVerified(Path.Combine(folder, "layout.json"), JsonSerializer.SerializeToUtf8Bytes(disk, json));
        var manifest = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Tool = "Lasteast Partition", Created = DateTimeOffset.Now, Disk = DiskFingerprint.Identity(disk),
            Fingerprint = DiskFingerprint.Compute(disk), Files = files,
            Note = "Partition-table metadata only. Restoring requires Lasteast Partition or a sector editor; this is not a backup of your files.",
        }, json);
        var manifestHash = WriteVerified(Path.Combine(folder, "manifest.json"), manifest);

        return new BackupRecord(disk.Number, folder, manifestHash, DateTimeOffset.Now, IsOnDisk(folder, disk, inventory));
    }

    private static byte[] ReadAt(SafeFileHandle h, long offset)
    {
        var buffer = new byte[Chunk];
        int total = 0;
        while (total < Chunk)
        {
            int n = RandomAccess.Read(h, buffer.AsSpan(total), offset + total);
            if (n <= 0) throw new OperationFailedException("Exec.Error.Backup", $"short read at {offset + total}");
            total += n;
        }
        return buffer;
    }

    private static string WriteVerified(string path, byte[] data)
    {
        File.WriteAllBytes(path, data);
        var expected = Convert.ToHexString(SHA256.HashData(data));
        var actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        if (expected != actual) throw new OperationFailedException("Exec.Error.Backup", $"verification failed for {path}");
        return expected;
    }

    /// <summary>True when the backup folder lives on the disk being changed (the UI warns: keep a copy elsewhere).</summary>
    private static bool IsOnDisk(string folder, DiskInfo disk, StorageInventory inventory)
    {
        var root = Path.GetPathRoot(folder);
        if (string.IsNullOrEmpty(root) || root.Length < 2 || root[1] != ':') return false;
        var letter = char.ToUpperInvariant(root[0]);
        return inventory.Disks.First(d => d.Number == disk.Number).Partitions.Any(p => p.Volume?.DriveLetter == letter);
    }
}
