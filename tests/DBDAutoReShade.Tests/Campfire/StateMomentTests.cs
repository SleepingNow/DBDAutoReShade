using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DBDAutoReShade.App.Media;

namespace DBDAutoReShade.Tests.Campfire;

/// <summary>
/// The motion kernel's orchestration: a moment ends exactly once however it ends (<see cref="MotionMoment.Ended"/>);
/// an interrupted moment eases back to its rest pose from where it was (no jump) and leaves nothing behind; a
/// <see cref="StateMoment"/> gathers the moments begun inside it, interrupts them as a whole and poses them at their
/// own offsets; moments a policy no longer allows are stopped at once; WAAPI keyframe effects are reproduced exactly.
/// </summary>
public sealed class StateMomentTests
{
    [Fact]
    public void AMoment_EndsExactlyOnce_HoweverItEnds()
    {
        CampfireTestHost.RunSta(() =>
        {
            (Window window, Border box, TranslateTransform move) = Host(new MotionPolicy());
            try
            {
                // played to the end: Completed, then Ended
                var order = new List<string>();
                MotionMoment played = Moment(box, move, 80);
                played.Completed += (_, _) => order.Add("completed");
                played.Ended += (_, _) => order.Add("ended");
                played.Begin();
                WindowScene.PumpUntil(() => played.HasEnded, "the end");
                Assert.Equal(new[] { "completed", "ended" }, order);

                // stopped: Ended only, at once; stopping again does nothing
                int ended = 0;
                MotionMoment stopped = Moment(box, move, 1000);
                stopped.Ended += (_, _) => ended++;
                stopped.Begin();
                stopped.Stop();
                stopped.Stop();
                Assert.Equal(1, ended);
                Assert.False(stopped.IsActive);

                // never begun: nothing
                MotionMoment idle = Moment(box, move, 1000);
                idle.Ended += (_, _) => ended++;
                idle.Stop();
                Assert.Equal(1, ended);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void AnInterruptedMoment_EasesBackFromWhereItWas_ThenEnds()
    {
        CampfireTestHost.RunSta(() =>
        {
            (Window window, Border box, TranslateTransform move) = Host(new MotionPolicy());
            try
            {
                move.Y = 0; // the rest pose
                var moment = new MotionMoment(box, "test.slide");
                moment.Add(move, TranslateTransform.YProperty, Anim.FromTo(-100, 0, 1000, Ease.Linear));
                int ended = 0;
                moment.Ended += (_, _) => ended++;
                moment.Begin();
                CampfireTestHost.Pump(); // (a clock is sought once it has had its first tick)
                moment.FreezeAt(TimeSpan.FromMilliseconds(400)); // -60 now
                Assert.Equal(-60, move.Y, 1);

                moment.Interrupt(TimeSpan.FromMilliseconds(200));
                CampfireTestHost.Pump();
                MotionDiagnostics.FreezeAllAt(TimeSpan.Zero); // the settle, at its start:
                Assert.Equal(-60, move.Y, 1); // no jump
                MotionDiagnostics.FreezeAllAt(TimeSpan.FromMilliseconds(100));
                Assert.InRange(move.Y, -59, -0.5); // on its way back
                Assert.True(moment.IsSettling);
                Assert.True(moment.IsActive);
                Assert.Equal(0, ended); // it ends once it has landed
                Assert.DoesNotContain("test.slide", MotionDiagnostics.ActiveMoments);
                Assert.Contains("test.slide.settle", MotionDiagnostics.ActiveMoments);

                MotionDiagnostics.FreezeAllAt(TimeSpan.FromMilliseconds(200));
                WindowScene.PumpUntil(() => moment.HasEnded, "the settle");
                Assert.Equal(1, ended);
                Assert.Equal(0, move.Y, 3);
                Assert.Empty(MotionDiagnostics.FindAnimatedObjects(box));

                // a property a newer moment took over is left to it
                var first = new MotionMoment(box, "test.first");
                first.Add(move, TranslateTransform.YProperty, Anim.FromTo(-100, 0, 1000));
                first.Add(box, UIElement.OpacityProperty, Anim.FromTo(0, 1, 1000));
                first.Begin();
                var second = new MotionMoment(box, "test.second");
                second.Add(move, TranslateTransform.YProperty, Anim.FromTo(50, 0, 1000));
                second.Begin();
                CampfireTestHost.Pump();
                first.Interrupt(TimeSpan.FromMilliseconds(200));
                Assert.True(second.IsActive);
                Assert.Contains("test.first.settle", MotionDiagnostics.ActiveMoments); // (the opacity eases back)
                second.FreezeAt(TimeSpan.Zero);
                Assert.Equal(50, move.Y, 1); // still the second moment's
                second.Stop();
                first.Stop();
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void Interrupt_IsAStop_WhenNothingMayMove_AndFadesOnly_UnderReducedMotion()
    {
        CampfireTestHost.RunSta(() =>
        {
            var policy = new MotionPolicy();
            (Window window, Border box, TranslateTransform move) = Host(policy);
            try
            {
                MotionMoment moment = Moment(box, move, 1000);
                moment.Begin();
                CampfireTestHost.Pump();
                moment.FreezeAt(TimeSpan.FromMilliseconds(500));
                policy.ReducedMotionOverride = true;
                moment.Interrupt(TimeSpan.FromMilliseconds(300));
                Assert.Equal(0, move.Y, 3); // the movement snapped home
                Assert.Contains("test.move.settle", MotionDiagnostics.ActiveMoments); // the fade eases
                Assert.DoesNotContain(MotionDiagnostics.FindAnimatedObjects(box), a => a.Contains("Transform", StringComparison.Ordinal));
                moment.Stop();

                MotionMoment off = Moment(box, move, 1000);
                off.Begin();
                policy.AnimationsEnabled = false;
                off.Interrupt(TimeSpan.FromMilliseconds(300));
                Assert.True(off.HasEnded);
                Assert.Empty(MotionDiagnostics.FindAnimatedObjects(box));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void AStateMoment_GathersItsMoments_InterruptsThemAsAWhole_AndPosesThemAtTheirOffsets()
    {
        CampfireTestHost.RunSta(() =>
        {
            (Window window, Border box, TranslateTransform move) = Host(new MotionPolicy());
            try
            {
                var other = new ScaleTransform(1, 1);
                var change = new StateMoment("Ready → Scanning");
                MotionMoment outside = Moment(box, move, 1000); // begun outside: not a member
                MotionMoment first, later;
                using (change.Enter(TimeSpan.Zero))
                {
                    Assert.Same(change, StateMoment.Current);
                    first = new MotionMoment(box, "test.first").Add(other, ScaleTransform.ScaleXProperty, Anim.FromTo(2, 1, 1000, Ease.Linear));
                    first.Begin();
                }

                Assert.Null(StateMoment.Current);
                outside.Begin();
                using (change.Enter(TimeSpan.FromMilliseconds(500)))
                {
                    later = new MotionMoment(box, "test.later").Add(box, UIElement.OpacityProperty, Anim.FromTo(0, 1, 1000, Ease.Linear));
                    later.Begin();
                }

                Assert.True(change.Has(first));
                Assert.True(change.Has(later));
                Assert.False(change.Has(outside));
                Assert.Equal(new[] { "test.first", "test.later" }, change.MomentNames);
                CampfireTestHost.Pump();

                // posed at 700 ms into the change: the first 700 ms in, the later one 200 ms in
                change.FreezeAt(TimeSpan.FromMilliseconds(700));
                Assert.Equal(1.3, other.ScaleX, 2);
                Assert.Equal(0.2, box.Opacity, 2);

                // the next change interrupts it as a whole; what it leaves settles inside the next one
                var next = new StateMoment("Scanning → Paused");
                using (next.Enter(TimeSpan.Zero))
                {
                    change.Interrupt(TimeSpan.FromMilliseconds(150));
                }

                Assert.True(change.WasInterrupted);
                Assert.True(outside.IsActive); // not a member: untouched
                Assert.Equal(new[] { "test.first.settle", "test.later.settle" }, next.MomentNames);
                WindowScene.PumpUntil(() => !change.IsPlaying, "the settle");
                Assert.Equal(1, other.ScaleX, 3);
                Assert.Equal(1, box.Opacity, 3);
                Assert.False(next.IsPlaying);
                outside.Stop();
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void StopDisallowed_StopsOnlyTheMomentsThatMayNoLongerPlay()
    {
        CampfireTestHost.RunSta(() =>
        {
            var still = new MotionPolicy();
            var live = new MotionPolicy();
            (Window window, Border box, TranslateTransform move) = Host(still);
            (Window window2, Border box2, TranslateTransform move2) = Host(live);
            try
            {
                MotionMoment a = Moment(box, move, 1000);
                MotionMoment b = Moment(box2, move2, 1000);
                a.Begin();
                b.Begin();
                still.AnimationsEnabled = false;
                Assert.Equal(1, MotionMoment.StopDisallowed());
                Assert.True(a.HasEnded);
                Assert.True(b.IsActive);
                b.Stop();
            }
            finally
            {
                window.Close();
                window2.Close();
            }
        });
    }

    [Theory]
    [InlineData(100, 0.956)]
    [InlineData(150, 1.0)]
    [InlineData(250, 0.831)]
    [InlineData(500, 0.016)]
    [InlineData(1100, 0.0)]
    public void Waapi_ReproducesAnEasedIteration_OverSeveralKeyframes(double ms, double expected)
    {
        // the Entity's yank at match end: 0 (ease-in-out) -> 1 at .28 -> 1 at .46 (ease-out) -> 0, 2 s eased out
        var yank = new[] { new WaapiKf(0, 0, Ease.InOut), new WaapiKf(0.28, 1), new WaapiKf(0.46, 1, Ease.Out), new WaapiKf(1, 0) };
        Assert.Equal(expected, Anim.WaapiValue(yank, Ease.Out, ms / 2000), 2);
    }

    [Fact]
    public void Waapi_SamplesTheCurve_AndHoldsItsFirstValueDuringTheDelay()
    {
        var pop = new[] { new WaapiKf(0, 0.4), new WaapiKf(0.6, 1.3), new WaapiKf(1, 1) };
        var anim = Anim.Waapi(320, Ease.Out, pop, delayMs: 300);
        Assert.True(anim.KeyFrames.Count > 18);
        Assert.Equal(0.4, anim.KeyFrames[0].Value, 3);
        Assert.Equal(TimeSpan.Zero, anim.KeyFrames[0].KeyTime.TimeSpan);
        Assert.Equal(TimeSpan.FromMilliseconds(620), anim.KeyFrames[^1].KeyTime.TimeSpan);
        Assert.Equal(1, anim.KeyFrames[^1].Value, 3);
        Assert.Equal(1.3, anim.KeyFrames.Cast<System.Windows.Media.Animation.DoubleKeyFrame>().Max(k => k.Value), 1); // the overshoot is there
    }

    [Fact]
    public void ReducedMotion_KeepsEveryMomentShort_LikeThePrototypesA()
    {
        // a long delayed fade (the sink's clearing: 420 ms after 560 ms) and a keyframe swell (1.3 s after 420 ms)
        var fade = Anim.FromTo(1, 0, 420, Ease.Out, 560);
        Assert.True(Anim.ClampForReducedMotion(fade));
        Assert.Equal(TimeSpan.FromMilliseconds(Anim.ReducedMaxDelayMs + Anim.ReducedMaxMs), fade.KeyFrames[^1].KeyTime.TimeSpan);
        Assert.Equal(TimeSpan.FromMilliseconds(Anim.ReducedMaxDelayMs), fade.KeyFrames[1].KeyTime.TimeSpan); // the held start
        Assert.Equal(1, fade.KeyFrames[0].Value, 3);
        var swell = Anim.Keys(420, new[] { new Kf(0, 0), new Kf(455, 1, Ease.Out), new Kf(1300, 0, Ease.InOut) });
        Assert.True(Anim.ClampForReducedMotion(swell));
        Assert.Equal(160 + 200 * 455.0 / 1300, swell.KeyFrames[2].KeyTime.TimeSpan.TotalMilliseconds, 3); // the peak keeps its place
        Assert.Equal(360, swell.KeyFrames[^1].KeyTime.TimeSpan.TotalMilliseconds, 3);

        // already short: unchanged; a loop: left alone
        var quick = Anim.FromTo(0, 1, 180, Ease.Out, 40);
        Assert.False(Anim.ClampForReducedMotion(quick));
        Assert.False(Anim.ClampForReducedMotion(Anim.Loop(1200, new[] { new Kf(0, 0), new Kf(1200, 1) })));

        CampfireTestHost.RunSta(() =>
        {
            (Window window, Border box, TranslateTransform move) = Host(new MotionPolicy { ReducedMotionOverride = true });
            try
            {
                // a moment begun under reduced motion is clamped as it begins; one that keeps exact timing is not
                var sink = new MotionMoment(box, "test.sink");
                sink.Add(box, UIElement.OpacityProperty, Anim.FromTo(0, 1, 420, Ease.Out, 560));
                var exact = new MotionMoment(box, "test.exact") { ExactTiming = true };
                exact.Add(move, TranslateTransform.YProperty, Anim.FromTo(40, 0, 900, Ease.Linear));
                sink.Begin();
                exact.Begin();
                CampfireTestHost.Wait(TimeSpan.FromMilliseconds(500));
                Assert.True(sink.HasEnded, "a reduced-motion moment still plays after 500 ms");
                Assert.False(exact.HasEnded);
                exact.Stop();
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static MotionMoment Moment(FrameworkElement owner, TranslateTransform move, double ms)
    {
        var moment = new MotionMoment(owner, "test.move");
        moment.Add(move, TranslateTransform.YProperty, Anim.FromTo(40, 0, ms, Ease.Linear));
        moment.Add(owner, UIElement.OpacityProperty, Anim.FromTo(0, 1, ms, Ease.Linear));
        return moment;
    }

    private static (Window Window, Border Box, TranslateTransform Move) Host(MotionPolicy policy)
    {
        var move = new TranslateTransform();
        var box = new Border { Width = 40, Height = 40, Background = Brushes.White, RenderTransform = move };
        var grid = new Grid();
        grid.Children.Add(box);
        Window window = CampfireTestHost.Show(grid, policy, 120, 120);
        return (window, box, move);
    }
}
