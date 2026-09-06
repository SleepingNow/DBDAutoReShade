using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using DBDAutoReShade.App.Infrastructure;
using DBDAutoReShade.App.Services;
using DBDAutoReShade.Core.Models;

namespace DBDAutoReShade.App.ViewModels;

/// <summary>One realm row on the Presets tab.</summary>
public sealed class RealmRow
{
    private readonly MainViewModel _parent;

    public RealmRow(MainViewModel parent, string realmName, string? presetPath)
    {
        _parent = parent;
        RealmName = realmName;
        PresetName = string.IsNullOrWhiteSpace(presetPath) ? "Not configured" : Path.GetFileName(presetPath);
        HasPreset = !string.IsNullOrWhiteSpace(presetPath);
        TestCommand = new RelayCommand(() => _parent.TestRealm(RealmName), () => HasPreset);
        SelectCommand = new RelayCommand(() => _parent.SelectRealmPreset(RealmName));
        ClearCommand = new RelayCommand(() => _parent.ClearRealmPreset(RealmName), () => HasPreset);
    }

    public string RealmName { get; }

    public string PresetName { get; }

    public bool HasPreset { get; }

    public ICommand TestCommand { get; }

    public ICommand SelectCommand { get; }

    public ICommand ClearCommand { get; }
}

/// <summary>
/// Binds the AppController to the WPF views. All controller events are
/// marshaled to the UI thread here; the controller itself stays UI-agnostic.
/// </summary>
public sealed class MainViewModel : ObservableObject
{
    private readonly AppController _controller;

    private string _dbdStatus = "Not running";
    private string _detectionText = "Waiting for DBD…";
    private string _statusMessage = string.Empty;
    private string _detectedMap = "—";
    private string _detectedRealm = "—";
    private string _activePreset = "—";
    private string _lastActivation = "—";
    private string _diagnosticsReport = "Press \"Run checks\" to verify your ReShade setup.";
    private string _autoSwitching = "ON";
    private string _hotkeyText = "F8";
    private bool _debugMode;
    private bool _autoRestore = true;
    private string _dbdPath = "Not detected";
    private string _reshadePath = "Not detected";
    private string _globalPresetName = "Not configured";
    private string _databaseText = string.Empty;
    private string _manifestUrl = string.Empty;
    private bool _showRestartNotice;
    private bool _hotkeyCaptureMode;
    private string? _bannerError;
    private string? _selectedOverrideRealm;

    public MainViewModel(AppController controller)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        _controller.Changed += (_, _) => MarshalRefresh();

        RealmRows = new ObservableCollection<RealmRow>();

        TestGlobalCommand = new RelayCommand(() => TestGlobal());
        ChangeGlobalCommand = new RelayCommand(() => ChangeGlobal());
        ApplyOverrideCommand = new RelayCommand(() => ApplyOverride(), () => !string.IsNullOrWhiteSpace(SelectedOverrideRealm));
        ReturnToAutoCommand = new RelayCommand(() => _controller.ReturnToAutomatic());
        BrowseDbdCommand = new RelayCommand(() => BrowseDbd());
        BrowseReshadeCommand = new RelayCommand(() => BrowseReshade());
        ResetSettingsCommand = new RelayCommand(() => ResetSettings());
        BeginHotkeyCaptureCommand = new RelayCommand(() => HotkeyCaptureMode = true);
        RunDiagnosticsCommand = new RelayCommand(() => RunDiagnostics());
        CopyDiagnosticsCommand = new RelayCommand(() => CopyDiagnostics(), () => !string.IsNullOrWhiteSpace(DiagnosticsReport));
    }

    public ObservableCollection<RealmRow> RealmRows { get; }

    public List<string> AvailableRealms { get; private set; } = new();

    // ---- Status tab ----

    public string DbdStatus { get => _dbdStatus; private set => SetProperty(ref _dbdStatus, value); }

    public string DetectionText { get => _detectionText; private set => SetProperty(ref _detectionText, value); }

    public string StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }

    public string DetectedMap { get => _detectedMap; private set => SetProperty(ref _detectedMap, value); }

    public string DetectedRealm { get => _detectedRealm; private set => SetProperty(ref _detectedRealm, value); }

    public string ActivePreset { get => _activePreset; private set => SetProperty(ref _activePreset, value); }

    public string LastActivation { get => _lastActivation; private set => SetProperty(ref _lastActivation, value); }

    public string DiagnosticsReport { get => _diagnosticsReport; private set => SetProperty(ref _diagnosticsReport, value); }

    public string AutoSwitching { get => _autoSwitching; private set => SetProperty(ref _autoSwitching, value); }

    public bool ShowRestartNotice { get => _showRestartNotice; private set => SetProperty(ref _showRestartNotice, value); }

    public string? BannerError { get => _bannerError; private set => SetProperty(ref _bannerError, value); }

    // ---- Presets tab ----

    public string GlobalPresetName { get => _globalPresetName; private set => SetProperty(ref _globalPresetName, value); }

    public string? SelectedOverrideRealm
    {
        get => _selectedOverrideRealm;
        set
        {
            if (SetProperty(ref _selectedOverrideRealm, value))
            {
                (ApplyOverrideCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    // ---- Settings tab ----

    public string HotkeyText { get => _hotkeyText; private set => SetProperty(ref _hotkeyText, value); }

    public bool HotkeyCaptureMode { get => _hotkeyCaptureMode; set => SetProperty(ref _hotkeyCaptureMode, value); }

    public bool DebugMode
    {
        get => _debugMode;
        set
        {
            if (SetProperty(ref _debugMode, value))
            {
                _controller.SetDebug(value);
            }
        }
    }

    public bool AutoRestore
    {
        get => _autoRestore;
        set
        {
            if (SetProperty(ref _autoRestore, value))
            {
                _controller.SetAutoRestore(value);
            }
        }
    }

    public string DbdPath { get => _dbdPath; private set => SetProperty(ref _dbdPath, value); }

    public string ReshadePath { get => _reshadePath; private set => SetProperty(ref _reshadePath, value); }

    public string DatabaseText { get => _databaseText; private set => SetProperty(ref _databaseText, value); }

    public string ManifestUrl
    {
        get => _manifestUrl;
        set
        {
            if (SetProperty(ref _manifestUrl, value))
            {
                _controller.SetManifestUrl(string.IsNullOrWhiteSpace(value) ? null : value);
            }
        }
    }

    public ICommand TestGlobalCommand { get; }

    public ICommand ChangeGlobalCommand { get; }

    public ICommand ApplyOverrideCommand { get; }

    public ICommand ReturnToAutoCommand { get; }

    public ICommand BrowseDbdCommand { get; }

    public ICommand BrowseReshadeCommand { get; }

    public ICommand ResetSettingsCommand { get; }

    public ICommand BeginHotkeyCaptureCommand { get; }

    public ICommand RunDiagnosticsCommand { get; }

    public ICommand CopyDiagnosticsCommand { get; }

    // ---- Actions ----

    public void Refresh()
    {
        var c = _controller;
        DbdStatus = c.DbdRunning ? "Running" : "Not running";
        DetectionText = c.DetectionText;
        StatusMessage = c.StatusMessage;
        DetectedMap = c.LastDetection?.MapName ?? "—";
        DetectedRealm = c.LastDetection?.RealmName ?? c.ActiveRealm ?? "—";
        ActivePreset = string.IsNullOrWhiteSpace(c.ActivePresetPath) ? "—" : Path.GetFileName(c.ActivePresetPath);
        LastActivation = c.LastActivationText;
        AutoSwitching = c.IsManualOverride ? $"Manual override ({c.OverrideRealm})" : "ON";
        HotkeyText = c.Config.DetectionHotkey.ToString();
        ShowRestartNotice = c.NeedsDbdRestartForShortcuts;
        BannerError = c.HotkeyError;

        // Settings (avoid echo loops: only push when different).
        if (_debugMode != c.Config.DebugMode)
        {
            _debugMode = c.Config.DebugMode;
            RaisePropertyChanged(nameof(DebugMode));
        }

        if (_autoRestore != c.Config.AutoRestoreGlobalOnMenu)
        {
            _autoRestore = c.Config.AutoRestoreGlobalOnMenu;
            RaisePropertyChanged(nameof(AutoRestore));
        }

        DbdPath = string.IsNullOrWhiteSpace(c.Config.DbdExePath) ? "Not detected" : c.Config.DbdExePath;
        ReshadePath = string.IsNullOrWhiteSpace(c.Config.ReshadeIniPath) ? "Not detected" : c.Config.ReshadeIniPath;
        GlobalPresetName = string.IsNullOrWhiteSpace(c.Config.GlobalPresetPath) ? "Not configured" : Path.GetFileName(c.Config.GlobalPresetPath);
        DatabaseText = $"Map database v{c.Database.Version} ({c.Database.Realms.Count} realms)";
        string manifest = c.Config.DatabaseUpdateManifestUrl ?? string.Empty;
        if (_manifestUrl != manifest)
        {
            _manifestUrl = manifest;
            RaisePropertyChanged(nameof(ManifestUrl));
        }

        AvailableRealms = c.Database.Realms.Select(r => r.Name).ToList();
        RaisePropertyChanged(nameof(AvailableRealms));

        RealmRows.Clear();
        foreach (var realm in c.Database.Realms)
        {
            c.Config.RealmPresets.TryGetValue(realm.Name, out string? preset);
            RealmRows.Add(new RealmRow(this, realm.Name, preset));
        }
    }

    public void TestGlobal()
    {
        string? error = _controller.TestGlobalPreset();
        if (error is not null)
        {
            ShowWarning(error);
        }
    }

    public void TestRealm(string realm)
    {
        string? error = _controller.TestRealmPreset(realm);
        if (error is not null)
        {
            ShowWarning(error);
        }
    }

    public void ChangeGlobal()
    {
        string? file = PickPresetFile();
        if (file is null)
        {
            return;
        }

        string? error = _controller.SetGlobalPreset(file);
        if (error is not null)
        {
            ShowWarning(error);
        }
    }

    public void SelectRealmPreset(string realm)
    {
        string? file = PickPresetFile();
        if (file is null)
        {
            return;
        }

        string? error = _controller.SetRealmPreset(realm, file);
        if (error is not null)
        {
            ShowWarning(error);
        }
    }

    public void ClearRealmPreset(string realm) => _controller.SetRealmPreset(realm, null);

    public void ApplyOverride() => _controller.ApplyManualOverride(SelectedOverrideRealm);

    public void BrowseDbd()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Locate Dead by Daylight executable",
            Filter = "DBD executable (*.exe)|*.exe",
            FileName = "DeadByDaylight-Win64-Shipping.exe"
        };
        if (dlg.ShowDialog() == true)
        {
            _controller.SetDbdPath(dlg.FileName);
        }
    }

    public void BrowseReshade()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Locate ReShade.ini",
            Filter = "ReShade configuration (ReShade.ini)|ReShade.ini",
            FileName = "ReShade.ini"
        };
        if (dlg.ShowDialog() == true)
        {
            _controller.SetReshadeIni(dlg.FileName);
        }
    }

    public void ResetSettings()
    {
        var result = MessageBox.Show(
            "Reset all DBD Auto ReShade settings to defaults?\n\nYour ReShade preset files, DBD and ReShade installs are never touched.",
            "Reset Settings", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result == MessageBoxResult.Yes)
        {
            _controller.ResetSettings();
        }
    }

    public void RunDiagnostics()
    {
        try
        {
            DiagnosticsReport = _controller.RunReshadeDiagnostics();
            (CopyDiagnosticsCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
        catch (Exception ex)
        {
            DiagnosticsReport = $"Diagnostics failed: {ex.Message}";
        }
    }

    public void CopyDiagnostics()
    {
        try
        {
            Clipboard.SetText(DiagnosticsReport);
        }
        catch
        {
            // Best effort.
        }
    }

    /// <summary>Called from the view while capture mode is armed. Returns false when the key is unusable.</summary>
    public bool CaptureHotkey(Key key, ModifierKeys modifiers)
    {
        string? name = KeyToName(key);
        if (name is null)
        {
            HotkeyCaptureMode = false;
            ShowWarning("Use F1–F24, A–Z or 0–9 for the detection hotkey.");
            return false;
        }

        var hotkey = new DetectionHotkeyConfig
        {
            Key = name,
            Ctrl = modifiers.HasFlag(ModifierKeys.Control),
            Shift = modifiers.HasFlag(ModifierKeys.Shift),
            Alt = modifiers.HasFlag(ModifierKeys.Alt)
        };

        HotkeyCaptureMode = false;
        string? error = _controller.UpdateHotkey(hotkey);
        if (error is not null)
        {
            ShowWarning($"Hotkey saved, but registration failed: {error}");
            return false;
        }

        return true;
    }

    private static string? KeyToName(Key key)
    {
        if (key >= Key.F1 && key <= Key.F24)
        {
            return $"F{key - Key.F1 + 1}";
        }

        if (key >= Key.A && key <= Key.Z)
        {
            return key.ToString();
        }

        if (key >= Key.D0 && key <= Key.D9)
        {
            return ((char)('0' + (key - Key.D0))).ToString();
        }

        if (key >= Key.NumPad0 && key <= Key.NumPad9)
        {
            return ((char)('0' + (key - Key.NumPad0))).ToString();
        }

        return null;
    }

    private static string? PickPresetFile()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select ReShade preset",
            Filter = "ReShade preset (*.ini)|*.ini"
        };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    private void MarshalRefresh()
    {
        try
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is not null && !dispatcher.CheckAccess())
            {
                dispatcher.BeginInvoke(Refresh);
            }
            else
            {
                Refresh();
            }
        }
        catch
        {
            // UI refresh must never break the controller.
        }
    }

    private static void ShowWarning(string message)
    {
        try
        {
            MessageBox.Show(message, "DBD Auto ReShade", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch
        {
            // Best effort.
        }
    }
}
