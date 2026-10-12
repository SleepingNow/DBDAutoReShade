using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DBDAutoReShade.App;
using DBDAutoReShade.App.Controls;
using DBDAutoReShade.App.Media;
using DBDAutoReShade.App.ViewModels;
using DBDAutoReShade.App.Views;

namespace DBDAutoReShade.Tests.Campfire;

/// <summary>
/// Text drawn straight over the window's backdrop (the map picture under the fog, whose brightness depends on the
/// map) keeps at least 4.5:1 against every pixel around it, for every map picture, at the design size and at the
/// minimum: the Presets detail's head (the realm, its map count and its tag) and the thumbnails' names, and the
/// Status page's hero, status line and facts. The REAL window is rendered off screen (never activated) twice per map,
/// with those texts and without them: the pixels that differ are the ink, and the background around the ink is read
/// from the render without them, over the window's night colour as on screen.
/// </summary>
public sealed class BackdropContrastTests : IDisposable
{
    private const double Minimum = 4.5;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dbd-contrast-" + Guid.NewGuid().ToString("N"));

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

    [Theory]
    [InlineData(1280, 800)]
    [InlineData(1024, 680)]
    public void PresetsDetail_TextOverTheBackdrop_HasContrast_OnEveryMap(double width, double height)
    {
        CampfireTestHost.RunSta(() =>
        {
            WindowScene scene = WindowScene.Create(Path.Combine(_dir, $"presets-{width}"), "active", width, height,
                policy: new MotionPolicy { AnimationsEnabled = false });
            try
            {
                MainViewModel vm = scene.ViewModel;
                vm.SelectedTab = AppTab.Presets;
                scene.Settle();
                var root = (FrameworkElement)scene.Window.Content;
                var backdrop = (FogBackdrop)scene.Window.FindName("Backdrop");
                var page = (PresetsView)scene.Window.FindName("PresetsPage");
                var title = (TextBlock)page.FindName("DetailTitleText");
                var count = (TextBlock)page.FindName("DetailCountText");
                var tagText = (TextBlock)page.FindName("DetailTagText");
                var thumbs = (FrameworkElement)page.FindName("ThumbList");

                // the head's tag ("Active realm", as for the realm being played) is shown for every realm, so it is
                // measured on every map too
                ((FrameworkElement)page.FindName("DetailTag")).Visibility = Visibility.Visible;
                tagText.Text = "Active realm";

                var failures = new List<string>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (RealmRow row in vm.RealmRows.ToList())
                {
                    vm.SelectRow(row);
                    for (int i = 0; i < Math.Max(1, vm.Thumbs.Count); i++)
                    {
                        if (vm.Thumbs.Count > 0)
                        {
                            vm.Thumbs[i].SelectCommand.Execute(null); // (a one-map realm has no thumbnails: its hero is its map)
                        }

                        CampfireTestHost.Pump();
                        root.UpdateLayout();
                        string map = vm.HeroMap!;
                        Assert.Equal(map, backdrop.MapName); // the backdrop shows the hero's map
                        seen.Add(map);
                        var texts = new List<TextBlock> { title, count, tagText };
                        texts.AddRange(Descendants<TextBlock>(thumbs).Where(t => t.Name == "ThumbNameText"));
                        foreach (Measured m in Measure(root, texts).Where(m => m.Contrast < Minimum))
                        {
                            failures.Add($"{map}: {m}");
                        }
                    }
                }

                Assert.True(seen.Count >= 40, $"every map picture was behind the page ({seen.Count})");
                Assert.True(failures.Count == 0, $"{failures.Count} text(s) under {Minimum}:1 at {width}x{height}:{Environment.NewLine}" + string.Join(Environment.NewLine, failures));
            }
            finally
            {
                scene.Dispose();
            }
        }, timeoutSeconds: 300);
    }

    [Theory]
    [InlineData(1280, 800)]
    [InlineData(1024, 680)]
    public void Status_TextOverTheBackdrop_HasContrast_OnEveryMap(double width, double height)
    {
        CampfireTestHost.RunSta(() =>
        {
            WindowScene scene = WindowScene.Create(Path.Combine(_dir, $"status-{width}"), "active", width, height,
                policy: new MotionPolicy { AnimationsEnabled = false });
            try
            {
                scene.Settle();
                var root = (FrameworkElement)scene.Window.Content;
                var backdrop = (FogBackdrop)scene.Window.FindName("Backdrop");
                var page = (StatusView)scene.Window.FindName("StatusPage");
                var hero = (FrameworkElement)page.FindName("Hero");
                var facts = (FrameworkElement)page.FindName("Facts");
                var failures = new List<string>();
                foreach (string map in scene.ViewModel.RealmRows.SelectMany(r => r.MapNames).Distinct().ToList())
                {
                    // the Status page shows the map the game is on: every map's picture behind the same screen
                    backdrop.ShowMap(map, TimeSpan.Zero);
                    CampfireTestHost.Pump();
                    root.UpdateLayout();
                    List<TextBlock> texts = Descendants<TextBlock>(hero).Concat(Descendants<TextBlock>(facts)).ToList();
                    foreach (Measured m in Measure(root, texts).Where(m => m.Contrast < Minimum))
                    {
                        failures.Add($"{map}: {m}");
                    }
                }

                Assert.True(failures.Count == 0, $"{failures.Count} text(s) under {Minimum}:1 at {width}x{height}:{Environment.NewLine}" + string.Join(Environment.NewLine, failures));
            }
            finally
            {
                scene.Dispose();
            }
        }, timeoutSeconds: 300);
    }

    /// <summary>A text's lowest contrast against the backdrop around its ink, and where that was.</summary>
    private sealed record Measured(string Text, double Contrast, int X, int Y, Color Background)
    {
        public override string ToString() => $"\"{Text}\" {Contrast:0.00}:1 at ({X},{Y}) on #{Background.R:X2}{Background.G:X2}{Background.B:X2}";
    }

    /// <summary>
    /// Renders <paramref name="root"/> with and without <paramref name="texts"/> (visible, non-empty, drawn in one solid
    /// colour) and returns, for each text that drew ink, its lowest contrast against the background pixels within 2 px
    /// of its ink (its colour composed with its opacity over each pixel).
    /// </summary>
    private static List<Measured> Measure(FrameworkElement root, IEnumerable<TextBlock> texts)
    {
        List<TextBlock> measured = texts
            .Where(t => t.IsVisible && !string.IsNullOrWhiteSpace(t.Text) && t.Foreground is SolidColorBrush && Effective(t) > 0.01)
            .Distinct()
            .ToList();
        int w = (int)Math.Ceiling(root.ActualWidth), h = (int)Math.Ceiling(root.ActualHeight);
        Color[] with = OverNight(root, w, h);
        var opacities = measured.Select(t => t.Opacity).ToList();
        measured.ForEach(t => t.Opacity = 0);
        root.UpdateLayout();
        Color[] without = OverNight(root, w, h);
        for (int i = 0; i < measured.Count; i++)
        {
            measured[i].Opacity = opacities[i];
        }

        root.UpdateLayout();
        var results = new List<Measured>();
        foreach (TextBlock text in measured)
        {
            Rect bounds = text.TransformToAncestor(root).TransformBounds(new Rect(text.RenderSize));
            int x0 = Math.Max(0, (int)Math.Floor(bounds.Left)), y0 = Math.Max(0, (int)Math.Floor(bounds.Top));
            int x1 = Math.Min(w - 1, (int)Math.Ceiling(bounds.Right)), y1 = Math.Min(h - 1, (int)Math.Ceiling(bounds.Bottom));
            int inkLeft = int.MaxValue, inkTop = int.MaxValue, inkRight = -1, inkBottom = -1;
            for (int y = y0; y <= y1; y++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    Color a = with[y * w + x], b = without[y * w + x];
                    if (Math.Max(Math.Abs(a.R - b.R), Math.Max(Math.Abs(a.G - b.G), Math.Abs(a.B - b.B))) > 24)
                    {
                        inkLeft = Math.Min(inkLeft, x);
                        inkTop = Math.Min(inkTop, y);
                        inkRight = Math.Max(inkRight, x);
                        inkBottom = Math.Max(inkBottom, y);
                    }
                }
            }

            if (inkRight < 0)
            {
                continue; // nothing drawn (scrolled out of view)
            }

            Color ink = ((SolidColorBrush)text.Foreground).Color;
            double alpha = ink.A / 255.0 * Effective(text);
            var worst = new Measured(text.Text, double.MaxValue, 0, 0, Colors.Black);
            for (int y = Math.Max(0, inkTop - 2); y <= Math.Min(h - 1, inkBottom + 2); y++)
            {
                for (int x = Math.Max(0, inkLeft - 2); x <= Math.Min(w - 1, inkRight + 2); x++)
                {
                    Color background = without[y * w + x];
                    double ratio = CampfireThemeTests.Contrast(Blend(ink, background, alpha), background);
                    if (ratio < worst.Contrast)
                    {
                        worst = new Measured(text.Text, ratio, x, y, background);
                    }
                }
            }

            results.Add(worst);
        }

        return results;
    }

    /// <summary>The element's opacity with its ancestors' (up to the window).</summary>
    private static double Effective(UIElement element)
    {
        double opacity = 1;
        for (DependencyObject? at = element; at is not null; at = VisualTreeHelper.GetParent(at))
        {
            if (at is UIElement ui)
            {
                opacity *= ui.Opacity;
            }
        }

        return opacity;
    }

    private static Color Blend(Color top, Color bottom, double alpha) => Color.FromRgb(
        (byte)Math.Round(top.R * alpha + bottom.R * (1 - alpha)),
        (byte)Math.Round(top.G * alpha + bottom.G * (1 - alpha)),
        (byte)Math.Round(top.B * alpha + bottom.B * (1 - alpha)));

    /// <summary>The element's pixels as shown in the window: rendered at 96 DPI, composed over the night background.</summary>
    private static Color[] OverNight(FrameworkElement root, int w, int h)
    {
        var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        byte[] px = new byte[w * h * 4];
        bitmap.CopyPixels(px, w * 4, 0);
        Color night = CampfirePalette.NightColor;
        var colors = new Color[w * h];
        for (int i = 0; i < colors.Length; i++)
        {
            double rest = 1 - px[i * 4 + 3] / 255.0; // premultiplied: the night shows through what is not covered
            colors[i] = Color.FromRgb(
                (byte)Math.Round(Math.Min(255, px[i * 4 + 2] + night.R * rest)),
                (byte)Math.Round(Math.Min(255, px[i * 4 + 1] + night.G * rest)),
                (byte)Math.Round(Math.Min(255, px[i * 4] + night.B * rest)));
        }

        return colors;
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
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
}
