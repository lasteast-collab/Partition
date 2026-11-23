namespace Lasteast.Presentation.Services;

/// <summary>Where and how large the main window opens (device-independent units).</summary>
public sealed record WindowPlacement(double Left, double Top, double Width, double Height, double MinWidth, double MinHeight, bool Maximize);

/// <summary>
/// Fits the main window into the screen's work area (the screen minus the taskbar). The preferred 1440×900 is larger
/// than many laptop screens — 1366×768, or 1920×1080 at 150 % scaling — and a centred window that is larger than the
/// screen opens with its title bar and close button off-screen. The window is never larger than the work area, its
/// minimum size never exceeds it, and on a small screen it opens maximized.
/// </summary>
public static class WindowFit
{
    public static WindowPlacement Compute(double areaLeft, double areaTop, double areaWidth, double areaHeight,
        double preferredWidth = 1440, double preferredHeight = 900, double minWidth = 1024, double minHeight = 640)
    {
        double minW = Math.Min(minWidth, areaWidth), minH = Math.Min(minHeight, areaHeight);
        // leave a margin so the whole frame, title bar included, is visible
        double w = Math.Max(minW, Math.Min(preferredWidth, areaWidth * 0.94));
        double h = Math.Max(minH, Math.Min(preferredHeight, areaHeight * 0.94));
        bool small = preferredWidth > areaWidth || preferredHeight > areaHeight * 1.05 || minWidth > areaWidth || minHeight > areaHeight;
        double left = areaLeft + Math.Max(0, (areaWidth - w) / 2);
        double top = areaTop + Math.Max(0, (areaHeight - h) / 2);
        return new WindowPlacement(Math.Round(left), Math.Round(top), Math.Round(w), Math.Round(h), minW, minH, small);
    }
}
