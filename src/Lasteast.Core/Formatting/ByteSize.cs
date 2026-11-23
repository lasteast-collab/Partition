using System.Globalization;

namespace Lasteast.Core.Formatting;

public enum SizeUnit { Auto, B, KB, MB, GB, TB }

/// <summary>
/// Formats byte counts the way Windows Explorer does (binary multiples, labelled KB/MB/GB/TB),
/// using the current UI culture for digits and separators. Unit labels are supplied by the
/// localization layer so they can be translated (e.g. "ГБ").
/// </summary>
public static class ByteSize
{
    private static readonly string[] DefaultUnits = { "B", "KB", "MB", "GB", "TB", "PB" };

    public static string Format(ulong bytes, CultureInfo? culture = null, Func<string, string>? unitLabel = null,
        SizeUnit unit = SizeUnit.Auto, int decimals = 2)
    {
        culture ??= CultureInfo.CurrentCulture;
        int index;
        if (unit == SizeUnit.Auto)
        {
            index = 0;
            double v = bytes;
            while (v >= 1024 && index < DefaultUnits.Length - 1) { v /= 1024; index++; }
        }
        else
        {
            index = unit - SizeUnit.B;
        }

        double value = bytes / Math.Pow(1024, index);
        string number = index == 0
            ? bytes.ToString("N0", culture)
            : value.ToString("N" + decimals, culture);
        var label = DefaultUnits[index];
        return $"{number} {unitLabel?.Invoke(label) ?? label}";
    }

    public static double ToGiB(ulong bytes) => bytes / (1024.0 * 1024 * 1024);
    public static double ToMiB(ulong bytes) => bytes / (1024.0 * 1024);
}
