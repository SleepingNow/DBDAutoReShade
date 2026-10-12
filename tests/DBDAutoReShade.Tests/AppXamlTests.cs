using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;

namespace DBDAutoReShade.Tests;

/// <summary>
/// C23: the app's resources and windows load at runtime (resource keys,
/// templates, bindings), and the dark theme really reaches the windows and
/// the controls whose default templates ignore Background. The XAML sources
/// are parsed as loose XAML on an STA thread; no Application is created
/// (that would change how the view model marshals its refreshes).
/// </summary>
public sealed class AppXamlTests
{
    private static readonly string AppDir = Path.Combine(RepoRoot(), "src", "DBDAutoReShade.App");

    [Fact]
    public void AppResources_DefineTheDarkThemeExplicitly()
    {
        RunSta(() =>
        {
            ResourceDictionary app = LoadAppResources();

            // Implicit Window styles never reach MainWindow/SetupWizardWindow (exact type match).
            Assert.False(app.Contains(typeof(Window)));
            var window = Assert.IsType<Style>(app["AppWindow"]);
            Assert.Contains(window.Setters.OfType<Setter>(), s => s.Property == Control.BackgroundProperty);

            // No app-wide TextBlock style: it would recolour the text inside the ComboBox's light chrome.
            Assert.False(app.Contains(typeof(TextBlock)));

            // TabItem and Button paint their own (dark) chrome in every state.
            Assert.NotNull(((Style)app[typeof(TabItem)]).Setters.OfType<Setter>()
                .SingleOrDefault(s => s.Property == Control.TemplateProperty));
            Assert.NotNull(((Style)app[typeof(Button)]).Setters.OfType<Setter>()
                .SingleOrDefault(s => s.Property == Control.TemplateProperty));

            // The ComboBox keeps its light system chrome, so its text is dark.
            var combo = (Style)app[typeof(ComboBox)];
            var foreground = (SolidColorBrush)combo.Setters.OfType<Setter>().Single(s => s.Property == Control.ForegroundProperty).Value;
            Assert.True(Luminance(foreground.Color) < 0.2);
        });
    }

    [Fact]
    public void TabItemTemplate_KeepsSelectedTabsReadable()
    {
        RunSta(() =>
        {
            ResourceDictionary app = LoadAppResources();
            var tabs = new TabControl { Style = (Style)app[typeof(TabControl)] };
            var selected = new TabItem { Header = "Status", Style = (Style)app[typeof(TabItem)], Content = new TextBlock { Text = "x" } };
            var other = new TabItem { Header = "Presets", Style = (Style)app[typeof(TabItem)] };
            tabs.Items.Add(selected);
            tabs.Items.Add(other);
            tabs.SelectedItem = selected;
            tabs.Measure(new Size(400, 300));
            tabs.Arrange(new Rect(0, 0, 400, 300));

            Assert.True(selected.ApplyTemplate() || selected.Template is not null);
            var chrome = (Border)selected.Template.FindName("Chrome", selected);
            Color text = ((SolidColorBrush)selected.Foreground).Color;
            Color background = ((SolidColorBrush)chrome.Background).Color;
            Assert.True(Luminance(text) - Luminance(background) > 0.5, "selected tab text must contrast with its background");
        });
    }

    [Theory]
    [InlineData("MainWindow.xaml")]
    [InlineData("SetupWizardWindow.xaml")]
    public void Windows_LoadWithTheAppResources(string file)
    {
        RunSta(() =>
        {
            ResourceDictionary app = LoadAppResources();
            string xaml = File.ReadAllText(Path.Combine(AppDir, file));
            Assert.Contains("Style=\"{StaticResource AppWindow}\"", xaml); // the theme reaches this window

            // Loose XAML: no code-behind class or handlers; app resources are merged after parsing.
            xaml = Regex.Replace(xaml, @"\s+x:Class=""[^""]+""", string.Empty);
            xaml = Regex.Replace(xaml, @"\s+Click=""[^""]+""", string.Empty);
            xaml = xaml.Replace("clr-namespace:DBDAutoReShade.App.Infrastructure\"", "clr-namespace:DBDAutoReShade.App.Infrastructure;assembly=DBDAutoReShade\"");
            foreach (string key in new[] { "AppWindow", "MutedText", "LabelText", "ErrorText", "BorderBrush" })
            {
                xaml = xaml.Replace($"{{StaticResource {key}}}", $"{{DynamicResource {key}}}");
            }

            var window = (Window)XamlReader.Parse(xaml);
            try
            {
                window.Resources.MergedDictionaries.Add(app);
                var content = (FrameworkElement)window.Content;
                content.Measure(new Size(600, 640));
                content.Arrange(new Rect(0, 0, 600, 640));

                Assert.Same(app["AppWindow"], window.Style);
                Color background = ((SolidColorBrush)window.Background).Color;
                Color text = ((SolidColorBrush)window.Foreground).Color;
                Assert.True(Luminance(background) < 0.1, "dark window background");
                Assert.True(Luminance(text) > 0.7, "light window text");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void Texts_PromiseGlobalOnlyWhenOneIsSet()
    {
        // No Global preset may be set (it is optional): then a realm without a
        // preset switches nothing, so no text may say it "uses Global" outright.
        string main = File.ReadAllText(Path.Combine(AppDir, "MainWindow.xaml"));
        Assert.Contains("Command=\"{Binding ClearCommand}\" ToolTip=\"{Binding ClearToolTip}\"", main);
        Assert.DoesNotContain("use Global", main);

        string wizard = File.ReadAllText(Path.Combine(AppDir, "SetupWizardWindow.xaml"));
        Assert.DoesNotContain("simply use Global", wizard);
        Assert.Contains("use the Global preset, if you set one", wizard);
    }

    private static ResourceDictionary LoadAppResources()
    {
        string xaml = File.ReadAllText(Path.Combine(AppDir, "App.xaml"));
        Match resources = Regex.Match(xaml, @"<Application\.Resources>(?<body>.*)</Application\.Resources>", RegexOptions.Singleline);
        Assert.True(resources.Success);
        return (ResourceDictionary)XamlReader.Parse(
            "<ResourceDictionary xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" " +
            "xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">" + resources.Groups["body"].Value + "</ResourceDictionary>");
    }

    private static double Luminance(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "STA work timed out");
        if (failure is not null)
        {
            throw new Xunit.Sdk.XunitException($"{failure.GetType().Name}: {failure.Message}{Environment.NewLine}{failure}");
        }
    }

    private static string RepoRoot()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "DBDAutoReShade.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Repository root (DBDAutoReShade.slnx) not found.");
    }
}
