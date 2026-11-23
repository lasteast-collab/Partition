using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Lasteast.Core.Abstractions;
using Lasteast.Core.Boot;
using Lasteast.Core.Logging;
using Lasteast.Core.Operations;
using Microsoft.Win32;

namespace Lasteast.Storage.Windows.Operations;

[SupportedOSPlatform("windows")]
internal static class Proc
{
    public static (int Code, string Output) Run(string exe, string args, int timeoutMs = 300_000, IProgress<string>? log = null)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        using var p = Process.Start(psi)!;
        var sb = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data is null) return; lock (sb) sb.AppendLine(e.Data); log?.Report(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is null) return; lock (sb) sb.AppendLine(e.Data); log?.Report(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch { } throw new TimeoutException($"{exe} {args}"); }
        p.WaitForExit();
        return (p.ExitCode, sb.ToString());
    }

    public static string System32(string exe) => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), exe);
}

/// <summary>bcdedit and the EFI system partition of the running Windows.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsBootTools : IBootTools
{
    private readonly IAppLog _log;
    public WindowsBootTools(IAppLog? log = null) => _log = log ?? NullLog.Instance;

    public Task<IReadOnlyList<BcdEntry>> EnumerateBcdAsync() => Task.Run<IReadOnlyList<BcdEntry>>(() =>
    {
        var (code, output) = Proc.Run(Proc.System32("bcdedit.exe"), "/enum all /v");
        if (code != 0) throw new OperationFailedException("Boot.Error.Bcdedit", output.Trim());
        return BcdParser.Parse(output);
    });

    public Task<string> ExportBcdAsync(string folder) => Task.Run(() =>
    {
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, $"BCD-{DateTime.Now:yyyyMMdd-HHmmss}.bcd");
        var (code, output) = Proc.Run(Proc.System32("bcdedit.exe"), $"/export \"{file}\"");
        if (code != 0 || !File.Exists(file)) throw new OperationFailedException("Boot.Error.Bcdedit", output.Trim());
        _log.Info($"BCD exported to {file}");
        return file;
    });

    [DllImport("kernel32.dll", EntryPoint = "SetVolumeMountPointW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetVolumeMountPoint(string mountPoint, string volumeName);
    [DllImport("kernel32.dll", EntryPoint = "DeleteVolumeMountPointW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool DeleteVolumeMountPoint(string mountPoint);

    /// <summary>The ESP's volume name, from the firmware's record of where Windows booted from.</summary>
    public static string? SystemPartitionVolume()
    {
        using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\Setup");
        var dev = k?.GetValue("SystemPartition") as string; // \Device\HarddiskVolumeN
        if (dev is null) return null;
        // map the NT device name to a volume GUID path
        foreach (var v in EnumerateVolumes())
        {
            var target = new char[1024];
            var name = v.TrimEnd('\\')[4..]; // "Volume{...}"
            if (QueryDosDevice(name, target, target.Length) > 0 && new string(target).TrimEnd('\0').Split('\0')[0].Equals(dev, StringComparison.OrdinalIgnoreCase))
                return v;
        }
        return null;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern int QueryDosDevice(string name, char[] target, int max);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr FindFirstVolume(StringBuilder name, int len);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool FindNextVolume(IntPtr h, StringBuilder name, int len);
    [DllImport("kernel32.dll")] private static extern bool FindVolumeClose(IntPtr h);

    private static IEnumerable<string> EnumerateVolumes()
    {
        var sb = new StringBuilder(260);
        var h = FindFirstVolume(sb, sb.Capacity);
        if (h == new IntPtr(-1)) yield break;
        try
        {
            do { yield return sb.ToString(); } while (FindNextVolume(h, sb, sb.Capacity));
        }
        finally { FindVolumeClose(h); }
    }

    private static T WithEsp<T>(Func<string, T> body)
    {
        var vol = SystemPartitionVolume() ?? throw new OperationFailedException("Boot.Error.NoEsp", "system partition not found");
        var used = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
        char letter = Enumerable.Range('H', 19).Select(c => (char)c).Reverse().First(c => !used.Contains(c));
        var mp = $"{letter}:\\";
        if (!SetVolumeMountPoint(mp, vol)) throw new OperationFailedException("Boot.Error.Mount", $"SetVolumeMountPoint {Marshal.GetLastWin32Error()}");
        try { return body(mp); }
        finally { DeleteVolumeMountPoint(mp); }
    }

    public Task<IReadOnlyList<(string Path, long Size)>> ListEspAsync() => Task.Run<IReadOnlyList<(string, long)>>(() => WithEsp(root =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Select(f => (Path.GetRelativePath(root, f), new FileInfo(f).Length)).ToList()));

    public Task<int> BackupEspAsync(string folder) => Task.Run(() => WithEsp(root =>
    {
        int n = 0;
        foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(folder, Path.GetRelativePath(root, f));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(f, target, overwrite: false);
            n++;
        }
        _log.Info($"ESP backed up to {folder}: {n} files");
        return n;
    }));

    private string Bcdedit(string args)
    {
        var backup = ExportBcdAsync(Lasteast.Core.AppPaths.Bcd).GetAwaiter().GetResult();
        var (code, output) = Proc.Run(Proc.System32("bcdedit.exe"), args);
        _log.Info($"bcdedit {args}: exit {code} {output.Trim()} (backup {backup})");
        if (code != 0) throw new OperationFailedException("Boot.Error.Bcdedit", output.Trim());
        return backup;
    }

    private static string Id(string id) => id.StartsWith('{') && id.EndsWith('}') && id.IndexOfAny(new[] { '"', ' ', '&', '|' }) < 0 ? id
        : throw new OperationFailedException("Boot.Error.Id", id);

    public Task<string> SetDefaultAsync(string identifier) => Task.Run(() => Bcdedit($"/default {Id(identifier)}"));
    public Task<string> SetTimeoutAsync(int seconds) => Task.Run(() => Bcdedit($"/timeout {Math.Clamp(seconds, 0, 999)}"));
    public Task<string> SetDisplayOrderAsync(IReadOnlyList<string> identifiers) => Task.Run(() => Bcdedit("/displayorder " + string.Join(" ", identifiers.Select(Id))));
    public Task<string> SetDescriptionAsync(string identifier, string description) => Task.Run(() =>
    {
        var d = description.Replace("\"", "").Trim();
        if (d.Length is 0 or > 120) throw new OperationFailedException("Boot.Error.Description", description);
        return Bcdedit($"/set {Id(identifier)} description \"{d}\"");
    });

    public Task<int> RestoreEspAsync(string backupFolder) => Task.Run(() =>
    {
        if (!Directory.Exists(backupFolder)) throw new OperationFailedException("Boot.Error.EspBackup", backupFolder);
        var check = EspAnalyzer.Check(Directory.EnumerateFiles(backupFolder, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(backupFolder, f)));
        if (check.Missing.Count > 0) throw new OperationFailedException("Boot.Error.EspBackup", string.Join(", ", check.Missing));
        // the current ESP is kept first, so the restore itself can be undone
        var safety = Path.Combine(Lasteast.Core.AppPaths.Backups, $"esp-before-restore-{DateTime.Now:yyyyMMdd-HHmmss}");
        BackupEspAsync(safety).GetAwaiter().GetResult();
        return WithEsp(root =>
        {
            int n = 0;
            foreach (var f in Directory.EnumerateFiles(backupFolder, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(root, Path.GetRelativePath(backupFolder, f));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(f, target, overwrite: true);
                n++;
            }
            _log.Info($"ESP restored from {backupFolder}: {n} files (previous ESP saved in {safety})");
            return n;
        });
    });

    public Task<WinReStatus> WinReAsync() => Task.Run(() =>
    {
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "Recovery", "ReAgent.xml");
            if (!File.Exists(path)) return new WinReStatus(null, null);
            var x = System.Xml.Linq.XDocument.Load(path);
            var id = x.Descendants("WinreBCD").FirstOrDefault()?.Attribute("id")?.Value;
            bool enabled = id is not null && Guid.TryParse(id.Trim('{', '}'), out var g) && g != Guid.Empty;
            var loc = x.Descendants("WinreLocation").FirstOrDefault()?.Attribute("path")?.Value;
            return new WinReStatus(enabled, loc);
        }
        catch (Exception) { return new WinReStatus(null, null); }
    });

    public Task<string> SetWinReAsync(bool enable) => Task.Run(() =>
    {
        var (code, output) = Proc.Run(Proc.System32("reagentc.exe"), enable ? "/enable" : "/disable");
        _log.Info($"reagentc {(enable ? "/enable" : "/disable")}: exit {code} {output.Trim()}");
        if (code != 0) throw new OperationFailedException("Boot.Error.Reagentc", output.Trim());
        return output.Trim();
    });

    /// <summary>Executes <see cref="RepairBootOp"/>: export BCD, then bcdboot from the running Windows.</summary>
    public void Repair(string backupFolder)
    {
        ExportBcdAsync(backupFolder).GetAwaiter().GetResult();
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var (code, output) = Proc.Run(Proc.System32("bcdboot.exe"), $"\"{win}\"");
        _log.Info($"bcdboot {win}: exit {code} {output}");
        if (code != 0) throw new OperationFailedException("Boot.Error.Bcdboot", output.Trim());
    }
}

/// <summary>
/// Builds the Lasteast Rescue Boot ISO with Microsoft's own tools from the Windows ADK and its WinPE add-on, which the
/// user installs and licenses from Microsoft. Lasteast never redistributes Windows PE.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WinPeRescueMediaBuilder : IRescueMediaBuilder
{
    private readonly string _payload;
    public WinPeRescueMediaBuilder(string? rescuePayload = null) =>
        _payload = rescuePayload ?? Path.Combine(AppContext.BaseDirectory, "rescue");

    public AdkStatus Detect()
    {
        using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows Kits\Installed Roots") ??
                      Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Microsoft\Windows Kits\Installed Roots");
        var root = k?.GetValue("KitsRoot10") as string;
        var adk = root is not null && File.Exists(Path.Combine(root, @"Assessment and Deployment Kit\Deployment Tools\DandISetEnv.bat"));
        var pe = root is not null && Directory.Exists(Path.Combine(root, @"Assessment and Deployment Kit\Windows Preinstallation Environment\amd64"));
        return new(adk, pe, root, File.Exists(Path.Combine(_payload, "lasteast.exe")) ? _payload : null);
    }

    public Task<string> BuildIsoAsync(string isoPath, IProgress<string> log, CancellationToken ct) => Task.Run(() =>
    {
        var st = Detect();
        if (!st.AdkInstalled || !st.WinPeInstalled) throw new OperationFailedException("Rescue.Error.NoAdk");
        if (st.RescuePayload is null) throw new OperationFailedException("Rescue.Error.NoPayload", _payload);
        var adk = Path.Combine(st.KitsRoot!, "Assessment and Deployment Kit");
        var env = Path.Combine(adk, @"Deployment Tools\DandISetEnv.bat");
        var pe = Path.Combine(adk, @"Windows Preinstallation Environment");
        var work = Path.Combine(Path.GetTempPath(), "LasteastRescue-" + Guid.NewGuid().ToString("N")[..8]);
        var mount = Path.Combine(work, "mount");
        var ocs = Path.Combine(pe, @"amd64\WinPE_OCs");
        string Cmd(string inner) => $"/c call \"{env}\" >nul && {inner}";
        void Step(string what, string exe, string args)
        {
            ct.ThrowIfCancellationRequested();
            log.Report("» " + what);
            var (code, output) = Proc.Run(exe, args, 1_800_000, log);
            if (code != 0) throw new OperationFailedException("Rescue.Error.Step", $"{what}: exit {code}\n{output}");
        }
        var dism = Proc.System32("dism.exe");
        var cmd = Proc.System32("cmd.exe");
        bool mounted = false;
        try
        {
            Step("copype", cmd, Cmd($"copype amd64 \"{work}\""));
            Step("Mount boot.wim", dism, $"/Mount-Image /ImageFile:\"{Path.Combine(work, @"media\sources\boot.wim")}\" /Index:1 /MountDir:\"{mount}\"");
            mounted = true;
            foreach (var oc in new[] { "WinPE-WMI", "WinPE-StorageWMI", "WinPE-Scripting", "WinPE-SecureStartup", "WinPE-FMAPI" })
            {
                var cab = Path.Combine(ocs, oc + ".cab");
                if (File.Exists(cab)) Step("Add " + oc, dism, $"/Image:\"{mount}\" /Add-Package /PackagePath:\"{cab}\"");
                else log.Report($"  ({oc} not found in the ADK; skipped)");
            }
            log.Report("» Copy Lasteast rescue tools");
            var target = Path.Combine(mount, "Lasteast");
            foreach (var f in Directory.EnumerateFiles(st.RescuePayload, "*", SearchOption.AllDirectories))
            {
                var t = Path.Combine(target, Path.GetRelativePath(st.RescuePayload, f));
                Directory.CreateDirectory(Path.GetDirectoryName(t)!);
                File.Copy(f, t, true);
            }
            File.WriteAllText(Path.Combine(mount, @"Windows\System32\startnet.cmd"),
                "@echo off\r\nwpeinit\r\ntitle Lasteast Rescue Boot\r\nX:\\Lasteast\\lasteast.exe rescue\r\ncmd /k echo Type X:\\Lasteast\\lasteast.exe rescue to open the menu again.\r\n");
            Step("Commit image", dism, $"/Unmount-Image /MountDir:\"{mount}\" /Commit");
            mounted = false;
            if (File.Exists(isoPath)) File.Delete(isoPath);
            Step("MakeWinPEMedia /ISO", cmd, Cmd($"MakeWinPEMedia /ISO \"{work}\" \"{isoPath}\""));
            log.Report("✓ " + isoPath);
            return isoPath;
        }
        finally
        {
            if (mounted) Proc.Run(dism, $"/Unmount-Image /MountDir:\"{mount}\" /Discard");
            try { Directory.Delete(work, true); } catch { }
        }
    }, ct);
}
