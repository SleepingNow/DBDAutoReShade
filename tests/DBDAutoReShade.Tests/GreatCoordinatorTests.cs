using DBDAutoReShade.App.Media;
using DBDAutoReShade.App.ViewModels;

namespace DBDAutoReShade.Tests;

/// <summary>
/// The Great when a map is found (<see cref="GreatCoordinator"/>): the skill check lands only when the main window
/// can show it (visible, not minimized, active, Status shown with the check scanning; with animations off too, as a
/// still flash whose hit is now), and then the sound is scheduled from the landing's computed hit delay; otherwise it
/// plays at once. Exactly once per detection, with the user's Sound setting and volume (through the real controller
/// and its fake sound output: no audio device).
/// </summary>
public sealed class GreatCoordinatorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dbd-great-" + Guid.NewGuid().ToString("N"));
    private readonly List<ControllerHarness> _harnesses = new();

    public GreatCoordinatorTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        foreach (ControllerHarness h in _harnesses)
        {
            h.Dispose();
        }

        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // Best effort.
        }
    }

    [Theory]
    // animations, visible (shown, not minimized), active, Status shown, check scanning → lands
    [InlineData(true, true, true, true, true, true)]
    [InlineData(true, true, false, true, true, false)] // behind the fullscreen game: the player is in the game
    [InlineData(true, false, true, true, true, false)] // minimized or hidden
    [InlineData(false, true, true, true, true, true)] // Settings → Play animations off: a still flash (the stage's hit is now)
    [InlineData(false, true, false, true, true, false)] // animations off, behind the game
    [InlineData(true, true, true, false, true, false)] // Presets or Settings shown: the dial is out of sight
    [InlineData(true, true, true, true, false, false)] // no spinning check on the plate
    [InlineData(false, false, false, false, false, false)]
    public void TheGreat_LandsOnlyWhenTheWindowCanShowIt_ElseTheSoundPlaysAtOnce(
        bool animations, bool visible, bool active, bool statusShown, bool spinning, bool lands)
    {
        var policy = new MotionPolicy { AnimationsEnabled = animations, WindowVisible = visible, WindowActive = active };
        Assert.Equal(lands, GreatCoordinator.CanLand(policy, statusShown, spinning));

        var stage = new FakeStage(() => GreatCoordinator.CanLand(policy, statusShown, spinning), TimeSpan.FromMilliseconds(280));
        var scheduler = new FakeScheduler();
        int played = 0;
        var great = new GreatCoordinator(() => ++played > 0, scheduler) { Stage = stage };

        GreatPlan plan = great.OnMapFound("MacMillan Estate", "Coal Tower");

        if (lands)
        {
            Assert.Equal(new GreatPlan(GreatPresentation.Landed, TimeSpan.FromMilliseconds(280)), plan);
            Assert.Equal(new[] { ("MacMillan Estate", (string?)"Coal Tower") }, stage.Landings);
            Assert.Equal(0, played); // not before the hit
            Assert.Equal(new[] { TimeSpan.FromMilliseconds(280) }, scheduler.Delays); // the computed hit delay
            scheduler.RunAll();
            Assert.Equal(1, played); // at the hit
            scheduler.Scheduled.Single()(); // the same timer firing a second time
            Assert.Equal(1, played); // never again
        }
        else
        {
            Assert.Equal(new GreatPlan(GreatPresentation.Immediate, TimeSpan.Zero), plan);
            Assert.Empty(stage.Landings);
            Assert.Empty(scheduler.Delays);
            Assert.Equal(1, played); // at once, exactly once
        }
    }

    [Theory]
    // Sound setting, volume, landed → plays at that volume
    [InlineData(true, 0.5, true, true)]
    [InlineData(true, 0.5, false, true)]
    [InlineData(true, 1.0, true, true)]
    [InlineData(true, 0.07, false, true)]
    [InlineData(true, 0.0, true, false)] // 0 % is silent
    [InlineData(true, 0.0, false, false)]
    [InlineData(false, 0.5, true, false)] // the Sound setting is off
    [InlineData(false, 0.5, false, false)]
    public void TheGreatSound_FollowsTheSoundSetting_AndTheVolume(bool soundEnabled, double volume, bool landed, bool plays)
    {
        ControllerHarness h = Harness();
        h.Controller.SetSoundEnabled(soundEnabled);
        h.Controller.SetSoundVolume(volume);
        var scheduler = new FakeScheduler();
        var stage = new FakeStage(() => landed, TimeSpan.FromMilliseconds(150));
        var great = new GreatCoordinator(h.Controller.PlayGreatSound, scheduler) { Stage = stage };

        great.OnMapFound("Coldwind Farm", "Fractured Cowshed");
        scheduler.RunAll();

        Assert.Equal(plays ? new[] { volume } : Array.Empty<double>(), h.Sounds.Played.ToArray());
    }

    [Fact]
    public void ALanding_WithNoTimeToTheHit_PlaysAtOnce_AndABrokenStageStillPlays()
    {
        int played = 0;
        var scheduler = new FakeScheduler();
        var great = new GreatCoordinator(() => ++played > 0, scheduler) { Stage = new FakeStage(() => true, TimeSpan.Zero) };
        Assert.Equal(new GreatPlan(GreatPresentation.Landed, TimeSpan.Zero), great.OnMapFound("Ormond", null));
        Assert.Equal(1, played);
        Assert.Empty(scheduler.Delays);

        great.Stage = new FakeStage(() => true, TimeSpan.FromMilliseconds(100)) { ThrowOnLand = true };
        great.OnMapFound("Ormond", "Ormond Lake Mine");
        Assert.Equal(2, played); // the screen failed; the sound did not

        great.Stage = new FakeStage(() => throw new InvalidOperationException("gone"), TimeSpan.Zero);
        Assert.Equal(GreatPresentation.Immediate, great.OnMapFound("Ormond", null).Presentation);
        Assert.Equal(3, played);

        great.Stage = null; // no window (the setup wizard)
        Assert.Equal(GreatPresentation.Immediate, great.OnMapFound("Ormond", null).Presentation);
        Assert.Equal(4, played);
    }

    [Fact]
    public void ASoundThatThrows_NeverBreaksTheCaller()
    {
        var great = new GreatCoordinator(() => throw new InvalidOperationException("no audio"), new FakeScheduler());
        GreatPlan plan = great.OnMapFound("Ormond", null);
        Assert.Equal(GreatPresentation.Immediate, plan.Presentation);
    }

    [Fact]
    public async Task TheViewModel_PlaysTheGreatOncePerDetection_NeverOnStartupOrAScanWithoutARead()
    {
        ControllerHarness h = Harness();
        var vm = new MainViewModel(h.Controller, _ => { });
        vm.Refresh();
        Assert.Empty(h.Sounds.Played); // startup

        await h.Controller.TriggerDetectionAsync(); // Coldwind Farm, read at once
        Assert.Equal(new[] { 0.5 }, h.Sounds.Played.ToArray()); // the first-run volume

        h.Ocr.Text = string.Empty; // nothing readable: the scan is stopped by hand
        Task scan = h.Controller.TriggerDetectionAsync();
        await ControllerHarness.WaitUntilAsync(() => h.Controller.DetectionText.Contains("last read", StringComparison.Ordinal), "a read");
        await h.Controller.TriggerDetectionAsync(); // stop
        await scan.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Single(h.Sounds.Played);

        vm.SoundEnabled = false;
        h.Ocr.Text = "FRACTURED COWSHED\nCOLDWIND FARM";
        await h.Controller.TriggerDetectionAsync();
        Assert.Single(h.Sounds.Played); // the Sound setting is off
        GC.KeepAlive(vm);
    }

    private ControllerHarness Harness()
    {
        ControllerHarness h = ControllerHarness.Create(_dir);
        _harnesses.Add(h);
        return h;
    }

    private sealed class FakeStage(Func<bool> canLand, TimeSpan hit) : IGreatStage
    {
        public List<(string Realm, string? Map)> Landings { get; } = new();

        public bool ThrowOnLand { get; init; }

        public bool CanLand => canLand();

        public TimeSpan Land(string realm, string? map)
        {
            Landings.Add((realm, map));
            if (ThrowOnLand)
            {
                throw new InvalidOperationException("The window went away.");
            }

            return hit;
        }
    }

    private sealed class FakeScheduler : IDelayScheduler
    {
        private readonly List<Action> _pending = new();

        public List<TimeSpan> Delays { get; } = new();

        /// <summary>Every action scheduled so far, kept after it ran, so a test can fire one again (a duplicate tick).</summary>
        public List<Action> Scheduled { get; } = new();

        public void Schedule(TimeSpan delay, Action action)
        {
            Delays.Add(delay);
            _pending.Add(action);
            Scheduled.Add(action);
        }

        /// <summary>Runs every scheduled action (each one only once, like a one-shot timer).</summary>
        public void RunAll()
        {
            List<Action> due = _pending.ToList();
            _pending.Clear();
            foreach (Action action in due)
            {
                action();
            }
        }
    }
}
