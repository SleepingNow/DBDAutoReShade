using System.IO.Packaging;
using System.Windows;
using System.Windows.Media;

namespace DBDAutoReShade.App.Media;

/// <summary>
/// The two embedded font families (static instances, SIL OFL 1.1), loaded through pack URIs so they work in a
/// single-file publish and never depend on what is installed:
/// <list type="bullet">
/// <item><see cref="Display"/>: Big Shoulders Display 600 / 700 / 800 (headings, the state hero, keycaps).</item>
/// <item><see cref="Body"/>: Barlow 400 / 500 / 600 / 700 (body and UI).</item>
/// </list>
/// Pick the face with <c>FontWeight</c> (SemiBold = 600, Bold = 700, ExtraBold = 800; Normal / Medium / SemiBold / Bold
/// for Barlow); WPF groups the static files of one family by weight. Segoe UI is the fallback.
/// </summary>
public static class CampfireFonts
{
    /// <summary>The pack base URI of this assembly's resources.</summary>
    public static readonly Uri PackBase;

    /// <summary>Big Shoulders Display (600, 700, 800).</summary>
    public static readonly FontFamily Display;

    /// <summary>Barlow (400, 500, 600, 700).</summary>
    public static readonly FontFamily Body;

    static CampfireFonts()
    {
        PackResources.EnsureRegistered();
        PackBase = new Uri("pack://application:,,,/DBDAutoReShade;component/");
        Display = new FontFamily(PackBase, "./Assets/Fonts/#Big Shoulders Display, Segoe UI");
        Body = new FontFamily(PackBase, "./Assets/Fonts/#Barlow, Segoe UI");
    }
}

/// <summary>Makes the <c>pack://application:,,,/</c> scheme usable before (or without) an Application instance.</summary>
public static class PackResources
{
    private static int Registered;

    /// <summary>Registers the pack scheme and the application resource package (idempotent, thread-safe).</summary>
    public static void EnsureRegistered()
    {
        if (Interlocked.Exchange(ref Registered, 1) == 1)
        {
            return;
        }

        // Touching these runs the type initializers that register "pack:" and the application/siteoforigin packages.
        _ = PackUriHelper.UriSchemePack;
        _ = Application.Current;
    }

    /// <summary>A pack URI for <paramref name="relativePath"/> (e.g. "Assets/Maps/Ind_CoalTower.png") in this assembly.</summary>
    public static Uri Pack(string relativePath)
    {
        EnsureRegistered();
        return new Uri("pack://application:,,,/DBDAutoReShade;component/" + relativePath.TrimStart('/'), UriKind.Absolute);
    }
}
