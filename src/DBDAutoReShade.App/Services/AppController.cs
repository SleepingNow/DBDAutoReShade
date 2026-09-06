using DBDAutoReShade.Core.Models;
using DBDAutoReShade.Core.Services;
using DBDAutoReShade.Desktop.Services;
using System.IO;

namespace DBDAutoReShade.App.Services;

/// <summary>
/// Application glue: DBD monitor → hotkey → detection → lifecycle → ReShade
/// activation, plus preset management and configuration. UI-agnostic except
/// for the <see cref="Changed"/> notification (subscribers marshal to the UI
/// thread). Core decision logic stays in the testable Core services.
/// </summary>
public sealed class AppController : IDisposable
{
    private readonly AppConfigService _configService;
    private readonly RealmDatabaseService _database;
    private readonly RuntimeStateService _states;
    private readonly IDbdMonitor _dbd;
    private readonly GlobalHotkeyService _hotkey;
    private readonly DetectionOrchestrator _detector;
    private readonly MatchLifecycleService _lifecycle;
    private readonly PresetActivationService _activator;
    private readonly PresetDecisionService _decisions;
    private readonly ReshadeConfigurationService _reshadeConfig;
    private readonly TimeSpan _retryInterval;
    private readonly int _maxActivationAttempts;
    private readonly int _verifyDelayMs;
    private readonly CancellationTokenSource _shutdownCts = new();

    private readonly object _gate = new();
    private bool _disposed;
    private bool _detectionInFlight;
    private CancellationTokenSource? _scanCts;

    private string? _pendingRealm;
    private string? _pendingPreset;
    private bool _pendingIsGlobal;
    private int _activationAttempts;
    private int _activationGeneration;

    private string? _overrideRealm;
    private DetectionResult? _lastDetection;
    private ActivationAttempt? _lastActivation;
    private string _statusMessage = "Starting…";
    private string _detectionText = "Waiting for DBD…";
    private string? _activePresetPath;
    private string? _activeRealm;
    private bool _needsDbdRestartForShortcuts;
    private string? _hotkeyError;

    public AppController(
        AppConfigService configService,
        RealmDatabaseService database,
        RuntimeStateService states,
        IDbdMonitor dbd,
        GlobalHotkeyService hotkey,
        DetectionOrchestrator detector,
        MatchLifecycleService lifecycle,
        PresetActivationService activator,
        PresetDecisionService decisions,
        ReshadeConfigurationService reshadeConfig,
        TimeSpan? activationRetryInterval = null,
        int maxActivationAttempts = 12,
        int presetVerifyDelayMs = 2000)
    {
        _configService = configService;
        _database = database;
        _states = states;
        _dbd = dbd;
        _hotkey = hotkey;
        _detector = detector;
        _lifecycle = lifecycle;
        _activator = activator;
        _decisions = decisions;
        _reshadeConfig = reshadeConfig;
        _retryInterval = activationRetryInterval ?? TimeSpan.FromSeconds(5);
        _maxActivationAttempts = Math.Max(1, maxActivationAttempts);
        _verifyDelayMs = Math.Max(0, presetVerifyDelayMs);
    }

    public event EventHandler? Changed;

    public AppConfig Config => _configService.Config;

    public RealmDatabaseService Database => _database;

    public RuntimeStateService States => _states;

    public bool DbdRunning => _dbd.IsRunning;

    public IntPtr DbdWindow => _dbd.MainWindowHandle;

    public DetectionResult? LastDetection => _lastDetection;

    public string LastActivationText => _lastActivation is null
        ? "—"
        : $"{_lastActivation.At:HH:mm:ss} {_lastActivation.What} via {_lastActivation.Key}: " +
          $"{(_lastActivation.Success ? "OK" : "FAILED")} — {_lastActivation.Message}";

    public string StatusMessage => _statusMessage;

    public string DetectionText => _detectionText;

    public string? ActivePresetPath => _activePresetPath;

    public string? ActiveRealm => _activeRealm;

    public bool IsManualOverride => _overrideRealm is not null;

    public string? OverrideRealm => _overrideRealm;

    public bool NeedsDbdRestartForShortcuts => _needsDbdRestartForShortcuts;

    public string? HotkeyError => _hotkeyError;

    public string DebugDirectory =>
        Path.Combine(AppConfigService.DefaultAppDirectory(), "debug-crops");

    public void Initialize()
    {
        _states.StateChanged += (_, _) => RaiseChanged();
        _dbd.StatusChanged += (_, e) => OnDbdStatus(e.IsRunning);
        _hotkey.HotkeyPressed += (_, _) => _ = OnDetectionHotkeyAsync();
        _lifecycle.PhaseChanged += (_, p) => OnLifecyclePhase(p);
        _detector.SampleTaken += (_, n) =>
        {
            _detectionText = $"Scanning… press F8 to stop ({n})";
            RaiseChanged();
        };

        if (_dbd.IsRunning)
        {
            _states.TryTransitionTo(AppState.Ready);
            _detectionText = $"Ready — Press {_configService.Config.DetectionHotkey}";
        }
        else
        {
            _states.ResetTo(AppState.WaitingForDbd);
            _detectionText = "Waiting for DBD…";
            _statusMessage = "Launch Dead by Daylight.";
        }

        _dbd.Start();
        RegisterHotkey();
        _lifecycle.Start(() => _dbd.MainWindowHandle, () => _dbd.IsRunning);

        // Adopt a discovered DBD/ReShade path when the user has none configured.
        AdoptDiscoveredPaths();
        RaiseChanged();
    }

    public void Shutdown()
    {
        try
        {
            _configService.Save();
        }
        catch
        {
            // Best effort on exit.
        }

        _dbd.Stop();
        _hotkey.Stop();
        _lifecycle.Stop();
        CancelScanLocked();
        try
        {
            _shutdownCts.Cancel();
        }
        catch
        {
            // Best effort on exit.
        }
    }

    /// <summary>Cancel the running toggle scan, if any. Takes the CTS under lock, cancels outside.</summary>
    private void CancelScanLocked()
    {
        CancellationTokenSource? scanToStop;
        lock (_gate)
        {
            scanToStop = _scanCts;
            _scanCts = null;
        }

        if (scanToStop is not null)
        {
            try
            {
                scanToStop.Cancel();
            }
            catch
            {
                // Best effort.
            }
        }
    }

    // ---------- Detection (global hotkey toggle) ----------

    private Task OnDetectionHotkeyAsync() => TriggerDetectionAsync();

    /// <summary>
    /// Global hotkey toggle. First press starts an open-ended loading-screen
    /// scan (no timeout — it keeps sampling until it confidently reads the map);
    /// pressing again stops the running scan. A confident read stops the scan
    /// automatically and flows into pending/activation as before.
    /// </summary>
    public async Task TriggerDetectionAsync()
    {
        CancellationTokenSource? toStop = null;
        CancellationToken scanToken = CancellationToken.None;
        CancellationTokenSource? myCts = null;
        bool start = false;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (_detectionInFlight)
            {
                // Toggle OFF: stop the running scan; the scan task itself
                // reports "stopped" and returns to Ready.
                toStop = _scanCts;
                _scanCts = null;
            }
            else if (!_dbd.IsRunning)
            {
                _statusMessage = "Dead by Daylight is not running.";
                _detectionText = "Waiting for DBD…";
            }
            else if (_states.State == AppState.DetectingLoadingScreen)
            {
                _statusMessage = "Already scanning — press F8 again to stop.";
            }
            else if (!_states.TryTransitionTo(AppState.DetectingLoadingScreen))
            {
                _statusMessage = $"Busy ({_states.State}); wait a moment and press {_configService.Config.DetectionHotkey} again.";
            }
            else
            {
                _detectionInFlight = true;
                start = true;
                _overrideRealm = null; // a fresh detection supersedes any override
                ClearPendingLocked(); // supersede any previous pending + kill its retry loop
                _lastDetection = null;
                myCts = new CancellationTokenSource();
                _scanCts = myCts;
                scanToken = myCts.Token;
                _detectionText = "Scanning… press F8 to stop";
                _statusMessage = "Reading the loading screen… press F8 again to stop.";
            }
        }

        if (toStop is not null)
        {
            try
            {
                toStop.Cancel();
            }
            catch
            {
                // Best effort; the scan task observes cancellation itself.
            }

            RaiseChanged();
            return;
        }

        if (!start)
        {
            RaiseChanged();
            return;
        }

        RaiseChanged();
        try
        {
            DetectionResult result = await _detector.DetectAsync(
                _dbd.MainWindowHandle,
                debugMode: () => _configService.Config.DebugMode,
                debugDirectory: DebugDirectory,
                continuous: true,
                ct: scanToken).ConfigureAwait(false);

            lock (_gate)
            {
                _lastDetection = result;
                if (result.WasStopped)
                {
                    ClearPendingLocked();
                    if (_states.State == AppState.DetectingLoadingScreen)
                    {
                        _states.TryTransitionTo(AppState.Ready);
                    }

                    _detectionText = _dbd.IsRunning ? $"Ready — Press {_configService.Config.DetectionHotkey}" : "Waiting for DBD…";
                    _statusMessage = "Scan stopped.";
                }
                else if (result.Success && !string.IsNullOrWhiteSpace(result.RealmName))
                {
                    PresetDecision decision = _decisions.DecideForDetection(
                        new MapMatchResult(true, result.MapName, result.RealmName, result.Confidence, MatchMethod.MapName),
                        _configService.Config);
                    if (string.IsNullOrWhiteSpace(decision.PresetPath))
                    {
                        // Nothing is assigned at all: do not pend pointless retries.
                        ClearPendingLocked();
                        _states.TryTransitionTo(AppState.Ready);
                        _detectionText = $"Complete — {result.RealmName} (no preset assigned)";
                        _statusMessage = $"Detected {result.RealmName ?? "a realm"}, but no preset is assigned " +
                            "and no Global preset is set. Assign presets under the Presets tab.";
                        RaiseChanged();
                        return;
                    }

                    _pendingRealm = result.RealmName;
                    _pendingPreset = decision.PresetPath;
                    _pendingIsGlobal = decision.IsGlobal;
                    _activationAttempts = 0;
                    _states.TryTransitionTo(AppState.RealmIdentified);
                    _detectionText = $"Complete — {result.RealmName}";
                    _statusMessage = result.MapName is not null
                        ? $"Detected {result.MapName} → {result.RealmName}."
                        : $"Detected realm {result.RealmName}.";
                    _states.TryTransitionTo(AppState.WaitingForGameplay);
                    _lifecycle.ArmForTrial();
                    StartActivationRetriesLocked();
                }
                else
                {
                    ClearPendingLocked();
                    _states.TryTransitionTo(AppState.Ready);
                    _detectionText = "No confident map detected — Global preset";
                    _statusMessage = result.FailureReason ?? "Detection failed — Global preset retained.";
                }
            }

            RaiseChanged();

            // Try immediate activation (works when pressed after gameplay began
            // or when the loading screen is still up and DBD is focused).
            TryActivatePending();
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                _states.TryTransitionTo(AppState.Error);
                _detectionText = "Detection error";
                _statusMessage = $"Detection error: {ex.Message}";
            }

            RaiseChanged();
        }
        finally
        {
            lock (_gate)
            {
                _detectionInFlight = false;
                if (ReferenceEquals(_scanCts, myCts))
                {
                    _scanCts = null; // our scan ended; a newer toggle already replaced it otherwise
                }
            }

            try
            {
                myCts?.Dispose();
            }
            catch
            {
                // Best effort.
            }
        }
    }

    private void TryActivatePending()
    {
        RetryPendingActivation();
    }

    /// <summary>
    /// Performs one activation attempt for the pending realm. Returns true
    /// when the preset was activated (or there is nothing pending).
    /// Safe to call repeatedly: attempts are capped, and every failure
    /// explains the most likely fix (focus, shortcuts, ReShade.ini).
    /// </summary>
    public bool RetryPendingActivation()
    {
        string? realm;
        string? preset;
        bool exhausted;
        lock (_gate)
        {
            if (_disposed || _states.State != AppState.WaitingForGameplay || _pendingPreset is null)
            {
                return true; // nothing to do
            }

            if (_activationAttempts >= _maxActivationAttempts)
            {
                _statusMessage = "Could not switch presets after several tries. " +
                    "Click into DBD so it is focused (or press Test on the preset), then press " +
                    $"{_configService.Config.DetectionHotkey} to retry.";
                RaiseChanged();
                return false;
            }

            _activationAttempts++;
            exhausted = _activationAttempts >= _maxActivationAttempts;
            realm = _pendingRealm;
            preset = _pendingPreset;
        }

        ReshadeShortcutConfig? shortcut;
        lock (_gate)
        {
            shortcut = LookupShortcut(_pendingIsGlobal ? AppConfigKeys.GlobalShortcutKey : realm);
        }

        ActivationResult result = _activator.Activate(preset!, shortcut, _dbd.MainWindowHandle, _dbd.IsRunning);

        lock (_gate)
        {
            if (_states.State != AppState.WaitingForGameplay || _pendingPreset is null)
            {
                return true; // superseded while attempting
            }

            RecordActivationLocked($"Realm {realm} →", preset, shortcut, result);
            if (result.Success)
            {
                _activePresetPath = preset;
                _activeRealm = realm;
                ClearPendingLocked();
                _states.TryTransitionTo(AppState.PresetActive);
                _statusMessage = $"Active preset: {Path.GetFileName(preset)} ({realm}).";
                _lifecycle.NotifyTrialStarted();
                ScheduleSwitchVerification(preset, realm);
            }
            else if (exhausted)
            {
                _statusMessage = $"Could not switch presets: {result.Message} Press " +
                    $"{_configService.Config.DetectionHotkey} to retry once DBD is focused.";
            }
            else
            {
                _statusMessage = $"Waiting for gameplay to activate {Path.GetFileName(preset)}… ({result.Message})";
            }
        }

        RaiseChanged();
        return result.Success;
    }

    /// <summary>
    /// Background retry loop: keeps attempting activation while a realm is
    /// pending (DBD often isn't focused the second detection finishes).
    /// Invalidated by generation whenever a new cycle starts or pending clears.
    /// </summary>
    private void StartActivationRetriesLocked()
    {
        int generation = ++_activationGeneration;
        CancellationToken token = _shutdownCts.Token;
        _ = Task.Run(async () =>
        {
            for (; ; )
            {
                try
                {
                    await Task.Delay(_retryInterval, token).ConfigureAwait(false);
                }
                catch
                {
                    break; // shutting down
                }

                lock (_gate)
                {
                    if (_disposed || generation != _activationGeneration ||
                        _states.State != AppState.WaitingForGameplay || _pendingPreset is null ||
                        _activationAttempts >= _maxActivationAttempts)
                    {
                        break;
                    }
                }

                if (RetryPendingActivation())
                {
                    break;
                }
            }
        }, token);
    }

    /// <summary>Clears pending state and invalidates any running retry loop. Call with _gate held.</summary>
    private void ClearPendingLocked()
    {
        _pendingRealm = null;
        _pendingPreset = null;
        _activationGeneration++;
    }

    /// <summary>
    /// Confirms the switch actually landed: ReShade saves its config (including
    /// the new PresetPath) in the same frame it processes a preset shortcut, so
    /// reading the ini back shortly after distinguishes "keys ignored" from
    /// "switched but not visibly different". Never blocks; superseded checks
    /// simply do nothing.
    /// </summary>
    private void ScheduleSwitchVerification(string expectedPreset, string? realm)
    {
        string? iniPath;
        lock (_gate)
        {
            iniPath = Config.ReshadeIniPath;
        }

        if (string.IsNullOrWhiteSpace(iniPath))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(_verifyDelayMs, _shutdownCts.Token).ConfigureAwait(false);
            }
            catch
            {
                return; // shutting down
            }

            bool confirmed = false;
            try
            {
                string? current = ReshadeConfigurationService.ReadCurrentPresetAbsolute(iniPath);
                confirmed = string.Equals(
                    PresetValidationService.NormalizePresetPath(current ?? string.Empty),
                    PresetValidationService.NormalizePresetPath(expectedPreset),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                // Read-back is best-effort confirmation only.
            }

            string name = Path.GetFileName(expectedPreset);
            lock (_gate)
            {
                if (_disposed || _states.State != AppState.PresetActive ||
                    !string.Equals(_activePresetPath, expectedPreset, StringComparison.OrdinalIgnoreCase))
                {
                    return; // superseded
                }

                _statusMessage = confirmed
                    ? $"Active preset: {name} ({realm}) — confirmed by ReShade."
                    : $"Keys sent for {name}, but ReShade hasn't confirmed the switch. " +
                      "Open the ReShade overlay (Home key) to check the active preset; " +
                      "if it didn't change, restart DBD so ReShade loads the shortcuts.";
            }

            RaiseChanged();
        });
    }

    // ---------- Lifecycle ----------

    private void OnLifecyclePhase(LifecyclePhase phase)
    {
        if (phase == LifecyclePhase.InTrial)
        {
            TryActivatePending();
        }
        else if (phase == LifecyclePhase.BackToMenus)
        {
            ReturnToGlobalAfterMatch();
        }

        RaiseChanged();
    }

    private void ReturnToGlobalAfterMatch()
    {
        bool shouldRestore;
        string? globalPreset;
        lock (_gate)
        {
            shouldRestore = _configService.Config.AutoRestoreGlobalOnMenu &&
                (_states.State == AppState.PresetActive ||
                 _states.State == AppState.ManualOverride ||
                 _states.State == AppState.WaitingForGameplay);
            globalPreset = _configService.Config.GlobalPresetPath;
        }

        if (!shouldRestore)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(globalPreset))
        {
            ReshadeShortcutConfig? globalShortcut = LookupShortcut(AppConfigKeys.GlobalShortcutKey);
            ActivationResult result = _activator.Activate(
                globalPreset, globalShortcut, _dbd.MainWindowHandle, _dbd.IsRunning);
            lock (_gate)
            {
                RecordActivationLocked("Match end → Global", globalPreset, globalShortcut, result);
                _statusMessage = result.Success
                    ? "Match ended — Global preset restored."
                    : $"Match ended — Global preset restore deferred ({result.Message}).";
            }
        }

        lock (_gate)
        {
            ClearPendingLocked();
            _overrideRealm = null;
            _activePresetPath = globalPreset;
            _activeRealm = null;
            _states.TryTransitionTo(AppState.ReturningToMenu);
            _states.TryTransitionTo(AppState.Ready);
            _detectionText = _dbd.IsRunning ? $"Ready — Press {_configService.Config.DetectionHotkey}" : "Waiting for DBD…";
            _lifecycle.Disarm();
        }

        RaiseChanged();
    }

    private void OnDbdStatus(bool running)
    {
        CancellationTokenSource? scanToStop = null;
        lock (_gate)
        {
            if (!running)
            {
                // Stop any running scan: no window to read from anymore.
                scanToStop = _scanCts;
                _scanCts = null;
                ClearPendingLocked();
                _overrideRealm = null;
                _states.TryTransitionTo(AppState.WaitingForDbd);
                _detectionText = "Waiting for DBD…";
                _statusMessage = "Launch Dead by Daylight.";
                _lifecycle.Disarm();
            }
            else
            {
                if (_states.State == AppState.WaitingForDbd)
                {
                    _states.TryTransitionTo(AppState.Ready);
                }

                _needsDbdRestartForShortcuts = false; // fresh process loads current shortcuts
                _detectionText = $"Ready — Press {_configService.Config.DetectionHotkey}";
                _statusMessage = "Dead by Daylight detected.";
                AdoptDiscoveredPaths();
            }
        }

        if (scanToStop is not null)
        {
            try
            {
                scanToStop.Cancel();
            }
            catch
            {
                // Best effort.
            }
        }

        RaiseChanged();
    }

    // ---------- Manual override ----------

    public void ApplyManualOverride(string? realm)
    {
        PresetDecision decision;
        lock (_gate)
        {
            decision = _decisions.DecideManualOverride(realm, _configService.Config);
            if (!_states.TryTransitionTo(AppState.ManualOverride))
            {
                _statusMessage = $"Cannot override right now (state: {_states.State}).";
                RaiseChanged();
                return;
            }

            _overrideRealm = realm;
            ClearPendingLocked();
        }

        ActivationResult result = string.IsNullOrWhiteSpace(decision.PresetPath)
            ? new ActivationResult(true, "No preset configured.")
            : _activator.Activate(decision.PresetPath,
                LookupShortcut(decision.IsGlobal ? AppConfigKeys.GlobalShortcutKey : realm),
                _dbd.MainWindowHandle, _dbd.IsRunning);

        ReshadeShortcutConfig? usedShortcut = string.IsNullOrWhiteSpace(decision.PresetPath)
            ? null
            : LookupShortcut(decision.IsGlobal ? AppConfigKeys.GlobalShortcutKey : realm);
        lock (_gate)
        {
            RecordActivationLocked($"Override {realm ?? "—"} →", decision.PresetPath, usedShortcut, result);
            if (!string.IsNullOrWhiteSpace(decision.PresetPath))
            {
                _activePresetPath = decision.PresetPath;
                _activeRealm = realm;
            }

            _statusMessage = result.Success
                ? $"Manual override: {decision.Reason}"
                : $"Manual override selected ({decision.Reason}) — activation deferred: {result.Message}";
        }

        RaiseChanged();
    }

    public void ReturnToAutomatic()
    {
        lock (_gate)
        {
            _overrideRealm = null;
            ClearPendingLocked();
            if (_states.State == AppState.ManualOverride)
            {
                _states.TryTransitionTo(AppState.Ready);
            }
        }

        string? global = _configService.Config.GlobalPresetPath;
        if (!string.IsNullOrWhiteSpace(global))
        {
            ReshadeShortcutConfig? globalShortcut = LookupShortcut(AppConfigKeys.GlobalShortcutKey);
            ActivationResult result = _activator.Activate(global, globalShortcut,
                _dbd.MainWindowHandle, _dbd.IsRunning);
            lock (_gate)
            {
                RecordActivationLocked("Return to automatic →", global, globalShortcut, result);
                _activePresetPath = global;
                _activeRealm = null;
                _statusMessage = result.Success ? "Returned to automatic (Global preset)." : $"Automatic restored — activation deferred: {result.Message}";
                _detectionText = _dbd.IsRunning ? $"Ready — Press {_configService.Config.DetectionHotkey}" : "Waiting for DBD…";
            }
        }
        else
        {
            lock (_gate)
            {
                _statusMessage = "Returned to automatic.";
            }
        }

        RaiseChanged();
    }

    // ---------- Preset management ----------

    public string? TestRealmPreset(string realm)
    {
        string? preset;
        lock (_gate)
        {
            if (!Config.RealmPresets.TryGetValue(realm, out preset) || string.IsNullOrWhiteSpace(preset))
            {
                return $"Realm '{realm}' has no preset assigned.";
            }
        }

        ActivationResult result = _activator.Activate(preset, LookupShortcut(realm),
            _dbd.MainWindowHandle, _dbd.IsRunning);
        lock (_gate)
        {
            RecordActivationLocked($"Test {realm} →", preset, LookupShortcut(realm), result);
            _statusMessage = result.Success ? $"Testing {Path.GetFileName(preset)}." : $"Test failed: {result.Message}";
            if (result.Success)
            {
                _activePresetPath = preset;
                _activeRealm = realm;
            }
        }

        RaiseChanged();
        return result.Success ? null : result.Message;
    }

    public string? TestGlobalPreset()
    {
        string? global = Config.GlobalPresetPath;
        if (string.IsNullOrWhiteSpace(global))
        {
            return "No Global preset is configured.";
        }

        ActivationResult result = _activator.Activate(global, LookupShortcut(AppConfigKeys.GlobalShortcutKey),
            _dbd.MainWindowHandle, _dbd.IsRunning);
        lock (_gate)
        {
            RecordActivationLocked("Test Global →", global, LookupShortcut(AppConfigKeys.GlobalShortcutKey), result);
            _statusMessage = result.Success ? $"Testing {Path.GetFileName(global)}." : $"Test failed: {result.Message}";
            if (result.Success)
            {
                _activePresetPath = global;
                _activeRealm = null;
            }
        }

        RaiseChanged();
        return result.Success ? null : result.Message;
    }

    /// <summary>Validate + assign a realm preset (null/empty clears). Returns an error message or null.</summary>
    public string? SetRealmPreset(string realm, string? presetPath)
    {
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(presetPath))
            {
                Config.RealmPresets.Remove(realm);
                SyncShortcutsLocked();
                _configService.Save();
                _statusMessage = $"Cleared preset for {realm} (uses Global).";
                RaiseChanged();
                return null;
            }

            var validation = PresetValidationService.ValidatePresetFile(presetPath);
            if (!validation.IsValid)
            {
                return validation.Message;
            }

            string? duplicate = PresetValidationService.FindDuplicateRealm(presetPath, realm, Config.RealmPresets);
            if (duplicate is not null)
            {
                return $"This preset is already assigned to {duplicate}.";
            }

            Config.RealmPresets[realm] = PresetValidationService.NormalizePresetPath(presetPath);
            SyncShortcutsLocked();
            _configService.Save();
            _statusMessage = $"Assigned {Path.GetFileName(presetPath)} to {realm}."
                + (_needsDbdRestartForShortcuts ? " Restart DBD so ReShade loads the new shortcut." : string.Empty);
            RaiseChanged();
            return null;
        }
    }

    public string? SetGlobalPreset(string? presetPath)
    {
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(presetPath))
            {
                Config.GlobalPresetPath = null;
                SyncShortcutsLocked();
                _configService.Save();
                RaiseChanged();
                return null;
            }

            var validation = PresetValidationService.ValidatePresetFile(presetPath);
            if (!validation.IsValid)
            {
                return validation.Message;
            }

            Config.GlobalPresetPath = PresetValidationService.NormalizePresetPath(presetPath);
            SyncShortcutsLocked();
            _configService.Save();
            _statusMessage = $"Global preset: {Path.GetFileName(presetPath)}."
                + (_needsDbdRestartForShortcuts ? " Restart DBD so ReShade loads the new shortcut." : string.Empty);
            RaiseChanged();
            return null;
        }
    }

    // ---------- Settings ----------

    public string? UpdateHotkey(DetectionHotkeyConfig hotkey)
    {
        lock (_gate)
        {
            Config.DetectionHotkey = hotkey;
            _configService.Save();
        }

        RegisterHotkey();
        lock (_gate)
        {
            if (_hotkeyError is null && _dbd.IsRunning && _states.State == AppState.Ready)
            {
                _detectionText = $"Ready — Press {hotkey}";
            }
        }

        RaiseChanged();
        return _hotkeyError;
    }

    public void SetDbdPath(string? path)
    {
        lock (_gate)
        {
            Config.DbdExePath = string.IsNullOrWhiteSpace(path) ? null : path;
            RefreshReshadeIniFromDbdLocked();
            _configService.Save();
            RaiseChanged();
        }
    }

    public void SetReshadeIni(string? path)
    {
        lock (_gate)
        {
            Config.ReshadeIniPath = string.IsNullOrWhiteSpace(path) ? null : path;
            SyncShortcutsLocked();
            _configService.Save();
            RaiseChanged();
        }
    }

    public void SetDebug(bool enabled)
    {
        lock (_gate)
        {
            Config.DebugMode = enabled;
            _configService.Save();
            RaiseChanged();
        }
    }

    public void SetAutoRestore(bool enabled)
    {
        lock (_gate)
        {
            Config.AutoRestoreGlobalOnMenu = enabled;
            _configService.Save();
            RaiseChanged();
        }
    }

    public void SetManifestUrl(string? url)
    {
        lock (_gate)
        {
            Config.DatabaseUpdateManifestUrl = string.IsNullOrWhiteSpace(url) ? null : url.Trim();
            _configService.Save();
            RaiseChanged();
        }
    }

    public void CompleteSetup()
    {
        lock (_gate)
        {
            Config.SetupCompleted = true;
            _configService.Save();
            RaiseChanged();
        }
    }

    public void ResetSettings()
    {
        lock (_gate)
        {
            _configService.Reset();
            ClearPendingLocked();
            _overrideRealm = null;
            _activePresetPath = null;
            _activeRealm = null;
            _needsDbdRestartForShortcuts = false;
        }

        RegisterHotkey();
        RaiseChanged();
    }

    // ---------- Internals ----------

    private void RecordActivationLocked(string what, string? presetPath, ReshadeShortcutConfig? shortcut, ActivationResult result)
    {
        string key = shortcut is null ? "(no shortcut assigned)" : ReshadeConfigurationService.FormatShortcut(shortcut);
        string name = string.IsNullOrWhiteSpace(presetPath) ? "(no preset)" : Path.GetFileName(presetPath);
        _lastActivation = new ActivationAttempt(DateTime.Now, $"{what} {name}", key, result.Success, result.Message);
    }

    /// <summary>
    /// One-click diagnostics: ReShade setup verification plus live runtime facts
    /// (DBD running/focused, hotkey, last activation attempt).
    /// </summary>
    public string RunReshadeDiagnostics()
    {
        var (allOk, report) = ReshadeDiagnosticsService.BuildReport(
            Config, Database.Realms.Select(r => r.Name).ToList());
        bool focused = false;
        IntegrityLevel ownLevel = IntegrityLevel.Unknown;
        IntegrityLevel gameLevel = IntegrityLevel.Unknown;
        try
        {
            focused = DbdWindowService.IsForeground(_dbd.MainWindowHandle);
        }
        catch
        {
            // Best effort.
        }

        try
        {
            ownLevel = ProcessIntegrity.GetForCurrentProcess();
            if (_dbd.ProcessId is int pid)
            {
                gameLevel = ProcessIntegrity.GetForProcess(pid);
            }
        }
        catch
        {
            // Best effort.
        }

        string integrityLine = $"This app integrity: {ProcessIntegrity.Describe(ownLevel)}, " +
            $"DBD integrity: {(DbdRunning ? ProcessIntegrity.Describe(gameLevel) : "(not running)")}";
        if (DbdRunning && ownLevel != IntegrityLevel.Unknown && gameLevel != IntegrityLevel.Unknown &&
            gameLevel > ownLevel)
        {
            integrityLine += " — MISMATCH: Windows blocks this app's keystrokes to the game. " +
                "Run both the same way (preferably both normally, not as admin).";
        }

        return report + Environment.NewLine + string.Join(Environment.NewLine, new[]
        {
            string.Empty,
            $"DBD running: {(DbdRunning ? "yes" : "no")}, focused: {(focused ? "yes" : "no")}",
            integrityLine,
            $"Detection hotkey: {Config.DetectionHotkey}",
            $"Last activation: {LastActivationText}",
            $"Overall: {(allOk ? "setup looks correct" : "problems found — see [!!] lines")}"
        });
    }

    private ReshadeShortcutConfig? LookupShortcut(string? realmOrGlobal)
    {
        lock (_gate)
        {
            if (realmOrGlobal is not null && Config.ReshadeShortcuts.TryGetValue(realmOrGlobal, out var s))
            {
                return s;
            }

            return null;
        }
    }

    private void RegisterHotkey()
    {
        DetectionHotkeyConfig hotkey;
        lock (_gate)
        {
            hotkey = Config.DetectionHotkey;
        }

        string? error = _hotkey.Start(hotkey);
        lock (_gate)
        {
            _hotkeyError = error ?? _hotkey.LastError;
            if (_hotkeyError is not null)
            {
                _statusMessage = $"Hotkey '{hotkey}' unavailable: {_hotkeyError}";
            }
        }

        RaiseChanged();
    }

    private void AdoptDiscoveredPaths()
    {
        // Called with _gate held.
        try
        {
            if (string.IsNullOrWhiteSpace(Config.DbdExePath))
            {
                var found = DbdDiscoveryService.Discover();
                var best = found.FirstOrDefault();
                if (best is not null)
                {
                    Config.DbdExePath = best.ExePath;
                }
            }

            // Prefer the live process path when available.
            if (_dbd.IsRunning && !string.IsNullOrWhiteSpace(_dbd.ExePath) && File.Exists(_dbd.ExePath))
            {
                Config.DbdExePath = _dbd.ExePath;
            }

            RefreshReshadeIniFromDbdLocked();
            _configService.Save();
        }
        catch
        {
            // Discovery must never break startup.
        }
    }

    private void RefreshReshadeIniFromDbdLocked()
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(Config.ReshadeIniPath) && File.Exists(Config.ReshadeIniPath))
            {
                SyncShortcutsLocked();
                return;
            }

            var reshade = ReshadeDiscoveryService.Discover(Config.DbdExePath);
            if (reshade is not null)
            {
                Config.ReshadeIniPath = reshade.IniPath;
            }

            SyncShortcutsLocked();
        }
        catch
        {
            // Best effort.
        }
    }

    /// <summary>
    /// Regenerate deterministic shortcuts for Global + all configured realms
    /// and write them into ReShade.ini. Sets the restart notice when DBD is
    /// running (ReShade only loads shortcuts at startup).
    /// </summary>
    private void SyncShortcutsLocked()
    {
        try
        {
            string? iniPath = Config.ReshadeIniPath;
            if (string.IsNullOrWhiteSpace(iniPath) || !File.Exists(iniPath))
            {
                return;
            }

            var names = new List<string>();
            if (!string.IsNullOrWhiteSpace(Config.GlobalPresetPath))
            {
                names.Add(AppConfigKeys.GlobalShortcutKey);
            }

            names.AddRange(Config.RealmPresets
                .Where(kv => !string.IsNullOrWhiteSpace(kv.Value))
                .Select(kv => kv.Key));

            var allocated = ReshadeConfigurationService.AllocateShortcuts(names);
            foreach (var kv in allocated)
            {
                Config.ReshadeShortcuts[kv.Key] = kv.Value;
            }

            // Drop shortcuts for realms that no longer have presets.
            foreach (string key in Config.ReshadeShortcuts.Keys.ToList())
            {
                if (key != AppConfigKeys.GlobalShortcutKey && !Config.RealmPresets.ContainsKey(key))
                {
                    Config.ReshadeShortcuts.Remove(key);
                }
            }

            if (string.IsNullOrWhiteSpace(Config.GlobalPresetPath))
            {
                Config.ReshadeShortcuts.Remove(AppConfigKeys.GlobalShortcutKey);
            }

            var assignments = allocated
                .Select(kv => new PresetShortcutAssignment(
                    kv.Key,
                    kv.Key == AppConfigKeys.GlobalShortcutKey ? Config.GlobalPresetPath! : Config.RealmPresets[kv.Key],
                    kv.Value))
                .ToList();

            ShortcutSyncResult sync = _reshadeConfig.SyncPresetShortcuts(
                iniPath, assignments, Config.LastSyncedPresetPaths);
            Config.LastSyncedPresetPaths = assignments
                .Select(a => PresetValidationService.NormalizePresetPath(a.PresetPath))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (sync.Changed && _dbd.IsRunning)
            {
                _needsDbdRestartForShortcuts = true;
            }
        }
        catch
        {
            // ReShade sync failures are surfaced via status, never thrown.
            _statusMessage = "Could not update ReShade shortcuts (is ReShade.ini writable?).";
        }
    }

    private void RaiseChanged()
    {
        try
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            // Subscribers must not break the controller.
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (!_disposed)
            {
                _disposed = true;
            }
        }

        CancelScanLocked();
        try
        {
            _shutdownCts.Cancel();
        }
        catch
        {
            // Best effort.
        }
    }
}

/// <summary>One recorded preset-switch attempt (shown on the Status tab).</summary>
public sealed record ActivationAttempt(DateTime At, string What, string Key, bool Success, string Message);
