using DBDAutoReShade.Core.Services;

namespace DBDAutoReShade.Desktop.Services;

/// <summary>Best-effort external match phase (no OCR, no game introspection).</summary>
public enum LifecyclePhase
{
    Unknown,
    /// <summary>Armed: loading screen reference captured, watching for the trial transition.</summary>
    WatchingForTrial,
    /// <summary>Trial gameplay presumed active.</summary>
    InTrial,
    /// <summary>Returned to menus (sustained scene change after a minimum trial time).</summary>
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
/// Lightweight gameplay/menu inference: captures a 32×32 thumbnail every few
/// seconds (negligible CPU, no OCR) and watches for sustained scene changes.
/// Conservative by design — long debounce, minimum trial time — so mid-match
/// lighting changes never yank the preset back to Global. All thresholds are
/// best-effort external heuristics and documented as such; the user can
/// disable automatic menu restore in Settings.
/// </summary>
public sealed class MatchLifecycleService : IDisposable
{
    private readonly IWindowCaptureService _capture;
    private readonly LifecycleOptions _options;
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private bool _disposed;

    private LifecyclePhase _phase = LifecyclePhase.Unknown;
    private Func<IntPtr>? _windowProvider;
    private Func<bool>? _runningProvider;
    private byte[]? _reference;
    private byte[]? _previous;
    private int _streak;
    private DateTime _phaseSince = DateTime.UtcNow;

    public MatchLifecycleService(IWindowCaptureService capture, LifecycleOptions? options = null)
    {
        _capture = capture ?? throw new ArgumentNullException(nameof(capture));
        _options = options ?? new LifecycleOptions();
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

    public event EventHandler<LifecyclePhase>? PhaseChanged;

    public void Start(Func<IntPtr> windowProvider, Func<bool> runningProvider)
    {
        ArgumentNullException.ThrowIfNull(windowProvider);
        ArgumentNullException.ThrowIfNull(runningProvider);
        lock (_gate)
        {
            ThrowIfDisposed();
            StopLocked();
            _windowProvider = windowProvider;
            _runningProvider = runningProvider;
            _cts = new CancellationTokenSource();
            _loop = Task.Run(() => LoopAsync(_cts.Token));
        }
    }

    /// <summary>Arm trial detection with the loading screen as reference.</summary>
    public void ArmForTrial()
    {
        lock (_gate)
        {
            _reference = TakeThumbnailLocked();
            _previous = _reference;
            _streak = 0;
            SetPhaseLocked(LifecyclePhase.WatchingForTrial);
        }
    }

    /// <summary>Mark trial start externally (e.g. immediate activation path).</summary>
    public void NotifyTrialStarted()
    {
        lock (_gate)
        {
            _reference = TakeThumbnailLocked() ?? _reference;
            _previous = _reference;
            _streak = 0;
            SetPhaseLocked(LifecyclePhase.InTrial);
        }
    }

    public void Disarm()
    {
        lock (_gate)
        {
            _reference = null;
            _previous = null;
            _streak = 0;
            SetPhaseLocked(LifecyclePhase.Unknown);
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            StopLocked();
        }
    }

    private void StopLocked()
    {
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
        lock (_gate)
        {
            windowProvider = _windowProvider;
            runningProvider = _runningProvider;
            if (windowProvider is null || runningProvider is null)
            {
                return;
            }
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
            lock (_gate)
            {
                SetPhaseLocked(LifecyclePhase.Unknown);
            }

            return;
        }

        byte[]? thumb;
        try
        {
            IntPtr hwnd = windowProvider();
            using CapturedImage? full = _capture.CaptureClientArea(hwnd);
            thumb = full is null ? null : ImagePreprocessingService.Thumbnail32(full);
        }
        catch
        {
            return;
        }

        if (thumb is null)
        {
            return;
        }

        lock (_gate)
        {
            if (_phase != LifecyclePhase.WatchingForTrial && _phase != LifecyclePhase.InTrial)
            {
                _previous = thumb;
                return;
            }

            double fromPrev = _previous is null
                ? 0
                : ImagePreprocessingService.ThumbnailDistance(_previous, thumb);
            _previous = thumb;

            if (_phase == LifecyclePhase.WatchingForTrial)
            {
                double fromRef = _reference is null
                    ? fromPrev
                    : ImagePreprocessingService.ThumbnailDistance(_reference, thumb);
                // Loading screens pan slowly; the trial transition is abrupt.
                bool far = fromPrev >= _options.TrialTransitionDistance || fromRef >= _options.TrialTransitionDistance * 1.5;
                _streak = far ? _streak + 1 : 0;
                if (_streak >= _options.TrialConfirmPolls)
                {
                    _reference = thumb;
                    _streak = 0;
                    SetPhaseLocked(LifecyclePhase.InTrial);
                }
            }
            else // InTrial → watch for a sustained return to menus.
            {
                double elapsed = (DateTime.UtcNow - _phaseSince).TotalSeconds;
                double fromRef = _reference is null
                    ? fromPrev
                    : ImagePreprocessingService.ThumbnailDistance(_reference, thumb);
                bool far = fromPrev >= _options.MenuTransitionDistance || fromRef >= _options.MenuTransitionDistance * 1.6;
                if (far)
                {
                    _streak++;
                }
                else
                {
                    _streak = 0;
                    // Track slow drift (weather/time changes) so it never accumulates into a false menu signal.
                    _reference = thumb;
                }

                if (elapsed >= _options.MinimumTrialSeconds && _streak >= _options.MenuConfirmPolls)
                {
                    _reference = thumb;
                    _streak = 0;
                    SetPhaseLocked(LifecyclePhase.BackToMenus);
                }
            }
        }
    }

    private byte[]? TakeThumbnailLocked()
    {
        try
        {
            if (_windowProvider is null)
            {
                return null;
            }

            using CapturedImage? full = _capture.CaptureClientArea(_windowProvider());
            return full is null ? null : ImagePreprocessingService.Thumbnail32(full);
        }
        catch
        {
            return null;
        }
    }

    private void SetPhaseLocked(LifecyclePhase phase)
    {
        if (_phase != phase)
        {
            _phase = phase;
            _phaseSince = DateTime.UtcNow;
            try
            {
                PhaseChanged?.Invoke(this, phase);
            }
            catch
            {
                // Subscribers must not break the monitor.
            }
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(MatchLifecycleService));
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (!_disposed)
            {
                _disposed = true;
                StopLocked();
            }
        }
    }
}
