using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using DBDAutoReShade.App.Infrastructure;
using DBDAutoReShade.Core.Models;

namespace DBDAutoReShade.App.ViewModels;

/// <summary>
/// The Settings screen: the detection and Global preset hotkeys (with capture), paths, behaviour (auto-restore,
/// animations, the Great sound and its volume, debug mode), the map database, the ReShade check and reset. Every
/// action tells its outcome on the Settings screen itself, next to the group it belongs to: the controller's status
/// line is shown on the Status screen only, so a problem it reports (ReShade.ini could not be updated, the settings
/// could not be saved, a hotkey could not be registered again) is taken from it right after the call.
/// </summary>
public sealed partial class MainViewModel
{
    private string _hotkeyText = "F8";
    private string _globalHotkeyText = "F9";
    private bool _hasGlobalHotkey = true;
    private bool _hotkeyCaptureMode;
    private bool _globalHotkeyCaptureMode;
    private string _hotkeyNote = string.Empty;
    private string _globalHotkeyNote = string.Empty;
    private string? _hotkeyNoteProblem;
    private string? _globalHotkeyNoteProblem;
    private string? _detectionHotkeyProblem;
    private string? _globalHotkeyProblem;
    private bool _debugMode;
    private bool _autoRestore = true;
    private bool _animationsEnabled = true;
    private bool _soundEnabled = true;
    private double _soundVolumePercent = AppConfig.DefaultSoundVolume * 100;
    private bool _soundVolumeDirty;
    private string? _soundProblem;
    private bool _soundProblemChecked;
    private string? _behaviourProblem;
    private string _dbdPath = "Not detected";
    private string _reshadePath = "Not detected";
    private string _pathsNote = string.Empty;
    private string? _pathsProblem;
    private string? _databaseProblem;
    private string _databaseVersionText = string.Empty;
    private string _databaseSummary = string.Empty;
    private string _manifestUrl = string.Empty;
    private string? _manifestUrlError;
    private bool _applyingManifestUrl;

    // The stored URL and reset count the last Refresh saw (null: none seen yet).
    private string? _seenManifestUrl;
    private int _seenResetCount;
    private string _diagnosticsReport = "Press \"Run checks\" to verify your ReShade setup.";
    private string _copyNote = string.Empty;
    private bool _showReportText;
    private bool _isResetConfirming;
    private string _resetNote = string.Empty;
    private string? _resetProblem;
    private RelayCommand _copyDiagnosticsCommand = null!;
    private RelayCommand _clearGlobalHotkeyCommand = null!;
    private RelayCommand _playSoundCommand = null!;

    // ---- hotkeys ----

    /// <summary>The detection hotkey, e.g. "F8".</summary>
    public string HotkeyText
    {
        get => _hotkeyText;
        private set
        {
            if (SetProperty(ref _hotkeyText, value))
            {
                RaisePropertyChanged(nameof(ScanHelpText));
                RaisePropertyChanged(nameof(OverrideHelpText));
                RaisePropertyChanged(nameof(RailScanHint));
                RaisePropertyChanged(nameof(RailHotkeyName));
            }
        }
    }

    /// <summary>Capturing a new detection hotkey (both hotkeys are released meanwhile).</summary>
    public bool HotkeyCaptureMode
    {
        get => _hotkeyCaptureMode;
        private set
        {
            if (SetProperty(ref _hotkeyCaptureMode, value))
            {
                RaisePropertyChanged(nameof(IsCapturingHotkey));
                RaisePropertyChanged(nameof(HotkeyButtonText));
                RaisePropertyChanged(nameof(HotkeyButtonName));
            }
        }
    }

    /// <summary>The Global preset hotkey ("Switch to Global preset"), e.g. "F9", or "Not set".</summary>
    public string GlobalHotkeyText
    {
        get => _globalHotkeyText;
        private set
        {
            if (SetProperty(ref _globalHotkeyText, value))
            {
                RaisePropertyChanged(nameof(RailGlobalHotkeyName));
            }
        }
    }

    /// <summary>A Global preset hotkey is set (it may still be unregistered; see <see cref="GlobalHotkeyUnavailable"/>).</summary>
    public bool HasGlobalHotkey
    {
        get => _hasGlobalHotkey;
        private set
        {
            if (SetProperty(ref _hasGlobalHotkey, value))
            {
                _clearGlobalHotkeyCommand.RaiseCanExecuteChanged();
                RaiseRailGlobalKey();
            }
        }
    }

    /// <summary>
    /// The Global preset hotkey is set but not registered (<see cref="GlobalHotkeyProblem"/>: it is the detection
    /// hotkey's key, a ReShade shortcut combo, or another program holds it): pressing it does nothing, so the rail
    /// dims its keycap and says it is unavailable instead of presenting a working key.
    /// </summary>
    public bool GlobalHotkeyUnavailable => HasGlobalHotkey && GlobalHotkeyProblem is not null;

    /// <summary>The rail hint next to the Global preset hotkey: what it does, that it is unavailable, or that none is set.</summary>
    public string RailGlobalHint =>
        !HasGlobalHotkey ? "Global preset hotkey: not set"
        : GlobalHotkeyUnavailable ? "Global preset hotkey unavailable"
        : "Global preset";

    /// <summary>The rail's Global preset keycap for assistive technology ("Global preset hotkey F9", "…, unavailable").</summary>
    public string RailGlobalHotkeyName =>
        GlobalHotkeyUnavailable ? $"Global preset hotkey {GlobalHotkeyText}, unavailable" : $"Global preset hotkey {GlobalHotkeyText}";

    /// <summary>
    /// The detection hotkey is not registered (<see cref="DetectionHotkeyProblem"/>): the rail dims its keycap and says
    /// it is unavailable instead of asking for a key press that can't reach the app.
    /// </summary>
    public bool HotkeyUnavailable => DetectionHotkeyProblem is not null;

    /// <summary>The rail's detection keycap for assistive technology ("Detection hotkey F8", "…, unavailable").</summary>
    public string RailHotkeyName => HotkeyUnavailable ? $"Detection hotkey {HotkeyText}, unavailable" : $"Detection hotkey {HotkeyText}";

    /// <summary>Capturing a new Global preset hotkey (both hotkeys are released meanwhile).</summary>
    public bool GlobalHotkeyCaptureMode
    {
        get => _globalHotkeyCaptureMode;
        private set
        {
            if (SetProperty(ref _globalHotkeyCaptureMode, value))
            {
                RaisePropertyChanged(nameof(IsCapturingHotkey));
                RaisePropertyChanged(nameof(GlobalHotkeyButtonText));
                RaisePropertyChanged(nameof(GlobalHotkeyButtonName));
            }
        }
    }

    /// <summary>Either hotkey is being captured: the window routes key presses to <see cref="CaptureHotkey"/>.</summary>
    public bool IsCapturingHotkey => HotkeyCaptureMode || GlobalHotkeyCaptureMode;

    /// <summary>"Change hotkey", or "Cancel" while capturing.</summary>
    public string HotkeyButtonText => HotkeyCaptureMode ? "Cancel" : "Change hotkey";

    /// <summary>"Change hotkey", or "Cancel" while capturing.</summary>
    public string GlobalHotkeyButtonText => GlobalHotkeyCaptureMode ? "Cancel" : "Change hotkey";

    /// <summary>The detection hotkey button's accessible name: it starts with the visible label and follows the capture.</summary>
    public string HotkeyButtonName => HotkeyCaptureMode ? "Cancel detection hotkey change" : "Change hotkey for detection";

    /// <summary>The Global preset hotkey button's accessible name: it starts with the visible label and follows the capture.</summary>
    public string GlobalHotkeyButtonName => GlobalHotkeyCaptureMode ? "Cancel Global preset hotkey change" : "Change hotkey for the Global preset";

    /// <summary>The detection hotkey's note ("Hotkey set to F7." / "Hotkey unchanged.").</summary>
    public string HotkeyNote { get => _hotkeyNote; private set => SetProperty(ref _hotkeyNote, value); }

    /// <summary>
    /// What went wrong along with a detection hotkey change (it was not saved, the Global preset hotkey could not be
    /// registered again); null when nothing.
    /// </summary>
    public string? HotkeyNoteProblem { get => _hotkeyNoteProblem; private set => SetProperty(ref _hotkeyNoteProblem, value); }

    /// <summary>The Global preset hotkey's note.</summary>
    public string GlobalHotkeyNote { get => _globalHotkeyNote; private set => SetProperty(ref _globalHotkeyNote, value); }

    /// <summary>
    /// What went wrong along with a Global preset hotkey change or clear (it was not saved, the detection hotkey could
    /// not be registered again); null when nothing.
    /// </summary>
    public string? GlobalHotkeyNoteProblem { get => _globalHotkeyNoteProblem; private set => SetProperty(ref _globalHotkeyNoteProblem, value); }

    /// <summary>Why the detection hotkey is not registered (another program holds it, …); null while it works.</summary>
    public string? DetectionHotkeyProblem
    {
        get => _detectionHotkeyProblem;
        private set
        {
            if (SetProperty(ref _detectionHotkeyProblem, value))
            {
                RaisePropertyChanged(nameof(HotkeyUnavailable));
                RaisePropertyChanged(nameof(RailScanHint));
                RaisePropertyChanged(nameof(RailHotkeyName));
            }
        }
    }

    /// <summary>Why the Global preset hotkey is set but not registered; null while it works or is cleared.</summary>
    public string? GlobalHotkeyProblem
    {
        get => _globalHotkeyProblem;
        private set
        {
            if (SetProperty(ref _globalHotkeyProblem, value))
            {
                RaiseRailGlobalKey();
            }
        }
    }

    private void RaiseRailGlobalKey()
    {
        RaisePropertyChanged(nameof(GlobalHotkeyUnavailable));
        RaisePropertyChanged(nameof(RailGlobalHint));
        RaisePropertyChanged(nameof(RailGlobalHotkeyName));
    }

    /// <summary>Starts (or, while capturing, cancels) capturing the detection hotkey.</summary>
    public ICommand BeginHotkeyCaptureCommand { get; private set; } = null!;

    /// <summary>Starts (or, while capturing, cancels) capturing the Global preset hotkey.</summary>
    public ICommand BeginGlobalHotkeyCaptureCommand { get; private set; } = null!;

    /// <summary>Disables the Global preset hotkey.</summary>
    public ICommand ClearGlobalHotkeyCommand => _clearGlobalHotkeyCommand;

    // ---- behaviour ----

    public bool DebugMode
    {
        get => _debugMode;
        set
        {
            if (SetProperty(ref _debugMode, value))
            {
                _controller.SetDebug(value);
                TellBehaviourSaved();
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
                TellBehaviourSaved();
            }
        }
    }

    /// <summary>The "Play animations" checkbox's hint (shown under it, and its help text).</summary>
    public const string AnimationsHint = "Turn off to save performance. Animations also stop when Windows animation effects are off.";

    /// <summary>Settings → "Play animations" (saved). Off: no storyboards, no ambient loops, instant state changes.</summary>
    public bool AnimationsEnabled
    {
        get => _animationsEnabled;
        set
        {
            if (SetProperty(ref _animationsEnabled, value))
            {
                _controller.SetAnimationsEnabled(value);
                TellBehaviourSaved();
            }
        }
    }

    /// <summary>Settings → "Play the Great skill-check sound when a map is found" (saved).</summary>
    public bool SoundEnabled
    {
        get => _soundEnabled;
        set
        {
            if (SetProperty(ref _soundEnabled, value))
            {
                _controller.SetSoundEnabled(value);
                TellBehaviourSaved();
                _playSoundCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>
    /// The volume slider, 0..100 %, live (the label follows every tick). It is saved by <see cref="CommitSoundVolume"/>
    /// when the slider settles (thumb released, key released, focus lost), not on every tick.
    /// </summary>
    public double SoundVolumePercent
    {
        get => _soundVolumePercent;
        set
        {
            double clamped = double.IsFinite(value) ? Math.Clamp(value, 0, 100) : AppConfig.DefaultSoundVolume * 100;
            if (SetProperty(ref _soundVolumePercent, clamped))
            {
                _soundVolumeDirty = true;
                RaisePropertyChanged(nameof(SoundVolumeText));
            }
        }
    }

    /// <summary>The slider's live label, e.g. "50 %".</summary>
    public string SoundVolumeText => $"{Math.Round(SoundVolumePercent):0} %";

    /// <summary>Why the Great sound can't play whatever the settings (e.g. its asset is missing); null when it can. Asked once.</summary>
    public string? SoundProblem { get => _soundProblem; private set => SetProperty(ref _soundProblem, value); }

    /// <summary>A Behaviour setting was changed but could not be saved ("Could not save settings: …"); null once a save works.</summary>
    public string? BehaviourProblem { get => _behaviourProblem; private set => SetProperty(ref _behaviourProblem, value); }

    /// <summary>
    /// A Behaviour setting was just saved (or not): its problem line tells the outcome anew, so the same failure twice
    /// is announced twice (a live region speaks when its text changes).
    /// </summary>
    private void TellBehaviourSaved()
    {
        BehaviourProblem = null;
        BehaviourProblem = _controller.LastSaveError;
    }

    /// <summary>Plays the Great sound at the slider's volume (enabled while the sound is on).</summary>
    public ICommand PlaySoundCommand => _playSoundCommand;

    /// <summary>Saves the slider's volume (when it settles). Nothing is written when it did not change.</summary>
    public void CommitSoundVolume()
    {
        if (!_soundVolumeDirty)
        {
            return;
        }

        _soundVolumeDirty = false;
        _controller.SetSoundVolume(Math.Round(SoundVolumePercent) / 100.0);
        TellBehaviourSaved();
    }

    /// <summary>Plays the Great sound once at the slider's (unsaved) volume; silent at 0 %.</summary>
    public bool PlaySoundPreview()
    {
        double volume = Math.Round(SoundVolumePercent) / 100.0;
        if (!SoundEnabled || volume <= 0)
        {
            return false;
        }

        try
        {
            return _controller.SoundEffects.PlayGreat(volume);
        }
        catch
        {
            return false;
        }
    }

    // ---- paths ----

    public string DbdPath { get => _dbdPath; private set => SetProperty(ref _dbdPath, value); }

    public string ReshadePath { get => _reshadePath; private set => SetProperty(ref _reshadePath, value); }

    /// <summary>What the last path change did ("ReShade.ini path updated."); empty before one.</summary>
    public string PathsNote { get => _pathsNote; private set => SetProperty(ref _pathsNote, value); }

    /// <summary>
    /// What went wrong along with the last path change (ReShade.ini's shortcuts could not be updated, the settings
    /// could not be saved, …) or a notice to act on; null when nothing.
    /// </summary>
    public string? PathsProblem { get => _pathsProblem; private set => SetProperty(ref _pathsProblem, value); }

    public ICommand BrowseDbdCommand { get; private set; } = null!;

    public ICommand BrowseReshadeCommand { get; private set; } = null!;

    // ---- map database ----

    /// <summary>The database version, e.g. "1".</summary>
    public string DatabaseVersionText { get => _databaseVersionText; private set => SetProperty(ref _databaseVersionText, value); }

    /// <summary>"20 realms and 44 maps".</summary>
    public string DatabaseSummary { get => _databaseSummary; private set => SetProperty(ref _databaseSummary, value); }

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
                    DatabaseProblem = ManifestUrlError is null ? _controller.LastSaveError : null;

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

    public string? ManifestUrlError
    {
        get => _manifestUrlError;
        private set
        {
            if (SetProperty(ref _manifestUrlError, value))
            {
                RaisePropertyChanged(nameof(ManifestUrlHelp));
            }
        }
    }

    /// <summary>The manifest URL box's hint under it.</summary>
    public const string ManifestUrlHint = "Leave empty to disable. Must be an https:// address.";

    /// <summary>What the manifest URL box tells assistive technology about itself: why it was refused, else its hint.</summary>
    public string ManifestUrlHelp => ManifestUrlError ?? ManifestUrlHint;

    /// <summary>A valid manifest URL could not be saved ("Could not save settings: …"); null when it was.</summary>
    public string? DatabaseProblem { get => _databaseProblem; private set => SetProperty(ref _databaseProblem, value); }

    // ---- ReShade check ----

    public string DiagnosticsReport { get => _diagnosticsReport; private set => SetProperty(ref _diagnosticsReport, value); }

    /// <summary>The report's lines, styled OK / Info / Issue.</summary>
    public ObservableCollection<ReportLine> DiagnosticsLines { get; } = new();

    /// <summary>"Report copied." (or how to copy it by hand).</summary>
    public string CopyNote { get => _copyNote; private set => SetProperty(ref _copyNote, value); }

    /// <summary>
    /// The clipboard could not be reached: the report is also shown as plain, selectable text (select it and press
    /// Ctrl+C). Cleared by the next check or copy.
    /// </summary>
    public bool ShowReportText { get => _showReportText; private set => SetProperty(ref _showReportText, value); }

    /// <summary>Puts text on the clipboard (default: the Windows clipboard; tests replace it). It may throw (the clipboard is busy).</summary>
    public Action<string> ClipboardWriter { get; set; } = Clipboard.SetText;

    public ICommand RunDiagnosticsCommand { get; private set; } = null!;

    public ICommand CopyDiagnosticsCommand => _copyDiagnosticsCommand;

    // ---- reset ----

    /// <summary>"Reset settings now?" is shown.</summary>
    public bool IsResetConfirming { get => _isResetConfirming; private set => SetProperty(ref _isResetConfirming, value); }

    /// <summary>"Settings reset. Your preset files were not touched." (or that the reset had problems: <see cref="ResetProblem"/>).</summary>
    public string ResetNote { get => _resetNote; private set => SetProperty(ref _resetNote, value); }

    /// <summary>
    /// What went wrong during the reset (this app's shortcuts could not be removed from ReShade.ini, the settings could
    /// not be saved, a default hotkey could not be registered); null when nothing.
    /// </summary>
    public string? ResetProblem { get => _resetProblem; private set => SetProperty(ref _resetProblem, value); }

    /// <summary>Asks to reset (the danger zone shows Reset settings / Cancel).</summary>
    public ICommand ResetSettingsCommand { get; private set; } = null!;

    /// <summary>Resets the settings.</summary>
    public ICommand ConfirmResetCommand { get; private set; } = null!;

    /// <summary>Keeps the settings.</summary>
    public ICommand CancelResetCommand { get; private set; } = null!;

    // ---- actions ----

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
            ApplyDbdPath(dlg.FileName);
        }
    }

    /// <summary>Uses <paramref name="path"/> as Dead by Daylight's executable; the Paths group tells the outcome.</summary>
    public void ApplyDbdPath(string? path)
    {
        const string lead = "Dead by Daylight path updated.";
        ClearPathsOutcome();
        _controller.SetDbdPath(path);
        PathsProblem = StatusAfter(lead);
        PathsNote = lead;
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
            ApplyReshadeIni(dlg.FileName);
        }
    }

    /// <summary>Uses <paramref name="path"/> as ReShade.ini (its shortcuts are synced); the Paths group tells the outcome.</summary>
    public void ApplyReshadeIni(string? path)
    {
        const string lead = "ReShade.ini path updated.";
        ClearPathsOutcome();
        _controller.SetReshadeIni(path);
        PathsProblem = StatusAfter(lead);
        PathsNote = lead;
    }

    /// <summary>A path change starts its outcome afresh: the same note or problem again is announced again.</summary>
    private void ClearPathsOutcome()
    {
        PathsNote = string.Empty;
        PathsProblem = null;
    }

    /// <summary>
    /// Asks to reset the settings (inline confirmation on the Settings screen). A hotkey capture in progress ends first:
    /// the reset registers both hotkeys again, and a capture left armed could then never receive them.
    /// </summary>
    public void ResetSettings()
    {
        CancelHotkeyCapture();
        ResetNote = string.Empty;
        ResetProblem = null;
        IsResetConfirming = true;
    }

    /// <summary>
    /// Resets every setting to its default; this app's own ReShade shortcuts are removed from ReShade.ini. The note
    /// tells what the controller reported: a clean reset, or the problems it had (<see cref="ResetProblem"/>).
    /// </summary>
    public void ConfirmResetSettings()
    {
        IsResetConfirming = false;
        CancelHotkeyCapture(); // (the reset registers both hotkeys again: no capture may stay armed over it)
        _controller.ResetSettings();
        string? problem = StatusAfter("Settings reset to defaults.");
        ResetProblem = problem;
        ResetNote = problem is null
            ? "Settings reset. Your preset files were not touched."
            : "Settings reset, with problems. Your preset files were not touched.";
        PathsNote = string.Empty;
        PathsProblem = null;
        BehaviourProblem = null;
        DatabaseProblem = null;
        HotkeyNote = string.Empty; // (the hotkeys are the defaults again: an older note about them no longer holds)
        HotkeyNoteProblem = null;
        GlobalHotkeyNote = string.Empty;
        GlobalHotkeyNoteProblem = null;
    }

    public void RunDiagnostics()
    {
        try
        {
            DiagnosticsReport = _controller.RunReshadeDiagnostics();
        }
        catch (Exception ex)
        {
            DiagnosticsReport = $"Diagnostics failed: {ex.Message}";
        }

        DiagnosticsLines.Clear();
        foreach (ReportLine line in ReportLine.ParseReport(DiagnosticsReport))
        {
            DiagnosticsLines.Add(line);
        }

        CopyNote = string.Empty;
        ShowReportText = false;
        _copyDiagnosticsCommand.RaiseCanExecuteChanged();
    }

    /// <summary>Copies the report. When the clipboard is busy (another program holds it), the report is shown as selectable text instead.</summary>
    public void CopyDiagnostics()
    {
        CopyNote = string.Empty; // (each copy is told: "Report copied." again is announced again)
        try
        {
            ClipboardWriter(DiagnosticsReport);
            CopyNote = "Report copied.";
            ShowReportText = false;
        }
        catch
        {
            CopyNote = "The clipboard is busy. Select the report text below and press Ctrl+C.";
            ShowReportText = true;
        }
    }

    /// <summary>
    /// Starts capturing a new detection hotkey. Both registered global hotkeys are released
    /// meanwhile: Windows would swallow them, so pressing one could never be captured.
    /// </summary>
    public void BeginHotkeyCapture()
    {
        if (IsCapturingHotkey)
        {
            return;
        }

        _controller.SuspendHotkey();
        HotkeyNote = string.Empty;
        HotkeyNoteProblem = null;
        HotkeyCaptureMode = true;
    }

    /// <summary>Starts capturing a new Global preset hotkey (both hotkeys are released meanwhile).</summary>
    public void BeginGlobalHotkeyCapture()
    {
        if (IsCapturingHotkey)
        {
            return;
        }

        _controller.SuspendHotkey();
        GlobalHotkeyNote = string.Empty;
        GlobalHotkeyNoteProblem = null;
        GlobalHotkeyCaptureMode = true;
    }

    /// <summary>Ends capture mode without a change (Esc, the window losing focus or closing); the configured hotkeys are registered again.</summary>
    public void CancelHotkeyCapture()
    {
        if (!IsCapturingHotkey)
        {
            return;
        }

        if (HotkeyCaptureMode)
        {
            HotkeyNote = "Hotkey unchanged.";
            HotkeyNoteProblem = null;
        }

        if (GlobalHotkeyCaptureMode)
        {
            GlobalHotkeyNote = "Hotkey unchanged.";
            GlobalHotkeyNoteProblem = null;
        }

        HotkeyCaptureMode = false;
        GlobalHotkeyCaptureMode = false;
        _controller.ResumeHotkey();
    }

    /// <summary>
    /// Called from the view while capture mode is armed. Esc cancels. Returns
    /// true when the key became the new hotkey (the detection hotkey or the
    /// Global preset hotkey, whichever is being captured); otherwise the
    /// previous hotkey stays (and is registered again).
    /// </summary>
    public bool CaptureHotkey(Key key, ModifierKeys modifiers)
    {
        if (!IsCapturingHotkey)
        {
            return false;
        }

        bool global = GlobalHotkeyCaptureMode;
        if (key == Key.Escape)
        {
            CancelHotkeyCapture();
            return false;
        }

        string? name = KeyToName(key);
        if (name is null)
        {
            CancelHotkeyCapture();
            ShowWarning(global ? UnsupportedGlobalHotkeyMessage : UnsupportedHotkeyMessage);
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
        GlobalHotkeyCaptureMode = false;
        if (global)
        {
            string? globalError = _controller.UpdateGlobalHotkey(hotkey); // registers first; restores the previous one on failure
            if (globalError is not null)
            {
                GlobalHotkeyNote = "Hotkey unchanged.";
                DetectionHotkeyConfig? still = _controller.GlobalHotkey;
                ShowWarning($"{hotkey} can't be the Global preset hotkey: {globalError}\n\n" +
                            (still is null ? "The Global preset still has no hotkey." : $"The Global preset hotkey is still {still}."));
                return false;
            }

            GlobalHotkeyNote = $"Hotkey set to {hotkey}.";
            GlobalHotkeyNoteProblem = StatusAfter($"Global preset hotkey: {hotkey}.");
            return true;
        }

        string? error = _controller.UpdateHotkey(hotkey); // registers first; restores the previous one on failure
        if (error is not null)
        {
            HotkeyNote = "Hotkey unchanged.";
            ShowWarning($"{hotkey} can't be the detection hotkey: {error}\n\n" +
                        $"The detection hotkey is still {_controller.Config.DetectionHotkey}.");
            return false;
        }

        HotkeyNote = $"Hotkey set to {hotkey}.";
        HotkeyNoteProblem = StatusAfter($"Detection hotkey: {hotkey}.");
        return true;
    }

    /// <summary>
    /// A key pressed while a hotkey is captured (the windows' PreviewKeyDown). Tab and Shift+Tab end the capture and
    /// move focus as usual (not used: no keyboard trap); a bare modifier waits for its key; Enter or Space on the
    /// capture button itself (it reads "Cancel" meanwhile) cancel silently, like Esc; any other key is captured
    /// (<see cref="CaptureHotkey"/>). Returns whether the key was used (the window marks it handled).
    /// </summary>
    public bool HandleCaptureKey(Key key, ModifierKeys modifiers, bool onCaptureButton)
    {
        if (!IsCapturingHotkey)
        {
            return false;
        }

        if (key == Key.Tab)
        {
            CancelHotkeyCapture();
            return false;
        }

        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
        {
            return true;
        }

        if (onCaptureButton && key is Key.Enter or Key.Space)
        {
            CancelHotkeyCapture();
            return true;
        }

        CaptureHotkey(key, modifiers);
        return true;
    }

    /// <summary>The command is a capture button's (Change hotkey / Cancel, for either hotkey).</summary>
    public bool IsCaptureCommand(ICommand? command) =>
        command is not null && (ReferenceEquals(command, BeginHotkeyCaptureCommand) || ReferenceEquals(command, BeginGlobalHotkeyCaptureCommand));

    /// <summary>Disables the Global preset hotkey.</summary>
    public void ClearGlobalHotkey()
    {
        CancelHotkeyCapture();
        _controller.ClearGlobalHotkey();
        GlobalHotkeyNote = "Global preset hotkey cleared.";
        GlobalHotkeyNoteProblem = StatusAfter("Global preset hotkey cleared.");
    }

    /// <summary>
    /// What the controller reported along with a Settings action whose own sentence is <paramref name="lead"/>: its
    /// status line starts with that sentence, and whatever follows (a problem, or a notice to act on) is returned.
    /// Null when nothing follows, or when something else already replaced the status.
    /// </summary>
    private string? StatusAfter(string lead)
    {
        string status = _controller.StatusMessage;
        if (!status.StartsWith(lead, StringComparison.Ordinal))
        {
            return null;
        }

        string rest = status[lead.Length..].Trim();
        return rest.Length == 0 ? null : rest;
    }

    private void InitializeSettings()
    {
        BrowseDbdCommand = new RelayCommand(() => BrowseDbd());
        BrowseReshadeCommand = new RelayCommand(() => BrowseReshade());
        ResetSettingsCommand = new RelayCommand(() => ResetSettings());
        ConfirmResetCommand = new RelayCommand(() => ConfirmResetSettings());
        CancelResetCommand = new RelayCommand(() => IsResetConfirming = false);
        BeginHotkeyCaptureCommand = new RelayCommand(() =>
        {
            if (HotkeyCaptureMode)
            {
                CancelHotkeyCapture();
            }
            else
            {
                BeginHotkeyCapture();
            }
        });
        BeginGlobalHotkeyCaptureCommand = new RelayCommand(() =>
        {
            if (GlobalHotkeyCaptureMode)
            {
                CancelHotkeyCapture();
            }
            else
            {
                BeginGlobalHotkeyCapture();
            }
        });
        _clearGlobalHotkeyCommand = new RelayCommand(() => ClearGlobalHotkey(), () => HasGlobalHotkey);
        _playSoundCommand = new RelayCommand(() => PlaySoundPreview(), () => SoundEnabled);
        RunDiagnosticsCommand = new RelayCommand(() => RunDiagnostics());
        _copyDiagnosticsCommand = new RelayCommand(() => CopyDiagnostics(), () => DiagnosticsLines.Count > 0);
    }

    private void RefreshSettings()
    {
        var c = _controller;
        DetectionHotkeyConfig? globalHotkey = c.GlobalHotkey;
        HasGlobalHotkey = globalHotkey is not null;
        GlobalHotkeyText = globalHotkey?.ToString() ?? "Not set";
        DetectionHotkeyProblem = c.HotkeyError;
        GlobalHotkeyProblem = c.GlobalHotkeyError;

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

        if (_animationsEnabled != c.AnimationsEnabled)
        {
            _animationsEnabled = c.AnimationsEnabled;
            RaisePropertyChanged(nameof(AnimationsEnabled));
        }

        if (_soundEnabled != c.SoundEnabled)
        {
            _soundEnabled = c.SoundEnabled;
            RaisePropertyChanged(nameof(SoundEnabled));
            _playSoundCommand.RaiseCanExecuteChanged();
        }

        double stored = Math.Round(c.SoundVolume * 100);
        if (!_soundVolumeDirty && !stored.Equals(_soundVolumePercent))
        {
            _soundVolumePercent = stored;
            RaisePropertyChanged(nameof(SoundVolumePercent));
            RaisePropertyChanged(nameof(SoundVolumeText));
        }

        if (!_soundProblemChecked)
        {
            // it does not change once the sound is loaded, and asking takes the sound player's lock: ask once
            _soundProblemChecked = true;
            SoundProblem = c.GreatSoundProblem();
        }

        DbdPath = string.IsNullOrWhiteSpace(c.Config.DbdExePath) ? "Not detected" : c.Config.DbdExePath;
        ReshadePath = string.IsNullOrWhiteSpace(c.Config.ReshadeIniPath) ? "Not detected" : c.Config.ReshadeIniPath;
        int realms = c.Database.Realms.Count;
        int maps = c.Database.Realms.Sum(r => r.Maps.Count);
        DatabaseVersionText = c.Database.Version.ToString(System.Globalization.CultureInfo.InvariantCulture);
        DatabaseSummary = $"{realms} {(realms == 1 ? "realm" : "realms")} and {maps} {(maps == 1 ? "map" : "maps")}";

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
    }
}
