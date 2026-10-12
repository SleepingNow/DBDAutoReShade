using System.Windows.Threading;
using DBDAutoReShade.App.Media;

namespace DBDAutoReShade.App.ViewModels;

/// <summary>Where the Great is shown when a map is found: the main window's Status screen (the skill check).</summary>
public interface IGreatStage
{
    /// <summary>
    /// The skill check can land in view right now: the main window is shown, not minimized and active, and the Status
    /// screen shows the scanning check (see <see cref="GreatCoordinator.CanLand"/>). With animations off it lands too,
    /// as a still flash (an opacity swap) with no time to the hit.
    /// </summary>
    bool CanLand { get; }

    /// <summary>
    /// Lands the skill check for <paramref name="map"/> (or the realm) and returns the delay to its hit (zero with
    /// animations off or under reduced motion: the hit is now).
    /// </summary>
    TimeSpan Land(string realm, string? map);
}

/// <summary>Runs an action once after a delay (a DispatcherTimer in the app; a recorder in tests).</summary>
public interface IDelayScheduler
{
    /// <summary>Runs <paramref name="action"/> once, <paramref name="delay"/> from now.</summary>
    void Schedule(TimeSpan delay, Action action);
}

/// <summary>
/// A one-shot <see cref="DispatcherTimer"/> per call on the given dispatcher, at Send priority. It ticks while the
/// window is minimized or covered (no rendering involved), which is exactly when the Great sound matters.
/// </summary>
public sealed class DispatcherDelayScheduler : IDelayScheduler
{
    private readonly Dispatcher _dispatcher;

    /// <param name="dispatcher">The dispatcher the timers run on (the UI thread's).</param>
    public DispatcherDelayScheduler(Dispatcher dispatcher) => _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

    /// <inheritdoc />
    public void Schedule(TimeSpan delay, Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var timer = new DispatcherTimer(DispatcherPriority.Send, _dispatcher) { Interval = delay < TimeSpan.Zero ? TimeSpan.Zero : delay };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            action();
        };
        timer.Start();
    }
}

/// <summary>How a map found was presented.</summary>
public enum GreatPresentation
{
    /// <summary>No landing (window minimized, hidden or inactive, Status not shown): the sound played at once.</summary>
    Immediate,

    /// <summary>The skill check landed; the sound plays at its hit (scheduled from the computed delay).</summary>
    Landed,
}

/// <summary>What <see cref="GreatCoordinator.OnMapFound"/> did.</summary>
/// <param name="Presentation">Landed or immediate.</param>
/// <param name="SoundDelay">When the sound plays, from now (zero: it already played).</param>
public sealed record GreatPlan(GreatPresentation Presentation, TimeSpan SoundDelay);

/// <summary>
/// The Great skill check and its sound when the controller finds a map (<c>AppController.MapDetected</c>), exactly
/// once per detection. When the main window can show it (<see cref="CanLand"/>) the skill check lands and the sound
/// is scheduled from the landing's computed hit delay on a timer, so it is exact even if rendering stalls (with
/// animations off the landing is a still flash with no delay, so the sound plays at once); otherwise (the player is
/// in the game, the window minimized, hidden or inactive) the sound plays at once. The sound itself goes through the
/// controller, which applies the Sound setting and the volume.
/// </summary>
public sealed class GreatCoordinator
{
    private readonly Func<bool> _playSound;

    /// <param name="playSound">Plays the Great sound with the user's settings (AppController.PlayGreatSound).</param>
    /// <param name="scheduler">Runs the landing's sound at its hit (null: the sound plays at once even after a landing).</param>
    public GreatCoordinator(Func<bool> playSound, IDelayScheduler? scheduler)
    {
        _playSound = playSound ?? throw new ArgumentNullException(nameof(playSound));
        Scheduler = scheduler;
    }

    /// <summary>Runs the landing's sound at its hit (the main window sets a dispatcher timer one when none was given).</summary>
    public IDelayScheduler? Scheduler { get; set; }

    /// <summary>The stage (the main window); null while there is none (setup, tests): the sound plays at once.</summary>
    public IGreatStage? Stage { get; set; }

    /// <summary>
    /// The landing decision: the window is visible, not minimized and active, the Status screen is shown and its skill
    /// check is scanning. Animations do not matter: with "Play animations" off the check still lands, as a still flash
    /// (an opacity swap) and the score event, and its hit is now (<see cref="MotionPolicy.CanPresentLanding"/> then
    /// decides only how the landing moves).
    /// </summary>
    public static bool CanLand(MotionPolicy policy, bool statusShown, bool skillCheckScanning)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return policy.WindowVisible && policy.WindowActive && statusShown && skillCheckScanning;
    }

    /// <summary>A map was found: land the skill check and play the sound at its hit, or play it at once.</summary>
    public GreatPlan OnMapFound(string realm, string? map)
    {
        IGreatStage? stage = Stage;
        bool land;
        try
        {
            land = stage is not null && stage.CanLand;
        }
        catch
        {
            land = false;
        }

        if (land)
        {
            TimeSpan hit;
            try
            {
                hit = stage!.Land(realm, map);
            }
            catch
            {
                hit = TimeSpan.Zero; // the screen failed to land: the sound must still play
            }

            IDelayScheduler? scheduler = Scheduler;
            if (hit > TimeSpan.Zero && scheduler is not null)
            {
                bool played = false;
                scheduler.Schedule(hit, () =>
                {
                    if (!played)
                    {
                        played = true;
                        Play();
                    }
                });
                return new GreatPlan(GreatPresentation.Landed, hit);
            }

            Play();
            return new GreatPlan(GreatPresentation.Landed, TimeSpan.Zero);
        }

        Play();
        return new GreatPlan(GreatPresentation.Immediate, TimeSpan.Zero);
    }

    private void Play()
    {
        try
        {
            _playSound();
        }
        catch
        {
            // A sound never breaks the caller.
        }
    }
}
