using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DBDAutoReShade.App.Media;

namespace DBDAutoReShade.Tests.Campfire;

/// <summary>
/// Runs Campfire UI work on its own STA thread (no Application is ever created: that would change how the
/// view model marshals in other tests), hosts controls in an off-screen window when a test needs them loaded,
/// pumps the dispatcher, and renders elements to PNG.
/// </summary>
internal static class CampfireTestHost
{
    // WPF's process-wide one-time state is created once, on one thread, before any Campfire STA test runs.
    private static readonly Lazy<bool> Warm = new(WarmUp, LazyThreadSafetyMode.ExecutionAndPublication);

    // One Campfire STA test at a time: WPF reads the assembly's BAML through one shared package part whose stream
    // bookkeeping is not thread-safe (PackagePart.GetStream / CleanUpRequestedStreamsList), so two dispatcher threads
    // creating XAML-backed controls at once (a window's InitializeComponent, a plate's new CampfireScene) can fail
    // with an index error. The app has a single UI thread; only the test runner has several.
    private static readonly SemaphoreSlim OneUiThread = new(1, 1);

    /// <summary>Runs <paramref name="action"/> on a fresh STA thread (one such test at a time) and rethrows its failure.</summary>
    public static void RunSta(Action action, int timeoutSeconds = 120)
    {
        _ = Warm.Value;
        OneUiThread.Wait();
        try
        {
            RunStaThread(action, timeoutSeconds);
        }
        finally
        {
            OneUiThread.Release();
        }
    }

    /// <summary>
    /// The test classes run in parallel, so the first STA test of every class starts at the same instant on its own
    /// dispatcher thread. Several dispatcher threads creating WPF's process-wide state together (the system theme
    /// dictionaries, the shared BAML schema, the font cache, the pack resource package) can deadlock inside WPF
    /// (seen as every first test hanging at once). The app has a single UI thread, so this only concerns the test
    /// runner: one thread builds that state first (the theme, every templated control, the Campfire fonts, the map
    /// pictures, the leg geometry, the policies), then the tests run in parallel as before.
    /// </summary>
    private static bool WarmUp()
    {
        RunStaThread(() =>
        {
            var theme = (ResourceDictionary)Application.LoadComponent(new Uri("/DBDAutoReShade;component/Themes/Campfire.xaml", UriKind.Relative));
            _ = MotionPolicy.Current;
            var root = new StackPanel();
            root.Resources.MergedDictionaries.Add(theme);
            MotionPolicy.SetPolicy(root, new MotionPolicy { AnimationsEnabled = false });
            root.Children.Add(new Button { Content = "Warm" });
            root.Children.Add(new CheckBox { Content = "Warm" });
            root.Children.Add(new TextBox { Text = "Warm" });
            root.Children.Add(new ComboBox { ItemsSource = new[] { "Warm" }, SelectedIndex = 0 });
            root.Children.Add(new Slider());
            root.Children.Add(new ListBox { ItemsSource = new[] { "Warm" } });
            root.Children.Add(new ScrollViewer { Height = 20, Content = new TextBlock { Text = "Warm", FontFamily = CampfireFonts.Body } });
            root.Children.Add(new TextBlock { Text = "Warm", Style = (Style)theme["CfHeroL"] });
            root.Children.Add(new TextBlock { Text = "Warm", Style = (Style)theme["CfHeroMessage"] });
            root.Children.Add(new DBDAutoReShade.App.Controls.Keycap { Text = "F8" });
            root.Children.Add(new DBDAutoReShade.App.Controls.MapPlate { Width = 200, Height = 100, Scene = DBDAutoReShade.App.Controls.PlateScene.Lobby(DBDAutoReShade.App.Controls.FireState.Burning, "Warm", "Warm") });
            root.Children.Add(new DBDAutoReShade.App.Controls.MapPicture { MapName = "Coal Tower", Width = 100, Height = 50 });
            root.Children.Add(new DBDAutoReShade.App.Controls.EntityLegs { Width = 200, Height = 300 });
            Window window = Show(root, new MotionPolicy { AnimationsEnabled = false }, 400, 900);
            window.Close();
        }, 120);
        return true;
    }

    private static void RunStaThread(Action action, int timeoutSeconds)
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
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(timeoutSeconds)), "STA work timed out");
        if (failure is not null)
        {
            throw new Xunit.Sdk.XunitException($"{failure.GetType().Name}: {failure.Message}{Environment.NewLine}{failure}");
        }
    }

    /// <summary>Processes everything queued on the current dispatcher down to <paramref name="priority"/>.</summary>
    public static void Pump(DispatcherPriority priority = DispatcherPriority.ApplicationIdle)
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(priority, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    /// <summary>Lets the dispatcher (and the animation clocks) run for <paramref name="time"/>.</summary>
    public static void Wait(TimeSpan time)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = time };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    /// <summary>
    /// Shows <paramref name="content"/> in a borderless, inactive window far off screen (so Loaded fires and
    /// clocks tick), with <paramref name="policy"/> set on the window. Close it when done.
    /// </summary>
    public static Window Show(FrameworkElement content, MotionPolicy policy, double width, double height)
    {
        var window = new Window
        {
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            Left = -20000,
            Top = -20000,
            Width = width,
            Height = height,
            Background = CampfirePalette.Night,
            Content = content,
        };
        MotionPolicy.SetPolicy(window, policy);
        window.Show();
        Pump();
        return window;
    }

    /// <summary>Lays out <paramref name="element"/> at <paramref name="width"/> x <paramref name="height"/> (no window).</summary>
    public static void Layout(FrameworkElement element, double width, double height)
    {
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();
    }

    /// <summary>Renders <paramref name="element"/> (laid out at its size) over the night colour to a PNG.</summary>
    public static void SavePng(FrameworkElement element, string path, double scale = 1.0)
    {
        double width = Math.Max(1, element.ActualWidth);
        double height = Math.Max(1, element.ActualHeight);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width * scale), (int)Math.Ceiling(height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.DrawRectangle(CampfirePalette.Night, null, new Rect(0, 0, width, height));
            dc.DrawRectangle(new VisualBrush(element) { Stretch = Stretch.None, Viewbox = new Rect(0, 0, width, height), ViewboxUnits = BrushMappingMode.Absolute, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, new Rect(0, 0, width, height));
        }

        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using FileStream stream = File.Create(path);
        encoder.Save(stream);
    }

    /// <summary>Renders <paramref name="element"/> to a 96-DPI bitmap (pixel checks).</summary>
    public static BitmapSource Render(FrameworkElement element)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>The BGRA pixel at (<paramref name="x"/>, <paramref name="y"/>).</summary>
    public static Color PixelAt(BitmapSource bitmap, int x, int y)
    {
        byte[] px = new byte[4];
        bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), px, 4, 0);
        return Color.FromArgb(px[3], px[2], px[1], px[0]);
    }

    /// <summary>Wraps <paramref name="element"/> in a night-coloured grid of the given size.</summary>
    public static Grid Frame(UIElement element, double width, double height)
    {
        var grid = new Grid { Width = width, Height = height, Background = CampfirePalette.Night };
        grid.Children.Add(element);
        return grid;
    }

    /// <summary>The repository root (the folder with DBDAutoReShade.slnx).</summary>
    public static string RepoRoot()
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
