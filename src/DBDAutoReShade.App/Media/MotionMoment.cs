using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media.Animation;

namespace DBDAutoReShade.App.Media;

/// <summary>
/// The clocks of one group of animations, created and applied directly (the way <c>BeginAnimation</c> does it), so
/// every target works: elements, transforms, brushes, effects, geometries. One root clock controls them all
/// (pause, seek, remove), which makes the group behave like one WPF Storyboard.
/// </summary>
internal sealed class ClockSet
{
    // Which clock set animates a property now (a newer set replaces an older one on the same property), so removing a
    // set detaches exactly its own clocks, at once, and never a newer set's.
    private static readonly ConditionalWeakTable<DependencyObject, Dictionary<DependencyProperty, ClockSet>> Owners = new();

    private readonly List<(DependencyObject Target, DependencyProperty Property)> _targets = new();
    private readonly ParallelTimeline _root;
    private ClockGroup? _clock;

    public ClockSet(ParallelTimeline root) => _root = root;

    public ClockGroup? Clock => _clock;

    public bool IsAttached => _clock is not null;

    public void Add(DependencyObject target, DependencyProperty property, AnimationTimeline timeline)
    {
        _targets.Add((target, property));
        _root.Children.Add(timeline);
    }

    public int Count => _targets.Count;

    public ClockGroup Start(EventHandler? completed)
    {
        var clock = (ClockGroup)_root.CreateClock(hasControllableRoot: true);
        if (completed is not null)
        {
            clock.Completed += completed;
        }

        for (int i = 0; i < _targets.Count; i++)
        {
            (DependencyObject target, DependencyProperty property) = _targets[i];
            if (target is IAnimatable animatable && clock.Children[i] is AnimationClock child)
            {
                animatable.ApplyAnimationClock(property, child, HandoffBehavior.SnapshotAndReplace);
                Owners.GetOrCreateValue(target)[property] = this;
            }
        }

        _clock = clock;
        return clock;
    }

    public void Remove()
    {
        ClockGroup? clock = _clock;
        _clock = null;
        if (clock is null)
        {
            return;
        }

        foreach ((DependencyObject target, DependencyProperty property) in _targets)
        {
            if (Owners.TryGetValue(target, out Dictionary<DependencyProperty, ClockSet>? owners)
                && owners.TryGetValue(property, out ClockSet? owner) && ReferenceEquals(owner, this))
            {
                owners.Remove(property);
                if (target is IAnimatable animatable)
                {
                    animatable.ApplyAnimationClock(property, null);
                }
            }
        }

        clock.Controller?.Remove();
    }

    public TimeSpan? CurrentTime => _clock?.CurrentTime;

    /// <summary>The timelines of the set (before it starts, to adjust them).</summary>
    public IEnumerable<Timeline> Timelines => _root.Children;

    /// <summary>The clock set that animates <paramref name="property"/> on <paramref name="target"/> now, or null.</summary>
    public static ClockSet? OwnerOf(DependencyObject target, DependencyProperty property) =>
        Owners.TryGetValue(target, out Dictionary<DependencyProperty, ClockSet>? owners) && owners.TryGetValue(property, out ClockSet? owner)
            ? owner
            : null;

    /// <summary>This set still animates <paramref name="property"/> on <paramref name="target"/> (no newer set took it over).</summary>
    public bool Owns(DependencyObject target, DependencyProperty property) => ReferenceEquals(OwnerOf(target, property), this);

    public void Seek(TimeSpan time, bool pause)
    {
        ClockController? controller = _clock?.Controller;
        if (controller is null)
        {
            return;
        }

        if (pause)
        {
            controller.Pause();
        }

        controller.SeekAlignedToLastTick(time, TimeSeekOrigin.BeginTime);
    }

    public IEnumerable<(DependencyObject Target, DependencyProperty Property)> Targets => _targets;
}

/// <summary>
/// One moment (a state change, a press, a hit): a group of animations that start together, like one Storyboard.
/// Targets are objects (elements, transforms, brushes, effects), not names. When the moment completes its clocks are
/// removed, so the targets fall back to their local (resting) values: every moment animates INTO the rest pose that the
/// control has already set (the prototype's <c>fill: backwards</c>). Stopping a moment therefore always lands on the
/// rest pose, which keeps rapid state flips clean, and leaves no clock behind. <see cref="Interrupt"/> lands there
/// smoothly instead: whatever the moment still holds away from its rest pose eases back within a short settle.
/// A moment begun while a <see cref="StateMoment"/> is entered on this thread belongs to it (the window's one
/// orchestrated moment per state change). Under reduced motion every moment is kept short in one place, like the
/// prototype's <c>A()</c>: each of its animations lasts at most <see cref="Anim.ReducedMaxMs"/> and starts at most
/// <see cref="Anim.ReducedMaxDelayMs"/> in (<see cref="ExactTiming"/> opts out).
/// </summary>
public sealed class MotionMoment
{
    [ThreadStatic]
    private static List<MotionMoment>? ActiveMomentsList;

    private readonly ClockSet _clocks = new(new ParallelTimeline { FillBehavior = FillBehavior.HoldEnd });
    private bool _begun;
    private bool _ended;
    private MotionMoment? _settle;

    /// <summary>Creates an empty moment owned by <paramref name="owner"/> (diagnostics; the clocks live on its dispatcher).</summary>
    public MotionMoment(FrameworkElement owner, string name)
    {
        Owner = owner;
        Name = name;
    }

    /// <summary>Raised once the moment has finished playing (not when it is stopped early).</summary>
    public event EventHandler? Completed;

    /// <summary>
    /// Raised exactly once when a begun moment is over, however it ended: it played to the end (after
    /// <see cref="Completed"/>), it was stopped, or it was interrupted (once its settle has landed on the rest pose).
    /// Clean-up that must always happen (removing a leaving ghost, dropping a temporary effect) belongs here.
    /// </summary>
    public event EventHandler? Ended;

    /// <summary>The element the moment belongs to.</summary>
    public FrameworkElement Owner { get; }

    /// <summary>Diagnostic name (shows in <see cref="MotionDiagnostics"/>).</summary>
    public string Name { get; }

    /// <summary>The moment is playing (begun, not completed or stopped), or settling after an interruption.</summary>
    public bool IsActive => _clocks.IsAttached || _settle is { IsActive: true };

    /// <summary>Where the playing moment is, from its begin (null when it is not playing its own clocks).</summary>
    public TimeSpan? CurrentTime => _clocks.IsAttached ? _clocks.CurrentTime : null;

    /// <summary>The moment was interrupted and its displaced values are easing back to their rest pose.</summary>
    public bool IsSettling => _settle is { IsActive: true };

    /// <summary>The moment is over (<see cref="Ended"/> was raised).</summary>
    public bool HasEnded => _ended;

    /// <summary>Number of animations in the moment.</summary>
    public int Count => _clocks.Count;

    /// <summary>
    /// The moment plays exactly as built under reduced motion too (the skill check's own one-shots, whose landing is
    /// timed against the Great sound and the reveal, and the Test countdown). Otherwise, under reduced motion, every
    /// animation in it is capped when it begins (<see cref="Anim.ClampForReducedMotion"/>). Set it before
    /// <see cref="Begin"/>.
    /// </summary>
    public bool ExactTiming { get; set; }

    /// <summary>Moments that are playing on the calling thread (verification hook).</summary>
    internal static IReadOnlyList<MotionMoment> ActiveOnThisThread => (IReadOnlyList<MotionMoment>?)ActiveMomentsList ?? Array.Empty<MotionMoment>();

    /// <summary>
    /// Stops every moment playing on the calling thread whose owner's <see cref="MotionPolicy"/> no longer lets one-shot
    /// moments play (the "Play animations" setting turned off, the window minimized, hidden or no longer the active
    /// window): each lands on its rest pose at once and nothing is left mid-way. Returns how many were stopped.
    /// </summary>
    public static int StopDisallowed()
    {
        int stopped = 0;
        foreach (MotionMoment moment in ActiveOnThisThread.ToList())
        {
            if (!MotionPolicy.For(moment.Owner).ShouldAnimate)
            {
                moment.Stop();
                stopped++;
            }
        }

        return stopped;
    }

    /// <summary>Adds an animation of <paramref name="property"/> on <paramref name="target"/>.</summary>
    public MotionMoment Add(DependencyObject target, DependencyProperty property, AnimationTimeline timeline)
    {
        if (_begun)
        {
            throw new InvalidOperationException("A moment cannot change after it has begun.");
        }

        _clocks.Add(target, property, timeline);
        return this;
    }

    /// <summary>Starts the moment. An empty moment completes at once.</summary>
    public void Begin()
    {
        if (_begun)
        {
            return;
        }

        _begun = true;
        if (_clocks.Count == 0)
        {
            Completed?.Invoke(this, EventArgs.Empty);
            RaiseEnded();
            return;
        }

        if (!ExactTiming && MotionPolicy.For(Owner).ReducedMotion)
        {
            // reduced motion: no long choreography, short fades only (the prototype's A() clamps every one-shot)
            foreach (Timeline timeline in _clocks.Timelines)
            {
                Anim.ClampForReducedMotion(timeline);
            }
        }

        StateMoment.Register(this);
        (ActiveMomentsList ??= new List<MotionMoment>()).Add(this);
        _clocks.Start(OnCompleted);
    }

    /// <summary>
    /// What the playing moment shows now: every property it still animates (one no newer moment has taken over) with
    /// its current animated value. Read it before the moment stops to carry on from where it was. Empty when the moment
    /// is not playing its own clocks.
    /// </summary>
    public IReadOnlyList<(DependencyObject Target, DependencyProperty Property, object Value)> CurrentValues()
    {
        var values = new List<(DependencyObject, DependencyProperty, object)>();
        if (!_clocks.IsAttached)
        {
            return values;
        }

        var seen = new HashSet<(DependencyObject, DependencyProperty)>();
        foreach ((DependencyObject target, DependencyProperty property) in _clocks.Targets)
        {
            if (seen.Add((target, property)) && _clocks.Owns(target, property))
            {
                values.Add((target, property, target.GetValue(property)));
            }
        }

        return values;
    }

    /// <summary>Stops the moment (and a settle in progress) and removes its clocks: the targets show their resting values.</summary>
    public void Stop()
    {
        MotionMoment? settle = _settle;
        _settle = null;
        settle?.Stop();
        if (_clocks.IsAttached)
        {
            ActiveMomentsList?.Remove(this);
            _clocks.Remove();
        }

        RaiseEnded();
    }

    /// <summary>
    /// Ends the moment early without a jump: every value it still holds away from the rest pose (a property no newer
    /// moment has taken over) eases back to it within <paramref name="settle"/> (ease-out; under reduced motion only
    /// opacities and colours ease, at most 200 ms), then <see cref="Ended"/> is raised. With a zero settle, or when
    /// nothing may animate, this is <see cref="Stop"/>. Nothing happens to a moment that is not playing.
    /// </summary>
    public void Interrupt(TimeSpan settle)
    {
        if (!_clocks.IsAttached)
        {
            return;
        }

        // what the moment still shows, read before its clocks go (the animated values)
        IReadOnlyList<(DependencyObject Target, DependencyProperty Property, object Current)> displaced = CurrentValues();
        ActiveMomentsList?.Remove(this);
        _clocks.Remove();
        MotionPolicy policy = MotionPolicy.For(Owner);
        MotionMoment? moment = settle > TimeSpan.Zero && policy.ShouldAnimate ? BuildSettle(displaced, settle, policy.ReducedMotion) : null;
        if (moment is null)
        {
            RaiseEnded();
            return;
        }

        _settle = moment;
        moment.Ended += (_, _) =>
        {
            if (ReferenceEquals(_settle, moment))
            {
                _settle = null;
            }

            RaiseEnded();
        };
        moment.Begin();
    }

    /// <summary>Freezes the moment at <paramref name="time"/> from its begin (frame captures).</summary>
    public void FreezeAt(TimeSpan time) => _clocks.Seek(time, pause: true);

    private MotionMoment? BuildSettle(IReadOnlyList<(DependencyObject Target, DependencyProperty Property, object Current)> displaced, TimeSpan settle, bool reduced)
    {
        double ms = reduced ? Math.Min(200, settle.TotalMilliseconds) : settle.TotalMilliseconds;
        var moment = new MotionMoment(Owner, Name + ".settle");
        foreach ((DependencyObject target, DependencyProperty property, object current) in displaced)
        {
            object rest = target.GetValue(property);
            if (current is double from && rest is double to)
            {
                bool fade = property.Name == nameof(UIElement.Opacity);
                if (double.IsFinite(from) && double.IsFinite(to) && Math.Abs(from - to) > 0.0005 && (fade || !reduced))
                {
                    moment.Add(target, property, Anim.FromTo(from, to, ms, Ease.Out));
                }
            }
            else if (current is System.Windows.Media.Color fromColor && rest is System.Windows.Media.Color toColor && fromColor != toColor)
            {
                moment.Add(target, property, Anim.Colors(0, new ColorKf(0, fromColor), new ColorKf(ms, toColor, Ease.Out)));
            }
        }

        return moment.Count == 0 ? null : moment;
    }

    private void RaiseEnded()
    {
        if (!_begun || _ended)
        {
            return;
        }

        _ended = true;
        Ended?.Invoke(this, EventArgs.Empty);
    }

    private void OnCompleted(object? sender, EventArgs e)
    {
        if (!_clocks.IsAttached || !ReferenceEquals(sender, _clocks.Clock))
        {
            return;
        }

        ActiveMomentsList?.Remove(this);
        _clocks.Remove();
        Completed?.Invoke(this, EventArgs.Empty);
        RaiseEnded();
    }
}

/// <summary>
/// An ambient loop (fog drift, flicker, embers, sway, heartbeat, a spinning needle): forever-repeating animations that
/// can halt IN PLACE (the current values are kept as local values and the clocks are removed, so nothing ticks) and
/// resume from the same point of their cycle. A loop of slow, nearly still motion asks for fewer frames
/// (<see cref="DesiredFrameRate"/>), so the window is not re-composited at the full tick rate for it.
/// </summary>
public sealed class AmbientLoop
{
    /// <summary>Frames a second for near-still drift (the fog, the legs' sway): a few pixels a second at most.</summary>
    public const int SlowFrameRate = 20;

    /// <summary>Frames a second for small, quick ambient motion (the campfire's flicker and embers, a heartbeat).</summary>
    public const int LivelyFrameRate = 30;

    [ThreadStatic]
    private static List<AmbientLoop>? RunningLoopsList;

    private readonly Func<IEnumerable<(DependencyObject Target, DependencyProperty Property, AnimationTimeline Timeline)>> _build;
    private readonly List<(DependencyObject Target, DependencyProperty Property, object? Base)> _targets = new();
    private ClockSet? _clocks;
    private TimeSpan _offset;

    /// <summary>
    /// Creates a loop owned by <paramref name="owner"/>; <paramref name="build"/> returns its animations.
    /// <paramref name="desiredFrameRate"/> caps how often it asks for a frame (null: every frame, e.g. the skill check's
    /// spinning needle).
    /// </summary>
    public AmbientLoop(FrameworkElement owner, string name, Func<IEnumerable<(DependencyObject Target, DependencyProperty Property, AnimationTimeline Timeline)>> build, int? desiredFrameRate = null)
    {
        Owner = owner;
        Name = name;
        _build = build;
        DesiredFrameRate = desiredFrameRate is > 0 ? desiredFrameRate : null;
    }

    /// <summary>The element the loop belongs to.</summary>
    public FrameworkElement Owner { get; }

    /// <summary>Diagnostic name.</summary>
    public string Name { get; }

    /// <summary>The frames a second the loop asks for (<c>Timeline.DesiredFrameRate</c> on its root); null: every frame.</summary>
    public int? DesiredFrameRate { get; }

    /// <summary>Clocks are running.</summary>
    public bool IsRunning => _clocks is not null;

    /// <summary>Where in its cycle the loop is (running) or resumes (held); set it before <see cref="Run"/> to start part-way.</summary>
    public TimeSpan Offset
    {
        get => _clocks?.CurrentTime ?? _offset;
        set => _offset = value < TimeSpan.Zero ? TimeSpan.Zero : value;
    }

    /// <summary>Loops running on the calling thread (verification hook).</summary>
    internal static IReadOnlyList<AmbientLoop> RunningOnThisThread => (IReadOnlyList<AmbientLoop>?)RunningLoopsList ?? Array.Empty<AmbientLoop>();

    /// <summary>Runs (or keeps running) the loop, continuing from where it was halted.</summary>
    public void Run()
    {
        if (_clocks is not null)
        {
            return;
        }

        var root = new ParallelTimeline();
        if (DesiredFrameRate is int fps)
        {
            Timeline.SetDesiredFrameRate(root, fps);
        }

        var clocks = new ClockSet(root);
        bool record = _targets.Count == 0;
        foreach ((DependencyObject target, DependencyProperty property, AnimationTimeline timeline) in _build())
        {
            clocks.Add(target, property, timeline);
            if (record)
            {
                _targets.Add((target, property, target.ReadLocalValue(property)));
            }
        }

        if (clocks.Count == 0)
        {
            return;
        }

        _clocks = clocks;
        (RunningLoopsList ??= new List<AmbientLoop>()).Add(this);
        clocks.Start(null);
        if (_offset > TimeSpan.Zero)
        {
            clocks.Seek(_offset, pause: false);
        }
    }

    /// <summary>
    /// Stops the clocks and keeps the current values in place (the loop pauses where it is). Only the properties the
    /// loop still animates are held: a property a newer <see cref="MotionMoment"/> has taken over keeps that moment's
    /// clock and lands on the moment's own rest pose (its local value is not overwritten with a mid-animation value).
    /// </summary>
    public void Halt()
    {
        if (_clocks is null)
        {
            return;
        }

        _offset = _clocks.CurrentTime ?? _offset;
        var held = new List<(DependencyObject, DependencyProperty, object)>();
        foreach ((DependencyObject target, DependencyProperty property) in _clocks.Targets)
        {
            if (_clocks.Owns(target, property))
            {
                held.Add((target, property, target.GetValue(property)));
            }
        }

        RemoveClocks();
        foreach ((DependencyObject target, DependencyProperty property, object value) in held)
        {
            target.SetValue(property, value);
        }
    }

    /// <summary>Stops the clocks and forgets the position; the targets show whatever local values they have.</summary>
    public void Stop()
    {
        RemoveClocks();
        _offset = TimeSpan.Zero;
    }

    /// <summary>Stops the clocks and restores the resting values (the loop starts over next time). A property that a
    /// running <see cref="MotionMoment"/> animates is left to that moment (it lands on its own rest pose).</summary>
    public void Reset()
    {
        RemoveClocks();
        _offset = TimeSpan.Zero;
        foreach ((DependencyObject target, DependencyProperty property, object? baseValue) in _targets)
        {
            if (ClockSet.OwnerOf(target, property) is not null)
            {
                continue;
            }

            if (baseValue == DependencyProperty.UnsetValue)
            {
                target.ClearValue(property);
            }
            else
            {
                target.SetValue(property, baseValue);
            }
        }
    }

    /// <summary>Runs when <paramref name="run"/> is true, otherwise halts in place.</summary>
    public void Set(bool run)
    {
        if (run)
        {
            Run();
        }
        else
        {
            Halt();
        }
    }

    /// <summary>Moves the loop to <paramref name="time"/> into its cycle and pauses it there (frame captures).</summary>
    public void FreezeAt(TimeSpan time)
    {
        _offset = time;
        if (_clocks is null)
        {
            Run();
        }

        _clocks?.Seek(time, pause: true);
    }

    private void RemoveClocks()
    {
        if (_clocks is null)
        {
            return;
        }

        _clocks.Remove();
        _clocks = null;
        RunningLoopsList?.Remove(this);
    }
}
