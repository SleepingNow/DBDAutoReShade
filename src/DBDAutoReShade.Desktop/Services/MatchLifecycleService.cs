using DBDAutoReShade.Core.Services;

namespace DBDAutoReShade.Desktop.Services;

/// <summary>Best-effort external match phase (no OCR, no game introspection).</summary>
public enum LifecyclePhase
{
    /// <summary>Not tracking a match (disarmed, or DBD not running). Polls do no work.</summary>
    Unknown,
    /// <summary>
    /// Armed: watching for the trial transition. The reference is the loading
    /// screen captured when arming, or (armed while DBD was hidden) the first
    /// frame of DBD a poll sees.
    /// </summary>
    WatchingForTrial,
    /// <summary>Trial gameplay presumed active (entered only when the transition is observed).</summary>
    InTrial,
    /// <summary>Returned to menus (sustained scene change after a minimum trial time). Polls do no work until re-armed.</summary>
    BackToMenus
}

/// <summary>Tuning for the lightweight scene-change monitor.</summary>
public sealed record LifecycleOptions(
    int PollIntervalMs = 2500,
    double TrialTransitionDistance = 30.0,
    int TrialConfirmPolls = 3,
    double MenuTransitionDistance = 35.0,
    int MenuConfirmPolls = 4,
    int MinimumTrialSeconds = 45);

/// <summary>
/// Lightweight gameplay/menu inference: while a match is being tracked it
/// captures a 32×32 thumbnail every few seconds (negligible CPU, no OCR) and
/// watches for sustained scene changes; while idle (<see cref="LifecyclePhase.Unknown"/>
/// or <see cref="LifecyclePhase.BackToMenus"/>) a poll captures nothing.
/// Conservative by design — long debounce, minimum trial time, drift tracking —
/// so mid-match camera motion or lighting changes never yank the preset back
/// to Global. All thresholds are best-effort external heuristics and documented
/// as such; the user can disable automatic menu restore in Settings.
/// <para>
/// Only frames of DBD itself count. The capture copies whatever is on screen
/// at DBD's position, so while DBD is not the foreground window (alt-tabbed
/// in borderless windowed mode) a poll captures nothing, leaves the reference
/// alone and restarts the debounce streak (see <see cref="Start(Func{IntPtr}, Func{bool}, Func{IntPtr, bool}?)"/>).
/// </para>
/// <para>
/// Threading: <c>_gate</c> is a leaf lock. It is never held while capturing
/// the screen or while invoking <see cref="PhaseChanged"/>, and nothing is
/// called out of it, so callers may invoke any member from any thread (also
/// from a <see cref="PhaseChanged"/> handler) without lock-order concerns.
/// Only transitions the monitor OBSERVES (polls) raise <see cref="PhaseChanged"/>;
/// commands (<see cref="ArmForTrial"/>, <see cref="EnsureArmed"/>, <see cref="Disarm"/>)
/// never do, because their caller already knows the outcome. A poll raises
/// only if no command ran since it started, and never after <see cref="Stop"/>
/// or <see cref="Dispose"/>.
/// </para>
/// </summary>
public sealed class MatchLifecycleService : IDisposable
{
    /// <summary>How long <see cref="Stop"/> waits for a running <see cref="PhaseChanged"/> handler to return.</summary>
    internal const int StopWaitMs = 2000;

    // Set while this thread is inside a PhaseChanged handler, so Stop/Dispose
    // called from a handler never waits for itself.
    [ThreadStatic]
    private static int _threadRaiseDepth;

    private readonly IWindowCaptureService _capture;
    private readonly LifecycleOptions _options;
    private readonly Func<long> _clockMs;
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private bool _disposed;
    private bool _stopped = true;

    // Bumped by every command (arm/disarm/start/stop): a poll that started
    // before a command must neither apply its frame nor raise its transition.
    private long _epoch;
    private int _raising;

    private LifecyclePhase _phase = LifecyclePhase.Unknown;
    private Func<IntPtr>? _windowProvider;
    private Func<bool>? _runningProvider;
    private Func<IntPtr, bool>? _isForeground;
    private byte[]? _reference;
    private byte[]? _previous;
    private int _streak;
    private long _trialSinceMs;

    public MatchLifecycleService(IWindowCaptureService capture, LifecycleOptions? options = null)
        : this(capture, options, static () => Environment.TickCount64)
    {
    }

    /// <summary>Test seam: <paramref name="clockMs"/> is a monotonic millisecond clock.</summary>
    internal MatchLifecycleService(IWindowCaptureService capture, LifecycleOptions? options, Func<long> clockMs)
    {
        _capture = capture ?? throw new ArgumentNullException(nameof(capture));
        _options = options ?? new LifecycleOptions();
        _clockMs = clockMs ?? throw new ArgumentNullException(nameof(clockMs));
    }

    public LifecyclePhase Phase
    {
        get
        {
            lock (_gate)
            {
                return _phase;
            }
        }
    }

    /// <summary>
    /// Raised (outside any lock, on the polling thread) when a poll observes a
    /// phase transition. Not raised for commands.
    /// </summary>
    public event EventHandler<LifecyclePhase>? PhaseChanged;

    /// <summary>Starts polling; every captured frame is evaluated (no foreground check).</summary>
    public void Start(Func<IntPtr> windowProvider, Func<bool> runningProvider) =>
        Start(windowProvider, runningProvider, null);

    /// <summary>
    /// Starts polling. <paramref name="isForeground"/> tells whether the given
    /// DBD window is the foreground window. The capture copies whatever is on
    /// screen at DBD's position, so while it returns false nothing is captured
    /// or evaluated: the reference stays, and the debounce streak restarts
    /// (a streak counts consecutive frames of DBD itself). Null evaluates
    /// every frame.
    /// </summary>
    public void Start(Func<IntPtr> windowProvider, Func<bool> runningProvider, Func<IntPtr, bool>? isForeground)
    {
        ArgumentNullException.ThrowIfNull(windowProvider);
        ArgumentNullException.ThrowIfNull(runningProvider);
        lock (_gate)
        {
            ThrowIfDisposed();
            StopLocked();
            _windowProvider = windowProvider;
            _runningProvider = runningProvider;
            _isForeground = isForeground;
            _stopped = false;
            _cts = new CancellationTokenSource();
            CancellationToken token = _cts.Token;
            _ = Task.Run(() => LoopAsync(token));
        }
    }

    /// <summary>
    /// Arm trial detection with the current frame (the loading screen) as
    /// reference. Captures outside the lock; does not raise <see cref="PhaseChanged"/>.
    /// When no frame of DBD can be taken (another window is in front, or the
    /// capture failed) it arms without a reference, and the first frame a
    /// poll sees becomes the reference.
    /// </summary>
    public void ArmForTrial()
    {
        (Func<IntPtr>? window, Func<IntPtr, bool>? foreground) = Providers();
        byte[]? thumb = CaptureThumbnail(window, foreground, out _);
        lock (_gate)
        {
            ArmLocked(thumb);
        }
    }

    /// <summary>
    /// Arms (like <see cref="ArmForTrial"/>) only when nothing is being tracked
    /// (<see cref="LifecyclePhase.Unknown"/>); otherwise leaves the phase and its
    /// reference alone. A successful preset switch calls this: it must never
    /// force <see cref="LifecyclePhase.InTrial"/>, which is entered only when a
    /// poll observes the transition.
    /// </summary>
    public void EnsureArmed()
    {
        lock (_gate)
        {
            if (_phase != LifecyclePhase.Unknown)
            {
                return;
            }
        }

        (Func<IntPtr>? window, Func<IntPtr, bool>? foreground) = Providers();
        byte[]? thumb = CaptureThumbnail(window, foreground, out _);
        lock (_gate)
        {
            if (_phase == LifecyclePhase.Unknown)
            {
                ArmLocked(thumb);
            }
        }
    }

    /// <summary>Stop tracking (phase <see cref="LifecyclePhase.Unknown"/>). Does not raise <see cref="PhaseChanged"/>.</summary>
    public void Disarm()
    {
        lock (_gate)
        {
            _epoch++;
            ClearTrackingLocked();
            _phase = LifecyclePhase.Unknown;
        }
    }

    /// <summary>
    /// Stops polling. After it returns no <see cref="PhaseChanged"/> is raised
    /// (an in-flight poll drops its transition), and a handler that was already
    /// running has returned (waits up to <see cref="StopWaitMs"/>; never waits
    /// when called from a handler). Do not call it while holding a lock that a
    /// <see cref="PhaseChanged"/> handler takes.
    /// </summary>
    public void Stop()
    {
        lock (_gate)
        {
            StopLocked();
            WaitForHandlersLocked();
        }
    }

    private void StopLocked()
    {
        _stopped = true;
        _epoch++;
        try
        {
            _cts?.Cancel();
        }
        catch
        {
            // Best effort.
        }

        _cts = null;
    }

    private void WaitForHandlersLocked()
    {
        if (_threadRaiseDepth > 0)
        {
            return; // called from a handler: waiting would wait for ourselves
        }

        long deadline = Environment.TickCount64 + StopWaitMs;
        while (_raising > 0)
        {
            long remaining = deadline - Environment.TickCount64;
            if (remaining <= 0)
            {
                break; // a handler is stuck; bounded so shutdown never hangs
            }

            Monitor.Wait(_gate, (int)remaining);
        }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_options.PollIntervalMs, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                PollOnce();
            }
            catch
            {
                // The monitor must never crash the app.
            }
        }
    }

    internal void PollOnce()
    {
        Func<IntPtr>? windowProvider;
        Func<bool>? runningProvider;
        Func<IntPtr, bool>? isForeground;
        LifecyclePhase phase;
        long epoch;
        lock (_gate)
        {
            if (_stopped || _disposed || _windowProvider is null || _runningProvider is null ||
                _phase == LifecyclePhase.Unknown)
            {
                return; // idle: no capture, no work
            }

            windowProvider = _windowProvider;
            runningProvider = _runningProvider;
            isForeground = _isForeground;
            phase = _phase;
            epoch = _epoch;
        }

        bool running;
        try
        {
            running = runningProvider();
        }
        catch
        {
            return;
        }

        if (!running)
        {
            bool changed;
            lock (_gate)
            {
                if (epoch != _epoch)
                {
                    return;
                }

                ClearTrackingLocked();
                changed = SetPhaseLocked(LifecyclePhase.Unknown);
            }

            if (changed)
            {
                RaiseIfCurrent(LifecyclePhase.Unknown, epoch);
            }

            return;
        }

        if (phase == LifecyclePhase.BackToMenus)
        {
            return; // idle until re-armed: no capture
        }

        byte[]? thumb = CaptureThumbnail(windowProvider, isForeground, out bool hidden);
        if (hidden)
        {
            // Another window is in front of DBD: a screen copy would show it, not
            // the game. Nothing is evaluated, and a streak must consist of
            // consecutive frames of DBD itself, so it starts over.
            lock (_gate)
            {
                if (epoch == _epoch)
                {
                    _streak = 0;
                }
            }

            return;
        }

        if (thumb is null)
        {
            return;
        }

        LifecyclePhase? transition;
        lock (_gate)
        {
            if (epoch != _epoch)
            {
                return; // a command re-armed/disarmed while we captured: our frame is stale
            }

            transition = EvaluateLocked(thumb);
        }

        if (transition is LifecyclePhase next)
        {
            RaiseIfCurrent(next, epoch);
        }
    }

    /// <summary>Apply one frame to the tracking state; returns the phase entered, if any.</summary>
    private LifecyclePhase? EvaluateLocked(byte[] thumb)
    {
        if (_phase == LifecyclePhase.WatchingForTrial)
        {
            if (_reference is null)
            {
                // Armed without a frame of DBD (it was hidden, or the capture
                // failed): the first frame of DBD becomes the reference.
                _reference = thumb;
                _previous = thumb;
                _streak = 0;
                return null;
            }

            double fromPrev = ImagePreprocessingService.ThumbnailDistance(_previous ?? _reference, thumb);
            double fromRef = ImagePreprocessingService.ThumbnailDistance(_reference, thumb);
            _previous = thumb;

            // Loading screens pan slowly; the trial transition is abrupt.
            bool far = fromPrev >= _options.TrialTransitionDistance || fromRef >= _options.TrialTransitionDistance * 1.5;
            _streak = far ? _streak + 1 : 0;
            if (_streak < _options.TrialConfirmPolls)
            {
                return null;
            }

            // Gameplay observed: the gameplay frame becomes the reference and
            // the minimum-trial clock starts now (not at the loading screen).
            _reference = thumb;
            _previous = thumb;
            _streak = 0;
            _trialSinceMs = _clockMs();
            return SetPhaseLocked(LifecyclePhase.InTrial) ? LifecyclePhase.InTrial : null;
        }

        if (_phase != LifecyclePhase.InTrial)
        {
            return null;
        }

        // InTrial → watch for a sustained return to menus. Only distance from
        // the reference counts: frame-to-frame differences (camera motion) never
        // do. Near frames re-base the reference so slow drift (weather, lighting,
        // walking around) never accumulates into a false menu signal.
        _previous = thumb;
        if (_reference is null)
        {
            _reference = thumb;
            _streak = 0;
            return null;
        }

        double distance = ImagePreprocessingService.ThumbnailDistance(_reference, thumb);
        if (distance >= _options.MenuTransitionDistance * 1.6)
        {
            _streak++;
        }
        else
        {
            _streak = 0;
            _reference = thumb;
        }

        double elapsedSeconds = (_clockMs() - _trialSinceMs) / 1000.0;
        if (_streak < _options.MenuConfirmPolls || elapsedSeconds < _options.MinimumTrialSeconds)
        {
            return null;
        }

        ClearTrackingLocked();
        return SetPhaseLocked(LifecyclePhase.BackToMenus) ? LifecyclePhase.BackToMenus : null;
    }

    private void ArmLocked(byte[]? reference)
    {
        _epoch++;
        _reference = reference;
        _previous = reference;
        _streak = 0;
        _phase = LifecyclePhase.WatchingForTrial;
    }

    private void ClearTrackingLocked()
    {
        _reference = null;
        _previous = null;
        _streak = 0;
    }

    private bool SetPhaseLocked(LifecyclePhase phase)
    {
        if (_phase == phase)
        {
            return false;
        }

        _phase = phase;
        return true;
    }

    /// <summary>Raise <see cref="PhaseChanged"/> unless a command or Stop ran since the poll began. Call WITHOUT the lock.</summary>
    private void RaiseIfCurrent(LifecyclePhase phase, long epoch)
    {
        lock (_gate)
        {
            if (_stopped || _disposed || epoch != _epoch)
            {
                return;
            }

            _raising++;
        }

        _threadRaiseDepth++;
        try
        {
            PhaseChanged?.Invoke(this, phase);
        }
        catch
        {
            // Subscribers must not break the monitor.
        }
        finally
        {
            _threadRaiseDepth--;
            lock (_gate)
            {
                _raising--;
                if (_raising == 0)
                {
                    Monitor.PulseAll(_gate);
                }
            }
        }
    }

    private (Func<IntPtr>? Window, Func<IntPtr, bool>? IsForeground) Providers()
    {
        lock (_gate)
        {
            return (_windowProvider, _isForeground);
        }
    }

    /// <summary>
    /// Capture a 32×32 thumbnail of DBD's window. Returns null with
    /// <paramref name="hidden"/> set when DBD is not the foreground window
    /// (the screen copy would show whatever covers it), and null without it
    /// when the capture failed. Never call with <c>_gate</c> held.
    /// </summary>
    private byte[]? CaptureThumbnail(Func<IntPtr>? windowProvider, Func<IntPtr, bool>? isForeground, out bool hidden)
    {
        hidden = false;
        if (windowProvider is null)
        {
            return null;
        }

        try
        {
            IntPtr window = windowProvider();
            if (isForeground is not null && !isForeground(window))
            {
                hidden = true;
                return null;
            }

            using CapturedImage? full = _capture.CaptureClientArea(window);
            return full is null ? null : ImagePreprocessingService.Thumbnail32(full);
        }
        catch
        {
            return null;
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(MatchLifecycleService));
        }
    }

    /// <summary>Stops polling (see <see cref="Stop"/> for the guarantees) and releases the monitor.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            StopLocked();
            WaitForHandlersLocked();
        }
    }
}
