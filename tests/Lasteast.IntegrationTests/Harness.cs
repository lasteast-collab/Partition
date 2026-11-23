using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using Lasteast.Core.Models;
using Lasteast.Storage.Windows.Detection;

namespace Lasteast.IntegrationTests;

[AttributeUsage(AttributeTargets.Method)]
public sealed class TestAttribute : Attribute { }

public sealed class AssertionException(string message) : Exception(message);

public static class Assert
{
    public static void True(bool condition, string? message = null, [CallerArgumentExpression(nameof(condition))] string? expr = null)
    {
        if (!condition) throw new AssertionException(message ?? $"Expected true: {expr}");
    }

    public static void Equal<T>(T expected, T actual, string? message = null, [CallerArgumentExpression(nameof(actual))] string? expr = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new AssertionException($"{message ?? expr}: expected <{expected}> but was <{actual}>");
    }
}

/// <summary>
/// Creates, attaches and removes VHDX test disks with diskpart (built into Windows).
/// Every disk lives in a dedicated temp folder and is identified by a unique size, so tests can never
/// select a physical disk by mistake.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class VirtualDisk : IDisposable
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "lasteast-itest");

    public string File { get; }
    public int SizeMb { get; }
    public int Number { get; private set; } = -1;

    private VirtualDisk(string file, int sizeMb)
    {
        File = file;
        SizeMb = sizeMb;
    }

    /// <summary>Creates and attaches an expandable VHDX, then runs <paramref name="setup"/> diskpart lines against it.</summary>
    public static async Task<VirtualDisk> CreateAsync(int sizeMb, params string[] setup)
    {
        Directory.CreateDirectory(Root);
        var file = Path.Combine(Root, $"it-{sizeMb}-{Guid.NewGuid():N}.vhdx");
        var vd = new VirtualDisk(file, sizeMb);
        DiskPart($"create vdisk file=\"{file}\" maximum={sizeMb} type=expandable", "attach vdisk");
        vd.Number = await FindDiskAsync(sizeMb);
        if (setup.Length > 0) DiskPart(new[] { $"select disk {vd.Number}" }.Concat(setup).ToArray());
        await Task.Delay(1500); // let volumes arrive
        return vd;
    }

    private static async Task<int> FindDiskAsync(int sizeMb)
    {
        for (int i = 0; i < 20; i++)
        {
            var inv = await new WindowsStorageInventoryProvider().GetInventoryAsync();
            var d = inv.Disks.FirstOrDefault(x => x.IsVirtual && x.SizeBytes == (ulong)sizeMb * 1024 * 1024);
            if (d is not null) return d.Number;
            await Task.Delay(500);
        }
        throw new InvalidOperationException($"Attached VHDX of {sizeMb} MB not found");
    }

    public async Task<DiskInfo> ReadAsync()
    {
        var inv = await new WindowsStorageInventoryProvider().GetInventoryAsync();
        var d = inv.Disks.First(x => x.Number == Number);
        if (!d.IsVirtual) throw new InvalidOperationException($"Disk {Number} is not virtual — refusing to continue");
        return d;
    }

    public static string DiskPart(params string[] lines)
    {
        var script = Path.GetTempFileName();
        System.IO.File.WriteAllLines(script, lines);
        try
        {
            var psi = new ProcessStartInfo("diskpart.exe", $"/s \"{script}\"") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode != 0) throw new InvalidOperationException($"diskpart failed ({p.ExitCode}):\n{string.Join("\n", lines)}\n{output}");
            return output;
        }
        finally { System.IO.File.Delete(script); }
    }

    public void Dispose()
    {
        try { DiskPart($"select vdisk file=\"{File}\"", "detach vdisk"); } catch { }
        try { System.IO.File.Delete(File); } catch { }
    }
}

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (!OperatingSystem.IsWindows() || !new WindowsPrivilegeService().IsElevated)
        {
            Console.WriteLine("SKIPPED: integration tests need Windows and administrator rights (they create VHDX disks).");
            return 0;
        }

        var tests = typeof(Program).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static).Select(m => (t, m)))
            .Where(x => x.m.GetCustomAttribute<TestAttribute>() is not null)
            .Where(x => args.Length == 0 || args.Any(a => $"{x.t.Name}.{x.m.Name}".Contains(a, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(x => x.t.Name).ThenBy(x => x.m.Name).ToList();

        int failed = 0;
        foreach (var (type, method) in tests)
        {
            var name = $"{type.Name}.{method.Name}";
            var sw = Stopwatch.StartNew();
            try
            {
                if (method.Invoke(null, null) is Task t) await t;
                Console.WriteLine($"  PASS  {name} ({sw.Elapsed.TotalSeconds:0.0}s)");
            }
            catch (Exception ex)
            {
                failed++;
                var inner = ex is TargetInvocationException { InnerException: { } ie } ? ie : ex;
                Console.WriteLine($"  FAIL  {name}\n        {inner.GetType().Name}: {inner.Message}\n{inner.StackTrace}");
            }
        }
        Console.WriteLine($"\n{tests.Count - failed}/{tests.Count} integration tests passed");
        return failed == 0 ? 0 : 1;
    }
}
