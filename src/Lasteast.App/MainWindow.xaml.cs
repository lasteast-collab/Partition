using System.Windows;
using System.Windows.Input;
using Lasteast.App.Infrastructure;
using Lasteast.Presentation.ViewModels;

namespace Lasteast.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly ThemeService _theme;
    private DeviceChangeWatcher? _watcher;

    public MainWindow(MainViewModel vm, ThemeService theme)
    {
        InitializeComponent();
        _vm = vm;
        _theme = theme;
        DataContext = vm;

        InputBindings.Add(new KeyBinding(vm.RefreshCommand, Key.F5, ModifierKeys.None));

        SourceInitialized += (_, _) =>
        {
            _theme.ApplyTitleBar(this);
            _watcher = new DeviceChangeWatcher(this);
            _watcher.Changed += async (_, _) =>
            {
                if (_vm.AppSettings.AutoRefreshOnDeviceChange && !_vm.IsBusy)
                    await _vm.RefreshCommand.ExecuteAsync();
            };
        };
        Loaded += async (_, _) => await _vm.RefreshCommand.ExecuteAsync();
        Closed += (_, _) => _watcher?.Dispose();
        Closing += (_, e) =>
        {
            // Never let the window close in the middle of a disk operation.
            if (_vm.PendingPage.Running)
            {
                e.Cancel = true;
                MessageBox.Show(this, Lasteast.Core.Localization.Loc.Instance["Pending.DoNotClose"], "Lasteast Partition",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        };
    }
}
