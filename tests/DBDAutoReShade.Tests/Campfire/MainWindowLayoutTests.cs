using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DBDAutoReShade.App;
using DBDAutoReShade.App.Controls;
using DBDAutoReShade.App.Infrastructure;
using DBDAutoReShade.App.Media;
using DBDAutoReShade.App.Services;
using DBDAutoReShade.App.ViewModels;
using DBDAutoReShade.App.Views;

namespace DBDAutoReShade.Tests.Campfire;

/// <summary>
/// The REAL windows and views, for their layout and keyboard rules (off screen, never activated; nothing is placed on
/// the desktop): a window that opens centred is fitted to the monitor's work area (never above the screen), a
/// maximized one is padded by the frame it overhangs the monitor with, Tab starts in the rail, the Settings groups
/// are the prototype's 800 px, paths and text boxes are selectable with the Campfire menu (never Windows' light one),
/// and the Presets hero's 1.8 x cap narrows only the frame, not the column.
/// </summary>
public sealed class MainWindowLayoutTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dbd-layout-" + Guid.NewGuid().ToString("N"));

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
    // a 1366 x 768 laptop at 100 %: WPF centres the 1280 x 800 window at top -36
    [InlineData(0, 0, 1366, 728, 43, -36, 1280, 800, 43, 0, 1280, 728)]
    // 1920 x 1080 at 150 %: a 1280 x 688 DIP work area, top -56
    [InlineData(0, 0, 1280, 688, 0, -56, 1280, 800, 0, 0, 1280, 688)]
    // a work area on a second monitor that has room: the design size and WPF's place are kept
    [InlineData(1920, 0, 1920, 1040, 2240, 120, 1280, 800, 2240, 120, 1280, 800)]
    // the setup wizard (620 x 760) on the laptop: it fits under the screen's top, centred
    [InlineData(0, 0, 1366, 728, 373, -16, 620, 760, 373, 0, 620, 728)]
    public void Fit_KeepsTheWholeWindow_TitleBarFirst_OnTheWorkArea(
        double waLeft, double waTop, double waWidth, double waHeight,
        double left, double top, double width, double height,
        double fitLeft, double fitTop, double fitWidth, double fitHeight)
    {
        WindowFit fit = WindowPlacement.Fit(new Rect(waLeft, waTop, waWidth, waHeight), new Rect(left, top, width, height), new Size(620, 520));
        Assert.Equal(new Rect(fitLeft, fitTop, fitWidth, fitHeight), fit.Bounds);
        Assert.True(fit.Bounds.Top >= waTop, "the title bar is on the screen");
    }

    [Fact]
    public void Fit_ShrinksTheMinimum_WhenTheWorkAreaIsSmallerThanIt()
    {
        // 1366 x 768 at 125 %: 1093 x 582 DIPs, less than the main window's 1024 x 680 minimum height
        WindowFit fit = WindowPlacement.Fit(new Rect(0, 0, 1093, 582), new Rect(-93, -109, 1280, 800), new Size(1024, 680));
        Assert.Equal(new Size(1024, 582), fit.Minimum);
        Assert.Equal(new Rect(0, 0, 1093, 582), fit.Bounds); // as large as the work area allows
    }

    [Fact]
    public void TheWindows_FitTheirWorkArea_OnlyWhenTheyOpenCentred()
    {
        CampfireTestHost.RunSta(() =>
        {
            WindowScene scene = WindowScene.Create(Path.Combine(_dir, "fit"), "ready", show: false);
            try
            {
                // what SourceInitialized does with a centred window (applied here without a handle: nothing is shown)
                var main = new MainWindow(scene.ViewModel, new MotionPolicy());
                Assert.Equal(WindowStartupLocation.CenterScreen, main.WindowStartupLocation);
                main.Left = 43;
                main.Top = -36;
                WindowPlacement.FitTo(main, new Rect(0, 0, 1366, 728));
                Assert.Equal((43.0, 0.0, 1280.0, 728.0), (main.Left, main.Top, main.Width, main.Height));
                Assert.Equal(680, main.MinHeight);

                var wizard = new SetupWizardWindow(scene.ViewModel, new MotionPolicy());
                Assert.Equal(WindowStartupLocation.CenterScreen, wizard.WindowStartupLocation);
                wizard.Left = 373;
                wizard.Top = -16;
                WindowPlacement.FitTo(wizard, new Rect(0, 0, 1366, 728));
                Assert.Equal((0.0, 728.0), (wizard.Top, wizard.Height));

                // a window placed by hand (here: far off screen) is left where it was put
                scene.Window.Show();
                CampfireTestHost.Pump();
                Assert.Equal(-20000, scene.Window.Left);
                Assert.Equal(-20000, scene.Window.Top);
                Assert.Equal(800, scene.Window.Height);
            }
            finally
            {
                scene.Dispose();
            }
        });
    }

    [Fact]
    public void TheMainWindow_OpenedCentred_IsInsideItsWorkArea_WithoutBeingShown()
    {
        CampfireTestHost.RunSta(() =>
        {
            WindowScene scene = WindowScene.Create(Path.Combine(_dir, "handle"), "ready", show: false);
            try
            {
                // the window gets its (hidden) handle and its centred place, as at Show, but is never shown
                var main = new MainWindow(scene.ViewModel, new MotionPolicy());
                new System.Windows.Interop.WindowInteropHelper(main).EnsureHandle();
                try
                {
                    Assert.False(main.IsVisible);
                    Rect? workArea = WindowPlacement.WorkAreaOf(main);
                    Assert.NotNull(workArea);
                    Rect area = workArea!.Value;
                    Assert.True(main.Top >= area.Top - 0.5, $"top {main.Top} in {area}");
                    Assert.True(main.Left >= area.Left - 0.5, $"left {main.Left} in {area}");
                    Assert.True(main.Top + main.Height <= area.Bottom + 0.5, $"bottom {main.Top + main.Height} in {area}");
                    Assert.True(main.Height <= 800 && main.Width <= 1280, "never larger than the design size");
                    Assert.Equal(Math.Min(800, area.Height), main.Height, 0); // the design size when it fits
                }
                finally
                {
                    main.Close();
                }
            }
            finally
            {
                scene.Dispose();
            }
        });
    }

    [Fact]
    public void Maximized_ThePaddingIsTheResizeFrame_PlusThePaddedBorder()
    {
        Assert.Equal(new Thickness(8), WindowPlacement.MaximizedInset(4, 4, 4, 1.0, 1.0)); // 100 %
        Assert.Equal(new Thickness(8), WindowPlacement.MaximizedInset(6, 6, 6, 1.5, 1.5)); // 150 %: 12 px
        Assert.Equal(new Thickness(4), WindowPlacement.MaximizedInset(4, 4, 0, 1.0, 1.0)); // no padded border

        CampfireTestHost.RunSta(() =>
        {
            WindowScene scene = WindowScene.Create(Path.Combine(_dir, "inset"), "ready");
            try
            {
                Thickness inset = WindowPlacement.MaximizedInset(scene.Window);
                int padded = WindowPlacement.Metric(92, 96); // SM_CXPADDEDBORDER
                Assert.True(inset.Top >= SystemParameters.WindowResizeBorderThickness.Top, "at least the resize frame");
                if (padded > 0)
                {
                    Assert.True(inset.Top > SystemParameters.WindowResizeBorderThickness.Top, "the padded border is added");
                }
            }
            finally
            {
                scene.Dispose();
            }
        });
    }

    [Fact]
    public void Tab_StartsInTheRail_ThenTheContent_ThenTheWindowButtons()
    {
        CampfireTestHost.RunSta(() =>
        {
            WindowScene scene = WindowScene.Create(Path.Combine(_dir, "tab"), "ready");
            try
            {
                var rail = (FrameworkElement)scene.Window.FindName("Rail");
                var nav = (FrameworkElement)scene.Window.FindName("Nav");
                var content = (FrameworkElement)scene.Window.FindName("ContentHost");
                var controls = (FrameworkElement)scene.Window.FindName("WindowControls");
                Assert.Equal(KeyboardNavigationMode.Local, KeyboardNavigation.GetTabNavigation((DependencyObject)scene.Window.FindName("Root")));
                Assert.True(KeyboardNavigation.GetTabIndex(rail) < KeyboardNavigation.GetTabIndex(content));
                Assert.True(KeyboardNavigation.GetTabIndex(content) < KeyboardNavigation.GetTabIndex(controls));

                // WPF's own tab order: what Tab presses from the first stop would visit (its internal navigation, asked
                // without moving any focus), until it wraps round
                List<DependencyObject> order = TabOrder(scene.Window);
                string seen = Describe(order);
                Assert.True(order.Count > 3, seen);
                Assert.All(order, stop => Assert.True(rail.IsAncestorOf(stop) || content.IsAncestorOf(stop) || controls.IsAncestorOf(stop), seen));
                int lastRail = order.FindLastIndex(rail.IsAncestorOf);
                int firstContent = order.FindIndex(content.IsAncestorOf);
                int lastContent = order.FindLastIndex(content.IsAncestorOf);
                int firstControl = order.FindIndex(controls.IsAncestorOf);
                Assert.True(rail.IsAncestorOf(order[0]), "Tab starts in the rail: " + seen);
                Assert.Same(scene.Window.FindName("StatusTab"), order[0]); // the selected section
                Assert.Single(order, nav.IsAncestorOf); // the sections are one stop (arrows move between them)
                Assert.True(firstContent > lastRail, "the content comes after the rail: " + seen);
                Assert.True(firstControl > lastContent, "the window buttons come after the content: " + seen);
                Assert.Equal(new[] { "MinimizeButton", "MaximizeButton", "CloseButton" },
                    order.Skip(firstControl).Select(stop => ((FrameworkElement)stop).Name)); // and last
            }
            finally
            {
                scene.Dispose();
            }
        });
    }

    [Fact]
    public void SetupWizard_Tab_StartsInTheSteps_ThenFinish_ThenTheWindowButtons()
    {
        CampfireTestHost.RunSta(() =>
        {
            WindowScene scene = WindowScene.Create(Path.Combine(_dir, "wizard-tab"), "ready", show: false);
            var wizard = new SetupWizardWindow(scene.ViewModel, new MotionPolicy { AnimationsEnabled = false })
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                ShowInTaskbar = false,
                ShowActivated = false,
                Left = -20000,
                Top = -20000,
            };
            try
            {
                wizard.Show();
                CampfireTestHost.Pump();
                var steps = (FrameworkElement)wizard.FindName("Steps");
                var finish = (FrameworkElement)wizard.FindName("FinishButton");
                var controls = (FrameworkElement)wizard.FindName("WindowControls");

                List<DependencyObject> order = TabOrder(wizard);
                string seen = Describe(order);
                Assert.True(order.Count > 3, seen);
                Assert.Same(wizard.FindName("FirstBrowseButton"), order[0]); // the first step, not the title bar
                int lastStep = order.FindLastIndex(steps.IsAncestorOf);
                int finishAt = order.IndexOf(finish);
                int firstControl = order.FindIndex(controls.IsAncestorOf);
                Assert.True(lastStep > 0 && finishAt > lastStep, "Finish comes after the steps: " + seen);
                Assert.True(firstControl > finishAt, "the window buttons come last: " + seen);
                Assert.All(order.Skip(firstControl), stop => Assert.True(controls.IsAncestorOf(stop), seen));
            }
            finally
            {
                wizard.Close();
                scene.Dispose();
            }
        });
    }

    [Fact]
    public void Settings_GroupsAreTheDesignWidth_PathsAreSelectable_AndTextBoxesHaveTheCampfireMenu()
    {
        CampfireTestHost.RunSta(() =>
        {
            WindowScene scene = WindowScene.Create(Path.Combine(_dir, "settings"), "ready");
            try
            {
                scene.ViewModel.SelectedTab = AppTab.Settings;
                scene.Settle();
                var settings = (SettingsView)scene.Window.FindName("SettingsPage");
                var groups = (FrameworkElement)settings.FindName("Groups");
                Assert.Equal(SettingsView.GroupsWidth, groups.ActualWidth, 0); // 200 + 40 + 560, whatever the content

                ResourceDictionary theme = CampfireTheme.ForCurrentThread();
                var dbdPath = (TextBox)settings.FindName("DbdPathText");
                Assert.True(dbdPath.IsReadOnly);
                Assert.Equal(scene.ViewModel.DbdPath, dbdPath.Text);
                AssertCampfireMenu(dbdPath.ContextMenu, theme, "Copy", "Select all");

                var manifest = (TextBox)settings.FindName("ManifestBox");
                AssertCampfireMenu(manifest.ContextMenu, theme, "Cut", "Copy", "Paste", "Select all");
                var filter = (TextBox)((PresetsView)scene.Window.FindName("PresetsPage")).FindName("Filter");
                AssertCampfireMenu(filter.ContextMenu, theme, "Cut", "Copy", "Paste", "Select all");
                Assert.NotSame(manifest.ContextMenu, filter.ContextMenu); // one menu per text box

                // Behaviour: "Start the next game with the Global preset" with its hint; nothing about a match end
                CheckBox startWithGlobal = FindAll<CheckBox>(settings).Single(c => Equals(c.Content, MainViewModel.StartWithGlobalLabel));
                Assert.Equal(MainViewModel.StartWithGlobalHint, System.Windows.Automation.AutomationProperties.GetHelpText(startWithGlobal));
                Assert.Contains(FindAll<TextBlock>(settings), t => t.Text == MainViewModel.StartWithGlobalHint);
                Assert.DoesNotContain(FindAll<CheckBox>(settings), c => c.Content is string text && text.Contains("match", StringComparison.OrdinalIgnoreCase));

                // the capture button's accessible name follows its label
                var button = (Button)settings.FindName("HotkeyButton");
                Assert.Equal("Change hotkey for detection", System.Windows.Automation.AutomationProperties.GetName(button));
                scene.ViewModel.BeginHotkeyCapture();
                CampfireTestHost.Pump();
                Assert.Equal("Cancel", button.Content);
                Assert.Equal("Cancel detection hotkey change", System.Windows.Automation.AutomationProperties.GetName(button));
                scene.ViewModel.CancelHotkeyCapture();
            }
            finally
            {
                scene.Dispose();
            }
        });
    }

    [Fact]
    public void Settings_AtTheMinimumSize_TheGroupsFitTheWindow()
    {
        CampfireTestHost.RunSta(() =>
        {
            WindowScene scene = WindowScene.Create(Path.Combine(_dir, "settings-min"), "ready", 1024, 680);
            try
            {
                scene.ViewModel.SelectedTab = AppTab.Settings;
                scene.Settle();
                var settings = (SettingsView)scene.Window.FindName("SettingsPage");
                var groups = (FrameworkElement)settings.FindName("Groups");
                var scroller = (ScrollViewer)settings.FindName("Scroller");
                Assert.True(groups.ActualWidth <= scroller.ViewportWidth - 64 + 0.5, $"{groups.ActualWidth} in {scroller.ViewportWidth}");
                Assert.True(groups.ActualWidth > 700);
            }
            finally
            {
                scene.Dispose();
            }
        });
    }

    [Theory]
    [InlineData(1084)] // the default window: the thumbnails share the column
    [InlineData(2340)] // 2560 wide at 100 %, or a maximized 4K screen at 150 %
    [InlineData(3620)] // 4K at 100 %
    [InlineData(4900)] // 5120 x 1440
    public void Presets_Thumbnails_NeverEnlargeTheirPicture_HoweverWideTheWindow(double width)
    {
        CampfireTestHost.RunSta(() =>
        {
            WindowScene scene = WindowScene.Create(Path.Combine(_dir, "thumbs-" + width), "active", show: false);
            try
            {
                // MacMillan Estate (the detected realm): five maps, pictures 364 px wide
                var view = new PresetsView { DataContext = scene.ViewModel };
                MotionPolicy.SetPolicy(view, new MotionPolicy { AnimationsEnabled = false });
                CampfireTestHost.Layout(view, width, 900);
                CampfireTestHost.Layout(view, width, 900);
                Assert.Equal("MacMillan Estate", scene.ViewModel.SelectedRealmRow?.RealmName);
                Assert.Equal(5, scene.ViewModel.Thumbs.Count);

                var list = (FrameworkElement)view.FindName("ThumbList");
                MapPicture[] pictures = FindAll<MapPicture>(list).ToArray();
                Assert.Equal(5, pictures.Length);
                foreach (MapPicture picture in pictures)
                {
                    var source = (System.Windows.Media.Imaging.BitmapSource)picture.Picture.Source;
                    DpiScale dpi = VisualTreeHelper.GetDpi(picture);
                    double scale = Math.Max(picture.ActualWidth * dpi.DpiScaleX / source.PixelWidth, picture.ActualHeight * dpi.DpiScaleY / source.PixelHeight);
                    Assert.True(scale <= 1.0 + 1e-6, $"{picture.MapName} at {width}: {picture.ActualWidth:0.#} DIP for a {source.PixelWidth} px picture ({scale:0.00} x)");
                }

                // the hero stays the largest picture on the screen
                var hero = (FrameworkElement)view.FindName("HeroArt");
                Assert.All(pictures, picture => Assert.True(picture.ActualWidth < hero.ActualWidth, $"{picture.ActualWidth} < {hero.ActualWidth}"));
            }
            finally
            {
                scene.Dispose();
            }
        });
    }

    [Fact]
    public void Presets_TheHeroCap_NarrowsTheFrameOnly_NotTheColumn()
    {
        CampfireTestHost.RunSta(() =>
        {
            WindowScene scene = WindowScene.Create(Path.Combine(_dir, "presets"), "active", show: false);
            try
            {
                // a detail column wider than the cap (here 100 % and a wide view; at 150 % the cap is 434 DIP in the
                // design's 650 DIP column, the same case): the picture is capped, the rest keeps the column
                var view = new PresetsView { DataContext = scene.ViewModel };
                MotionPolicy.SetPolicy(view, new MotionPolicy { AnimationsEnabled = false });
                CampfireTestHost.Layout(view, 1500, 800);
                CampfireTestHost.Layout(view, 1500, 800);

                var detail = (FrameworkElement)view.FindName("Detail");
                var frame = (AspectFrame)view.FindName("HeroFrame");
                var hero = (FrameworkElement)view.FindName("HeroArt");
                var glow = (FrameworkElement)view.FindName("HeroGlow");
                var panel = (FrameworkElement)view.FindName("PresetPanel");
                var thumbs = (FrameworkElement)view.FindName("ThumbList");
                double cap = MapImageCatalog.MaxFrameWidth(MapImageCatalog.Default.ReferencePixelWidth, 1.0);
                Assert.True(double.IsPositiveInfinity(detail.MaxWidth));
                Assert.True(detail.ActualWidth > cap + 300, $"the column is not capped: {detail.ActualWidth}");
                Assert.Equal(cap, hero.ActualWidth, 1); // 362 px x 1.8
                Assert.Equal(hero.ActualWidth, glow.ActualWidth, 1); // the live glow follows the frame
                Assert.Equal(hero.ActualHeight, glow.ActualHeight, 1);
                Assert.Equal(detail.ActualWidth, panel.ActualWidth, 1); // the preset panel keeps the column
                Assert.True(thumbs.ActualWidth > cap + 300, $"the thumbnails (below their own cap here) share the column: {thumbs.ActualWidth}");

                // the same frame at 150 %: 434 DIP, wherever the column is wider
                Assert.Equal(MapImageCatalog.MaxFrameWidth(MapImageCatalog.Default.ReferencePixelWidth, 1.5),
                    frame.FrameSize(new Size(650, double.PositiveInfinity), 1.5).Width, 1);
            }
            finally
            {
                scene.Dispose();
            }
        });
    }

    private static void AssertCampfireMenu(ContextMenu? menu, ResourceDictionary theme, params string[] headers)
    {
        Assert.NotNull(menu);
        Assert.Equal(typeof(ContextMenu), menu!.GetType()); // not WPF's internal light editor menu
        Assert.Same(theme["CfContextMenu"], menu.Style);
        MenuItem[] items = menu.Items.OfType<MenuItem>().ToArray();
        Assert.Equal(headers, items.Select(i => (string)i.Header));
        Assert.All(items, i => Assert.Same(theme["CfMenuItem"], i.Style));
    }

    private static IEnumerable<T> FindAll<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                yield return match;
            }

            foreach (T inner in FindAll<T>(child))
            {
                yield return inner;
            }
        }
    }

    /// <summary>
    /// The tab stops in WPF's own order, read through its internal navigation as a Tab press does it
    /// (KeyboardNavigation.Navigate: <c>GetNextTab(element, GetGroupParent(element, true), false)</c>), so no focus moves
    /// and no window is activated: from the window's first stop, each next stop, until the walk wraps round.
    /// </summary>
    internal static List<DependencyObject> TabOrder(Window window)
    {
        const BindingFlags Internal = BindingFlags.NonPublic | BindingFlags.Instance;
        PropertyInfo? current = typeof(KeyboardNavigation).GetProperty("Current", BindingFlags.NonPublic | BindingFlags.Static);
        MethodInfo? next = typeof(KeyboardNavigation).GetMethod("GetNextTab", Internal, null,
            new[] { typeof(DependencyObject), typeof(DependencyObject), typeof(bool) }, null);
        MethodInfo? groupParent = typeof(KeyboardNavigation).GetMethod("GetGroupParent", Internal, null,
            new[] { typeof(DependencyObject), typeof(bool) }, null);
        FieldInfo? property = typeof(KeyboardNavigation).GetField("_navigationProperty", Internal); // what Navigate() sets for Tab
        object? navigation = current?.GetValue(null);
        Assert.NotNull(navigation);
        Assert.NotNull(next);
        Assert.NotNull(groupParent);
        Assert.NotNull(property);

        var root = (DependencyObject)window.Content;
        object? was = property!.GetValue(navigation);
        property.SetValue(navigation, KeyboardNavigation.TabNavigationProperty);
        try
        {
            var order = new List<DependencyObject>();
            DependencyObject? at = null;
            for (int i = 0; i < 80; i++)
            {
                // the container is the stop's own navigation group, as for a real Tab press (a "Once" group is left
                // after its one stop; a "Local" group runs through its stops before the next group)
                var container = at is null ? root : (DependencyObject)groupParent!.Invoke(navigation, new object[] { at, true })!;
                at = next!.Invoke(navigation, new object?[] { at, container, false }) as DependencyObject;
                if (at is null || order.Contains(at))
                {
                    break;
                }

                order.Add(at);
            }

            return order;
        }
        finally
        {
            property.SetValue(navigation, was);
        }
    }

    /// <summary>
    /// Where Shift+Tab from <paramref name="from"/> goes, read through WPF's internal navigation as the key does it
    /// (KeyboardNavigation.Navigate: <c>GetPrevTab(element, null, false)</c>), without moving any focus.
    /// </summary>
    internal static DependencyObject? PreviousTab(DependencyObject from)
    {
        const BindingFlags Internal = BindingFlags.NonPublic | BindingFlags.Instance;
        PropertyInfo? current = typeof(KeyboardNavigation).GetProperty("Current", BindingFlags.NonPublic | BindingFlags.Static);
        MethodInfo? previous = typeof(KeyboardNavigation).GetMethod("GetPrevTab", Internal, null,
            new[] { typeof(DependencyObject), typeof(DependencyObject), typeof(bool) }, null);
        FieldInfo? property = typeof(KeyboardNavigation).GetField("_navigationProperty", Internal);
        object? navigation = current?.GetValue(null);
        Assert.NotNull(navigation);
        Assert.NotNull(previous);
        Assert.NotNull(property);
        object? was = property!.GetValue(navigation);
        property.SetValue(navigation, KeyboardNavigation.TabNavigationProperty);
        try
        {
            return previous!.Invoke(navigation, new object?[] { from, null, false }) as DependencyObject;
        }
        finally
        {
            property.SetValue(navigation, was);
        }
    }

    internal static string Describe(IEnumerable<DependencyObject> order) =>
        string.Join(" > ", order.Select(stop => stop is FrameworkElement { Name.Length: > 0 } named ? named.Name : stop.GetType().Name));
}
