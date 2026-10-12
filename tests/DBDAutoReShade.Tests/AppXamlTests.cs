using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using DBDAutoReShade.App;
using DBDAutoReShade.App.Media;
using DBDAutoReShade.App.ViewModels;
using DBDAutoReShade.Tests.Campfire;

namespace DBDAutoReShade.Tests;

/// <summary>
/// C23, Campfire edition: the application resources are the Campfire theme and only that (the old light-chrome
/// workarounds are gone), every themed control type gets its own template from the theme (nothing falls back to the
/// light Windows templates), and the REAL windows load with it: dark background, light text, every button, check box,
/// slider, text box, combo box, list item and scroll bar themed. No Application is created (that would change how the
/// view model marshals its refreshes): windows built without one merge the same theme themselves.
/// </summary>
public sealed class AppXamlTests
{
    private static readonly string AppDir = Path.Combine(CampfireTestHost.RepoRoot(), "src", "DBDAutoReShade.App");

    /// <summary>The control types the theme styles implicitly (each style must set a template).</summary>
    private static readonly Type[] ThemedTypes =
    {
        typeof(Button), typeof(CheckBox), typeof(Slider), typeof(TextBox), typeof(ComboBox), typeof(ComboBoxItem),
        typeof(ScrollBar), typeof(ScrollViewer), typeof(ToolTip), typeof(ListBox), typeof(ListBoxItem), typeof(ContextMenu), typeof(MenuItem),
    };

    [Fact]
    public void AppXaml_MergesTheCampfireTheme_AndNothingElse()
    {
        string xaml = File.ReadAllText(Path.Combine(AppDir, "App.xaml"));
        Match resources = Regex.Match(xaml, @"<Application\.Resources>(?<body>.*)</Application\.Resources>", RegexOptions.Singleline);
        Assert.True(resources.Success);
        string body = Regex.Replace(resources.Groups["body"].Value, "<!--.*?-->", string.Empty, RegexOptions.Singleline);
        Assert.Contains("<ResourceDictionary Source=\"" + CampfireTheme.Source + "\"/>", body);

        // the old theme is gone: no own brushes, styles or templates next to the theme
        Assert.DoesNotContain("<Style", body);
        Assert.DoesNotContain("<SolidColorBrush", body);
        Assert.DoesNotContain("<Color", body);
        Assert.DoesNotContain("AppWindow", xaml);
    }

    [Fact]
    public void Theme_StylesEveryControlType_WithItsOwnTemplate()
    {
        CampfireTestHost.RunSta(() =>
        {
            ResourceDictionary theme = CampfireTheme.ForCurrentThread();
            foreach (Type type in ThemedTypes)
            {
                var style = Assert.IsType<Style>(theme[type]);
                Assert.True(SetsTemplate(style), $"The implicit {type.Name} style sets no template: Windows' light one would show.");
            }

            // no application-wide TextBlock style (it would reach the text inside other controls' templates)
            Assert.False(theme.Contains(typeof(TextBlock)));

            // the combo box is dark now: its text is light
            Setter foreground = AllSetters((Style)theme[typeof(ComboBox)]).First(s => s.Property == Control.ForegroundProperty);
            Assert.True(Luminance(((SolidColorBrush)foreground.Value).Color) > 0.7);
        });
    }

    [Fact]
    public void MainWindow_LoadsWithTheTheme_AndNoLightTemplatesLeak()
    {
        CampfireTestHost.RunSta(() =>
        {
            string dir = Path.Combine(Path.GetTempPath(), "dbd-xaml-" + Guid.NewGuid().ToString("N"));
            WindowScene scene = WindowScene.Create(dir, "active", show: true);
            try
            {
                MainWindow window = scene.Window;
                Assert.True(Luminance(((SolidColorBrush)window.Background).Color) < 0.1, "dark window background");
                Assert.True(Luminance(((SolidColorBrush)window.Foreground).Color) > 0.7, "light window text");
                foreach (AppTab tab in Enum.GetValues<AppTab>())
                {
                    scene.ViewModel.SelectedTab = tab;
                    scene.Settle();
                    AssertThemed(window);
                }
            }
            finally
            {
                scene.Dispose();
                TryDelete(dir);
            }
        });
    }

    [Fact]
    public void SetupWizard_LoadsWithTheTheme_AndNoLightTemplatesLeak()
    {
        CampfireTestHost.RunSta(() =>
        {
            string dir = Path.Combine(Path.GetTempPath(), "dbd-xaml-" + Guid.NewGuid().ToString("N"));
            WindowScene scene = WindowScene.Create(dir, "ready", show: false);
            try
            {
                var wizard = new SetupWizardWindow(scene.ViewModel, new MotionPolicy { AnimationsEnabled = false })
                {
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    ShowInTaskbar = false,
                    ShowActivated = false,
                    Left = -20000,
                    Top = -20000,
                };
                wizard.Show();
                CampfireTestHost.Pump();
                Assert.True(Luminance(((SolidColorBrush)wizard.Background).Color) < 0.1, "dark window background");
                Assert.True(Luminance(((SolidColorBrush)wizard.Foreground).Color) > 0.7, "light window text");
                AssertThemed(wizard);
                wizard.Close();
            }
            finally
            {
                scene.Dispose();
                TryDelete(dir);
            }
        });
    }

    [Fact]
    public void Texts_PromiseGlobalOnlyWhenOneIsSet()
    {
        // No Global preset may be set (it is optional): then a realm without a preset switches nothing, so no text
        // may say it "uses Global" outright. The view model words it by the setting (MainViewModelCampfireTests).
        foreach (string file in new[] { "MainWindow.xaml", "Views/StatusView.xaml", "Views/PresetsView.xaml", "Views/SettingsView.xaml" })
        {
            string xaml = File.ReadAllText(Path.Combine(AppDir, file));
            Assert.DoesNotContain("use Global", xaml, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("uses Global", xaml, StringComparison.OrdinalIgnoreCase);
        }

        string wizard = File.ReadAllText(Path.Combine(AppDir, "SetupWizardWindow.xaml"));
        Assert.DoesNotContain("simply use Global", wizard);
        Assert.Contains("use the Global preset, if you set one", wizard);
    }

    /// <summary>Every control of a themed type in <paramref name="root"/>'s visual tree has a Campfire template.</summary>
    private static void AssertThemed(DependencyObject root)
    {
        int checkedControls = 0;
        foreach (Control control in Descendants(root).OfType<Control>())
        {
            if (!ThemedTypes.Contains(control.GetType()) && control is not RadioButton)
            {
                continue;
            }

            checkedControls++;
            Assert.True(control.Style is not null && SetsTemplate(control.Style),
                $"{control.GetType().Name} '{control.Name}' uses Windows' default template.");
        }

        Assert.True(checkedControls > 5, "the window was not laid out");
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        var stack = new Stack<DependencyObject>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            DependencyObject node = stack.Pop();
            yield return node;
            int count = node is Visual ? VisualTreeHelper.GetChildrenCount(node) : 0;
            for (int i = 0; i < count; i++)
            {
                stack.Push(VisualTreeHelper.GetChild(node, i));
            }
        }
    }

    private static bool SetsTemplate(Style style) => AllSetters(style).Any(s => s.Property == Control.TemplateProperty);

    private static IEnumerable<Setter> AllSetters(Style? style)
    {
        for (; style is not null; style = style.BasedOn)
        {
            foreach (Setter setter in style.Setters.OfType<Setter>())
            {
                yield return setter;
            }
        }
    }

    private static double Luminance(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;

    private static void TryDelete(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch
        {
            // Best effort.
        }
    }
}
