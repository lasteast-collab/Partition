using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Lasteast.Core.Operations;
using Lasteast.Core.Optimization;

namespace Lasteast.Storage.Windows.Operations;

/// <summary>
/// Windows' own optimisation through WMI: Win32_Volume.DefragAnalysis for fragmentation figures (locale-independent),
/// MSFT_Volume.Optimize for defragmentation (HDD) or re-TRIM (SSD/NVMe). The planner decides which; this class never
/// defragments a volume it was asked to TRIM.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsVolumeOptimizer : IVolumeOptimizer
{
    private static dynamic Connect(string ns)
    {
        var t = Type.GetTypeFromProgID("WbemScripting.SWbemLocator", throwOnError: true)!;
        dynamic locator = Activator.CreateInstance(t)!;
        return locator.ConnectServer(".", ns);
    }

    public Task<FragmentationReport?> AnalyzeAsync(char letter, CancellationToken ct = default) => Task.Run<FragmentationReport?>(() =>
    {
        try
        {
            dynamic svc = Connect(@"root\cimv2");
            dynamic vol = StorageManagementExecutor.QueryFirst(svc, $"SELECT * FROM Win32_Volume WHERE DriveLetter = '{char.ToUpperInvariant(letter)}:'");
            if (vol is null) return null;
            dynamic outp = vol.ExecMethod_("DefragAnalysis");
            if ((uint)outp.ReturnValue != 0) return null;
            dynamic a = outp.DefragAnalysis;
            return new FragmentationReport((int)(uint)a.TotalPercentFragmentation, (int)(uint)a.FilePercentFragmentation, (bool)outp.DefragRecommended);
        }
        catch (COMException) { return null; }
        catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { return null; }
    }, ct);

    public Task<string> OptimizeAsync(char letter, OptimizeAction action, CancellationToken ct = default) => Task.Run(() =>
    {
        if (action == OptimizeAction.None) return "";
        dynamic svc = Connect(@"root\Microsoft\Windows\Storage");
        dynamic vol = StorageManagementExecutor.QueryFirst(svc, $"SELECT * FROM MSFT_Volume WHERE DriveLetter = '{char.ToUpperInvariant(letter)}'")
                      ?? throw new OperationFailedException("Op.Error.NotFound");
        var args = action == OptimizeAction.Retrim
            ? new Dictionary<string, object?> { ["ReTrim"] = true }
            : new Dictionary<string, object?> { ["Defrag"] = true };
        StorageManagementExecutor.Invoke(vol, "Optimize", args);
        return action == OptimizeAction.Retrim ? "TRIM sent" : "Defragmented";
    }, ct);
}
