using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Lasteast.Core.Formatting;
using Lasteast.Core.Layout;
using Lasteast.Core.Models;
using Lasteast.Storage.Windows.Detection;

// Lasteast command line (Phase 1: read-only inventory).
//   lasteast inventory            human-readable disk/partition listing
//   lasteast inventory --json     machine-readable JSON
// Exit codes: 0 ok, 1 error, 2 usage, 3 unsupported OS.

if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
{
    Console.WriteLine("Usage: lasteast inventory [--json] [--show-serials]");
    Console.WriteLine("Read-only: this build never modifies any disk.");
    return args.Length == 0 ? 2 : 0;
}

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("Lasteast requires Windows 10 or 11.");
    return 3;
}

if (args[0] != "inventory")
{
    Console.Error.WriteLine($"Unknown command '{args[0]}'. Try --help.");
    return 2;
}

bool json = args.Contains("--json");
bool showSerials = args.Contains("--show-serials");

try
{
    var privileges = new WindowsPrivilegeService();
    var inv = await new WindowsStorageInventoryProvider(privileges: privileges).GetInventoryAsync();
    if (!showSerials)
        inv = inv with { Disks = inv.Disks.Select(d => d with { SerialNumber = Mask(d.SerialNumber) }).ToList() };

    if (json)
    {
        var options = new JsonSerializerOptions { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
        Console.WriteLine(JsonSerializer.Serialize(inv, options));
        return 0;
    }

    var c = CultureInfo.CurrentCulture;
    Console.WriteLine($"Lasteast Partition — inventory  ({inv.CapturedAt:G})  firmware={inv.Firmware}  elevated={inv.IsElevated}");
    foreach (var d in inv.Disks)
    {
        Console.WriteLine();
        Console.WriteLine($"Disk {d.Number}: {d.Model}  {ByteSize.Format(d.SizeBytes, c)}  {d.Style} {d.Media} {d.Bus}" +
                          $"{(d.IsWindowsDisk ? "  [Windows]" : "")}{(d.IsBootDisk ? "  [Boot]" : "")}  serial={d.SerialNumber ?? "-"}");
        foreach (var s in DiskMapLayout.BuildSegments(d, out var problems))
        {
            if (s.Partition is not { } p)
            {
                Console.WriteLine($"   {"-",-4} {"(unallocated)",-28} {ByteSize.Format(s.SizeBytes, c),14}  @ {s.OffsetBytes}");
                continue;
            }
            var v = p.Volume;
            var name = v?.DriveLetter is { } l ? $"{l}:" : "*:";
            Console.WriteLine($"   #{p.PartitionNumber,-3} {name + " " + (v?.Label ?? p.GptName ?? ""),-28} {ByteSize.Format(p.SizeBytes, c),14}" +
                              $"  {v?.FileSystem ?? "-",-6} {p.Role,-18} {p.Alignment,-16} @ {p.OffsetBytes}" +
                              $"{(v?.FreeBytes is { } f ? $"  free {ByteSize.Format(f, c)}" : "")}");
        }
        foreach (var n in d.Notes) Console.WriteLine($"   note: {n}");
    }
    foreach (var n in inv.Notes) Console.WriteLine($"note: {n}");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine("Error: " + ex.Message);
    return 1;
}

static string? Mask(string? s) => string.IsNullOrEmpty(s) || s.Length <= 4 ? s : new string('*', s.Length - 4) + s[^4..];
