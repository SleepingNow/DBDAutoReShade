using DBDAutoReShade.App.Infrastructure;
using DBDAutoReShade.Core.Models;
using DBDAutoReShade.Core.Services;
using DBDAutoReShade.Desktop.Services;
using System.IO;

namespace DBDAutoReShade.App.Services;

/// <summary>
/// Application glue: DBD monitor → hotkey → detection → ReShade activation,
/// plus preset management and configuration. UI-agnostic except for the
/// <see cref="Changed"/> notification (subscribers marshal to the UI thread).
/// Core decision logic stays in the testable Core services.
/// <para>
/// No match tracking: the app never tries to tell when gameplay starts or a
/// match ends (1.2.1 removed that screen-change heuristic after it fired in
/// the middle of a real match), and the screen is captured only while a scan
/// runs. A switched preset stays until the next switch the user causes: a new
/// scan (detection hotkey), the Global preset hotkey or "Switch to Global"
/// (<see cref="ActivateGlobalNow"/>), a manual override or Return to
/// Automatic, a Test, a scan that ends without a confident read, or DBD
/// exiting (which, with <see cref="AppConfig.StartNextGameWithGlobal"/> on,
/// makes Global ReShade's startup preset for the next launch).
/// </para>
/// <para>
/// Activation: every preset switch (detected realm, manual override, Return
/// to Automatic, Test buttons, Global preset hotkey, scan without a result)
/// becomes the ONE desired activation target. A newer request supersedes the older one.
/// It is sent as soon as DBD runs and is focused (immediately when possible,
/// otherwise by a background worker every <c>activationRetryInterval</c>)
/// until it succeeds, is superseded, fails for good, or its deadline passes.
/// Waiting for focus costs no attempts; rejected sends do (<c>maxActivationAttempts</c>
/// per request). The active preset changes only after a successful send.
/// </para>
/// <para>
/// Status line: unrelated messages go through <c>SetStatusLocked</c>, the
/// activation pipeline's through <c>SetRequestStatusLocked</c>. A pending
/// request only re-asserts its "waiting for focus" text while the line still
/// belongs to it, and a read-back verification only reports while no other
/// status was written since its send, so neither overwrites unrelated news.
/// A scan reads only while the screen shows DBD: DBD is the foreground window,
/// or no other window covers its loading-screen text (DBD in plain view on one
/// monitor while the user works on another). The capture copies the screen, so
/// anything else would read the covering window. A pause is said once, and the
/// scanning text comes back only while that notice still owns the line.
/// </para>
/// <para>
/// Shortcuts: ReShade loads its preset shortcut lists only when the game
/// starts, so the controller keeps a snapshot of what the running DBD's
/// ReShade loaded, keyed by its PID (and creation time). A new process is
/// snapshotted after the sync (ReShade reads ReShade.ini later, while the game
/// starts). The snapshot is also kept on disk next to the config until DBD
/// exits, so after an app restart a DBD that kept running is judged by what it
/// really loaded, not by the file an earlier run may have rewritten since; a
/// DBD that was already running before any run of this app saw it is
/// snapshotted before the startup sync (it loaded the file as it was). A preset
/// whose current (combo, path) is not in the snapshot is never sent: that combo
/// may switch the running ReShade to a different preset, so the request fails
/// with <see cref="ActivationFailure.RestartRequired"/>. The message says what
/// fixes it: a DBD restart, or (when the configured ReShade.ini is not set or
/// doesn't exist, which a restart alone can't fix) setting the right ReShade.ini
/// first. A loaded pair is still never sent when its combo would fire an
/// earlier loaded entry for another preset first (ReShade's "Force correct
/// modifier keys" off: held extra modifiers don't stop an entry); that request
/// fails with <see cref="ActivationFailure.ShortcutClash"/>. The check (outside
/// <c>_gate</c>) reads the option from ReShade.ini only when an entry other than
/// the preset's own exact one could fire first, and reads ReShade.ini's entries
/// when no stored snapshot describes the running process.
/// Shortcut syncs are transactional: combos are allocated stably
/// (existing combos are kept, the user's own combos are skipped), ReShade.ini
/// is written, and only then are the combos committed to the config.
/// </para>
/// <para>
/// Lock order (never acquire leftwards while holding something to the right):
/// <c>_sendLock</c> → <c>_gate</c> → <c>_iniWriteLock</c> (a leaf lock). Rules:
/// nothing waits for <c>_sendLock</c> while holding <c>_gate</c>; no screen
/// capture or key send runs under <c>_gate</c>; every ReShade.ini
/// write (shortcut sync and reset cleanup under <c>_gate</c>, the startup-preset
/// write at DBD exit without it) holds <c>_iniWriteLock</c>; the two global
/// hotkeys (detection and Global preset) are registered and released
/// (<see cref="IGlobalHotkeyService.Start"/>/<see cref="IGlobalHotkeyService.Stop"/>,
/// which join the service's pump thread, whose handler takes <c>_gate</c> for
/// the detection hotkey) only with no controller lock held; the Global preset
/// hotkey's handler only takes <c>_globalPressLock</c> (a leaf lock: nothing is
/// called while it is held) to queue the press, so its pump thread never waits
/// for a switch; the config and the stored ReShade snapshot (a small file next to it) are
/// saved, read and deleted under <c>_gate</c> (no other lock involved);
/// <see cref="MapDetected"/> is raised with no controller lock held.
/// </para>
/// <para>
/// Global preset hotkey (default F9, <see cref="AppConfig.GlobalHotkey"/>): a
/// second registration next to the detection hotkey, never the same combo, and,
/// like the detection hotkey, never one of the app's ReShade shortcut combos
/// (RegisterHotKey would swallow the app's own preset switches). Its press is
/// queued off the hotkey's pump thread and runs <see cref="ActivateGlobalNow"/>
/// on the thread pool, one press at a time, in order: a running scan stops
/// without its own "no read → Global" request, the pending target and the
/// manual override are dropped, and Global is requested through the same
/// focus-aware pipeline as every other switch.
/// </para>
/// </summary>
public sealed class AppController : IDisposable
{
    /// <summary>How long a manual override waits for DBD focus.</summary>
    public static readonly TimeSpan OverrideFocusWindow = TimeSpan.FromSeconds(60);

    /// <summary>How long Return to Automatic waits for DBD focus.</summary>
    public static readonly TimeSpan ReturnToAutomaticFocusWindow = TimeSpan.FromSeconds(60);

    /// <summary>How long a Test button waits for DBD focus.</summary>
    public static readonly TimeSpan TestFocusWindow = TimeSpan.FromSeconds(15);

    /// <summary>How long the Global switch after a scan without a confident read waits for DBD focus.</summary>
    public static readonly TimeSpan ScanWithoutResultFocusWindow = TimeSpan.FromMinutes(10);

    /// <summary>How long the Global preset hotkey's switch (<see cref="ActivateGlobalNow"/>) waits for DBD focus.</summary>
    public static readonly TimeSpan GlobalHotkeyFocusWindow = TimeSpan.FromSeconds(60);

    private const int VerifyPollMs = 250;

    private readonly AppConfigService _configService;
    private readonly RealmDatabaseService _database;
    private readonly RuntimeStateService _states;
    private readonly IDbdMonitor _dbd;
    private readonly IGlobalHotkeyService _hotkey;
    private readonly IGlobalHotkeyService _globalHotkey;
    private readonly ISoundEffects _soundEffects;
    private readonly DetectionOrchestrator _detector;
    private readonly PresetActivationService _activator;
    private readonly PresetDecisionService _decisions;
    private readonly ReshadeConfigurationService _reshadeConfig;
    private readonly TimeSpan _retryInterval;
    private readonly int _maxActivationAttempts;
    private readonly int _verifyDelayMs;
    private readonly Func<long> _clockMs;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<string?> _discoverDbdExePath;
    private readonly Func<string?, string?> _discoverReshadeIniPath;
    private readonly Func<int, DateTime?> _processStartTime;
    private readonly Func<IntPtr, bool> _isScanTextUncovered;
    private readonly ReshadeLoadedSnapshotStore _snapshotStore;
    private readonly CancellationTokenSource _shutdownCts = new();

    // Serializes every keystroke send (down/hold/up) from every source.
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    // Serializes every ReShade.ini write (leaf lock; may be taken under _gate).
    private readonly object _iniWriteLock = new();

    private readonly object _gate = new();

    // Queues Global preset hotkey presses (leaf lock: held only to chain a press).
    private readonly object _globalPressLock = new();

    // The last queued Global preset hotkey press (presses run in order on the pool).
    private Task _globalPresses = Task.CompletedTask;

    // A queued press has not started yet: a new press is folded into it.
    private bool _globalPressWaiting;
    private bool _disposed;

    // Set at the end of Initialize: DBD status events before that are ignored
    // (Initialize reads the monitor's state itself).
    private bool _initialized;
    private bool _detectionInFlight;
    private CancellationTokenSource? _scanCts;

    // While the running scan is paused (DBD not in front): the notice it put on
    // the status line, said once per pause. Null while the scan reads.
    private string? _scanPauseNotice;

    private ActivationRequest? _desired;

    // Bumped by every new request, cleared target, successful send and
    // unrelated status write: a read-back verification only reports while its
    // generation is current.
    private int _statusGeneration;

    // The request whose text the status line shows (null = an unrelated message).
    private ActivationRequest? _statusOwner;

    // The Tests' numbers, and how the newest Test that ended by itself ended (LastTestOutcome).
    private long _testSerial;
    private TestOutcome? _lastTestOutcome;
    private int? _knownProcessId;

    private string? _overrideRealm;
    private DetectionResult? _lastDetection;
    private ActivationAttempt? _lastActivation;
    private string _statusMessage = "Starting…";
    private string _detectionText = "Waiting for DBD…";
    private string? _activePresetPath;
    private string? _activeRealm;

    // ReShade.ini was seen holding _activePresetPath after the app switched to
    // it (it already did before the send, or the read-back confirmed it). False
    // while unknown: the keys went out, but ReShade may have ignored them.
    private bool _activePresetConfirmed;

    // Bumped whenever _activePresetPath is set: a read-back confirms only the
    // switch it was started for, whatever the status line shows meanwhile.
    private long _activeSwitch;
    private string? _hotkeyError;
    private string? _globalHotkeyError;

    // Both global hotkeys are released while the UI captures a new one.
    private bool _hotkeySuspended;

    // What the running DBD's ReShade loaded (null while DBD is not running);
    // mirrored to _snapshotStore so an app restart doesn't lose it.
    private ReshadeLoadedSnapshot? _reshadeLoadedSnapshot;

    // Bumped by every ResetSettings (the UI drops unsaved drafts).
    private int _settingsResetCount;

    // The last config save's error (null: it worked). Written under _gate.
    private string? _lastSaveError;

    /// <param name="activationRetryInterval">Focus-poll / retry cadence of the activation worker (default 250 ms).</param>
    /// <param name="maxActivationAttempts">Rejected sends (SendBlocked) allowed per request; waiting for focus costs none.</param>
    /// <param name="presetVerifyDelayMs">How long ReShade.ini is watched for the switch confirmation after a send.</param>
    /// <param name="monotonicClockMs">Test seam: monotonic millisecond clock for request deadlines.</param>
    /// <param name="delayAsync">Test seam: the delay used by the activation worker and the read-back verification.</param>
    /// <param name="discoverDbdExePath">Test seam: finds an installed DBD executable (default: <see cref="DbdDiscoveryService"/>).</param>
    /// <param name="discoverReshadeIniPath">Test seam: finds ReShade.ini for a DBD executable (default: <see cref="ReshadeDiscoveryService"/>).</param>
    /// <param name="processStartTime">Test seam: a process's creation time, null when unknown (default: <see cref="ProcessStartTime.TryGet"/>, query-limited access).</param>
    /// <param name="isScanTextUncovered">Test seam: whether no other window covers DBD's loading-screen text, so a scan may read a DBD that is not the foreground window (default: <see cref="DbdWindowService.IsLoadingScreenTextUncovered"/>).</param>
    /// <param name="globalPresetHotkey">The Global preset hotkey's registration, a second instance next to <paramref name="hotkey"/> (default: one that registers nothing with Windows and never fires, for tests and tools).</param>
    /// <param name="soundEffects">The app's sound effects (default: <see cref="NullSoundEffects"/>, silent).</param>
    public AppController(
        AppConfigService configService,
        RealmDatabaseService database,
        RuntimeStateService states,
        IDbdMonitor dbd,
        IGlobalHotkeyService hotkey,
        DetectionOrchestrator detector,
        PresetActivationService activator,
        PresetDecisionService decisions,
        ReshadeConfigurationService reshadeConfig,
        TimeSpan? activationRetryInterval = null,
        int maxActivationAttempts = 3,
        int presetVerifyDelayMs = 2000,
        Func<long>? monotonicClockMs = null,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null,
        Func<string?>? discoverDbdExePath = null,
        Func<string?, string?>? discoverReshadeIniPath = null,
        Func<int, DateTime?>? processStartTime = null,
        Func<IntPtr, bool>? isScanTextUncovered = null,
        IGlobalHotkeyService? globalPresetHotkey = null,
        ISoundEffects? soundEffects = null)
    {
        _configService = configService;
        _database = database;
        _states = states;
        _dbd = dbd;
        _hotkey = hotkey;
        _globalHotkey = globalPresetHotkey ?? new UnregisteredHotkeyService();
        _soundEffects = soundEffects ?? NullSoundEffects.Instance;
        _detector = detector;
        _activator = activator;
        _decisions = decisions;
        _reshadeConfig = reshadeConfig;
        TimeSpan interval = activationRetryInterval ?? TimeSpan.FromMilliseconds(250);
        _retryInterval = interval < TimeSpan.FromMilliseconds(10) ? TimeSpan.FromMilliseconds(10) : interval;
        _maxActivationAttempts = Math.Max(1, maxActivationAttempts);
        _verifyDelayMs = Math.Max(0, presetVerifyDelayMs);
        _clockMs = monotonicClockMs ?? (static () => Environment.TickCount64);
        _delay = delayAsync ?? Task.Delay;
        _discoverDbdExePath = discoverDbdExePath ?? (static () => DbdDiscoveryService.Discover().FirstOrDefault()?.ExePath);
        _discoverReshadeIniPath = discoverReshadeIniPath ?? (static exe => ReshadeDiscoveryService.Discover(exe)?.IniPath);
        _processStartTime = processStartTime ?? ProcessStartTime.TryGet;
        _isScanTextUncovered = isScanTextUncovered ?? DbdWindowService.IsLoadingScreenTextUncovered;
        _snapshotStore = ReshadeLoadedSnapshotStore.ForConfig(configService.ConfigPath);
    }

    public event EventHandler? Changed;

    /// <summary>
    /// Raised once per confident detection: a scan read a map (or realm) and the
    /// controller accepted it, whether or not a preset is assigned to it. Raised
    /// on the scan's thread-pool thread with no controller lock held, right after
    /// the state left <see cref="AppState.DetectingLoadingScreen"/> (a
    /// <see cref="Changed"/> for that may come first) and before the switch it
    /// leads to is attempted. Subscribers marshal to the UI thread themselves and
    /// must return quickly. Never raised for a scan that was stopped, failed or
    /// was superseded (DBD exited, the Global preset hotkey), nor on startup or
    /// any other transition.
    /// </summary>
    public event EventHandler<MapDetectedEventArgs>? MapDetected;

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

    /// <summary>The preset ReShade was last switched to by a successful send (null = unknown).</summary>
    public string? ActivePresetPath => _activePresetPath;

    public string? ActiveRealm => _activeRealm;

    public bool IsManualOverride => _overrideRealm is not null;

    public string? OverrideRealm => _overrideRealm;

    /// <summary>True while a preset switch is waiting to be sent (e.g. for DBD focus).</summary>
    public bool HasPendingActivation
    {
        get
        {
            lock (_gate)
            {
                return _desired is not null;
            }
        }
    }

    /// <summary>
    /// True while DBD runs and its ReShade has not loaded the current shortcut
    /// of at least one assigned preset, and a DBD restart fixes that (see
    /// <see cref="NamesNeedingDbdRestart"/>).
    /// </summary>
    public bool NeedsDbdRestartForShortcuts => NamesNeedingDbdRestart.Count > 0;

    /// <summary>
    /// Display names (Global/Menu first, then realms) of the assigned presets
    /// whose current shortcut the running DBD's ReShade has not loaded, while
    /// the configured ReShade.ini exists: restarting DBD makes ReShade load
    /// them. They cannot be switched to until then. Empty while DBD is not running.
    /// </summary>
    public IReadOnlyList<string> NamesNeedingDbdRestart
    {
        get
        {
            lock (_gate)
            {
                return BlockedShortcutsLocked().NeedRestart;
            }
        }
    }

    /// <summary>
    /// Display names of the assigned presets the running DBD's ReShade has not
    /// loaded while the configured ReShade.ini is not set or doesn't exist: a
    /// DBD restart alone can't help, the right ReShade.ini must be set first.
    /// Empty while DBD is not running.
    /// </summary>
    public IReadOnlyList<string> NamesWaitingForReshadeIni
    {
        get
        {
            lock (_gate)
            {
                return BlockedShortcutsLocked().WaitForIni;
            }
        }
    }

    /// <summary>
    /// The banner text: which assigned presets can't be switched to and what
    /// fixes it (a DBD restart, or setting ReShade.ini first); null when none.
    /// </summary>
    public string? BlockedShortcutsNotice
    {
        get
        {
            lock (_gate)
            {
                return BlockedShortcutsNoticeLocked(BlockedShortcutsLocked());
            }
        }
    }

    /// <summary>
    /// <see cref="NamesNeedingDbdRestart"/>, <see cref="NamesWaitingForReshadeIni"/> and
    /// <see cref="BlockedShortcutsNotice"/> together, worked out once (a UI refresh needs all three).
    /// </summary>
    public BlockedPresetShortcuts BlockedPresetShortcuts
    {
        get
        {
            lock (_gate)
            {
                BlockedShortcuts blocked = BlockedShortcutsLocked();
                return new BlockedPresetShortcuts(blocked.NeedRestart, blocked.WaitForIni, BlockedShortcutsNoticeLocked(blocked));
            }
        }
    }

    /// <summary>
    /// The preset file the manual override forces (the realm's preset, else the Global preset); null outside an
    /// override or when neither is set. Compare it with <see cref="ActivePresetPath"/> to tell whether it is on yet.
    /// </summary>
    public string? OverridePresetPath
    {
        get
        {
            lock (_gate)
            {
                if (_overrideRealm is null)
                {
                    return null;
                }

                string? path = _decisions.DecideManualOverride(_overrideRealm, Config).PresetPath;
                return string.IsNullOrWhiteSpace(path) ? null : path;
            }
        }
    }

    /// <summary>
    /// Why the last save of the settings failed ("Could not save settings: …"); null when it worked. A settings
    /// change that saves (e.g. <see cref="SetDebug"/>) shows a failure on the status line too.
    /// </summary>
    public string? LastSaveError
    {
        get
        {
            lock (_gate)
            {
                return _lastSaveError;
            }
        }
    }

    /// <summary>Incremented by every <see cref="ResetSettings"/>: the UI drops unsaved drafts (e.g. a mistyped URL).</summary>
    public int SettingsResetCount => Volatile.Read(ref _settingsResetCount);

    public string? HotkeyError => _hotkeyError;

    /// <summary>The Global preset hotkey ("Switch to Global preset"); null when cleared (disabled).</summary>
    public DetectionHotkeyConfig? GlobalHotkey => Config.GlobalHotkey;

    /// <summary>
    /// Why the Global preset hotkey is not registered (e.g. another program holds
    /// it, or it is the detection hotkey's combo); null while it works or is cleared.
    /// </summary>
    public string? GlobalHotkeyError => _globalHotkeyError;

    /// <summary>
    /// Completes once every Global preset hotkey press received so far has been
    /// handled (presses are queued off the hotkey's pump thread and run one at a
    /// time on the thread pool). Never faults. For tests and tools.
    /// </summary>
    public Task GlobalHotkeyPressesHandled
    {
        get
        {
            lock (_globalPressLock)
            {
                return _globalPresses;
            }
        }
    }

    /// <summary>
    /// True while a scan runs but reads nothing because Dead by Daylight is not
    /// in front (the scan is paused; it resumes by itself once DBD is back).
    /// </summary>
    public bool IsScanPaused
    {
        get
        {
            lock (_gate)
            {
                return _detectionInFlight && _scanPauseNotice is not null && _states.State == AppState.DetectingLoadingScreen;
            }
        }
    }

    /// <summary>
    /// True when ReShade.ini was seen holding <see cref="ActivePresetPath"/> after
    /// the app switched to it ("confirmed by ReShade"); false while unknown.
    /// </summary>
    public bool IsActivePresetConfirmed
    {
        get
        {
            lock (_gate)
            {
                return _activePresetPath is not null && _activePresetConfirmed;
            }
        }
    }

    /// <summary>The last recorded preset-switch attempt (null: none yet). See <see cref="LastActivationText"/>.</summary>
    public ActivationAttempt? LastActivation => _lastActivation;

    /// <summary>
    /// The Test switch (<see cref="TestRealmPreset"/> / <see cref="TestGlobalPreset"/>)
    /// that is still waiting for DBD focus, with the time it has left; null when
    /// no Test is pending (it was sent, superseded, failed or gave up).
    /// </summary>
    public PendingTest? PendingTest
    {
        get
        {
            lock (_gate)
            {
                if (_desired is not { Origin: ActivationOrigin.Test } request)
                {
                    return null;
                }

                TimeSpan left = request.DeadlineMs is long deadline
                    ? TimeSpan.FromMilliseconds(Math.Max(0, deadline - _clockMs()))
                    : TestFocusWindow;
                return new PendingTest(request.ShortcutName, request.Name, left, request.Serial);
            }
        }
    }

    /// <summary>
    /// How the newest Test that ended by itself ended (sent, failed, or gave up waiting for DBD focus), with that Test's
    /// <see cref="PendingTest.Serial"/>; null before any. A Test that something else superseded (a scan, another
    /// switch, DBD closing) leaves no outcome: compare the serial to tell.
    /// </summary>
    public TestOutcome? LastTestOutcome
    {
        get
        {
            lock (_gate)
            {
                return _lastTestOutcome;
            }
        }
    }

    /// <summary>Whether the UI plays its animations (Settings → Animations).</summary>
    public bool AnimationsEnabled => Config.AnimationsEnabled;

    /// <summary>Whether the Great skill-check sound plays when a map is found (Settings → Sound).</summary>
    public bool SoundEnabled => Config.SoundEnabled;

    /// <summary>The Great skill-check sound volume, 0..1 (Settings → Sound; 0.5 on first run).</summary>
    public double SoundVolume => Config.SoundVolume;

    /// <summary>
    /// The app's sound effects. <see cref="ISoundEffects.PlayGreat"/> plays at
    /// any volume, whatever the settings; <see cref="PlayGreatSound"/> applies them.
    /// </summary>
    public ISoundEffects SoundEffects => _soundEffects;

    public string DebugDirectory =>
        Path.Combine(AppConfigService.DefaultAppDirectory(), "debug-crops");

    /// <summary>A copy of the realm → preset assignments (safe to enumerate on any thread).</summary>
    public IReadOnlyDictionary<string, string> GetRealmPresets()
    {
        lock (_gate)
        {
            return new Dictionary<string, string>(Config.RealmPresets, StringComparer.OrdinalIgnoreCase);
        }
    }

    public void Initialize()
    {
        _states.StateChanged += (_, _) => RaiseChanged();
        _dbd.StatusChanged += (_, e) => OnDbdStatus(e);
        _hotkey.HotkeyPressed += (_, _) => _ = OnDetectionHotkeyAsync();
        _globalHotkey.HotkeyPressed += (_, _) => QueueGlobalHotkeyPress(); // returns at once; presses run in order
        _detector.SampleEvaluated += (_, info) => OnSampleEvaluated(info);

        // The monitor's first poll runs inside Start, so afterwards IsRunning
        // says whether DBD was already running when the app started. Status
        // events raised before the block below finishes are ignored
        // (_initialized): the block reads the same state itself.
        _dbd.Start();

        lock (_gate)
        {
            if (_dbd.IsRunning)
            {
                _knownProcessId = _dbd.ProcessId;
                _states.TryTransitionTo(AppState.Ready);
                SetReadyTextLocked();

                // An earlier run of this app may have rewritten ReShade.ini while
                // this same process kept running: its stored snapshot says what
                // was really loaded. Otherwise its ReShade loaded ReShade.ini
                // before this app touched it: snapshot the file BEFORE the sync.
                ReshadeLoadedSnapshot? stored = _snapshotStore.Load();
                bool restored = stored is not null && stored.Describes(_dbd.ProcessId, StartTimeOf(_dbd.ProcessId));
                if (restored)
                {
                    _reshadeLoadedSnapshot = stored;
                }

                string? problem = AdoptDiscoveredPathsLocked(
                    restored ? ShortcutSnapshotTiming.None : ShortcutSnapshotTiming.BeforeSync);
                SetStatusLocked(WithSentences("Dead by Daylight detected.", problem));
            }
            else
            {
                _snapshotStore.Delete(); // the process it describes has exited
                _states.ResetTo(AppState.WaitingForDbd);
                _detectionText = "Waiting for DBD…";
                string? problem = AdoptDiscoveredPathsLocked(ShortcutSnapshotTiming.None);
                SetStatusLocked(WithSentences("Launch Dead by Daylight.", problem));
            }

            _initialized = true;
        }

        RegisterHotkey(appendToStatus: true); // both hotkeys; a problem must not hide the startup status
        RaiseChanged();
    }

    /// <summary>
    /// Stops everything. Sends nothing from the moment it starts: every pending
    /// request stops being current first, so the scan it stops (which would
    /// otherwise request Global) sends no keys as a side effect of quitting. A
    /// send already in progress is let finish, so no key stays pressed in the game.
    /// </summary>
    public void Shutdown()
    {
        CancelQuietly(_shutdownCts);
        lock (_gate)
        {
            TrySaveLocked(); // best effort on exit
        }

        _dbd.Stop();
        _hotkey.Stop();
        _globalHotkey.Stop();
        CancelScan();

        // Drain an in-flight down/hold/up sequence (bounded: exit never hangs).
        if (_sendLock.Wait(TimeSpan.FromMilliseconds(500)))
        {
            _sendLock.Release();
        }
    }

    /// <summary>Cancel the running toggle scan, if any. Takes the CTS under lock, cancels outside.</summary>
    private void CancelScan()
    {
        CancellationTokenSource? scanToStop;
        lock (_gate)
        {
            scanToStop = _scanCts;
            _scanCts = null;
        }

        CancelQuietly(scanToStop);
    }

    private static void CancelQuietly(CancellationTokenSource? cts)
    {
        if (cts is null)
        {
            return;
        }

        try
        {
            cts.Cancel();
        }
        catch
        {
            // Best effort; the scan task observes cancellation itself.
        }
    }

    // ---------- Detection (global hotkey toggle) ----------

    private Task OnDetectionHotkeyAsync() => TriggerDetectionAsync();

    /// <summary>
    /// Global hotkey toggle. First press starts an open-ended loading-screen
    /// scan (no timeout — it keeps sampling until it confidently reads the map);
    /// pressing again stops the running scan. A confident read stops the scan
    /// automatically and becomes the desired activation, sent as soon as DBD is
    /// focused. A scan that ends without a confident read switches to Global.
    /// Works from any state while DBD runs (a fresh attempt every time).
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

            string hotkey = Config.DetectionHotkey.ToString();
            if (_detectionInFlight)
            {
                // Toggle OFF: stop the running scan; the scan task itself
                // reports "stopped" and returns to Ready.
                toStop = _scanCts;
                _scanCts = null;
                if (toStop is null)
                {
                    // Already told to stop (the Global preset hotkey took over, or DBD
                    // restarted); it ends within a moment, then a new scan can start.
                    SetStatusLocked($"The previous scan is still stopping — press {hotkey} again in a moment.");
                }
            }
            else if (!_dbd.IsRunning)
            {
                SetStatusLocked("Dead by Daylight is not running.");
                _detectionText = "Waiting for DBD…";
            }
            else if (_states.State == AppState.DetectingLoadingScreen)
            {
                SetStatusLocked($"Already scanning — press {hotkey} again to stop.");
            }
            else if (!_states.TryTransitionTo(AppState.DetectingLoadingScreen))
            {
                SetStatusLocked($"Busy ({_states.State}); wait a moment and press {hotkey} again.");
            }
            else
            {
                _detectionInFlight = true;
                _scanPauseNotice = null;
                start = true;
                _overrideRealm = null; // a fresh detection supersedes any override
                ClearDesiredLocked(); // and any pending switch (its worker stops)
                _lastDetection = null;
                myCts = new CancellationTokenSource();
                _scanCts = myCts;
                scanToken = myCts.Token;
                _detectionText = $"Scanning… press {hotkey} to stop";
                SetStatusLocked(ScanningStatus(hotkey));
            }
        }

        if (toStop is not null)
        {
            CancelQuietly(toStop);
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
            // Only frames of DBD itself are read: while another window covers DBD
            // the screen capture would show (and OCR, and save) that window instead.
            DetectionResult result = await _detector.DetectAsync(
                () => _dbd.MainWindowHandle, // follows window-handle changes
                isInFront: IsDbdInFrontForScan,
                debugMode: () => _configService.Config.DebugMode,
                debugDirectory: DebugDirectory,
                continuous: true,
                ct: scanToken).ConfigureAwait(false);

            ActivationRequest? request = null;
            MapDetectedEventArgs? detected = null;
            lock (_gate)
            {
                _lastDetection = result;
                if (_disposed || _shutdownCts.IsCancellationRequested ||
                    _states.State != AppState.DetectingLoadingScreen)
                {
                    // DBD exited, the Global preset hotkey took over (or we are shutting
                    // down) while scanning; that path owns the state and the switch.
                    // Shutting down: never request a switch as a side effect of quitting.
                }
                else if (result.WasStopped)
                {
                    _states.TryTransitionTo(AppState.Ready);
                    SetReadyTextLocked();
                    request = RequestGlobalAfterScanLocked("Scan stopped.");
                }
                else if (result.Success && !string.IsNullOrWhiteSpace(result.RealmName))
                {
                    request = AcceptDetectionLocked(result);
                    detected = new MapDetectedEventArgs(result.RealmName!, result.MapName, result.Confidence);
                }
                else
                {
                    _states.TryTransitionTo(AppState.Ready);
                    _detectionText = "No confident map detected";
                    request = RequestGlobalAfterScanLocked(DescribeScanFailure(result));
                }
            }

            if (detected is not null)
            {
                RaiseMapDetected(detected); // first: the UI lands the skill check and plays the sound
            }

            if (request is not null)
            {
                Attempt(request); // immediate try: DBD is usually focused when the hotkey is pressed
            }
            else
            {
                RaiseChanged();
            }
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                _states.TryTransitionTo(AppState.Error);
                _detectionText = "Detection error";
                SetStatusLocked($"Detection error: {ex.Message} Press {Config.DetectionHotkey} to try again.");
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

    /// <summary>
    /// A confident read: make its preset (or Global) the desired activation. It
    /// waits for DBD focus without a deadline and, once sent, stays active until
    /// the user's next switch (nothing watches the match). Call with _gate held.
    /// </summary>
    private ActivationRequest? AcceptDetectionLocked(DetectionResult result)
    {
        string realm = result.RealmName!;
        PresetDecision decision = _decisions.DecideForDetection(
            new MapMatchResult(true, result.MapName, realm, result.Confidence, MatchMethod.MapName),
            Config);
        if (string.IsNullOrWhiteSpace(decision.PresetPath))
        {
            // Nothing is assigned at all: nothing to switch to, nothing to pend.
            _states.TryTransitionTo(AppState.Ready);
            _detectionText = $"Complete — {realm} (no preset assigned)";
            SetStatusLocked($"Detected {realm}, but no preset is assigned " +
                "and no Global preset is set. Assign presets under the Presets tab.");
            return null;
        }

        string context = result.MapName is not null
            ? $"Detected {result.MapName} → {realm}."
            : $"Detected realm {realm}.";
        string name = Path.GetFileName(decision.PresetPath);
        var request = new ActivationRequest(
            ActivationOrigin.Detected,
            decision.PresetPath,
            decision.IsGlobal ? AppConfigKeys.GlobalShortcutKey : realm,
            decision.IsGlobal ? null : realm,
            what: $"Realm {realm} →",
            context: context,
            successMessage: decision.IsGlobal
                ? $"Active preset: {name} (Global — {realm} has no preset)."
                : $"Active preset: {name} ({realm}).",
            window: null, // no deadline: it waits for DBD focus as long as it is the target
            deadlineMs: null);

        _states.TryTransitionTo(AppState.RealmIdentified);
        _states.TryTransitionTo(AppState.WaitingForGameplay);
        _detectionText = $"Complete — {realm}";
        SetDesiredLocked(request);
        SetRequestStatusLocked(request, $"{context} Switching to {name}…");
        return request;
    }

    /// <summary>
    /// A scan ended without a confident read while DBD runs: unsure reads use
    /// Global, so request it unless ReShade is known to run Global already.
    /// Sets a status that says exactly what happens. Call with _gate held.
    /// </summary>
    private ActivationRequest? RequestGlobalAfterScanLocked(string outcome)
    {
        string? global = Config.GlobalPresetPath;
        if (!_dbd.IsRunning)
        {
            SetStatusLocked(outcome);
            return null; // DBD exit handles Global (startup preset)
        }

        if (string.IsNullOrWhiteSpace(global))
        {
            SetStatusLocked($"{outcome} No Global preset is configured, so the current preset stays.");
            return null;
        }

        string name = Path.GetFileName(global);
        if (IsConfirmedActiveLocked(global))
        {
            SetStatusLocked($"{outcome} The Global preset ({name}) stays active.");
            return null;
        }

        var request = new ActivationRequest(
            ActivationOrigin.ScanWithoutResult,
            global,
            AppConfigKeys.GlobalShortcutKey,
            realm: null,
            what: "No confident read → Global",
            context: outcome,
            successMessage: $"{outcome} Switched to the Global preset ({name}).",
            window: ScanWithoutResultFocusWindow,
            deadlineMs: DeadlineAfter(ScanWithoutResultFocusWindow));
        SetDesiredLocked(request);
        SetRequestStatusLocked(request, $"{outcome} Switching to the Global preset ({name})…");
        return request;
    }

    private static string DescribeScanFailure(DetectionResult result)
    {
        string reason = string.IsNullOrWhiteSpace(result.FailureReason)
            ? "No confident map detected."
            : result.FailureReason.Trim();

        // The aggregator's generic text promises Global; what really happens is said separately.
        const string globalSuffix = "— Global preset.";
        if (reason.EndsWith(globalSuffix, StringComparison.Ordinal))
        {
            reason = reason[..^globalSuffix.Length].TrimEnd() + ".";
        }

        return reason;
    }

    private static string ScanningStatus(string hotkey) =>
        $"Reading the loading screen… press {hotkey} again to stop.";

    /// <summary>
    /// Whether a scan tick may read DBD's window: it is the foreground window
    /// (the same check activation uses), or no other window covers its
    /// loading-screen text, e.g. DBD in plain view on one monitor while the user
    /// works on another. Called on the scan's thread with no controller lock
    /// held; never throws (a failed check counts as covered).
    /// </summary>
    private bool IsDbdInFrontForScan(IntPtr window)
    {
        if (_activator.IsForeground(window))
        {
            return true;
        }

        try
        {
            return _isScanTextUncovered(window);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Per-sample feedback. A paused tick (DBD not in front, nothing read) says
    /// so once per pause on the status line, so the repeats stay quiet and never
    /// overwrite later news; the first real sample afterwards puts the scanning
    /// text back only while the pause notice still owns the line.
    /// </summary>
    private void OnSampleEvaluated(DetectionSampleInfo info)
    {
        lock (_gate)
        {
            if (!_detectionInFlight || _states.State != AppState.DetectingLoadingScreen)
            {
                return;
            }

            string hotkey = Config.DetectionHotkey.ToString();
            if (info.Paused)
            {
                if (_scanPauseNotice is not null)
                {
                    return; // already said
                }

                string reason = string.IsNullOrWhiteSpace(info.Diagnostic)
                    ? DetectionOrchestrator.NotForegroundDiagnostic
                    : info.Diagnostic.Trim();
                _scanPauseNotice = WithSentences(reason,
                    $"Reading resumes when you switch back to the game; press {hotkey} again to stop.");
                _detectionText = $"{reason.TrimEnd('.')}; press {hotkey} to stop";
                SetStatusLocked(_scanPauseNotice);
            }
            else
            {
                if (_scanPauseNotice is not null)
                {
                    if (_statusMessage == _scanPauseNotice)
                    {
                        SetStatusLocked(ScanningStatus(hotkey)); // nothing else was said meanwhile
                    }

                    _scanPauseNotice = null;
                }

                string lastRead = info.Success && !string.IsNullOrWhiteSpace(info.RealmName)
                    ? (info.MapName is null ? info.RealmName! : $"{info.MapName} → {info.RealmName}") +
                      $" ({info.Confidence:P0})"
                    : string.IsNullOrWhiteSpace(info.Diagnostic) ? "nothing readable" : info.Diagnostic!;
                _detectionText = $"Scanning… ({info.Index}) — last read: {lastRead}";
            }
        }

        RaiseChanged();
    }

    // ---------- Desired activation (one target, focus-aware) ----------

    /// <summary>
    /// Performs one activation attempt for the desired target, whatever its
    /// origin. Returns true when the preset was switched or nothing is pending.
    /// Waiting for focus costs no attempt; a rejected send does, and the last
    /// allowed one explains the fix. Safe to call repeatedly.
    /// </summary>
    public bool RetryPendingActivation()
    {
        ActivationRequest? request;
        lock (_gate)
        {
            request = _desired;
            if (_disposed || request is null)
            {
                return true; // nothing to do
            }
        }

        return Attempt(request).Outcome == AttemptOutcome.Succeeded;
    }

    /// <summary>
    /// One attempt for <paramref name="request"/>. Takes the send lock (never
    /// while holding _gate), so no two sends ever interleave.
    /// </summary>
    private AttemptResult Attempt(ActivationRequest request)
    {
        AttemptResult result;
        PendingVerification? verification = null;
        _sendLock.Wait();
        try
        {
            result = AttemptWhileSending(request, ref verification);
        }
        catch (Exception ex)
        {
            result = new AttemptResult(AttemptOutcome.Failed, ex.Message);
        }
        finally
        {
            _sendLock.Release();
        }

        if (verification is not null)
        {
            _ = VerifySwitchAsync(verification);
        }

        if (!result.Quiet)
        {
            RaiseChanged();
        }

        return result;
    }

    private AttemptResult AttemptWhileSending(ActivationRequest request, ref PendingVerification? verification)
    {
        ReshadeShortcutConfig? shortcut;
        string? iniPath;
        string? notLoaded;
        IReadOnlyList<IniShortcutEntry>? loaded;
        lock (_gate)
        {
            if (!IsCurrentLocked(request))
            {
                return new AttemptResult(AttemptOutcome.Superseded, null, Quiet: true);
            }

            if (request.DeadlineMs is long deadline && _clockMs() >= deadline)
            {
                _desired = null;
                string message = ExpiredMessage(request);
                SetRequestStatusLocked(request, message);
                return new AttemptResult(AttemptOutcome.Failed, message);
            }

            shortcut = LookupShortcutLocked(request.ShortcutName);
            iniPath = Config.ReshadeIniPath;
            notLoaded = NotLoadedMessageLocked(request.PresetPath, shortcut, request.ShortcutName);
            loaded = LoadedShortcutEntriesLocked();
        }

        bool running = _dbd.IsRunning;
        IntPtr window = _dbd.MainWindowHandle;
        ActivationResult ready = _activator.CheckReady(request.PresetPath, shortcut, window, running);
        if (!ready.Success && ready.Failure != ActivationFailure.NotFocused)
        {
            return OnAttemptFailed(request, shortcut, ready);
        }

        if (notLoaded is not null)
        {
            // The running ReShade never loaded this (combo, preset) pair: the
            // combo may switch it to a different preset, or do nothing. Final
            // for this request, and reported before any wait for focus.
            return OnAttemptFailed(request, shortcut, ActivationResult.Fail(ActivationFailure.RestartRequired, notLoaded));
        }

        if (OtherShortcutFiresFirstMessage(request.PresetPath, shortcut, iniPath, loaded) is { } clash)
        {
            // ReShade loaded the pair, but the combo would fire an earlier entry
            // for another preset first (its "Force correct modifier keys" is off).
            // Final for this request, and reported before any wait for focus.
            return OnAttemptFailed(request, shortcut, ActivationResult.Fail(ActivationFailure.ShortcutClash, clash));
        }

        if (!ready.Success)
        {
            return OnAttemptFailed(request, shortcut, ready);
        }

        // What ReShade saved before the send: if it already is the target, the
        // read-back after the send cannot prove anything.
        string? before = ReadCurrentPreset(iniPath);
        ActivationResult sent = _activator.Activate(request.PresetPath, shortcut, window, running);
        if (!sent.Success)
        {
            return OnAttemptFailed(request, shortcut, sent);
        }

        lock (_gate)
        {
            RecordActivationLocked(request.What, request.PresetPath, shortcut, sent);
            _activePresetPath = request.PresetPath; // the keys went out: that is what ReShade switched to
            _activeRealm = request.Realm;
            _activePresetConfirmed = IsSamePreset(before, request.PresetPath); // otherwise the read-back decides
            long activeSwitch = ++_activeSwitch;
            int generation = ++_statusGeneration;
            if (!IsCurrentLocked(request))
            {
                return new AttemptResult(AttemptOutcome.Superseded, null); // the newer request sends next
            }

            _desired = null;
            if (request.Origin == ActivationOrigin.Detected)
            {
                _states.TryTransitionTo(AppState.PresetActive); // until the user's next switch
            }

            if (IsSamePreset(before, request.PresetPath))
            {
                SetRequestStatusLocked(request, WithNote(request.SuccessMessage, "already active in ReShade"));
            }
            else
            {
                SetRequestStatusLocked(request, request.SuccessMessage);
                if (!string.IsNullOrWhiteSpace(iniPath))
                {
                    verification = new PendingVerification(
                        generation, activeSwitch, iniPath, request.PresetPath, request.SuccessMessage);
                }
            }

            return new AttemptResult(AttemptOutcome.Succeeded, null);
        }
    }

    private AttemptResult OnAttemptFailed(ActivationRequest request, ReshadeShortcutConfig? shortcut, ActivationResult failure)
    {
        lock (_gate)
        {
            if (failure.Failure != ActivationFailure.NotFocused)
            {
                RecordActivationLocked(request.What, request.PresetPath, shortcut, failure);
            }

            if (!IsCurrentLocked(request))
            {
                return new AttemptResult(AttemptOutcome.Superseded, null);
            }

            if (failure.Failure == ActivationFailure.NotFocused)
            {
                // Transient: the worker keeps checking; no attempt is consumed.
                // The waiting text is (re)asserted only while the status line
                // still belongs to this request: a message written since (an
                // error, "Automatic mode is already active.") stays visible.
                // Repeated checks with nothing new to say stay quiet (no UI churn).
                string waiting = WaitingMessage(request);
                if (!ReferenceEquals(_statusOwner, request) ||
                    string.Equals(_statusMessage, waiting, StringComparison.Ordinal))
                {
                    return new AttemptResult(AttemptOutcome.Waiting, null, Quiet: true);
                }

                SetRequestStatusLocked(request, waiting);
                return new AttemptResult(AttemptOutcome.Waiting, null);
            }

            if (failure.Failure == ActivationFailure.SendBlocked)
            {
                request.SendFailures++;
                request.LastSendError = failure.Message;
                if (request.SendFailures < _maxActivationAttempts)
                {
                    SetRequestStatusLocked(request, Prefixed(request.Context,
                        $"Could not send the shortcut for {request.Name} (try {request.SendFailures} of " +
                        $"{_maxActivationAttempts}); retrying… {failure.Message}"));
                    return new AttemptResult(AttemptOutcome.Waiting, null);
                }
            }

            // Final failure for this request, a spent send budget included: nothing
            // retries it by itself (no gameplay detection renews it), so a detected
            // preset stops being the pending target and the app is Ready again.
            _desired = null;
            if (request.Origin == ActivationOrigin.Detected && _states.State == AppState.WaitingForGameplay)
            {
                _states.TryTransitionTo(AppState.Ready);
            }

            string message = request.Origin switch
            {
                ActivationOrigin.Test => $"Test failed: {failure.Message}",
                ActivationOrigin.Detected when failure.Failure == ActivationFailure.SendBlocked => SendBudgetSpentMessage(request),
                _ => Prefixed(request.Context, $"Could not switch to {request.Name}: {failure.Message}"),
            };
            SetRequestStatusLocked(request, message);
            return new AttemptResult(AttemptOutcome.Failed,
                request.Origin == ActivationOrigin.Test ? failure.Message : message);
        }
    }

    private string WaitingMessage(ActivationRequest request) => request.Origin == ActivationOrigin.Test
        ? $"Click into Dead by Daylight within {FormatWindow(TestFocusWindow)} to test {request.Name}…"
        : Prefixed(request.Context, $"Waiting for DBD focus to switch to {request.Name} — click into the game.");

    /// <summary>
    /// A detected preset whose rejected sends used up the budget. A new scan can
    /// only read a loading screen (in a match it would end without a read and
    /// switch to Global), so the hint says when to press it, and names the
    /// manual override as the way to force the preset during a match.
    /// </summary>
    private string SendBudgetSpentMessage(ActivationRequest request) =>
        $"Could not switch to {request.Name}: {request.LastSendError} Press {Config.DetectionHotkey} on a loading " +
        "screen to try again, or use the manual override.";

    private static string ExpiredMessage(ActivationRequest request) => request.Origin == ActivationOrigin.Test
        ? $"Test of {request.Name} cancelled — Dead by Daylight wasn't focused within {FormatWindow(request.Window)}."
        : Prefixed(request.Context,
            $"Gave up switching to {request.Name}: Dead by Daylight wasn't focused within {FormatWindow(request.Window)}.");

    /// <summary>Make <paramref name="request"/> the desired activation (supersedes the previous one). Call with _gate held.</summary>
    private void SetDesiredLocked(ActivationRequest request)
    {
        _desired = request;
        _statusGeneration++;
        StartWorkerLocked(request);
    }

    /// <summary>Drop the desired activation (its worker stops at its next check). Call with _gate held.</summary>
    private void ClearDesiredLocked()
    {
        _desired = null;
        _statusGeneration++;
    }

    /// <summary>
    /// Show a message that is not about a pending activation. A pending
    /// request no longer re-asserts its waiting text over it, and a read-back
    /// verification still running no longer reports. Call with _gate held.
    /// </summary>
    private void SetStatusLocked(string message)
    {
        _statusMessage = message;
        _statusOwner = null;
        _statusGeneration++;
    }

    /// <summary>Show a message of the activation pipeline about <paramref name="request"/>. Call with _gate held.</summary>
    private void SetRequestStatusLocked(ActivationRequest request, string message)
    {
        _statusMessage = message;
        _statusOwner = request;
        if (request.Origin == ActivationOrigin.Test && !ReferenceEquals(_desired, request))
        {
            // a Test that is no longer the target says how it ended (sent, failed, gave up): its outcome. (Its waiting
            // and retry texts are written while it still is the target; a Test superseded by something else writes
            // nothing more.)
            _lastTestOutcome = new TestOutcome(request.ShortcutName, request.Serial, message);
        }
    }

    private bool IsCurrentLocked(ActivationRequest request) =>
        !_disposed && !_shutdownCts.IsCancellationRequested && ReferenceEquals(_desired, request);

    /// <summary>Starts the background sender for <paramref name="request"/> unless one runs. Call with _gate held.</summary>
    private void StartWorkerLocked(ActivationRequest request)
    {
        if (request.WorkerRunning || _disposed)
        {
            return;
        }

        request.WorkerRunning = true;
        CancellationToken token = _shutdownCts.Token;
        _ = Task.Run(() => RunActivationWorkerAsync(request, token));
    }

    /// <summary>
    /// Sends <paramref name="request"/> as soon as DBD runs and is focused,
    /// checking every <see cref="_retryInterval"/>, until it succeeds, is
    /// superseded, fails for good (a spent send budget included) or its
    /// deadline passes. The exit decision and <c>WorkerRunning = false</c>
    /// happen together under _gate, so the flag is set exactly while a worker
    /// can still send for the request and <see cref="StartWorkerLocked"/> never
    /// starts a second one.
    /// </summary>
    private async Task RunActivationWorkerAsync(ActivationRequest request, CancellationToken token)
    {
        for (; ; )
        {
            try
            {
                await _delay(_retryInterval, token).ConfigureAwait(false);
            }
            catch
            {
                lock (_gate)
                {
                    request.WorkerRunning = false;
                }

                return; // shutting down
            }

            lock (_gate)
            {
                if (!IsCurrentLocked(request))
                {
                    request.WorkerRunning = false;
                    return;
                }
            }

            try
            {
                Attempt(request);
            }
            catch
            {
                // Never let one failed attempt end the worker; the next check decides.
            }
        }
    }

    /// <summary>
    /// Confirms the switch actually landed: ReShade saves its config (including
    /// the new PresetPath) in the same frame it processes a preset shortcut, so
    /// watching the ini for a short while distinguishes "keys ignored" from
    /// "switched but not visibly different". Never blocks. A confirmation
    /// counts while the switch is still the active one (no newer send), even
    /// when another message took the status line meanwhile; the result is
    /// only reported while the status generation is still the send's own.
    /// </summary>
    private async Task VerifySwitchAsync(PendingVerification verification)
    {
        try
        {
            int step = Math.Min(VerifyPollMs, _verifyDelayMs);
            int polls = step <= 0 ? 1 : Math.Max(1, (_verifyDelayMs + step - 1) / step);
            bool confirmed = false;
            for (int i = 0; i < polls && !confirmed; i++)
            {
                await _delay(TimeSpan.FromMilliseconds(step), _shutdownCts.Token).ConfigureAwait(false);
                lock (_gate)
                {
                    if (!IsVerifiedSwitchActiveLocked(verification))
                    {
                        return; // a newer send (or a reset): nothing left to confirm
                    }
                }

                confirmed = IsSamePreset(ReadCurrentPreset(verification.IniPath), verification.ExpectedPreset);
            }

            string name = Path.GetFileName(verification.ExpectedPreset);
            lock (_gate)
            {
                if (!IsVerifiedSwitchActiveLocked(verification))
                {
                    return; // superseded by a newer send
                }

                // No newer send: the verified preset is still the active one.
                _activePresetConfirmed |= confirmed && IsSamePreset(_activePresetPath, verification.ExpectedPreset);
                if (verification.Generation != _statusGeneration)
                {
                    return; // a newer request or another message took the status line: it stays
                }

                // Still the send's own status line (same generation), so its owner stays.
                _statusMessage = confirmed
                    ? WithNote(verification.SuccessMessage, "confirmed by ReShade")
                    : $"Keys sent for {name}, but ReShade hasn't confirmed the switch. " +
                      "Open the ReShade overlay (Home key) to check the active preset; " +
                      "if it didn't change, restart DBD so ReShade loads the shortcuts.";
            }

            RaiseChanged();
        }
        catch
        {
            // Shutting down, or a best-effort read failed: confirmation is optional.
        }
    }

    private bool IsVerifiedSwitchActiveLocked(PendingVerification verification) =>
        !_disposed && !_shutdownCts.IsCancellationRequested && verification.ActiveSwitch == _activeSwitch;

    /// <summary>
    /// ReShade is known to run <paramref name="presetPath"/>: the app's last
    /// switch was to it and ReShade.ini was seen holding it. Call with _gate held.
    /// </summary>
    private bool IsConfirmedActiveLocked(string? presetPath) =>
        _activePresetConfirmed && IsSamePreset(_activePresetPath, presetPath);

    // ---------- DBD running / exited ----------

    private void OnDbdStatus(DbdStatusEventArgs e)
    {
        if (!e.IsRunning)
        {
            OnDbdExited();
            return;
        }

        CancellationTokenSource? scanToStop = null;
        lock (_gate)
        {
            if (_disposed || !_initialized)
            {
                return;
            }

            bool wasDown = _states.State is AppState.WaitingForDbd or AppState.Error;
            bool pidChanged = e.ProcessId is int pid && _knownProcessId is int known && pid != known;
            bool newProcess = _states.State == AppState.WaitingForDbd || pidChanged;
            if (e.ProcessId is int current)
            {
                _knownProcessId = current;
            }

            // Otherwise this is a window-handle-only change of the same process:
            // scans and activations read the live handle, and
            // status, discovery and shortcut-restart state stay as they are.
            if (wasDown || pidChanged)
            {
                if (!wasDown)
                {
                    // DBD restarted between polls: the old process is gone.
                    scanToStop = _scanCts;
                    _scanCts = null;
                    ClearDesiredLocked();
                    _overrideRealm = null;
                    _states.TryTransitionTo(AppState.WaitingForDbd);
                }

                _states.TryTransitionTo(AppState.Ready);
                SetReadyTextLocked();

                // A new process reads ReShade.ini while the game starts: sync
                // first, then snapshot what it is about to load.
                string? problem = AdoptDiscoveredPathsLocked(
                    newProcess ? ShortcutSnapshotTiming.AfterSync : ShortcutSnapshotTiming.None);
                SetStatusLocked(WithSentences("Dead by Daylight detected.", problem));
            }
        }

        CancelQuietly(scanToStop);
        RaiseChanged();
    }

    /// <summary>
    /// DBD exited: stop everything (a manual override ends too), and (with
    /// <see cref="AppConfig.StartNextGameWithGlobal"/> on, "Start the next game
    /// with the Global preset") make Global ReShade's startup preset so the next
    /// launch doesn't start with the last realm's preset. That setting gates
    /// only this write. The process is gone, so ReShade can't overwrite it.
    /// What ReShade.ini says decides, not what the app last sent: Global keys
    /// that ReShade ignored leave the realm preset there. Only after ReShade
    /// confirmed the app's switch to Global is a different preset in
    /// ReShade.ini the user's own pick (e.g. in the overlay), and kept; the
    /// next launch starts with it, so the app no longer counts Global as active.
    /// </summary>
    private void OnDbdExited()
    {
        CancellationTokenSource? scanToStop;
        string? iniPath = null;
        string? global = null;
        bool globalConfirmed = false;
        lock (_gate)
        {
            if (_disposed || !_initialized)
            {
                return;
            }

            // Stop any running scan: no window to read from anymore.
            scanToStop = _scanCts;
            _scanCts = null;
            ClearDesiredLocked();
            _overrideRealm = null;
            _knownProcessId = null;
            _reshadeLoadedSnapshot = null; // the next process loads ReShade.ini afresh
            _snapshotStore.Delete();
            _states.TryTransitionTo(AppState.WaitingForDbd);
            _detectionText = "Waiting for DBD…";
            SetStatusLocked("Launch Dead by Daylight.");

            if (Config.StartNextGameWithGlobal &&
                !string.IsNullOrWhiteSpace(Config.GlobalPresetPath) &&
                !string.IsNullOrWhiteSpace(Config.ReshadeIniPath))
            {
                iniPath = Config.ReshadeIniPath;
                global = Config.GlobalPresetPath;
                globalConfirmed = IsConfirmedActiveLocked(global);
            }
        }

        CancelQuietly(scanToStop);

        if (iniPath is not null && global is not null && File.Exists(iniPath))
        {
            string? error = null;
            string? startup = null;
            bool startsWithGlobal = true;
            try
            {
                // Not under _gate, but serialized with the shortcut syncs that
                // write the same file (they hold _iniWriteLock inside _gate).
                lock (_iniWriteLock)
                {
                    startup = ReadCurrentPreset(iniPath);
                    if (!IsSamePreset(startup, global))
                    {
                        if (globalConfirmed)
                        {
                            startsWithGlobal = false; // the user's own pick after Global: keep it
                        }
                        else
                        {
                            _reshadeConfig.WriteCurrentPreset(iniPath, global);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                error = ex.Message; // best effort: never throws
            }

            lock (_gate)
            {
                if (!_disposed && _states.State == AppState.WaitingForDbd)
                {
                    if (error is not null)
                    {
                        SetStatusLocked($"Launch Dead by Daylight. (Could not make the Global preset ReShade's startup preset: {error})");
                    }
                    else if (startsWithGlobal)
                    {
                        _activePresetPath = global;
                        _activeRealm = null;
                        _activePresetConfirmed = true; // ReShade.ini now holds it
                        _activeSwitch++;
                        SetStatusLocked("Launch Dead by Daylight. ReShade will start with the Global preset.");
                    }
                    else
                    {
                        // The user's pick stays, and the next launch starts with
                        // it: Global is no longer what ReShade runs.
                        _activePresetPath = startup;
                        _activeRealm = null;
                        _activePresetConfirmed = startup is not null; // ReShade.ini holds it
                        _activeSwitch++;
                    }
                }
            }
        }

        RaiseChanged();
    }

    // ---------- Manual override ----------

    /// <summary>
    /// Forces <paramref name="realm"/>'s preset (Global when it has none) until
    /// the user ends the override: Return to Automatic, a new scan (detection
    /// hotkey), the Global preset hotkey or "Switch to Global", or DBD exiting.
    /// Nothing else clears it (the app does not watch the match).
    /// </summary>
    public void ApplyManualOverride(string? realm)
    {
        ActivationRequest? request = null;
        lock (_gate)
        {
            PresetDecision decision = _decisions.DecideManualOverride(realm, Config);
            if (!_states.TryTransitionTo(AppState.ManualOverride))
            {
                SetStatusLocked($"Cannot override right now (state: {_states.State}).");
            }
            else
            {
                _overrideRealm = realm;
                ClearDesiredLocked();
                string context = $"Manual override: {decision.Reason}";
                if (string.IsNullOrWhiteSpace(decision.PresetPath))
                {
                    SetStatusLocked($"{context} No preset is configured, so nothing was switched.");
                }
                else
                {
                    string name = Path.GetFileName(decision.PresetPath);
                    request = new ActivationRequest(
                        ActivationOrigin.ManualOverride,
                        decision.PresetPath,
                        decision.IsGlobal ? AppConfigKeys.GlobalShortcutKey : realm!,
                        decision.IsGlobal ? null : realm,
                        what: $"Override {realm ?? "—"} →",
                        context: context,
                        successMessage: $"{context} Active preset: {name}.",
                        window: OverrideFocusWindow,
                        deadlineMs: DeadlineAfter(OverrideFocusWindow));
                    SetDesiredLocked(request);
                    SetRequestStatusLocked(request, $"{context} Switching to {name}…");
                }
            }
        }

        if (request is not null)
        {
            Attempt(request);
        }
        else
        {
            RaiseChanged();
        }
    }

    /// <summary>
    /// Ends a manual override: back to Ready and Global is requested. Outside
    /// an override automatic mode is already active, so nothing changes.
    /// </summary>
    public void ReturnToAutomatic()
    {
        ActivationRequest? request = null;
        lock (_gate)
        {
            if (_states.State != AppState.ManualOverride)
            {
                SetStatusLocked("Automatic mode is already active.");
            }
            else
            {
                _overrideRealm = null;
                ClearDesiredLocked();
                _states.TryTransitionTo(AppState.Ready);
                SetReadyTextLocked();
                string? global = Config.GlobalPresetPath;
                if (string.IsNullOrWhiteSpace(global))
                {
                    SetStatusLocked("Returned to automatic. No Global preset is configured.");
                }
                else
                {
                    string name = Path.GetFileName(global);
                    request = new ActivationRequest(
                        ActivationOrigin.ReturnToAutomatic,
                        global,
                        AppConfigKeys.GlobalShortcutKey,
                        realm: null,
                        what: "Return to automatic →",
                        context: "Returned to automatic.",
                        successMessage: $"Returned to automatic — Global preset ({name}) active.",
                        window: ReturnToAutomaticFocusWindow,
                        deadlineMs: DeadlineAfter(ReturnToAutomaticFocusWindow));
                    SetDesiredLocked(request);
                    SetRequestStatusLocked(request, $"Returned to automatic — switching to the Global preset ({name})…");
                }
            }
        }

        if (request is not null)
        {
            Attempt(request);
        }
        else
        {
            RaiseChanged();
        }
    }

    // ---------- Global preset hotkey ----------

    /// <summary>
    /// The Global preset hotkey's handler, on the hotkey's pump thread: queues
    /// the press and returns at once, so registering or releasing a hotkey (which
    /// joins that thread) never waits for a switch in progress (a ReShade.ini read
    /// on a sleeping disk, a send waiting for another one). Presses run one at a
    /// time, in order, on the thread pool; a press that arrives while an earlier
    /// one is still waiting to start is folded into it (it would do the same).
    /// </summary>
    private void QueueGlobalHotkeyPress()
    {
        lock (_globalPressLock)
        {
            if (_globalPressWaiting)
            {
                return;
            }

            _globalPressWaiting = true;

            // Not ExecuteSynchronously: the press always runs on the pool, never here.
            _globalPresses = _globalPresses.ContinueWith(
                _ => RunQueuedGlobalHotkeyPress(),
                CancellationToken.None,
                TaskContinuationOptions.DenyChildAttach,
                TaskScheduler.Default);
        }
    }

    private void RunQueuedGlobalHotkeyPress()
    {
        lock (_globalPressLock)
        {
            _globalPressWaiting = false; // from now on a new press queues behind this one
        }

        try
        {
            ActivateGlobalNow(fromHotkey: true);
        }
        catch
        {
            // Never fault the queue: later presses must still run.
        }
    }

    /// <summary>
    /// "Switch to Global preset": the Global preset hotkey (F9 by default; it
    /// calls this with <paramref name="fromHotkey"/> true), or a UI button (the
    /// default: the texts then say the Global preset was requested, not that a
    /// key was pressed). With DBD running and a Global preset set: a running scan
    /// is cancelled without its own "scan ended without a read → Global" request
    /// (it would double up), the pending target and the manual override are
    /// dropped, the state becomes <see cref="AppState.Ready"/>, and
    /// Global is requested through the focus-aware pipeline (sent at once while
    /// DBD is focused, the usual case for a hotkey pressed in game; otherwise
    /// within <see cref="GlobalHotkeyFocusWindow"/>). Status: "Switched to the
    /// Global preset (F9)." (the configured key; "Switched to the Global preset."
    /// from a button), or the pipeline's waiting/failure text, prefixed with
    /// "F9 pressed." or "Global preset requested.". Otherwise only the status says
    /// why nothing happened. Runs on the calling thread (it may read ReShade.ini
    /// and send keys); never call with a controller lock held.
    /// </summary>
    /// <param name="fromHotkey">True for a press of the Global preset hotkey; false (the default) for any other request, e.g. a UI button.</param>
    public void ActivateGlobalNow(bool fromHotkey = false)
    {
        CancellationTokenSource? scanToStop = null;
        ActivationRequest? request = null;
        lock (_gate)
        {
            if (_disposed || _shutdownCts.IsCancellationRequested)
            {
                return;
            }

            string? global = Config.GlobalPresetPath;
            if (!_dbd.IsRunning || !_initialized || _states.State == AppState.WaitingForDbd)
            {
                // WaitingForDbd while the monitor already sees the process: its
                // status event (discovery, sync, shortcut snapshot) owns the state.
                SetStatusLocked("Dead by Daylight is not running.");
            }
            else if (string.IsNullOrWhiteSpace(global))
            {
                SetStatusLocked("No Global / Menu preset is set — choose one under Presets.");
            }
            else
            {
                // (1) The scan's own result handling skips once the state has left
                // DetectingLoadingScreen (below), so it requests nothing.
                scanToStop = _scanCts;
                _scanCts = null;
                _scanPauseNotice = null;

                // (2) Nothing pending, no override.
                ClearDesiredLocked();
                _overrideRealm = null;

                // (4) Every state with DBD running may go to Ready.
                _states.TryTransitionTo(AppState.Ready);
                SetReadyTextLocked();

                // (3) + (5). The key is named only for a press of it (a cleared
                // hotkey's queued press, too, reads as a plain request).
                string? key = fromHotkey ? Config.GlobalHotkey?.ToString() : null;
                string keySuffix = key is null ? string.Empty : $" ({key})";
                var hotkeyRequest = new ActivationRequest(
                    key is null ? ActivationOrigin.GlobalRequest : ActivationOrigin.Hotkey,
                    global,
                    AppConfigKeys.GlobalShortcutKey,
                    realm: null,
                    what: key is null ? "Switch to Global →" : $"Global preset hotkey{keySuffix} →",
                    context: key is null ? "Global preset requested." : $"{key} pressed.",
                    successMessage: $"Switched to the Global preset{keySuffix}.",
                    window: GlobalHotkeyFocusWindow,
                    deadlineMs: DeadlineAfter(GlobalHotkeyFocusWindow));
                SetDesiredLocked(hotkeyRequest);
                SetRequestStatusLocked(hotkeyRequest, $"Switching to the Global preset{keySuffix}…");
                request = hotkeyRequest;
            }
        }

        CancelQuietly(scanToStop);
        if (request is not null)
        {
            Attempt(request); // immediate try: DBD is usually focused when the hotkey is pressed
        }
        else
        {
            RaiseChanged();
        }
    }

    // ---------- Preset management ----------

    /// <summary>
    /// Switches to the realm's preset as a test: immediately when DBD is
    /// focused, otherwise as soon as it is (within <see cref="TestFocusWindow"/>).
    /// Returns an error message when the test failed right away, else null.
    /// </summary>
    public string? TestRealmPreset(string realm)
    {
        ActivationRequest request;
        lock (_gate)
        {
            if (!Config.RealmPresets.TryGetValue(realm, out string? preset) || string.IsNullOrWhiteSpace(preset))
            {
                return $"Realm '{realm}' has no preset assigned.";
            }

            request = TestRequestLocked(preset, realm, realm, $"Test {realm} →");
        }

        return RunTest(request);
    }

    /// <summary>Like <see cref="TestRealmPreset"/>, for the Global preset.</summary>
    public string? TestGlobalPreset()
    {
        ActivationRequest request;
        lock (_gate)
        {
            string? global = Config.GlobalPresetPath;
            if (string.IsNullOrWhiteSpace(global))
            {
                return "No Global preset is configured.";
            }

            request = TestRequestLocked(global, AppConfigKeys.GlobalShortcutKey, null, "Test Global →");
        }

        return RunTest(request);
    }

    private ActivationRequest TestRequestLocked(string preset, string shortcutName, string? realm, string what)
    {
        string name = Path.GetFileName(preset);
        var request = new ActivationRequest(
            ActivationOrigin.Test,
            preset,
            shortcutName,
            realm,
            what: what,
            context: string.Empty,
            successMessage: $"Test: switched to {name}.",
            window: TestFocusWindow,
            deadlineMs: DeadlineAfter(TestFocusWindow))
        {
            Serial = ++_testSerial,
        };
        if (_desired is { Origin: ActivationOrigin.Detected } && _states.State == AppState.WaitingForGameplay)
        {
            _states.TryTransitionTo(AppState.Ready); // the test replaces the pending detected preset
        }

        SetDesiredLocked(request);
        SetRequestStatusLocked(request, $"Testing {name}…");
        return request;
    }

    private string? RunTest(ActivationRequest request)
    {
        AttemptResult result = Attempt(request);
        return result.Outcome == AttemptOutcome.Failed ? result.Error : null;
    }

    /// <summary>
    /// Validate + assign a realm preset (null/empty clears). Transactional: the
    /// assignment is kept only when its shortcut could be allocated and written
    /// to ReShade.ini (or ReShade.ini doesn't exist yet). Returns an error
    /// message or null.
    /// </summary>
    public string? SetRealmPreset(string realm, string? presetPath)
    {
        string? error;
        lock (_gate)
        {
            error = AssignPresetLocked(realm, presetPath);
        }

        RaiseChanged();
        return error;
    }

    /// <summary>Like <see cref="SetRealmPreset"/>, for the Global preset (null/empty clears).</summary>
    public string? SetGlobalPreset(string? presetPath)
    {
        string? error;
        lock (_gate)
        {
            error = AssignPresetLocked(AppConfigKeys.GlobalShortcutKey, presetPath);
        }

        RaiseChanged();
        return error;
    }

    /// <summary>
    /// Assigns (or clears) the preset of <paramref name="name"/> (a realm or the
    /// Global key), syncs the shortcuts and saves. When the sync fails the
    /// assignment is rolled back, so the config, its shortcuts and ReShade.ini
    /// stay consistent. Call with _gate held.
    /// </summary>
    private string? AssignPresetLocked(string name, string? presetPath)
    {
        bool isGlobal = name == AppConfigKeys.GlobalShortcutKey;
        string display = AppConfigKeys.DisplayName(name);
        string? previous = isGlobal
            ? Config.GlobalPresetPath
            : Config.RealmPresets.TryGetValue(name, out string? assigned) ? assigned : null;

        string? normalized = null;
        if (!string.IsNullOrWhiteSpace(presetPath))
        {
            var validation = PresetValidationService.ValidatePresetFile(presetPath);
            if (!validation.IsValid)
            {
                return validation.Message;
            }

            // Global counts too: the same file twice would be two entries with one path.
            string? duplicate = PresetValidationService.FindDuplicateAssignment(presetPath, name, Config);
            if (duplicate is not null)
            {
                return $"This preset is already assigned to {AppConfigKeys.DisplayName(duplicate)}.";
            }

            normalized = PresetValidationService.NormalizePresetPath(presetPath);
        }

        SetAssignmentLocked(name, normalized);
        SyncOutcome sync = SyncShortcutsLocked();
        if (!sync.Succeeded)
        {
            SetAssignmentLocked(name, previous); // nothing was written: keep everything as it was
            SetStatusLocked(sync.Message!);
            return sync.Message;
        }

        string done;
        if (normalized is null)
        {
            done = isGlobal ? "Cleared the Global preset."
                : !string.IsNullOrWhiteSpace(Config.GlobalPresetPath) ? $"Cleared preset for {name} (uses Global)."
                : $"Cleared preset for {name}; no Global preset is set, so detecting {name} switches nothing.";
        }
        else
        {
            string file = Path.GetFileName(normalized);
            done = isGlobal ? $"Global preset: {file}." : $"Assigned {file} to {name}.";

            // Without a ReShade.ini the sync notice already says why nothing was written.
            if (ShortcutNotLoadedLocked(normalized, LookupShortcutLocked(name)) && !ReshadeIniMissingLocked())
            {
                done += $" Restart Dead by Daylight so ReShade loads the shortcut for {display}.";
            }
        }

        SetStatusLocked(WithSentences(done, sync.Message, TrySaveLocked()));
        return null;
    }

    private void SetAssignmentLocked(string name, string? presetPath)
    {
        if (name == AppConfigKeys.GlobalShortcutKey)
        {
            Config.GlobalPresetPath = presetPath;
        }
        else if (presetPath is null)
        {
            Config.RealmPresets.Remove(name);
        }
        else
        {
            Config.RealmPresets[name] = presetPath;
        }
    }

    // ---------- Settings ----------

    /// <summary>
    /// Changes the detection hotkey. The new hotkey is registered FIRST and
    /// saved only when that worked; otherwise the previous one stays (it is
    /// registered again if it had to be released) and the error is returned.
    /// The Global preset hotkey's combo is refused, and so is any of the app's
    /// ReShade shortcut combos (<see cref="HotkeyParser.IsReshadeShortcutCombo"/>:
    /// the hotkey would swallow the app's own preset switches). Ends a capture
    /// (<see cref="SuspendHotkey"/>): the Global preset hotkey is registered
    /// again too, as it is after a change that frees its combo.
    /// </summary>
    public string? UpdateHotkey(DetectionHotkeyConfig hotkey)
    {
        ArgumentNullException.ThrowIfNull(hotkey);
        var requested = CopyHotkey(hotkey);
        DetectionHotkeyConfig previous;
        bool resumeGlobal;
        string? error;
        lock (_gate)
        {
            previous = Config.DetectionHotkey;
            resumeGlobal = _hotkeySuspended;
            _hotkeySuspended = false;
            error = DetectionHotkeyConflictLocked(requested, previous);
        }

        // Never under _gate: Start/Stop join the pump thread, whose handler takes it.
        error ??= _hotkey.Start(requested);
        if (error is not null)
        {
            // A parse error (or a refused combo) leaves the previous registration
            // in place; a failed registration already released it to make room.
            // The configured (previous) hotkey is checked again before it is
            // registered: one that may not be registered stays released.
            string? restoreError = null;
            if (!IsRegistered(_hotkey, previous))
            {
                RegisterDetectionHotkeyCore(out restoreError);
            }

            string? globalProblem = resumeGlobal ? RegisterGlobalHotkeyCore() : null;
            lock (_gate)
            {
                _hotkeyError = restoreError;
                SetStatusLocked(WithSentences(restoreError is null
                    ? $"Hotkey '{requested}' unavailable: {error} {previous} is still the detection hotkey."
                    : $"Hotkey '{requested}' unavailable: {error} The previous hotkey {previous} " +
                      $"could not be registered again either: {restoreError}", globalProblem));
            }

            RaiseChanged();
            return error;
        }

        string? saveError;
        DetectionHotkeyConfig? global;
        lock (_gate)
        {
            _hotkeyError = null;
            Config.DetectionHotkey = requested;
            if (_states.State == AppState.Ready)
            {
                SetReadyTextLocked();
            }

            saveError = TrySaveLocked();
            global = Config.GlobalHotkey;
        }

        // Also when the Global preset hotkey is set but not registered: e.g. it
        // was the old detection hotkey's combo (a config from before 1.2 with F9
        // as its detection hotkey), which the change may just have freed.
        bool registerGlobal = resumeGlobal || (global is not null && !IsRegistered(_globalHotkey, global));
        string? globalProblemAfter = registerGlobal ? RegisterGlobalHotkeyCore() : null;
        lock (_gate)
        {
            SetStatusLocked(WithSentences($"Detection hotkey: {requested}.", saveError, globalProblemAfter));
        }

        RaiseChanged();
        return null;
    }

    /// <summary>
    /// Changes the Global preset hotkey, like <see cref="UpdateHotkey"/>: the new
    /// hotkey is registered FIRST and saved only when that worked; otherwise the
    /// previous one stays (registered again if it had to be released; none when
    /// it was cleared), <see cref="GlobalHotkeyError"/> says whether that
    /// worked, and the error is returned. The detection hotkey's combo is
    /// refused, and so is any of the app's ReShade shortcut combos
    /// (<see cref="HotkeyParser.IsReshadeShortcutCombo"/>: a press of the
    /// Global preset's own combo would trigger the hotkey again and again); the
    /// rules are the same (F1–F24 alone, letters, digits and NumPad keys with
    /// Ctrl, Alt or Shift). Ends a capture (<see cref="SuspendHotkey"/>): the
    /// detection hotkey is registered again too.
    /// </summary>
    public string? UpdateGlobalHotkey(DetectionHotkeyConfig hotkey)
    {
        ArgumentNullException.ThrowIfNull(hotkey);
        var requested = CopyHotkey(hotkey);
        DetectionHotkeyConfig? previous;
        bool resumeDetection;
        string? error = null;
        lock (_gate)
        {
            previous = Config.GlobalHotkey;
            resumeDetection = _hotkeySuspended;
            _hotkeySuspended = false;
            if (HotkeyParser.IsReshadeShortcutCombo(requested, Config.ReshadeShortcuts.Values))
            {
                error = HotkeyParser.ReservedForReshadeError(requested);
            }
            else if (HotkeyParser.SameCombo(requested, Config.DetectionHotkey))
            {
                error = $"{requested} is already the detection hotkey.";
            }
        }

        // Never under _gate (see the lock order).
        error ??= _globalHotkey.Start(requested);
        if (error is not null)
        {
            // The configured (previous) hotkey is checked again before it is
            // registered: one that may not be registered stays released.
            string? restoreError = null;
            if (previous is null)
            {
                _globalHotkey.Stop(); // it was cleared: nothing stays registered
            }
            else if (!IsRegistered(_globalHotkey, previous))
            {
                RegisterGlobalHotkeyCore(out restoreError);
            }

            string? detectionProblem = resumeDetection ? RegisterDetectionHotkeyCore() : null;
            lock (_gate)
            {
                _globalHotkeyError = restoreError;
                string still = previous is null
                    ? "The Global preset still has no hotkey."
                    : restoreError is null
                        ? $"{previous} is still the Global preset hotkey."
                        : $"The previous hotkey {previous} could not be registered again either: {restoreError}";
                SetStatusLocked(WithSentences(
                    $"Hotkey '{requested}' unavailable for the Global preset: {error} {still}", detectionProblem));
            }

            RaiseChanged();
            return error;
        }

        string? saveError;
        lock (_gate)
        {
            _globalHotkeyError = null;
            Config.GlobalHotkey = requested;
            saveError = TrySaveLocked();
        }

        string? detectionProblemAfter = resumeDetection ? RegisterDetectionHotkeyCore() : null;
        lock (_gate)
        {
            SetStatusLocked(WithSentences($"Global preset hotkey: {requested}.", saveError, detectionProblemAfter));
        }

        RaiseChanged();
        return null;
    }

    /// <summary>
    /// Disables the Global preset hotkey: it is released first, then saved as
    /// cleared (an explicit null, so it stays off after an upgrade). Ends a
    /// capture (<see cref="SuspendHotkey"/>): the detection hotkey is registered again.
    /// </summary>
    public void ClearGlobalHotkey()
    {
        bool resumeDetection;
        lock (_gate)
        {
            resumeDetection = _hotkeySuspended;
            _hotkeySuspended = false;
        }

        _globalHotkey.Stop(); // never under _gate
        string? saveError;
        lock (_gate)
        {
            _globalHotkeyError = null;
            Config.GlobalHotkey = null;
            saveError = TrySaveLocked();
        }

        string? detectionProblem = resumeDetection ? RegisterDetectionHotkeyCore() : null;
        lock (_gate)
        {
            SetStatusLocked(WithSentences("Global preset hotkey cleared.", saveError, detectionProblem));
        }

        RaiseChanged();
    }

    /// <summary>
    /// Releases BOTH global hotkeys (detection and Global preset) while the UI
    /// captures a new one for either: a registered hotkey is swallowed by
    /// Windows, so the window would never see the key (and pressing the other
    /// one would switch presets mid-capture). The capture ends with
    /// <see cref="UpdateHotkey"/>, <see cref="UpdateGlobalHotkey"/>,
    /// <see cref="ClearGlobalHotkey"/> or <see cref="ResumeHotkey"/>, which
    /// register both again.
    /// </summary>
    public void SuspendHotkey()
    {
        lock (_gate)
        {
            if (_disposed || _hotkeySuspended)
            {
                return;
            }

            _hotkeySuspended = true;
        }

        _hotkey.Stop();
        _globalHotkey.Stop();
    }

    /// <summary>Registers both configured hotkeys again after a cancelled capture (see <see cref="SuspendHotkey"/>).</summary>
    public void ResumeHotkey()
    {
        lock (_gate)
        {
            if (_disposed || !_hotkeySuspended)
            {
                return;
            }
        }

        RegisterHotkey();
    }

    public void SetDbdPath(string? path)
    {
        lock (_gate)
        {
            Config.DbdExePath = string.IsNullOrWhiteSpace(path) ? null : path;
            DiscoverReshadeIniLocked();
            SyncOutcome sync = SyncShortcutsLocked();
            SetStatusLocked(WithSentences("Dead by Daylight path updated.", sync.Message, TrySaveLocked()));
        }

        RaiseChanged();
    }

    public void SetReshadeIni(string? path)
    {
        lock (_gate)
        {
            Config.ReshadeIniPath = string.IsNullOrWhiteSpace(path) ? null : path;
            SyncOutcome sync = SyncShortcutsLocked();
            SetStatusLocked(WithSentences("ReShade.ini path updated.", sync.Message, TrySaveLocked()));
        }

        RaiseChanged();
    }

    public void SetDebug(bool enabled)
    {
        lock (_gate)
        {
            Config.DebugMode = enabled;
            SaveOrReportLocked();
        }

        RaiseChanged();
    }

    /// <summary>
    /// "Start the next game with the Global preset" (<see cref="AppConfig.StartNextGameWithGlobal"/>):
    /// whether DBD exiting makes the Global preset ReShade's startup preset. Saved (a save failure is
    /// shown on the status line). It changes nothing while DBD runs.
    /// </summary>
    public void SetStartNextGameWithGlobal(bool enabled)
    {
        lock (_gate)
        {
            if (Config.StartNextGameWithGlobal == enabled)
            {
                return;
            }

            Config.StartNextGameWithGlobal = enabled;
            SaveOrReportLocked();
        }

        RaiseChanged();
    }

    /// <summary>Turns the UI animations on or off (saved; a save failure is shown on the status line).</summary>
    public void SetAnimationsEnabled(bool enabled)
    {
        lock (_gate)
        {
            if (Config.AnimationsEnabled == enabled)
            {
                return;
            }

            Config.AnimationsEnabled = enabled;
            SaveOrReportLocked();
        }

        RaiseChanged();
    }

    /// <summary>Turns the Great skill-check sound on or off (saved; a save failure is shown on the status line).</summary>
    public void SetSoundEnabled(bool enabled)
    {
        lock (_gate)
        {
            if (Config.SoundEnabled == enabled)
            {
                return;
            }

            Config.SoundEnabled = enabled;
            SaveOrReportLocked();
        }

        RaiseChanged();
    }

    /// <summary>
    /// Sets the Great skill-check sound volume, clamped to 0..1 (NaN is
    /// ignored). Saved when it changed (a save failure is shown on the status
    /// line), so a slider should call it when the value settles, not per pixel.
    /// </summary>
    public void SetSoundVolume(double volume)
    {
        if (double.IsNaN(volume))
        {
            return;
        }

        double clamped = Math.Clamp(volume, 0.0, 1.0);
        lock (_gate)
        {
            if (Config.SoundVolume.Equals(clamped))
            {
                return;
            }

            Config.SoundVolume = clamped;
            SaveOrReportLocked();
        }

        RaiseChanged();
    }

    /// <summary>
    /// Plays the Great skill-check sound at the configured <see cref="SoundVolume"/>
    /// when <see cref="SoundEnabled"/> (map found, or the Settings preview).
    /// Returns whether it started; false when the sound is off, the volume is 0,
    /// or playback failed. Never throws; any thread.
    /// </summary>
    public bool PlayGreatSound()
    {
        bool enabled;
        double volume;
        lock (_gate)
        {
            enabled = Config.SoundEnabled;
            volume = Config.SoundVolume;
        }

        if (!enabled || !(volume > 0))
        {
            return false;
        }

        try
        {
            return _soundEffects.PlayGreat(volume);
        }
        catch
        {
            return false; // a sound must never break the caller
        }
    }

    /// <summary>
    /// Why the Great skill-check sound can't play whatever the settings (e.g. its
    /// asset is missing from the build), or null when it can. Loads the sound if
    /// that was not tried yet. Never throws; any thread. Shown by
    /// <see cref="RunReshadeDiagnostics"/>.
    /// </summary>
    public string? GreatSoundProblem()
    {
        try
        {
            return _soundEffects.GreatSoundProblem();
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// Sets the database update manifest URL (null/blank disables updates).
    /// Only an absolute https:// URL is saved; otherwise the error is returned
    /// and the setting stays as it was.
    /// </summary>
    public string? SetManifestUrl(string? url)
    {
        string? value = string.IsNullOrWhiteSpace(url) ? null : url.Trim();
        string? error = null;
        lock (_gate)
        {
            if (!DatabaseUpdateService.IsAcceptableManifestUrl(value, out string? invalid))
            {
                error = invalid ?? "The update manifest URL must be an absolute https:// address.";
                SetStatusLocked(error);
            }
            else if (!string.Equals(Config.DatabaseUpdateManifestUrl, value, StringComparison.Ordinal))
            {
                Config.DatabaseUpdateManifestUrl = value;
                SaveOrReportLocked();
            }
        }

        RaiseChanged();
        return error;
    }

    public void CompleteSetup()
    {
        lock (_gate)
        {
            Config.SetupCompleted = true;
            SaveOrReportLocked();
        }

        RaiseChanged();
    }

    /// <summary>Records the loaded map database version in the config (the database may have new realms).</summary>
    public void ApplyDatabaseVersion(int version)
    {
        lock (_gate)
        {
            if (Config.DatabaseVersion != version)
            {
                Config.DatabaseVersion = version;
                SaveOrReportLocked();
            }
        }

        RaiseChanged();
    }

    /// <summary>
    /// Adds <paramref name="message"/> to the status line (e.g. a problem found
    /// while starting), after what it already says, so neither hides the other.
    /// </summary>
    public void ReportWarning(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        lock (_gate)
        {
            SetStatusLocked(WithSentences(_statusMessage, message));
        }

        RaiseChanged();
    }

    /// <summary>
    /// Resets the app to defaults: removes this app's entries from ReShade.ini
    /// (best effort, the user's own shortcuts stay), resets the config,
    /// re-runs discovery, registers both default hotkeys (detection F8, Global
    /// preset F9) and resets the runtime
    /// state (no scan, no pending switch, no override).
    /// </summary>
    public void ResetSettings()
    {
        CancellationTokenSource? scanToStop;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            // (1) Before the config forgets which entries are ours.
            string? cleanup = RemoveManagedShortcutsLocked();

            // (2) Config only: preset files, DBD and ReShade installs are never touched.
            string? resetError = null;
            try
            {
                _configService.Reset();
            }
            catch (Exception ex)
            {
                resetError = $"Could not save settings: {ex.Message}";
            }

            // (5) Runtime state: nothing scanning or pending, no override, and
            // the active preset unknown again.
            scanToStop = _scanCts;
            _scanCts = null;
            ClearDesiredLocked();
            _overrideRealm = null;
            _lastDetection = null;
            _activePresetPath = null;
            _activeRealm = null;
            _activePresetConfirmed = false;
            _activeSwitch++; // a read-back still running confirms nothing now
            _states.TryTransitionTo(_dbd.IsRunning ? AppState.Ready : AppState.WaitingForDbd);
            SetReadyTextLocked();

            // (3) Discovery (+ a sync of the now empty assignments, + save).
            string? discovery = AdoptDiscoveredPathsLocked(ShortcutSnapshotTiming.None);
            SetStatusLocked(WithSentences("Settings reset to defaults.", cleanup, resetError, discovery));
            Interlocked.Increment(ref _settingsResetCount);
        }

        CancelQuietly(scanToStop);

        // (4) The default hotkeys (re-registering the same combo is safe). A
        // registration problem is added to the reset status, never replaces it.
        RegisterHotkey(appendToStatus: true);
        RaiseChanged();
    }

    /// <summary>
    /// Removes every entry this app wrote from ReShade.ini (an empty
    /// assignment list, with every path it manages or managed). Only the app's
    /// own entries go: a pool combo for one of those paths. The user's own
    /// shortcuts stay byte-for-byte, also those for a preset file the app
    /// manages. Returns a user-facing problem, or null. Call with _gate held.
    /// </summary>
    private string? RemoveManagedShortcutsLocked()
    {
        string? iniPath = Config.ReshadeIniPath;
        var managed = AssignedPresetsLocked().Select(a => a.PresetPath)
            .Concat(Config.LastSyncedPresetPaths)
            .ToList();
        if (string.IsNullOrWhiteSpace(iniPath) || !File.Exists(iniPath) || managed.Count == 0)
        {
            return null;
        }

        try
        {
            lock (_iniWriteLock)
            {
                _reshadeConfig.SyncPresetShortcuts(iniPath, Array.Empty<PresetShortcutAssignment>(), managed);
            }

            return null;
        }
        catch (Exception ex)
        {
            return $"Could not remove this app's shortcuts from ReShade.ini ({ex.Message}).";
        }
    }

    // ---------- Internals ----------

    private void RecordActivationLocked(string what, string? presetPath, ReshadeShortcutConfig? shortcut, ActivationResult result)
    {
        string key = shortcut is null ? "(no shortcut assigned)" : ReshadeConfigurationService.FormatShortcut(shortcut);
        string name = string.IsNullOrWhiteSpace(presetPath) ? "(no preset)" : Path.GetFileName(presetPath);
        _lastActivation = new ActivationAttempt(DateTime.Now, $"{what} {name}", key, result.Success, result.Message);
    }

    private void SetReadyTextLocked()
    {
        _detectionText = _dbd.IsRunning ? $"Ready — Press {Config.DetectionHotkey}" : "Waiting for DBD…";
    }

    private long? DeadlineAfter(TimeSpan window) => _clockMs() + (long)window.TotalMilliseconds;

    /// <summary>Best-effort read of ReShade's saved PresetPath (absolute); null when unknown.</summary>
    private static string? ReadCurrentPreset(string? iniPath)
    {
        if (string.IsNullOrWhiteSpace(iniPath))
        {
            return null;
        }

        try
        {
            return ReshadeConfigurationService.ReadCurrentPresetAbsolute(iniPath);
        }
        catch
        {
            return null;
        }
    }

    private static bool IsSamePreset(string? a, string? b) =>
        !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b) &&
        string.Equals(
            PresetValidationService.NormalizePresetPath(a),
            PresetValidationService.NormalizePresetPath(b),
            StringComparison.OrdinalIgnoreCase);

    private static string Prefixed(string context, string message) =>
        string.IsNullOrWhiteSpace(context) ? message : $"{context} {message}";

    /// <summary>"Active preset: X (Realm)." + note → "Active preset: X (Realm) — note."</summary>
    private static string WithNote(string message, string note) => $"{message.TrimEnd().TrimEnd('.')} — {note}.";

    private static string FormatWindow(TimeSpan? window)
    {
        if (window is not TimeSpan w)
        {
            return "the time limit";
        }

        return w.TotalMinutes >= 2 && w.TotalMinutes == Math.Floor(w.TotalMinutes)
            ? $"{(int)w.TotalMinutes} min"
            : $"{(int)Math.Round(w.TotalSeconds)} s";
    }

    /// <summary>
    /// One-click diagnostics: ReShade setup verification plus live runtime facts
    /// (DBD running/focused, hotkey, last activation attempt).
    /// </summary>
    public string RunReshadeDiagnostics()
    {
        // The report reads files and scans folders: it works on a copy of the
        // config (other threads change it under _gate), never under the lock.
        AppConfig config;
        BlockedShortcuts blocked;
        string? iniProblem;
        lock (_gate)
        {
            config = ConfigCopyLocked();
            blocked = BlockedShortcutsLocked();
            iniProblem = blocked.WaitForIni.Count > 0 ? MissingReshadeIniTextLocked() : null;
        }

        var (allOk, report) = ReshadeDiagnosticsService.BuildReport(
            config, Database.Realms.Select(r => r.Name).ToList());
        string loadedLine;
        if (blocked.WaitForIni.Count > 0)
        {
            loadedLine = $"Shortcuts NOT loaded by the running ReShade: {string.Join(", ", blocked.WaitForIni)} — " +
                $"{iniProblem}: set the right ReShade.ini under Settings, then restart DBD";
        }
        else if (blocked.NeedRestart.Count > 0)
        {
            loadedLine = $"Shortcuts NOT loaded by the running ReShade: {string.Join(", ", blocked.NeedRestart)} — " +
                "restart DBD before switching to them";
        }
        else
        {
            loadedLine = "Shortcuts loaded by the running ReShade: all assigned presets (or DBD not running)";
        }

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

        string? soundProblem = GreatSoundProblem();
        string soundLine = soundProblem is not null
            ? $"Great sound: unavailable — {soundProblem}"
            : config.SoundEnabled
                ? $"Great sound: OK (volume {Math.Round(config.SoundVolume * 100, MidpointRounding.AwayFromZero)} %)"
                : "Great sound: OK (turned off under Settings)";

        return report + Environment.NewLine + string.Join(Environment.NewLine, new[]
        {
            string.Empty,
            $"DBD running: {(DbdRunning ? "yes" : "no")}, focused: {(focused ? "yes" : "no")}",
            loadedLine,
            integrityLine,
            $"Detection hotkey: {config.DetectionHotkey}" +
                (_hotkeyError is { } detectionError ? $" — unavailable: {detectionError}" : string.Empty),
            $"Global preset hotkey: {(config.GlobalHotkey is { } globalKey ? globalKey.ToString() : "not set")}" +
                (_globalHotkeyError is { } globalError ? $" — unavailable: {globalError}" : string.Empty),
            soundLine,
            $"Last activation: {LastActivationText}",
            $"Overall: {(allOk ? "setup looks correct" : "problems found — see [!!] lines")}"
        });
    }

    private ReshadeShortcutConfig? LookupShortcutLocked(string? realmOrGlobal)
    {
        if (realmOrGlobal is not null && Config.ReshadeShortcuts.TryGetValue(realmOrGlobal, out var s))
        {
            return s;
        }

        return null;
    }

    /// <summary>
    /// (Re-)registers both configured hotkeys: the detection hotkey, then the
    /// Global preset hotkey (released first, so the detection hotkey can take a
    /// combo it held, e.g. after a reset). A failure is shown on the banner
    /// (<see cref="HotkeyError"/>, <see cref="GlobalHotkeyError"/>) and on the
    /// status line: added after what the status says when <paramref name="appendToStatus"/>
    /// (startup, reset: their own news, e.g. a ReShade.ini problem, must stay
    /// visible), else in its place. Never call with _gate held (see the lock order).
    /// </summary>
    private void RegisterHotkey(bool appendToStatus = false)
    {
        lock (_gate)
        {
            _hotkeySuspended = false;
        }

        _globalHotkey.Stop();
        string? problem = JoinSentences(RegisterDetectionHotkeyCore(), RegisterGlobalHotkeyCore());
        if (problem is not null)
        {
            lock (_gate)
            {
                SetStatusLocked(appendToStatus ? WithSentences(_statusMessage, problem) : problem);
            }
        }

        RaiseChanged();
    }

    /// <summary>
    /// Why <paramref name="requested"/> can't become the detection hotkey before
    /// any registration is tried, or null: one of the app's ReShade shortcut
    /// combos, or the Global preset hotkey's combo (unless it is the detection
    /// hotkey already: a config from before 1.2 with F9 as its detection hotkey
    /// keeps F9, its Global preset hotkey stays unregistered). Call with _gate held.
    /// </summary>
    private string? DetectionHotkeyConflictLocked(DetectionHotkeyConfig requested, DetectionHotkeyConfig current)
    {
        if (HotkeyParser.IsReshadeShortcutCombo(requested, Config.ReshadeShortcuts.Values))
        {
            return HotkeyParser.ReservedForReshadeError(requested);
        }

        if (HotkeyParser.SameCombo(requested, Config.GlobalHotkey) && !HotkeyParser.SameCombo(requested, current))
        {
            return $"{requested} is already the Global preset hotkey.";
        }

        return null;
    }

    /// <summary>
    /// Registers the configured detection hotkey and records the outcome in
    /// <see cref="HotkeyError"/>. Returns the user-facing problem, or null.
    /// Never call with _gate held.
    /// </summary>
    private string? RegisterDetectionHotkeyCore() => RegisterDetectionHotkeyCore(out _);

    /// <summary>
    /// Registers the configured detection hotkey (released instead when it is
    /// one of the app's ReShade shortcut combos, see
    /// <see cref="HotkeyParser.IsReshadeShortcutCombo"/>) and records the outcome
    /// in <see cref="HotkeyError"/> (also returned as <paramref name="error"/>).
    /// Returns the user-facing problem, or null. Never call with _gate held.
    /// </summary>
    private string? RegisterDetectionHotkeyCore(out string? error)
    {
        DetectionHotkeyConfig hotkey;
        bool reserved;
        lock (_gate)
        {
            hotkey = Config.DetectionHotkey;
            reserved = HotkeyParser.IsReshadeShortcutCombo(hotkey, Config.ReshadeShortcuts.Values);
        }

        string? registrationError;
        if (reserved)
        {
            _hotkey.Stop(); // it would swallow the app's own preset switches
            registrationError = HotkeyParser.ReservedForReshadeError(hotkey);
        }
        else
        {
            registrationError = _hotkey.Start(hotkey);
        }

        lock (_gate)
        {
            _hotkeyError = registrationError ?? _hotkey.LastError;
            error = _hotkeyError;
            return _hotkeyError is null ? null : $"Hotkey '{hotkey}' unavailable: {_hotkeyError}";
        }
    }

    /// <summary>
    /// Registers the configured Global preset hotkey and records the outcome in
    /// <see cref="GlobalHotkeyError"/>. Returns the user-facing problem, or
    /// null. Never call with _gate held.
    /// </summary>
    private string? RegisterGlobalHotkeyCore() => RegisterGlobalHotkeyCore(out _);

    /// <summary>
    /// Registers the configured Global preset hotkey (releases it when cleared,
    /// invalid, one of the app's ReShade shortcut combos, or the detection
    /// hotkey's combo) and records the outcome in <see cref="GlobalHotkeyError"/>
    /// (also returned as <paramref name="error"/>). Returns the user-facing
    /// problem, or null. Never call with _gate held.
    /// </summary>
    private string? RegisterGlobalHotkeyCore(out string? error)
    {
        DetectionHotkeyConfig? hotkey;
        DetectionHotkeyConfig detection;
        bool reserved;
        lock (_gate)
        {
            hotkey = Config.GlobalHotkey;
            detection = Config.DetectionHotkey;
            reserved = HotkeyParser.IsReshadeShortcutCombo(hotkey, Config.ReshadeShortcuts.Values);
        }

        error = null;
        if (hotkey is null)
        {
            _globalHotkey.Stop();
        }
        else if (reserved)
        {
            // A press of the Global preset's own combo would trigger it again and
            // again; a realm's combo would land on Global instead of the realm.
            _globalHotkey.Stop();
            error = HotkeyParser.ReservedForReshadeError(hotkey);
        }
        else if (HotkeyParser.SameCombo(hotkey, detection))
        {
            _globalHotkey.Stop();
            error = $"{hotkey} is also the detection hotkey; choose another key for the Global preset.";
        }
        else if (!HotkeyParser.TryParse(hotkey, out _, out _, out string? parseError))
        {
            _globalHotkey.Stop(); // an invalid hotkey registers nothing (not the previous one either)
            error = parseError ?? $"Unsupported hotkey '{hotkey}'.";
        }
        else
        {
            error = _globalHotkey.Start(hotkey) ?? _globalHotkey.LastError;
        }

        lock (_gate)
        {
            _globalHotkeyError = error;
        }

        return error is null ? null : $"Global preset hotkey '{hotkey}' unavailable: {error}";
    }

    private static bool IsRegistered(IGlobalHotkeyService service, DetectionHotkeyConfig hotkey) =>
        service.RegisteredHotkey is { } registered &&
        string.Equals(registered.Key, hotkey.Key, StringComparison.OrdinalIgnoreCase) &&
        registered.Ctrl == hotkey.Ctrl && registered.Shift == hotkey.Shift && registered.Alt == hotkey.Alt;

    private static DetectionHotkeyConfig CopyHotkey(DetectionHotkeyConfig hotkey) => new()
    {
        Key = hotkey.Key,
        Ctrl = hotkey.Ctrl,
        Shift = hotkey.Shift,
        Alt = hotkey.Alt
    };

    /// <summary>
    /// Adopts discovered DBD/ReShade paths (only where none, or a missing one,
    /// is configured), syncs the shortcuts and saves. <paramref name="snapshot"/>
    /// says when to record what the running DBD's ReShade loaded. Never throws;
    /// returns a user-facing problem (or notice), else null. Call with _gate held.
    /// </summary>
    private string? AdoptDiscoveredPathsLocked(ShortcutSnapshotTiming snapshot)
    {
        if (string.IsNullOrWhiteSpace(Config.DbdExePath))
        {
            string? found = TryDiscover(_discoverDbdExePath);
            if (!string.IsNullOrWhiteSpace(found))
            {
                Config.DbdExePath = found;
            }
        }

        // Prefer the live process path when available.
        if (_dbd.IsRunning && !string.IsNullOrWhiteSpace(_dbd.ExePath) && File.Exists(_dbd.ExePath))
        {
            Config.DbdExePath = _dbd.ExePath;
        }

        DiscoverReshadeIniLocked();
        if (snapshot == ShortcutSnapshotTiming.BeforeSync)
        {
            TakeShortcutSnapshotLocked();
        }

        SyncOutcome sync = SyncShortcutsLocked();
        if (snapshot == ShortcutSnapshotTiming.AfterSync)
        {
            TakeShortcutSnapshotLocked();
        }

        return JoinSentences(sync.Message, TrySaveLocked());
    }

    /// <summary>Finds ReShade.ini for the configured DBD when no existing one is configured. Call with _gate held.</summary>
    private void DiscoverReshadeIniLocked()
    {
        string? current = Config.ReshadeIniPath;
        if (!string.IsNullOrWhiteSpace(current) && File.Exists(current))
        {
            return;
        }

        string? exe = Config.DbdExePath;
        string? found = TryDiscover(() => _discoverReshadeIniPath(exe));
        if (!string.IsNullOrWhiteSpace(found))
        {
            Config.ReshadeIniPath = found;
        }
    }

    private static string? TryDiscover(Func<string?> discover)
    {
        try
        {
            return discover();
        }
        catch
        {
            return null; // discovery must never break startup
        }
    }

    /// <summary>
    /// Records the shortcut entries the running DBD's ReShade loaded (ReShade
    /// reads them only while the game starts), keyed by its PID, its creation
    /// time and the ReShade.ini they came from, in memory and on disk (an app
    /// restart while DBD keeps running restores it). Without a readable
    /// ReShade.ini it loaded none of this app's entries as far as anyone can
    /// tell, so the snapshot is empty: nothing unconfirmed is ever sent. Call
    /// with _gate held.
    /// </summary>
    private void TakeShortcutSnapshotLocked()
    {
        if (!_dbd.IsRunning)
        {
            _reshadeLoadedSnapshot = null;
            _snapshotStore.Delete();
            return;
        }

        string? iniPath = Config.ReshadeIniPath;
        IReadOnlyList<IniShortcutEntry> entries;
        try
        {
            entries = string.IsNullOrWhiteSpace(iniPath)
                ? Array.Empty<IniShortcutEntry>()
                : ReshadeConfigurationService.ReadShortcutAssignments(iniPath);
        }
        catch
        {
            entries = Array.Empty<IniShortcutEntry>();
        }

        int? pid = _dbd.ProcessId;
        _reshadeLoadedSnapshot = new ReshadeLoadedSnapshot(pid, StartTimeOf(pid), iniPath, entries);
        _snapshotStore.Save(_reshadeLoadedSnapshot);
    }

    /// <summary>The creation time of process <paramref name="processId"/>; null when unknown. Never throws.</summary>
    private DateTime? StartTimeOf(int? processId)
    {
        if (processId is not int pid)
        {
            return null;
        }

        try
        {
            return _processStartTime(pid);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>True when the configured ReShade.ini is not set or doesn't exist. Call with _gate held.</summary>
    private bool ReshadeIniMissingLocked() =>
        string.IsNullOrWhiteSpace(Config.ReshadeIniPath) || !File.Exists(Config.ReshadeIniPath);

    /// <summary>"ReShade.ini is not set" / "ReShade.ini was not found at '…'". Call with _gate held.</summary>
    private string MissingReshadeIniTextLocked() => string.IsNullOrWhiteSpace(Config.ReshadeIniPath)
        ? "ReShade.ini is not set"
        : $"ReShade.ini was not found at '{Config.ReshadeIniPath}'";

    /// <summary>
    /// Why <paramref name="shortcut"/> must not be sent for <paramref name="presetPath"/>
    /// (see <see cref="ShortcutNotLoadedLocked"/>), saying what fixes it; null when
    /// nothing is known to be wrong. Without a ReShade.ini a DBD restart alone
    /// can't help, so that case says to set it first. Call with _gate held.
    /// </summary>
    private string? NotLoadedMessageLocked(string presetPath, ReshadeShortcutConfig? shortcut, string shortcutName)
    {
        if (!ShortcutNotLoadedLocked(presetPath, shortcut))
        {
            return null;
        }

        string display = AppConfigKeys.DisplayName(shortcutName);
        return ReshadeIniMissingLocked()
            ? $"{MissingReshadeIniTextLocked()} — set the right ReShade.ini under Settings, " +
              $"then restart Dead by Daylight so ReShade loads the shortcut for {display}."
            : $"Restart Dead by Daylight so ReShade loads the shortcut for {display}.";
    }

    /// <summary>
    /// The display names of the assigned presets the running ReShade has not
    /// loaded, under what fixes it: a DBD restart (the configured ReShade.ini
    /// exists) or setting ReShade.ini first (it doesn't). Call with _gate held.
    /// </summary>
    /// <summary>The banner text for <paramref name="blocked"/> (see <see cref="BlockedShortcutsNotice"/>). Call with _gate held.</summary>
    private string? BlockedShortcutsNoticeLocked(BlockedShortcuts blocked)
    {
        if (blocked.WaitForIni.Count > 0)
        {
            bool one = blocked.WaitForIni.Count == 1;
            return $"{MissingReshadeIniTextLocked()}, so ReShade can't load the preset shortcut{(one ? string.Empty : "s")} " +
                   $"for: {string.Join(", ", blocked.WaitForIni)}. Set the right ReShade.ini under Settings, " +
                   $"then restart Dead by Daylight. Until then {(one ? "it" : "they")} can't be switched to.";
        }

        if (blocked.NeedRestart.Count > 0)
        {
            bool one = blocked.NeedRestart.Count == 1;
            return $"Restart Dead by Daylight so ReShade loads the new preset shortcut{(one ? string.Empty : "s")} " +
                   $"for: {string.Join(", ", blocked.NeedRestart)}. Until then {(one ? "it" : "they")} can't be switched to.";
        }

        return null;
    }

    private BlockedShortcuts BlockedShortcutsLocked()
    {
        var names = AssignedPresetsLocked()
            .Where(a => ShortcutNotLoadedLocked(a.PresetPath, LookupShortcutLocked(a.Name)))
            .Select(a => AppConfigKeys.DisplayName(a.Name))
            .ToList();
        if (names.Count == 0)
        {
            return new BlockedShortcuts(Array.Empty<string>(), Array.Empty<string>());
        }

        return ReshadeIniMissingLocked()
            ? new BlockedShortcuts(Array.Empty<string>(), names)
            : new BlockedShortcuts(names, Array.Empty<string>());
    }

    /// <summary>
    /// True when the running DBD's ReShade has not loaded <paramref name="shortcut"/>
    /// as the shortcut for <paramref name="presetPath"/>, so sending it could
    /// switch to a different preset. False when nothing is known to be wrong:
    /// DBD not running, no shortcut (that failure is reported separately), or
    /// a newer DBD process than the snapshot's (it loads the synced file). A
    /// snapshot of another ReShade.ini than the configured one confirms nothing.
    /// Call with _gate held.
    /// </summary>
    private bool ShortcutNotLoadedLocked(string presetPath, ReshadeShortcutConfig? shortcut)
    {
        if (shortcut is null || !_dbd.IsRunning ||
            _reshadeLoadedSnapshot is not { } snapshot || snapshot.ProcessId != _dbd.ProcessId)
        {
            return false;
        }

        if (!string.Equals(
                PresetValidationService.NormalizePresetPath(snapshot.IniPath),
                PresetValidationService.NormalizePresetPath(Config.ReshadeIniPath),
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string target = PresetValidationService.NormalizePresetPath(presetPath);
        return !snapshot.Entries.Any(e =>
            ReshadeConfigurationService.SameCombo(e.Shortcut, shortcut) &&
            string.Equals(e.PresetPath, target, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The shortcut entries the running DBD's ReShade loaded, in ReShade.ini
    /// order: the snapshot when it describes the running process and the
    /// configured ReShade.ini; null when it doesn't (a newer process loads the
    /// configured ReShade.ini as it is now). Call with _gate held.
    /// </summary>
    private IReadOnlyList<IniShortcutEntry>? LoadedShortcutEntriesLocked() =>
        _dbd.IsRunning &&
        _reshadeLoadedSnapshot is { } snapshot && snapshot.ProcessId == _dbd.ProcessId &&
        string.Equals(
            PresetValidationService.NormalizePresetPath(snapshot.IniPath),
            PresetValidationService.NormalizePresetPath(Config.ReshadeIniPath),
            StringComparison.OrdinalIgnoreCase)
            ? snapshot.Entries
            : null;

    /// <summary>
    /// Why sending <paramref name="shortcut"/> would switch the running ReShade to
    /// another preset than <paramref name="presetPath"/> even though it loaded that
    /// pair: ReShade switches to the FIRST entry the keys fire, and with its
    /// "Force correct modifier keys" off held extra modifiers don't stop an entry,
    /// so an earlier entry on the same key (the user's own Ctrl+1 for the app's
    /// Ctrl+Alt+Shift+1) fires instead. That happens when the option is turned off
    /// while DBD runs (nothing re-syncs), or when every free combo is taken so the
    /// sync could not move the preset off the user's key. Null when nothing is
    /// known to be wrong. <paramref name="loaded"/>: what the running ReShade
    /// loaded (null: the configured ReShade.ini as it is now, read on every call).
    /// Reads the option's current value from ReShade.ini only when the first entry
    /// the keys can fire is not this preset's exact entry (ReShade saves the option
    /// there as soon as it changes; unreadable counts as off). Never throws. Call
    /// without _gate.
    /// </summary>
    private static string? OtherShortcutFiresFirstMessage(
        string presetPath, ReshadeShortcutConfig? shortcut, string? iniPath, IReadOnlyList<IniShortcutEntry>? loaded)
    {
        if (shortcut is null || string.IsNullOrWhiteSpace(iniPath))
        {
            return null;
        }

        try
        {
            loaded ??= ReshadeConfigurationService.ReadShortcutAssignments(iniPath);
        }
        catch
        {
            return null; // nothing known: the snapshot check has the final say
        }

        IniShortcutEntry? fired = ReshadeConfigurationService.EntryFiredBy(loaded, shortcut, exactModifiers: false);
        if (fired is null)
        {
            return null;
        }

        // Every exact match is also a loose match, so when the first loose match is
        // this preset's EXACT entry it is the first entry under either setting. A
        // looser entry for this preset that comes first proves nothing when ReShade
        // matches modifiers exactly: a later exact entry for another preset could fire.
        if (ReshadeConfigurationService.SameCombo(fired.Shortcut, shortcut) && IsSamePreset(fired.PresetPath, presetPath))
        {
            return null;
        }

        bool exact;
        try
        {
            exact = ReshadeConfigurationService.ReadForcesExactModifiers(iniPath);
        }
        catch
        {
            exact = false;
        }

        if (!exact && IsSamePreset(fired.PresetPath, presetPath))
        {
            return null; // loose matching: the first entry the keys can fire is this preset's
        }

        if (exact)
        {
            fired = ReshadeConfigurationService.EntryFiredBy(loaded, shortcut, exactModifiers: true);
            if (fired is null || IsSamePreset(fired.PresetPath, presetPath))
            {
                return null;
            }

            return $"{ReshadeConfigurationService.FormatShortcut(shortcut)} is also the ReShade shortcut for " +
                   $"{fired.PresetPath}, which ReShade checks first. Assign the preset again so the app picks " +
                   "a free shortcut, then restart Dead by Daylight.";
        }

        return $"{ReshadeConfigurationService.FormatShortcut(shortcut)} would also trigger your ReShade shortcut " +
               $"{ReshadeConfigurationService.FormatShortcut(fired.Shortcut)} → {fired.PresetPath}, which ReShade " +
               "checks first, because ReShade's 'Force correct modifier keys' option is off. " +
               "Turn that option on in ReShade's settings.";
    }

    /// <summary>Every assigned preset: Global (when set) first, then the realms in ordinal order. Call with _gate held.</summary>
    private List<(string Name, string PresetPath)> AssignedPresetsLocked()
    {
        var assigned = new List<(string Name, string PresetPath)>();
        if (!string.IsNullOrWhiteSpace(Config.GlobalPresetPath))
        {
            assigned.Add((AppConfigKeys.GlobalShortcutKey, Config.GlobalPresetPath));
        }

        assigned.AddRange(Config.RealmPresets
            .Where(kv => !string.IsNullOrWhiteSpace(kv.Value))
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => (kv.Key, kv.Value)));
        return assigned;
    }

    /// <summary>
    /// Brings ReShade.ini's shortcut entries in line with the assigned presets,
    /// transactionally: combos are allocated stably (a name keeps its combo,
    /// new names get the lowest free one, the user's own combos are skipped),
    /// ReShade.ini is written, and only then are the combos and the synced
    /// paths committed to the config. On failure (pool exhausted, ReShade.ini
    /// unreadable or not writable) the config keeps its old shortcuts and the
    /// outcome carries the error. Without a ReShade.ini the combos are still
    /// allocated, ready for when it appears. Never throws. Call with _gate held.
    /// </summary>
    private SyncOutcome SyncShortcutsLocked()
    {
        List<(string Name, string PresetPath)> assigned = AssignedPresetsLocked();
        string? iniPath = Config.ReshadeIniPath;
        bool iniExists = !string.IsNullOrWhiteSpace(iniPath) && File.Exists(iniPath);

        Dictionary<string, ReshadeShortcutConfig> allocated;
        try
        {
            // Pool-combo entries of paths this app manages (or managed) are its
            // own; every other combo belongs to the user (also one for an
            // assigned preset file) and is never handed out, nor is a combo
            // that would also fire it (ReShade's ForceShortcutModifiers=0).
            IReadOnlyList<ReshadeShortcutConfig> reserved = iniExists
                ? ReshadeConfigurationService.ReadReservedShortcuts(
                    iniPath!, assigned.Select(a => a.PresetPath).Concat(Config.LastSyncedPresetPaths))
                : Array.Empty<ReshadeShortcutConfig>();
            allocated = ReshadeConfigurationService.AllocateShortcuts(
                assigned.Select(a => a.Name), Config.ReshadeShortcuts, reserved);
        }
        catch (InvalidOperationException ex)
        {
            return SyncOutcome.Failed(ex.Message); // the shortcut pool is exhausted
        }
        catch (Exception ex)
        {
            return SyncOutcome.Failed($"Could not read ReShade.ini ({ex.Message}). Nothing was changed.");
        }

        if (!iniExists)
        {
            CommitShortcutsLocked(allocated);
            return assigned.Count == 0
                ? SyncOutcome.Done(null)
                : SyncOutcome.Done("ReShade.ini not found — shortcuts will be written when it is.");
        }

        var assignments = assigned
            .Select(a => new PresetShortcutAssignment(a.Name, a.PresetPath, allocated[a.Name]))
            .ToList();
        ShortcutSyncResult result;
        try
        {
            lock (_iniWriteLock)
            {
                result = _reshadeConfig.SyncPresetShortcuts(iniPath!, assignments, Config.LastSyncedPresetPaths);
            }
        }
        catch (Exception ex)
        {
            return SyncOutcome.Failed(
                $"Could not update the preset shortcuts in ReShade.ini ({ex.Message}). Nothing was changed.");
        }

        // A skipped entry gets no combo: nothing ever sends a combo that another
        // ReShade.ini entry uses.
        foreach (ShortcutConflict conflict in result.Conflicts)
        {
            allocated.Remove(conflict.Name);
        }

        CommitShortcutsLocked(allocated);
        Config.LastSyncedPresetPaths = assignments
            .Select(a => PresetValidationService.NormalizePresetPath(a.PresetPath))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return SyncOutcome.Done(result.HasConflicts
            ? "Some preset shortcuts were not written: " + string.Join(" ", result.Conflicts.Select(c => c.Message))
            : null);
    }

    /// <summary>
    /// A copy of the config whose collections can be read without _gate. The
    /// combo and hotkey objects are shared: they are replaced, never changed.
    /// Call with _gate held.
    /// </summary>
    private AppConfig ConfigCopyLocked()
    {
        AppConfig c = Config;
        return new AppConfig
        {
            SchemaVersion = c.SchemaVersion,
            SetupCompleted = c.SetupCompleted,
            DbdExePath = c.DbdExePath,
            ReshadeIniPath = c.ReshadeIniPath,
            GlobalPresetPath = c.GlobalPresetPath,
            RealmPresets = new Dictionary<string, string>(c.RealmPresets, StringComparer.OrdinalIgnoreCase),
            DetectionHotkey = c.DetectionHotkey,
            ReshadeShortcuts = new Dictionary<string, ReshadeShortcutConfig>(c.ReshadeShortcuts, StringComparer.OrdinalIgnoreCase),
            LastSyncedPresetPaths = c.LastSyncedPresetPaths.ToList(),
            DebugMode = c.DebugMode,
            DatabaseVersion = c.DatabaseVersion,
            DatabaseUpdateManifestUrl = c.DatabaseUpdateManifestUrl,
            StartNextGameWithGlobal = c.StartNextGameWithGlobal,
            GlobalHotkey = c.GlobalHotkey,
            AnimationsEnabled = c.AnimationsEnabled,
            SoundEnabled = c.SoundEnabled,
            SoundVolume = c.SoundVolume
        };
    }

    private void CommitShortcutsLocked(Dictionary<string, ReshadeShortcutConfig> allocated) =>
        Config.ReshadeShortcuts = new Dictionary<string, ReshadeShortcutConfig>(allocated, StringComparer.OrdinalIgnoreCase);

    /// <summary>Saves the config; returns a user-facing message when that failed (never throws). Call with _gate held.</summary>
    private string? TrySaveLocked()
    {
        try
        {
            _configService.Save();
            _lastSaveError = null;
            return null;
        }
        catch (Exception ex)
        {
            _lastSaveError = $"Could not save settings: {ex.Message}";
            return _lastSaveError;
        }
    }

    /// <summary>Saves the config and shows a failure on the status line. Call with _gate held.</summary>
    private void SaveOrReportLocked()
    {
        if (TrySaveLocked() is { } error)
        {
            SetStatusLocked(error);
        }
    }

    /// <summary>
    /// Joins the non-blank sentences with spaces, each one once (a reset whose config could not be saved would
    /// otherwise say the same save error twice); null when there are none.
    /// </summary>
    private static string? JoinSentences(params string?[] sentences)
    {
        string joined = string.Join(" ", sentences.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!.Trim()).Distinct(StringComparer.Ordinal));
        return joined.Length == 0 ? null : joined;
    }

    /// <summary><paramref name="first"/>, followed by the non-blank sentences of <paramref name="more"/>.</summary>
    private static string WithSentences(string first, params string?[] more) => JoinSentences([first, .. more]) ?? first;

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

    /// <summary>Raises <see cref="MapDetected"/>. Never call with a controller lock held.</summary>
    private void RaiseMapDetected(MapDetectedEventArgs args)
    {
        try
        {
            MapDetected?.Invoke(this, args);
        }
        catch
        {
            // Subscribers must not break the controller (or the switch that follows).
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (!_disposed)
            {
                _disposed = true;
                _desired = null;
            }
        }

        CancelScan();
        try
        {
            _shutdownCts.Cancel();
        }
        catch
        {
            // Best effort.
        }
    }

    // ---------- Activation request model ----------

    private enum ActivationOrigin
    {
        Detected,
        ManualOverride,
        ReturnToAutomatic,
        Test,
        ScanWithoutResult,

        /// <summary>The Global preset hotkey (<see cref="ActivateGlobalNow"/> from a press).</summary>
        Hotkey,

        /// <summary><see cref="ActivateGlobalNow"/> requested otherwise (e.g. a UI button).</summary>
        GlobalRequest
    }

    private enum AttemptOutcome
    {
        /// <summary>The preset was switched.</summary>
        Succeeded,

        /// <summary>Still the target: waiting for DBD focus, or retrying a rejected send.</summary>
        Waiting,

        /// <summary>Final failure (a spent send budget or a passed deadline included); the status says why.</summary>
        Failed,

        /// <summary>A newer request (or a cleared target) replaced this one.</summary>
        Superseded
    }

    /// <param name="Quiet">Nothing observable changed: no <see cref="Changed"/> notification.</param>
    private readonly record struct AttemptResult(AttemptOutcome Outcome, string? Error, bool Quiet = false);

    /// <param name="Generation">The send's status generation: the result is reported only while it is current.</param>
    /// <param name="ActiveSwitch">The send's <c>_activeSwitch</c>: a confirmation counts only while it is current.</param>
    private sealed record PendingVerification(
        int Generation, long ActiveSwitch, string IniPath, string ExpectedPreset, string SuccessMessage);

    /// <summary>When <see cref="AdoptDiscoveredPathsLocked"/> records what the running ReShade loaded.</summary>
    private enum ShortcutSnapshotTiming
    {
        /// <summary>Keep the current snapshot (same process).</summary>
        None,

        /// <summary>DBD was already running when the app started: it loaded the file as it was before the sync.</summary>
        BeforeSync,

        /// <summary>A new DBD process: it loads the file while the game starts, i.e. after the sync.</summary>
        AfterSync
    }

    /// <param name="NeedRestart">Not loaded; a DBD restart loads them (ReShade.ini exists).</param>
    /// <param name="WaitForIni">Not loaded; ReShade.ini is not set or missing, so it must be set first.</param>
    private sealed record BlockedShortcuts(IReadOnlyList<string> NeedRestart, IReadOnlyList<string> WaitForIni);

    /// <param name="Succeeded">False when nothing was changed because of an error.</param>
    /// <param name="Message">The error (failure), or a notice for the user (success); null when there is nothing to say.</param>
    private readonly record struct SyncOutcome(bool Succeeded, string? Message)
    {
        public static SyncOutcome Done(string? notice) => new(true, notice);

        public static SyncOutcome Failed(string error) => new(false, error);
    }

    /// <summary>The desired activation. Mutable members are guarded by _gate.</summary>
    private sealed class ActivationRequest(
        ActivationOrigin origin,
        string presetPath,
        string shortcutName,
        string? realm,
        string what,
        string context,
        string successMessage,
        TimeSpan? window,
        long? deadlineMs)
    {
        public ActivationOrigin Origin { get; } = origin;

        public string PresetPath { get; } = presetPath;

        /// <summary>Key into <see cref="AppConfig.ReshadeShortcuts"/> (realm name or the Global key).</summary>
        public string ShortcutName { get; } = shortcutName;

        /// <summary>The realm that becomes <see cref="ActiveRealm"/> on success (null for Global).</summary>
        public string? Realm { get; } = realm;

        /// <summary>Label for <see cref="LastActivationText"/>.</summary>
        public string What { get; } = what;

        /// <summary>What led to the request; prefixes waiting/failure status texts (may be empty).</summary>
        public string Context { get; } = context;

        public string SuccessMessage { get; } = successMessage;

        public TimeSpan? Window { get; } = window;

        /// <summary>Monotonic deadline (ms) for waiting on focus; null = none.</summary>
        public long? DeadlineMs { get; } = deadlineMs;

        public string Name => Path.GetFileName(PresetPath);

        public int SendFailures { get; set; }

        public string? LastSendError { get; set; }

        /// <summary>A Test's number (<see cref="PendingTest.Serial"/>); 0 for other requests.</summary>
        public long Serial { get; init; }

        public bool WorkerRunning { get; set; }
    }

    /// <summary>
    /// The Global preset hotkey when the host passes none (tests, tools): it
    /// validates and remembers a hotkey but registers nothing with Windows and
    /// never fires.
    /// </summary>
    private sealed class UnregisteredHotkeyService : IGlobalHotkeyService
    {
        private volatile DetectionHotkeyConfig? _registered;

        public bool IsRegistered => _registered is not null;

        public DetectionHotkeyConfig? RegisteredHotkey => _registered;

        public string? LastError => null;

        public event EventHandler? HotkeyPressed
        {
            add { }
            remove { }
        }

        public string? Start(DetectionHotkeyConfig hotkey)
        {
            ArgumentNullException.ThrowIfNull(hotkey);
            if (!HotkeyParser.TryParse(hotkey, out _, out _, out string? error))
            {
                return error ?? $"Unsupported hotkey '{hotkey}'.";
            }

            _registered = CopyHotkey(hotkey);
            return null;
        }

        public void Stop() => _registered = null;

        public void Dispose() => _registered = null;
    }
}

/// <summary>One recorded preset-switch attempt (shown on the Status tab).</summary>
public sealed record ActivationAttempt(DateTime At, string What, string Key, bool Success, string Message);

/// <summary>A Test switch waiting for DBD focus (<see cref="AppController.PendingTest"/>).</summary>
/// <param name="Name">The realm tested, or <see cref="AppConfigKeys.GlobalShortcutKey"/> for the Global preset.</param>
/// <param name="PresetName">The preset's file name.</param>
/// <param name="Remaining">How long it still waits for focus before it gives up.</param>
public sealed record PendingTest(string Name, string PresetName, TimeSpan Remaining, long Serial = 0);

/// <summary>How a Test ended by itself (<see cref="AppController.LastTestOutcome"/>).</summary>
/// <param name="Name">The Test's shortcut name (a realm, or the Global key), as in <see cref="PendingTest.Name"/>.</param>
/// <param name="Serial">The Test's number (<see cref="PendingTest.Serial"/>).</param>
/// <param name="Message">Its outcome, as the status line said it ("Test: switched to …", "Test failed: …", "Test of … cancelled — …").</param>
public sealed record TestOutcome(string Name, long Serial, string Message);

/// <summary>The assigned presets the running ReShade can't switch to yet (<see cref="AppController.BlockedPresetShortcuts"/>).</summary>
/// <param name="NeedingDbdRestart">Display names a DBD restart fixes (<see cref="AppController.NamesNeedingDbdRestart"/>).</param>
/// <param name="WaitingForReshadeIni">Display names that need the right ReShade.ini first (<see cref="AppController.NamesWaitingForReshadeIni"/>).</param>
/// <param name="Notice">The banner text (<see cref="AppController.BlockedShortcutsNotice"/>); null when none is blocked.</param>
public sealed record BlockedPresetShortcuts(IReadOnlyList<string> NeedingDbdRestart, IReadOnlyList<string> WaitingForReshadeIni, string? Notice);

/// <summary>A confident detection (<see cref="AppController.MapDetected"/>).</summary>
public sealed class MapDetectedEventArgs(string realm, string? map, double confidence) : EventArgs
{
    /// <summary>The detected realm (as in the map database).</summary>
    public string Realm { get; } = realm;

    /// <summary>The detected map; null when only the realm was read.</summary>
    public string? Map { get; } = map;

    /// <summary>The read's confidence, 0..1.</summary>
    public double Confidence { get; } = confidence;
}
