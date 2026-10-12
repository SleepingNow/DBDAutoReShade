using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DBDAutoReShade.App;
using DBDAutoReShade.App.Controls;
using DBDAutoReShade.App.Media;
using DBDAutoReShade.App.ViewModels;
using DBDAutoReShade.App.Views;
using DBDAutoReShade.Core.Models;

namespace DBDAutoReShade.Tests.Campfire;

/// <summary>
/// Opt-in, clock-controlled motion frames of the REAL main window (set <c>DBD_SNAPSHOTS=&lt;dir&gt;</c>), laid out as
/// contact sheets next to the prototype's (<c>scratchpad\motion\a-campfire\final\sheet-*.png</c> and
/// <c>scratchpad\skillcheck\final\sheet-great.png</c>): the window over the real controller (<see cref="WindowScene"/>,
/// far off screen, never activated by Windows: activation is the WM_ACTIVATE message sent to it) is driven through a
/// state change; the moment the change is born every clock is paused, then each frame poses the whole window at a time
/// from the change: every moment and ambient loop at that time from its own start, and the members of the window's state
/// moment at their own offsets inside it (the Great: the dial lands first, the fog clears off the map after the lift).
/// Rendered with RenderTargetBitmap. Never runs in a normal test pass.
/// </summary>
public sealed class MainWindowMotionSnapshotTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dbd-motion-snap-" + Guid.NewGuid().ToString("N"));

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

    [SnapshotFact]
    public void StateChanges_Frames()
    {
        StateStory("catch", "waiting -> ready: the fire catches, the light swells, the rail's dot catches", "waiting",
            s => _ = Task.Run(() =>
            {
                s.Harness.Dbd.IsRunning = true;
                s.Harness.Dbd.ProcessId = 4242;
                s.Harness.Dbd.RaiseStatusChanged(true);
            }),
            s => s.Window.PresentedScreen?.State == ScreenState.Ready, new[] { 0, 150, 350, 600, 1000 });

        StateStory("scan", "ready -> scanning: the skill check appears, the legs lean in, the hero glides", "ready",
            s =>
            {
                s.Harness.Ocr.Text = string.Empty;
                _ = Task.Run(() => s.Harness.Controller.TriggerDetectionAsync());
            },
            s => s.Window.PresentedScreen?.State == ScreenState.Scanning, new[] { 0, 200, 600, 1000, 1300 });

        StateStory("pause", "scanning -> paused: the needle freezes, the ring greys, the fire burns down and the fog thickens", "scanning",
            s =>
            {
                s.Harness.Focused = false;
                s.Harness.Uncovered = false;
            },
            s => s.Window.PresentedScreen?.State == ScreenState.Paused, new[] { 0, 300, 700, 1200 });

        StateStory("confirm", "waitingFocus -> active: the ember races round the frame, the light swells, the legs flinch, the hero catches the light", "waitingFocus",
            s =>
            {
                s.Harness.Focused = true; // the click into the game: switched, then ReShade confirms
                Assert.True(s.Harness.Controller.RetryPendingActivation());
                s.Harness.ReshadeSaves(s.Preset("11.STX.Macmillan.ini"));
            },
            s => s.Window.PresentedScreen?.State == ScreenState.Active, new[] { 0, 200, 450, 700, 950, 1400 },
            thenWaitFor: s => s.Window.PresentedScreen?.Tail == PresetTail.Confirmed); // ("Confirmed by ReShade" arrives as it reads back)

        StateStory("sink", "active -> ready (F9 to Global): the picture sinks into the fog, the clearing returns", "active",
            s => _ = Task.Run(() => s.Harness.GlobalPresetHotkey.Press()),
            s => s.Window.PresentedScreen?.State == ScreenState.Ready, new[] { 0, 250, 500, 750, 1100, 2100 });

        StateStory("dieout", "ready -> waiting: the fire dies out, the light lingers and goes, the hero cools", "ready",
            s => _ = Task.Run(() =>
            {
                s.Harness.Dbd.IsRunning = false;
                s.Harness.Dbd.RaiseStatusChanged(false);
            }),
            s => s.Window.PresentedScreen?.State == ScreenState.Waiting, new[] { 0, 300, 700, 1200, 2000 });

        StateStory("restart", "restart needed: the banner drops in, the page glides down, a red rim light runs down the legs", "ready",
            s =>
            {
                string file = Path.Combine(s.PresetFolder, "13.STX.Haddonfield.ini");
                File.WriteAllText(file, "[General]\nTechniques=H\nTechniqueSorting=H\n");
                Assert.Null(s.Harness.Controller.SetRealmPreset("Haddonfield", file));
            },
            s => s.ViewModel.ShowRestartNotice, new[] { 0, 300, 600, 900, 1300 });

        StateStory("override", "Apply override (a click): no detection story - a quick blur-bridged swap on the plate, quick text swaps, the light fades in 220 ms", "ready",
            s =>
            {
                s.ViewModel.SelectedOverrideRealm = "MacMillan Estate";
                s.ViewModel.ApplyOverrideCommand.Execute(null);
            },
            s => s.Window.PresentedScreen?.State == ScreenState.Override, new[] { 0, 100, 220, 420 });

        StateStory("presets-found", "map found seen from Presets: no landing (the dial is out of sight); the detected map comes out of the fog on the hero", "scanning",
            s =>
            {
                s.Harness.UnfocusRightAfterTheRead();
                s.Harness.Ocr.Text = "COAL TOWER\nMACMILLAN ESTATE";
            },
            s => s.Window.PresentedScreen?.State == ScreenState.WaitingFocus, new[] { 0, 150, 400, 700, 1000 }, tab: AppTab.Presets);

        StateStory("presets-confirm", "waitingFocus -> active seen from Presets: the ember runs round the hero picture, the frame is lit where the flames meet", "waitingFocus",
            s =>
            {
                s.Harness.Focused = true;
                Assert.True(s.Harness.Controller.RetryPendingActivation());
                s.Harness.ReshadeSaves(s.Preset("11.STX.Macmillan.ini"));
            },
            s => s.Window.PresentedScreen?.State == ScreenState.Active, new[] { 0, 200, 450, 700, 950 }, tab: AppTab.Presets);
    }

    /// <summary>
    /// The Great, posed by the clock like the prototype's sheet: the needle 80 degrees short of the white block runs on at
    /// 0.3 deg/ms (HIT at about 268 ms), the flash, the burst and the sparks, the score event, the hold, the lift, then
    /// the rest of the screen changes inside the same moment (the fog clears off the map). Whole-window frames and plate
    /// crops.
    /// </summary>
    [SnapshotFact]
    public void TheGreat_Frames() => CampfireTestHost.RunSta(() =>
    {
        WindowScene scene = WindowScene.Create(Path.Combine(_dir, "great"), "scanning");
        try
        {
            scene.Activate(true);
            MapPlate plate = Status(scene).MapPlate;
            SkillCheck skill = plate.SkillCheck;
            WindowScene.PumpUntil(() => skill.IsSpinning, "the spinning check");
            Quiet();

            // the second check of the scan, the needle 80.4 degrees (268 ms at 0.3 deg/ms) short of 4 degrees into its white block
            const int rev = 1;
            double angle = ((SkillCheckMath.ZoneStart(rev) + 4 - 80.4) % 360 + 360) % 360;
            var stage = new HoldingStage(scene.Window, skill, TimeSpan.FromMilliseconds(1400 * rev + angle / 360 * 1400));
            scene.ViewModel.Great.Stage = stage;
            scene.Harness.UnfocusRightAfterTheRead();
            scene.Harness.Ocr.Text = "COAL TOWER\nMACMILLAN ESTATE";
            WindowScene.PumpUntil(() => stage.HitDelay is not null, "the landing");
            double h = stage.HitDelay!.Value.TotalMilliseconds;
            StateMoment found = scene.Window.CurrentMoment!;
            double lift = SkillCheck.FinishAfterHit.TotalMilliseconds;
            var window = new ContactSheet();
            var crops = new ContactSheet();
            void Frame(double ms, string what)
            {
                Pose(scene, TimeSpan.FromMilliseconds(ms));
                string label = $"found + {ms:0} ms: {what}";
                window.Add((FrameworkElement)scene.Window.Content, label, 0.5);
                crops.AddRegion((FrameworkElement)scene.Window.Content, plate, label, 0.75, 8);
            }

            Frame(0, "map found: the needle keeps its spin speed");
            Frame(h - 18, "still at full speed, just short of the white block");
            Frame(h + 2, $"HIT (h = {h:0} ms): flash, sparks, burst; the sound");
            Frame(h + 90, "the burst ring and the sparks leave the dial");
            Frame(h + 260, "the score event is in; the dial holds");
            Frame(h + 410, "the dial lifts away (scale 1.06 + fade)");

            // the dial is gone: the rest of the screen changes inside the same moment
            skill.CompleteLanding();
            Assert.Same(found, scene.Window.CurrentMoment);
            Frame(h + lift + 180, "the screen changed at h + 520: the fog starts to clear");
            Frame(h + lift + 450, "the picture emerges from the blur");
            Frame(h + lift + 900, "the names land");
            Frame(h + lift + 1450, "Coal Tower revealed, the score event beside it");
            window.Save("window-great", 2, $"Map found (scanning -> waitingFocus), the real window: the skill check lands (HIT at {h:0} ms), holds, lifts; at h + 520 the fog clears off the map");
            crops.Save("window-great-plate", 5, "The Great on the real window's map plate (compare skillcheck\\final\\sheet-great.png)");
        }
        finally
        {
            scene.Dispose();
        }
    }, timeoutSeconds: 180);

    /// <summary>The small moments: the tab crossfade (pointer), the Presets hero swap with the selection bar's glide, the
    /// Clear fog, the filter's fade-up and the Test countdown ring.</summary>
    [SnapshotFact]
    public void TabsAndPresets_Frames() => CampfireTestHost.RunSta(() =>
    {
        WindowScene scene = WindowScene.Create(Path.Combine(_dir, "tabs"), "active");
        try
        {
            MainWindow window = scene.Window;
            MainViewModel vm = scene.ViewModel;
            scene.Activate(true);
            Quiet();

            // the tab crossfade: a click on Presets in the rail
            var nav = (StackPanel)window.FindName("Nav");
            Press(nav, down: true);
            vm.SelectedTab = AppTab.Presets;
            Press(nav, down: false);
            MotionDiagnostics.FreezeAllAt(TimeSpan.Zero);
            Sheet(scene, "window-tab", "Tabs: a pointer switch crossfades the content (180 ms) with a 6 px rise; keyboard switches are instant",
                (FrameworkElement)window.Content, 0.5, 4, 0, 60, 120, 180);
            Release();

            // the hero swap and the selection bar's glide: a click on another realm
            var view = (PresetsView)window.FindName("PresetsPage");
            var list = (ListBox)view.FindName("RealmList");
            var detail = (FrameworkElement)view.FindName("Detail");
            RealmRow raccoon = vm.RealmRows.First(r => r.RealmName == "Raccoon City"); // (two rows below the active realm, in view)
            Press(list, down: true);
            vm.SelectRow(raccoon);
            Press(list, down: false);
            MotionDiagnostics.FreezeAllAt(TimeSpan.Zero);
            var hero = new ContactSheet();
            var select = new ContactSheet();
            foreach (int ms in new[] { 0, 60, 120, 220 })
            {
                Pose(scene, TimeSpan.FromMilliseconds(ms));
                hero.AddRegion((FrameworkElement)window.Content, detail, $"map picture swap @ {ms} ms", 0.6);
                select.AddRegion((FrameworkElement)window.Content, list, $"selection bar glide @ {ms} ms");
            }

            hero.Save("window-heroswap", 4, "Presets: map picture swap (blur-bridged, 220 ms) on a click");
            select.Save("window-select", 4, "Presets: the selection bar glides to the clicked row (220 ms ease-in-out); keyboard moves snap");
            Release();

            // Clear: the fog swallows the realm's map strip
            vm.ClearSelectedCommand.Execute(null);
            vm.ConfirmClearCommand.Execute(null);
            MotionDiagnostics.FreezeAllAt(TimeSpan.Zero);
            var row = (FrameworkElement)list.ItemContainerGenerator.ContainerFromItem(raccoon);
            Sheet(scene, "window-clear", "Presets: Clear - the fog swallows the realm's map strip (280 ms)", row, 1.5, 4, 0, 90, 180, 300);
            Release();

            // the filter: rows that come back fade up, 30 ms apart
            vm.FilterText = "racc";
            CampfireTestHost.Pump();
            Quiet();
            vm.FilterText = string.Empty;
            CampfireTestHost.Pump(System.Windows.Threading.DispatcherPriority.Loaded);
            MotionDiagnostics.FreezeAllAt(TimeSpan.Zero);
            Sheet(scene, "window-filter", "Presets: clearing the filter - rows that come back fade up (30 ms apart); rows that stayed never move", list, 1, 5, 0, 60, 140, 240, 400);
            Release();

            // Test: the 15 s skill-check countdown ring
            vm.SelectRow(vm.RealmRows.First(r => r.RealmName == "Autohaven Wreckers"));
            scene.Harness.Focused = false;
            vm.TestSelectedCommand.Execute(null);
            var ring = (CountdownRing)view.FindName("TestRing");
            WindowScene.PumpUntil(() => ring.IsRunning, "the countdown");
            var feedback = (FrameworkElement)view.FindName("FeedbackArea");
            var test = new ContactSheet();
            foreach (int ms in new[] { 300, 4000, 9000 })
            {
                ring.FreezeAt(TimeSpan.FromMilliseconds(ms));
                feedback.UpdateLayout();
                test.AddRegion((FrameworkElement)window.Content, feedback, $"Test @ {ms} ms", 1, 6);
            }

            test.Save("window-test", 3, "Presets: Test - the 15 s focus wait as a draining skill-check ring");
        }
        finally
        {
            scene.Dispose();
        }
    }, timeoutSeconds: 180);

    /// <summary>
    /// Builds the window in <paramref name="from"/>, lets it rest, runs <paramref name="trigger"/>, pauses every clock
    /// the instant the change is <paramref name="born"/> (and keeps what follows paused until <paramref name="thenWaitFor"/>
    /// holds, if given: those parts are posed at their real offsets), then saves a sheet of whole-window frames at
    /// <paramref name="times"/> (ms from the change).
    /// </summary>
    private void StateStory(string name, string title, string from, Action<WindowScene> trigger, Func<WindowScene, bool> born, int[] times, Func<WindowScene, bool>? thenWaitFor = null, AppTab tab = AppTab.Status) =>
        CampfireTestHost.RunSta(() =>
        {
            WindowScene scene = WindowScene.Create(Path.Combine(_dir, name), from);
            try
            {
                scene.Activate(true);
                scene.ViewModel.SelectedTab = tab;
                CampfireTestHost.Pump();
                Quiet();
                var camera = new Camera(scene, born);
                trigger(scene);
                WindowScene.PumpUntil(() => camera.Born, name);
                if (thenWaitFor is not null)
                {
                    WindowScene.PumpUntil(() => thenWaitFor(scene), name + ", then the rest of it"); // (still held)
                }

                Sheet(scene, "window-" + name, title, (FrameworkElement)scene.Window.Content, 0.5, 3, times);
            }
            finally
            {
                scene.Dispose();
            }
        }, timeoutSeconds: 180);

    private static void Sheet(WindowScene scene, string name, string title, FrameworkElement element, double scale, int columns, params int[] times)
    {
        var sheet = new ContactSheet();
        foreach (int ms in times)
        {
            Pose(scene, TimeSpan.FromMilliseconds(ms));
            if (ReferenceEquals(element, scene.Window.Content))
            {
                sheet.Add(element, $"@ {ms} ms", scale);
            }
            else
            {
                sheet.AddRegion((FrameworkElement)scene.Window.Content, element, $"@ {ms} ms", scale, 4);
            }
        }

        sheet.Save(name, columns, title);
    }

    /// <summary>Poses the window <paramref name="time"/> into the change: every clock at that time from its own start, then
    /// the state moment's members at their own offsets.</summary>
    private static void Pose(WindowScene scene, TimeSpan time)
    {
        MotionDiagnostics.FreezeAllAt(time, scene.Window.CurrentMoment);
        scene.Window.UpdateLayout();
    }

    /// <summary>Lets go of the moments the frames paused (they land on their rest poses).</summary>
    private static void Release()
    {
        MotionDiagnostics.StopAllMoments();
        CampfireTestHost.Pump();
    }

    /// <summary>Waits until no one-shot moment plays (the ambient loops keep running).</summary>
    private static void Quiet() =>
        WindowScene.PumpUntil(() => MotionDiagnostics.ActiveMoments.Count == 0, "the window at rest", 8000);

    private static void Press(UIElement element, bool down) =>
        element.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
        {
            RoutedEvent = down ? UIElement.PreviewMouseDownEvent : UIElement.PreviewMouseUpEvent,
        });

    private static StatusView Status(WindowScene scene) => (StatusView)scene.Window.FindName("StatusPage");

    /// <summary>Pauses every clock the instant a change is born (right after the window has presented it, in the same
    /// dispatcher operation) and keeps whatever starts after it paused too, until the frames are posed.</summary>
    private sealed class Camera
    {
        private readonly WindowScene _scene;
        private bool _holding;

        public Camera(WindowScene scene, Func<WindowScene, bool> born)
        {
            _scene = scene;
            scene.ViewModel.PropertyChanged += (_, _) =>
            {
                if (!Born && born(scene))
                {
                    Born = true;
                    _holding = true;
                }

                if (_holding)
                {
                    MotionDiagnostics.FreezeAllAt(TimeSpan.Zero, _scene.Window.CurrentMoment);
                }
            };
        }

        public bool Born { get; private set; }
    }

    /// <summary>
    /// The window's own stage with the needle put where the frames need it first, and the landing's clocks and timers
    /// paused right after it starts (the frames step through it by the clock; <see cref="SkillCheck.CompleteLanding"/>
    /// ends it).
    /// </summary>
    private sealed class HoldingStage(MainWindow window, SkillCheck skill, TimeSpan spinAt) : IGreatStage
    {
        public TimeSpan? HitDelay { get; private set; }

        public bool CanLand => ((IGreatStage)window).CanLand;

        public TimeSpan Land(string realm, string? map)
        {
            skill.SeekSpin(spinAt);
            TimeSpan hit = ((IGreatStage)window).Land(realm, map);
            skill.FreezeAt(TimeSpan.Zero);
            MotionDiagnostics.FreezeAllAt(TimeSpan.Zero, window.CurrentMoment);
            HitDelay = hit;
            return hit;
        }
    }
}
