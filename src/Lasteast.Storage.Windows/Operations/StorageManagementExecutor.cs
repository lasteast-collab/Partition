using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Lasteast.Core.Models;
using Lasteast.Core.Operations;

namespace Lasteast.Storage.Windows.Operations;

/// <summary>
/// Executes single operations through the Windows Storage Management API
/// (root\Microsoft\Windows\Storage: MSFT_Disk / MSFT_Partition / MSFT_Volume) — the supported,
/// transactional-per-call API behind Disk Management and the Storage PowerShell module — and native
/// Win32 calls for drive letters and labels. No raw sector writes and no PowerShell.
///
/// COM late binding (WbemScripting.SWbemLocator) keeps the project free of NuGet dependencies.
/// Only <see cref="GuardedExecutionEngine"/> may call this class.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class StorageManagementExecutor : IOperationExecutor
{
    private const string Namespace = @"root\Microsoft\Windows\Storage";

    public Task ExecuteAsync(DiskOperation operation, DiskInfo liveDisk, CancellationToken cancellationToken)
        => Task.Run(() => Execute(operation, liveDisk));

    /// <summary>Runs one step of a WMI conversation; any failure is reported with the step name and HRESULT.</summary>
    private static T Step<T>(string label, Func<T> f)
    {
        try { return f(); }
        catch (OperationFailedException) { throw; }
        catch (Exception ex)
        {
            throw new OperationFailedException("Exec.Error.Wmi", $"{label}: {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");
        }
    }

    private static void Step(string label, Action a) => Step<object?>(label, () => { a(); return null; });

    private void Execute(DiskOperation op, DiskInfo disk)
    {
        dynamic svc = Connect();
        try
        {
            switch (op)
            {
                case CreatePartitionOp c: Create(svc, disk, c); break;
                case DeletePartitionOp d: Invoke(PartitionAt(svc, disk, d.OffsetBytes), "DeleteObject", NoArgs()); break;
                case FormatPartitionOp f: Format(svc, VolumePathOf(disk, f.OffsetBytes), f.FileSystem, f.Label, f.ClusterSizeBytes); break;
                case ResizePartitionOp r: Resize(svc, disk, r); break;
                case SetLabelOp l: SetLabel(VolumePathOf(disk, l.OffsetBytes), l.Label); break;
                case SetDriveLetterOp s: SetLetter(disk, s); break;
                case InitializeDiskOp i:
                {
                    dynamic d = QueryFirst(svc, $"SELECT * FROM MSFT_Disk WHERE Number = {disk.Number}")
                                ?? throw new OperationFailedException("Exec.Error.DiskMissing", null, disk.Number);
                    Invoke(d, "Initialize", new Dictionary<string, object?> { ["PartitionStyle"] = i.Style == PartitionStyle.Gpt ? 2 : 1 });
                    break;
                }
                default: throw new OperationFailedException("Exec.Error.Step", "unsupported operation " + op.GetType().Name);
            }
        }
        finally
        {
            Marshal.FinalReleaseComObject(svc);
        }
    }

    // ------------------------------------------------------------------ operations

    private static void Create(dynamic svc, DiskInfo disk, CreatePartitionOp c)
    {
        dynamic d = QueryFirst(svc, $"SELECT * FROM MSFT_Disk WHERE Number = {disk.Number}")
                    ?? throw new OperationFailedException("Exec.Error.DiskMissing", null, disk.Number);
        var args = new Dictionary<string, object?>
        {
            ["Size"] = c.SizeBytes.ToString(),
            ["Offset"] = c.OffsetBytes.ToString(),
            ["AssignDriveLetter"] = false,
        };
        if (disk.Style == PartitionStyle.Gpt) args["GptType"] = "{" + PartitionTypes.GptBasicData + "}";
        else args["MbrType"] = (int)FileSystems.MbrTypeFor(c.FileSystem);
        Invoke(d, "CreatePartition", args);

        // Windows assigns the volume asynchronously; wait for it, then format and mount.
        string? volume = null;
        for (int i = 0; i < 40 && volume is null; i++)
        {
            dynamic? p = QueryFirst(svc, $"SELECT * FROM MSFT_Partition WHERE DiskNumber = {disk.Number} AND Offset = {c.OffsetBytes}");
            if (p is not null) volume = FirstVolumePath(p);
            if (volume is null) Thread.Sleep(250);
        }
        if (volume is null) throw new OperationFailedException("Exec.Error.Step", "the new partition did not get a volume");

        Format(svc, volume, c.FileSystem, c.Label, null);
        if (c.DriveLetter is { } letter) Mount(char.ToUpperInvariant(letter), volume);
    }

    private static void Format(dynamic svc, string volumePath, string fs, string? label, uint? cluster)
    {
        dynamic v = QueryFirst(svc, $"SELECT * FROM MSFT_Volume WHERE Path = '{Wql(volumePath)}'")
                    ?? throw new OperationFailedException("Exec.Error.Step", "volume not found: " + volumePath);
        var args = new Dictionary<string, object?>
        {
            ["FileSystem"] = fs,
            ["FileSystemLabel"] = label ?? "",
            ["Full"] = false,
            ["Force"] = false, // never format a volume that is in use; the user must close programs first
        };
        if (cluster is { } cs) args["AllocationUnitSize"] = (int)cs;
        Invoke(v, "Format", args);
    }

    private static void Resize(dynamic svc, DiskInfo disk, ResizePartitionOp r)
    {
        dynamic p = PartitionAt(svc, disk, r.OffsetBytes);
        // Ask Windows for the real limits (unmovable files, page file, shadow copies …) before changing anything.
        dynamic outp = Invoke(p, "GetSupportedSize", NoArgs());
        ulong min = Convert.ToUInt64(outp.Properties_.Item("SizeMin").Value);
        ulong max = Convert.ToUInt64(outp.Properties_.Item("SizeMax").Value);
        if (r.NewSizeBytes < min || r.NewSizeBytes > max)
            throw new OperationFailedException("Exec.Error.SupportedRange", $"{min}..{max}", min, max);
        Invoke(p, "Resize", new Dictionary<string, object?> { ["Size"] = r.NewSizeBytes.ToString() });
    }

    private static void SetLabel(string volumePath, string label)
    {
        if (!SetVolumeLabel(volumePath, label)) throw Win32("SetVolumeLabel");
    }

    private static void SetLetter(DiskInfo disk, SetDriveLetterOp s)
    {
        var part = disk.Partitions.First(p => p.OffsetBytes == s.OffsetBytes);
        var volume = part.Volume?.VolumeGuidPath ?? throw new OperationFailedException("Op.Error.NoVolume");
        if (part.Volume.DriveLetter is { } old && !DeleteVolumeMountPoint($"{old}:\\")) throw Win32("DeleteVolumeMountPoint");
        if (s.Letter is { } l) Mount(char.ToUpperInvariant(l), volume);
    }

    private static void Mount(char letter, string volumeGuidPath)
    {
        if (!SetVolumeMountPoint($"{letter}:\\", volumeGuidPath.EndsWith('\\') ? volumeGuidPath : volumeGuidPath + "\\"))
            throw Win32("SetVolumeMountPoint");
    }

    // ------------------------------------------------------------------ WMI helpers

    private static dynamic Connect()
    {
        var t = Type.GetTypeFromProgID("WbemScripting.SWbemLocator", throwOnError: true)!;
        dynamic locator = Activator.CreateInstance(t)!;
        try { return locator.ConnectServer(".", Namespace); }
        catch (COMException ex) { throw new OperationFailedException("Exec.Error.Wmi", ex.Message); }
    }

    private static dynamic? QueryFirst(dynamic svc, string wql) => Step<dynamic?>("query " + wql, () =>
    {
        dynamic set = svc.ExecQuery(wql);
        return (int)set.Count > 0 ? set.ItemIndex(0) : null;
    });

    private static dynamic PartitionAt(dynamic svc, DiskInfo disk, ulong offset)
        => QueryFirst(svc, $"SELECT * FROM MSFT_Partition WHERE DiskNumber = {disk.Number} AND Offset = {offset}")
           ?? throw new OperationFailedException("Op.Error.NotFound");

    private static string VolumePathOf(DiskInfo disk, ulong offset)
    {
        var v = disk.Partitions.FirstOrDefault(p => p.OffsetBytes == offset)?.Volume;
        if (v is null || v.VolumeGuidPath.StartsWith("pending:", StringComparison.Ordinal)) throw new OperationFailedException("Op.Error.NoVolume");
        return v.VolumeGuidPath;
    }

    private static string? FirstVolumePath(dynamic partition)
    {
        object? paths = Step<object?>("AccessPaths", () => (object?)partition.Properties_.Item("AccessPaths").Value);
        if (paths is not object[] arr) return null;
        return arr.OfType<string>().FirstOrDefault(s => s.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase));
    }

    private static Dictionary<string, object?> NoArgs() => new();

    private static string Wql(string s) => s.Replace("\\", "\\\\").Replace("'", "\\'");

    /// <summary>Calls a WMI method; throws with Windows' own extended error message on a non-zero return.</summary>
    private static dynamic Invoke(dynamic obj, string method, Dictionary<string, object?> args)
    {
        dynamic? inParams = null;
        dynamic def = Step<dynamic>($"{method}: method definition", () => obj.Methods_.Item(method));
        dynamic? inDef = Step<dynamic?>($"{method}: in-parameters", () => def.InParameters);
        if (inDef is not null)
        {
            inParams = Step<dynamic>($"{method}: spawn", () => inDef.SpawnInstance_());
            foreach (var (k, v) in args)
                if (v is not null)
                {
                    var key = k; var value = v;
                    Step($"{method}: set {key}={value}", () => { inParams.Properties_.Item(key).Value = value; });
                }
        }
        dynamic outParams = Step<dynamic>($"{method}: execute", () => inParams is null ? obj.ExecMethod_(method) : obj.ExecMethod_(method, inParams));

        uint rv = Step($"{method}: return value", () => Convert.ToUInt32(outParams.Properties_.Item("ReturnValue").Value));
        if (rv != 0)
        {
            string? message = null;
            try
            {
                dynamic ext = outParams.Properties_.Item("ExtendedStatus").Value;
                if (ext is not null) message = (string?)ext.Properties_.Item("Message").Value;
            }
            catch { /* ExtendedStatus is optional */ }
            throw new OperationFailedException("Exec.Error.Wmi", $"{method} returned {rv}{(message is null ? "" : ": " + message)}");
        }
        return outParams;
    }

    // ------------------------------------------------------------------ Win32

    private static OperationFailedException Win32(string call)
    {
        var e = Marshal.GetLastWin32Error();
        return new OperationFailedException("Exec.Error.Step", $"{call}: {new System.ComponentModel.Win32Exception(e).Message} (0x{e:X8})");
    }

    [DllImport("kernel32.dll", EntryPoint = "SetVolumeLabelW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetVolumeLabel(string rootPathName, string volumeName);

    [DllImport("kernel32.dll", EntryPoint = "SetVolumeMountPointW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetVolumeMountPoint(string volumeMountPoint, string volumeName);

    [DllImport("kernel32.dll", EntryPoint = "DeleteVolumeMountPointW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteVolumeMountPoint(string volumeMountPoint);
}
