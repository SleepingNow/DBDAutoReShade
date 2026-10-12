using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;

namespace DBDAutoReShade.App.Media;

/// <summary>
/// Decides how much the Campfire UI may move. Every Campfire control asks its policy before it animates:
/// <list type="bullet">
/// <item><see cref="ShouldAnimate"/> false: one-shot moments become instant (the end state is applied at once). That is
/// the case whenever nobody can watch the window: animations off, the window minimized or hidden, or the window not the
/// active one (in a match the game is in front: a map found, the click into the game and the match end change the
/// screen at once, and no clock runs behind the game).</item>
/// <item><see cref="ShouldRunAmbient"/> false: ambient loops (fog drift, flicker, embers, sway, heartbeat, the
/// skill-check spin, the Test countdown's needle) stop and hold in place; they resume where they were when it turns
/// true again.</item>
/// <item><see cref="ReducedMotion"/> true (Windows "Show animations in Windows" off): no movement, short fades only.</item>
/// </list>
/// One process-wide instance (<see cref="Current"/>) follows the user's "Play animations" setting
/// (<see cref="AnimationsEnabled"/>), the Windows animation setting and the main window
/// (<see cref="AttachWindow"/>). A subtree can use its own instance through the inheritable attached
/// property <see cref="PolicyProperty"/> (tests and the snapshot harness do this so they never share state).
/// </summary>
public sealed class MotionPolicy : INotifyPropertyChanged
{
    /// <summary>
    /// Inheritable attached policy; when unset, controls use <see cref="Current"/>. Every element whose policy changes
    /// (set, inherited, or re-parented) tells its <see cref="MotionPolicyWatch"/> through this property's change
    /// callback, so following a policy swap needs no subscription that could keep the element alive.
    /// </summary>
    public static readonly DependencyProperty PolicyProperty = DependencyProperty.RegisterAttached(
        "Policy",
        typeof(MotionPolicy),
        typeof(MotionPolicy),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.Inherits, (d, _) => MotionPolicyWatch.NotifyPolicyChanged(d)));

    // (PublicationOnly: the factory reads SystemParameters, which must not run under the Lazy's lock; a racing second
    // instance is simply dropped, and every caller gets the published one)
    private static readonly Lazy<MotionPolicy> CurrentPolicy = new(() => FollowingSystemSetting(), LazyThreadSafetyMode.PublicationOnly);

    // The policies that follow the Windows setting, held weakly: one static WM_SETTINGCHANGE subscription serves them all.
    private static readonly List<WeakReference<MotionPolicy>> SystemFollowers = new();
    private static bool SystemHooked;

    private readonly object _gate = new();
    private readonly Func<bool>? _readSystemReducedMotion;
    private bool _animationsEnabled = true;
    private bool _windowActive = true;
    private bool _windowVisible = true;
    private bool _systemReducedMotion;
    private bool? _reducedMotionOverride;

    /// <summary>A policy that ignores the Windows animation setting (tests, previews). Everything is allowed.</summary>
    public MotionPolicy()
    {
    }

    private MotionPolicy(Func<bool> readSystemReducedMotion)
    {
        _readSystemReducedMotion = readSystemReducedMotion;
        _systemReducedMotion = SafeRead(readSystemReducedMotion);
        bool hook;
        lock (SystemFollowers)
        {
            SystemFollowers.Add(new WeakReference<MotionPolicy>(this));
            hook = !SystemHooked;
            SystemHooked = true;
        }

        // (outside the lock: no call into WPF ever runs under a lock of ours)
        if (hook)
        {
            SystemParameters.StaticPropertyChanged += OnSystemParameterChanged;
        }
    }

    /// <summary>Raised after any input changes (on the thread that changed it).</summary>
    public event EventHandler? Changed;

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The process-wide policy (the app's main window and settings drive it).</summary>
    public static MotionPolicy Current => CurrentPolicy.Value;

    /// <summary>The user's "Play animations" setting (AppConfig <c>animationsEnabled</c>). Default true.</summary>
    public bool AnimationsEnabled
    {
        get => _animationsEnabled;
        set => Set(ref _animationsEnabled, value, nameof(AnimationsEnabled));
    }

    /// <summary>The watched window is the foreground window. Ambient loops pause while it is not
    /// (a window behind a fullscreen game still renders).</summary>
    public bool WindowActive
    {
        get => _windowActive;
        set => Set(ref _windowActive, value, nameof(WindowActive));
    }

    /// <summary>The watched window is shown and not minimized.</summary>
    public bool WindowVisible
    {
        get => _windowVisible;
        set => Set(ref _windowVisible, value, nameof(WindowVisible));
    }

    /// <summary>
    /// The followed reduced-motion setting is on: for <see cref="Current"/> (and any policy made by
    /// <see cref="FollowingSystemSetting"/> with the default reader), Windows' own animation setting is off
    /// (<c>SystemParameters.ClientAreaAnimation == false</c>). Always false for instances created with the public
    /// constructor.
    /// </summary>
    public bool SystemReducedMotion => _systemReducedMotion;

    /// <summary>Forces reduced motion on (true) or off (false) regardless of Windows; null follows Windows.</summary>
    public bool? ReducedMotionOverride
    {
        get => _reducedMotionOverride;
        set
        {
            bool changed;
            lock (_gate)
            {
                changed = _reducedMotionOverride != value;
                _reducedMotionOverride = value;
            }

            if (changed)
            {
                Raise(nameof(ReducedMotionOverride));
            }
        }
    }

    /// <summary>Reduced motion: no movement (transform, particles, sweeps); short opacity / colour fades only.</summary>
    public bool ReducedMotion => _reducedMotionOverride ?? _systemReducedMotion;

    /// <summary>
    /// One-shot moments may play: animations on and the window shown AND active (a window behind the game is not
    /// watched, and it still renders). False: apply end states instantly; a moment still playing stops at once.
    /// </summary>
    public bool ShouldAnimate => _animationsEnabled && _windowVisible && _windowActive;

    /// <summary>Ambient loops may run: animations on, window shown AND active, and no reduced motion.</summary>
    public bool ShouldRunAmbient => ShouldAnimate && !ReducedMotion;

    /// <summary>The skill check may land visibly (the window is shown, active, and animations are on: the same as
    /// <see cref="ShouldAnimate"/>). When false the Great sound plays immediately on map found instead of at the hit.</summary>
    public bool CanPresentLanding => ShouldAnimate;

    /// <summary>
    /// A policy that follows a reduced-motion setting: <paramref name="readReducedMotion"/> (default: Windows'
    /// "Show animations in Windows" is off, <c>!SystemParameters.ClientAreaAnimation</c>), read now and again whenever
    /// Windows reports a settings change (WM_SETTINGCHANGE, raised as <c>SystemParameters.StaticPropertyChanged</c>)
    /// and on <see cref="RefreshSystemSetting"/>. <see cref="Current"/> is made this way. The policy is tracked weakly,
    /// so dropping it lets it be collected.
    /// </summary>
    public static MotionPolicy FollowingSystemSetting(Func<bool>? readReducedMotion = null) =>
        new(readReducedMotion ?? ReadWindowsReducedMotion);

    /// <summary>The policy that applies to <paramref name="element"/> (its inherited one, else <see cref="Current"/>).</summary>
    public static MotionPolicy For(DependencyObject? element) =>
        element?.GetValue(PolicyProperty) as MotionPolicy ?? Current;

    /// <summary>Sets the inheritable policy on a subtree root.</summary>
    public static void SetPolicy(DependencyObject element, MotionPolicy? value) => element.SetValue(PolicyProperty, value);

    /// <summary>Gets the policy set on (or inherited by) <paramref name="element"/>; null when none is set.</summary>
    public static MotionPolicy? GetPolicy(DependencyObject element) => (MotionPolicy?)element.GetValue(PolicyProperty);

    /// <summary>
    /// Follows <paramref name="window"/>: <see cref="WindowActive"/> tracks Activated / Deactivated and
    /// <see cref="WindowVisible"/> tracks IsVisible and the minimized state. Returns a token that detaches.
    /// </summary>
    public IDisposable AttachWindow(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        void Sync()
        {
            WindowVisible = window.IsVisible && window.WindowState != WindowState.Minimized;
            WindowActive = window.IsActive;
        }

        void OnActivated(object? s, EventArgs e) => Sync();
        void OnStateChanged(object? s, EventArgs e) => Sync();
        void OnVisibleChanged(object s, DependencyPropertyChangedEventArgs e) => Sync();

        window.Activated += OnActivated;
        window.Deactivated += OnActivated;
        window.StateChanged += OnStateChanged;
        window.IsVisibleChanged += OnVisibleChanged;
        Sync();
        return new Detach(() =>
        {
            window.Activated -= OnActivated;
            window.Deactivated -= OnActivated;
            window.StateChanged -= OnStateChanged;
            window.IsVisibleChanged -= OnVisibleChanged;
        });
    }

    /// <summary>Re-reads the followed reduced-motion setting (also done automatically on WM_SETTINGCHANGE). No effect
    /// on a policy created with the public constructor.</summary>
    public void RefreshSystemSetting()
    {
        if (_readSystemReducedMotion is null)
        {
            return;
        }

        bool value = SafeRead(_readSystemReducedMotion);
        bool changed;
        lock (_gate)
        {
            changed = _systemReducedMotion != value;
            _systemReducedMotion = value;
        }

        if (changed)
        {
            Raise(nameof(SystemReducedMotion));
        }
    }

    private static bool ReadWindowsReducedMotion() => !SystemParameters.ClientAreaAnimation;

    private static bool SafeRead(Func<bool> read)
    {
        try
        {
            return read();
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void OnSystemParameterChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!string.IsNullOrEmpty(e.PropertyName) && e.PropertyName != nameof(SystemParameters.ClientAreaAnimation))
        {
            return;
        }

        var live = new List<MotionPolicy>();
        lock (SystemFollowers)
        {
            for (int i = SystemFollowers.Count - 1; i >= 0; i--)
            {
                if (SystemFollowers[i].TryGetTarget(out MotionPolicy? policy))
                {
                    live.Add(policy);
                }
                else
                {
                    SystemFollowers.RemoveAt(i);
                }
            }
        }

        foreach (MotionPolicy policy in live)
        {
            policy.RefreshSystemSetting();
        }
    }

    private void Set(ref bool field, bool value, string name)
    {
        bool changed;
        lock (_gate)
        {
            changed = field != value;
            field = value;
        }

        if (changed)
        {
            Raise(name);
        }
    }

    private void Raise(string name)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ReducedMotion)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShouldAnimate)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShouldRunAmbient)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanPresentLanding)));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private sealed class Detach(Action action) : IDisposable
    {
        private Action? _action = action;

        public void Dispose() => Interlocked.Exchange(ref _action, null)?.Invoke();
    }
}

/// <summary>
/// Subscribes a control to its <see cref="MotionPolicy"/> while it is loaded, marshals change notifications to the
/// control's dispatcher, and follows policy swaps through the inherited <see cref="MotionPolicy.PolicyProperty"/>.
/// Nothing outside the control refers to it unless it is loaded: the policy's <see cref="MotionPolicy.Changed"/>
/// subscription is made on Loaded and removed on Unloaded, and a policy swap is told by the property's change callback
/// (not by a property-descriptor subscription, which would hold the control forever). A control that leaves the tree,
/// or is never shown, can be collected.
/// </summary>
public sealed class MotionPolicyWatch
{
    // The watches of an element (a chain through _next), so the policy property's change callback can find them.
    private static readonly DependencyProperty WatchProperty = DependencyProperty.RegisterAttached(
        "Watch", typeof(MotionPolicyWatch), typeof(MotionPolicyWatch), new PropertyMetadata(null));

    private readonly FrameworkElement _owner;
    private readonly Action _onChanged;
    private readonly MotionPolicyWatch? _next;
    private MotionPolicy? _subscribed;
    private bool _pending;

    /// <summary>Starts watching <paramref name="owner"/>'s policy; <paramref name="onChanged"/> runs on its dispatcher
    /// when the policy or its inputs change, when another policy takes over, and when the owner is loaded or unloaded.</summary>
    public MotionPolicyWatch(FrameworkElement owner, Action onChanged)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(onChanged);
        _owner = owner;
        _onChanged = onChanged;
        _next = owner.GetValue(WatchProperty) as MotionPolicyWatch;
        owner.SetValue(WatchProperty, this);
        owner.Loaded += (_, _) => OnLoaded();
        owner.Unloaded += (_, _) => OnUnloaded();
    }

    /// <summary>The policy that applies to the owner now.</summary>
    public MotionPolicy Policy => MotionPolicy.For(_owner);

    /// <summary>The watch is subscribed to the policy's changes (the owner is loaded).</summary>
    public bool IsSubscribed => _subscribed is not null;

    /// <summary>Called by <see cref="MotionPolicy.PolicyProperty"/>'s change callback for each element whose policy changed.</summary>
    internal static void NotifyPolicyChanged(DependencyObject element)
    {
        for (var watch = element.GetValue(WatchProperty) as MotionPolicyWatch; watch is not null; watch = watch._next)
        {
            watch.OnPolicySwapped();
        }
    }

    private void OnLoaded()
    {
        Attach(Policy);
        _onChanged();
    }

    private void OnUnloaded()
    {
        Detach();
        _onChanged();
    }

    private void OnPolicySwapped()
    {
        if (_subscribed is not null)
        {
            Attach(Policy);
        }

        _onChanged();
    }

    private void Attach(MotionPolicy policy)
    {
        if (ReferenceEquals(_subscribed, policy))
        {
            return;
        }

        Detach();
        _subscribed = policy;
        policy.Changed += OnPolicyChanged;
    }

    private void Detach()
    {
        if (_subscribed is not null)
        {
            _subscribed.Changed -= OnPolicyChanged;
            _subscribed = null;
        }
    }

    private void OnPolicyChanged(object? sender, EventArgs e)
    {
        Dispatcher dispatcher = _owner.Dispatcher;
        if (dispatcher.CheckAccess())
        {
            _onChanged();
            return;
        }

        if (_pending)
        {
            return;
        }

        _pending = true;
        dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
        {
            _pending = false;
            _onChanged();
        }));
    }
}
