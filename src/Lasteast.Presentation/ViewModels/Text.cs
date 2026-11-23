using Lasteast.Core.Formatting;
using Lasteast.Core.Insights;
using Lasteast.Core.Localization;
using Lasteast.Core.Models;

namespace Lasteast.Presentation.ViewModels;

/// <summary>Localized display text for domain values.</summary>
public static class Text
{
    public static string L(string key) => Loc.Instance[key];

    /// <summary>Message for an error from a command: localized for engine errors, Windows' text otherwise.</summary>
    public static string ErrorText(Exception ex) =>
        ex is Core.RawDisk.RawOperationException r && Loc.Instance.Has(r.Key) ? Loc.Instance.Format(r.Key, r.Message) : ex.Message;

    public static string Size(ulong bytes) =>
        ByteSize.Format(bytes, Loc.Instance.Language.Culture, u => Loc.Instance["Unit." + u]);

    public static string Size(ulong? bytes) => bytes is { } b ? Size(b) : L("Common.NotAvailable");

    public static string Percent(double fraction) => fraction.ToString("P0", Loc.Instance.Language.Culture);

    public static string Number(long n) => n.ToString("N0", Loc.Instance.Language.Culture);

    public static string Style(PartitionStyle s) => L("Style." + s);
    public static string Media(MediaKind m) => L("Media." + m);
    public static string Bus(BusKind b) => L("Bus." + b);
    public static string Role(PartitionRole r) => L("Role." + r);
    public static string Alignment(AlignmentStatus a) => L("Align." + a);
    public static string Health(HealthState h) => L("Health." + h);
    public static string Firmware(FirmwareKind f) => L("Firmware." + f);
    public static string Severity(FindingSeverity s) => L("Severity." + s);
    public static string YesNo(bool v) => L(v ? "Common.Yes" : "Common.No");
    public static string YesNo(bool? v) => v is { } b ? YesNo(b) : L("Common.Unknown");

    public static string MaskSerial(string? serial, bool mask)
    {
        if (string.IsNullOrWhiteSpace(serial)) return L("Common.NotAvailable");
        if (!mask || serial.Length <= 4) return serial;
        return new string('•', serial.Length - 4) + serial[^4..];
    }

    /// <summary>Formats a finding: ulong args become sizes, double args become percentages.</summary>
    public static string Finding(Finding f)
    {
        var args = f.Args.Select(a => a switch
        {
            ulong u => (object)Size(u),
            double d => Percent(d),
            _ => a,
        }).ToArray();
        return Loc.Instance.Format(f.MessageKey, args);
    }

    public static string PartitionTitle(PartitionInfo p) =>
        p.Volume?.DriveLetter is { } l ? $"{l}:" :
        p.Role switch
        {
            PartitionRole.EfiSystem or PartitionRole.MicrosoftReserved or PartitionRole.Recovery => Role(p.Role),
            _ => "*:",
        };

    public static string FileSystem(PartitionInfo p) =>
        p.Volume?.FileSystem ??
        (p.Role switch
        {
            PartitionRole.MicrosoftReserved => L("Fs.None"),
            PartitionRole.MbrExtended => L("Role.MbrExtended"),
            _ when p.Volume?.AccessError is not null => L("Fs.Raw"),
            _ => L("Fs.Unknown"),
        });
}
