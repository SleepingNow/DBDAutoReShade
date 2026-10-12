using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using DBDAutoReShade.App.Infrastructure;
using DBDAutoReShade.App.Services;
using DBDAutoReShade.Core.Models;

namespace DBDAutoReShade.App.ViewModels;

/// <summary>
/// One realm row on the Presets tab. Rows live as long as the realm list is
/// unchanged; a new assignment only updates their properties, so the buttons
/// the user is clicking are never recreated underneath the mouse.
/// </summary>
public sealed class RealmRow : ObservableObject
{
    /// <summary>Clear button tooltip while a Global preset is set.</summary>
    public const string ClearToolTipWithGlobal = "Clear (detecting this realm then switches to the Global preset)";

    /// <summary>Clear button tooltip while no Global preset is set.</summary>
    public const string ClearToolTipWithoutGlobal = "Clear (no Global preset is set, so detecting this realm then switches nothing)";

    private readonly MainViewModel _parent;
    private readonly RelayCommand _testCommand;
    private readonly RelayCommand _clearCommand;
    private string _presetName = "Not configured";
    private bool _hasPreset;
    private string _clearToolTip = ClearToolTipWithoutGlobal;

    public RealmRow(MainViewModel parent, string realmName, string? presetPath, bool hasGlobalPreset)
    {
        _parent = parent;
        RealmName = realmName;
        _testCommand = new RelayCommand(() => _parent.TestRealm(RealmName), () => HasPreset);
        _clearCommand = new RelayCommand(() => _parent.ClearRealmPreset(RealmName), () => HasPreset);
        SelectCommand = new RelayCommand(() => _parent.SelectRealmPreset(RealmName));
        Update(presetPath, hasGlobalPreset);
    }

    public string RealmName { get; }

    public string PresetName { get => _presetName; private set => SetProperty(ref _presetName, value); }

    public bool HasPreset => _hasPreset;

    public ICommand TestCommand => _testCommand;

    public ICommand SelectCommand { get; }

    public ICommand ClearCommand => _clearCommand;

    /// <summary>What clearing means: the realm falls back to Global only when one is set.</summary>
    public string ClearToolTip { get => _clearToolTip; private set => SetProperty(ref _clearToolTip, value); }

    /// <summary>Shows the realm's current assignment (null/blank = none) and whether a Global preset is set.</summary>
    public void Update(string? presetPath, bool hasGlobalPreset)
    {
        PresetName = string.IsNullOrWhiteSpace(presetPath) ? "Not configured" : Path.GetFileName(presetPath);
        ClearToolTip = hasGlobalPreset ? ClearToolTipWithGlobal : ClearToolTipWithoutGlobal;
        if (SetProperty(ref _hasPreset, !string.IsNullOrWhiteSpace(presetPath), nameof(HasPreset)))
        {
            _testCommand.RaiseCanExecuteChanged();
            _clearCommand.RaiseCanExecuteChanged();
        }
    }
}

/// <summary>
/// Binds the AppController to the WPF views. All controller events are
/// marshaled to the UI thread here; the controller itself stays UI-agnostic.
/// </summary>
public sealed class MainViewModel : ObservableObject
{
    /// <summary>Shown when the captured key can't be a detection hotkey.</summary>
    public const string UnsupportedHotkeyMessage =
        "Use F1–F24 (alone or with Ctrl, Alt or Shift), or a letter, a number or a NumPad key " +
        "together with Ctrl, Alt or Shift, for the detection hotkey.";

    private readonly AppController _controller;
    private readonly Action<string> _showWarning;

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
    private string? _manifestUrlError;
    private bool _applyingManifestUrl;

    // The stored URL and reset count the last Refresh saw (null: none seen yet).
    private string? _seenManifestUrl;
    private int _seenResetCount;
    private bool _showRestartNotice;
    private string _restartNoticeText = string.Empty;
    private bool _hotkeyCaptureMode;
    private string? _bannerError;
    private string? _selectedOverrideRealm;

    /// <param name="controller">The application controller.</param>
    /// <param name="showWarning">Shows a warning to the user (default: a message box; tests record it).</param>
    public MainViewModel(AppController controller, Action<string>? showWarning = null)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        _showWarning = showWarning ?? ShowMessageBox;
        _controller.Changed += (_, _) => MarshalRefresh();

        RealmRows = new ObservableCollection<RealmRow>();

        TestGlobalCommand = new RelayCommand(() => TestGlobal());
        ChangeGlobalCommand = new RelayCommand(() => ChangeGlobal());
        ApplyOverrideCommand = new RelayCommand(() => ApplyOverride(), () => !string.IsNullOrWhiteSpace(SelectedOverrideRealm));
        ReturnToAutoCommand = new RelayCommand(() => _controller.ReturnToAutomatic());
        BrowseDbdCommand = new RelayCommand(() => BrowseDbd());
        BrowseReshadeCommand = new RelayCommand(() => BrowseReshade());
        ResetSettingsCommand = new RelayCommand(() => ResetSettings());
        BeginHotkeyCaptureCommand = new RelayCommand(() => BeginHotkeyCapture());
        RunDiagnosticsCommand = new RelayCommand(() => RunDiagnostics());
        CopyDiagnosticsCommand = new RelayCommand(() => CopyDiagnostics(), () => !string.IsNullOrWhiteSpace(DiagnosticsReport));
    }

    public ObservableCollection<RealmRow> RealmRows { get; }

    /// <summary>Realm names for the override list; replaced only when the realm list itself changes.</summary>
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

    /// <summary>True while some assigned presets can't be switched to (the running ReShade hasn't loaded their shortcuts).</summary>
    public bool ShowRestartNotice { get => _showRestartNotice; private set => SetProperty(ref _showRestartNotice, value); }

    /// <summary>
    /// The banner: which presets can't be switched to yet and what fixes it (a
    /// DBD restart, or first setting the right ReShade.ini when it is missing).
    /// </summary>
    public string RestartNoticeText { get => _restartNoticeText; private set => SetProperty(ref _restartNoticeText, value); }

    public string? BannerError { get => _bannerError; private set => SetProperty(ref _bannerError, value); }

    /// <summary>How to scan, with the configured hotkey.</summary>
    public string ScanHelpText =>
        $"Press {HotkeyText} on the loading screen to read the map; press it again to stop. " +
        "Scanning stops by itself once the map is read.";

    /// <summary>When a manual override ends (truthful: the match end clears it only while the match is tracked).</summary>
    public string OverrideHelpText =>
        $"An override stays until Return to Automatic, a new {HotkeyText} scan or DBD exit. " +
        $"It also clears at the match end, but only when the app is tracking the match (after a {HotkeyText} detection) " +
        "and \"Restore Global preset when the match ends\" is on.";

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

    public string HotkeyText
    {
        get => _hotkeyText;
        private set
        {
            if (SetProperty(ref _hotkeyText, value))
            {
                RaisePropertyChanged(nameof(ScanHelpText));
                RaisePropertyChanged(nameof(OverrideHelpText));
            }
        }
    }

    public bool HotkeyCaptureMode { get => _hotkeyCaptureMode; private set => SetProperty(ref _hotkeyCaptureMode, value); }

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

    /// <summary>The manifest URL as typed. An invalid URL is not saved; <see cref="ManifestUrlError"/> says why.</summary>
    public string ManifestUrl
    {
        get => _manifestUrl;
        set
        {
            if (SetProperty(ref _manifestUrl, value))
            {
                // The controller's change event refreshes this view model right
                // away; that refresh must not replace the text being applied.
                _applyingManifestUrl = true;
                try
                {
                    ManifestUrlError = _controller.SetManifestUrl(string.IsNullOrWhiteSpace(value) ? null : value);

                    // The user's own edit: not a change from elsewhere that would drop the draft.
                    _seenManifestUrl = _controller.Config.DatabaseUpdateManifestUrl ?? string.Empty;
                    _seenResetCount = _controller.SettingsResetCount;
                }
                finally
                {
                    _applyingManifestUrl = false;
                }
            }
        }
    }

    public string? ManifestUrlError { get => _manifestUrlError; private set => SetProperty(ref _manifestUrlError, value); }

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

    /// <summary>
    /// Pulls the controller's state into the bound properties. Cheap and
    /// churn-free: it runs on every controller event (every scan sample), so
    /// the realm rows and the override list are rebuilt only when the realm
    /// list changes; assignments update the existing rows in place.
    /// </summary>
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
        BannerError = c.HotkeyError;

        string? blocked = c.BlockedShortcutsNotice;
        ShowRestartNotice = blocked is not null;
        RestartNoticeText = blocked ?? string.Empty;

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

        // An invalid URL being typed stays in the box (with its error) instead of
        // being reverted, until the stored URL changes or the settings are reset:
        // then the box shows what is really configured and the draft is dropped.
        string manifest = c.Config.DatabaseUpdateManifestUrl ?? string.Empty;
        int resets = c.SettingsResetCount;
        if (!_applyingManifestUrl)
        {
            bool storedChanged = !string.Equals(manifest, _seenManifestUrl, StringComparison.Ordinal) ||
                resets != _seenResetCount;
            _seenManifestUrl = manifest;
            _seenResetCount = resets;
            if (storedChanged)
            {
                ManifestUrlError = null;
            }

            if (ManifestUrlError is null && _manifestUrl != manifest)
            {
                _manifestUrl = manifest;
                RaisePropertyChanged(nameof(ManifestUrl));
            }
        }

        List<string> realms = c.Database.Realms.Select(r => r.Name).ToList();
        if (!realms.SequenceEqual(AvailableRealms, StringComparer.Ordinal))
        {
            AvailableRealms = realms;
            RaisePropertyChanged(nameof(AvailableRealms));
        }

        IReadOnlyDictionary<string, string> presets = c.GetRealmPresets();
        bool hasGlobal = !string.IsNullOrWhiteSpace(c.Config.GlobalPresetPath);
        if (!RealmRows.Select(r => r.RealmName).SequenceEqual(realms, StringComparer.Ordinal))
        {
            RealmRows.Clear();
            foreach (string realm in realms)
            {
                presets.TryGetValue(realm, out string? preset);
                RealmRows.Add(new RealmRow(this, realm, preset, hasGlobal));
            }
        }
        else
        {
            foreach (RealmRow row in RealmRows)
            {
                presets.TryGetValue(row.RealmName, out string? preset);
                row.Update(preset, hasGlobal);
            }
        }
    }

    /// <summary>Tests the Global preset (waits for DBD focus); only an immediate failure is shown.</summary>
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

    public void ClearRealmPreset(string realm)
    {
        string? error = _controller.SetRealmPreset(realm, null);
        if (error is not null)
        {
            ShowWarning(error);
        }
    }

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
            "Reset all DBD Auto ReShade settings to defaults?\n\n" +
            "This app's own preset shortcuts are removed from ReShade.ini; your own ReShade shortcuts stay. " +
            "Your ReShade preset files, DBD and ReShade installs are never touched.",
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

    /// <summary>
    /// Starts capturing a new hotkey. The registered global hotkey is released
    /// meanwhile: Windows would swallow it, so pressing it could never be captured.
    /// </summary>
    public void BeginHotkeyCapture()
    {
        if (HotkeyCaptureMode)
        {
            return;
        }

        _controller.SuspendHotkey();
        HotkeyCaptureMode = true;
    }

    /// <summary>Ends capture mode without a change (Esc, the window losing focus or closing); the configured hotkey is registered again.</summary>
    public void CancelHotkeyCapture()
    {
        if (!HotkeyCaptureMode)
        {
            return;
        }

        HotkeyCaptureMode = false;
        _controller.ResumeHotkey();
    }

    /// <summary>
    /// Called from the view while capture mode is armed. Esc cancels. Returns
    /// true when the key became the new hotkey; otherwise the previous hotkey
    /// stays (and is registered again).
    /// </summary>
    public bool CaptureHotkey(Key key, ModifierKeys modifiers)
    {
        if (!HotkeyCaptureMode)
        {
            return false;
        }

        if (key == Key.Escape)
        {
            CancelHotkeyCapture();
            return false;
        }

        string? name = KeyToName(key);
        if (name is null)
        {
            CancelHotkeyCapture();
            ShowWarning(UnsupportedHotkeyMessage);
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
        string? error = _controller.UpdateHotkey(hotkey); // registers first; restores the previous one on failure
        if (error is not null)
        {
            ShowWarning($"{hotkey} can't be the detection hotkey: {error}\n\n" +
                        $"The detection hotkey is still {_controller.Config.DetectionHotkey}.");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Maps a captured WPF key to a hotkey key name understood by
    /// <c>HotkeyParser</c> (F1–F24, A–Z, 0–9, NumPad0–NumPad9); null when unsupported.
    /// </summary>
    public static string? KeyToName(Key key)
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
            // Distinct from the top-row digits: registers VK_NUMPAD0..9, not VK 0x30..0x39.
            return $"NumPad{key - Key.NumPad0}";
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

    private void ShowWarning(string message)
    {
        try
        {
            _showWarning(message);
        }
        catch
        {
            // Best effort.
        }
    }

    private static void ShowMessageBox(string message) =>
        MessageBox.Show(message, "DBD Auto ReShade", MessageBoxButton.OK, MessageBoxImage.Warning);
}
