using System.Diagnostics;

namespace DBDAutoReShade.App.Media;

/// <summary>
/// The window's one orchestrated moment for a state change (the prototype's set of animations per <c>changeState</c>):
/// every <see cref="MotionMoment"/> begun on this thread while it is entered (<see cref="Enter()"/>) belongs to it,
/// whichever control plays it — the map plate, the skill check, the Entity's legs, the campfire, the backdrop, the
/// hero and status texts, the banner. The next state change interrupts it as a whole (<see cref="Interrupt"/>): what
/// is still playing eases back to its rest pose instead of jumping, so fast state flips never stack or garble. A
/// moment can be entered again later at an offset (the Great: the dial lands first, the fog clears off the map after
/// it has lifted), and <see cref="FreezeAt"/> poses every member at one time of the whole story (frame captures).
/// </summary>
public sealed class StateMoment
{
    [ThreadStatic]
    private static List<(StateMoment Moment, TimeSpan Offset)>? EnteredStack;

    private readonly List<Member> _members = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    /// <summary>Creates a state moment (nothing plays until moments are begun inside <see cref="Enter()"/>).</summary>
    /// <param name="name">Diagnostic name, e.g. "Scanning → WaitingFocus".</param>
    public StateMoment(string name) => Name = name ?? string.Empty;

    /// <summary>Diagnostic name.</summary>
    public string Name { get; }

    /// <summary>The state moment entered on the calling thread (the innermost), or null.</summary>
    public static StateMoment? Current => EnteredStack is { Count: > 0 } stack ? stack[^1].Moment : null;

    /// <summary>Real time since the state moment was created.</summary>
    public TimeSpan Elapsed => _clock.Elapsed;

    /// <summary>Its moments that are still playing (or settling).</summary>
    public IReadOnlyList<MotionMoment> Moments => _members.Where(m => m.Moment.IsActive).Select(m => m.Moment).ToList();

    /// <summary>Names of its moments that are still playing (diagnostics).</summary>
    public IReadOnlyList<string> MomentNames => Moments.Select(m => m.Name).ToList();

    /// <summary>Something of it is still playing.</summary>
    public bool IsPlaying => _members.Any(m => m.Moment.IsActive);

    /// <summary>A later state change interrupted it.</summary>
    public bool WasInterrupted { get; private set; }

    /// <summary>
    /// Enters the state moment at its current <see cref="Elapsed"/> time: moments begun on this thread until the token
    /// is disposed belong to it. Entries nest; the innermost wins.
    /// </summary>
    public IDisposable Enter() => Enter(Elapsed);

    /// <summary>Enters the state moment; moments begun inside start <paramref name="offset"/> into its story
    /// (used by <see cref="FreezeAt"/>).</summary>
    public IDisposable Enter(TimeSpan offset)
    {
        List<(StateMoment, TimeSpan)> stack = EnteredStack ??= new List<(StateMoment, TimeSpan)>();
        stack.Add((this, offset < TimeSpan.Zero ? TimeSpan.Zero : offset));
        return new Exit(stack, this);
    }

    /// <summary>
    /// Interrupts every moment still playing: each eases back to its rest pose within <paramref name="settle"/>
    /// (<see cref="MotionMoment.Interrupt"/>); with zero, each stops at once. Settles begun while another state moment
    /// is entered belong to that one.
    /// </summary>
    public void Interrupt(TimeSpan settle)
    {
        WasInterrupted = true;
        foreach (Member member in _members.ToList())
        {
            if (settle > TimeSpan.Zero)
            {
                member.Moment.Interrupt(settle);
            }
            else
            {
                member.Moment.Stop();
            }
        }
    }

    /// <summary>Poses every member at <paramref name="time"/> into the whole story (each at its own offset; frame captures).</summary>
    public void FreezeAt(TimeSpan time)
    {
        foreach (Member member in _members.ToList())
        {
            TimeSpan local = time - member.Offset;
            member.Moment.FreezeAt(local < TimeSpan.Zero ? TimeSpan.Zero : local);
        }
    }

    /// <summary><paramref name="moment"/> belongs to this state moment (and has not ended).</summary>
    public bool Has(MotionMoment moment) => _members.Any(m => ReferenceEquals(m.Moment, moment));

    /// <summary>Called by <see cref="MotionMoment.Begin"/>: the moment joins the state moment entered on this thread.</summary>
    internal static void Register(MotionMoment moment)
    {
        if (EnteredStack is not { Count: > 0 } stack)
        {
            return;
        }

        (StateMoment owner, TimeSpan offset) = stack[^1];
        var member = new Member(moment, offset);
        owner._members.Add(member);
        moment.Ended += (_, _) => owner._members.Remove(member);
    }

    private sealed class Member(MotionMoment moment, TimeSpan offset)
    {
        public MotionMoment Moment { get; } = moment;

        public TimeSpan Offset { get; } = offset;
    }

    private sealed class Exit(List<(StateMoment Moment, TimeSpan Offset)> stack, StateMoment moment) : IDisposable
    {
        private bool _done;

        public void Dispose()
        {
            if (_done)
            {
                return;
            }

            _done = true;
            int index = stack.FindLastIndex(e => ReferenceEquals(e.Moment, moment));
            if (index >= 0)
            {
                stack.RemoveAt(index);
            }
        }
    }
}
