using Lasteast.Core.Models;
using Lasteast.Storage.Windows.Interop;

namespace Lasteast.Storage.Windows.Detection;

/// <summary>A volume as discovered by FindFirstVolume, plus the disk extents it occupies.</summary>
internal sealed record VolumeRecord(VolumeInfo Info, IReadOnlyList<(int Disk, ulong Offset, ulong Length)> Extents);

internal sealed record DiskProbe(
    int Number,
    string DevicePath,
    ulong SizeBytes,
    uint LogicalSector,
    uint PhysicalSector,
    StructParsers.DeviceDescriptor? Descriptor,
    bool? IncursSeekPenalty,
    bool? TrimEnabled,
    StructParsers.RawLayout? Layout,
    IReadOnlyList<string> Notes);

/// <summary>
/// Combines raw IOCTL results into the domain model. Pure and deterministic — unit tested.
/// </summary>
internal static class DiskAssembler
{
    public static DiskInfo Assemble(DiskProbe probe, IReadOnlyList<VolumeRecord> volumes)
    {
        var notes = new List<string>(probe.Notes);
        var layout = probe.Layout;
        var partitions = new List<PartitionInfo>();
        bool dynamic = false;

        if (layout is not null)
        {
            var extended = layout.Partitions.FirstOrDefault(p => p.Style == PartitionStyle.Mbr && PartitionTypes.IsMbrExtended(p.MbrType));
            foreach (var raw in layout.Partitions)
            {
                PartitionRole role;
                if (raw.Style == PartitionStyle.Gpt)
                {
                    role = PartitionTypes.Classify(raw.GptType);
                }
                else
                {
                    bool inside = extended is not null && !ReferenceEquals(raw, extended)
                                  && raw.Offset >= extended.Offset && raw.Offset < extended.Offset + extended.Length;
                    role = PartitionTypes.Classify(raw.MbrType, inside);
                }
                if (role is PartitionRole.LdmData or PartitionRole.LdmMetadata) dynamic = true;

                var volume = volumes.FirstOrDefault(v => v.Extents.Any(e => e.Disk == probe.Number && e.Offset == raw.Offset));
                if (volume is { Extents.Count: > 1 })
                    notes.Add($"Volume {volume.Info.DriveLetter?.ToString() ?? volume.Info.VolumeGuidPath} spans {volume.Extents.Count} extents (dynamic/spanned volume).");

                partitions.Add(new PartitionInfo
                {
                    DiskNumber = probe.Number,
                    PartitionNumber = raw.Number,
                    OffsetBytes = raw.Offset,
                    SizeBytes = raw.Length,
                    Style = raw.Style,
                    Role = role,
                    GptType = raw.Style == PartitionStyle.Gpt ? raw.GptType : null,
                    GptId = raw.Style == PartitionStyle.Gpt ? raw.GptId : null,
                    GptName = raw.Style == PartitionStyle.Gpt && raw.GptName.Length > 0 ? raw.GptName : null,
                    GptAttributes = raw.GptAttributes,
                    MbrType = raw.Style == PartitionStyle.Mbr ? raw.MbrType : null,
                    MbrActive = raw.MbrBoot,
                    MbrRecognized = raw.MbrRecognized,
                    IsServicePartition = raw.IsServicePartition,
                    Volume = volume?.Info,
                    Alignment = PartitionTypes.GetAlignment(raw.Offset, probe.PhysicalSector),
                });
            }
        }
        else
        {
            notes.Add("Partition table could not be read.");
        }

        if (dynamic)
            notes.Add("Dynamic disk (LDM): partitions are shown as stored in the partition table; volume layout is managed by LDM. Information only.");

        var desc = probe.Descriptor;
        var model = string.Join(' ', new[] { desc?.Vendor, desc?.Product }.Where(s => !string.IsNullOrWhiteSpace(s)));
        if (model.Length == 0) model = $"Disk {probe.Number}";

        ulong usableStart = 0, usableEnd = probe.SizeBytes;
        if (layout?.Style == PartitionStyle.Gpt && layout.GptUsableLength > 0)
        {
            usableStart = layout.GptUsableStart;
            usableEnd = layout.GptUsableStart + layout.GptUsableLength;
        }

        return new DiskInfo
        {
            Number = probe.Number,
            DevicePath = probe.DevicePath,
            Model = model,
            Vendor = desc?.Vendor,
            SerialNumber = desc?.Serial,
            FirmwareRevision = desc?.Revision,
            SizeBytes = probe.SizeBytes,
            BytesPerLogicalSector = probe.LogicalSector,
            BytesPerPhysicalSector = probe.PhysicalSector,
            Style = layout?.Style ?? PartitionStyle.Unknown,
            GptDiskId = layout?.GptDiskId,
            MbrSignature = layout?.MbrSignature,
            UsableStartBytes = usableStart,
            UsableEndBytes = usableEnd,
            Media = probe.IncursSeekPenalty switch { true => MediaKind.Hdd, false => MediaKind.Ssd, null => MediaKind.Unknown },
            Bus = desc?.Bus ?? BusKind.Unknown,
            IsRemovable = desc?.Removable ?? false,
            TrimEnabled = probe.TrimEnabled,
            IsWindowsDisk = partitions.Any(p => p.Volume?.IsWindowsVolume == true),
            IsBootDisk = partitions.Any(p => p.Volume?.IsSystemPartitionVolume == true),
            IsDynamic = dynamic,
            Health = HealthState.Unknown, // SMART/NVMe health log arrives in Phase 9 — never guessed.
            Partitions = partitions,
            Notes = notes,
        };
    }
}
