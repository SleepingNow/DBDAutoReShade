using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using DBDAutoReShade.App.Services;
using DBDAutoReShade.Core.Models;
using DBDAutoReShade.Core.Services;
using DBDAutoReShade.Desktop.Services;

namespace DBDAutoReShade.Tests;

/// <summary>Options for <see cref="ControllerHarness"/>.</summary>
internal sealed record ControllerHarnessOptions
{
    /// <summary>Activation worker cadence. The 30 s default keeps the worker out of the way (tests drive attempts).</summary>
    public TimeSpan RetryInterval { get; init; } = TimeSpan.FromSeconds(30);

    public int MaxAttempts { get; init; } = 20;

    public bool WithPresets { get; init; } = true;

    public int VerifyDelayMs { get; init; } = 2000;

    public int? ProcessId { get; init; } = 4242;

    public Action<AppConfig>? Configure { get; init; }

    public Func<TimeSpan, CancellationToken, Task>? Delay { get; init; }

    /// <summary>
    /// The shortcuts of the configured presets are already in ReShade.ini when
    /// the app starts (synced on an earlier run, then DBD was started), so the
    /// running ReShade loaded them. False: ReShade.ini has no shortcuts yet.
    /// </summary>
    public bool ShortcutsLoadedByReshade { get; init; } = true;

    /// <summary>DBD is already running when the app starts (false: launched later via the monitor).</summary>
    public bool DbdRunningAtStart { get; init; } = true;

    /// <summary>
    /// Like the real monitor: DBD (already running) is only seen by the first
    /// poll, which runs inside <see cref="IDbdMonitor.Start"/> and raises
    /// StatusChanged right there. Implies DBD is not "running" before Start.
    /// </summary>
    public bool DbdSeenByFirstPoll { get; init; }

    /// <summary>Discovery of an installed DBD (default: none found; never scans the real machine).</summary>
    public Func<string?> DiscoverDbdExe { get; init; } = static () => null;

    /// <summary>Discovery of ReShade.ini for a DBD executable (default: none found).</summary>
    public Func<string?, string?> DiscoverReshadeIni { get; init; } = static _ => null;

    /// <summary>
    /// A process's creation time. Default: a fixed time per PID, so the same
    /// PID is the same process (a reused PID needs a different function).
    /// </summary>
    public Func<int, DateTime?> ProcessStartTime { get; init; } =
        static pid => new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(pid);

    /// <summary>
    /// The app is restarted: this harness is shut down, and the new one runs on
    /// its config.json, ReShade.ini and preset files, with the same DBD process
    /// still running (unless <see cref="DbdMeanwhile"/> changes that). Presets,
    /// Configure and ShortcutsLoadedByReshade don't apply: the config is loaded from disk.
    /// </summary>
    public ControllerHarness? RestartOf { get; init; }

    /// <summary>With <see cref="RestartOf"/>: what happened to DBD while the app was closed (e.g. a new PID, or not running).</summary>
    public Action<ControllerHarness.FakeDbdMonitor>? DbdMeanwhile { get; init; }

    /// <summary>Hotkeys another program already holds when the app starts (registering one fails).</summary>
    public IReadOnlyList<DetectionHotkeyConfig> HotkeysHeldElsewhere { get; init; } = Array.Empty<DetectionHotkeyConfig>();

    /// <summary>Manually driven by default (huge poll interval): tests call PollOnce.</summary>
    public LifecycleOptions Lifecycle { get; init; } = new(
        PollIntervalMs: 3_600_000,
        TrialTransitionDistance: 30,
        TrialConfirmPolls: 3,
        MenuTransitionDistance: 35,
        MenuConfirmPolls: 4,
        MinimumTrialSeconds: 0);
}

/// <summary>
/// The real <see cref="AppController"/> wired to fakes: DBD monitor, global
/// hotkey (nothing is registered with Windows), key simulator, focus, OCR,
/// capture (lifecycle frames by grey level), a manual monotonic clock and a
/// manually polled lifecycle.
/// </summary>
internal sealed class ControllerHarness : IDisposable
{
    internal sealed class FakeDbdMonitor : IDbdMonitor
    {
        private volatile bool _isRunning = true;
        private volatile StrongBox<int?> _processId = new(null);

        public bool IsRunning
        {
            get => _isRunning;
            set => _isRunning = value;
        }

        public IntPtr MainWindowHandle { get; set; } = new(0x1234);
        public string? ExePath { get; set; }

        public int? ProcessId
        {
            get => _processId.Value;
            set => _processId = new StrongBox<int?>(value); // whole-value swap: safe to read from other threads
        }

        public event EventHandler<DbdStatusEventArgs>? StatusChanged;

        public void RaiseStatusChanged(bool running) =>
            StatusChanged?.Invoke(this, new DbdStatusEventArgs(running, MainWindowHandle, ExePath, ProcessId));

        /// <summary>Runs inside <see cref="Start"/> (the real monitor's synchronous first poll).</summary>
        public Action? OnStart { get; set; }

        public void Start() => OnStart?.Invoke();

        public void Stop()
        {
        }

        public void Dispose()
        {
        }
    }

    /// <summary>
    /// The detection hotkey without Windows, so controller tests never take a
    /// system-wide hotkey (the user's F8, or a Ctrl+Alt+Shift combo the app
    /// sends to ReShade) and never fail because another program holds one.
    /// Same contract as <see cref="GlobalHotkeyService"/>: a hotkey that fails
    /// to parse keeps the current one; one that fails to register (held
    /// elsewhere) leaves none.
    /// </summary>
    internal sealed class FakeHotkeyService : IGlobalHotkeyService
    {
        private readonly object _lock = new();
        private readonly HashSet<(int Key, int Modifiers)> _heldElsewhere = new();
        private (int Key, int Modifiers)? _held;
        private DetectionHotkeyConfig? _registered;
        private string? _lastError;
        private bool _disposed;

        public bool IsRegistered
        {
            get
            {
                lock (_lock)
                {
                    return _registered is not null;
                }
            }
        }

        public DetectionHotkeyConfig? RegisteredHotkey
        {
            get
            {
                lock (_lock)
                {
                    return _registered;
                }
            }
        }

        public string? LastError
        {
            get
            {
                lock (_lock)
                {
                    return _lastError;
                }
            }
        }

        public event EventHandler? HotkeyPressed;

        /// <summary>Another program takes <paramref name="hotkey"/>: registering it fails from now on.</summary>
        public void HoldElsewhere(DetectionHotkeyConfig hotkey)
        {
            lock (_lock)
            {
                _heldElsewhere.Add(Parse(hotkey));
            }
        }

        /// <summary>Whether this service currently holds <paramref name="hotkey"/>.</summary>
        public bool Holds(DetectionHotkeyConfig hotkey)
        {
            lock (_lock)
            {
                return _held == Parse(hotkey);
            }
        }

        /// <summary>The user presses the registered hotkey.</summary>
        public void Press()
        {
            Assert.True(IsRegistered, "No hotkey is registered.");
            HotkeyPressed?.Invoke(this, EventArgs.Empty);
        }

        public string? Start(DetectionHotkeyConfig hotkey)
        {
            ArgumentNullException.ThrowIfNull(hotkey);
            lock (_lock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (!HotkeyParser.TryParse(hotkey, out int vk, out int mods, out string? parseError))
                {
                    return parseError ?? $"Unsupported hotkey '{hotkey}'.";
                }

                _held = null;
                _registered = null;
                if (_heldElsewhere.Contains((vk, mods)))
                {
                    _lastError = GlobalHotkeyService.AlreadyInUseError;
                    return _lastError;
                }

                _held = (vk, mods);
                _registered = new DetectionHotkeyConfig { Key = hotkey.Key, Ctrl = hotkey.Ctrl, Shift = hotkey.Shift, Alt = hotkey.Alt };
                _lastError = null;
                return null;
            }
        }

        public void Stop()
        {
            lock (_lock)
            {
                _held = null;
                _registered = null;
                _lastError = null;
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                _disposed = true;
                _held = null;
                _registered = null;
            }
        }

        private static (int Key, int Modifiers) Parse(DetectionHotkeyConfig hotkey)
        {
            Assert.True(HotkeyParser.TryParse(hotkey, out int vk, out int mods), $"Unsupported test hotkey '{hotkey}'.");
            return (vk, mods);
        }
    }

    /// <summary>
    /// Records every send and detects overlapping ones: a real send is a
    /// down/hold/up sequence, so two sends inside each other would interleave
    /// keystrokes in the game.
    /// </summary>
    internal sealed class FakeKeys : IKeySimulator
    {
        private int _callCount;
        private int _inFlight;
        private int _overlaps;
        private volatile bool _nextResult = true;

        public bool NextResult
        {
            get => _nextResult;
            set => _nextResult = value;
        }

        public int CallCount => Volatile.Read(ref _callCount);

        /// <summary>Sends that started while another send was still in progress.</summary>
        public int Overlaps => Volatile.Read(ref _overlaps);

        /// <summary>Spin iterations each send "holds" its keys (widens the overlap window in stress tests).</summary>
        public int HoldSpins { get; set; }

        /// <summary>Runs inside the send, while it is "holding" the keys (gates, probes).</summary>
        public Action<ReshadeShortcutConfig>? OnSend { get; set; }

        public ConcurrentQueue<ReshadeShortcutConfig> Sent { get; } = new();

        public bool SendShortcut(ReshadeShortcutConfig shortcut)
        {
            if (Interlocked.Increment(ref _inFlight) > 1)
            {
                Interlocked.Increment(ref _overlaps);
            }

            try
            {
                Interlocked.Increment(ref _callCount);
                Sent.Enqueue(shortcut);
                OnSend?.Invoke(shortcut);
                if (HoldSpins > 0)
                {
                    Thread.Yield();
                    Thread.SpinWait(HoldSpins);
                }

                return NextResult;
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }
    }

    internal sealed class FakeOcr : IOcrEngine
    {
        private volatile string _text = "FRACTURED COWSHED\nCOLDWIND FARM";
        private int _calls;

        public string Text
        {
            get => _text;
            set => _text = value;
        }

        /// <summary>Crops recognized so far.</summary>
        public int Calls => Volatile.Read(ref _calls);

        public string Name => "Fake";
        public bool IsAvailable => true;
        public string? UnavailableReason => null;

        public Task<IReadOnlyList<OcrTextLine>> RecognizeAsync(CapturedImage image, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult<IReadOnlyList<OcrTextLine>>(
                Text.Split('\n').Select(l => new OcrTextLine(l, null)).ToList());
        }
    }

    /// <summary>Full frames (lifecycle) are a flat grey <see cref="Level"/>; OCR regions are blank.</summary>
    internal sealed class FakeCapture : IWindowCaptureService
    {
        private volatile int _level = 10;
        private int _clientAreaCalls;
        private int _regionCalls;

        public int Level
        {
            get => _level;
            set => _level = value;
        }

        /// <summary>Flip between levels 10 and 200 on every full-frame capture (stress tests).</summary>
        public bool Alternate { get; set; }

        public volatile bool ThrowOnRegions;

        /// <summary>Runs inside every full-frame capture (lock-order probes).</summary>
        public Action? OnClientAreaCapture { get; set; }

        public int ClientAreaCalls => Volatile.Read(ref _clientAreaCalls);

        /// <summary>OCR regions captured so far (detection scans).</summary>
        public int RegionCalls => Volatile.Read(ref _regionCalls);

        public CapturedImage? CaptureClientArea(IntPtr hwnd)
        {
            int call = Interlocked.Increment(ref _clientAreaCalls);
            OnClientAreaCapture?.Invoke();
            int level = Alternate ? (call % 2 == 0 ? 200 : 10) : Level;
            const int size = 64;
            var px = new byte[size * size * 4];
            for (int i = 0; i < px.Length; i += 4)
            {
                px[i] = px[i + 1] = px[i + 2] = (byte)level;
                px[i + 3] = 255;
            }

            return new CapturedImage(size, size, px);
        }

        public CapturedImage? CaptureRegion(IntPtr hwnd, IntRegion region)
        {
            Interlocked.Increment(ref _regionCalls);
            if (ThrowOnRegions)
            {
                throw new InvalidOperationException("Simulated capture failure.");
            }

            int w = Math.Max(1, Math.Min(region.Width, 800));
            int h = Math.Max(1, Math.Min(region.Height, 200));
            return new CapturedImage(w, h, new byte[w * h * 4]);
        }
    }

    private long _nowMs = 1_000_000;
    private volatile bool _focused = true;
    private volatile bool _uncovered;
    private int _disposed;

    private ControllerHarness(string dir) => Dir = dir;

    public string Dir { get; }

    public AppController Controller { get; private set; } = null!;

    public RuntimeStateService States { get; private set; } = null!;

    public FakeDbdMonitor Dbd { get; } = new();

    public FakeKeys Keys { get; } = new();

    public FakeOcr Ocr { get; } = new();

    public FakeCapture Capture { get; } = new();

    public MatchLifecycleService Lifecycle { get; private set; } = null!;

    public DetectionOrchestrator Detector { get; private set; } = null!;

    public string ColdwindIni { get; private set; } = null!;

    public string GlobalIni { get; private set; } = null!;

    public string ReshadeIni { get; private set; } = null!;

    public string ConfigPath { get; private set; } = null!;

    /// <summary>The (fake) global hotkey service the controller registers with.</summary>
    public FakeHotkeyService Hotkey { get; } = new();

    /// <summary>Whether DBD's window counts as the foreground window.</summary>
    public bool Focused
    {
        get => _focused;
        set => _focused = value;
    }

    /// <summary>
    /// Whether no other window covers DBD's loading-screen text (e.g. DBD in plain
    /// view on another monitor). Default false: an unfocused DBD counts as covered.
    /// </summary>
    public bool Uncovered
    {
        get => _uncovered;
        set => _uncovered = value;
    }

    /// <summary>The windows the scan asked the occlusion check about.</summary>
    public ConcurrentQueue<IntPtr> UncoveredAsked { get; } = new();

    /// <summary>Optional replacement for the occlusion check (e.g. one that throws).</summary>
    public Func<IntPtr, bool>? UncoveredCheck { get; set; }

    public long NowMs => Interlocked.Read(ref _nowMs);

    public static ControllerHarness Create(string dir, ControllerHarnessOptions? options = null)
    {
        options ??= new ControllerHarnessOptions();
        var h = new ControllerHarness(dir);
        AppConfigService configSvc;
        if (options.RestartOf is { } previous)
        {
            previous.Dispose(); // the app exits; DBD keeps running
            h.ColdwindIni = previous.ColdwindIni;
            h.GlobalIni = previous.GlobalIni;
            h.ReshadeIni = previous.ReshadeIni;
            h.ConfigPath = previous.ConfigPath;
            configSvc = new AppConfigService(h.ConfigPath);
            configSvc.Load();
            h.Dbd.ProcessId = previous.Dbd.ProcessId;
            h.Dbd.MainWindowHandle = previous.Dbd.MainWindowHandle;
            h.Dbd.IsRunning = previous.Dbd.IsRunning;
            options.DbdMeanwhile?.Invoke(h.Dbd);
        }
        else
        {
            h.ColdwindIni = Path.Combine(dir, Guid.NewGuid() + "-Coldwind.ini");
            h.GlobalIni = Path.Combine(dir, Guid.NewGuid() + "-Global.ini");
            File.WriteAllText(h.ColdwindIni, "[General]\nTechniques=A\nTechniqueSorting=A\n");
            File.WriteAllText(h.GlobalIni, "[General]\nTechniques=B\nTechniqueSorting=B\n");
            h.ReshadeIni = Path.Combine(dir, Guid.NewGuid() + "-ReShade.ini");
            File.WriteAllText(h.ReshadeIni, "[GENERAL]\nPresetPath=" + h.GlobalIni + "\n");
            h.ConfigPath = Path.Combine(dir, Guid.NewGuid() + "-config.json");

            configSvc = new AppConfigService(h.ConfigPath);
            if (options.WithPresets)
            {
                configSvc.Config.GlobalPresetPath = h.GlobalIni;
                configSvc.Config.RealmPresets["Coldwind Farm"] = h.ColdwindIni;
            }

            configSvc.Config.ReshadeIniPath = h.ReshadeIni;
            options.Configure?.Invoke(configSvc.Config);
            if (options.ShortcutsLoadedByReshade)
            {
                SyncLikeAnEarlierRun(configSvc.Config);
            }

            h.Dbd.ProcessId = options.ProcessId;
            h.Dbd.IsRunning = options.DbdRunningAtStart && !options.DbdSeenByFirstPoll;
        }

        foreach (DetectionHotkeyConfig held in options.HotkeysHeldElsewhere)
        {
            h.Hotkey.HoldElsewhere(held);
        }

        var db = new RealmDatabaseService();
        db.LoadAsync(null).GetAwaiter().GetResult();

        if (options.DbdSeenByFirstPoll)
        {
            h.Dbd.OnStart = () =>
            {
                h.Dbd.IsRunning = true;
                h.Dbd.RaiseStatusChanged(running: true);
            };
        }
        h.States = new RuntimeStateService();
        h.Detector = new DetectionOrchestrator(
            h.Ocr, new MapMatchingService(), db, h.Capture,
            new DetectionPolicy(TimeoutSeconds: 5, SampleIntervalMs: 25),
            _ => new DbdClientBounds(0, 0, 1920, 1080, 96));
        h.Lifecycle = new MatchLifecycleService(h.Capture, options.Lifecycle, () => h.NowMs);
        var activator = new PresetActivationService(h.Keys, _ => h.Focused);
        h.Controller = new AppController(
            configSvc, db, h.States, h.Dbd, h.Hotkey, h.Detector, h.Lifecycle, activator,
            new PresetDecisionService(), new ReshadeConfigurationService(),
            activationRetryInterval: options.RetryInterval, maxActivationAttempts: options.MaxAttempts,
            presetVerifyDelayMs: options.VerifyDelayMs,
            monotonicClockMs: () => h.NowMs,
            delayAsync: options.Delay,
            discoverDbdExePath: options.DiscoverDbdExe,
            discoverReshadeIniPath: options.DiscoverReshadeIni,
            processStartTime: options.ProcessStartTime,
            isScanTextUncovered: hwnd =>
            {
                h.UncoveredAsked.Enqueue(hwnd);
                return h.UncoveredCheck?.Invoke(hwnd) ?? h.Uncovered;
            });
        h.Controller.Initialize();
        return h;
    }

    /// <summary>What an earlier run of the app left behind: the configured presets' shortcuts, in config and in ReShade.ini.</summary>
    private static void SyncLikeAnEarlierRun(AppConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.ReshadeIniPath) || !File.Exists(config.ReshadeIniPath))
        {
            return;
        }

        var presets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(config.GlobalPresetPath))
        {
            presets[AppConfigKeys.GlobalShortcutKey] = config.GlobalPresetPath;
        }

        foreach (var kv in config.RealmPresets)
        {
            presets[kv.Key] = kv.Value;
        }

        var allocated = ReshadeConfigurationService.AllocateShortcuts(presets.Keys);
        var assignments = allocated
            .Select(kv => new PresetShortcutAssignment(kv.Key, presets[kv.Key], kv.Value))
            .ToList();
        new ReshadeConfigurationService().SyncPresetShortcuts(config.ReshadeIniPath, assignments);
        config.ReshadeShortcuts = allocated;
        config.LastSyncedPresetPaths = assignments.Select(a => PresetValidationService.NormalizePresetPath(a.PresetPath)).ToList();
    }

    public void Advance(TimeSpan by) => Interlocked.Add(ref _nowMs, (long)by.TotalMilliseconds);

    /// <summary>
    /// The user leaves DBD right after the scan's confident read, before the
    /// switch is sent. (A scan only reads while DBD is in front, so while DBD counts
    /// as covered (<see cref="Uncovered"/> false) focus can't already be elsewhere
    /// when the hotkey is pressed if the read is to succeed.)
    /// </summary>
    public void UnfocusRightAfterTheRead() =>
        Detector.SampleEvaluated += (_, info) =>
        {
            if (info.Success)
            {
                Focused = false;
            }
        };

    /// <summary>Loading screen → gameplay as the lifecycle sees it (abrupt, sustained change).</summary>
    public void DriveToTrial(int gameplayLevel = 200)
    {
        Capture.Level = gameplayLevel;
        for (int i = 0; i < 3; i++)
        {
            Lifecycle.PollOnce();
        }

        Assert.Equal(LifecyclePhase.InTrial, Lifecycle.Phase);
    }

    /// <summary>Gameplay → menus (sustained change far from the gameplay reference).</summary>
    public void DriveToMenus(int menuLevel = 40)
    {
        Capture.Level = menuLevel;
        for (int i = 0; i < 4; i++)
        {
            Lifecycle.PollOnce();
        }
    }

    /// <summary>What ReShade does when it switches: save PresetPath to ReShade.ini.</summary>
    public void ReshadeSaves(string presetPath)
    {
        var doc = IniDocument.Parse(File.ReadAllText(ReshadeIni));
        doc.SetValue("GENERAL", "PresetPath", presetPath);
        File.WriteAllText(ReshadeIni, doc.ToString());
    }

    public string? ReshadeStartupPreset() => ReshadeConfigurationService.ReadCurrentPresetAbsolute(ReshadeIni);

    /// <summary>The configured name (realm or Global key) of every shortcut sent, in order.</summary>
    public IReadOnlyList<string> SentNames() => Keys.Sent
        .Select(s => Controller.Config.ReshadeShortcuts
            .FirstOrDefault(kv => ReshadeConfigurationService.SameCombo(kv.Value, s)).Key ?? "?")
        .ToList();

    public static async Task WaitUntilAsync(Func<bool> condition, string what, int timeoutMs = 10_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail($"Timed out waiting for: {what}");
            }

            await Task.Delay(10);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return; // already shut down (e.g. by a RestartOf harness)
        }

        try
        {
            Controller.Shutdown();
            Controller.Dispose();
            Lifecycle.Dispose();
            Detector.Dispose();
            Hotkey.Dispose();
        }
        catch
        {
            // Best effort.
        }
    }
}
