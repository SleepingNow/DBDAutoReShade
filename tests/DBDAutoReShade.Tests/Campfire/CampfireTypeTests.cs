using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DBDAutoReShade.App.Controls;
using DBDAutoReShade.App.Media;

namespace DBDAutoReShade.Tests.Campfire;

/// <summary>
/// The viewport-scaled type ramp: the prototype's clamp(min, n·vw, max) sizes, its short-window hero ramp
/// (max-height 720 px) and the 13ch / 27ch hero widths, applied by the Cf* text styles to the window they are in.
/// </summary>
public sealed class CampfireTypeTests
{
    [Theory]
    [InlineData(TypeRamp.HeroXL, 1280, 800, 89.6)] // 7vw
    [InlineData(TypeRamp.HeroL, 1280, 800, 71.68)] // 5.6vw
    [InlineData(TypeRamp.HeroM, 1280, 800, 52.48)] // 4.1vw
    [InlineData(TypeRamp.DetailTitle, 1280, 800, 47.36)] // 3.7vw
    [InlineData(TypeRamp.PlateRealm, 1280, 800, 35.84)] // 2.8vw
    [InlineData(TypeRamp.HeroMap, 1280, 800, 29.44)] // 2.3vw
    [InlineData(TypeRamp.HeroXL, 1024, 680, 57.344)] // the minimum window is short: clamp(54px, 5.6vw, 82px)
    [InlineData(TypeRamp.HeroL, 1024, 680, 47.104)] // short: clamp(46px, 4.6vw, 68px)
    [InlineData(TypeRamp.HeroM, 1024, 680, 41.984)]
    [InlineData(TypeRamp.DetailTitle, 1024, 680, 38)] // 37.9 -> the 38 px floor
    [InlineData(TypeRamp.PlateRealm, 1024, 680, 28.672)]
    [InlineData(TypeRamp.HeroMap, 1024, 680, 24)] // 23.6 -> the 24 px floor
    [InlineData(TypeRamp.HeroXL, 1024, 800, 71.68)] // narrow but tall: 7vw
    [InlineData(TypeRamp.HeroXL, 1920, 1080, 108)] // the 108 px ceiling
    [InlineData(TypeRamp.HeroXL, 0, 0, 89.6)] // no viewport: the 1280 x 800 design
    public void Sizes_FollowThePrototypesClamps(TypeRamp ramp, double width, double height, double fontSize) => CampfireTestHost.RunSta(() =>
    {
        TypeMetrics metrics = CampfireType.Measure(ramp, new Size(width, height));
        Assert.Equal(fontSize, metrics.FontSize, 3);
    });

    [Fact]
    public void LineHeights_AndMaxWidths_FollowThePrototype() => CampfireTestHost.RunSta(() =>
    {
        var design = new Size(1280, 800);
        TypeMetrics xl = CampfireType.Measure(TypeRamp.HeroXL, design);
        Assert.Equal(xl.FontSize * 0.92, xl.LineHeight, 6);
        Assert.Equal(CampfireType.HeroBlockMaxWidth, xl.MaxWidth); // inside the 820 px hero block

        // 13ch / 27ch of Big Shoulders Display 800
        TypeMetrics l = CampfireType.Measure(TypeRamp.HeroL, design);
        double ch = CampfireType.ChWidth(CampfireFonts.Display, FontWeights.ExtraBold, l.FontSize);
        Assert.InRange(ch / l.FontSize, 0.3, 0.75);
        Assert.NotEqual(0.5, ch / l.FontSize, 6); // read from the font, not the fallback
        Assert.Equal(13 * ch, l.MaxWidth, 6);
        TypeMetrics m = CampfireType.Measure(TypeRamp.HeroM, design);
        Assert.Equal(Math.Min(CampfireType.HeroBlockMaxWidth, 27 * CampfireType.ChWidth(CampfireFonts.Display, FontWeights.ExtraBold, m.FontSize)), m.MaxWidth, 6);

        // the status line: Barlow 15 / 1.45, 70ch
        TypeMetrics message = CampfireType.Measure(TypeRamp.HeroMessage, design);
        Assert.Equal(15, message.FontSize);
        Assert.Equal(21.75, message.LineHeight, 6);
        Assert.Equal(Math.Min(CampfireType.HeroBlockMaxWidth, 70 * CampfireType.ChWidth(CampfireFonts.Body, FontWeights.Normal, 15)), message.MaxWidth, 6);

        TypeMetrics title = CampfireType.Measure(TypeRamp.DetailTitle, design);
        Assert.Equal(title.FontSize * 0.95, title.LineHeight, 6);
        Assert.True(double.IsPositiveInfinity(title.MaxWidth));
        Assert.True(double.IsNaN(CampfireType.Measure(TypeRamp.HeroMap, design).LineHeight));

        // the layout clamps use the same formula
        Assert.Equal(195.84, CampfireType.Clamp(168, 15.3, 220, 1280), 3); // rail
        Assert.Equal(168, CampfireType.Clamp(168, 15.3, 220, 1024), 3);
        Assert.Equal(640, CampfireType.Clamp(470, 50, 800, 1280), 3); // Entity legs
    });

    [Fact]
    public void TheStyles_FollowTheWindow_AndExplicitSizesWin()
    {
        CampfireTestHost.RunSta(() =>
        {
            ResourceDictionary theme = CampfireThemeTests.LoadTheme();
            var root = new StackPanel();
            root.Resources.MergedDictionaries.Add(theme);
            var hero = new TextBlock { Text = "Waiting for Dead by Daylight", Style = (Style)theme["CfHeroXL"] };
            var heroL = new TextBlock { Text = "Map found, waiting for you to click into the game", Style = (Style)theme["CfHeroL"] };
            var fixedSize = new TextBlock { Text = "Fixed", Style = (Style)theme["CfHeroXL"], FontSize = 40 };
            var crossfade = new CrossfadeText { Text = "Preset active", TextStyle = (Style)theme["CfHeroXL"] };
            var plate = new MapPlate { Width = 480, Height = 240, Scene = PlateScene.Map("MacMillan Estate", "Coal Tower") };
            root.Children.Add(hero);
            root.Children.Add(heroL);
            root.Children.Add(fixedSize);
            root.Children.Add(crossfade);
            root.Children.Add(plate);

            // outside a window: the 1280 x 800 design sizes
            CampfireTestHost.Pump();
            Assert.Equal(89.6, hero.FontSize, 3);

            // the minimum window (1024 x 680, a short window)
            Window window = CampfireTestHost.Show(root, new MotionPolicy(), 1024, 680);
            try
            {
                CampfireTestHost.Pump();
                Assert.Equal(57.344, hero.FontSize, 3);
                Assert.Equal(57.344 * 0.92, hero.LineHeight, 3);
                Assert.Equal(CampfireType.HeroBlockMaxWidth, hero.MaxWidth, 3);
                Assert.Equal(47.104, heroL.FontSize, 3);
                Assert.Equal(13 * CampfireType.ChWidth(CampfireFonts.Display, FontWeights.ExtraBold, 47.104), heroL.MaxWidth, 3);
                Assert.Equal(40, fixedSize.FontSize); // an explicit size wins
                Assert.Equal(57.344, crossfade.TextElement.FontSize, 3);
                TextBlock caption = FindText(plate, "MacMillan Estate");
                Assert.Equal(28.672, caption.FontSize, 3); // the plate caption: clamp(28px, 2.8vw, 38px)
                Assert.Equal(28.672 * 0.95, caption.LineHeight, 3);

                // the window grows to the design size: the text follows
                window.Width = 1280;
                window.Height = 800;
                CampfireTestHost.Pump();
                Assert.Equal(89.6, hero.FontSize, 3);
                Assert.Equal(71.68, heroL.FontSize, 3);
                Assert.Equal(35.84, FindText(plate, "MacMillan Estate").FontSize, 3);

                // a fixed plate title size, and the ramp switched off: the style's own size
                plate.TitleFontSize = 30;
                Assert.Equal(30, FindText(plate, "MacMillan Estate").FontSize, 3);
                CampfireType.SetRamp(hero, TypeRamp.None);
                Assert.Equal(90, hero.FontSize, 3);
                Assert.Equal(83, hero.LineHeight, 3);
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static TextBlock FindText(DependencyObject root, string text)
    {
        var queue = new Queue<DependencyObject>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            DependencyObject node = queue.Dequeue();
            if (node is TextBlock block && block.Text == text)
            {
                return block;
            }

            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            {
                queue.Enqueue(VisualTreeHelper.GetChild(node, i));
            }
        }

        throw new Xunit.Sdk.XunitException($"No TextBlock \"{text}\".");
    }
}
