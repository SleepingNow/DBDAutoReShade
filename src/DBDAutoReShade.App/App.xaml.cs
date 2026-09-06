using System.IO;
using System.Net.Http;
using System.Windows;
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
/// </summary>
public partial class App : Application
{
    private AppController? _controller;
    private MainViewModel? _viewModel;
    private DbdProcessService? _dbd;
    private GlobalHotkeyService? _hotkey;
    private DetectionOrchestrator? _detector;
    private MatchLifecycleService? _lifecycle;
    private AppConfigService? _configService;
    private RealmDatabaseService? _database;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            try
            {
                MessageBox.Show($"Unexpected error: {args.Exception.Message}", "DBD Auto ReShade",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch
            {
                // Last resort.
            }

            args.Handled = true;
        };

        _configService = new AppConfigService();
        _configService.Load();

        _database = new RealmDatabaseService();
        string dbDir = Path.Combine(AppConfigService.DefaultAppDirectory(), "database");
        string activeDb = Path.Combine(dbDir, "mapDatabase.json");
        await _database.LoadAsync(File.Exists(activeDb) ? activeDb : null);
        if (_configService.Config.DatabaseVersion != _database.Version)
        {
            _configService.Config.DatabaseVersion = _database.Version;
            _configService.Save();
        }

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

        if (!_configService.Config.SetupCompleted)
        {
            var wizard = new SetupWizardWindow(_viewModel);
            wizard.ShowDialog();
            _controller.CompleteSetup();
        }

        var main = new MainWindow(_viewModel);
        MainWindow = main;
        main.Show();
        _viewModel.Refresh();

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

        base.OnExit(e);
    }

    private async Task CheckForDatabaseUpdateAsync(string activeDb, string lastGoodDb)
    {
        try
        {
            string? manifestUrl = _configService?.Config.DatabaseUpdateManifestUrl;
            if (string.IsNullOrWhiteSpace(manifestUrl) || _database is null)
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

            DatabaseInstallResult installed = await updater.DownloadAndInstallAsync(
                http, check.Manifest, activeDb, lastGoodDb);
            if (!installed.Success)
            {
                return;
            }

            await _database.LoadAsync(activeDb);
            if (_configService is not null)
            {
                _configService.Config.DatabaseVersion = _database.Version;
                _configService.Save();
            }

            _viewModel?.Refresh();
            MessageBox.Show($"Map database updated to v{installed.InstalledVersion}. New realms appear under Presets.",
                "DBD Auto ReShade", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch
        {
            // Offline or any failure: keep the current database silently.
        }
    }
}
