using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using DBDAutoReShade.App;
using DBDAutoReShade.App.Controls;
using DBDAutoReShade.App.Media;
using DBDAutoReShade.App.ViewModels;
using DBDAutoReShade.Core.Models;

namespace DBDAutoReShade.Tests.Campfire;

/// <summary>
/// The REAL main window over the real controller (<see cref="WindowScene"/>, off screen, never activated):
/// the Great lands on the Status screen and its sound waits for the hit (scheduled from the computed delay), or plays
/// at once behind the game; with animations off it still lands as a still flash with the score event, and the sound
/// plays at once; the screen holds the scan until the dial has lifted, and keeps updating after a landing that failed;
/// nothing animates while the window is inactive or animations are off; the title bar buttons are real, named buttons.
/// </summary>
public sealed class MainWindowTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dbd-window-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // Best effort.
        }
    }

    [Fact]
    public void MapFound_InFront_TheSkillCheckLands_AndTheSoundPlaysAtTheHit_Once()
    {
        CampfireTestHost.RunSta(() =>
        {
            WindowScene scene = WindowScene.Create(Path.Combine(_dir, "land"), "scanning");
            try
            {
                var recorder = new RecordingScheduler(scene.ViewModel.Great.Scheduler!, hold: true);
                scene.ViewModel.Great.Scheduler = recorder;
                scene.Policy.WindowActive = true; // the player is looking at the app
                SkillCheck skill = Status(scene).MapPlate.SkillCheck;
                WindowScene.PumpUntil(() => skill.State == SkillCheckState.Scanning, "the spinning check");
                CampfireTestHost.Wait(TimeSpan.FromMilliseconds(300)); // the dial has entered

                // the needle is where it is when the map is read: put it 80.4 degrees (268 ms at 0.3 deg/ms) short of 4
                // degrees into the second check's white block, so the window's own landing has a known run to the hit
                const int rev = 1;
                double angle = ((SkillCheckMath.ZoneStart(rev) + SkillCheckMath.HitDepth - 80.4) % 360 + 360) % 360;
                var stage = new SeekingStage(scene.Window, skill, TimeSpan.FromMilliseconds(skill.RevolutionDuration.TotalMilliseconds * (rev + angle / 360)));
                scene.ViewModel.Great.Stage = stage;

                scene.Harness.Ocr.Text = "COAL TOWER\nMACMILLAN ESTATE"; // the next read finds the map
                try
                {
                    WindowScene.PumpUntil(() => scene.Window.IsLanding, "the landing");
                }
                catch (TimeoutException)
                {
                    var skillNow = Status(scene).MapPlate.SkillCheck;
                    throw new Xunit.Sdk.XunitException($"no landing: sounds={scene.Harness.Sounds.Played.Count} delays={recorder.Delays.Count} vm={scene.ViewModel.StatusScreen.State} shown={scene.Window.PresentedScreen?.State} skill={skillNow.State} canPresent={scene.Policy.CanPresentLanding} visible={scene.Window.IsVisible} tab={scene.ViewModel.SelectedTab} status={scene.Harness.Controller.StatusMessage} det={scene.Harness.Controller.DetectionText}");
                }

                Assert.Equal(ScreenState.Scanning, scene.Window.PresentedScreen!.State); // the screen holds the scan

                // the needle runs on to the white block: the window's landing returns the run's time to the hit (from the
                // needle's real angle), and the sound is scheduled for exactly then, not played before
                (TimeSpan expected, TimeSpan hit) = Assert.Single(stage.Landings);
                Assert.InRange(expected.TotalMilliseconds, 200, 307); // well short of the block: a real run (at most 92 degrees)
                Assert.Equal(expected.TotalMilliseconds, hit.TotalMilliseconds, 1);
                Assert.Equal(hit, Assert.Single(recorder.Delays));
                Assert.Empty(scene.Harness.Sounds.Played);

                recorder.Release(); // the real timer now: the sound plays at the hit
                WindowScene.PumpUntil(() => !scene.Harness.Sounds.Played.IsEmpty, "the sound");
                Assert.Equal(new[] { 0.5 }, scene.Harness.Sounds.Played.ToArray());

                WindowScene.PumpUntil(() => !scene.Window.IsLanding, "the dial lifted");
                Assert.Contains(scene.Window.PresentedScreen!.State, new[] { ScreenState.WaitingFocus, ScreenState.Active });
                Assert.Equal("Coal Tower", scene.Window.PresentedScreen.Plate.MapName);
                CampfireTestHost.Wait(TimeSpan.FromMilliseconds(400));
                Assert.Single(scene.Harness.Sounds.Played); // exactly once
            }
            finally
            {
                scene.Dispose();
            }
        });
    }

    [Fact]
    public void RealmOnlyRead_InFront_TheGreatSaysRealmFound_AndThePlateShowsTheClearing_NotAMap()
    {
        CampfireTestHost.RunSta(() =>
        {
            WindowScene scene = WindowScene.Create(Path.Combine(_dir, "realm-only"), "scanning");
            try
            {
                scene.Policy.WindowActive = true;
                SkillCheck skill = Status(scene).MapPlate.SkillCheck;
                WindowScene.PumpUntil(() => skill.State == SkillCheckState.Scanning, "the spinning check");
                CampfireTestHost.Wait(TimeSpan.FromMilliseconds(300));

                scene.Harness.Ocr.Text = "MACMILLAN ESTATE"; // only the realm line is legible
                WindowScene.PumpUntil(() => scene.Window.IsLanding, "the landing");
                WindowScene.PumpUntil(() => skill.Score.Detail.Length > 0, "the score event");
                Assert.Equal("Realm found: MacMillan Estate", skill.Score.Detail); // never "Map found: <realm>"

                WindowScene.PumpUntil(() => !scene.Window.IsLanding, "the dial lifted");
                StatusScreen shown = scene.Window.PresentedScreen!;
                Assert.Null(scene.Harness.Controller.LastDetection?.MapName);
                Assert.Equal("MacMillan Estate", shown.DetectedRealm);
                Assert.False(shown.Plate.IsMap); // the clearing, not one of the realm's maps
                Assert.Equal("MacMillan Estate", shown.Plate.Title);
                Assert.Equal(MainViewModel.RealmOnlyPlateLine, shown.Plate.Subtitle);
                Assert.Null(shown.BackdropMap);
                Assert.Null(Status(scene).MapPlate.Picture);
                Assert.Single(scene.Harness.Sounds.Played);
            }
            finally
            {
                scene.Dispose();
            }
        });
    }

    [Fact]
    public void SetupWizard_AsTheAppMakesIt_FollowsItsOwnWindow_ItsFogStopsWhileInactiveOrMinimized()
    {
        CampfireTestHost.RunSta(() =>
        {
            WindowScene scene = WindowScene.Create(Path.Combine(_dir, "wizard"), "ready", show: false);
            MotionPolicy current = MotionPolicy.Current; // the app's process-wide policy (no other UI test runs meanwhile)
            (bool active, bool visible, bool animations) was = (current.WindowActive, current.WindowVisible, current.AnimationsEnabled);
            SetupWizardWindow? wizard = null;
            try
            {
                // at app start nothing has told the process-wide policy about a window yet
                current.WindowActive = true;
                current.WindowVisible = true;
                current.AnimationsEnabled = true; // "Play animations" on (the default)

                wizard = new SetupWizardWindow(scene.ViewModel) // as App makes it: no policy of its own
                {
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    ShowInTaskbar = false,
                    ShowActivated = false,
                    Left = -20000,
                    Top = -20000,
                };
                Assert.Same(current, wizard.Policy);
                Assert.False(current.WindowVisible); // it follows the wizard from the start: not shown yet
                wizard.Show();
                CampfireTestHost.Pump();
                Assert.True(current.WindowVisible);

                // behind another window (the game, Steam, a Browse dialog): the policy follows the wizard
                WindowScene.Activate(wizard, false);
                Assert.False(wizard.IsActive);
                Assert.False(current.WindowActive);
                Assert.False(current.ShouldRunAmbient);
                Assert.Empty(MotionDiagnostics.RunningLoops); // the fog does not drift

                // in front: it drifts (unless Windows' own animations are off)
                WindowScene.Activate(wizard, true);
                Assert.True(current.WindowActive);
                if (!current.ReducedMotion)
                {
                    Assert.Contains("FogBackdrop.drift", MotionDiagnostics.RunningLoops);
                }

                // minimized: it stops again
                wizard.WindowState = WindowState.Minimized;
                CampfireTestHost.Pump();
                Assert.False(current.WindowVisible);
                Assert.Empty(MotionDiagnostics.RunningLoops);

                // closed: its fog is gone with it (the main window attaches the policy next)
                wizard.Close();
                wizard = null;
                CampfireTestHost.Pump();
                Assert.Empty(MotionDiagnostics.RunningLoops);
            }
            finally
            {
                wizard?.Close();
                current.WindowActive = was.active;
                current.WindowVisible = was.visible;
                current.AnimationsEnabled = was.animations;
                scene.Dispose();
            }
        });
    }

    [Fact]
    public void MapFound_WhenTheStateChangeReachesTheScreenFirst_TheSkillCheckStillLands()
    {
        CampfireTestHost.RunSta(() =>
        {
            WindowScene scene = WindowScene.Create(Path.Combine(_dir, "race"), "scanning");
            try
            {
                scene.Policy.WindowActive = true;
                CampfireTestHost.Wait(TimeSpan.FromMilliseconds(300));
                System.Windows.Threading.Dispatcher ui = scene.Window.Dispatcher;
                int raced = 0;

                // the controller's state change is refreshed onto the screen BEFORE it reports the map (the race the
                // window's grace covers): queued at the same priority, ahead of the Great
                scene.Harness.Controller.Changed += (_, _) =>
                {
                    if (scene.Harness.States.State is AppState.WaitingForGameplay or AppState.PresetActive && Interlocked.Exchange(ref raced, 1) == 0)
                    {
                        ui.BeginInvoke(System.Windows.Threading.DispatcherPriority.Send, new Action(scene.ViewModel.Refresh));
                    }
                };
                scene.Harness.Ocr.Text = "COAL TOWER\nMACMILLAN ESTATE";

                WindowScene.PumpUntil(() => scene.Window.IsLanding || !scene.Harness.Sounds.Played.IsEmpty, "the Great");
                Assert.Equal(1, Volatile.Read(ref raced));
                Assert.True(scene.Window.IsLanding || scene.Window.PresentedScreen!.State == ScreenState.Scanning, "the scan was held for the landing");
                WindowScene.PumpUntil(() => !scene.Window.IsLanding && scene.Window.PresentedScreen!.Plate.IsMap, "the map");
                Assert.Single(scene.Harness.Sounds.Played);
            }
            finally
            {
                scene.Dispose();
            }
        });
    }

    [Fact]
    public void MapFound_BehindTheGame_TheSoundPlaysAtOnce_WithoutALanding()
    {
        CampfireTestHost.RunSta(() =>
        {
            WindowScene scene = WindowScene.Create(Path.Combine(_dir, "behind"), "scanning");
            try
            {
                var recorder = new RecordingScheduler(scene.ViewModel.Great.Scheduler!);
                scene.ViewModel.Great.Scheduler = recorder;
                scene.Policy.WindowActive = false; // the player is in the game
                SkillCheck skill = Status(scene).MapPlate.SkillCheck;
                int hits = 0;
                skill.Hit += (_, _) => hits++;

                scene.Harness.Ocr.Text = "COAL TOWER\nMACMILLAN ESTATE";
                WindowScene.PumpUntil(() => !scene.Harness.Sounds.Played.IsEmpty, "the sound");

                Assert.False(scene.Window.IsLanding);
                Assert.Empty(recorder.Delays);
                WindowScene.PumpUntil(() => scene.Window.PresentedScreen!.Plate.IsMap, "the map on the plate");
                CampfireTestHost.Wait(TimeSpan.FromMilliseconds(300));
                Assert.Single(scene.Harness.Sounds.Played);
                Assert.Equal(0, hits); // nothing landed out of sight
            }
            finally
            {
                scene.Dispose();
            }
        });
    }

    [Fact]
    public void MapFound_WithAnimationsOff_TheCheckFlashesStill_WithTheScoreEvent_AndTheSoundPlaysAtOnce()
    {
        CampfireTestHost.RunSta(() =>
        {
            WindowScene scene = WindowScene.Create(Path.Combine(_dir, "still"), "scanning");
            try
            {
                var recorder = new RecordingScheduler(scene.ViewModel.Great.Scheduler!);
                scene.ViewModel.Great.Scheduler = recorder;
                scene.Policy.WindowActive = true; // the player looks at the app
                scene.ViewModel.AnimationsEnabled = false;
                CampfireTestHost.Pump();
                Assert.False(scene.Policy.AnimationsEnabled);
                SkillCheck skill = Status(scene).MapPlate.SkillCheck;
                WindowScene.PumpUntil(() => skill.State == SkillCheckState.Scanning, "the still check");
                int hits = 0;
                bool landed = false;
                skill.Hit += (_, _) =>
                {
                    hits++;
                    landed |= scene.Window.IsLanding;
                };

                scene.Harness.Ocr.Text = "COAL TOWER\nMACMILLAN ESTATE";
                WindowScene.PumpUntil(() => !scene.Harness.Sounds.Played.IsEmpty, "the sound");
                Assert.Empty(recorder.Delays); // a still landing has no time to its hit: the sound played at once

                WindowScene.PumpUntil(() => hits == 1, "the still flash (the hit)");
                Assert.True(landed, "the window landed the check");
                Assert.True(skill.Score.IsShowing);
                Assert.Equal("Map found: Coal Tower", skill.Score.Detail); // the DBD-style score event

                WindowScene.PumpUntil(() => !scene.Window.IsLanding && scene.Window.PresentedScreen!.Plate.IsMap, "the map on the plate");
                CampfireTestHost.Wait(TimeSpan.FromMilliseconds(300));
                Assert.Single(scene.Harness.Sounds.Played);
                Assert.Equal(1, hits);
                Assert.Empty(MotionDiagnostics.RunningLoops); // and nothing moved
            }
            finally
            {
                scene.Dispose();
            }
        });
    }

    [Fact]
    public void ALandingThatFails_NeverFreezesTheStatusScreen()
    {
        CampfireTestHost.RunSta(() =>
        {
            WindowScene scene = WindowScene.Create(Path.Combine(_dir, "broken"), "scanning");
            try
            {
                scene.Policy.WindowActive = true;
                SkillCheck skill = Status(scene).MapPlate.SkillCheck;
                WindowScene.PumpUntil(() => skill.State == SkillCheckState.Scanning, "the spinning check");

                // a landing already under way whose owed hit throws when the window's landing completes it
                skill.LandGreat();
                EventHandler broken = (_, _) => throw new InvalidOperationException("broken stage");
                skill.Hit += broken;
                try
                {
                    Assert.Throws<InvalidOperationException>(() => ((IGreatStage)scene.Window).Land("MacMillan Estate", "Coal Tower"));
                }
                finally
                {
                    skill.Hit -= broken;
                }

                Assert.False(scene.Window.IsLanding); // nothing is held for a landing that never started

                // the screen keeps following the controller: the next map found lands and is presented
                scene.Harness.Ocr.Text = "COAL TOWER\nMACMILLAN ESTATE";
                WindowScene.PumpUntil(() => !scene.Window.IsLanding && scene.Window.PresentedScreen!.Plate.IsMap, "the map on the plate");
                Assert.Equal("Coal Tower", scene.Window.PresentedScreen!.Plate.MapName);
                Assert.Single(scene.Harness.Sounds.Played);
            }
            finally
            {
                scene.Dispose();
            }
        });
    }

    [Fact]
    public void NothingAnimates_WhileTheWindowIsInactive_OrAnimationsAreOff()
    {
        CampfireTestHost.RunSta(() =>
        {
            WindowScene scene = WindowScene.Create(Path.Combine(_dir, "quiet"), "scanning");
            try
            {
                FrameworkElement root = (FrameworkElement)scene.Window.Content;
                scene.Policy.WindowActive = true;
                CampfireTestHost.Wait(TimeSpan.FromMilliseconds(2500)); // the one-shot moments of the start have ended
                Assert.NotEmpty(MotionDiagnostics.RunningLoops); // in front: fog, fire, needle
                Assert.NotEmpty(MotionDiagnostics.FindAnimatedObjects(root));

                // behind the game: the scan keeps reading (the detection line changes with every read), yet no clock runs
                scene.Policy.WindowActive = false;
                CampfireTestHost.Wait(TimeSpan.FromMilliseconds(500));
                Assert.Equal(ScreenState.Scanning, scene.ViewModel.StatusScreen.State);
                Assert.Empty(MotionDiagnostics.FindAnimatedObjects(root));
                Assert.Empty(MotionDiagnostics.RunningLoops);

                // "Play animations" off (saved by the controller), even in front
                scene.Policy.WindowActive = true;
                scene.ViewModel.AnimationsEnabled = false;
                Assert.False(scene.Harness.Controller.Config.AnimationsEnabled);
                CampfireTestHost.Wait(TimeSpan.FromMilliseconds(500));
                Assert.False(scene.Policy.AnimationsEnabled);
                Assert.Empty(MotionDiagnostics.FindAnimatedObjects(root));
                Assert.Empty(MotionDiagnostics.RunningLoops);

                // a tab switch with animations off is instant
                scene.ViewModel.SelectedTab = AppTab.Presets;
                CampfireTestHost.Pump();
                Assert.Empty(MotionDiagnostics.FindAnimatedObjects(root));
            }
            finally
            {
                scene.Dispose();
            }
        });
    }

    [Fact]
    public void TitleBar_HasRealNamedButtons_AndTheRailShowsBothHotkeys()
    {
        CampfireTestHost.RunSta(() =>
        {
            WindowScene scene = WindowScene.Create(Path.Combine(_dir, "chrome"), "ready");
            try
            {
                var controls = (StackPanel)scene.Window.FindName("WindowControls");
                string[] names = controls.Children.OfType<Button>().Select(b => AutomationProperties.GetName(b)).ToArray();
                Assert.Equal(new[] { "Minimize", "Maximize", "Close" }, names);
                Assert.All(controls.Children.OfType<Button>(), b => Assert.True(b.Focusable && b.IsTabStop));
                Assert.NotNull(System.Windows.Shell.WindowChrome.GetWindowChrome(scene.Window));
                Assert.Equal(36, System.Windows.Shell.WindowChrome.GetWindowChrome(scene.Window).CaptionHeight);

                var scanKey = (Keycap)scene.Window.FindName("RailHotkey");
                var globalKey = (Keycap)scene.Window.FindName("RailGlobalHotkey");
                var globalHint = (TextBlock)scene.Window.FindName("RailGlobalHintText");
                Assert.Equal("F8", scanKey.Text);
                Assert.Equal("F9", globalKey.Text);
                Assert.Equal(Visibility.Visible, globalKey.Visibility);
                Assert.Equal(1.0, globalKey.Opacity);
                Assert.Equal("Global preset", globalHint.Text);
                Assert.Equal("Global preset hotkey F9", AutomationProperties.GetName(globalKey));

                // another program takes F9: when it is registered again it fails, and the rail no longer presents a
                // working key (dimmed, its hint in Entity red, "unavailable" for assistive technology)
                var f9 = new DetectionHotkeyConfig { Key = "F9" };
                scene.Harness.GlobalPresetHotkey.HoldElsewhere(f9);
                scene.ViewModel.BeginGlobalHotkeyCapture();
                scene.ViewModel.CancelHotkeyCapture();
                CampfireTestHost.Pump();
                Assert.NotNull(scene.Harness.Controller.GlobalHotkeyError);
                Assert.Equal(Visibility.Visible, globalKey.Visibility);
                Assert.True(globalKey.Opacity < 0.5, $"dimmed: {globalKey.Opacity}");
                Assert.Equal("Global preset hotkey unavailable", globalHint.Text);
                Assert.Equal(CampfirePalette.EntityInk.Color, ((SolidColorBrush)globalHint.Foreground).Color);
                Assert.Equal("Global preset hotkey F9, unavailable", AutomationProperties.GetName(globalKey));
                Assert.Equal(1.0, scanKey.Opacity); // F8 still works

                scene.Harness.GlobalPresetHotkey.ReleaseElsewhere(f9);
                scene.ViewModel.BeginGlobalHotkeyCapture();
                scene.ViewModel.CancelHotkeyCapture();
                CampfireTestHost.Pump();
                Assert.Equal(1.0, globalKey.Opacity);
                Assert.Equal("Global preset", globalHint.Text);

                scene.ViewModel.ClearGlobalHotkey();
                CampfireTestHost.Pump();
                Assert.Equal(Visibility.Collapsed, globalKey.Visibility);
                Assert.Equal("Global preset hotkey: not set", scene.ViewModel.RailGlobalHint);

                // 1280 x 800: the rail is clamp(168px, 15.3vw, 220px)
                Assert.Equal(196, ((ColumnDefinition)scene.Window.FindName("RailColumn")).ActualWidth, 0);
            }
            finally
            {
                scene.Dispose();
            }
        });
    }

    private static DBDAutoReShade.App.Views.StatusView Status(WindowScene scene) =>
        (DBDAutoReShade.App.Views.StatusView)scene.Window.FindName("StatusPage");

    /// <summary>Records each scheduled delay; passes it on to the real scheduler, or holds it until <see cref="Release"/>.</summary>
    private sealed class RecordingScheduler(IDelayScheduler inner, bool hold = false) : IDelayScheduler
    {
        private readonly List<(TimeSpan Delay, Action Action)> _held = new();

        public List<TimeSpan> Delays { get; } = new();

        public void Schedule(TimeSpan delay, Action action)
        {
            Delays.Add(delay);
            if (hold)
            {
                _held.Add((delay, action));
            }
            else
            {
                inner.Schedule(delay, action);
            }
        }

        /// <summary>Hands the held actions to the real scheduler (each runs after its own delay from now).</summary>
        public void Release()
        {
            foreach ((TimeSpan delay, Action action) in _held)
            {
                inner.Schedule(delay, action);
            }

            _held.Clear();
        }
    }

    /// <summary>
    /// The real window's landing, from a known point of the spin: the needle is put at <paramref name="spinAt"/> into
    /// the scan's spin, then the window lands the check as usual. Records the run the needle's angle calls for (the
    /// skill check's own landing math) next to the delay the window returned.
    /// </summary>
    private sealed class SeekingStage(MainWindow window, SkillCheck skill, TimeSpan spinAt) : IGreatStage
    {
        public List<(TimeSpan Expected, TimeSpan Hit)> Landings { get; } = new();

        public bool CanLand => ((IGreatStage)window).CanLand;

        public TimeSpan Land(string realm, string? map)
        {
            skill.SeekSpin(spinAt);
            TimeSpan expected = SkillCheckMath.ComputeLanding(skill.NeedleAngle, skill.ZoneStart).HitDelay;
            TimeSpan hit = ((IGreatStage)window).Land(realm, map);
            Landings.Add((expected, hit));
            return hit;
        }
    }
}
