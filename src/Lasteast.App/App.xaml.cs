using System.Windows;
using System.Windows.Threading;
using Lasteast.App.Infrastructure;
using Lasteast.Core.Abstractions;
using Lasteast.Core.Localization;
using Lasteast.Core.Logging;
using Lasteast.Core.Operations;
using Lasteast.Core.Settings;
using Lasteast.Presentation.Sample;
using Lasteast.Presentation.ViewModels;
using Lasteast.Storage.Windows.Detection;
using Lasteast.Storage.Windows.Operations;

namespace Lasteast.App;

/// <summary>
/// Composition root. Command line:
///   --demo   show the labelled sample inventory instead of real disks (UI development / screenshots)
/// </summary>
public partial class App : Application
{
    private FileLog? _log;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _log = new FileLog(FileLog.DefaultDirectory);
        DispatcherUnhandledException += OnUnhandled;
        AppDomain.CurrentDomain.UnhandledException += (_, a) => _log.Error("Unhandled (domain)", a.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, a) => { _log.Error("Unobserved task", a.Exception); a.SetObserved(); };

        var store = new JsonSettingsStore(JsonSettingsStore.DefaultPath);
        var settings = store.Load();
        Loc.Instance.SetLanguage(settings.Language);

        var theme = new ThemeService();
        theme.Apply(settings.Theme, settings.AccentColor);

        var privileges = new WindowsPrivilegeService();
        bool demo = e.Args.Any(a => string.Equals(a, "--demo", StringComparison.OrdinalIgnoreCase));
        IStorageInventoryProvider provider = demo
            ? new SampleInventoryProvider()
            : new WindowsStorageInventoryProvider(_log, privileges);

        _log.Info($"Lasteast Partition starting (elevated={privileges.IsElevated}, demo={demo}, lang={settings.Language}).");

        // The only path to disk changes. Absent in demo mode, so sample data can never be "applied".
        GuardedExecutionEngine? engine = demo ? null : new GuardedExecutionEngine(
            provider, new WindowsOperationExecutor(provider, _log), new PartitionTableBackupService(), privileges,
            new JsonlOperationHistory(JsonlOperationHistory.DefaultPath), _log);

        var vm = new MainViewModel(provider, privileges, settings, store, theme, new ShellService(), _log,
            FileLog.DefaultDirectory, e.Args, engine, demo ? null : new WindowsSystemProbe(_log, provider,
                new Lasteast.Storage.Windows.Health.WindowsHealthProvider(_log), new Lasteast.Storage.Windows.Info.WindowsSystemInfo(_log),
                new Lasteast.Storage.Windows.Operations.WindowsVolumeOptimizer()),
            new RecycleBinRemover(), demo ? Array.Empty<Lasteast.Core.Cleanup.CleanupTarget>() : Lasteast.Core.Cleanup.CleanupService.WindowsTargets(),
            demo ? null : d => new Lasteast.Storage.Windows.Operations.PhysicalDiskDevice(d, writable: false),
            demo ? null : new Lasteast.Storage.Windows.Operations.WindowsBootTools(_log),
            demo ? null : new Lasteast.Storage.Windows.Operations.WinPeRescueMediaBuilder(),
            Lasteast.Storage.Windows.Operations.PartitionTableBackupService.DefaultRoot,
            demo ? null : new Lasteast.Storage.Windows.Health.WindowsHealthProvider(_log),
            demo ? null : new Lasteast.Storage.Windows.Info.WindowsVirtualDiskManager(),
            demo ? null : new Lasteast.Storage.Windows.Info.WindowsSystemInfo(_log),
            new Lasteast.Core.Licensing.LicenseStore(),
            demo ? null : new Lasteast.Storage.Windows.Operations.WindowsFileSystemTools(),
            demo ? null : new Lasteast.Storage.Windows.Operations.WindowsVolumeOptimizer());
        var window = new MainWindow(vm, theme);
        vm.RequestShutdown += (_, _) => Shutdown();
        MainWindow = window;
        window.Show();
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _log?.Error("Unhandled UI exception", e.Exception);
        MessageBox.Show(
            Loc.Instance.Format("Error.Unhandled", e.Exception.Message, FileLog.DefaultDirectory),
            "Lasteast Partition", MessageBoxButton.OK, MessageBoxImage.Error);
        // Disk writes only happen inside GuardedExecutionEngine, which handles its own failures;
        // an unhandled UI exception cannot be in the middle of one.
        e.Handled = true;
    }
}
