using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;
using DBDAutoReShade.App.Controls;
using DBDAutoReShade.App.Media;
using DBDAutoReShade.App.ViewModels;

namespace DBDAutoReShade.App;

/// <summary>
/// The main window's motion director: one orchestrated moment per state change across the whole window (a
/// <see cref="StateMoment"/>, the prototype's <c>changeState</c> → <c>interrupt()</c> → <c>renderAll(from)</c>). Every
/// part of the change is played inside it — the map plate (reveal, sink, swap, the fire catching, burning down or dying
/// out, the ember confirm), the skill check, the Entity's legs (lean, flinch, rim light), the campfire light behind
/// everything, the hero and status texts' blur-bridged crossfades, the detection line, the glide of whatever the change
/// moved, the banners — and the next change interrupts it as a whole: what is still playing eases back to its rest pose
/// within <see cref="InterruptSettle"/> while the new moment starts. The Great is one moment too: the dial lands, and
/// after it has lifted the rest of the screen joins the same moment (the fog clears off the map). A change the user made
/// on the Status screen (the override on or off) is no state story: it is a quick swap (<see cref="QuickChange"/>) that
/// joins whatever plays and interrupts nothing. Nothing moves while nobody can watch: with the window inactive (the
/// game in front), minimized or hidden, or animations off, changes are instant and whatever played stops at once.
/// </summary>
public partial class MainWindow
{
    /// <summary>How long what an interrupted state change was still playing takes to ease back to its rest pose.</summary>
    public static readonly TimeSpan InterruptSettle = TimeSpan.FromMilliseconds(200);

    /// <summary>The backdrop's crossfade and the campfire light's fade for a change the user made (the override).</summary>
    public static readonly TimeSpan QuickChange = TimeSpan.FromMilliseconds(220);

    // The policy of a page that is not shown: nothing on it moves (ambient loops stop, changes are instant).
    private readonly MotionPolicy _offstage = new() { WindowVisible = false };
    private StateMoment? _stateMoment;

    /// <summary>The current (latest) state change's orchestrated moment; null before the first change.</summary>
    public StateMoment? CurrentMoment => _stateMoment;

    /// <summary>
    /// Presents <paramref name="next"/> on the Status screen and plays the window's part of the change. A state change
    /// (<see cref="StatusScreen.IsStateChange"/>: another state, or another scene on the plate) starts a new state moment
    /// and interrupts the previous one; a smaller update (a caption, a message, the tail) joins the current one.
    /// <paramref name="continuing"/> is the Great's moment: the change joins it at <paramref name="offset"/> (once the
    /// dial has lifted) instead. A <paramref name="quick"/> change is one the user made (the override on or off, the
    /// prototype's quickStatusMotion): it joins the current moment like a small update, interrupts nothing, and is told
    /// as a quick swap: no reveal or sink, no swell of the light (a 220 ms fade), no flare, no flinch.
    /// </summary>
    private void ApplyScreen(StatusScreen next, bool animate, bool quick = false, StateMoment? continuing = null, TimeSpan offset = default)
    {
        StatusScreen? previous = _shown;
        _shown = next;
        quick &= animate;
        bool beat = animate && !quick && StatusScreen.IsStateChange(previous, next);
        StateMoment? interrupted = null;
        StateMoment? moment = continuing;
        if (moment is null)
        {
            if (beat)
            {
                interrupted = _stateMoment;
                moment = _stateMoment = new StateMoment($"{previous!.State} → {next.State}");
                offset = TimeSpan.Zero;
            }
            else
            {
                moment = _stateMoment;
                offset = moment?.Elapsed ?? TimeSpan.Zero;
            }
        }

        using (moment?.Enter(offset))
        {
            bool confirming = StatusPage.Present(next, beat, quick);
            if (quick)
            {
                Backdrop.SetEmberLevel(next.EmberLevel, QuickChange);
            }
            else
            {
                Backdrop.EmberLevel = next.EmberLevel;
            }

            Legs.IsLeaning = next.Leaning;
            if (animate && !quick && previous is not null)
            {
                if (previous.State == ScreenState.Waiting && next.State != ScreenState.Waiting)
                {
                    Backdrop.Flare(TimeSpan.FromMilliseconds(60), TimeSpan.FromMilliseconds(700)); // the fire catches
                    TellDbdRunning(running: true);
                }
                else if (next.State == ScreenState.Waiting && previous.State != ScreenState.Waiting)
                {
                    TellDbdRunning(running: false);
                }

                if (confirming)
                {
                    Backdrop.Flare(TimeSpan.FromMilliseconds(260), TimeSpan.FromMilliseconds(1200)); // the ember flares
                    Legs.Flex();
                }

                if (beat && _shownTab == AppTab.Presets)
                {
                    PresetsPage.TellStateChange(previous.State, next.State); // the same story, told smaller
                }
            }

            bool mapChanged = previous is not null && previous.Plate.IsMap != next.Plate.IsMap;
            UpdateBackdrop(!animate ? TimeSpan.Zero : quick ? QuickChange : mapChanged ? TimeSpan.FromMilliseconds(next.Plate.IsMap ? 700 : 600) : TimeSpan.FromMilliseconds(220));
            UpdateSway();

            // what the previous change left playing eases back to rest (the settles belong to this moment)
            interrupted?.Interrupt(InterruptSettle);
        }
    }

    /// <summary><paramref name="moment"/> becomes the current state moment; the previous one is interrupted.</summary>
    private void TakeOver(StateMoment moment)
    {
        StateMoment? interrupted = _stateMoment;
        _stateMoment = moment;
        using (moment.Enter(TimeSpan.Zero))
        {
            interrupted?.Interrupt(InterruptSettle);
        }
    }

    /// <summary>
    /// The current state moment if something of it still plays (a later beat of the same change joins it), else a new
    /// one for <paramref name="name"/> (nothing is left to interrupt).
    /// </summary>
    private IDisposable EnterBeat(string name)
    {
        if (_stateMoment is { IsPlaying: true } current)
        {
            return current.Enter();
        }

        _stateMoment = new StateMoment(name);
        return _stateMoment.Enter(TimeSpan.Zero);
    }

    /// <summary>The restart notice or a hotkey problem opened or closed: the banner drops in, the page glides, and a
    /// restart notice runs the red rim light down the Entity's legs.</summary>
    private void TellBanners(bool restartOpened, bool hotkeyOpened)
    {
        if (!IsLoaded)
        {
            return; // (being built: the bindings show the banners as they are)
        }

        using (EnterBeat("Banners"))
        {
            StatusPage.TellBannersChanged(restartOpened, hotkeyOpened);
            if (restartOpened)
            {
                Legs.RimRun(); // restart needed: a red light runs down the legs
                PopAttention(PresetsTab);
            }
        }
    }

    /// <summary>The rail item's red attention dot appears: it pops in (scale .4 → 1.3 → 1 with a fade, 320 ms after 300 ms).</summary>
    private void PopAttention(FrameworkElement item)
    {
        BindingOperations.GetBindingExpression(item, CampfireAssist.ShowAttentionProperty)?.UpdateTarget();
        item.ApplyTemplate();
        if ((item as System.Windows.Controls.Control)?.Template?.FindName("Attention", item) is not Ellipse dot || !CampfireAssist.GetShowAttention(item) || !_policy.ShouldAnimate)
        {
            return;
        }

        if (dot.RenderTransform is not ScaleTransform scale || scale.IsFrozen)
        {
            scale = new ScaleTransform(1, 1);
            dot.RenderTransform = scale;
            dot.RenderTransformOrigin = new Point(0.5, 0.5);
        }

        // keyframes (0, .4) → (1, 1.3) at 60 % → (1, 1) over 320 ms, the whole iteration eased out (the overshoot comes early)
        var moment = new MotionMoment(this, "MainWindow.attention");
        moment.Add(dot, OpacityProperty, Anim.Waapi(320, Ease.Out, new[] { new WaapiKf(0, 0), new WaapiKf(0.6, 1), new WaapiKf(1, 1) }, 300));
        if (!_policy.ReducedMotion)
        {
            var pop = new[] { new WaapiKf(0, 0.4), new WaapiKf(0.6, 1.3), new WaapiKf(1, 1) };
            moment.Add(scale, ScaleTransform.ScaleXProperty, Anim.Waapi(320, Ease.Out, pop, 300));
            moment.Add(scale, ScaleTransform.ScaleYProperty, Anim.Waapi(320, Ease.Out, pop, 300));
        }

        moment.Begin();
    }

    /// <summary>Dead by Daylight started or closed (part of the waiting → ready / → waiting change): the rail's dot
    /// catches like a match (or simply goes out) and its text fades in (180 ms).</summary>
    private void TellDbdRunning(bool running)
    {
        if (!IsLoaded || !_policy.ShouldAnimate)
        {
            return;
        }

        if (running)
        {
            DbdDot.Catch();
        }

        var moment = new MotionMoment(this, "MainWindow.dbd");
        moment.Add(DbdStatusText, OpacityProperty, Anim.FromTo(0, 1, 180));
        moment.Begin();
    }

    /// <summary>
    /// The policy changed. When nothing may move any more ("Play animations" turned off, the window minimized or hidden,
    /// or another window, usually the game, in front) everything stops at once: the state moment, the tab crossfade and
    /// any other moment still playing on the window (each lands on its rest pose). Ambient loops stop by themselves (each
    /// control follows the policy).
    /// </summary>
    private void OnPolicyChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => OnPolicyChanged(sender, e)));
            return;
        }

        if (_policy.ShouldAnimate)
        {
            return;
        }

        _stateMoment?.Interrupt(TimeSpan.Zero);
        _tabMoment?.Stop();
        _tabMoment = null;
        MotionMoment.StopDisallowed();
    }
}
