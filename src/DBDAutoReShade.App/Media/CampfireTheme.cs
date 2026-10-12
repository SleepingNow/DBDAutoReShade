using System.Windows;

namespace DBDAutoReShade.App.Media;

/// <summary>
/// The Campfire theme dictionary (<c>Themes/Campfire.xaml</c>) for windows and views. The app merges it into the
/// application resources (App.xaml); a window or view built without that Application (the snapshot harness, tests,
/// tools) merges one instance per thread into its own resources before its XAML loads, so its static resource
/// references resolve the same way.
/// </summary>
public static class CampfireTheme
{
    /// <summary>The theme's component URI (relative pack form).</summary>
    public const string Source = "/DBDAutoReShade;component/Themes/Campfire.xaml";

    // One instance per thread: styles and brushes belong to the thread that loaded them.
    [ThreadStatic]
    private static ResourceDictionary? _threadTheme;

    /// <summary>The theme loaded once for the calling thread.</summary>
    public static ResourceDictionary ForCurrentThread() =>
        _threadTheme ??= (ResourceDictionary)Application.LoadComponent(new Uri(Source, UriKind.Relative));

    /// <summary>
    /// True when the running Application (on this thread) already provides the theme, as the app does through
    /// App.xaml; false without an Application (tests, tools) or from another thread.
    /// </summary>
    public static bool IsProvidedByApplication
    {
        get
        {
            Application? app = Application.Current;
            if (app is null || !app.Dispatcher.CheckAccess())
            {
                return false;
            }

            return app.TryFindResource("CfNightBrush") is not null;
        }
    }

    /// <summary>
    /// Merges the theme into <paramref name="element"/>'s resources unless the Application provides it. Call it in a
    /// window's or view's constructor before <c>InitializeComponent</c>.
    /// </summary>
    public static void EnsureFor(FrameworkElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (IsProvidedByApplication)
        {
            return;
        }

        ResourceDictionary theme = ForCurrentThread();
        if (!element.Resources.MergedDictionaries.Contains(theme))
        {
            element.Resources.MergedDictionaries.Add(theme);
        }
    }
}
