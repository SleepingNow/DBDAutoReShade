using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Threading;
using DBDAutoReShade.App.Infrastructure;
using DBDAutoReShade.App.Services;
using DBDAutoReShade.App.ViewModels;
using DBDAutoReShade.Core.Services;
using DBDAutoReShade.Desktop.Services;

namespace DBDAutoReShade.App;

/// <summary>
/// Composition root. Wires services manually (no DI container dependency),
/// shows the first-run wizard when needed, and fires the optional database
/// update check asynchronously without blocking startup.
/// Closing the main window exits the application completely (no tray).
/// Only one instance runs per user session; a fatal startup error is shown
/// and ends the process instead of leaving it running without a window.
/// </summary>
public partial class App : Application
{
    private const string AppTitle = "DBD Auto ReShade";

    private SingleInstanceGuard? _instance;
    private AppController? _controller;
    private MainViewModel? _viewModel;
    private DbdProcessService? _dbd;
    private IGlobalHotkeyService? _hotkey;
    private DetectionOrchestrator? _detector;
    private MatchLifecycleService? _lifecycle;
    private AppConfigService? _configService;
    private RealmDatabaseService? _database;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Before anything touches the config, ReShade.ini or the global hotkey.
        try
        {
            _instance = SingleInstanceGuard.TryAcquire();
        }
        catch (Exception ex)
        {
            // async void: nothing may escape (see below).
            ShowMessage($"DBD Auto ReShade could not start.\n\n{ex.Message}", MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        if (_instance is null)
        {
            ShowMessage("DBD Auto ReShade is already running.", MessageBoxImage.Information);
            Shutdown(0);
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;

        try
        {
            await StartAsync();
        }
        catch (Exception ex)
        {
            // async void: an escaping exception would end up in the handler
            // above and leave a process without a window. Say why and exit.
            ShowMessage($"DBD Auto ReShade could not start.\n\n{ex.Message}", MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private async Task StartAsync()
    {
        _configService = new AppConfigService();

        // Throws a clear IOException when the file exists but can't be read
        // (e.g. locked); the file is left untouched.
        _configService.Load();

        // Right away, before the first-run wizard: a corrupt config is why the
        // setup starts over (and, when it couldn't be moved aside, why nothing
        // set up now can be saved).
        if (_configService.LastLoadWarning is { } configWarning)
        {
            ShowMessage(configWarning, MessageBoxImage.Warning);
        }

        _database = new RealmDatabaseService();
        string dbDir = Path.Combine(AppConfigService.DefaultAppDirectory(), "database");
        string activeDb = Path.Combine(dbDir, "mapDatabase.json");
        await _database.LoadAsync(File.Exists(activeDb) ? activeDb : null); // a bad override falls back to the built-in DB

        var states = new RuntimeStateService();
        _dbd = new DbdProcessService();
        _hotkey = new GlobalHotkeyService();
        var capture = new GdiWindowCaptureService();
        var ocr = new WindowsOcrEngine();
        var matcher = new MapMatchingService();
        _detector = new DetectionOrchestrator(ocr, matcher, _database, capture);
        _lifecycle = new MatchLifecycleService(capture);
        var activator = new PresetActivationService(new SendInputKeySimulator());
        var decisions = new PresetDecisionService();
        var reshadeConfig = new ReshadeConfigurationService();

        _controller = new AppController(_configService, _database, states, _dbd, _hotkey,
            _detector, _lifecycle, activator, decisions, reshadeConfig);
        _viewModel = new MainViewModel(_controller);
        _controller.Initialize();
        _controller.ApplyDatabaseVersion(_database.Version);

        if (!_controller.Config.SetupCompleted)
        {
            // The wizard is the first window, so WPF makes it MainWindow:
            // closing it must not end the app.
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var wizard = new SetupWizardWindow(_viewModel);
            wizard.ShowDialog();
            _controller.CompleteSetup();
        }

        var main = new MainWindow(_viewModel);
        MainWindow = main;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        main.Show();
        _viewModel.Refresh();

        if (_database.LastLoadWarning is { } databaseWarning)
        {
            _controller.ReportWarning(databaseWarning); // added to the startup status, not replacing it
        }

        // Optional database update check: async, short timeout, never blocks.
        _ = CheckForDatabaseUpdateAsync(activeDb, Path.Combine(dbDir, "mapDatabase.last-good.json"));
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _controller?.Shutdown();
            _lifecycle?.Dispose();
            _detector?.Dispose();
            _hotkey?.Dispose();
            _dbd?.Dispose();
            _controller?.Dispose();
        }
        catch
        {
            // Best effort on exit.
        }

        _instance?.Dispose();
        base.OnExit(e);
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs args)
    {
        ShowMessage($"Unexpected error: {args.Exception.Message}", MessageBoxImage.Error);
        args.Handled = true;
    }

    private static void ShowMessage(string message, MessageBoxImage icon)
    {
        try
        {
            MessageBox.Show(message, AppTitle, MessageBoxButton.OK, icon);
        }
        catch
        {
            // Last resort.
        }
    }

    private async Task CheckForDatabaseUpdateAsync(string activeDb, string lastGoodDb)
    {
        try
        {
            string? manifestUrl = _controller?.Config.DatabaseUpdateManifestUrl;
            if (string.IsNullOrWhiteSpace(manifestUrl) || _database is null || _controller is null)
            {
                return;
            }

            using var http = new HttpClient();
            var updater = new DatabaseUpdateService();
            UpdateCheckResult check = await updater.CheckForUpdateAsync(
                http, manifestUrl, _database.Version, DatabaseUpdateService.DefaultTimeout);

            if (!check.UpdateAvailable || check.Manifest is null)
            {
                return;
            }

            // The version in use (built-in or downloaded): never a downgrade or a reinstall.
            DatabaseInstallResult installed = await updater.DownloadAndInstallAsync(
                http, check.Manifest, activeDb, lastGoodDb, _database.Version);
            if (!installed.Success)
            {
                return;
            }

            await _database.LoadAsync(activeDb);
            _controller.ApplyDatabaseVersion(_database.Version); // config change under the controller's lock
            if (_database.LastLoadWarning is { } warning)
            {
                _controller.ReportWarning(warning);
                return;
            }

            MessageBox.Show($"Map database updated to v{installed.InstalledVersion}. New realms appear under Presets.",
                AppTitle, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch
        {
            // Offline or any failure: keep the current database silently.
        }
    }
}
