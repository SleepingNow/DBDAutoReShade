using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using DBDAutoReShade.App.Controls;
using DBDAutoReShade.App.Media;

namespace DBDAutoReShade.Tests.Campfire;

/// <summary>
/// The skill check: the prototype's deterministic zone sequence and landing maths (constant 0.3°/ms to 4° into the
/// white block), and the control's contract with the view model: LandGreat returns the hit delay, raises Hit at that
/// moment and Finished after the 0.3 s hold and the lift; ComputeHitDelay predicts it without animating. Every
/// landing raises exactly one Hit (the Great sound) and then one Finished (the map reveal), even when it is cut short.
/// </summary>
public sealed class SkillCheckTests
{
    [Fact]
    public void ZoneSequence_IsTheProtototypes_AndAlwaysLooksNew()
    {
        // the prototype's comment: zones 291, 191, 269, 133 deg: three misses, then the 4th read can land
        Assert.Equal(291, SkillCheckMath.ZoneStart(0), 0);
        Assert.Equal(191, SkillCheckMath.ZoneStart(1), 0);
        Assert.Equal(269, SkillCheckMath.ZoneStart(2), 0);
        Assert.Equal(133, SkillCheckMath.ZoneStart(3), 0);
        double previous = double.NaN;
        for (int k = 0; k < 200; k++)
        {
            double z = SkillCheckMath.ZoneStart(k);
            Assert.InRange(z, 112, 298);
            if (!double.IsNaN(previous))
            {
                Assert.True(Math.Abs(z - previous) >= 43, $"zone {k} at {z:0} too close to {previous:0}"); // "a new check should look new"
            }

            previous = z;
        }
    }

    [Fact]
    public void Landing_RunsOnAt03DegPerMs_To4DegIntoTheWhite()
    {
        SkillCheckLanding ahead = SkillCheckMath.ComputeLanding(angle: 100, zoneStart: 160);
        Assert.Equal(64, ahead.Lead, 6);
        Assert.Equal(164, ahead.TargetAngle, 6);
        Assert.Equal(64 / 0.3, ahead.HitDelay.TotalMilliseconds, 3);
        Assert.False(ahead.Fresh);

        SkillCheckLanding inside = SkillCheckMath.ComputeLanding(angle: 165, zoneStart: 160);
        Assert.Equal(TimeSpan.Zero, inside.HitDelay);
        Assert.Equal(165, inside.TargetAngle, 6);

        SkillCheckLanding behind = SkillCheckMath.ComputeLanding(angle: 250, zoneStart: 160);
        Assert.True(behind.Fresh);
        Assert.Equal(SkillCheckMath.Reach, behind.Lead, 6);
        Assert.Equal(250 + 92, behind.TargetAngle, 6);
        Assert.Equal(250 + 92 - 4, behind.ZoneStart, 6);
        Assert.Equal(306.67, behind.HitDelay.TotalMilliseconds, 1);

        SkillCheckLanding tooFar = SkillCheckMath.ComputeLanding(angle: 10, zoneStart: 200);
        Assert.True(tooFar.Fresh);

        SkillCheckLanding still = SkillCheckMath.ComputeLanding(angle: 10, zoneStart: 200, still: true);
        Assert.Equal(TimeSpan.Zero, still.HitDelay);
        Assert.Equal(204, still.TargetAngle, 6);
    }

    [Theory]
    [InlineData("F8", null, "F8")]
    [InlineData("Ctrl+Shift+F9", "Ctrl+Shift", "F9")]
    [InlineData("Ctrl+Alt+Shift+NumPad5", "Ctrl+Alt+Shift", "Num 5")]
    [InlineData("Alt+7", "Alt", "7")]
    public void CentreKey_PutsModifiersOnTheirOwnLine(string hotkey, string? mods, string key)
    {
        (string? m, string k) = SkillCheckMath.SplitHotkey(hotkey);
        Assert.Equal(mods, m);
        Assert.Equal(key, k);
    }

    [Fact]
    public void SectorPaths_MatchThePrototype()
    {
        // skSector(0, 12): the great block at 12 o'clock
        Assert.Equal("M100,32.8 A67.2,67.2 0 0 1 113.97,34.27 L112.37,41.8 A59.5,59.5 0 0 0 100,40.5 Z", SkillCheckMath.SectorPath(0, 12));
    }

    [Fact]
    public void LandGreat_WithoutMotion_HitsAtOnce_AndFinishes()
    {
        CampfireTestHost.RunSta(() =>
        {
            var check = new SkillCheck { Width = 640, Height = 320 };
            MotionPolicy.SetPolicy(check, new MotionPolicy { AnimationsEnabled = false });
            CampfireTestHost.Layout(check, 640, 320);
            check.State = SkillCheckState.Scanning;
            int hits = 0, finished = 0;
            check.Hit += (_, _) => hits++;
            check.Finished += (_, _) => finished++;
            Assert.Equal(TimeSpan.Zero, check.ComputeHitDelay());
            Assert.Equal(TimeSpan.Zero, check.LandGreat("Map found: Coal Tower"));
            CampfireTestHost.Wait(TimeSpan.FromMilliseconds(700));
            Assert.Equal(1, hits);
            Assert.Equal(1, finished);
            Assert.Equal(SkillCheckState.Hidden, check.State);
            Assert.Equal("Map found: Coal Tower", check.Score.Detail);
            Assert.Empty(MotionDiagnostics.FindAnimatedObjects(check));
        });
    }

    [Fact]
    public void LandGreat_ReturnsTheDelay_ItPredicted_AndHitsOnTime()
    {
        CampfireTestHost.RunSta(() =>
        {
            var policy = new MotionPolicy();

            // a very slow revolution, so the needle stays where the test puts it
            var period = TimeSpan.FromHours(1);
            var check = new SkillCheck { RevolutionDuration = period };
            var window = CampfireTestHost.Show(check, policy, 640, 320);
            try
            {
                check.State = SkillCheckState.Scanning;
                CampfireTestHost.Wait(TimeSpan.FromMilliseconds(100));
                Assert.True(check.IsSpinning);

                // the needle 30° before this read's great block
                double angle = SkillCheckMath.ZoneStart(0) - 30;
                check.SeekSpin(TimeSpan.FromMilliseconds(angle / 360 * period.TotalMilliseconds));
                Assert.True(check.IsSpinning);
                Assert.Equal(angle, check.NeedleAngle, 1);
                TimeSpan predicted = check.ComputeHitDelay();
                Assert.Equal(34 / 0.3, predicted.TotalMilliseconds, 0);

                // behind the game the landing is not seen: no delay (the sound plays at once)
                policy.WindowActive = false;
                Assert.Equal(TimeSpan.Zero, check.ComputeHitDelay());
                policy.WindowActive = true;
                CampfireTestHost.Pump();

                var clock = Stopwatch.StartNew();
                double hitAt = -1, finishedAt = -1;
                check.Hit += (_, _) => hitAt = clock.Elapsed.TotalMilliseconds;
                check.Finished += (_, _) => finishedAt = clock.Elapsed.TotalMilliseconds;
                TimeSpan h = check.LandGreat("Map found: Coal Tower");
                Assert.Equal(predicted.TotalMilliseconds, h.TotalMilliseconds, 0);
                Assert.True(check.IsLanding);
                check.State = SkillCheckState.Hidden; // the view model moved on: the landing still owns the dial
                Assert.True(check.IsLanding);
                CampfireTestHost.Wait(TimeSpan.FromMilliseconds(h.TotalMilliseconds + 1000));
                Assert.InRange(hitAt, h.TotalMilliseconds - 5, h.TotalMilliseconds + 250);
                Assert.InRange(finishedAt, h.TotalMilliseconds + 500, h.TotalMilliseconds + 950);
                Assert.False(check.IsLanding);
                Assert.Equal(SkillCheckState.Hidden, check.State);
                Assert.Equal(SkillCheckMath.ZoneStart(0) + 4, check.NeedleAngle, 1); // stopped dead 4° into the white
                Assert.True(check.Score.IsShowing); // the score event stays beside the revealed map
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Theory]
    [InlineData(true, SkillCheckState.Scanning)] // the view model moved on (Hidden, deferred), then F8 again right after the read
    [InlineData(true, SkillCheckState.Paused)] // moved on, then the scan pauses
    [InlineData(false, SkillCheckState.Paused)] // the scan pauses straight from the read
    public void ALandingCutShort_BeforeItsHit_StillRaisesHitThenFinished_Once(bool movedOn, SkillCheckState next)
    {
        CampfireTestHost.RunSta(() =>
        {
            (SkillCheck check, Window window) = ScanningCheck(new MotionPolicy());
            try
            {
                var events = new List<string>();
                check.Hit += (_, _) => events.Add("hit");
                check.Finished += (_, _) => events.Add("finished:" + check.State);
                TimeSpan h = check.LandGreat("Map found: Coal Tower");
                Assert.True(h.TotalMilliseconds > 150, $"h = {h.TotalMilliseconds}");
                CampfireTestHost.Wait(TimeSpan.FromMilliseconds(10));
                Assert.Empty(events);
                if (movedOn)
                {
                    check.State = SkillCheckState.Hidden; // the landing owns the dial: nothing is cut yet
                    Assert.True(check.IsLanding);
                    Assert.Empty(events);
                }

                check.State = next;

                // the outcome is told at once, after the new state is applied
                Assert.Equal(new[] { "hit", "finished:" + next }, events);
                Assert.False(check.IsLanding);
                Assert.Equal(next, check.State);

                // ...and never again
                CampfireTestHost.Wait(TimeSpan.FromMilliseconds(h.TotalMilliseconds + 800));
                Assert.Equal(new[] { "hit", "finished:" + next }, events);
                Assert.Equal(next, check.State);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void ALandingCutShort_AfterItsHit_RaisesOnlyFinished()
    {
        CampfireTestHost.RunSta(() =>
        {
            (SkillCheck check, Window window) = ScanningCheck(new MotionPolicy());
            try
            {
                var events = new List<string>();
                check.Hit += (_, _) => events.Add("hit");
                check.Finished += (_, _) => events.Add("finished");
                TimeSpan h = check.LandGreat("Map found: Coal Tower");
                CampfireTestHost.Wait(TimeSpan.FromMilliseconds(h.TotalMilliseconds + 120)); // the hit; the dial holds
                Assert.Equal(new[] { "hit" }, events);
                Assert.True(check.IsLanding);
                check.State = SkillCheckState.Paused;
                Assert.Equal(new[] { "hit", "finished" }, events);
                CampfireTestHost.Wait(TimeSpan.FromMilliseconds(800));
                Assert.Equal(new[] { "hit", "finished" }, events);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void ASecondLanding_CompletesTheFirst_ThenPlaysItsOwn()
    {
        CampfireTestHost.RunSta(() =>
        {
            (SkillCheck check, Window window) = ScanningCheck(new MotionPolicy());
            try
            {
                var events = new List<string>();
                check.Hit += (_, _) => events.Add("hit");
                check.Finished += (_, _) => events.Add("finished");
                TimeSpan first = check.LandGreat("Map found: Coal Tower");
                Assert.True(first > TimeSpan.Zero);
                TimeSpan second = check.LandGreat("Map found: Shelter Woods");
                Assert.Equal(new[] { "hit", "finished" }, events); // the first one, told at once
                Assert.Equal(TimeSpan.Zero, second); // the needle is already in the white
                Assert.True(check.IsLanding);
                CampfireTestHost.Wait(TimeSpan.FromMilliseconds(900));
                Assert.Equal(new[] { "hit", "finished", "hit", "finished" }, events);
                Assert.False(check.IsLanding);
                Assert.Equal(SkillCheckState.Hidden, check.State);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void TheLandingHidesTheCheck_WithoutReplacingABindingOnState()
    {
        CampfireTestHost.RunSta(() =>
        {
            (SkillCheck check, Window window) = ScanningCheck(new MotionPolicy());
            try
            {
                var source = new StateSource { State = SkillCheckState.Scanning };
                System.Windows.Data.BindingOperations.SetBinding(check, SkillCheck.StateProperty, new System.Windows.Data.Binding(nameof(StateSource.State)) { Source = source });
                TimeSpan h = check.LandGreat("Map found: Coal Tower");
                CampfireTestHost.Wait(TimeSpan.FromMilliseconds(h.TotalMilliseconds + 800));
                Assert.Equal(SkillCheckState.Hidden, check.State);

                // the view model still drives the check afterwards
                source.State = SkillCheckState.Paused;
                Assert.Equal(SkillCheckState.Paused, check.State);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void ALandingCutShort_WithAnimationsOff_StillRaisesEachEventOnce()
    {
        CampfireTestHost.RunSta(() =>
        {
            (SkillCheck check, Window window) = ScanningCheck(new MotionPolicy { AnimationsEnabled = false });
            try
            {
                int hits = 0, finished = 0;
                check.Hit += (_, _) => hits++;
                check.Finished += (_, _) => finished++;
                Assert.Equal(TimeSpan.Zero, check.LandGreat("Map found: Coal Tower"));
                check.State = SkillCheckState.Paused; // before the queued hit has run
                Assert.Equal(1, hits);
                Assert.Equal(1, finished);
                CampfireTestHost.Wait(TimeSpan.FromMilliseconds(800));
                Assert.Equal(1, hits);
                Assert.Equal(1, finished);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void Paused_FreezesTheNeedle_AndGreysTheCheck()
    {
        CampfireTestHost.RunSta(() =>
        {
            var policy = new MotionPolicy();
            var check = new SkillCheck();
            var window = CampfireTestHost.Show(check, policy, 640, 320);
            try
            {
                check.State = SkillCheckState.Scanning;
                CampfireTestHost.Wait(TimeSpan.FromMilliseconds(300));
                Assert.True(check.IsSpinning);
                check.State = SkillCheckState.Paused;
                double angle = check.NeedleAngle;
                CampfireTestHost.Wait(TimeSpan.FromMilliseconds(300));
                Assert.False(check.IsSpinning);
                Assert.Equal(angle, check.NeedleAngle, 1);
                check.State = SkillCheckState.Scanning;
                CampfireTestHost.Wait(TimeSpan.FromMilliseconds(250));
                Assert.True(check.IsSpinning);
                Assert.NotEqual(angle, check.NeedleAngle, 1);
                check.State = SkillCheckState.Hidden;
                CampfireTestHost.Wait(TimeSpan.FromMilliseconds(250));
                Assert.Empty(MotionDiagnostics.FindAnimatedObjects(check));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void Scanning_RaisesOneRevolutionPerRead()
    {
        CampfireTestHost.RunSta(() =>
        {
            var check = new SkillCheck { RevolutionDuration = TimeSpan.FromMilliseconds(300) };
            var window = CampfireTestHost.Show(check, new MotionPolicy(), 640, 320);
            try
            {
                var revolutions = new List<int>();
                check.Revolution += (_, r) => revolutions.Add(r);
                check.State = SkillCheckState.Scanning;
                CampfireTestHost.Wait(TimeSpan.FromMilliseconds(1050));
                Assert.InRange(revolutions.Count, 2, 4);
                Assert.Equal(Enumerable.Range(1, revolutions.Count), revolutions);
                Assert.Equal(SkillCheckMath.ZoneStart(revolutions[^1]), check.ZoneStart, 3);
            }
            finally
            {
                window.Close();
            }
        });
    }

    private sealed class StateSource : System.ComponentModel.INotifyPropertyChanged
    {
        private SkillCheckState _state;

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

        public SkillCheckState State
        {
            get => _state;
            set
            {
                _state = value;
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(State)));
            }
        }
    }

    [Fact]
    public void TheDialEntersAndLeaves_FromWhereItIs()
    {
        CampfireTestHost.RunSta(() =>
        {
            var check = new SkillCheck();
            Window window = CampfireTestHost.Show(check, new MotionPolicy(), 640, 320);
            try
            {
                // F8, then F8 again at once: the half-entered dial fades out from where it is (no pop to full first)
                check.State = SkillCheckState.Scanning;
                CampfireTestHost.Pump();
                check.FreezeAt(TimeSpan.FromMilliseconds(60));
                double entering = check.DialOpacity;
                Assert.InRange(entering, 0.1, 0.95);
                check.State = SkillCheckState.Hidden;
                CampfireTestHost.Pump();
                check.FreezeAt(TimeSpan.Zero);
                Assert.Equal(entering, check.DialOpacity, 2);

                // and F8 once more while it fades out: it comes back from that opacity (no drop to nothing first)
                check.FreezeAt(TimeSpan.FromMilliseconds(40));
                double leaving = check.DialOpacity;
                Assert.InRange(leaving, 0.05, entering);
                check.State = SkillCheckState.Scanning;
                CampfireTestHost.Pump();
                check.FreezeAt(TimeSpan.Zero);
                Assert.Equal(leaving, check.DialOpacity, 2);
                check.FreezeAt(TimeSpan.FromMilliseconds(200));
                Assert.Equal(1, check.DialOpacity, 2);
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>A shown, scanning check whose needle stands 60 degrees before the read's great block (a ~213 ms landing).</summary>
    private static (SkillCheck Check, Window Window) ScanningCheck(MotionPolicy policy)
    {
        var period = TimeSpan.FromHours(1);
        var check = new SkillCheck { RevolutionDuration = period };
        Window window = CampfireTestHost.Show(check, policy, 640, 320);
        check.State = SkillCheckState.Scanning;
        CampfireTestHost.Wait(TimeSpan.FromMilliseconds(100));
        double angle = SkillCheckMath.ZoneStart(0) - 60;
        check.SeekSpin(TimeSpan.FromMilliseconds(angle / 360 * period.TotalMilliseconds));
        return (check, window);
    }
}
