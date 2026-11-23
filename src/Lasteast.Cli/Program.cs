using System.Globalization;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using Lasteast.Core.Backup;
using Lasteast.Core.Boot;
using Lasteast.Core.Formatting;
using Lasteast.Core.Layout;
using Lasteast.Core.Localization;
using Lasteast.Core.Models;
using Lasteast.Core.Recovery;
using Lasteast.Storage.Windows.Detection;
using Lasteast.Storage.Windows.Operations;

// Lasteast command line.
//   lasteast inventory [--json] [--show-serials]
//   lasteast scan-lost <disk> [--deep]                      read-only
//   lasteast undelete <disk> <offset> <size> --to <folder> [--all] [--by-content]
//   lasteast backup <folder>... --to <folder> [--no-verify]
//   lasteast bcd                                             read-only
//   lasteast account-help [--lang xx]
//   lasteast rescue                                          interactive menu (Lasteast Rescue Boot)
//   lasteast op <disk> <kind> key=value... [--apply --confirm APPLY] [--allow-physical]   dry-run by default
//   lasteast report --html|--csv|--json <file> [--show-serials]
//   lasteast vdisk create <file> <size> [--fixed] | attach <file> | detach <file>
// Exit codes: 0 ok, 1 error, 2 usage, 3 unsupported OS.

return await Cli.Main(args);

[SupportedOSPlatform("windows")]
internal static class Cli
{
    private static readonly CultureInfo C = CultureInfo.CurrentCulture;

    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            Usage();
            return args.Length == 0 ? 2 : 0;
        }
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("Lasteast requires Windows 10 or 11 (or Windows PE).");
            return 3;
        }
        if (Opt(args, "--lang") is { } lang && Loc.Languages.Any(l => l.Code == lang)) Loc.Instance.SetLanguage(lang);
        try
        {
            return args[0] switch
            {
                "inventory" => await Inventory(args),
                "scan-lost" => await ScanLost(args),
                "undelete" => await Undelete(args),
                "backup" => Backup(args),
                "bcd" => Bcd(),
                "account-help" => AccountHelp(),
                "rescue" => await Rescue(),
                "op" => await Op(args),
                "report" => await Report(args),
                "vdisk" => VDisk(args),
                _ => Unknown(args[0]),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Error: " + ex.Message);
            return 1;
        }
    }

    private static void Usage()
    {
        Console.WriteLine("Lasteast Partition — command line");
        Console.WriteLine("  inventory [--json] [--show-serials]                 list disks and partitions (read-only)");
        Console.WriteLine("  scan-lost <disk> [--deep]                           find lost partitions (read-only)");
        Console.WriteLine("  undelete <disk> <offset> <size> --to <folder> [--all] [--by-content]  copy deleted (or all) files of a volume to another disk;\n      --by-content finds photos/videos/PDF/Office files by their content (also used when the file system is unreadable)");
        Console.WriteLine("  backup <folder>... --to <folder> [--no-verify]      copy folders and verify");
        Console.WriteLine("  bcd                                                 show boot entries (read-only)");
        Console.WriteLine("  account-help [--lang en|he|ar|fr|es|de|ru]           supported ways to regain access to Windows");
        Console.WriteLine("  rescue                                              interactive rescue menu");
        Console.WriteLine("  op <disk> <kind> key=value... [--apply --confirm APPLY] [--allow-physical]");
        Console.WriteLine("        kinds: " + string.Join(", ", Lasteast.Core.Operations.OperationParser.Kinds) + "  (dry-run unless --apply)");
        Console.WriteLine("        e.g. op 2 create offset=1MiB size=20GB fs=NTFS label=Data letter=G");
        Console.WriteLine("  report --html|--csv|--json <file> [--show-serials]   technician report");
        Console.WriteLine("  vdisk create <file.vhdx> <size> [--fixed] | attach <file> | detach <file>");
        Console.WriteLine("'op' applies changes only with --apply --confirm APPLY, through the same checks, backup and verification as the app.");
    }

    private static int Unknown(string cmd)
    {
        Console.Error.WriteLine($"Unknown command '{cmd}'. Try --help.");
        return 2;
    }

    private static string? Opt(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static Task<StorageInventory> Inv() => new WindowsStorageInventoryProvider(privileges: new WindowsPrivilegeService()).GetInventoryAsync();

    private static string? Mask(string? s) => string.IsNullOrEmpty(s) || s.Length <= 4 ? s : new string('*', s.Length - 4) + s[^4..];

    // ------------------------------------------------------------------ inventory

    private static async Task<int> Inventory(string[] args)
    {
        var inv = await Inv();
        if (!args.Contains("--show-serials"))
            inv = inv with { Disks = inv.Disks.Select(d => d with { SerialNumber = Mask(d.SerialNumber) }).ToList() };
        if (args.Contains("--json"))
        {
            Console.WriteLine(JsonSerializer.Serialize(inv, new JsonSerializerOptions { WriteIndented = true, Converters = { new JsonStringEnumConverter() } }));
            return 0;
        }
        PrintInventory(inv);
        return 0;
    }

    private static void PrintInventory(StorageInventory inv)
    {
        Console.WriteLine($"Lasteast Partition — inventory  ({inv.CapturedAt:G})  firmware={inv.Firmware}  elevated={inv.IsElevated}");
        foreach (var d in inv.Disks)
        {
            Console.WriteLine();
            Console.WriteLine($"Disk {d.Number}: {d.Model}  {ByteSize.Format(d.SizeBytes, C)}  {d.Style} {d.Media} {d.Bus}" +
                              $"{(d.IsWindowsDisk ? "  [Windows]" : "")}{(d.IsBootDisk ? "  [Boot]" : "")}  serial={d.SerialNumber ?? "-"}");
            foreach (var s in DiskMapLayout.BuildSegments(d, out _))
            {
                if (s.Partition is not { } p)
                {
                    Console.WriteLine($"   {"-",-4} {"(unallocated)",-28} {ByteSize.Format(s.SizeBytes, C),14}  @ {s.OffsetBytes}");
                    continue;
                }
                var v = p.Volume;
                var name = v?.DriveLetter is { } l ? $"{l}:" : "*:";
                Console.WriteLine($"   #{p.PartitionNumber,-3} {name + " " + (v?.Label ?? p.GptName ?? ""),-28} {ByteSize.Format(p.SizeBytes, C),14}" +
                                  $"  {v?.FileSystem ?? "-",-6} {p.Role,-18} {p.Alignment,-16} @ {p.OffsetBytes}" +
                                  $"{(v?.FreeBytes is { } f ? $"  free {ByteSize.Format(f, C)}" : "")}");
            }
            foreach (var n in d.Notes) Console.WriteLine($"   note: {n}");
        }
        foreach (var n in inv.Notes) Console.WriteLine($"note: {n}");
    }

    // ------------------------------------------------------------------ recovery

    private static async Task<DiskInfo> DiskArg(string[] args, int index)
    {
        if (args.Length <= index || !int.TryParse(args[index], out var n)) throw new ArgumentException("disk number expected");
        return (await Inv()).Disks.FirstOrDefault(d => d.Number == n) ?? throw new ArgumentException($"disk {n} not found");
    }

    private static IReadOnlyList<FoundPartition> FindLost(DiskInfo disk, bool deep)
    {
        using var dev = new PhysicalDiskDevice(disk, writable: false);
        var existing = disk.Partitions.Where(p => p.Role != PartitionRole.MbrExtended).Select(p => (p.OffsetBytes, p.SizeBytes)).ToList();
        int last = -1;
        var progress = new SyncProgress(p =>
        {
            int pct = p.Total == 0 ? 100 : (int)(100 * p.Done / p.Total);
            if (pct != last) { last = pct; Console.Write($"\r  scanning… {pct}%  found {p.Found}   "); }
        });
        var found = LostPartitionScanner.Scan(dev, existing, deep ? LostScanMode.Deep : LostScanMode.Quick, progress, CancellationToken.None);
        Console.WriteLine();
        return found;
    }

    private static async Task<int> ScanLost(string[] args)
    {
        var disk = await DiskArg(args, 1);
        var found = FindLost(disk, args.Contains("--deep"));
        if (found.Count == 0) Console.WriteLine("No lost partitions found.");
        foreach (var f in found)
            Console.WriteLine($"  {f.FileSystem,-6} offset {f.OffsetBytes}  size {f.SizeBytes} ({ByteSize.Format(f.SizeBytes, C)})  {f.Label}  confidence={f.Confidence}" +
                              (f.FromBackupBootSector ? "  [start damaged]" : "") + (f.Conflicts ? "  [overlaps another candidate]" : ""));
        return 0;
    }

    private static async Task<int> Undelete(string[] args)
    {
        var disk = await DiskArg(args, 1);
        if (args.Length < 4 || !ulong.TryParse(args[2], out var off) || !ulong.TryParse(args[3], out var size)) throw new ArgumentException("offset and size (bytes) expected");
        var to = Opt(args, "--to") ?? throw new ArgumentException("--to <folder> is required");
        var inv = await Inv();
        return RecoverFiles(inv, disk, off, size, to, args.Contains("--all"), interactive: false, byContent: args.Contains("--by-content"));
    }

    private static int RecoverFiles(StorageInventory inv, DiskInfo disk, ulong off, ulong size, string to, bool all, bool interactive, bool byContent = false)
    {
        using var dev = new PhysicalDiskDevice(disk, writable: false);
        var vol = new OffsetDevice(dev, (long)off, (long)size);
        IRecoveryReader reader = (byContent ? null : RecoveryReaders.Open(vol)) ?? new CarvingRecoveryReader(vol);
        if (reader is CarvingRecoveryReader) Console.WriteLine("Searching by file content (names and folders are not recoverable this way).");
        Console.WriteLine($"  {reader.FileSystem} \"{reader.Label}\" — reading the file table…");
        var files = reader.Scan(all, null, CancellationToken.None);
        var wanted = files.Where(f => f.Recoverability != Recoverability.Unrecoverable && (all || f.IsDeleted)).ToList();
        Console.WriteLine($"  {files.Count(f => f.IsDeleted)} deleted file(s); {wanted.Count} will be copied to {to}.");
        foreach (var f in wanted.Take(20)) Console.WriteLine($"    {f.Recoverability,-6} {ByteSize.Format((ulong)f.Size, C),10}  {f.FullPath}");
        if (wanted.Count > 20) Console.WriteLine($"    … and {wanted.Count - 20} more");
        if (interactive && !Confirm("Copy these files? (y/N) ")) return 0;
        var rep = RecoveryWriter.Recover(reader, wanted, to, disk.Number, VolumeLocator.DiskOfPath(inv, to), null, CancellationToken.None);
        Console.WriteLine($"  Recovered {rep.Recovered} file(s), {ByteSize.Format((ulong)rep.Bytes, C)}; {rep.Failed} failed.");
        foreach (var (f, e) in rep.Errors.Take(10)) Console.WriteLine($"    ! {f}: {e}");
        return rep.Failed == 0 ? 0 : 1;
    }

    private static int Backup(string[] args)
    {
        var to = Opt(args, "--to") ?? throw new ArgumentException("--to <folder> is required");
        var sources = args.Skip(1).TakeWhile(a => a != "--to" && !a.StartsWith("--")).ToList();
        if (sources.Count == 0) throw new ArgumentException("at least one source folder is required");
        return RunBackup(sources, to, !args.Contains("--no-verify"));
    }

    private static int RunBackup(IReadOnlyList<string> sources, string to, bool verify)
    {
        var target = Path.Combine(to, $"Lasteast Backup {DateTime.Now:yyyy-MM-dd HHmm}");
        var progress = new SyncBackupProgress(p => Console.Write($"\r  {p.Files} files · {ByteSize.Format((ulong)p.Bytes, C)}      "));
        var r = FolderBackup.Run(sources, target, verify, progress, CancellationToken.None);
        Console.WriteLine();
        Console.WriteLine($"  Copied {r.Copied} file(s) ({ByteSize.Format((ulong)r.Bytes, C)}), skipped {r.Skipped}, failed {r.Failed}. → {target}");
        foreach (var (f, e) in r.Errors.Take(10)) Console.WriteLine($"    ! {f}: {e}");
        return r.Failed == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------------ boot

    private static int Bcd()
    {
        var entries = new WindowsBootTools().EnumerateBcdAsync().GetAwaiter().GetResult();
        foreach (var e in entries)
            Console.WriteLine($"{e.Type}\n  {e.Identifier}  {e.Description}\n  device={e.Get("device")}  path={e.Get("path")}\n");
        return 0;
    }

    private static int AccountHelp()
    {
        Console.WriteLine(Loc.Instance["Acc.Policy"]);
        foreach (var k in new[] { "Msa", "Local", "Work", "Pin", "BitLocker", "Reset" })
        {
            Console.WriteLine();
            Console.WriteLine("■ " + Loc.Instance[$"Acc.{k}.Title"]);
            Console.WriteLine(Loc.Instance[$"Acc.{k}.Steps"]);
        }
        return 0;
    }

    // ------------------------------------------------------------------ operations (dry-run by default)

    private static async Task<int> Op(string[] args)
    {
        if (args.Length < 3 || !int.TryParse(args[1], out var n)) throw new ArgumentException("usage: op <disk> <kind> key=value ...");
        var op = Lasteast.Core.Operations.OperationParser.Parse(n, args[2], Lasteast.Core.Operations.OperationParser.Pairs(args.Skip(3)));
        var inv = await Inv();
        var disk = inv.Disks.FirstOrDefault(d => d.Number == n) ?? throw new ArgumentException($"disk {n} not found");
        var lookup = new Func<int, DiskInfo?>(k => inv.Disks.FirstOrDefault(d => d.Number == k));
        var sim = Lasteast.Core.Operations.LayoutSimulator.Simulate(disk, new[] { op }, null, lookup);
        Console.WriteLine($"Operation: {op}");
        foreach (var i in sim.AllIssues) Console.WriteLine($"  {(i.IsError ? "ERROR" : "warn ")} {Loc.Instance.Format(i.Key, i.Args)}");
        Console.WriteLine("Result:");
        foreach (var s in DiskMapLayout.BuildSegments(sim.After, out _))
            Console.WriteLine(s.Partition is { } p
                ? $"   @{p.OffsetBytes,-14} {ByteSize.Format(p.SizeBytes, C),12}  {p.Volume?.FileSystem ?? "-",-6} {p.Volume?.DriveLetter}{(p.IsPending ? "  (new)" : "")}"
                : $"   @{s.OffsetBytes,-14} {ByteSize.Format(s.SizeBytes, C),12}  (unallocated)");
        if (!sim.IsValid) return 1;
        if (!args.Contains("--apply")) { Console.WriteLine("Dry run: nothing was changed. Add --apply --confirm APPLY to perform it."); return 0; }

        var log = new Lasteast.Core.Logging.FileLog(Lasteast.Core.AppPaths.Logs);
        var priv = new WindowsPrivilegeService();
        var provider = new WindowsStorageInventoryProvider(log, priv);
        var engine = new Lasteast.Core.Operations.GuardedExecutionEngine(provider, new WindowsOperationExecutor(provider, log), new PartitionTableBackupService(), priv,
            new Lasteast.Core.Operations.JsonlOperationHistory(Lasteast.Core.AppPaths.History), log);
        var report = await engine.ExecuteAsync(new[] { op }, inv, new Lasteast.Core.Operations.ExecutionPolicy(args.Contains("--allow-physical")), Opt(args, "--confirm") ?? "",
            new Progress<Lasteast.Core.Operations.ExecutionProgress>(e => Console.WriteLine($"  {e.State}: {e.Operation}")));
        foreach (var b in report.Backups) Console.WriteLine($"  partition table backup: {b.Folder}");
        Console.WriteLine(report.Succeeded ? "Done." : $"Not done: {Loc.Instance.Format(report.FailureKey ?? "", report.FailureArgs)} {report.FailureDetail}" + (report.NothingWritten ? " (nothing was written)" : ""));
        return report.Succeeded ? 0 : 1;
    }

    private static async Task<int> Report(string[] args)
    {
        string? fmt = args.Contains("--html") ? "--html" : args.Contains("--csv") ? "--csv" : args.Contains("--json") ? "--json" : null;
        var file = fmt is null ? null : Opt(args, fmt);
        if (file is null) throw new ArgumentException("usage: report --html|--csv|--json <file>");
        var inv = await Inv();
        var health = new Dictionary<int, Lasteast.Core.Health.DiskHealthReport>();
        var hp = new Lasteast.Storage.Windows.Health.WindowsHealthProvider();
        foreach (var d in inv.Disks) health[d.Number] = await hp.ReadAsync(d);
        var info = new Lasteast.Storage.Windows.Info.WindowsSystemInfo();
        var input = new Lasteast.Core.Reports.ReportInput
        {
            Inventory = inv, Health = health, BitLocker = await info.BitLockerAsync(), Spaces = await info.StorageSpacesAsync(), MaskSerials = !args.Contains("--show-serials"),
        };
        File.WriteAllText(file, fmt switch
        {
            "--html" => Lasteast.Core.Reports.TechnicianReport.Html(input),
            "--csv" => Lasteast.Core.Reports.TechnicianReport.Csv(input),
            _ => Lasteast.Core.Reports.TechnicianReport.Json(input),
        });
        Console.WriteLine("Report written: " + Path.GetFullPath(file));
        return 0;
    }

    private static int VDisk(string[] args)
    {
        if (args.Length < 3) throw new ArgumentException("usage: vdisk create <file> <size> [--fixed] | attach <file> | detach <file>");
        switch (args[1])
        {
            case "create":
                if (args.Length < 4) throw new ArgumentException("size required");
                Lasteast.Storage.Windows.Info.VirtualDiskService.Create(Path.GetFullPath(args[2]), Lasteast.Core.Operations.OperationParser.ParseSize(args[3]), dynamic: !args.Contains("--fixed"));
                Console.WriteLine("Created " + Path.GetFullPath(args[2]));
                return 0;
            case "attach":
                Console.WriteLine("Attached as " + Lasteast.Storage.Windows.Info.VirtualDiskService.Attach(Path.GetFullPath(args[2])));
                return 0;
            case "detach":
                Lasteast.Storage.Windows.Info.VirtualDiskService.Detach(Path.GetFullPath(args[2]));
                Console.WriteLine("Detached.");
                return 0;
        }
        return Unknown("vdisk " + args[1]);
    }

    // ------------------------------------------------------------------ rescue menu

    private static bool Confirm(string prompt)
    {
        Console.Write(prompt);
        return Console.ReadLine()?.Trim().ToLowerInvariant() is "y" or "yes";
    }

    private static string Ask(string prompt)
    {
        Console.Write(prompt);
        return Console.ReadLine()?.Trim() ?? "";
    }

    private static async Task<int> Rescue()
    {
        Console.WriteLine("==============================================");
        Console.WriteLine("  Lasteast Rescue Boot");
        Console.WriteLine("  Nothing is changed without your confirmation.");
        Console.WriteLine("==============================================");
        while (true)
        {
            Console.WriteLine();
            Console.WriteLine("  1  Show disks and partitions");
            Console.WriteLine("  2  Find lost partitions");
            Console.WriteLine("  3  Recover deleted files to another disk");
            Console.WriteLine("  4  Copy folders to another disk (backup)");
            Console.WriteLine("  5  Rebuild boot files of an installed Windows");
            Console.WriteLine("  6  Can't sign in to Windows? (guidance)");
            Console.WriteLine("  7  Command prompt");
            Console.WriteLine("  8  Restart     9  Shut down     0  Exit menu");
            var choice = Ask("> ");
            try
            {
                switch (choice)
                {
                    case "1": PrintInventory(await Inv()); break;
                    case "2":
                    {
                        var disk = await PickDisk();
                        if (disk is null) break;
                        var found = FindLost(disk, Confirm("Deep scan (every sector, slow)? (y/N) "));
                        if (found.Count == 0) Console.WriteLine("  No lost partitions found.");
                        foreach (var f in found) Console.WriteLine($"  {f.FileSystem} at {f.OffsetBytes}, {ByteSize.Format(f.SizeBytes, C)}, confidence {f.Confidence}");
                        Console.WriteLine("  To get files back without changing the disk, use option 3 with these numbers.");
                        Console.WriteLine("  Restoring the partition itself is done in the Lasteast Partition app.");
                        break;
                    }
                    case "3":
                    {
                        var inv = await Inv();
                        var disk = await PickDisk();
                        if (disk is null) break;
                        var vols = disk.Partitions.Where(p => p.Volume?.FileSystem is "NTFS" or "FAT32" || p.Volume is null).ToList();
                        for (int i = 0; i < vols.Count; i++)
                            Console.WriteLine($"  {i + 1}  @{vols[i].OffsetBytes}  {ByteSize.Format(vols[i].SizeBytes, C)}  {vols[i].Volume?.FileSystem ?? "?"}  {vols[i].Volume?.Label}");
                        Console.WriteLine("  (or type: <offset> <size> for a lost partition)");
                        var sel = Ask("Volume: ").Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        ulong off, size;
                        if (sel.Length == 2 && ulong.TryParse(sel[0], out off) && ulong.TryParse(sel[1], out size)) { }
                        else if (sel.Length == 1 && int.TryParse(sel[0], out var k) && k >= 1 && k <= vols.Count) { off = vols[k - 1].OffsetBytes; size = vols[k - 1].SizeBytes; }
                        else break;
                        var to = Ask("Copy to folder on ANOTHER disk (e.g. E:\\Recovered): ");
                        if (to.Length == 0) break;
                        RecoverFiles(inv, disk, off, size, to, Confirm("Also copy files that were not deleted? (y/N) "), interactive: true);
                        break;
                    }
                    case "4":
                    {
                        var src = Ask("Folders to copy (separate with ;), e.g. C:\\Users\\Anna: ").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                        var to = Ask("Copy to folder on ANOTHER disk: ");
                        if (src.Length > 0 && to.Length > 0) RunBackup(src, to, verify: true);
                        break;
                    }
                    case "5": await RepairBoot(); break;
                    case "6": AccountHelp(); break;
                    case "7": System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false })?.WaitForExit(); break;
                    case "8": System.Diagnostics.Process.Start("wpeutil", "reboot"); return 0;
                    case "9": System.Diagnostics.Process.Start("wpeutil", "shutdown"); return 0;
                    case "0": return 0;
                }
            }
            catch (Exception ex) { Console.WriteLine("  Error: " + ex.Message); }
        }
    }

    private static async Task<DiskInfo?> PickDisk()
    {
        var inv = await Inv();
        foreach (var d in inv.Disks) Console.WriteLine($"  {d.Number}  {d.Model}  {ByteSize.Format(d.SizeBytes, C)}  {d.Style}");
        var s = Ask("Disk number: ");
        return int.TryParse(s, out var n) ? inv.Disks.FirstOrDefault(d => d.Number == n) : null;
    }

    private static async Task RepairBoot()
    {
        var inv = await Inv();
        var candidates = new List<(DiskInfo Disk, PartitionInfo Part, string Root)>();
        foreach (var d in inv.Disks)
        foreach (var p in d.Partitions)
        {
            var root = p.Volume?.MountPoints.FirstOrDefault();
            if (root is null || root.StartsWith("X:", StringComparison.OrdinalIgnoreCase)) continue; // X: is Windows PE itself
            if (File.Exists(Path.Combine(root, @"Windows\System32\config\SYSTEM"))) candidates.Add((d, p, root));
        }
        if (candidates.Count == 0) { Console.WriteLine("  No installed Windows found (encrypted drives must be unlocked first: manage-bde -unlock)."); return; }
        for (int i = 0; i < candidates.Count; i++)
            Console.WriteLine($"  {i + 1}  {candidates[i].Root}Windows  (disk {candidates[i].Disk.Number}, {candidates[i].Disk.Style})");
        if (!int.TryParse(Ask("Which Windows: "), out var k) || k < 1 || k > candidates.Count) return;
        var (disk, _, winRoot) = candidates[k - 1];
        Console.WriteLine($"  This runs Microsoft's bcdboot from {winRoot}Windows onto the system partition of disk {disk.Number}.");
        Console.WriteLine("  The existing boot store is copied first to " + Path.Combine(winRoot, "Lasteast"));
        if (Ask("Type APPLY to continue: ") != "APPLY") return;
        new BootPreparer().Prepare(disk, new Lasteast.Core.Operations.PrepareBootOp(disk.Number, FixDriveLetter: false, WindowsDirectoryOverride: winRoot + "Windows"),
            backupFolder: Path.Combine(winRoot, "Lasteast", $"boot-backup-{DateTime.Now:yyyyMMdd-HHmmss}"));
        Console.WriteLine("  Boot files rebuilt. Remove the rescue media and restart.");
    }

    private sealed class SyncProgress(Action<RecoveryScanProgress> a) : IProgress<RecoveryScanProgress> { public void Report(RecoveryScanProgress v) => a(v); }
    private sealed class SyncBackupProgress(Action<BackupProgress> a) : IProgress<BackupProgress> { public void Report(BackupProgress v) => a(v); }
}
