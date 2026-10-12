using DBDAutoReShade.Core.Services;
using DBDAutoReShade.Desktop.Services;

namespace DBDAutoReShade.Tests;

public sealed class MatchLifecycleTests
{
    private sealed class ScriptedCapture : IWindowCaptureService
    {
        private volatile int _level;
        private int _calls;

        public void SetLevel(byte level) => _level = level;

        public int Calls => Volatile.Read(ref _calls);

        /// <summary>Runs inside every capture (lock probes, mid-poll commands).</summary>
        public Action? OnCapture { get; set; }

        private CapturedImage Frame()
        {
            const int w = 64;
            const int h = 64;
            var px = new byte[w * h * 4];
            for (int i = 0; i < px.Length; i += 4)
            {
                px[i] = px[i + 1] = px[i + 2] = (byte)_level;
                px[i + 3] = 255;
            }

            return new CapturedImage(w, h, px);
        }

        public CapturedImage? CaptureClientArea(IntPtr hwnd)
        {
            Interlocked.Increment(ref _calls);
            OnCapture?.Invoke();
            return Frame();
        }

        public CapturedImage? CaptureRegion(IntPtr hwnd, IntRegion region) => Frame();
    }

    private sealed class ManualClock
    {
        private long _ms = 5_000_000;

        public long Now() => Interlocked.Read(ref _ms);

        public void Advance(TimeSpan by) => Interlocked.Add(ref _ms, (long)by.TotalMilliseconds);
    }

    private static MatchLifecycleService Service(
        ScriptedCapture capture,
        int minimumTrialSeconds = 0,
        ManualClock? clock = null,
        Func<bool>? running = null,
        Func<bool>? foreground = null)
    {
        clock ??= new ManualClock();
        var svc = new MatchLifecycleService(capture, new LifecycleOptions(
            PollIntervalMs: 10_000, // driven manually via PollOnce
            TrialTransitionDistance: 30,
            TrialConfirmPolls: 3,
            MenuTransitionDistance: 35,
            MenuConfirmPolls: 4,
            MinimumTrialSeconds: minimumTrialSeconds), clock.Now);
        if (foreground is null)
        {
            svc.Start(() => new IntPtr(1), running ?? (() => true));
        }
        else
        {
            svc.Start(() => new IntPtr(1), running ?? (() => true), hwnd => hwnd == new IntPtr(1) && foreground());
        }

        return svc;
    }

    /// <summary>Arm on a loading screen (level 10) and observe gameplay at <paramref name="gameplayLevel"/>.</summary>
    private static void EnterTrial(MatchLifecycleService svc, ScriptedCapture capture, byte gameplayLevel = 200)
    {
        capture.SetLevel(10);
        svc.ArmForTrial();
        capture.SetLevel(gameplayLevel);
        for (int i = 0; i < 3; i++)
        {
            svc.PollOnce();
        }

        Assert.Equal(LifecyclePhase.InTrial, svc.Phase);
    }

    [Fact]
    public void LoadingToTrial_TransitionOnSustainedChange()
    {
        var capture = new ScriptedCapture();
        using var svc = Service(capture);
        var phases = new List<LifecyclePhase>();
        svc.PhaseChanged += (_, p) => phases.Add(p);

        capture.SetLevel(10); // dark loading screen
        svc.ArmForTrial();
        Assert.Equal(LifecyclePhase.WatchingForTrial, svc.Phase);

        svc.PollOnce(); // same scene: no transition
        Assert.Equal(LifecyclePhase.WatchingForTrial, svc.Phase);

        capture.SetLevel(200); // abrupt change: trial begins
        svc.PollOnce();
        svc.PollOnce();
        Assert.Equal(LifecyclePhase.WatchingForTrial, svc.Phase); // debounced
        svc.PollOnce();
        Assert.Equal(LifecyclePhase.InTrial, svc.Phase);
        Assert.Contains(LifecyclePhase.InTrial, phases);
    }

    [Fact]
    public void TrialToMenu_TransitionAfterSustainedChange()
    {
        var capture = new ScriptedCapture();
        using var svc = Service(capture);

        capture.SetLevel(10);
        svc.ArmForTrial();
        capture.SetLevel(200);
        for (int i = 0; i < 3; i++)
        {
            svc.PollOnce();
        }

        Assert.Equal(LifecyclePhase.InTrial, svc.Phase);

        capture.SetLevel(40); // abrupt change: back to menus
        for (int i = 0; i < 3; i++)
        {
            svc.PollOnce();
        }

        Assert.Equal(LifecyclePhase.InTrial, svc.Phase); // still debouncing
        svc.PollOnce();
        Assert.Equal(LifecyclePhase.BackToMenus, svc.Phase);
    }

    [Fact]
    public void SlowDrift_BeyondTheMenuThreshold_DoesNotTriggerMenu()
    {
        var capture = new ScriptedCapture();
        using var svc = Service(capture);
        EnterTrial(svc, capture, gameplayLevel: 100);

        // Each step (8 levels) is far below the 56-level menu distance, but the
        // total drift (100 → 220 = 120 levels) is more than twice it: only a
        // reference that tracks the drift keeps this InTrial.
        for (int level = 108; level <= 220; level += 8)
        {
            capture.SetLevel((byte)level);
            svc.PollOnce();
            Assert.Equal(LifecyclePhase.InTrial, svc.Phase);
        }
    }

    [Fact]
    public void CameraMotion_AlternatingFrames_NeverTriggersMenu()
    {
        var capture = new ScriptedCapture();
        using var svc = Service(capture);
        EnterTrial(svc, capture, gameplayLevel: 100);

        // Every poll differs from the previous one by 40 (>= 35, the old
        // frame-to-frame trigger) but stays near the reference.
        for (int i = 0; i < 20; i++)
        {
            capture.SetLevel(i % 2 == 0 ? (byte)140 : (byte)100);
            svc.PollOnce();
        }

        Assert.Equal(LifecyclePhase.InTrial, svc.Phase);
    }

    [Fact]
    public void SustainedMenuChange_TriggersOnlyAfterMinimumTrialTime()
    {
        var capture = new ScriptedCapture();
        var clock = new ManualClock();
        using var svc = Service(capture, minimumTrialSeconds: 45, clock: clock);

        capture.SetLevel(10);
        svc.ArmForTrial();
        clock.Advance(TimeSpan.FromSeconds(60)); // a long loading screen must not count toward the trial
        EnterTrial(svc, capture);

        clock.Advance(TimeSpan.FromSeconds(30));
        capture.SetLevel(40); // menus
        for (int i = 0; i < 6; i++)
        {
            svc.PollOnce();
        }

        Assert.Equal(LifecyclePhase.InTrial, svc.Phase); // only 30 s of trial so far

        clock.Advance(TimeSpan.FromSeconds(15));
        svc.PollOnce();
        Assert.Equal(LifecyclePhase.BackToMenus, svc.Phase);
    }

    [Fact]
    public void ActivationWhileWatching_KeepsLoadingReference_GameplayBecomesReference()
    {
        var capture = new ScriptedCapture();
        using var svc = Service(capture);

        capture.SetLevel(10);
        svc.ArmForTrial();
        svc.EnsureArmed(); // the preset switched on the loading screen
        Assert.Equal(LifecyclePhase.WatchingForTrial, svc.Phase);

        capture.SetLevel(200); // gameplay
        for (int i = 0; i < 3; i++)
        {
            svc.PollOnce();
        }

        Assert.Equal(LifecyclePhase.InTrial, svc.Phase);

        // The gameplay frame is the reference: more gameplay is not "back to menus".
        for (int i = 0; i < 10; i++)
        {
            svc.PollOnce();
        }

        Assert.Equal(LifecyclePhase.InTrial, svc.Phase);
    }

    [Fact]
    public void EnsureArmed_ArmsOnlyWhenIdle()
    {
        var capture = new ScriptedCapture();
        using var svc = Service(capture);

        capture.SetLevel(10);
        svc.EnsureArmed();
        Assert.Equal(LifecyclePhase.WatchingForTrial, svc.Phase);

        EnterTrial(svc, capture);
        int captures = capture.Calls;
        svc.EnsureArmed();
        Assert.Equal(LifecyclePhase.InTrial, svc.Phase); // left alone
        Assert.Equal(captures, capture.Calls); // and nothing captured
    }

    [Fact]
    public void Commands_DoNotRaisePhaseChanged()
    {
        var capture = new ScriptedCapture();
        using var svc = Service(capture);
        var phases = new List<LifecyclePhase>();
        svc.PhaseChanged += (_, p) => phases.Add(p);

        svc.ArmForTrial();
        svc.Disarm();
        svc.EnsureArmed();
        svc.Disarm();

        Assert.Empty(phases);
    }

    [Fact]
    public void IdlePolls_DoNotCapture()
    {
        var capture = new ScriptedCapture();
        using var svc = Service(capture);

        for (int i = 0; i < 5; i++)
        {
            svc.PollOnce(); // Unknown
        }

        Assert.Equal(0, capture.Calls);

        EnterTrial(svc, capture);
        capture.SetLevel(40);
        for (int i = 0; i < 4; i++)
        {
            svc.PollOnce();
        }

        Assert.Equal(LifecyclePhase.BackToMenus, svc.Phase);
        int captures = capture.Calls;
        for (int i = 0; i < 5; i++)
        {
            svc.PollOnce(); // BackToMenus
        }

        svc.Disarm();
        for (int i = 0; i < 5; i++)
        {
            svc.PollOnce(); // Unknown again
        }

        Assert.Equal(captures, capture.Calls);
    }

    [Fact]
    public void DbdStopping_ClearsTracking()
    {
        var capture = new ScriptedCapture();
        bool running = true;
        using var svc = Service(capture, running: () => running);
        EnterTrial(svc, capture);

        running = false;
        svc.PollOnce();
        Assert.Equal(LifecyclePhase.Unknown, svc.Phase);

        running = true;
        int captures = capture.Calls;
        svc.PollOnce();
        Assert.Equal(LifecyclePhase.Unknown, svc.Phase); // nothing tracked until armed again
        Assert.Equal(captures, capture.Calls);
    }

    [Fact]
    public void HiddenFrames_NeverStartTheTrial_EvaluationResumesWhenVisible()
    {
        var capture = new ScriptedCapture();
        bool visible = true;
        using var svc = Service(capture, foreground: () => visible);
        capture.SetLevel(10);
        svc.ArmForTrial(); // loading screen

        // Alt-tabbed: a browser covers DBD's area (borderless windowed).
        visible = false;
        capture.SetLevel(200);
        int captures = capture.Calls;
        for (int i = 0; i < 6; i++)
        {
            svc.PollOnce();
        }

        Assert.Equal(LifecyclePhase.WatchingForTrial, svc.Phase);
        Assert.Equal(captures, capture.Calls); // nothing captured while hidden

        visible = true;
        capture.SetLevel(10); // still the loading screen
        svc.PollOnce();
        Assert.Equal(LifecyclePhase.WatchingForTrial, svc.Phase);

        capture.SetLevel(200); // gameplay, seen in DBD itself
        for (int i = 0; i < 3; i++)
        {
            svc.PollOnce();
        }

        Assert.Equal(LifecyclePhase.InTrial, svc.Phase);
    }

    [Fact]
    public void HiddenFrames_NeverTriggerMenu()
    {
        var capture = new ScriptedCapture();
        bool visible = true;
        using var svc = Service(capture, foreground: () => visible);
        EnterTrial(svc, capture, gameplayLevel: 100);

        visible = false; // e.g. a full-screen window over DBD for a while
        capture.SetLevel(250);
        for (int i = 0; i < 10; i++)
        {
            svc.PollOnce();
        }

        Assert.Equal(LifecyclePhase.InTrial, svc.Phase);

        visible = true;
        capture.SetLevel(100); // back to the match: still the reference scene
        for (int i = 0; i < 6; i++)
        {
            svc.PollOnce();
        }

        Assert.Equal(LifecyclePhase.InTrial, svc.Phase);

        capture.SetLevel(30); // the real return to menus
        for (int i = 0; i < 4; i++)
        {
            svc.PollOnce();
        }

        Assert.Equal(LifecyclePhase.BackToMenus, svc.Phase);
    }

    [Fact]
    public void HiddenPoll_RestartsTheStreak()
    {
        var capture = new ScriptedCapture();
        bool visible = true;
        using var svc = Service(capture, foreground: () => visible);
        EnterTrial(svc, capture, gameplayLevel: 100);

        capture.SetLevel(30); // menus
        for (int i = 0; i < 3; i++)
        {
            svc.PollOnce();
        }

        visible = false;
        svc.PollOnce(); // a gap: the streak must consist of consecutive frames of DBD
        visible = true;
        for (int i = 0; i < 3; i++)
        {
            svc.PollOnce();
        }

        Assert.Equal(LifecyclePhase.InTrial, svc.Phase);
        svc.PollOnce();
        Assert.Equal(LifecyclePhase.BackToMenus, svc.Phase);
    }

    [Fact]
    public void ArmWhileHidden_FirstVisibleFrameBecomesTheReference()
    {
        var capture = new ScriptedCapture();
        bool visible = false;
        using var svc = Service(capture, foreground: () => visible);
        capture.SetLevel(200); // a browser in front of DBD
        svc.ArmForTrial();
        Assert.Equal(LifecyclePhase.WatchingForTrial, svc.Phase);
        Assert.Equal(0, capture.Calls);

        visible = true;
        capture.SetLevel(10); // the loading screen: becomes the reference, no "change" from the browser
        for (int i = 0; i < 4; i++)
        {
            svc.PollOnce();
        }

        Assert.Equal(LifecyclePhase.WatchingForTrial, svc.Phase);

        capture.SetLevel(200); // gameplay
        for (int i = 0; i < 3; i++)
        {
            svc.PollOnce();
        }

        Assert.Equal(LifecyclePhase.InTrial, svc.Phase);
    }

    [Fact]
    public void PhaseChanged_IsRaisedOutsideTheLock()
    {
        var capture = new ScriptedCapture();
        using var svc = Service(capture);
        bool disarmedFromOtherThread = false;
        svc.PhaseChanged += (_, p) =>
        {
            if (p == LifecyclePhase.InTrial)
            {
                // A handler that needs another thread to call into the monitor
                // (the controller does exactly this) must not deadlock.
                disarmedFromOtherThread = Task.Run(svc.Disarm).Wait(TimeSpan.FromSeconds(5));
            }
        };

        EnterTrialWithoutAssert(svc, capture);
        Assert.True(disarmedFromOtherThread);
        Assert.Equal(LifecyclePhase.Unknown, svc.Phase);
    }

    [Fact]
    public void Capture_RunsOutsideTheLock()
    {
        var capture = new ScriptedCapture();
        using var svc = Service(capture);
        int probes = 0;
        capture.OnCapture = () =>
        {
            Assert.True(Task.Run(() => svc.Phase).Wait(TimeSpan.FromSeconds(5)), "lock held during capture");
            probes++;
        };

        svc.ArmForTrial(); // capture #1
        svc.PollOnce(); // capture #2
        svc.Disarm();
        svc.EnsureArmed(); // capture #3
        capture.OnCapture = null;

        Assert.Equal(3, probes);
    }

    [Fact]
    public void InFlightPoll_AfterStop_DoesNotRaise()
    {
        var capture = new ScriptedCapture();
        var svc = Service(capture);
        var phases = new List<LifecyclePhase>();
        svc.PhaseChanged += (_, p) => phases.Add(p);
        capture.SetLevel(10);
        svc.ArmForTrial();
        capture.SetLevel(200);
        svc.PollOnce();
        svc.PollOnce();

        capture.OnCapture = svc.Stop; // stopped while the third (transition) poll captures
        svc.PollOnce();

        Assert.Empty(phases);
        Assert.NotEqual(LifecyclePhase.InTrial, svc.Phase);
        svc.Dispose();
    }

    [Fact]
    public void InFlightPoll_AfterDispose_DoesNotRaise()
    {
        var capture = new ScriptedCapture();
        var svc = Service(capture);
        var phases = new List<LifecyclePhase>();
        svc.PhaseChanged += (_, p) => phases.Add(p);
        EnterTrial(svc, capture);
        phases.Clear();
        capture.SetLevel(40);
        for (int i = 0; i < 3; i++)
        {
            svc.PollOnce();
        }

        capture.OnCapture = svc.Dispose; // disposed while the BackToMenus poll captures
        svc.PollOnce();

        Assert.Empty(phases);
    }

    [Fact]
    public void InFlightPoll_AfterRearm_DropsItsStaleFrame()
    {
        var capture = new ScriptedCapture();
        using var svc = Service(capture);
        var phases = new List<LifecyclePhase>();
        svc.PhaseChanged += (_, p) => phases.Add(p);
        EnterTrial(svc, capture);
        phases.Clear();
        capture.SetLevel(40);
        for (int i = 0; i < 3; i++)
        {
            svc.PollOnce();
        }

        // A new match is armed while the menu-confirming poll captures.
        capture.OnCapture = () =>
        {
            capture.OnCapture = null;
            svc.ArmForTrial();
        };
        svc.PollOnce();

        Assert.Empty(phases);
        Assert.Equal(LifecyclePhase.WatchingForTrial, svc.Phase);
    }

    [Fact]
    public async Task Stop_WaitsForARunningHandler()
    {
        var capture = new ScriptedCapture();
        var svc = Service(capture);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        svc.PhaseChanged += (_, _) =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(10));
        };

        Task poll = Task.Run(() => EnterTrialWithoutAssert(svc, capture));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));

        Task stop = Task.Run(svc.Stop);
        Assert.NotSame(stop, await Task.WhenAny(stop, Task.Delay(200))); // the handler is still running
        release.Set();
        await stop.WaitAsync(TimeSpan.FromSeconds(5));
        await poll.WaitAsync(TimeSpan.FromSeconds(5));
        svc.Dispose();
    }

    private static void EnterTrialWithoutAssert(MatchLifecycleService svc, ScriptedCapture capture)
    {
        capture.SetLevel(10);
        svc.ArmForTrial();
        capture.SetLevel(200);
        for (int i = 0; i < 3; i++)
        {
            svc.PollOnce();
        }
    }
}
