using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using DBDAutoReShade.App.Controls;
using DBDAutoReShade.App.Media;

namespace DBDAutoReShade.Tests.Campfire;

/// <summary>
/// The Campfire theme: the dictionary loads from the assembly (pack URI), every keyed template instantiates, nothing
/// falls back to the Windows light templates, the embedded fonts resolve by weight, and text contrast is at least
/// 4.5:1 on the night.
/// </summary>
public sealed class CampfireThemeTests
{
    private static readonly string[] ControlStyles =
    {
        "CfButtonSecondary", "CfButtonPrimary", "CfButtonGhost", "CfButtonDanger", "CfButtonSecondarySmall", "CfButtonPrimarySmall",
        "CfButtonDangerSmall", "CfButtonGhostSmall", "CfTitleBarButton", "CfTitleBarCloseButton", "CfPlainButton", "CfNavItem",
        "CfCheckBox", "CfSlider", "CfTextBox", "CfComboBox", "CfComboBoxItem", "CfScrollBar", "CfScrollViewer", "CfToolTip",
        "CfListBox", "CfRealmRow", "CfContextMenu", "CfMenuItem", "CfPillRed", "CfReportTagOk", "CfReportTagInfo", "CfReportTagIssue",
    };

    internal static ResourceDictionary LoadTheme() =>
        (ResourceDictionary)Application.LoadComponent(new Uri("/DBDAutoReShade;component/Themes/Campfire.xaml", UriKind.Relative));

    [Fact]
    public void Theme_Loads_AndEveryControlTemplateInstantiates()
    {
        CampfireTestHost.RunSta(() =>
        {
            ResourceDictionary theme = LoadTheme();
            var host = new StackPanel { Width = 600 };
            host.Resources.MergedDictionaries.Add(theme);
            foreach (string key in ControlStyles)
            {
                var style = Assert.IsType<Style>(theme[key]);
                Type type = style.TargetType.IsAbstract ? typeof(Button) : style.TargetType;
                var control = (FrameworkElement)Activator.CreateInstance(type)!;
                control.Style = style;
                switch (control)
                {
                    case ContentControl cc when control is not ToolTip:
                        cc.Content = key;
                        break;
                    case ComboBox combo:
                        combo.Items.Add("Global / Menu");
                        combo.Items.Add("MacMillan Estate");
                        combo.SelectedIndex = 0;
                        break;
                    case TextBox text:
                        CampfireAssist.SetPlaceholder(text, "Filter by realm or map");
                        CampfireAssist.SetIcon(text, (Geometry)theme["CfIconSearch"]);
                        break;
                    case ListBox list:
                        list.Items.Add(new ListBoxItem { Content = "Autohaven Wreckers" });
                        break;
                }

                if (control is ToolTip or ContextMenu)
                {
                    // popups: their templates are applied when opened; check the template instead
                    Assert.NotNull(style.Setters.OfType<Setter>().SingleOrDefault(s => s.Property == Control.TemplateProperty));
                    continue;
                }

                host.Children.Add(control);
                CampfireTestHost.Layout(host, 600, 4000);
                Assert.True(((Control)control).Template is not null, key + " has a template");
                Assert.True(VisualTreeHelper.GetChildrenCount(control) > 0, key + " built its visual tree");
            }
        });
    }

    [Fact]
    public void Theme_StylesEveryCommonControl_Implicitly()
    {
        CampfireTestHost.RunSta(() =>
        {
            ResourceDictionary theme = LoadTheme();
            foreach (Type type in new[]
            {
                typeof(Button), typeof(CheckBox), typeof(Slider), typeof(TextBox), typeof(ComboBox), typeof(ComboBoxItem),
                typeof(ScrollBar), typeof(ScrollViewer), typeof(ToolTip), typeof(ListBox), typeof(ListBoxItem), typeof(ContextMenu), typeof(MenuItem),
            })
            {
                var style = Assert.IsType<Style>(theme[type]);
                Assert.Equal(type, style.TargetType);
            }

            // no implicit TextBlock style (it would reach the text inside other controls' templates)
            Assert.False(theme.Contains(typeof(TextBlock)));
        });
    }

    [Fact]
    public void Buttons_PressDown_AndAreDimmedWhenDisabled()
    {
        CampfireTestHost.RunSta(() =>
        {
            ResourceDictionary theme = LoadTheme();
            var button = new Button { Content = "Apply override", Style = (Style)theme["CfButtonPrimary"] };
            Assert.Equal(0.97, PressFeedback.GetScale(button));
            var row = new ListBoxItem { Style = (Style)theme["CfRealmRow"] };
            Assert.Equal(0.98, PressFeedback.GetScale(row));
            var host = new Grid();
            host.Children.Add(button);
            MotionPolicy.SetPolicy(host, new MotionPolicy());
            CampfireTestHost.Layout(host, 300, 60);
            PressFeedback.Animate(button, pressed: true);
            var scale = Assert.IsType<ScaleTransform>(button.RenderTransform);
            Assert.True(scale.HasAnimatedProperties);
            button.IsEnabled = false;
            Assert.Equal(0.42, button.Opacity, 3);
        });
    }

    [Fact]
    public void PressFeedback_StaysStill_WhenAnimationsAreOff_OrMotionIsReduced()
    {
        CampfireTestHost.RunSta(() =>
        {
            foreach (MotionPolicy policy in new[] { new MotionPolicy { AnimationsEnabled = false }, new MotionPolicy { ReducedMotionOverride = true } })
            {
                var button = new Button { Content = "Test" };
                PressFeedback.SetScale(button, 0.97);
                MotionPolicy.SetPolicy(button, policy);
                PressFeedback.Animate(button, pressed: true);
                var scale = Assert.IsType<ScaleTransform>(button.RenderTransform);
                Assert.False(scale.HasAnimatedProperties);
                Assert.Equal(1, scale.ScaleX);
            }
        });
    }

    [Theory]
    [InlineData("Display", 600, "BigShouldersDisplay-600.ttf")]
    [InlineData("Display", 700, "BigShouldersDisplay-700.ttf")]
    [InlineData("Display", 800, "BigShouldersDisplay-800.ttf")]
    [InlineData("Body", 400, "Barlow-Regular.ttf")]
    [InlineData("Body", 500, "Barlow-Medium.ttf")]
    [InlineData("Body", 600, "Barlow-SemiBold.ttf")]
    [InlineData("Body", 700, "Barlow-Bold.ttf")]
    public void EmbeddedFonts_ResolveByWeight(string family, int weight, string file)
    {
        CampfireTestHost.RunSta(() =>
        {
            FontFamily font = family == "Display" ? CampfireFonts.Display : CampfireFonts.Body;
            var typeface = new Typeface(font, FontStyles.Normal, FontWeight.FromOpenTypeWeight(weight), FontStretches.Normal);
            Assert.True(typeface.TryGetGlyphTypeface(out GlyphTypeface glyphs), $"{family} {weight} resolves");
            Assert.EndsWith(file, Uri.UnescapeDataString(glyphs.FontUri.ToString()), StringComparison.OrdinalIgnoreCase);
            Assert.Equal(FontWeight.FromOpenTypeWeight(weight), glyphs.Weight);
        });
    }

    [Fact]
    public void TextColours_Contrast_AtLeast45To1()
    {
        Color night = CampfirePalette.NightColor;
        Color panel = Over(CampfirePalette.PanelColor, night);
        Color slate = CampfirePalette.SlateColor;
        (Color Text, Color Background, string What)[] pairs =
        {
            (CampfirePalette.BoneColor, night, "bone on night"),
            (CampfirePalette.AshColor, night, "ash on night"),
            (CampfirePalette.AshColor, panel, "ash on a panel"),
            (CampfirePalette.AshColor, slate, "ash on slate (menus, tool tips)"),
            (CampfirePalette.BoneColor, slate, "bone on slate"),
            (CampfirePalette.EmberColor, night, "ember tag on night"),
            (CampfirePalette.EntityInkColor, night, "red text on night"),
            (night, CampfirePalette.BoneColor, "primary button"),
            (Colors.White, CampfirePalette.EntityColor, "danger button"),
            (Color.FromRgb(0x7E, 0x88, 0x8A), Over(CampfirePalette.FieldColor, night), "placeholder in a field"),
        };
        foreach ((Color text, Color background, string what) in pairs)
        {
            double ratio = Contrast(text, background);
            Assert.True(ratio >= 4.5, $"{what}: {ratio:0.00}:1");
        }
    }

    [Fact]
    public void EntityGeometry_HasThreeLegs()
    {
        CampfireTestHost.RunSta(() =>
        {
            var dictionary = (ResourceDictionary)Application.LoadComponent(new Uri("/DBDAutoReShade;component/Themes/EntityGeometry.xaml", UriKind.Relative));
            foreach (string key in new[] { "EntityLegFar", "EntityLegFront", "EntityLegRight" })
            {
                var pieces = Assert.IsType<string[]>(dictionary[key]);
                Assert.InRange(pieces.Length, 8, 14);
                foreach (string piece in pieces)
                {
                    Assert.False(Geometry.Parse(piece).Bounds.IsEmpty);
                }
            }
        });
    }

    internal static double Contrast(Color a, Color b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double Luminance(Color c)
    {
        static double Channel(byte v)
        {
            double s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    private static Color Over(Color top, Color bottom)
    {
        double a = top.A / 255.0;
        return Color.FromRgb(
            (byte)Math.Round(top.R * a + bottom.R * (1 - a)),
            (byte)Math.Round(top.G * a + bottom.G * (1 - a)),
            (byte)Math.Round(top.B * a + bottom.B * (1 - a)));
    }
}
