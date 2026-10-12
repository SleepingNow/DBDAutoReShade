using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using DBDAutoReShade.App.Controls;
using DBDAutoReShade.App.Media;
using DBDAutoReShade.App.Services;

namespace DBDAutoReShade.Tests.Campfire;

/// <summary>The Campfire controls' contracts: states, sizes, events and end states (motion off unless noted).</summary>
public sealed class CampfireControlsTests
{
    private static MotionPolicy Still => new() { AnimationsEnabled = false };

    [Fact]
    public void AspectFrame_KeepsTheShape_AndNeverEnlargesThePictureBeyond18x() => CampfireTestHost.RunSta(() =>
    {
        var frame = new AspectFrame { MinAspect = 1.5, MaxAspect = 2, MaxWidthPixels = 364 * 1.8 };

        // room for a 2:1 plate up to 3:2: the height fills the space between them
        Assert.Equal(new Size(634, 317), frame.FrameSize(new Size(634, 317), 1.0));
        Assert.Equal(new Size(600, 360), frame.FrameSize(new Size(600, 360), 1.0));
        Assert.Equal(new Size(600, 400), frame.FrameSize(new Size(600, 500), 1.0));

        // a wide window: the frame is capped at 1.8 x 364 px and the fog fills the rest
        Size wide = frame.FrameSize(new Size(960, 480), 1.0);
        Assert.Equal(655.2, wide.Width, 3);
        Assert.Equal(655.2 / 1.5, wide.Height, 3);

        // 150 % scaling: 655 physical pixels are 437 DIPs
        Assert.Equal(436.8, frame.FrameSize(new Size(960, 480), 1.5).Width, 3);

        // too short: the height wins and the width follows the widest shape
        Assert.Equal(new Size(400, 200), frame.FrameSize(new Size(634, 200), 1.0));
    });

    [Fact]
    public void AspectFrame_HeightCap_KeepsATallFrameFromEnlargingThePictureByItsHeight() => CampfireTestHost.RunSta(() =>
    {
        // the 362 x 239 pictures are wider than 3:2: a 3:2 frame at the width cap (651.6 x 434.4) would enlarge them by
        // height, 432.4 / 239 = 1.81 x; the height cap holds it at 1.8 x 239
        var frame = new AspectFrame { MinAspect = 1.5, MaxAspect = 2, MaxWidthPixels = 362 * 1.8, MaxHeightPixels = 239 * 1.8 };
        Size tall = frame.FrameSize(new Size(960, 480), 1.0);
        Assert.Equal(651.6, tall.Width, 3);
        Assert.Equal(430.2, tall.Height, 3);
        Assert.InRange(tall.Width / tall.Height, 1.5, 2);

        // 150 %: both caps in physical pixels
        Size scaled = frame.FrameSize(new Size(960, 480), 1.5, 1.5);
        Assert.Equal(434.4, scaled.Width, 3);
        Assert.Equal(286.8, scaled.Height, 3);

        // below the caps nothing changes
        Assert.Equal(new Size(600, 400), frame.FrameSize(new Size(600, 500), 1.0));

        // a height cap below even the widest shape's height narrows the frame to it
        var low = new AspectFrame { MinAspect = 1.5, MaxAspect = 2, MaxWidthPixels = 1000, MaxHeightPixels = 200 };
        Assert.Equal(new Size(400, 200), low.FrameSize(new Size(960, 480), 1.0));
    });

    [Fact]
    public void MapPicture_ShowsThePicture_OrThePlaceholder()
    {
        CampfireTestHost.RunSta(() =>
        {
            var picture = new MapPicture { MapName = "Coal Tower" };
            CampfireTestHost.Layout(picture, 208, 104);
            Assert.True(picture.HasPicture);
            Assert.Equal(Stretch.UniformToFill, picture.Picture.Stretch);
            Assert.Equal(BitmapScalingMode.HighQuality, RenderOptions.GetBitmapScalingMode(picture.Picture));
            Assert.Equal(364, picture.PixelWidth);

            picture.MapName = "A Map From The Future";
            CampfireTestHost.Layout(picture, 208, 104);
            Assert.False(picture.HasPicture);
        });
    }

    [Fact]
    public void MapPlate_TellsEachSceneChange_AndEndsOnTheNewScene()
    {
        CampfireTestHost.RunSta(() =>
        {
            var plate = new MapPlate { Width = 640, Height = 320 };
            MotionPolicy.SetPolicy(plate, Still);
            plate.Scene = PlateScene.Lobby(FireState.Out, "No map detected yet", "Start Dead by Daylight, then press F8 on a loading screen.");
            CampfireTestHost.Layout(plate, 640, 320);
            Assert.NotNull(plate.Campfire);
            Assert.Equal(FireState.Out, plate.Campfire!.FireState);

            CampfireScene fire = plate.Campfire;
            plate.Scene = PlateScene.Lobby(FireState.Burning, "No map detected yet", "Global / Menu is on. Press F8 on the loading screen.");
            Assert.Same(fire, plate.Campfire); // lobby to lobby: the same clearing, the fire catches in place
            Assert.Equal(FireState.Burning, fire.FireState);

            plate.Scene = PlateScene.Map("MacMillan Estate", "Coal Tower");
            CampfireTestHost.Layout(plate, 640, 320);
            Assert.Null(plate.Campfire);
            Assert.True(plate.Picture!.HasPicture);

            plate.Sink(PlateScene.Lobby(FireState.Burning, "No map detected yet", "Global / Menu is on. Press F8 on the loading screen."));
            Assert.NotNull(plate.Campfire);
            Assert.Empty(MotionDiagnostics.FindAnimatedObjects(plate));
        });
    }

    [Fact]
    public void MapPlate_FrameIsCappedAt18xThePicture()
    {
        CampfireTestHost.RunSta(() =>
        {
            var plate = new MapPlate { Scene = PlateScene.Map("MacMillan Estate", "Coal Tower") };
            MotionPolicy.SetPolicy(plate, Still);
            Assert.Equal(MapImageCatalog.BuiltInReferencePixelWidth, plate.ReferencePixelWidth);
            CampfireTestHost.Layout(plate, 1100, 500);

            // capped by the narrowest built-in picture (362 px), so no picture is enlarged more than 1.8 x
            Assert.True(plate.Frame.ActualWidth <= MapImageCatalog.BuiltInReferencePixelWidth * MapImageCatalog.MaxUpscale + 0.5, $"frame {plate.Frame.ActualWidth}");
            Assert.InRange(plate.Frame.ActualWidth / plate.Frame.ActualHeight, 1.5 - 1e-6, 2 + 1e-6);
        });
    }

    [Theory]
    [InlineData("Eyrie of Crows")] // Ion_Ionlevel01, 362 x 239: the narrowest and shortest picture
    [InlineData("Lampkin Lane")] // Sub_Street, 362 x 239
    [InlineData("Coal Tower")] // 364 x 241
    [InlineData("Raccoon City Police Station West Wing")] // Ecl_Orionlevel01, translucent edge trimmed
    public void MapPlate_AtItsLargest_EnlargesNoPictureBeyond18x_ByWidthOrHeight(string map)
    {
        CampfireTestHost.RunSta(() =>
        {
            // room for the frame's tallest shape (3:2) at the width cap: UniformToFill then scales by the height
            var plate = new MapPlate { Scene = PlateScene.Map("Realm", map) };
            MotionPolicy.SetPolicy(plate, Still);
            Assert.Equal(MapImageCatalog.BuiltInReferencePixelHeight, plate.ReferencePixelHeight);
            CampfireTestHost.Layout(plate, 1400, 900);

            MapPicture picture = plate.Picture!;
            var source = (BitmapSource)picture.Picture.Source;
            DpiScale dpi = VisualTreeHelper.GetDpi(picture);
            double byWidth = picture.ActualWidth * dpi.DpiScaleX / source.PixelWidth;
            double byHeight = picture.ActualHeight * dpi.DpiScaleY / source.PixelHeight;
            double scale = Math.Max(byWidth, byHeight); // UniformToFill: the side the picture is short of
            Assert.True(scale <= MapImageCatalog.MaxUpscale + 1e-6, $"{map}: {source.PixelWidth} x {source.PixelHeight} drawn at {scale:0.0000} x (by width {byWidth:0.0000}, by height {byHeight:0.0000})");
            Assert.True(scale > 1.7, $"{map}: the plate still uses its room ({scale:0.0000} x)");
        });
    }

    [Fact]
    public void MapPlate_Confirm_LeavesTheFrameLive()
    {
        CampfireTestHost.RunSta(() =>
        {
            var plate = new MapPlate { Scene = PlateScene.Map("MacMillan Estate", "Coal Tower"), Tone = PlateTone.Waiting };
            MotionPolicy.SetPolicy(plate, new MotionPolicy());
            CampfireTestHost.Layout(plate, 640, 320);
            plate.Confirm();
            Assert.Equal(PlateTone.Live, plate.Tone);
            Assert.Contains("MapPlate.confirm", MotionDiagnostics.ActiveMoments);
        });
    }

    [Fact]
    public void CampfireScene_SwitchesStates_AndItsEmbersFollowTheLiveFlag()
    {
        CampfireTestHost.RunSta(() =>
        {
            var scene = new CampfireScene { IsLive = true };
            MotionPolicy.SetPolicy(scene, new MotionPolicy());
            CampfireTestHost.Layout(scene, 400, 200);
            var risers = (UIElement)scene.FindName("Risers");
            var sparks = (UIElement)scene.FindName("Sparks");
            Assert.Equal(1, risers.Opacity);
            Assert.Equal(0, sparks.Opacity);
            scene.IsLive = false;
            Assert.Equal(0, risers.Opacity);
            Assert.Equal(1, sparks.Opacity);
            foreach (FireState state in Enum.GetValues<FireState>())
            {
                scene.SetFireState(state, animate: false);
                Assert.Equal(state, scene.FireState);
                Assert.Equal(state == FireState.Burning ? 1 : 0, ((UIElement)scene.FindName("FlameWrap")).Opacity);
            }

            // twelve embers, no more
            Assert.Equal(12, ((Canvas)risers).Children.Count);
        });
    }

    [Fact]
    public void CountdownRing_CountsDown_AndEndsCancelled()
    {
        CampfireTestHost.RunSta(() =>
        {
            var ring = new CountdownRing { Duration = TimeSpan.FromSeconds(15) };
            MotionPolicy.SetPolicy(ring, Still);
            CampfireTestHost.Layout(ring, 52, 52);
            CountdownEndedEventArgs? ended = null;
            ring.Ended += (_, e) => ended = e;
            ring.Start(TimeSpan.FromSeconds(4.2));
            Assert.True(ring.IsRunning);
            Assert.Equal(11, ring.SecondsLeft);
            ring.Cancel();
            Assert.False(ring.IsRunning);
            Assert.NotNull(ended);
            Assert.True(ended!.Cancelled);
        });
    }

    [Fact]
    public void CountdownRing_KeepsCounting_OutOfTheTree_AndEndsOnTime()
    {
        CampfireTestHost.RunSta(() =>
        {
            var host = new Grid();
            Window window = CampfireTestHost.Show(host, new MotionPolicy(), 200, 200);
            try
            {
                var ring = new CountdownRing { Duration = TimeSpan.FromMilliseconds(1500) };
                host.Children.Add(ring);
                CampfireTestHost.Pump();
                var ended = new List<bool>();
                ring.Ended += (_, e) => ended.Add(e.Cancelled);
                ring.Start();
                Assert.Equal(CountdownMode.Drain, ring.Mode);
                CampfireTestHost.Wait(TimeSpan.FromMilliseconds(300));

                // the user switches tab while the Test runs: no clock while it is out of the tree, the run goes on
                host.Children.Remove(ring);
                CampfireTestHost.Pump();
                Assert.True(ring.IsRunning);
                Assert.Equal(CountdownMode.Held, ring.Mode);
                Assert.Empty(MotionDiagnostics.FindAnimatedObjects(ring));
                CampfireTestHost.Wait(TimeSpan.FromMilliseconds(200));

                // ...and comes back: it sweeps on from where the run is now, and ends on time
                host.Children.Add(ring);
                CampfireTestHost.Pump();
                Assert.Equal(CountdownMode.Drain, ring.Mode);
                Assert.InRange(ring.Elapsed.TotalMilliseconds, 450, 1400);
                CampfireTestHost.Wait(TimeSpan.FromMilliseconds(1600));
                Assert.Equal(new[] { false }, ended);
                Assert.False(ring.IsRunning);
                Assert.Equal(0, ring.SecondsLeft);

                // a run that is never shown again still ends on time
                ring.Start();
                CampfireTestHost.Wait(TimeSpan.FromMilliseconds(200));
                host.Children.Remove(ring);
                CampfireTestHost.Wait(TimeSpan.FromMilliseconds(1700));
                Assert.Equal(new[] { false, false }, ended);
                Assert.False(ring.IsRunning);

                // a run that expired while it was out of the tree (its ticker stopped) ends as soon as it is back
                host.Children.Add(ring);
                CampfireTestHost.Pump();
                ring.Start(TimeSpan.FromMilliseconds(1400));
                host.Children.Remove(ring);
                CampfireTestHost.Pump();
                ring.FreezeAt(TimeSpan.FromMilliseconds(100)); // (stops its ticker, as a frame capture does)
                CampfireTestHost.Wait(TimeSpan.FromMilliseconds(300));
                Assert.True(ring.IsRunning);
                host.Children.Add(ring);
                CampfireTestHost.Pump();
                Assert.False(ring.IsRunning); // over at once; Ended follows the 150 ms fade
                CampfireTestHost.Wait(TimeSpan.FromMilliseconds(300));
                Assert.Equal(new[] { false, false, false }, ended);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void CountdownRing_HoldsItsNeedle_WhileTheWindowIsInactive()
    {
        CampfireTestHost.RunSta(() =>
        {
            var policy = new MotionPolicy();
            var ring = new CountdownRing { Duration = TimeSpan.FromSeconds(15) };
            Window window = CampfireTestHost.Show(CampfireTestHost.Frame(ring, 80, 80), policy, 80, 80);
            try
            {
                ring.Start();
                Assert.Equal(CountdownMode.Drain, ring.Mode);
                Assert.NotEmpty(MotionDiagnostics.FindAnimatedObjects(ring));

                // the user clicks into the game: the needle steps with the ticker, no animation clock
                policy.WindowActive = false;
                CampfireTestHost.Pump();
                Assert.Equal(CountdownMode.Held, ring.Mode);
                Assert.Empty(MotionDiagnostics.FindAnimatedObjects(ring));
                double angle = ring.NeedleAngle;
                CampfireTestHost.Wait(TimeSpan.FromMilliseconds(600));
                Assert.True(ring.NeedleAngle > angle, $"the needle steps: {angle} -> {ring.NeedleAngle}");
                Assert.Empty(MotionDiagnostics.FindAnimatedObjects(ring));

                // back in front: it sweeps again
                policy.WindowActive = true;
                CampfireTestHost.Pump();
                Assert.Equal(CountdownMode.Drain, ring.Mode);
                Assert.NotEmpty(MotionDiagnostics.FindAnimatedObjects(ring));

                // Windows animations off: no needle, the ring steps once a second
                policy.ReducedMotionOverride = true;
                CampfireTestHost.Pump();
                Assert.Equal(CountdownMode.Still, ring.Mode);
                Assert.Empty(MotionDiagnostics.FindAnimatedObjects(ring));
                ring.Cancel();
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void CountdownRing_RunsOut()
    {
        CampfireTestHost.RunSta(() =>
        {
            var ring = new CountdownRing { Duration = TimeSpan.FromMilliseconds(600) };
            var window = CampfireTestHost.Show(ring, new MotionPolicy(), 80, 80);
            try
            {
                CountdownEndedEventArgs? ended = null;
                ring.Ended += (_, e) => ended = e;
                ring.Start();
                CampfireTestHost.Wait(TimeSpan.FromMilliseconds(1200));
                Assert.NotNull(ended);
                Assert.False(ended!.Cancelled);
                Assert.Equal(0, ring.SecondsLeft);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void Keycap_WrapsLongHotkeysAfterAPlus()
    {
        Assert.Equal("Ctrl+​Alt+​Shift+​NumPad5", Keycap.Breakable("Ctrl+Alt+Shift+NumPad5"));
        Assert.Equal("F8", Keycap.Breakable("F8"));
        CampfireTestHost.RunSta(() =>
        {
            var key = new Keycap { Text = "Ctrl+Alt+Shift+NumPad5", MaxWidth = 120 };
            CampfireTestHost.Layout(key, 120, 200);
            Assert.True(key.ActualWidth <= 120);
            Assert.True(key.ActualHeight > 26, "it wrapped onto more lines instead of clipping");
        });
    }

    [Fact]
    public void ScoreEvent_ShowsTheDetail()
    {
        CampfireTestHost.RunSta(() =>
        {
            var score = new ScoreEvent();
            MotionPolicy.SetPolicy(score, new MotionPolicy());
            CampfireTestHost.Layout(score, 400, 100);
            score.Show("Map found: Coal Tower", TimeSpan.FromMilliseconds(150));
            Assert.Equal("Map found: Coal Tower", score.Detail);
            Assert.True(score.IsShowing);

            // a polite live region named by what it says
            Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(score));
            Assert.Equal("Great skill check. Map found: Coal Tower", AutomationProperties.GetName(score));
            score.Hide();
            Assert.False(score.IsShowing);
        });
    }

    [Fact]
    public void RestartBanner_OpensAndCloses()
    {
        CampfireTestHost.RunSta(() =>
        {
            var banner = new RestartBanner { Text = "Restart Dead by Daylight so ReShade loads the new shortcut for Haddonfield." };
            Window window = CampfireTestHost.Show(banner, new MotionPolicy(), 700, 80);
            Assert.Equal(Visibility.Collapsed, banner.Visibility);

            // an assertive live region named by its message
            Assert.Equal(AutomationLiveSetting.Assertive, AutomationProperties.GetLiveSetting(banner));
            Assert.Equal(banner.Text, AutomationProperties.GetName(banner));
            int clicks = 0;
            banner.ActionClick += (_, _) => clicks++;
            banner.IsOpen = true;
            Assert.Equal(Visibility.Visible, banner.Visibility);
            Assert.Contains("RestartBanner.drop", MotionDiagnostics.ActiveMoments);
            banner.IsOpen = false;
            Assert.Equal(Visibility.Collapsed, banner.Visibility);
            Assert.Equal(0, clicks);
            window.Close();
        });
    }

    [Fact]
    public void MapStrip_ShowsASliceOfEachMap_AndFogsWhenUnset()
    {
        CampfireTestHost.RunSta(() =>
        {
            var strip = new MapStrip { MapNames = new[] { "Coal Tower", "Groaning Storehouse", "Ironworks of Misery", "Shelter Woods", "Suffocation Pit" } };
            MotionPolicy.SetPolicy(strip, Still);
            CampfireTestHost.Layout(strip, 104, 52);
            Assert.Equal(5, strip.SliceCount);
            var inner = (Grid)((Border)strip.Content).Child;
            UIElement slices = inner.Children[0];
            UIElement fog = inner.Children[1];
            Assert.Equal(0, fog.Opacity);
            Assert.Equal(1, slices.Opacity);

            // no preset: the mist bank covers the strip and the pictures dim
            strip.IsFogged = true;
            Assert.Equal(1, fog.Opacity);
            Assert.Equal(0.6, slices.Opacity, 3);
            Assert.Equal(0, ((TranslateTransform)fog.RenderTransform).X);
            Color covered = CampfireTestHost.PixelAt(CampfireTestHost.Render(strip), 80, 26);

            // the fog lifts again
            strip.IsFogged = false;
            Assert.Equal(0, fog.Opacity);
            Assert.Equal(1, slices.Opacity);
            Color clear = CampfireTestHost.PixelAt(CampfireTestHost.Render(strip), 80, 26);
            Assert.NotEqual(clear, covered);
        });
    }

    [Fact]
    public void StatusDot_KindsRender()
    {
        CampfireTestHost.RunSta(() =>
        {
            foreach (DotKind kind in Enum.GetValues<DotKind>())
            {
                var dot = new StatusDot { Kind = kind };
                CampfireTestHost.Layout(dot, 8, 8);
                Assert.Equal(8, dot.ActualWidth);
                BitmapSource bitmap = CampfireTestHost.Render(dot);
                Color centre = CampfireTestHost.PixelAt(bitmap, 4, 4);
                Color rim = CampfireTestHost.PixelAt(bitmap, 4, 0); // on the 1.5 px ring at 12 o'clock
                switch (kind)
                {
                    case DotKind.Ember:
                        Assert.Equal(255, centre.A);
                        Assert.True(centre.R > centre.G && centre.G > centre.B, $"ember {centre}");
                        break;
                    case DotKind.Red:
                        Assert.Equal(255, centre.A);
                        Assert.True(centre.R > 120 && centre.G < 60 && centre.B < 80, $"entity red {centre}");
                        break;
                    case DotKind.Ring:
                        Assert.Equal(0, centre.A); // hollow
                        Assert.True(rim.A > 96, $"ring {rim}");
                        Assert.True(rim.R > rim.B + 40, $"an ember ring {rim}");
                        break;
                    case DotKind.Off:
                        Assert.Equal(0, centre.A); // hollow
                        Assert.True(rim.A > 96, $"ring {rim}");
                        Assert.True(Math.Abs(rim.R - rim.B) < 40 && rim.B >= rim.R - 5, $"an ash ring {rim}");
                        break;
                }
            }
        });
    }

    [Fact]
    public void CrossfadeText_SwapsText()
    {
        CampfireTestHost.RunSta(() =>
        {
            var text = new CrossfadeText { Text = "Reading the loading screen" };
            Window window = CampfireTestHost.Show(text, new MotionPolicy(), 600, 200);
            text.Text = "Map found — waiting for you to click into the game";
            Assert.Equal("Map found — waiting for you to click into the game", text.TextElement.Text);
            Assert.Contains("CrossfadeText.BlurRise", MotionDiagnostics.ActiveMoments);
            window.Close();
        });
    }

    [Fact]
    public void CrossfadeText_AChangeMidSwap_CarriesOnFromWhatIsDrawn()
    {
        CampfireTestHost.RunSta(() =>
        {
            var text = new CrossfadeText { Text = "Ready", Width = 500 };
            Window window = CampfireTestHost.Show(text, new MotionPolicy(), 600, 200);
            try
            {
                // "Reading the loading screen" comes in: 30 ms in it is still inside its 60 ms delay (not drawn) while
                // "Ready" blurs out on top
                text.Text = "Reading the loading screen";
                CampfireTestHost.Pump();
                text.FreezeAt(TimeSpan.FromMilliseconds(30));
                TextBlock ready = text.Ghosts.Single();
                double readyWas = ready.Opacity;
                Assert.InRange(readyWas, 0.05, 0.95);
                Assert.True(text.TextElement.Opacity < 0.01);

                // F8 again at once: the text that never showed does not flash in as a ghost, and "Ready" keeps fading
                // from where it was instead of vanishing
                text.Text = "Ready";
                CampfireTestHost.Pump();
                text.FreezeAt(TimeSpan.Zero);
                Assert.DoesNotContain(text.Ghosts, g => g.Text == "Reading the loading screen" && g.Opacity > 0.01);
                Assert.Same(ready, text.Ghosts.Single(g => g.Text == "Ready"));
                Assert.Equal(readyWas, ready.Opacity, 2);

                // a change while the new text is well on its way: it leaves from the opacity and blur it has, not from full
                text.FreezeAt(TimeSpan.FromMilliseconds(200));
                double shownWas = text.TextElement.Opacity;
                double blurWas = ((BlurEffect)text.TextElement.Effect).Radius;
                Assert.InRange(shownWas, 0.3, 0.98);
                text.Text = "Scan paused";
                CampfireTestHost.Pump();
                text.FreezeAt(TimeSpan.Zero);
                TextBlock leaving = text.Ghosts.Single(g => g.Text == "Ready");
                Assert.Equal(shownWas, leaving.Opacity, 2);
                Assert.Equal(blurWas, ((BlurEffect)leaving.Effect).Radius, 0.5);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void CrossfadeText_AQuickSwap_HasNoDelayAndNoRise()
    {
        CampfireTestHost.RunSta(() =>
        {
            var text = new CrossfadeText { Text = "Ready", Width = 500, SwapQuickly = true };
            Window window = CampfireTestHost.Show(text, new MotionPolicy(), 600, 200);
            try
            {
                text.Text = "Manual override";
                CampfireTestHost.Pump();
                text.FreezeAt(TimeSpan.FromMilliseconds(30));
                Assert.True(text.TextElement.Opacity > 0.05, "a quick swap starts at once");
                Assert.Equal(0, ((TranslateTransform)text.TextElement.RenderTransform).Y, 3);
                text.FreezeAt(TimeSpan.FromMilliseconds(200));
                Assert.Equal(1, text.TextElement.Opacity, 2);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void MapPlate_ASinkCutInMidReveal_StartsFromThePicturesLookThen()
    {
        CampfireTestHost.RunSta(() =>
        {
            var plate = new MapPlate { Width = 634, Height = 317, Scene = PlateScene.Lobby(FireState.Burning, "Looking for the map name", "Global / Menu stays on.") };
            Window window = CampfireTestHost.Show(CampfireTestHost.Frame(plate, 700, 380), new MotionPolicy(), 700, 380);
            try
            {
                // the fog clears off the map; 400 ms in the picture is still coming out of its blur
                plate.Scene = PlateScene.Map("MacMillan Estate", "Coal Tower");
                CampfireTestHost.Pump();
                plate.FreezeAt(TimeSpan.FromMilliseconds(400));
                Image picture = plate.Picture!.Picture;
                double opacity = picture.Opacity, blur = ((BlurEffect)picture.Effect).Radius;
                Assert.InRange(opacity, 0.2, 0.97);
                Assert.True(blur > 1, "blur " + blur);

                // F8 at once (the clearing returns): the picture sinks from that look, not from a sharp, full picture
                plate.Scene = PlateScene.Lobby(FireState.Burning, "Looking for the map name", "Global / Menu stays on.");
                CampfireTestHost.Pump();
                plate.FreezeAt(TimeSpan.Zero);
                Assert.Contains("MapPlate.Sink", MotionDiagnostics.ActiveMoments);
                Assert.Equal(opacity, picture.Opacity, 2);
                Assert.Equal(blur, ((BlurEffect)picture.Effect).Radius, 0.5);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void MapPlate_DrawsItsGlowOnlyWhileItShows()
    {
        CampfireTestHost.RunSta(() =>
        {
            static int Glows(MapPlate plate) => ((Panel)plate.Frame).Children.OfType<UIElement>().Count(c => c.Effect is DropShadowEffect);
            var plate = new MapPlate { Width = 634, Height = 317, Scene = PlateScene.Map("MacMillan Estate", "Coal Tower") };
            Window window = CampfireTestHost.Show(CampfireTestHost.Frame(plate, 700, 380), new MotionPolicy(), 700, 380);
            try
            {
                Assert.Equal(0, Glows(plate)); // the neutral frame has no glow: no shadow pass
                plate.Tone = PlateTone.Live;
                Assert.Equal(1, Glows(plate));
                CampfireTestHost.Wait(TimeSpan.FromMilliseconds(400));
                Assert.Equal(1, Glows(plate)); // live: the warm glow stays
                plate.Tone = PlateTone.Neutral;
                Assert.Equal(1, Glows(plate)); // (while it fades out)
                CampfireTestHost.Wait(TimeSpan.FromMilliseconds(400));
                Assert.Equal(0, Glows(plate));
                plate.Confirm();
                Assert.Equal(1, Glows(plate));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void FogBackdrop_ShowMap_UsesItsCrossfadeForThatChangeOnly()
    {
        CampfireTestHost.RunSta(() =>
        {
            var fog = new FogBackdrop { Layer = FogLayer.Backdrop };
            Window window = CampfireTestHost.Show(fog, new MotionPolicy(), 400, 300);
            try
            {
                fog.ShowMap("Coal Tower", TimeSpan.Zero);
                Assert.Equal(0.6, ShownMap(fog, "Coal Tower").Opacity, 3);

                // a state moment re-asserts the map that is already shown with its slow crossfade: nothing to do
                fog.ShowMap("Coal Tower", TimeSpan.FromMilliseconds(700));
                Assert.DoesNotContain("FogBackdrop.map", MotionDiagnostics.ActiveMoments);

                // browsing realms afterwards uses the quick 220 ms crossfade, not the pending 700 ms
                fog.MapName = "Shelter Woods";
                fog.FreezeAt(TimeSpan.FromMilliseconds(230));
                Assert.Equal(0.6, ShownMap(fog, "Shelter Woods").Opacity, 3);

                // a real ShowMap change uses its own duration (100 ms into 700 ms: about a half; 220 ms would be 0.57)
                fog.ShowMap("Coal Tower", TimeSpan.FromMilliseconds(700));
                fog.FreezeAt(TimeSpan.FromMilliseconds(100));
                Assert.InRange(ShownMap(fog, "Coal Tower").Opacity, 0.2, 0.45);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void EntityLegs_LeanAndSettle()
    {
        CampfireTestHost.RunSta(() =>
        {
            var legs = new EntityLegs { Width = 640 };
            MotionPolicy.SetPolicy(legs, Still);
            CampfireTestHost.Layout(legs, 640, 760);
            legs.Lean(true);
            Assert.True(legs.IsLeaning);
            legs.Lean(false);
            legs.RimRun();
            Assert.Empty(MotionDiagnostics.FindAnimatedObjects(legs));
        });
    }

    private static Grid ShownMap(FogBackdrop fog, string map)
    {
        ImageSource? picture = MapImageCatalog.Default.TryGetImage(map);
        return ((Grid)fog.Content).Children.OfType<Grid>()
            .First(host => host.Children.OfType<Image>().Any(image => ReferenceEquals(image.Source, picture)));
    }

    [Fact]
    public void AmbientLoops_StartPartWayThroughTheirCycle()
    {
        // CSS negative animation-delay = a negative BeginTime: the embers are spread over their cycles, not in step
        CampfireTestHost.RunSta(() =>
        {
            var scene = new CampfireScene { IsLive = true };
            var window = CampfireTestHost.Show(scene, new MotionPolicy(), 400, 200);
            try
            {
                CampfireTestHost.Wait(TimeSpan.FromMilliseconds(100));
                Assert.True(scene.IsAmbientRunning);
                var risers = (Canvas)scene.FindName("Risers");
                var heights = risers.Children.OfType<System.Windows.Shapes.Path>().Select(p => ((TranslateTransform)p.RenderTransform).Y).ToList();
                Assert.True(heights.Distinct().Count() > 6, "embers at different heights: " + string.Join(", ", heights.Select(h => h.ToString("0"))));
            }
            finally
            {
                window.Close();
            }
        });
    }
}
