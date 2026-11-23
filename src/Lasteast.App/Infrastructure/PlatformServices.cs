using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Lasteast.Core.Settings;
using Lasteast.Presentation.Services;
using Microsoft.Win32;

namespace Lasteast.App.Infrastructure;

/// <summary>Swaps the palette dictionary (Dark/Light), sets the accent brush and the window title-bar mode.</summary>
public sealed class ThemeService : IThemeService
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public bool IsDark { get; private set; } = true;

    public void Apply(ThemeMode mode, string accentHex)
    {
        IsDark = mode switch
        {
            ThemeMode.Dark => true,
            ThemeMode.Light => false,
            _ => !SystemPrefersLight(),
        };

        var resources = Application.Current.Resources;
        var palette = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/LasteastPartition;component/Themes/{(IsDark ? "Dark" : "Light")}.xaml", UriKind.Absolute),
        };
        // MergedDictionaries[0] is always the palette (see App.xaml).
        if (resources.MergedDictionaries.Count > 0) resources.MergedDictionaries[0] = palette;
        else resources.MergedDictionaries.Add(palette);

        Color accent;
        try { accent = (Color)ColorConverter.ConvertFromString(accentHex)!; }
        catch (Exception) { accent = Color.FromRgb(0x1E, 0x88, 0xE5); }
        resources["Color.Accent"] = accent;
        resources["Brush.Accent"] = Freeze(new SolidColorBrush(accent));
        resources["Brush.AccentSoft"] = Freeze(new SolidColorBrush(Color.FromArgb(0x33, accent.R, accent.G, accent.B)));
        resources[SystemColors.HighlightBrushKey] = Freeze(new SolidColorBrush(accent));

        foreach (Window w in Application.Current.Windows) ApplyTitleBar(w);
    }

    public void ApplyTitleBar(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        int on = IsDark ? 1 : 0;
        _ = DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int));
    }

    private static bool SystemPrefersLight()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v != 0;
        }
        catch { return false; }
    }

    private static Brush Freeze(Brush b)
    {
        b.Freeze();
        return b;
    }
}

public sealed class ShellService : IShellService
{
    public void OpenFolder(string path)
    {
        try
        {
            System.IO.Directory.CreateDirectory(path);
            Process.Start("explorer.exe", $"\"{path}\"");
        }
        catch (Exception) { /* non-critical */ }
    }

    public void RevealFile(string path)
    {
        try { Process.Start("explorer.exe", $"/select,\"{path}\""); } catch (Exception) { }
    }

    public void CopyToClipboard(string text)
    {
        try { Clipboard.SetText(text); } catch (COMException) { }
    }

    public bool Confirm(string title, string message)
        => MessageBox.Show(Application.Current.MainWindow, message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning,
               MessageBoxResult.No) == MessageBoxResult.Yes;

    public string? PickFolder()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Multiselect = false };
        return dlg.ShowDialog(Application.Current.MainWindow) == true ? dlg.FolderName : null;
    }

    public string? PickSaveFile(string defaultName, string filter)
    {
        var d = new Microsoft.Win32.SaveFileDialog { FileName = defaultName, Filter = filter, OverwritePrompt = true };
        return d.ShowDialog() == true ? d.FileName : null;
    }

    public string? PickOpenFile(string filter)
    {
        var d = new Microsoft.Win32.OpenFileDialog { Filter = filter, CheckFileExists = true };
        return d.ShowDialog() == true ? d.FileName : null;
    }
}

/// <summary>
/// Listens for WM_DEVICECHANGE (disk/volume arrival and removal) and raises a debounced event,
/// so the disk list stays current when USB drives are plugged in or removed.
/// </summary>
public sealed class DeviceChangeWatcher : IDisposable
{
    private const int WM_DEVICECHANGE = 0x0219;
    private const int DBT_DEVICEARRIVAL = 0x8000;
    private const int DBT_DEVICEREMOVECOMPLETE = 0x8004;
    private const int DBT_DEVNODES_CHANGED = 0x0007;

    private readonly HwndSource _source;
    private readonly System.Windows.Threading.DispatcherTimer _debounce;

    public event EventHandler? Changed;

    public DeviceChangeWatcher(Window window)
    {
        _source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle)!;
        _source.AddHook(Hook);
        _debounce = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            Changed?.Invoke(this, EventArgs.Empty);
        };
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_DEVICECHANGE && (int)wParam is DBT_DEVICEARRIVAL or DBT_DEVICEREMOVECOMPLETE or DBT_DEVNODES_CHANGED)
        {
            _debounce.Stop();
            _debounce.Start();
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        _debounce.Stop();
        _source.RemoveHook(Hook);
    }
}
