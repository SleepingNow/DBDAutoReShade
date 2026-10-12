using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DBDAutoReShade.App;
using DBDAutoReShade.App.Controls;
using DBDAutoReShade.App.Media;
using DBDAutoReShade.App.ViewModels;
using DBDAutoReShade.App.Views;
using DBDAutoReShade.Core.Models;

namespace DBDAutoReShade.Tests.Campfire;

/// <summary>
/// Screen-level motion on the REAL main window over the real controller (<see cref="WindowScene"/>: off screen, never
/// activated by Windows; activation is simulated with the WM_ACTIVATE message WPF reacts to, <see cref="WindowScene.Activate"/>):
/// one orchestrated moment per state change across the whole window, a new change interrupting the previous one cleanly,
/// the Great as one moment; the performance gating (no clock runs while the window is inactive, minimized or hidden,
/// on a page that is not shown, or once "Play animations" is turned off — at once; behind the game even a state change
/// is instant), reduced motion (short fades only); the override as a quick user-driven swap; the plate's bottom edge
/// kept through a glide; the tab crossfade for pointer switches only; the Presets moments; press feedback on every
/// button.
/// </summary>
public sealed class MainWindowMotionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dbd-motion-" + Guid.NewGuid().ToString("N"));

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
    public void EachStateChange_IsOneOrchestratedMoment_AcrossTheWindow()
    {
        CampfireTestHost.RunSta(() =>
        {
            WindowScene scene = WindowScene.Create(Path.Combine(_dir, "one"), "ready");
            try
            {
                scene.Activate(true);
                CampfireTestHost.Wait(TimeSpan.FromMilliseconds(100));
                var births = RecordBirths(scene);

                // ready -> scanning: the skill check appears, the legs lean in, the hero and the light change
                scene.Harness.Ocr.Text = string.Empty;
                _ = Task.Run(() => scene.Harness.Controller.TriggerDetectionAsync());
                WindowScene.PumpUntil(() => births.Any(b => b.Name == "Ready → Scanning"), "the scan's moment");
                (string _, IReadOnlyList<string> parts, StateMoment scan) = births.First(b => b.Name == "Ready → Scanning");
                Assert.Contains("CrossfadeText.BlurRise", parts); // the hero's blur-bridged crossfade
                Assert.Contains("SkillCheck.enter", parts);
                Assert.Contains("EntityLegs.pose", parts); // the lean-in
                Assert.Contains("FogBackdrop.ember", parts); // the campfire light
                Assert.Contains("StatusView.detection", parts);
                Assert.Same(scan, scene.Window.CurrentMoment);

                // scanning -> paused: the needle freezes and the fog thickens over the clearing, in a new moment
                scene.Harness.Focused = false;
                scene.Harness.Uncovered = false;
                WindowScene.PumpUntil(() => births.Any(b => b.Name == "Scanning → Paused"), "the pause's moment");
                (string _, IReadOnlyList<string> paused, StateMoment pause) = births.First(b => b.Name == "Scanning → Paused");
                Assert.Contains("MapPlate.fire", paused); // the burn-down (the fog thickens)
                Assert.Contains("SkillCheck.tint", paused); // the ring greys
                Assert.True(scan.WasInterrupted);
                Assert.False(Status(scene).MapPlate.SkillCheck.IsSpinning); // the needle froze
                Assert.Same(pause, scene.Window.CurrentMoment);
            }
            finally
            {
                scene.Dispose();
            }
        });
    }

    [Fact]
    public void ANewStateChange_InterruptsThePreviousMoment_Cleanly()
    {
        CampfireTestHost.RunSta(() =>
        {
            WindowScene scene = WindowScene.Create(Path.Combine(_dir, "flip"), "active");
            try
            {
                scene.Activate(true);
                scene.Settle();
                var births = RecordBirths(scene);

                // active -> Global (F9, handled off the UI thread, as in the app): the picture sinks into the fog
                _ = Task.Run(() => scene.Harness.GlobalPresetHotkey.Press());
                WindowScene.PumpUntil(() => births.Any(b => b.Name == "Active → Ready"), "the switch to Global");
                StateMoment toGlobal = births.First(b => b.Name == "Active → Ready").Moment;
                Assert.True(toGlobal.MomentNames.Contains("MapPlate.Sink"), string.Join(" | ", births.Select(b => b.Name + ": " + string.Join(", ", b.Parts))));
                MapPlate plate = Status(scene).MapPlate;
                Assert.Equal(1, Count<MapPicture>(plate)); // the sinking picture, a ghost over the clearing

                // F8 at once: the scan's moment takes over; the sink eases back to rest instead of jumping
                CampfireTestHost.Wait(TimeSpan.FromMilliseconds(120));
                scene.Harness.Ocr.Text = string.Empty; // (a loading screen that is still being read)
                _ = Task.Run(() => scene.Harness.Controller.TriggerDetectionAsync());
                WindowScene.PumpUntil(() => scene.Window.CurrentMoment is { } m && !ReferenceEquals(m, toGlobal) && m.Name.EndsWith("Scanning", StringComparison.Ordinal), "the scan");
                Assert.True(toGlobal.WasInterrupted);
                Assert.Contains(toGlobal.Moments, m => m.IsSettling); // easing back, not snapping
                Assert.DoesNotContain("MapPlate.Sink", MotionDiagnostics.ActiveMoments); // its own clocks are gone

                // within the settle everything of the old moment has landed: no ghost, nothing of it plays
                CampfireTestHost.Wait(MainWindow.InterruptSettle + TimeSpan.FromMilliseconds(150));
                Assert.False(toGlobal.IsPlaying, "Still playing: " + string.Join(", ", toGlobal.MomentNames));
                Assert.Equal(0, Count<MapPicture>(plate));
                Assert.DoesNotContain(MotionDiagnostics.ActiveMoments, n => n.StartsWith("MapPlate.Sink", StringComparison.Ordinal));
                Assert.NotNull(plate.Campfire);
            }
            finally
            {
                scene.Dispose();
            }
        }, timeoutSeconds: 180);
    }

    [Fact]
    public void TheGreat_IsOneMoment_TheFogClearsOffTheMapInsideIt()
    {
        CampfireTestHost.RunSta(() =>
        {
            WindowScene scene = WindowScene.Create(Path.Combine(_dir, "great"), "scanning");
            try
            {
                scene.Activate(true);
                SkillCheck skill = Status(scene).MapPlate.SkillCheck;
                WindowScene.PumpUntil(() => skill.IsSpinning, "the spinning check");
                StateMoment? scan = scene.Window.CurrentMoment;

                scene.Harness.UnfocusRightAfterTheRead(); // found, then waiting for the click into the game
                scene.Harness.Ocr.Text = "COAL TOWER\nMACMILLAN ESTATE";
                WindowScene.PumpUntil(() => scene.Window.IsLanding, "the landing");
                StateMoment found = scene.Window.CurrentMoment!;
                Assert.NotSame(scan, found);
                Assert.Contains("SkillCheck.land", found.MomentNames);

                // the dial lifts: the rest of the screen changes inside the same moment
                skill.CompleteLanding();
                CampfireTestHost.Pump();
                Assert.False(scene.Window.IsLanding);
                Assert.Same(found, scene.Window.CurrentMoment);
                Assert.Contains("MapPlate.Reveal", found.MomentNames); // the fog clears off the map
                Assert.Contains("CrossfadeText.BlurRise", found.MomentNames); // the hero
                Assert.Equal("Coal Tower", scene.Window.PresentedScreen!.Plate.MapName);
            }
            finally
            {
                scene.Dispose();
            }
        });
    }

    [Fact]
    public void AmbientLoops_RunOnlyWhileTheWindowIsActiveAndShown_AndResumeOnActivation()
    {
        CampfireTestHost.RunSta(() =>
        {
            WindowScene scene = WindowScene.Create(Path.Combine(_dir, "gate"), "scanning");
            try
            {
                MainWindow window = scene.Window;
                var root = (FrameworkElement)window.Content;
                scene.Activate(true);
                Assert.True(window.IsActive);
                Assert.True(scene.Policy.ShouldRunAmbient);
                CampfireTestHost.Wait(TimeSpan.FromMilliseconds(2500)); // the scan's one-shot moments have ended
                Assert.NotEmpty(MotionDiagnostics.RunningLoops); // in front: the fog drifts, the fire flickers, the needle spins
                Assert.True(Status(scene).MapPlate.SkillCheck.IsSpinning);

                // behind the game (Windows deactivates the window): every loop holds still at once, and nothing runs
                // while the scan keeps reading
                scene.Activate(false);
                Assert.False(scene.Policy.WindowActive);
                Assert.Empty(MotionDiagnostics.RunningLoops);
                Assert.False(Status(scene).MapPlate.SkillCheck.IsSpinning);
                CampfireTestHost.Wait(TimeSpan.FromMilliseconds(300));
                AssertQuiet(root);

                // back in front: they resume
                scene.Activate(true);
                Assert.NotEmpty(MotionDiagnostics.RunningLoops);
                Assert.True(Status(scene).MapPlate.SkillCheck.IsSpinning);

                // minimized (the window is far off screen and not in the taskbar)
                window.WindowState = WindowState.Minimized;
                CampfireTestHost.Pump();
                Assert.False(scene.Policy.WindowVisible);
                AssertQuiet(root);
                window.WindowState = WindowState.Normal;
                CampfireTestHost.Pump();
                scene.Activate(true);
                Assert.NotEmpty(MotionDiagnostics.RunningLoops);

                // hidden
                window.Hide();
                CampfireTestHost.Pump();
                Assert.False(scene.Policy.WindowVisible);
                AssertQuiet(root);
                window.Show();
                CampfireTestHost.Pump();
                scene.Activate(true);
                Assert.True(scene.Policy.ShouldRunAmbient);
                Assert.NotEmpty(MotionDiagnostics.RunningLoops);
            }
            finally
            {
                scene.Dispose();
            }
        });
    }

    [Fact]
    public void TurningAnimationsOff_StopsEverythingAtOnce_EvenMidChange()
    {
        CampfireTestHost.RunSta(() =>
        {
            WindowScene scene = WindowScene.Create(Path.Combine(_dir, "off"), "ready");
            try
            {
                var root = (FrameworkElement)scene.Window.Content;
                scene.Activate(true);
                var births = RecordBirths(scene);
                scene.Harness.Ocr.Text = string.Empty;
                _ = Task.Run(() => scene.Harness.Controller.TriggerDetectionAsync());
                WindowScene.PumpUntil(() => births.Any(b => b.Name == "Ready → Scanning"), "the scan's moment");
                Assert.NotEmpty(MotionDiagnostics.ActiveMoments); // the change is playing
                Assert.NotEmpty(MotionDiagnostics.RunningLoops);
                Assert.NotEmpty(MotionDiagnostics.FindAnimatedObjects(root));

                // Settings -> "Play animations" off, mid-change: nothing is left running, without waiting for anything
                scene.ViewModel.AnimationsEnabled = false;
                Assert.Empty(MotionDiagnostics.ActiveMoments);
                AssertQuiet(root);
                Assert.False(scene.Window.CurrentMoment!.IsPlaying);

                // and the next state change is instant
                scene.Harness.Focused = false;
                scene.Harness.Uncovered = false;
                WindowScene.PumpUntil(() => scene.ViewModel.StatusScreen.State == ScreenState.Paused && scene.Window.PresentedScreen!.State == ScreenState.Paused, "the pause");
                Assert.Empty(MotionDiagnostics.ActiveMoments);
                AssertQuiet(root);

                // on again: the loops come back (the window is in front)
                scene.ViewModel.AnimationsEnabled = true;
                CampfireTestHost.Pump();
                Assert.NotEmpty(MotionDiagnostics.RunningLoops);
            }
            finally
            {
                scene.Dispose();
            }
        });
    }

    [Fact]
    public void WindowsAnimationsOff_StateChangesOnlyFade_AndNoLoopRuns()
    {
        CampfireTestHost.RunSta(() =>
        {
            WindowScene scene = WindowScene.Create(Path.Combine(_dir, "reduced"), "active");
            try
            {
                var root = (FrameworkElement)scene.Window.Content;
                scene.Policy.ReducedMotionOverride = true; // what SystemParameters.ClientAreaAnimation == false sets
                scene.Activate(true);
                scene.Settle();
                Assert.Empty(MotionDiagnostics.RunningLoops);
                var births = RecordBirths(scene);
                IReadOnlyList<MotionMoment> born = Array.Empty<MotionMoment>();
                scene.ViewModel.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(MainViewModel.StatusScreen) && born.Count == 0 && scene.Window.CurrentMoment is { Name: "Active → Ready" } m)
                    {
                        born = m.Moments;
                    }
                };

                _ = Task.Run(() => scene.Harness.GlobalPresetHotkey.Press()); // F9: active -> ready, the map sinks
                WindowScene.PumpUntil(() => births.Any(b => b.Name == "Active → Ready"), "the switch to Global");
                IReadOnlyList<string> animated = MotionDiagnostics.FindAnimatedObjects(root);
                Assert.NotEmpty(animated); // the fades
                Assert.DoesNotContain(animated, a => a.Contains("Transform", StringComparison.Ordinal) || a.Contains("BlurEffect", StringComparison.Ordinal) || a.Contains(".Clip", StringComparison.Ordinal));
                Assert.Empty(MotionDiagnostics.RunningLoops);

                // short fades only, like the prototype's A() under reduced motion (≤ 200 ms, starting ≤ 160 ms in): the
                // whole change has played out well before half a second
                Assert.NotEmpty(born);
                CampfireTestHost.Wait(TimeSpan.FromMilliseconds(500));
                Assert.True(born.All(m => m.HasEnded), "Still playing 500 ms after the change: " + string.Join(", ", born.Where(m => !m.HasEnded).Select(m => m.Name)));
            }
            finally
            {
                scene.Dispose();
            }
        }, timeoutSeconds: 180);
    }

    [Fact]
    public void APageThatIsNotShown_DoesNotMove()
    {
        CampfireTestHost.RunSta(() =>
        {
            WindowScene scene = WindowScene.Create(Path.Combine(_dir, "offstage"), "scanning");
            try
            {
                scene.Activate(true);
                StatusView status = Status(scene);
                WindowScene.PumpUntil(() => status.MapPlate.SkillCheck.IsSpinning, "the spinning check");

                // on Settings, the Status page's needle, fire and fog wisp stop; the backdrop still drifts
                scene.ViewModel.SelectedTab = AppTab.Settings;
                CampfireTestHost.Pump();
                Assert.False(status.MapPlate.SkillCheck.IsSpinning);
                Assert.False(status.MapPlate.Campfire!.IsAmbientRunning);
                Assert.Empty(MotionDiagnostics.FindAnimatedObjects(status));
                Assert.Empty(MotionDiagnostics.FindAnimatedObjects((FrameworkElement)scene.Window.FindName("FogFront")));
                Assert.True(((FogBackdrop)scene.Window.FindName("Backdrop")).IsDrifting);

                // back on Status they move again
                scene.ViewModel.SelectedTab = AppTab.Status;
                CampfireTestHost.Pump();
                Assert.True(status.MapPlate.SkillCheck.IsSpinning);
                Assert.True(status.MapPlate.Campfire!.IsAmbientRunning);
            }
            finally
            {
                scene.Dispose();
            }
        });
    }

    [Fact]
    public void Tabs_CrossfadeWithASmallRise_ForThePointer_AndSwitchAtOnce_FromTheKeyboard()
    {
        CampfireTestHost.RunSta(() =>
        {
            WindowScene scene = WindowScene.Create(Path.Combine(_dir, "tabs"), "ready");
            try
            {
                MainWindow window = scene.Window;
                scene.Activate(true);
                var nav = (StackPanel)window.FindName("Nav");
                var presets = (FrameworkElement)window.FindName("PresetsPage");
                var settings = (FrameworkElement)window.FindName("SettingsPage");

                // a pointer switch: a 180 ms crossfade with a 6 px rise
                Press(nav, down: true);
                scene.ViewModel.SelectedTab = AppTab.Presets;
                Press(nav, down: false);
                Assert.Contains("MainWindow.tab", MotionDiagnostics.ActiveMoments);
                MotionDiagnostics.FreezeAllAt(TimeSpan.Zero);
                Assert.Equal(0, presets.Opacity, 2);
                Assert.Equal(6, ((TranslateTransform)presets.RenderTransform).Y, 2);
                MotionDiagnostics.FreezeAllAt(TimeSpan.FromMilliseconds(90));
                Assert.InRange(((TranslateTransform)presets.RenderTransform).Y, 0.01, 6);
                MotionDiagnostics.FreezeAllAt(TimeSpan.FromMilliseconds(180));
                Assert.Equal(1, presets.Opacity, 2);
                Assert.Equal(0, ((TranslateTransform)presets.RenderTransform).Y, 2);
                CampfireTestHost.Pump();

                // a keyboard switch (Down in the rail): no motion at all
                var key = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, 0, Key.Down) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                nav.RaiseEvent(key);
                Assert.Equal(AppTab.Settings, scene.ViewModel.SelectedTab);
                Assert.DoesNotContain("MainWindow.tab", MotionDiagnostics.ActiveMoments);
                Assert.Equal(1, settings.Opacity, 2);
                Assert.Empty(MotionDiagnostics.FindAnimatedObjects(settings));

                // a press on the rail that switched nothing does not make a later switch (here: from code) animate
                Press(nav, down: true);
                Press(nav, down: false);
                CampfireTestHost.Pump();
                scene.ViewModel.SelectedTab = AppTab.Status;
                Assert.DoesNotContain("MainWindow.tab", MotionDiagnostics.ActiveMoments);

                // under reduced motion the pointer switch only fades
                scene.Policy.ReducedMotionOverride = true;
                Press(nav, down: true);
                scene.ViewModel.SelectedTab = AppTab.Presets;
                MotionDiagnostics.FreezeAllAt(TimeSpan.Zero);
                Assert.Equal(0, presets.Opacity, 2);
                Assert.Equal(0, ((TranslateTransform)presets.RenderTransform).Y, 2);
            }
            finally
            {
                scene.Dispose();
            }
        });
    }

    [Fact]
    public void Presets_TheBarGlides_TheHeroSwaps_ReappearingRowsFadeUp_ClearFogs_AndTheTestRingCounts()
    {
        CampfireTestHost.RunSta(() =>
        {
            WindowScene scene = WindowScene.Create(Path.Combine(_dir, "presets"), "ready");
            try
            {
                MainWindow window = scene.Window;
                MainViewModel vm = scene.ViewModel;
                scene.Activate(true);
                vm.SelectedTab = AppTab.Presets;
                scene.Settle();
                var view = (PresetsView)window.FindName("PresetsPage");
                var list = (ListBox)view.FindName("RealmList");

                // a click on a row: the selection bar glides (transform) and the hero swaps with a blur crossfade
                RealmRow target = vm.RealmRows.First(r => r.RealmName == "Coldwind Farm");
                Press(list, down: true);
                vm.SelectRow(target);
                Assert.Contains("PresetsView.selection", MotionDiagnostics.ActiveMoments);
                Assert.Contains("PresetsView.heroSwap", MotionDiagnostics.ActiveMoments);
                Assert.Contains(MotionDiagnostics.FindAnimatedObjects(view), a => a.Contains("TranslateTransform", StringComparison.Ordinal));
                Assert.Contains(MotionDiagnostics.FindAnimatedObjects(view), a => a.Contains("BlurEffect", StringComparison.Ordinal));
                Press(list, down: false);
                MotionDiagnostics.FreezeAllAt(TimeSpan.FromMilliseconds(221)); // the swap is over by 220 ms
                CampfireTestHost.Wait(TimeSpan.FromMilliseconds(60));
                Assert.DoesNotContain("PresetsView.heroSwap", MotionDiagnostics.ActiveMoments);

                // a keyboard move: the bar snaps (no glide, no swap)
                vm.SelectRow(vm.RealmRows.First(r => r.RealmName == "Crotus Prenn Asylum"));
                Assert.DoesNotContain("PresetsView.heroSwap", MotionDiagnostics.ActiveMoments);
                Assert.False(MotionDiagnostics.FindAnimatedObjects(view).Any(a => a.Contains("TranslateTransform", StringComparison.Ordinal)), "Moments: " + string.Join(", ", MotionDiagnostics.ActiveMoments));

                // the filter: typing that only hides rows moves nothing; rows that come back fade up, and only those
                vm.FilterText = "cold";
                CampfireTestHost.Pump();
                Assert.DoesNotContain("PresetsView.filter", MotionDiagnostics.ActiveMoments);
                vm.FilterText = "coldw";
                CampfireTestHost.Pump();
                Assert.DoesNotContain("PresetsView.filter", MotionDiagnostics.ActiveMoments);
                vm.FilterText = string.Empty;
                CampfireTestHost.Pump(DispatcherPriority.Render); // up to the first frame that draws the rows again
                Assert.Contains("PresetsView.filter", MotionDiagnostics.ActiveMoments);
                List<ListBoxItem> returning = vm.RealmRows.Where(r => !ReferenceEquals(r, target))
                    .Select(r => list.ItemContainerGenerator.ContainerFromItem(r)).OfType<ListBoxItem>()
                    .Where(item => InView(list, item)).ToList();
                Assert.NotEmpty(returning);
                Assert.True(returning.All(item => item.Opacity < 0.05), "Drawn opaque in the first frame: " + string.Join(", ", returning.Where(i => i.Opacity >= 0.05).Select(i => ((RealmRow)i.DataContext).RealmName + " " + i.Opacity.ToString("0.00"))));
                CampfireTestHost.Pump();
                var coldwind = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(target);
                Assert.Empty(MotionDiagnostics.FindAnimatedObjects(coldwind)); // it never left: it does not move
                CampfireTestHost.Wait(TimeSpan.FromMilliseconds(500)); // (the whole cascade ends within 400 ms)
                Assert.DoesNotContain("PresetsView.filter", MotionDiagnostics.ActiveMoments);

                // Clear: the realm's strip goes into the fog within 300 ms
                vm.SelectRow(target);
                vm.ClearSelectedCommand.Execute(null);
                vm.ConfirmClearCommand.Execute(null);
                CampfireTestHost.Pump();
                Assert.True(target.IsUnset);
                Assert.Contains("MapStrip.fogIn", MotionDiagnostics.ActiveMoments);
                CampfireTestHost.Wait(TimeSpan.FromMilliseconds(400));
                Assert.DoesNotContain("MapStrip.fogIn", MotionDiagnostics.ActiveMoments);

                // Test: the 15 s skill-check countdown ring runs (a smooth drain while the window is in front)
                vm.SelectRow(vm.RealmRows.First(r => r.RealmName == "Autohaven Wreckers"));
                scene.Harness.Focused = false;
                vm.TestSelectedCommand.Execute(null);
                var ring = (CountdownRing)view.FindName("TestRing");
                WindowScene.PumpUntil(() => ring.IsRunning, "the countdown");
                Assert.Equal(CountdownMode.Drain, ring.Mode);
                scene.Activate(false);
                Assert.Equal(CountdownMode.Held, ring.Mode); // behind the game: no clock, it steps
                Assert.True(ring.IsRunning);
            }
            finally
            {
                scene.Dispose();
            }
        }, timeoutSeconds: 180);
    }

    [Fact]
    public void AStateChange_SeenFromPresets_IsToldOnTheHeroPicture_InsideTheStateMoment()
    {
        CampfireTestHost.RunSta(() =>
        {
            WindowScene scene = WindowScene.Create(Path.Combine(_dir, "presets-story"), "scanning");
            try
            {
                MainViewModel vm = scene.ViewModel;
                scene.Activate(true);
                vm.SelectedTab = AppTab.Presets;
                CampfireTestHost.Pump();
                var births = RecordBirths(scene);

                // map found while Presets is open: no landing (the dial is out of sight); the detected map comes out of
                // the fog on the hero picture, as part of the change's moment
                scene.Harness.UnfocusRightAfterTheRead();
                scene.Harness.Ocr.Text = "COAL TOWER\nMACMILLAN ESTATE";
                WindowScene.PumpUntil(() => births.Any(b => b.Name == "Scanning → WaitingFocus"), "the map found");
                Assert.False(scene.Window.IsLanding);
                Assert.Equal("Coal Tower", vm.HeroMap);
                Assert.Contains("PresetsView.heroReveal", births.First(b => b.Name == "Scanning → WaitingFocus").Parts);
                Assert.Single(scene.Harness.Sounds.Played); // the Great sound, at once
                CampfireTestHost.Wait(TimeSpan.FromMilliseconds(1200));

                // the click into the game, then ReShade confirms: the ember runs round the hero picture
                scene.Harness.Focused = true;
                Assert.True(scene.Harness.Controller.RetryPendingActivation());
                WindowScene.PumpUntil(() => births.Any(b => b.Name == "WaitingFocus → Active"), "the switch");
                Assert.Contains("PresetsView.confirm", births.First(b => b.Name == "WaitingFocus → Active").Parts);
                var view = (PresetsView)scene.Window.FindName("PresetsPage");
                Assert.NotEmpty(((Canvas)view.FindName("HeroSweep")).Children); // the two ember strokes
            }
            finally
            {
                scene.Dispose();
            }
        });
    }

    [Fact]
    public void BehindTheGame_AChangeStopsAtOnce_AndStateChangesAreInstant()
    {
        CampfireTestHost.RunSta(() =>
        {
            WindowScene scene = WindowScene.Create(Path.Combine(_dir, "behind"), "ready");
            try
            {
                var root = (FrameworkElement)scene.Window.Content;
                ControllerHarness h = scene.Harness;
                scene.Activate(true);
                var births = RecordBirths(scene);

                // F8 while the app is in front: the scan's moment plays ...
                h.Ocr.Text = string.Empty;
                _ = Task.Run(() => h.Controller.TriggerDetectionAsync());
                WindowScene.PumpUntil(() => births.Any(b => b.Name == "Ready → Scanning"), "the scan's moment");
                Assert.NotEmpty(MotionDiagnostics.ActiveMoments);

                // ... and the click into the game (Windows deactivates the window) stops it at once, settled on its rest pose
                scene.Activate(false);
                Assert.False(scene.Policy.ShouldAnimate);
                Assert.True(MotionDiagnostics.ActiveMoments.Count == 0, "Still playing: " + string.Join(", ", MotionDiagnostics.ActiveMoments));
                AssertQuiet(root);
                Assert.False(scene.Window.CurrentMoment!.IsPlaying);

                // from now on every screen the window presents behind the game is checked as it is presented
                var seen = new List<ScreenState>();
                var moved = new List<string>();
                scene.ViewModel.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName != nameof(MainViewModel.StatusScreen) || scene.Window.PresentedScreen is not { } shown)
                    {
                        return;
                    }

                    seen.Add(shown.State);
                    IEnumerable<string> busy = MotionDiagnostics.ActiveMoments.Concat(MotionDiagnostics.RunningLoops).Concat(MotionDiagnostics.FindAnimatedObjects(root));
                    if (busy.Any())
                    {
                        moved.Add(shown.State + ": " + string.Join(", ", busy));
                    }
                };

                // the map is found (no landing, nobody would see it: the sound plays at once), the preset switches and
                // ReShade confirms it
                h.Ocr.Text = "COAL TOWER\nMACMILLAN ESTATE";
                WindowScene.PumpUntil(() => h.States.State == AppState.PresetActive, "the switch");
                h.ReshadeSaves(scene.Preset("11.STX.Macmillan.ini"));
                WindowScene.PumpUntil(() => scene.Window.PresentedScreen is { State: ScreenState.Active, Tail: PresetTail.Confirmed }, "confirmed by ReShade");
                Assert.Single(h.Sounds.Played);
                Assert.False(scene.Window.IsLanding);

                // F9 back to Global (handled off the UI thread, as in the app), then F8 on the next loading screen
                _ = Task.Run(() => h.GlobalPresetHotkey.Press());
                WindowScene.PumpUntil(() => scene.Window.PresentedScreen?.State == ScreenState.Ready, "the switch to Global");
                h.Ocr.Text = string.Empty;
                _ = Task.Run(() => h.Controller.TriggerDetectionAsync());
                WindowScene.PumpUntil(() => scene.Window.PresentedScreen?.State == ScreenState.Scanning, "the next scan");

                Assert.Contains(ScreenState.Active, seen);
                Assert.Contains(ScreenState.Ready, seen);
                Assert.True(moved.Count == 0, "Moved behind the game: " + string.Join(" | ", moved));
                CampfireTestHost.Wait(TimeSpan.FromMilliseconds(200));
                Assert.Empty(MotionDiagnostics.ActiveMoments);
                AssertQuiet(root);
            }
            finally
            {
                scene.Dispose();
            }
        }, timeoutSeconds: 180);
    }

    [Fact]
    public void TheOverride_IsAQuickUserDrivenSwap_NotTheDetectionStory()
    {
        CampfireTestHost.RunSta(() =>
        {
            WindowScene scene = WindowScene.Create(Path.Combine(_dir, "override"), "ready");
            try
            {
                MainViewModel vm = scene.ViewModel;
                scene.Activate(true);
                WindowScene.PumpUntil(() => MotionDiagnostics.ActiveMoments.Count == 0, "the window at rest", 8000);
                StateMoment? before = scene.Window.CurrentMoment;
                string[] story = { "MapPlate.Reveal", "MapPlate.Sink", "StatusView.detection", "FogBackdrop.flare", "EntityLegs.flex", "StatusView.heroGlow" };

                // "Apply override": the clearing becomes the realm's map with a blur-bridged swap, not the fog reveal
                vm.SelectedOverrideRealm = "MacMillan Estate";
                vm.ApplyOverrideCommand.Execute(null);
                Assert.Equal(ScreenState.Override, scene.Window.PresentedScreen!.State);
                Assert.True(scene.Window.PresentedScreen.Plate.IsMap);
                IReadOnlyList<string> playing = MotionDiagnostics.ActiveMoments;
                Assert.Contains("MapPlate.Swap", playing);
                Assert.True(!playing.Intersect(story).Any(), "The detection story played: " + string.Join(", ", playing));
                Assert.Same(before, scene.Window.CurrentMoment); // no state moment of its own: nothing was interrupted
                Assert.False(before?.WasInterrupted ?? false);

                // over within half a second (the caption lands last, as in the prototype's quick swap), not 1.5-2 s
                CampfireTestHost.Wait(TimeSpan.FromMilliseconds(500));
                string[] swap = { "MapPlate.Swap", "MapPlate.caption", "CrossfadeText.BlurRise", "CrossfadeText.BlurSoft", "StatusView.glide", "StatusView.tail", "FogBackdrop.map", "FogBackdrop.ember" };
                Assert.True(!MotionDiagnostics.ActiveMoments.Intersect(swap).Any(), "Still playing: " + string.Join(", ", MotionDiagnostics.ActiveMoments));

                // "Return to automatic": the map goes with the same quick swap, not the sink a state change plays
                vm.ReturnToAutoCommand.Execute(null);
                Assert.NotEqual(ScreenState.Override, scene.Window.PresentedScreen!.State);
                playing = MotionDiagnostics.ActiveMoments;
                Assert.Contains("MapPlate.Swap", playing);
                Assert.True(!playing.Intersect(story).Any(), "The detection story played: " + string.Join(", ", playing));
                Assert.Same(before, scene.Window.CurrentMoment);
            }
            finally
            {
                scene.Dispose();
            }
        }, timeoutSeconds: 180);
    }

    [Fact]
    public void APlateThatGrowsWithTheGrid_KeepsItsBottomEdge_WhileItsTopGlides()
    {
        CampfireTestHost.RunSta(() =>
        {
            WindowScene scene = WindowScene.Create(Path.Combine(_dir, "grow"), "scanning");
            try
            {
                scene.Activate(true);
                WindowScene.PumpUntil(() => MotionDiagnostics.ActiveMoments.Count == 0, "the scan at rest", 8000);
                StatusView status = Status(scene);
                var page = (FrameworkElement)status.FindName("Page");
                MapPlate plate = status.MapPlate;
                (double oldTop, double oldBottom, _, _) = DrawnFrame(plate, page);

                // the pause: the hero gets shorter, the grid takes the height and the plate grows with it; every clock is
                // held at the instant the change is born
                bool born = false;
                scene.ViewModel.PropertyChanged += (_, e) =>
                {
                    born |= e.PropertyName == nameof(MainViewModel.StatusScreen) && scene.Window.PresentedScreen?.State == ScreenState.Paused;
                    if (born)
                    {
                        MotionDiagnostics.FreezeAllAt(TimeSpan.Zero, scene.Window.CurrentMoment);
                    }
                };
                scene.Harness.Focused = false;
                scene.Harness.Uncovered = false;
                WindowScene.PumpUntil(() => born, "the pause");
                MotionDiagnostics.FreezeAllAt(TimeSpan.Zero, scene.Window.CurrentMoment);
                CampfireTestHost.Pump();
                Assert.True(plate.Frame.ActualHeight > oldBottom - oldTop + 1, $"The plate did not grow ({oldBottom - oldTop:0.#} -> {plate.Frame.ActualHeight:0.#}).");

                // at the start of the glide it is drawn where it was: the bottom edge (border, caption) whole and in place,
                // the growth clipped off at the top
                (double top, double bottom, double clipTop, double clipBottom) = DrawnFrame(plate, page);
                Assert.Equal(oldBottom, bottom, 0.75);
                Assert.Equal(oldTop, Math.Max(top, clipTop), 0.75);
                Assert.True(clipBottom >= bottom + 1, $"The clip cuts the plate's bottom edge (clip {clipBottom:0.#}, frame {bottom:0.#}).");

                // at its end the frame is where it belongs, whole
                MotionDiagnostics.FreezeAllAt(TimeSpan.FromMilliseconds(300), scene.Window.CurrentMoment);
                (top, bottom, clipTop, clipBottom) = DrawnFrame(plate, page);
                Assert.Equal(plate.Frame.ActualHeight, bottom - top, 0.5);
                Assert.True(clipTop <= top + 0.5 && clipBottom >= bottom, "The frame is still clipped at the end of the glide.");
                Assert.True(bottom - top > oldBottom - oldTop + 1);
            }
            finally
            {
                scene.Dispose();
            }
        });
    }

    [Fact]
    public void TheSelectionBar_RetargetsFromWhereItIs_AndPresetsOpensWithItInPlace()
    {
        CampfireTestHost.RunSta(() =>
        {
            WindowScene scene = WindowScene.Create(Path.Combine(_dir, "bar"), "active");
            try
            {
                MainWindow window = scene.Window;
                MainViewModel vm = scene.ViewModel;
                scene.Activate(true);
                var view = (PresetsView)window.FindName("PresetsPage");
                var list = (ListBox)view.FindName("RealmList");
                list.ApplyTemplate();
                var bar = (System.Windows.Shapes.Rectangle)list.Template.FindName("Indicator", list);
                TranslateTransform BarMove() => ((TransformGroup)bar.RenderTransform).Children.OfType<TranslateTransform>().Single();
                double TargetOf(RealmRow row) => ((ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(row)).TranslatePoint(new Point(0, 0), (Canvas)bar.Parent).Y + 8;

                // shown from code (a detection selected MacMillan Estate while the page was hidden): the first frame already
                // has the row in the middle of the list and its bar on it (no pass at Loaded priority needed)
                RealmRow selected = vm.SelectedRealmRow!;
                Assert.Equal("MacMillan Estate", selected.RealmName);
                vm.SelectedTab = AppTab.Presets;
                var item = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(selected);
                Assert.True(item.IsVisible && InView(list, item));
                Assert.Equal(1, bar.Opacity, 2);
                Assert.Equal(TargetOf(selected), BarMove().Y, 1.0);
                scene.Settle();

                // a click glides the bar; a second click mid-glide glides on from where the bar is, never via the first target
                RealmRow ormond = vm.RealmRows.First(r => r.RealmName == "Ormond");
                RealmRow raccoon = vm.RealmRows.First(r => r.RealmName == "Raccoon City");
                double start = BarMove().Y;
                Press(list, down: true);
                vm.SelectRow(ormond);
                Press(list, down: false);
                CampfireTestHost.Pump();
                MotionDiagnostics.FreezeAllAt(TimeSpan.FromMilliseconds(70));
                double mid = BarMove().Y;
                Assert.True(Math.Abs(mid - start) > 1 && Math.Abs(mid - TargetOf(ormond)) > 1, $"Not mid-glide at 70 ms ({start:0.#} -> {mid:0.#} -> {TargetOf(ormond):0.#}).");
                Press(list, down: true);
                vm.SelectRow(raccoon);
                Press(list, down: false);
                CampfireTestHost.Pump();
                MotionDiagnostics.FreezeAllAt(TimeSpan.Zero);
                Assert.Equal(mid, BarMove().Y, 1.0);
                MotionDiagnostics.FreezeAllAt(TimeSpan.FromMilliseconds(220));
                Assert.Equal(TargetOf(raccoon), BarMove().Y, 1.0);
            }
            finally
            {
                scene.Dispose();
            }
        });
    }

    [Fact]
    public void AHeartbeatThatIsNotDrawn_DoesNotBeat_AndSlowLoopsAskForFewerFrames()
    {
        CampfireTestHost.RunSta(() =>
        {
            foreach (string state in new[] { "active", "waitingFocus" })
            {
                WindowScene scene = WindowScene.Create(Path.Combine(_dir, "loops-" + state), state);
                try
                {
                    scene.Activate(true);
                    CampfireTestHost.Wait(TimeSpan.FromMilliseconds(100));
                    IReadOnlyList<(string Name, int? FrameRate)> loops = MotionDiagnostics.RunningLoopRates;
                    Assert.NotEmpty(loops);
                    if (state == "active")
                    {
                        // the pending line's heartbeat dot is collapsed outside "waiting for focus": it does not run
                        Assert.DoesNotContain(loops, l => l.Name == "StatusDot.heartbeat");
                    }
                    else
                    {
                        Assert.Contains(loops, l => l.Name == "StatusDot.heartbeat");
                    }

                    // near-still drift and sway, the flicker and the heartbeats ask for 20-30 frames a second; only the
                    // skill check's spinning needle would take every frame
                    Assert.All(loops.Where(l => l.Name != "SkillCheck.spin"), l => Assert.True(l.FrameRate is > 0 and <= AmbientLoop.LivelyFrameRate, l.Name + " asks for " + (l.FrameRate?.ToString() ?? "every") + " frame(s)"));
                }
                finally
                {
                    scene.Dispose();
                }
            }
        }, timeoutSeconds: 180);
    }

    [Fact]
    public void EveryButton_PressesIn_PerTheTheme()
    {
        CampfireTestHost.RunSta(() =>
        {
            WindowScene scene = WindowScene.Create(Path.Combine(_dir, "press"), "active");
            try
            {
                MainWindow window = scene.Window;
                scene.Activate(true);
                var checkedAny = new List<string>();
                foreach (AppTab tab in new[] { AppTab.Status, AppTab.Presets, AppTab.Settings })
                {
                    scene.ViewModel.SelectedTab = tab;
                    scene.Settle();
                    foreach (ButtonBase button in Descendants<ButtonBase>((DependencyObject)window.Content).Where(b => b.IsVisible && IsPressable(b)))
                    {
                        double scale = PressFeedback.GetScale(button);
                        string name = Describe(button);
                        Assert.True(scale is 0.97 or 0.98, $"{name} has no press feedback (scale {scale}).");
                        checkedAny.Add(name);
                    }
                }

                Assert.True(checkedAny.Count > 20, "Buttons checked: " + checkedAny.Count);

                // a press scales a button to 0.97 in 140 ms (ease-out), and back (on the page shown: a hidden page is still)
                scene.ViewModel.SelectedTab = AppTab.Status;
                CampfireTestHost.Pump();
                var apply = (Button)Status(scene).FindName("ApplyOverrideButton");
                PressFeedback.Animate(apply, pressed: true);
                var press = (ScaleTransform)apply.RenderTransform;
                CampfireTestHost.Wait(TimeSpan.FromMilliseconds(260));
                Assert.Equal(0.97, press.ScaleX, 2);
                PressFeedback.Animate(apply, pressed: false);
                CampfireTestHost.Wait(TimeSpan.FromMilliseconds(260));
                Assert.Equal(1, press.ScaleX, 2);
            }
            finally
            {
                scene.Dispose();
            }
        });
    }

    private static List<(string Name, IReadOnlyList<string> Parts, StateMoment Moment)> RecordBirths(WindowScene scene)
    {
        // (subscribed after the window: it runs right after the window has presented the change, in the same dispatcher
        // operation, so even the shortest parts are still playing)
        var births = new List<(string, IReadOnlyList<string>, StateMoment)>();
        StateMoment? last = scene.Window.CurrentMoment;
        scene.ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.StatusScreen) && scene.Window.CurrentMoment is { } m && !ReferenceEquals(m, last))
            {
                last = m;
                births.Add((m.Name, m.MomentNames, m));
            }
        };
        return births;
    }

    private static void AssertQuiet(DependencyObject root)
    {
        IReadOnlyList<string> animated = MotionDiagnostics.FindAnimatedObjects(root);
        Assert.True(animated.Count == 0, "Still animated: " + string.Join(", ", animated));
        Assert.True(MotionDiagnostics.RunningLoops.Count == 0, "Loops: " + string.Join(", ", MotionDiagnostics.RunningLoops));
    }

    /// <summary>
    /// A left-button press or release on <paramref name="element"/>, as the input system routes it: PreviewMouseDown /
    /// Up tunnels from the window to the element, and on the way every element raises its own PreviewMouseLeftButton*
    /// (WPF's class handlers do that). In-process routed events only: no input reaches Windows.
    /// </summary>
    private static void Press(UIElement element, bool down)
    {
        element.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
        {
            RoutedEvent = down ? UIElement.PreviewMouseDownEvent : UIElement.PreviewMouseUpEvent,
        });
    }

    private static StatusView Status(WindowScene scene) => (StatusView)scene.Window.FindName("StatusPage");

    /// <summary>Where the plate's frame is drawn on the page (its render transforms included) and where the plate's clip
    /// (if any) lets it show, top and bottom.</summary>
    private static (double Top, double Bottom, double ClipTop, double ClipBottom) DrawnFrame(MapPlate plate, FrameworkElement page)
    {
        double top = plate.Frame.TranslatePoint(new Point(0, 0), page).Y;
        double bottom = top + plate.Frame.ActualHeight;
        if (plate.Clip is not RectangleGeometry clip)
        {
            return (top, bottom, double.NegativeInfinity, double.PositiveInfinity);
        }

        double dy = (clip.Transform as TranslateTransform)?.Y ?? 0;
        return (top, bottom, plate.TranslatePoint(new Point(0, clip.Rect.Top + dy), page).Y, plate.TranslatePoint(new Point(0, clip.Rect.Bottom + dy), page).Y);
    }

    /// <summary>The row is (at least partly) inside the realm list's viewport.</summary>
    private static bool InView(ListBox list, ListBoxItem item)
    {
        list.ApplyTemplate();
        if (!item.IsVisible || list.Template.FindName("RowsScroller", list) is not ScrollViewer scroller)
        {
            return false;
        }

        double top = item.TranslatePoint(new Point(0, 0), scroller).Y;
        return top + item.ActualHeight > 0 && top < scroller.ViewportHeight;
    }

    private static int Count<T>(DependencyObject root) => Descendants<T>(root).Count();

    private static IEnumerable<T> Descendants<T>(DependencyObject root)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                yield return match;
            }

            foreach (T inner in Descendants<T>(child))
            {
                yield return inner;
            }
        }
    }

    // Parts of other controls' templates are not "buttons" the user presses as such (a combo box's toggle, a scroll bar's
    // arrows and pages, a slider's tracks) and the window's own chrome follows its own style (checked separately).
    private static bool IsPressable(ButtonBase button) =>
        button is not RepeatButton && button.TemplatedParent is not (ComboBox or ScrollBar or Slider or Track) && button is not CheckBox;

    private static string Describe(FrameworkElement element) =>
        element.GetType().Name + (string.IsNullOrEmpty(element.Name) ? string.Empty : "#" + element.Name) +
        (element is ContentControl { Content: string text } ? " '" + text + "'" : string.Empty);
}
