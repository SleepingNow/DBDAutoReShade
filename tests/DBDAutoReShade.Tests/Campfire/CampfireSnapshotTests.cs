using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DBDAutoReShade.App.Controls;
using DBDAutoReShade.App.Media;

namespace DBDAutoReShade.Tests.Campfire;

/// <summary>Runs only when <c>DBD_SNAPSHOTS</c> names an output folder.</summary>
public sealed class SnapshotFactAttribute : FactAttribute
{
    /// <summary>Skips unless the snapshot folder is set.</summary>
    public SnapshotFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DBD_SNAPSHOTS")))
        {
            Skip = "Opt-in: set DBD_SNAPSHOTS=<output folder> to render the Campfire control snapshots.";
        }
    }
}

/// <summary>
/// Opt-in visual verification of the Campfire controls (set <c>DBD_SNAPSHOTS=&lt;dir&gt;</c>): renders each control in its
/// key states, and its motion moments frozen at given times (every clock paused and sought), to PNG on an STA thread,
/// plus contact sheets laid out like the prototype's (scratchpad\motion\a-campfire\final and skillcheck\final).
/// Never runs in a normal test pass.
/// </summary>
public sealed class CampfireSnapshotTests
{
    private const double PlateW = 634, PlateH = 317;

    private static string OutDir => Environment.GetEnvironmentVariable("DBD_SNAPSHOTS") ?? Path.GetTempPath();

    [SnapshotFact]
    public void Theme_Gallery() => CampfireTestHost.RunSta(() =>
    {
        ResourceDictionary theme = CampfireThemeTests.LoadTheme();
        var root = new StackPanel { Width = 900, Margin = new Thickness(24), Background = CampfirePalette.Night };
        root.Resources.MergedDictionaries.Add(theme);
        MotionPolicy.SetPolicy(root, new MotionPolicy { WindowActive = false });

        root.Children.Add(Row(theme,
            Styled(new TextBlock { Text = "Reading the" }, theme, "CfHeroXL"),
            Styled(new TextBlock { Text = "Presets", VerticalAlignment = VerticalAlignment.Bottom }, theme, "CfDetailTitle"),
            Styled(new TextBlock { Text = "Manual override", VerticalAlignment = VerticalAlignment.Bottom }, theme, "CfSectionTitle")));
        root.Children.Add(Row(theme,
            Styled(new TextBlock { Text = "Active preset" }, theme, "CfLabel"),
            Styled(new TextBlock { Text = "00.STX.Universal.ini" }, theme, "CfFileName"),
            Styled(new TextBlock { Text = "Confirmed by ReShade" }, theme, "CfTag"),
            Styled(new TextBlock { Text = "Restart needed" }, theme, "CfTagRed"),
            new ContentControl { Content = "Override", Style = (Style)theme["CfPillRed"] }));
        root.Children.Add(Row(theme,
            new Button { Content = "Apply override", Style = (Style)theme["CfButtonPrimary"] },
            new Button { Content = "Return to automatic" },
            new Button { Content = "Return to automatic", IsEnabled = false },
            new Button { Content = "Clear", Style = (Style)theme["CfButtonGhost"] },
            new Button { Content = "Reset settings", Style = (Style)theme["CfButtonDanger"] },
            new Button { Content = "Show in Presets", Style = (Style)theme["CfButtonSecondarySmall"] }));
        var nav = new StackPanel { Width = 220, Background = new SolidColorBrush(Color.FromArgb(230, 8, 13, 15)) };
        nav.Children.Add(Nav(theme, "Status", "CfIconStatus", true, false));
        nav.Children.Add(Nav(theme, "Presets", "CfIconPresets", false, true));
        nav.Children.Add(Nav(theme, "Settings", "CfIconSettings", false, false));
        var titleBar = new StackPanel { Orientation = Orientation.Horizontal, Background = CampfirePalette.NightDeep, VerticalAlignment = VerticalAlignment.Top };
        foreach ((string glyph, string style) in new[] { ("CfGlyphMinimize", "CfTitleBarButton"), ("CfGlyphMaximize", "CfTitleBarButton"), ("CfGlyphClose", "CfTitleBarCloseButton") })
        {
            var b = new Button { Style = (Style)theme[style] };
            CampfireAssist.SetIcon(b, (Geometry)theme[glyph]);
            titleBar.Children.Add(b);
        }

        var checks = new StackPanel { Width = 380 };
        checks.Children.Add(new CheckBox { Content = "Start the next game with the Global preset", IsChecked = true, Margin = new Thickness(0, 0, 0, 12) });
        checks.Children.Add(new CheckBox { Content = "Play the Great skill-check sound when a map is found", Margin = new Thickness(0, 0, 0, 12) });
        checks.Children.Add(new CheckBox { Content = "Play animations (disabled)", IsChecked = true, IsEnabled = false, Margin = new Thickness(0, 0, 0, 12) });
        checks.Children.Add(new Slider { Minimum = 0, Maximum = 100, Value = 50, Width = 300, HorizontalAlignment = HorizontalAlignment.Left });
        root.Children.Add(Row(theme, nav, titleBar, checks));

        var filter = new TextBox { Width = 330, Padding = new Thickness(36, 0, 12, 0) };
        CampfireAssist.SetPlaceholder(filter, "Filter by realm or map");
        CampfireAssist.SetIcon(filter, (Geometry)theme["CfIconSearch"]);
        var manifest = new TextBox { Width = 330, Text = "http://example.com/manifest.json" };
        CampfireAssist.SetIsInvalid(manifest, true);
        var combo = new ComboBox { Width = 232 };
        combo.Items.Add("Global / Menu");
        combo.Items.Add("MacMillan Estate");
        combo.SelectedIndex = 0;
        root.Children.Add(Row(theme, filter, manifest, combo));
        root.Children.Add(Row(theme,
            new ContentControl { Content = "OK", Style = (Style)theme["CfReportTagOk"] },
            new ContentControl { Content = "Info", Style = (Style)theme["CfReportTagInfo"] },
            new ContentControl { Content = "Issue", Style = (Style)theme["CfReportTagIssue"] },
            new Keycap { Text = "F8" },
            new Keycap { Text = "F8", IsGlowing = true },
            new Keycap { Text = "F9" },
            new Keycap { Text = "Ctrl+Alt+Shift+NumPad5", MaxWidth = 120 },
            new Keycap { Text = "F8", Size = KeycapSize.Large },
            new Keycap { Text = "…", Size = KeycapSize.Large, IsCapturing = true },
            new StatusDot { Kind = DotKind.Ember },
            new StatusDot { Kind = DotKind.Ring },
            new StatusDot { Kind = DotKind.Off },
            new StatusDot { Kind = DotKind.Red }));
        var list = new ListBox { Width = 380, Height = 200 };
        foreach ((string realm, string[] maps, bool unset) in new[]
        {
            ("Autohaven Wreckers", new[] { "Azarov's Resting Place", "Blood Lodge", "Gas Heaven", "Wreckers' Yard", "Wretched Shop" }, true),
            ("MacMillan Estate", new[] { "Coal Tower", "Groaning Storehouse", "Ironworks of Misery", "Shelter Woods", "Suffocation Pit" }, false),
            ("Haddonfield", new[] { "Lampkin Lane" }, false),
        })
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var strip = new MapStrip { MapNames = maps, IsFogged = unset };
            var text = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(Styled(new TextBlock { Text = realm }, theme, "CfRowName"));
            text.Children.Add(Styled(new TextBlock { Text = unset ? "Uses Global / Menu" : "11.STX.Macmillan.ini" }, theme, "CfRowPreset"));
            text.Children.Add(Styled(new TextBlock { Text = maps.Length + " maps" }, theme, "CfRowMeta"));
            Grid.SetColumn(text, 1);
            row.Children.Add(strip);
            row.Children.Add(text);
            list.Items.Add(new ListBoxItem { Content = row, IsSelected = realm == "MacMillan Estate" });
        }

        var banner = new RestartBanner { Text = "Restart Dead by Daylight so ReShade loads the new shortcut for Haddonfield.", IsOpen = true, Width = 460 };
        root.Children.Add(Row(theme, list, banner));
        var ring = new CountdownRing();
        root.Children.Add(Row(theme, ring, new ScoreEventProbe().Build()));
        CampfireTestHost.Layout(root, 948, 2000);
        ring.Start(TimeSpan.FromSeconds(4));
        ring.FreezeAt(TimeSpan.FromSeconds(4));
        CampfireTestHost.Layout(root, 948, 2000);
        CampfireTestHost.SavePng(root, Path.Combine(OutDir, "theme-gallery.png"));
    });

    [SnapshotFact]
    public void CampfireScene_States() => CampfireTestHost.RunSta(() =>
    {
        var sheet = new ContactSheet();
        foreach (FireState state in Enum.GetValues<FireState>())
        {
            var scene = new CampfireScene { FireState = state, IsLive = true, AnimateChanges = false };
            MotionPolicy.SetPolicy(scene, new MotionPolicy { AnimationsEnabled = false });
            Grid frame = CampfireTestHost.Frame(scene, PlateW, PlateH);
            CampfireTestHost.Layout(frame, PlateW, PlateH);
            sheet.Add(frame, state.ToString());
        }

        sheet.Save("scene-states", 3, "CampfireScene: Out, Embers, Burning (static)");
    });

    [SnapshotFact]
    public void Plate_Moments() => CampfireTestHost.RunSta(() =>
    {
        PlateScene lobbyLit = PlateScene.Lobby(FireState.Burning, "No map detected yet", "Global / Menu is on. Press F8 on the loading screen.");
        PlateScene lobbyCold = PlateScene.Lobby(FireState.Out, "No map detected yet", "Start Dead by Daylight, then press F8 on a loading screen.");
        PlateScene scanning = PlateScene.Lobby(FireState.Burning, "Looking for the map name", "Global / Menu stays on until a realm is read.");
        PlateScene map = PlateScene.Map("MacMillan Estate", "Coal Tower");

        PlateSheet("plate-catch", "waiting -> ready: the fire catches", lobbyCold, p => p.Scene = lobbyLit, new[] { 0, 150, 350, 600, 1000 });
        PlateSheet("plate-dieout", "ready -> waiting: the fire dies out", lobbyLit, p => p.Scene = lobbyCold, new[] { 0, 300, 700, 1200, 2000 });
        PlateSheet("plate-reveal", "map found: the fog clears off the map (blur 10 -> 0, 700 ms), names land after", scanning,
            p => { p.Scene = map; p.CaptionTag = WaitingTag(); }, new[] { 0, 150, 400, 700, 1000, 1400 });
        PlateSheet("plate-confirm", "preset confirmed: ember runs the frame, light swells", map,
            p => p.Confirm(), new[] { 0, 200, 450, 700, 950, 1400 }, p => p.Tone = PlateTone.Waiting);
        PlateSheet("plate-sink", "back to Global (F9): the picture sinks into the fog, the clearing returns", map,
            p => p.Scene = lobbyLit, new[] { 0, 250, 500, 750, 1100, 2100 }, p => p.Tone = PlateTone.Live);
        PlateSheet("plate-burndown", "scanning -> paused: the fire burns down, fog thickens", scanning,
            p => p.Scene = PlateScene.Lobby(FireState.Embers, "Scan paused", "Switch back to Dead by Daylight to resume."), new[] { 0, 200, 450, 900 });
        PlateSheet("plate-heartbeat", "waiting for focus: the heartbeat on the frame's glow (lub-dub, then rest)", map,
            p => { }, new[] { 1000, 1084, 1150, 1290, 1400, 1700 }, p => { p.Tone = PlateTone.Waiting; p.IsWaitingForFocus = true; });
    });

    [SnapshotFact]
    public void SkillCheck_Moments() => CampfireTestHost.RunSta(() =>
    {
        PlateScene scanning = PlateScene.Lobby(FireState.Burning, "Looking for the map name", "Global / Menu stays on until a realm is read.");

        // scanning: one revolution per read (1.4 s), the zone relocates per revolution
        var sheet = new ContactSheet();
        foreach (int ms in new[] { 0, 200, 600, 1000, 1300, 1500, 2900, 4300 })
        {
            (Window window, MapPlate plate) = HostPlate(scanning, new MotionPolicy { WindowActive = false });
            plate.SkillCheck.State = SkillCheckState.Scanning;
            plate.SkillCheck.SeekSpin(TimeSpan.FromMilliseconds(ms));
            MotionDiagnostics.FreezeAllAt(TimeSpan.FromMilliseconds(400));
            sheet.Add((FrameworkElement)plate.Parent, $"scan @ {ms} ms (rev {plate.SkillCheck.CurrentRevolution}, needle {plate.SkillCheck.NeedleAngle:0} deg)");
            window.Close();
        }

        sheet.Save("skillcheck-scan", 4, "Scanning: one revolution per read; each revolution a new check");

        // paused: frozen and grey
        sheet = new ContactSheet();
        foreach (int ms in new[] { 0, 110, 220 })
        {
            (Window window, MapPlate plate) = HostPlate(scanning, new MotionPolicy());
            plate.SkillCheck.State = SkillCheckState.Scanning;
            CampfireTestHost.Wait(TimeSpan.FromMilliseconds(260)); // the dial has entered
            plate.SkillCheck.SeekSpin(TimeSpan.FromMilliseconds(900));
            plate.SkillCheck.State = SkillCheckState.Paused;
            plate.Scene = PlateScene.Lobby(FireState.Embers, "Scan paused", "Switch back to Dead by Daylight to resume.");
            MotionDiagnostics.FreezeAllAt(TimeSpan.FromMilliseconds(ms));
            sheet.Add((FrameworkElement)plate.Parent, $"paused @ {ms} ms");
            window.Close();
        }

        sheet.Save("skillcheck-paused", 3, "Paused: the needle freezes where it is, the check greys (220 ms)");

        // the landing: the needle 84 deg short of the white block -> h = 280 ms
        double zone = SkillCheckMath.ZoneStart(0);
        double h = 84 / 0.3;
        sheet = new ContactSheet();
        foreach (double t in new[] { 0, 250, h + 2, h + 90, h + 260, h + 410, h + 700, h + 1450 })
        {
            (Window window, MapPlate plate) = HostPlate(scanning, new MotionPolicy { WindowActive = false });
            plate.SkillCheck.State = SkillCheckState.Scanning;
            plate.SkillCheck.SeekSpin(TimeSpan.FromMilliseconds((zone + 4 - 84) / 360 * 1400));
            var policy = (MotionPolicy)MotionPolicy.GetPolicy(window)!;
            policy.WindowActive = true;
            plate.SkillCheck.SeekSpin(TimeSpan.FromMilliseconds((zone + 4 - 84) / 360 * 1400));
            TimeSpan landed = plate.LandGreat("Coal Tower");
            if (t >= h + 520)
            {
                // the screen changes once the dial has gone: the fog clears off the map
                plate.Scene = PlateScene.Map("MacMillan Estate", "Coal Tower");
                plate.CaptionTag = WaitingTag();
                plate.FreezeAt(TimeSpan.FromMilliseconds(t - (h + 520)));
                plate.SkillCheck.FreezeAt(TimeSpan.FromMilliseconds(t));
                plate.SkillCheck.Score.FreezeAt(TimeSpan.FromMilliseconds(t));
            }
            else
            {
                MotionDiagnostics.FreezeAllAt(TimeSpan.FromMilliseconds(t));
            }

            sheet.Add((FrameworkElement)plate.Parent, $"found + {t:0} ms (h = {landed.TotalMilliseconds:0} ms)");
            window.Close();
        }

        sheet.Save("skillcheck-great", 4, "Map found: the needle runs on at 0.3 deg/ms and stops dead in the white block; HIT; hold 0.3 s; lift; fog clears");

        // the centre key with a long hotkey
        sheet = new ContactSheet();
        foreach (string key in new[] { "F8", "F12", "Ctrl+Shift+Q", "Alt+7", "Ctrl+Alt+Shift+NumPad5" })
        {
            (Window window, MapPlate plate) = HostPlate(scanning, new MotionPolicy { WindowActive = false });
            plate.SkillCheck.KeyText = key;
            plate.SkillCheck.State = SkillCheckState.Scanning;
            plate.SkillCheck.SeekSpin(TimeSpan.FromMilliseconds(500));
            sheet.Add((FrameworkElement)plate.Parent, key);
            window.Close();
        }

        sheet.Save("skillcheck-keys", 3, "The centre key: modifiers on their own line, nothing ever cut");
    });

    [SnapshotFact]
    public void Legs_Moments() => CampfireTestHost.RunSta(() =>
    {
        void Capture(string name, string title, Action<EntityLegs> act, double[] times, bool lean = false)
        {
            var sheet = new ContactSheet();
            foreach (double t in times)
            {
                var legs = new EntityLegs { Width = 640, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };
                var host = new Grid { Width = 900, Height = 700, Background = CampfirePalette.Night, ClipToBounds = true };
                host.Children.Add(new FogBackdrop { Layer = FogLayer.Backdrop, EmberLevel = 0.5 });
                host.Children.Add(legs);
                host.Children.Add(new FogBackdrop { Layer = FogLayer.Bank });
                MotionPolicy.SetPolicy(host, new MotionPolicy());
                CampfireTestHost.Layout(host, 900, 700);
                legs.IsLeaning = lean;
                act(legs);
                legs.FreezeAt(TimeSpan.FromMilliseconds(t));
                CampfireTestHost.Layout(host, 900, 700);
                sheet.Add(host, $"{name} @ {t:0} ms");
            }

            sheet.Save("legs-" + name, 3, title);
        }

        Capture("idle", "The Entity: idle and leaning in (scanning)", _ => { }, new[] { 0.0 });
        Capture("lean", "Lean in (900 ms ease-in-out)", l => l.Lean(true), new[] { 0.0, 450, 900 });
        Capture("rim", "Restart needed: a red rim light runs down the legs", l => l.RimRun(), new[] { 0.0, 300, 600, 900, 1300 });
        Capture("flex", "Confirmed: the flinch", l => l.Flex(), new[] { 0.0, 190, 400, 1000 });
    });

    [SnapshotFact]
    public void StatusComposite() => CampfireTestHost.RunSta(() => RenderStatusComposite(1280, 800, string.Empty));

    [SnapshotFact]
    public void StatusComposite_MinimumWindow() => CampfireTestHost.RunSta(() => RenderStatusComposite(1024, 680, "-1024x680"));

    /// <summary>
    /// The atmosphere laid out like the prototype's Status screen (no real views: the controls only), at the design size
    /// or the minimum window (1024 x 680: rail clamp(168px, 15.3vw, 220px), plate 480 x 240 by the short-window rule,
    /// the hero and the plate caption sized by the viewport type ramp).
    /// </summary>
    private static void RenderStatusComposite(double width, double height, string suffix)
    {
        bool small = width < 1180;
        double rail = Math.Round(CampfireType.Clamp(168, 15.3, 220, width));
        double plateW = small ? 480 : PlateW, plateH = small ? 240 : PlateH;
        double contentLeft = rail + (small ? 32 : 44);
        double plateLeft = small ? width - 32 - plateW : 597;
        double plateTop = small ? height - 26 - 150 - plateH : 265;
        double legs = CampfireType.Clamp(470, 50, 800, width);
        foreach ((string name, PlateScene scene, bool scan, string hero, string? backdropMap, double ember) in new[]
        {
            ("status-scanning", PlateScene.Lobby(FireState.Burning, "Looking for the map name", "Global / Menu stays on until a realm is read."), true, "Reading the loading screen", (string?)null, 0.55),
            ("status-active", PlateScene.Map("MacMillan Estate", "Coal Tower"), false, "Preset active", (string?)"Coal Tower", 1.0),
            ("status-waiting", PlateScene.Lobby(FireState.Out, "No map detected yet", "Start Dead by Daylight, then press F8 on a loading screen."), false, "Waiting for Dead by Daylight", (string?)null, 0.0),
        })
        {
            ResourceDictionary theme = CampfireThemeTests.LoadTheme();
            var root = new Grid { Width = width, Height = height, Background = CampfirePalette.Night, ClipToBounds = true };
            root.Resources.MergedDictionaries.Add(theme);
            var policy = new MotionPolicy();
            var window = CampfireTestHost.Show(root, policy, width, height);
            try
            {
                var backdrop = new FogBackdrop { Layer = FogLayer.Backdrop, EmberLevel = ember, Margin = new Thickness(0, 36, 0, 0) };
                backdrop.ShowMap(backdropMap, TimeSpan.Zero);
                var legsControl = new EntityLegs { Width = legs, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, IsLeaning = scan, IsSwayAllowed = false };
                var bank = new FogBackdrop { Layer = FogLayer.Bank };
                var railBorder = new Border
                {
                    Width = rail,
                    Margin = new Thickness(0, 36, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    BorderBrush = CampfirePalette.Brush(0.22, CampfirePalette.MistColor),
                    BorderThickness = new Thickness(0, 0, 1, 0),
                    Background = CampfirePalette.Linear(new Point(0, 0), new Point(1, 0), (0, Color.FromArgb(230, 8, 13, 15)), (0.78, Color.FromArgb(158, 8, 13, 15)), (1, Color.FromArgb(89, 8, 13, 15))),
                };
                var navStack = new StackPanel { Margin = new Thickness(0, 28, 0, 0) };
                navStack.Children.Add(Nav(theme, "Status", "CfIconStatus", true, false));
                navStack.Children.Add(Nav(theme, "Presets", "CfIconPresets", false, false));
                navStack.Children.Add(Nav(theme, "Settings", "CfIconSettings", false, false));
                railBorder.Child = navStack;
                var titleBar = new Border { Height = 36, VerticalAlignment = VerticalAlignment.Top, Background = CampfirePalette.Linear(new Point(0, 0), new Point(0, 1), (0, Color.FromRgb(0x0A, 0x10, 0x13)), (1, Color.FromRgb(0x0D, 0x15, 0x18))) };
                titleBar.Child = new TextBlock { Text = "DBD Auto ReShade", Margin = new Thickness(16, 0, 0, 0), Style = (Style)theme["CfWindowTitle"] };
                var heroText = new CrossfadeText { Text = hero, TextStyle = (Style)theme["CfHeroXL"], Width = small ? 440 : 520, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(contentLeft, small ? 58 : 70, 0, 0) };
                var plate = new MapPlate { Width = plateW, Height = plateH, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(plateLeft, plateTop, 0, 0), Scene = scene, Tone = scene.IsMap ? PlateTone.Live : PlateTone.Neutral };
                root.Children.Add(backdrop);
                root.Children.Add(legsControl);
                root.Children.Add(bank);
                root.Children.Add(heroText);
                root.Children.Add(plate);
                root.Children.Add(railBorder);
                root.Children.Add(titleBar);
                CampfireTestHost.Pump();
                if (scan)
                {
                    plate.SkillCheck.State = SkillCheckState.Scanning;
                    plate.SkillCheck.SeekSpin(TimeSpan.FromMilliseconds(2400));
                }

                CampfireTestHost.Wait(TimeSpan.FromMilliseconds(150));
                MotionDiagnostics.FreezeAllAt(TimeSpan.FromMilliseconds(2400));
                CampfireTestHost.Layout(root, width, height);
                CampfireTestHost.SavePng(root, Path.Combine(OutDir, name + suffix + ".png"));
            }
            finally
            {
                window.Close();
            }
        }
    }

    [SnapshotFact]
    public void Small_Moments() => CampfireTestHost.RunSta(() =>
    {
        // the Test countdown ring
        var sheet = new ContactSheet();
        foreach (int ms in new[] { 300, 4000, 9000, 14800 })
        {
            var ring = new CountdownRing();
            Grid host = Pad(ring, 120, 90);
            MotionPolicy.SetPolicy(host, new MotionPolicy());
            CampfireTestHost.Layout(host, 120, 90);
            ring.Start();
            ring.FreezeAt(TimeSpan.FromMilliseconds(ms));
            sheet.Add(host, $"test @ {ms} ms");
        }

        sheet.Save("countdown", 4, "Presets > Test: the 15 s focus wait as a draining skill-check ring");

        // Clear: the fog swallows the realm's strip (280 ms)
        sheet = new ContactSheet();
        foreach (int ms in new[] { 0, 90, 180, 300 })
        {
            var strip = new MapStrip { MapNames = new[] { "Coal Tower", "Groaning Storehouse", "Ironworks of Misery", "Shelter Woods", "Suffocation Pit" } };
            Grid host = Pad(strip, 140, 80);
            Window window = CampfireTestHost.Show(host, new MotionPolicy(), 140, 80);
            strip.IsFogged = true;
            strip.FreezeAt(TimeSpan.FromMilliseconds(ms));
            sheet.Add(host, $"clear @ {ms} ms", scale: 2);
            window.Close();
        }

        sheet.Save("strip-clear", 4, "Clear: the fog swallows the realm's map strip (280 ms)");

        // the hero text swap
        sheet = new ContactSheet();
        foreach (int ms in new[] { 0, 60, 160, 360 })
        {
            ResourceDictionary theme = CampfireThemeTests.LoadTheme();
            var text = new CrossfadeText { Text = "Reading the loading screen", TextStyle = (Style)theme["CfHeroXL"], Width = 640 };
            Grid host = Pad(text, 700, 220);
            Window window = CampfireTestHost.Show(host, new MotionPolicy(), 700, 220);
            text.Text = "Map found — waiting for you to click into the game";
            text.FreezeAt(TimeSpan.FromMilliseconds(ms));
            sheet.Add(host, $"hero swap @ {ms} ms");
            window.Close();
        }

        sheet.Save("hero-swap", 2, "Text changes: blur-bridged crossfade");

        // the restart banner drop
        sheet = new ContactSheet();
        foreach (int ms in new[] { 0, 100, 300 })
        {
            var banner = new RestartBanner { Text = "Restart Dead by Daylight so ReShade loads the new shortcut for Haddonfield.", Width = 700 };
            Grid host = Pad(banner, 760, 90);
            host.Resources.MergedDictionaries.Add(CampfireThemeTests.LoadTheme());
            Window window = CampfireTestHost.Show(host, new MotionPolicy(), 760, 90);
            banner.IsOpen = true;
            MotionDiagnostics.FreezeAllAt(TimeSpan.FromMilliseconds(ms));
            CampfireTestHost.Layout(host, 760, 90);
            sheet.Add(host, $"banner @ {ms} ms");
            window.Close();
        }

        sheet.Save("banner", 1, "Restart needed: the banner drops in");
    });

    private static void PlateSheet(string name, string title, PlateScene from, Action<MapPlate> act, int[] times, Action<MapPlate>? setup = null)
    {
        var sheet = new ContactSheet();
        foreach (int ms in times)
        {
            (Window window, MapPlate plate) = HostPlate(from, new MotionPolicy(), setup);
            act(plate);
            MotionDiagnostics.FreezeAllAt(TimeSpan.FromMilliseconds(ms));
            sheet.Add((FrameworkElement)plate.Parent, $"{ms} ms");
            window.Close();
        }

        sheet.Save(name, 3, title);
    }

    private static (Window Window, MapPlate Plate) HostPlate(PlateScene scene, MotionPolicy policy, Action<MapPlate>? setup = null)
    {
        var plate = new MapPlate { Width = PlateW, Height = PlateH, Scene = scene };
        setup?.Invoke(plate);
        var host = new Grid { Width = PlateW + 60, Height = PlateH + 60, Background = CampfirePalette.Night };
        host.Children.Add(plate);
        Window window = CampfireTestHost.Show(host, policy, PlateW + 60, PlateH + 60);
        CampfireTestHost.Wait(TimeSpan.FromMilliseconds(30));
        return (window, plate);
    }

    private static object WaitingTag()
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(new StatusDot { Kind = DotKind.Ring, Margin = new Thickness(0, 0, 8, 0) });
        panel.Children.Add(new TextBlock { Text = "Waiting for game focus", FontFamily = CampfireFonts.Body, FontWeight = FontWeights.SemiBold, FontSize = 13, Foreground = CampfirePalette.Ember });
        return panel;
    }

    private static Grid Pad(FrameworkElement element, double width, double height)
    {
        element.HorizontalAlignment = HorizontalAlignment.Center;
        element.VerticalAlignment = VerticalAlignment.Center;
        var grid = new Grid { Width = width, Height = height, Background = CampfirePalette.Night };
        grid.Children.Add(element);
        return grid;
    }

    private static TextBlock Styled(TextBlock text, ResourceDictionary theme, string key)
    {
        text.Style = (Style)theme[key];
        return text;
    }

    private static RadioButton Nav(ResourceDictionary theme, string label, string icon, bool selected, bool attention)
    {
        var nav = new RadioButton { Content = label, IsChecked = selected, Style = (Style)theme["CfNavItem"], GroupName = "nav" + Guid.NewGuid() };
        CampfireAssist.SetIcon(nav, (Geometry)theme[icon]);
        CampfireAssist.SetShowAttention(nav, attention);
        return nav;
    }

    private static WrapPanel Row(ResourceDictionary theme, params FrameworkElement[] items)
    {
        var row = new WrapPanel { Margin = new Thickness(0, 0, 0, 24) };
        foreach (FrameworkElement item in items)
        {
            item.Margin = new Thickness(0, 0, 18, 8);
            row.Children.Add(item);
        }

        return row;
    }

    /// <summary>A score event on its own (gallery).</summary>
    private sealed class ScoreEventProbe
    {
        public FrameworkElement Build()
        {
            var score = new ScoreEvent();
            var host = new Grid { Width = 380, Height = 110 };
            host.Children.Add(score);
            score.Show("Map found: Coal Tower");
            score.FreezeAt(TimeSpan.FromMilliseconds(900));
            return host;
        }
    }
}
