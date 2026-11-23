using System.Collections.ObjectModel;
using System.Globalization;
using Lasteast.Core.Layout;
using Lasteast.Core.Models;
using Lasteast.Presentation.Mvvm;
using static Lasteast.Presentation.ViewModels.Text;

namespace Lasteast.Presentation.ViewModels;

public sealed record PropertyRow(string Label, string Value, bool Emphasize = false);

public sealed record Badge(string Text, string Kind);

/// <summary>One block on a graphical disk map.</summary>
public sealed class SegmentViewModel : ObservableObject
{
    private bool _isSelected;

    public SegmentViewModel(DiskViewModel disk, DiskSegment segment)
    {
        Disk = disk;
        Segment = segment;
        var p = segment.Partition;

        if (p is null)
        {
            Title = L("Seg.Unallocated");
            Subtitle = "";
            FileSystemText = L("Seg.Unallocated");
            ColorKey = "Unallocated";
            UsedFraction = 0;
        }
        else
        {
            Title = PartitionTitle(p);
            Subtitle = p.Volume?.Label ?? p.GptName ?? "";
            FileSystemText = FileSystem(p);
            ColorKey = ColorFor(p);
            UsedFraction = p.Volume is { CapacityBytes: > 0, UsedBytes: { } used }
                ? Math.Clamp((double)used / p.Volume.CapacityBytes!.Value, 0, 1)
                : 0;
        }
        SizeText = Size(segment.SizeBytes);
        ToolTip = string.Join(Environment.NewLine, new[] { Title, Subtitle, FileSystemText, SizeText }.Where(s => s.Length > 0));
    }

    public DiskViewModel Disk { get; }
    public DiskSegment Segment { get; }
    public PartitionInfo? Partition => Segment.Partition;
    public bool IsUnallocated => Segment.Kind == SegmentKind.Unallocated;
    public ulong SizeBytes => Segment.SizeBytes;
    /// <summary>Layout weight for the proportional disk map (double for WPF binding).</summary>
    public double Weight => Segment.SizeBytes;
    public string Title { get; }
    public string Subtitle { get; }
    public string FileSystemText { get; }
    public string SizeText { get; }
    public string ColorKey { get; }
    public double UsedFraction { get; }
    public string ToolTip { get; }
    public bool IsCritical => Partition?.IsCritical ?? false;
    public bool IsPending => Partition?.IsPending ?? false;

    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }

    public static string ColorFor(PartitionInfo p) => p.Role switch
    {
        PartitionRole.EfiSystem => "EFI",
        PartitionRole.MicrosoftReserved => "MSR",
        PartitionRole.Recovery => "Recovery",
        PartitionRole.Linux or PartitionRole.LinuxSwap or PartitionRole.AppleHfs => "Foreign",
        PartitionRole.LdmData or PartitionRole.LdmMetadata or PartitionRole.StorageSpaces => "Managed",
        _ => p.Volume?.FileSystem?.ToUpperInvariant() switch
        {
            "NTFS" => "NTFS",
            "FAT32" or "FAT" or "FAT16" or "FAT12" => "FAT",
            "EXFAT" => "exFAT",
            "REFS" => "ReFS",
            _ => "Other",
        },
    };

    /// <summary>Property rows for the details panel.</summary>
    public IReadOnlyList<PropertyRow> BuildDetails(bool maskSerials)
    {
        var rows = new List<PropertyRow>
        {
            new(L("Prop.Disk"), $"{L("Common.Disk")} {Disk.Number} — {Disk.Model}"),
        };
        var p = Partition;
        if (p is null)
        {
            rows.Add(new(L("Prop.Type"), L("Seg.Unallocated"), true));
            rows.Add(new(L("Prop.Capacity"), SizeText, true));
            rows.Add(new(L("Prop.Offset"), OffsetText(Segment.OffsetBytes)));
            return rows;
        }

        var v = p.Volume;
        rows.Add(new(L("Prop.Partition"), $"{Title}  ({L("Prop.Number")} {p.PartitionNumber})", true));
        if (!string.IsNullOrEmpty(v?.Label)) rows.Add(new(L("Prop.Label"), v!.Label!));
        rows.Add(new(L("Prop.FileSystem"), FileSystemText));
        rows.Add(new(L("Prop.Capacity"), SizeText, true));
        if (v?.UsedBytes is { } used) rows.Add(new(L("Prop.Used"), $"{Size(used)}  ({Percent(UsedFraction)})"));
        if (v?.FreeBytes is { } free) rows.Add(new(L("Prop.Free"), Size(free)));
        if (v?.ClusterSizeBytes is { } cl) rows.Add(new(L("Prop.Cluster"), Size(cl)));
        rows.Add(new(L("Prop.Type"), Role(p.Role)));
        rows.Add(new(L("Prop.Style"), Style(p.Style)));
        if (p.GptType is { } gt) rows.Add(new(L("Prop.GptType"), gt.ToString()));
        if (p.GptId is { } gid) rows.Add(new(L("Prop.GptId"), gid.ToString()));
        if (p.MbrType is { } mt) rows.Add(new(L("Prop.MbrType"), $"0x{mt:X2}"));
        if (p.Style == PartitionStyle.Mbr) rows.Add(new(L("Prop.Active"), YesNo(p.MbrActive)));
        rows.Add(new(L("Prop.Offset"), OffsetText(p.OffsetBytes)));
        rows.Add(new(L("Prop.Alignment"), Alignment(p.Alignment)));
        rows.Add(new(L("Prop.Hidden"), YesNo(p.IsHidden)));
        if (v is not null)
        {
            if (v.MountPoints.Count > 0) rows.Add(new(L("Prop.MountPoints"), string.Join(", ", v.MountPoints)));
            rows.Add(new(L("Prop.VolumePath"), v.VolumeGuidPath));
            if (v.IsReadOnly) rows.Add(new(L("Prop.ReadOnly"), L("Common.Yes")));
            if (v.AccessError is not null) rows.Add(new(L("Prop.Status"), v.AccessError));
        }
        var status = new List<string>();
        if (v?.IsWindowsVolume == true) status.Add(L("Badge.Windows"));
        if (v?.IsSystemPartitionVolume == true) status.Add(L("Badge.Boot"));
        if (p.IsCritical) status.Add(L("Badge.Protected"));
        if (status.Count > 0) rows.Add(new(L("Prop.Flags"), string.Join(" · ", status), true));
        return rows;
    }

    private static string OffsetText(ulong offset) =>
        $"{offset.ToString("N0", CultureInfo.CurrentCulture)} B  ({Size(offset)})";
}

public sealed class DiskViewModel : ObservableObject
{
    public DiskViewModel(DiskInfo disk, bool maskSerials)
    {
        Disk = disk;
        Segments = new ObservableCollection<SegmentViewModel>(
            DiskMapLayout.BuildSegments(disk, out var problems).Select(s => new SegmentViewModel(this, s)));
        LayoutProblems = problems;

        Title = $"{L("Common.Disk")} {disk.Number}";
        SizeText = Size(disk.SizeBytes);
        Summary = string.Join("  ·  ", new[] { Style(disk.Style), Media(disk.Media), Bus(disk.Bus) });
        SerialText = MaskSerial(disk.SerialNumber, maskSerials);

        var badges = new List<Badge>();
        if (disk.IsWindowsDisk) badges.Add(new(L("Badge.Windows"), "Windows"));
        if (disk.IsBootDisk) badges.Add(new(L("Badge.Boot"), "Boot"));
        if (disk.IsExternal) badges.Add(new(L("Badge.Removable"), "Removable"));
        if (disk.IsVirtual) badges.Add(new(L("Badge.Virtual"), "Virtual"));
        if (disk.IsDynamic) badges.Add(new(L("Badge.Dynamic"), "Dynamic"));
        if (problems.Count > 0) badges.Add(new(L("Badge.LayoutProblem"), "Problem"));
        Badges = badges;

        ulong used = 0, free = 0, unalloc = 0;
        foreach (var s in Segments)
        {
            if (s.IsUnallocated) unalloc += s.SizeBytes;
            else if (s.Partition?.Volume is { UsedBytes: { } u, FreeBytes: { } f }) { used += u; free += f; }
        }
        UsedBytes = used;
        FreeBytes = free;
        UnallocatedBytes = unalloc;
        UsedText = Size(used);
        FreeText = Size(free);
        UnallocatedText = Size(unalloc);
        UsedFraction = disk.SizeBytes > 0 ? Math.Clamp((double)used / disk.SizeBytes, 0, 1) : 0;
    }

    public DiskInfo Disk { get; }
    public int Number => Disk.Number;
    public string Model => Disk.Model;
    public string Title { get; }
    public string SizeText { get; }
    public string Summary { get; }
    public string SerialText { get; }
    public string HealthText => Health(Disk.Health);
    public string HealthKind => Disk.Health.ToString();
    public IReadOnlyList<Badge> Badges { get; }
    public IReadOnlyList<string> LayoutProblems { get; }
    public IReadOnlyList<string> Notes => Disk.Notes;
    public bool HasNotes => Disk.Notes.Count > 0 || LayoutProblems.Count > 0;
    public ObservableCollection<SegmentViewModel> Segments { get; }
    public ulong UsedBytes { get; }
    public ulong FreeBytes { get; }
    public ulong UnallocatedBytes { get; }
    public string UsedText { get; }
    public string FreeText { get; }
    public string UnallocatedText { get; }
    public double UsedFraction { get; }
    public string MediaGlyph => Disk.Bus == BusKind.Usb ? "\uE88E" : Disk.Media == MediaKind.Ssd ? "\uEDA2" : "\uEDA2";

    public IReadOnlyList<PropertyRow> BuildProperties(bool maskSerials) => new List<PropertyRow>
    {
        new(L("Prop.Disk"), Title, true),
        new(L("Prop.Model"), Disk.Model),
        new(L("Prop.Serial"), MaskSerial(Disk.SerialNumber, maskSerials)),
        new(L("Prop.Firmware"), Disk.FirmwareRevision ?? L("Common.NotAvailable")),
        new(L("Prop.Capacity"), $"{SizeText}  ({Disk.SizeBytes.ToString("N0", CultureInfo.CurrentCulture)} B)", true),
        new(L("Prop.Style"), Style(Disk.Style)),
        new(L("Prop.Media"), Media(Disk.Media)),
        new(L("Prop.Bus"), Bus(Disk.Bus)),
        new(L("Prop.Sectors"), $"{Disk.BytesPerLogicalSector} / {Disk.BytesPerPhysicalSector} B"),
        new(L("Prop.Trim"), YesNo(Disk.TrimEnabled)),
        new(L("Prop.Removable"), YesNo(Disk.IsRemovable)),
        new(L("Prop.Partitions"), Disk.Partitions.Count(p => p.Role != PartitionRole.MbrExtended).ToString(CultureInfo.CurrentCulture)),
        new(L("Prop.Used"), UsedText),
        new(L("Prop.Free"), FreeText),
        new(L("Prop.Unallocated"), UnallocatedText),
        new(L("Prop.Health"), HealthText),
        new(L("Prop.DiskId"), Disk.GptDiskId?.ToString() ?? (Disk.MbrSignature is { } s ? $"0x{s:X8}" : L("Common.NotAvailable"))),
        new(L("Prop.DevicePath"), Disk.DevicePath),
    };
}
