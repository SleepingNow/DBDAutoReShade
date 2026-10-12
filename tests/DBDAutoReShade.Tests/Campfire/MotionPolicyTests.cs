using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using DBDAutoReShade.App.Controls;
using DBDAutoReShade.App.Media;

namespace DBDAutoReShade.Tests.Campfire;

/// <summary>
/// The motion policy (the app runs alongside the game): ambient loops run only while the window is shown AND active
/// and animations are on; one-shots become instant when nothing may animate; reduced motion keeps fades only.
/// The proof is the verification hook: a tree whose window is deactivated, or whose animations are off, has no
/// animation clock attached anywhere.
/// </summary>
public sealed class MotionPolicyTests
{
    private const int WmActivate = 0x0006;

    [Theory]
    [InlineData(true, true, true, false, true, true, true)]
    [InlineData(true, true, false, false, false, false, false)] // behind a fullscreen game: nothing moves, changes are instant
    [InlineData(true, false, true, false, false, false, false)] // minimized / hidden
    [InlineData(false, true, true, false, false, false, false)] // the "Play animations" setting is off
    [InlineData(true, true, true, true, true, false, true)] // Windows animations off: fades only, no ambient
    public void DerivedFlags(bool enabled, bool visible, bool active, bool reduced, bool animate, bool ambient, bool landing)
    {
        var policy = new MotionPolicy { AnimationsEnabled = enabled, WindowVisible = visible, WindowActive = active, ReducedMotionOverride = reduced };
        Assert.Equal(animate, policy.ShouldAnimate);
        Assert.Equal(ambient, policy.ShouldRunAmbient);
        Assert.Equal(landing, policy.CanPresentLanding);
        Assert.Equal(reduced, policy.ReducedMotion);
    }

    [Fact]
    public void Changes_AreAnnounced_OncePerRealChange()
    {
        var policy = new MotionPolicy();
        int changes = 0;
        policy.Changed += (_, _) => changes++;
        policy.WindowActive = false;
        policy.WindowActive = false;
        policy.AnimationsEnabled = false;
        policy.ReducedMotionOverride = true;
        Assert.Equal(3, changes);
        Assert.False(policy.SystemReducedMotion); // a private policy ignores Windows
    }

    [Fact]
    public void TheSystemSetting_IsReread_WhenWindowsReportsASettingsChange()
    {
        bool reduced = false;
        MotionPolicy policy = MotionPolicy.FollowingSystemSetting(() => reduced);
        int changes = 0;
        policy.Changed += (_, _) => changes++;
        Assert.False(policy.ReducedMotion);

        // WM_SETTINGCHANGE for another setting: not re-read
        reduced = true;
        RaiseSystemParametersChanged("MenuDropAlignment");
        Assert.False(policy.SystemReducedMotion);
        Assert.Equal(0, changes);

        // "Show animations in Windows" turned off
        RaiseSystemParametersChanged(nameof(SystemParameters.ClientAreaAnimation));
        Assert.True(policy.SystemReducedMotion);
        Assert.True(policy.ReducedMotion);
        Assert.False(policy.ShouldRunAmbient);
        Assert.Equal(1, changes);

        // a change of everything (no property name) re-reads it too
        reduced = false;
        RaiseSystemParametersChanged(null);
        Assert.False(policy.ReducedMotion);
        Assert.Equal(2, changes);

        // the user's override wins over Windows
        reduced = true;
        policy.RefreshSystemSetting();
        Assert.True(policy.ReducedMotion);
        policy.ReducedMotionOverride = false;
        Assert.False(policy.ReducedMotion);

        // the process-wide policy reads Windows' own setting; a private policy never does
        MotionPolicy.Current.RefreshSystemSetting();
        Assert.Equal(!SystemParameters.ClientAreaAnimation, MotionPolicy.Current.SystemReducedMotion);
        var plain = new MotionPolicy();
        plain.RefreshSystemSetting();
        Assert.False(plain.SystemReducedMotion);
    }

    [Fact]
    public void Policy_IsInheritedByASubtree()
    {
        CampfireTestHost.RunSta(() =>
        {
            var policy = new MotionPolicy();
            var root = new Grid();
            var child = new Border();
            root.Children.Add(child);
            Assert.Same(MotionPolicy.Current, MotionPolicy.For(child));
            MotionPolicy.SetPolicy(root, policy);
            Assert.Same(policy, MotionPolicy.For(child));
        });
    }

    [Fact]
    public void AControl_FollowsAPolicySwap_InItsAncestors()
    {
        CampfireTestHost.RunSta(() =>
        {
            var outerPolicy = new MotionPolicy();
            var innerPolicy = new MotionPolicy { WindowActive = false };
            var legs = new EntityLegs { Width = 640, Height = 700 };
            var inner = new Grid();
            inner.Children.Add(legs);
            var outer = new Grid();
            outer.Children.Add(inner);
            Window window = CampfireTestHost.Show(outer, outerPolicy, 700, 760);
            try
            {
                CampfireTestHost.Wait(TimeSpan.FromMilliseconds(150));
                Assert.True(legs.IsSwaying);

                // a subtree takes its own (inactive) policy: the legs follow it at once
                MotionPolicy.SetPolicy(inner, innerPolicy);
                CampfireTestHost.Pump();
                Assert.False(legs.IsSwaying);

                // ...and listen to it (and no longer to the old one)
                innerPolicy.WindowActive = true;
                CampfireTestHost.Pump();
                Assert.True(legs.IsSwaying);
                outerPolicy.WindowActive = false;
                CampfireTestHost.Pump();
                Assert.True(legs.IsSwaying);

                // re-parented under the (now inactive) outer policy
                inner.Children.Remove(legs);
                outer.Children.Add(legs);
                CampfireTestHost.Pump();
                Assert.Same(outerPolicy, MotionPolicy.For(legs));
                Assert.False(legs.IsSwaying);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void NoClockRuns_WhenTheWindowIsDeactivated_OrAnimationsAreOff()
    {
        CampfireTestHost.RunSta(() =>
        {
            var policy = new MotionPolicy();
            (Grid root, Scene scene) = BuildAmbientScene();
            Window window = CampfireTestHost.Show(root, policy, 1280, 800);
            try
            {
                scene.Plate.SkillCheck.State = SkillCheckState.Scanning;
                scene.Ring.Start(); // the Presets Test countdown (the user is about to switch to the game)
                CampfireTestHost.Wait(TimeSpan.FromMilliseconds(400));

                // active and shown: the ambient loops run
                Assert.True(scene.Backdrop.IsDrifting, "fog drifts");
                Assert.True(scene.Bank.IsDrifting, "fog bank drifts");
                Assert.True(scene.Legs.IsSwaying, "legs sway");
                Assert.True(scene.Dot.IsHeartbeatRunning, "heartbeat");
                Assert.True(scene.Plate.IsHeartbeatRunning, "plate heartbeat");
                Assert.True(scene.Plate.SkillCheck.IsSpinning, "needle spins");
                Assert.True(scene.Fire.IsAmbientRunning, "fire flickers");
                Assert.Equal(CountdownMode.Drain, scene.Ring.Mode);
                Assert.NotEmpty(MotionDiagnostics.RunningLoops);
                Assert.NotEmpty(MotionDiagnostics.FindAnimatedObjects(root));

                // behind the game: everything holds still, no clock is attached anywhere
                double angle = scene.Plate.SkillCheck.NeedleAngle;
                policy.WindowActive = false;
                CampfireTestHost.Wait(TimeSpan.FromMilliseconds(300));
                AssertQuiet(root);
                Assert.Equal(angle, scene.Plate.SkillCheck.NeedleAngle, 0); // it paused in place (no reset to 12 o'clock)
                Assert.Equal(CountdownMode.Held, scene.Ring.Mode);
                Assert.True(scene.Ring.IsRunning); // the countdown itself keeps going (its needle steps with the ticker)

                // back in front: the loops resume
                policy.WindowActive = true;
                CampfireTestHost.Wait(TimeSpan.FromMilliseconds(200));
                Assert.True(scene.Plate.SkillCheck.IsSpinning);
                Assert.Equal(CountdownMode.Drain, scene.Ring.Mode);
                Assert.NotEmpty(MotionDiagnostics.FindAnimatedObjects(root));

                // minimized
                policy.WindowVisible = false;
                CampfireTestHost.Wait(TimeSpan.FromMilliseconds(200));
                AssertQuiet(root);
                policy.WindowVisible = true;

                // the "Play animations" setting off: nothing animates, even in front
                policy.AnimationsEnabled = false;
                CampfireTestHost.Wait(TimeSpan.FromMilliseconds(300));
                AssertQuiet(root);
                policy.AnimationsEnabled = true;

                // Windows animations off: no ambient movement either
                policy.ReducedMotionOverride = true;
                CampfireTestHost.Wait(TimeSpan.FromMilliseconds(300));
                AssertQuiet(root);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void AttachWindow_FollowsActivation_Minimize_AndHide_NoClockRunsBehindAnotherWindow()
    {
        CampfireTestHost.RunSta(() =>
        {
            var policy = new MotionPolicy();
            (Grid root, Scene scene) = BuildAmbientScene();
            Window main = CampfireTestHost.Show(root, policy, 1280, 800);
            Window game = CampfireTestHost.Show(new Grid(), new MotionPolicy(), 200, 100);
            IDisposable token = policy.AttachWindow(main);
            try
            {
                Assert.True(policy.WindowVisible);

                // the main window is the foreground window: the loops run
                SimulateActivation(main, true);
                Assert.True(main.IsActive);
                Assert.True(policy.WindowActive);
                Assert.True(policy.ShouldRunAmbient);
                scene.Plate.SkillCheck.State = SkillCheckState.Scanning;
                scene.Ring.Start();
                CampfireTestHost.Wait(TimeSpan.FromMilliseconds(250));
                Assert.True(scene.Legs.IsSwaying);
                Assert.NotEmpty(MotionDiagnostics.FindAnimatedObjects(root));

                // another window (the game) comes to the front: Windows deactivates the main window
                SimulateActivation(main, false);
                SimulateActivation(game, true);
                Assert.False(main.IsActive);
                Assert.False(policy.WindowActive);
                Assert.False(policy.ShouldRunAmbient);
                Assert.False(policy.CanPresentLanding);
                CampfireTestHost.Wait(TimeSpan.FromMilliseconds(300));
                AssertQuiet(root);

                // back in front
                SimulateActivation(game, false);
                SimulateActivation(main, true);
                CampfireTestHost.Wait(TimeSpan.FromMilliseconds(200));
                Assert.True(policy.ShouldRunAmbient);
                Assert.NotEmpty(MotionDiagnostics.FindAnimatedObjects(root));

                // minimized
                main.WindowState = WindowState.Minimized;
                CampfireTestHost.Pump();
                Assert.False(policy.WindowVisible);
                Assert.False(policy.ShouldAnimate);
                CampfireTestHost.Wait(TimeSpan.FromMilliseconds(300));
                AssertQuiet(root);
                main.WindowState = WindowState.Normal;
                CampfireTestHost.Pump();
                Assert.True(policy.WindowVisible);

                // hidden (e.g. to the tray)
                main.Hide();
                CampfireTestHost.Pump();
                Assert.False(policy.WindowVisible);
                CampfireTestHost.Wait(TimeSpan.FromMilliseconds(300));
                AssertQuiet(root);
                main.Show();
                CampfireTestHost.Pump();
                Assert.True(policy.WindowVisible);

                // detached: the window no longer drives the policy
                token.Dispose();
                main.Hide();
                CampfireTestHost.Pump();
                Assert.True(policy.WindowVisible);
            }
            finally
            {
                token.Dispose();
                main.Close();
                game.Close();
            }
        });
    }

    [Fact]
    public void OneShots_AreInstant_WhenNothingMayAnimate()
    {
        CampfireTestHost.RunSta(() =>
        {
            var policy = new MotionPolicy { AnimationsEnabled = false };
            (Grid root, Scene scene) = BuildAmbientScene();
            MotionPolicy.SetPolicy(root, policy);
            CampfireTestHost.Layout(root, 1280, 800);
            scene.Fire.DieOut();
            scene.Plate.SetScene(PlateScene.Map("MacMillan Estate", "Coal Tower"), PlateTransition.Reveal);
            scene.Plate.Confirm();
            scene.Legs.RimRun();
            scene.Legs.Flex();
            scene.Backdrop.Flare(TimeSpan.Zero, TimeSpan.FromMilliseconds(700));
            scene.Backdrop.EmberLevel = 1;
            scene.Dot.Pop();
            scene.Ring.Start();
            Assert.Equal(CountdownMode.Still, scene.Ring.Mode);
            Assert.True(MotionDiagnostics.ActiveMoments.Count == 0, "Moments: " + string.Join(", ", MotionDiagnostics.ActiveMoments));
            AssertQuiet(root);
            Assert.Equal(PlateTone.Live, scene.Plate.Tone);
            Assert.NotNull(scene.Plate.Picture);
        });
    }

    [Fact]
    public void ReducedMotion_KeepsFadesOnly()
    {
        CampfireTestHost.RunSta(() =>
        {
            var policy = new MotionPolicy { ReducedMotionOverride = true };
            (Grid root, Scene scene) = BuildAmbientScene();
            Window window = CampfireTestHost.Show(root, policy, 1280, 800);
            try
            {
                scene.Plate.SkillCheck.State = SkillCheckState.Scanning;
                CampfireTestHost.Pump();
                Assert.False(scene.Plate.SkillCheck.IsSpinning); // the needle steps once per read instead
                Assert.Equal(TimeSpan.Zero, scene.Plate.SkillCheck.ComputeHitDelay());

                // every moment at once: only opacity / colour may change, nothing moves, scales or blurs
                Assert.Equal(TimeSpan.Zero, scene.Plate.LandGreat("Coal Tower"));
                scene.Plate.SetScene(PlateScene.Map("MacMillan Estate", "Coal Tower"), PlateTransition.Reveal);
                scene.Plate.Confirm();
                scene.Legs.RimRun();
                scene.Legs.Flex();
                scene.Legs.Lean(true);
                scene.Backdrop.Flare(TimeSpan.Zero, TimeSpan.FromMilliseconds(700));
                scene.Dot.Catch();
                scene.Ring.Start();
                Assert.Equal(CountdownMode.Still, scene.Ring.Mode);
                IReadOnlyList<string> animated = MotionDiagnostics.FindAnimatedObjects(root);
                Assert.NotEmpty(animated); // the fades
                // (the frame's glow, a box-shadow in the prototype, is light: it may still fade in)
                Assert.DoesNotContain(animated, a => a.Contains("Transform", StringComparison.Ordinal) || a.Contains("BlurEffect", StringComparison.Ordinal));
                Assert.Empty(MotionDiagnostics.RunningLoops);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void AHaltedLoop_LeavesAPropertyThatANewerMomentOwns_ToThatMoment()
    {
        CampfireTestHost.RunSta(() =>
        {
            var scale = new ScaleTransform(1, 1);
            var host = new Border { Width = 40, Height = 40, Background = Brushes.White, RenderTransform = scale };
            Window window = CampfireTestHost.Show(host, new MotionPolicy(), 80, 80);
            try
            {
                var loop = new AmbientLoop(host, "test.loop", () => new (DependencyObject, DependencyProperty, AnimationTimeline)[]
                {
                    (scale, ScaleTransform.ScaleXProperty, Anim.Loop(400, new[] { new Kf(0, 1), new Kf(200, 2), new Kf(400, 1) })),
                    (host, UIElement.OpacityProperty, Anim.Loop(400, new[] { new Kf(0, 1), new Kf(200, 0.4), new Kf(400, 1) })),
                });
                loop.Run();
                CampfireTestHost.Wait(TimeSpan.FromMilliseconds(120));

                // a one-shot takes the scale over and animates into its rest pose (1)
                scale.ScaleX = 1;
                var pop = new MotionMoment(host, "test.pop").Add(scale, ScaleTransform.ScaleXProperty, Anim.FromTo(0.3, 1, 300));
                pop.Begin();
                CampfireTestHost.Wait(TimeSpan.FromMilliseconds(40));

                // the window deactivates mid-pop: the loop holds what it still owns, the pop keeps its own
                loop.Halt();
                Assert.False(loop.IsRunning);
                Assert.True(pop.IsActive);
                Assert.True(host.ReadLocalValue(UIElement.OpacityProperty) is double held && held < 1, "the loop's opacity is held in place");
                CampfireTestHost.Wait(TimeSpan.FromMilliseconds(450));
                Assert.False(pop.IsActive);
                Assert.Equal(1, scale.ScaleX); // the pop's rest pose, not a mid-pop value
                Assert.Empty(MotionDiagnostics.FindAnimatedObjects(host));

                // Reset restores the loop's resting values
                loop.Reset();
                Assert.Equal(1, host.Opacity);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void AStatusDotPop_EndsAtRest_WhenTheHeartbeatHaltsMidPop()
    {
        CampfireTestHost.RunSta(() =>
        {
            var policy = new MotionPolicy();
            var dot = new StatusDot { Kind = DotKind.Ring, IsHeartbeat = true, Diameter = 8 };
            Window window = CampfireTestHost.Show(CampfireTestHost.Frame(dot, 40, 40), policy, 40, 40);
            try
            {
                CampfireTestHost.Wait(TimeSpan.FromMilliseconds(1100)); // into the first beat
                Assert.True(dot.IsHeartbeatRunning);
                dot.Pop();
                CampfireTestHost.Wait(TimeSpan.FromMilliseconds(40));
                policy.WindowActive = false; // the heartbeat halts in place, mid-pop
                CampfireTestHost.Wait(TimeSpan.FromMilliseconds(600));
                var core = (Ellipse)((Grid)VisualTreeHelper.GetChild(dot, 0)).Children[1];
                var scale = (ScaleTransform)core.RenderTransform;
                Assert.Equal(1, scale.ScaleX, 3);
                Assert.Equal(1, scale.ScaleY, 3);
                Assert.Equal(1, core.Opacity, 3);
                Assert.Empty(MotionDiagnostics.FindAnimatedObjects(dot));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void Controls_CanBeCollected_AfterLeavingTheTree_OrWhenNeverShown()
    {
        CampfireTestHost.RunSta(() =>
        {
            var host = new Grid();
            var plate = new MapPlate { Width = 640, Height = 320, Scene = PlateScene.Lobby(FireState.Burning, "Looking for the map name", "Global / Menu stays on until a realm is read.") };
            host.Children.Add(plate);
            Window window = CampfireTestHost.Show(host, new MotionPolicy(), 1280, 800);
            try
            {
                List<WeakReference> refs = ShowAndRemove(host);
                refs.AddRange(CreateNeverShown());

                // a match: the plate's lobby campfire gives way to the map (the plate stays on screen)
                refs.Add(SwapPlateScene(plate));
                CampfireTestHost.Wait(TimeSpan.FromMilliseconds(1600)); // every moment has finished
                for (int i = 0; i < 3; i++)
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    CampfireTestHost.Pump();
                }

                GC.Collect();
                string[] alive = refs.Where(r => r.IsAlive).Select(r => r.Target?.GetType().Name ?? "?").ToArray();
                Assert.True(alive.Length == 0, $"{alive.Length} of {refs.Count} still alive: {string.Join(", ", alive)}");
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static void AssertQuiet(DependencyObject root)
    {
        IReadOnlyList<string> animated = MotionDiagnostics.FindAnimatedObjects(root);
        Assert.True(animated.Count == 0, "Still animated: " + string.Join(", ", animated));
        Assert.Empty(MotionDiagnostics.RunningLoops);
    }

    private static (Grid Root, Scene Scene) BuildAmbientScene()
    {
        var root = new Grid { Width = 1280, Height = 800 };
        var backdrop = new FogBackdrop { Layer = FogLayer.Backdrop, MapName = "Coal Tower", EmberLevel = 0.5 };
        var legs = new EntityLegs { Width = 640, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };
        var bank = new FogBackdrop { Layer = FogLayer.Bank };
        var plate = new MapPlate { Width = 640, Height = 320, IsWaitingForFocus = true, Scene = PlateScene.Lobby(FireState.Burning, "Looking for the map name", "Global / Menu stays on until a realm is read.") };
        var dot = new StatusDot { Kind = DotKind.Ring, IsHeartbeat = true, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom };
        var ring = new CountdownRing { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom };
        root.Children.Add(backdrop);
        root.Children.Add(legs);
        root.Children.Add(bank);
        root.Children.Add(plate);
        root.Children.Add(dot);
        root.Children.Add(ring);
        return (root, new Scene(backdrop, bank, legs, plate, dot, plate.Campfire!, ring));
    }

    // (separate, non-inlined methods: no stack slot of the test keeps the controls alive)
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static List<WeakReference> ShowAndRemove(Grid host)
    {
        var check = new SkillCheck();
        FrameworkElement[] controls =
        {
            new CampfireScene { IsLive = true },
            new MapPlate { Width = 640, Height = 320, Scene = PlateScene.Map("MacMillan Estate", "Coal Tower"), IsWaitingForFocus = true },
            check,
            new EntityLegs { Width = 640 },
            new FogBackdrop { Layer = FogLayer.Backdrop, MapName = "Coal Tower" },
            new FogBackdrop { Layer = FogLayer.Bank },
            new Keycap { Text = "F8", IsGlowing = true },
            new StatusDot { Kind = DotKind.Ring, IsHeartbeat = true },
            new MapStrip { MapNames = new[] { "Coal Tower", "Shelter Woods" }, IsFogged = true },
            new MapPicture { MapName = "Coal Tower" },
            new RestartBanner { Text = "Restart Dead by Daylight.", IsOpen = true },
            new ScoreEvent(),
            new CountdownRing(),
            new CrossfadeText { Text = "Reading the loading screen" },
        };
        var shown = new Grid();
        foreach (FrameworkElement control in controls)
        {
            shown.Children.Add(control);
        }

        host.Children.Add(shown);
        CampfireTestHost.Pump();
        check.State = SkillCheckState.Scanning;
        CampfireTestHost.Wait(TimeSpan.FromMilliseconds(300));
        host.Children.Remove(shown);
        CampfireTestHost.Pump();
        var refs = controls.Select(c => new WeakReference(c)).ToList();
        refs.Add(new WeakReference(shown));
        return refs;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static List<WeakReference> CreateNeverShown() => new()
    {
        new WeakReference(new Keycap { Text = "F9" }),
        new WeakReference(new StatusDot()),
        new WeakReference(new MapStrip { MapNames = new[] { "Coal Tower" } }),
        new WeakReference(new CampfireScene()),
    };

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference SwapPlateScene(MapPlate plate)
    {
        CampfireScene? lobbyFire = plate.Campfire;
        Assert.NotNull(lobbyFire);
        plate.Scene = PlateScene.Map("MacMillan Estate", "Coal Tower");
        CampfireTestHost.Pump();
        return new WeakReference(lobbyFire);
    }

    private static void SimulateActivation(Window window, bool active)
    {
        // what Windows sends when the foreground window changes (WA_ACTIVE = 1, WA_INACTIVE = 0)
        SendMessage(new WindowInteropHelper(window).Handle, WmActivate, (IntPtr)(active ? 1 : 0), IntPtr.Zero);
        CampfireTestHost.Pump();
    }

    private static void RaiseSystemParametersChanged(string? propertyName)
    {
        // what WPF raises on WM_SETTINGCHANGE
        FieldInfo? field = typeof(SystemParameters).GetField(nameof(SystemParameters.StaticPropertyChanged), BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);
        var handler = (PropertyChangedEventHandler?)field!.GetValue(null);
        Assert.NotNull(handler);
        handler!(null, new PropertyChangedEventArgs(propertyName));
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    private sealed record Scene(FogBackdrop Backdrop, FogBackdrop Bank, EntityLegs Legs, MapPlate Plate, StatusDot Dot, CampfireScene Fire, CountdownRing Ring);
}
