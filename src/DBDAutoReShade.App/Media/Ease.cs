using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DBDAutoReShade.App.Media;

/// <summary>
/// The Campfire motion curves (the prototype's CSS variables, as frozen WPF <see cref="KeySpline"/>s).
/// A <see cref="KeySpline"/> on a WPF key frame shapes the segment that ARRIVES at that key frame.
/// </summary>
public static class Ease
{
    /// <summary>ease-out <c>cubic-bezier(0.23, 1, 0.32, 1)</c>: entering and exiting.</summary>
    public static KeySpline Out { get; } = Frozen(0.23, 1, 0.32, 1);

    /// <summary>ease-in-out <c>cubic-bezier(0.77, 0, 0.175, 1)</c>: moving on screen.</summary>
    public static KeySpline InOut { get; } = Frozen(0.77, 0, 0.175, 1);

    /// <summary>CSS <c>ease-in-out</c> keyword <c>cubic-bezier(0.42, 0, 0.58, 1)</c> (ambient loops: fog drift, sway, flicker).</summary>
    public static KeySpline CssInOut { get; } = Frozen(0.42, 0, 0.58, 1);

    /// <summary>CSS <c>ease</c> keyword <c>cubic-bezier(0.25, 0.1, 0.25, 1)</c>.</summary>
    public static KeySpline Css { get; } = Frozen(0.25, 0.1, 0.25, 1);

    /// <summary>Linear (constant motion: a needle sweep, rising embers).</summary>
    public static KeySpline Linear { get; } = Frozen(0, 0, 1, 1);

    /// <summary>Eased progress of <paramref name="spline"/> at linear progress <paramref name="t"/> (0..1).</summary>
    public static double Progress(KeySpline spline, double t) => spline.GetSplineProgress(Math.Clamp(t, 0, 1));

    /// <summary>
    /// The linear time (0..1) at which <paramref name="spline"/> reaches eased progress <paramref name="progress"/>.
    /// Used to place key frames of a WAAPI animation whose whole iteration was eased (the prototype's
    /// <c>el.animate(frames, { easing })</c>): a frame at offset o is reached at <c>Inverse(easing, o)</c>.
    /// </summary>
    public static double Inverse(KeySpline spline, double progress)
    {
        double lo = 0, hi = 1;
        progress = Math.Clamp(progress, 0, 1);
        for (int i = 0; i < 40; i++)
        {
            double mid = (lo + hi) / 2;
            if (spline.GetSplineProgress(mid) < progress)
            {
                lo = mid;
            }
            else
            {
                hi = mid;
            }
        }

        return (lo + hi) / 2;
    }

    private static KeySpline Frozen(double x1, double y1, double x2, double y2)
    {
        var spline = new KeySpline(new Point(x1, y1), new Point(x2, y2));
        spline.Freeze();
        return spline;
    }
}

/// <summary>One key frame for <see cref="Anim"/>: a value reached at <see cref="AtMs"/> through <see cref="Ease"/>
/// (null = linear; the curve shapes the segment that arrives at this frame).</summary>
/// <param name="AtMs">Time of the key frame, in milliseconds from the animation's begin.</param>
/// <param name="Value">The value at that time.</param>
/// <param name="Ease">The curve of the segment arriving here (null = linear).</param>
public readonly record struct Kf(double AtMs, double Value, KeySpline? Ease = null);

/// <summary>
/// One keyframe of a Web Animations (WAAPI) effect, as the prototype writes them (<c>el.animate(frames, options)</c>):
/// a value at an <see cref="Offset"/> (0..1 of the iteration) and the easing of the segment that LEAVES this keyframe
/// (null = linear).
/// </summary>
/// <param name="Offset">Where the keyframe sits in the iteration, 0..1.</param>
/// <param name="Value">The value there.</param>
/// <param name="Easing">The easing of the segment from this keyframe to the next (null = linear).</param>
public readonly record struct WaapiKf(double Offset, double Value, KeySpline? Easing = null);

/// <summary>One colour key frame for <see cref="Anim"/>.</summary>
/// <param name="AtMs">Time of the key frame, in milliseconds from the animation's begin.</param>
/// <param name="Value">The colour at that time.</param>
/// <param name="Ease">The curve of the segment arriving here (null = linear).</param>
public readonly record struct ColorKf(double AtMs, Color Value, KeySpline? Ease = null);

/// <summary>Builders for the key-frame animations the Campfire controls use (milliseconds, KeySplines).</summary>
public static class Anim
{
    /// <summary>Under reduced motion an animation lasts at most this long (the prototype's A(): 200 ms).</summary>
    public const double ReducedMaxMs = 200;

    /// <summary>Under reduced motion an animation starts at most this far in (the prototype's A(): 160 ms).</summary>
    public const double ReducedMaxDelayMs = 160;

    /// <summary>
    /// Keeps <paramref name="timeline"/> short for reduced motion, the way the prototype's <c>A()</c> clamps every
    /// one-shot: its effect lasts at most <see cref="ReducedMaxMs"/> and starts at most <see cref="ReducedMaxDelayMs"/>
    /// in. The delay is the begin time plus a first value held from 0 (the fill-backwards hold of <see cref="Keys"/>);
    /// the key frames keep their places relative to the effect. Timelines that repeat, reverse, run at another speed,
    /// are frozen or use other than time key times are left as they are. Returns true when it changed anything.
    /// </summary>
    public static bool ClampForReducedMotion(Timeline timeline)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        if (timeline.IsFrozen || timeline.AutoReverse || Math.Abs(timeline.SpeedRatio - 1) > 1e-9 ||
            !(timeline.RepeatBehavior.HasCount && Math.Abs(timeline.RepeatBehavior.Count - 1) < 1e-9))
        {
            return false;
        }

        double begin = timeline.BeginTime?.TotalMilliseconds ?? 0;
        if (begin < 0)
        {
            return false;
        }

        if (timeline is not IKeyFrameAnimation keyed)
        {
            // a plain from / to animation
            bool changed = false;
            if (timeline.Duration.HasTimeSpan && timeline.Duration.TimeSpan.TotalMilliseconds > ReducedMaxMs)
            {
                timeline.Duration = new Duration(TimeSpan.FromMilliseconds(ReducedMaxMs));
                changed = true;
            }

            if (begin > ReducedMaxDelayMs)
            {
                timeline.BeginTime = TimeSpan.FromMilliseconds(ReducedMaxDelayMs);
                changed = true;
            }

            return changed;
        }

        System.Collections.IList frames = keyed.KeyFrames;
        if (frames is null || frames.Count == 0)
        {
            return false;
        }

        var times = new double[frames.Count];
        for (int i = 0; i < frames.Count; i++)
        {
            if (frames[i] is not IKeyFrame frame || frame.KeyTime.Type != KeyTimeType.TimeSpan)
            {
                return false;
            }

            times[i] = frame.KeyTime.TimeSpan.TotalMilliseconds;
        }

        // a first value held from 0 until the effect starts is the delay (Keys' fill-backwards hold)
        double start = frames.Count >= 2 && times[0] <= 0 && times[1] > 0 && frames[0]!.GetType().Name.StartsWith("Discrete", StringComparison.Ordinal)
            ? times[1]
            : 0;
        double end = times.Max();
        if (timeline.Duration.HasTimeSpan)
        {
            end = Math.Max(end, timeline.Duration.TimeSpan.TotalMilliseconds);
        }

        double delay = begin + start, length = Math.Max(0, end - start);
        double newDelay = Math.Min(delay, ReducedMaxDelayMs), newLength = Math.Min(length, ReducedMaxMs);
        if (Math.Abs(newDelay - delay) < 0.5 && Math.Abs(newLength - length) < 0.5)
        {
            return false;
        }

        double newBegin = Math.Min(begin, newDelay), newStart = newDelay - newBegin;
        double Map(double t) => t <= start
            ? (start > 0 ? t / start * newStart : 0)
            : newStart + (t - start) * (length > 0 ? newLength / length : 0);
        for (int i = 0; i < frames.Count; i++)
        {
            ((IKeyFrame)frames[i]!).KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(Map(times[i])));
        }

        if (begin > 0)
        {
            timeline.BeginTime = TimeSpan.FromMilliseconds(newBegin);
        }

        if (timeline.Duration.HasTimeSpan)
        {
            timeline.Duration = new Duration(TimeSpan.FromMilliseconds(Map(timeline.Duration.TimeSpan.TotalMilliseconds)));
        }

        return true;
    }

    /// <summary>A double animation through <paramref name="keys"/> starting <paramref name="delayMs"/> after the moment begins.
    /// The first key should be at 0 ms: it is held from the moment's begin (fill backwards) when <paramref name="holdBefore"/>.</summary>
    public static DoubleAnimationUsingKeyFrames Keys(double delayMs, IEnumerable<Kf> keys, bool holdBefore = true)
    {
        var anim = new DoubleAnimationUsingKeyFrames();
        double offset = holdBefore ? delayMs : 0;
        if (!holdBefore && delayMs > 0)
        {
            anim.BeginTime = TimeSpan.FromMilliseconds(delayMs);
        }

        bool first = true;
        foreach (Kf key in keys)
        {
            if (first && holdBefore && delayMs > 0)
            {
                anim.KeyFrames.Add(new DiscreteDoubleKeyFrame(key.Value, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            }

            first = false;
            KeyTime at = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(key.AtMs + offset));
            anim.KeyFrames.Add(key.Ease is null
                ? new LinearDoubleKeyFrame(key.Value, at)
                : new SplineDoubleKeyFrame(key.Value, at, key.Ease));
        }

        return anim;
    }

    /// <summary>A double animation through <paramref name="keys"/> (no delay).</summary>
    public static DoubleAnimationUsingKeyFrames Keys(params Kf[] keys) => Keys(0, keys);

    /// <summary>From → to over <paramref name="durationMs"/> after <paramref name="delayMs"/> (the from value is held during the delay).</summary>
    public static DoubleAnimationUsingKeyFrames FromTo(double from, double to, double durationMs, KeySpline? ease = null, double delayMs = 0) =>
        Keys(delayMs, new[] { new Kf(0, from), new Kf(durationMs, to, ease ?? Ease.Out) });

    /// <summary>A colour animation through <paramref name="keys"/> starting <paramref name="delayMs"/> after the moment begins.</summary>
    public static ColorAnimationUsingKeyFrames Colors(double delayMs, params ColorKf[] keys)
    {
        var anim = new ColorAnimationUsingKeyFrames();
        bool first = true;
        foreach (ColorKf key in keys)
        {
            if (first && delayMs > 0)
            {
                anim.KeyFrames.Add(new DiscreteColorKeyFrame(key.Value, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            }

            first = false;
            KeyTime at = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(key.AtMs + delayMs));
            anim.KeyFrames.Add(key.Ease is null
                ? new LinearColorKeyFrame(key.Value, at)
                : new SplineColorKeyFrame(key.Value, at, key.Ease));
        }

        return anim;
    }

    /// <summary>
    /// A WAAPI keyframe effect reproduced exactly (the prototype's <c>A(el, frames, { duration, easing })</c>): the
    /// iteration's progress is eased by <paramref name="iterationEasing"/> (the prototype's default is ease-out; null =
    /// linear), then each segment by the easing of the keyframe it leaves. A WPF key frame eases its segment in linear
    /// time, which cannot express an eased iteration over several keyframes (the yank that returns by 500 ms, not by
    /// 1.5 s), so the curve is sampled at 60 per second as linear key frames. The first value is held during
    /// <paramref name="delayMs"/> (fill backwards).
    /// </summary>
    public static DoubleAnimationUsingKeyFrames Waapi(double durationMs, KeySpline? iterationEasing, IReadOnlyList<WaapiKf> frames, double delayMs = 0)
    {
        ArgumentNullException.ThrowIfNull(frames);
        if (frames.Count == 0)
        {
            throw new ArgumentException("A WAAPI effect needs keyframes.", nameof(frames));
        }

        const double step = 1000.0 / 60;
        int samples = Math.Max(1, (int)Math.Ceiling(durationMs / step));
        var keys = new List<Kf>(samples + 1);
        for (int i = 0; i <= samples; i++)
        {
            double t = Math.Min(durationMs, i * step);
            keys.Add(new Kf(t, WaapiValue(frames, iterationEasing, durationMs <= 0 ? 1 : t / durationMs)));
        }

        return Keys(delayMs, keys);
    }

    /// <summary>The value of a WAAPI keyframe effect at linear iteration progress <paramref name="t"/> (0..1).</summary>
    public static double WaapiValue(IReadOnlyList<WaapiKf> frames, KeySpline? iterationEasing, double t)
    {
        ArgumentNullException.ThrowIfNull(frames);
        double p = iterationEasing is null ? Math.Clamp(t, 0, 1) : Ease.Progress(iterationEasing, t);
        if (frames.Count == 1 || p <= frames[0].Offset)
        {
            return frames[0].Value;
        }

        for (int i = 0; i < frames.Count - 1; i++)
        {
            WaapiKf a = frames[i], b = frames[i + 1];
            if (p <= b.Offset)
            {
                double span = b.Offset - a.Offset;
                double local = span <= 0 ? 1 : (p - a.Offset) / span;
                double eased = a.Easing is null ? local : Ease.Progress(a.Easing, local);
                return a.Value + (b.Value - a.Value) * eased;
            }
        }

        return frames[^1].Value;
    }

    /// <summary>
    /// A looping animation (ambient): the key frames span one iteration of <paramref name="periodMs"/>; it repeats
    /// forever, optionally back and forth (<paramref name="alternate"/>), starting <paramref name="phaseMs"/> into its
    /// cycle (CSS negative animation-delay).
    /// </summary>
    public static DoubleAnimationUsingKeyFrames Loop(double periodMs, IEnumerable<Kf> keys, bool alternate = false, double phaseMs = 0)
    {
        DoubleAnimationUsingKeyFrames anim = Keys(0, keys);
        anim.Duration = new Duration(TimeSpan.FromMilliseconds(periodMs));
        anim.AutoReverse = alternate;
        anim.RepeatBehavior = RepeatBehavior.Forever;
        if (phaseMs > 0)
        {
            anim.BeginTime = TimeSpan.FromMilliseconds(-phaseMs);
        }

        return anim;
    }
}
