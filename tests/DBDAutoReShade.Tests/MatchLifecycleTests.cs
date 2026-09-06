using DBDAutoReShade.Core.Services;
using DBDAutoReShade.Desktop.Services;

namespace DBDAutoReShade.Tests;

public sealed class MatchLifecycleTests
{
    private sealed class ScriptedCapture : IWindowCaptureService
    {
        private byte _level;

        public void SetLevel(byte level) => _level = level;

        private CapturedImage Frame()
        {
            const int w = 64;
            const int h = 64;
            var px = new byte[w * h * 4];
            for (int i = 0; i < px.Length; i += 4)
            {
                px[i] = px[i + 1] = px[i + 2] = _level;
                px[i + 3] = 255;
            }

            return new CapturedImage(w, h, px);
        }

        public CapturedImage? CaptureClientArea(IntPtr hwnd) => Frame();

        public CapturedImage? CaptureRegion(IntPtr hwnd, IntRegion region) => Frame();
    }

    private static MatchLifecycleService Service(ScriptedCapture capture)
    {
        var svc = new MatchLifecycleService(capture, new LifecycleOptions(
            PollIntervalMs: 10_000, // driven manually via PollOnce
            TrialTransitionDistance: 30,
            TrialConfirmPolls: 3,
            MenuTransitionDistance: 35,
            MenuConfirmPolls: 4,
            MinimumTrialSeconds: 0));
        svc.Start(() => new IntPtr(1), () => true);
        return svc;
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
    public void SlowDrift_DoesNotTriggerMenu()
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

        // Gradual lighting drift: reference tracks it, no false menu signal.
        for (byte level = 200; level < 220; level += 2)
        {
            capture.SetLevel(level);
            svc.PollOnce();
        }

        Assert.Equal(LifecyclePhase.InTrial, svc.Phase);
    }
}
