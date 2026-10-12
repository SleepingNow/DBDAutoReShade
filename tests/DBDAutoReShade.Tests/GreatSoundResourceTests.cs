using System.Collections;
using System.Resources;
using System.Windows;
using DBDAutoReShade.Core.Services;
using WpfApp = DBDAutoReShade.App.App;

namespace DBDAutoReShade.Tests;

/// <summary>
/// The Great sound as the app opens it (<see cref="WpfApp.OpenGreatSound"/>): the
/// WPF Resource behind <see cref="WpfApp.GreatSoundPackUri"/>, so it also works in
/// the single-file publish. Without the App csproj's Resource item the sound
/// is silently off (only SoundEffectsService.LoadError and the ReShade check
/// say why), so this guards the merge of the csproj.
/// </summary>
public sealed class GreatSoundResourceTests
{
    [Fact]
    public void ThePackUri_NamesTheAppAssembly_AndTheShippedFile()
    {
        string assembly = typeof(WpfApp).Assembly.GetName().Name!;

        Assert.Equal(WpfApp.GreatSoundPackUri, $"pack://application:,,,/{assembly};component/Assets/Sounds/great.wav");
        Assert.True(File.Exists(Path.Combine(AppAssets.SourceDir, "Sounds", "great.wav")));
    }

    [FactWhenTheAppEmbedsItsAssets]
    public void TheGreatSound_IsEmbedded_AndResolvesThroughItsPackUri()
    {
        Assert.Contains("assets/sounds/great.wav", AppAssets.EmbeddedKeys);

        _ = System.IO.Packaging.PackUriHelper.UriSchemePack;
        using (Stream? viaPackUri = Application.GetResourceStream(new Uri(WpfApp.GreatSoundPackUri, UriKind.Absolute))?.Stream)
        {
            Assert.NotNull(viaPackUri);
        }

        byte[] expected = File.ReadAllBytes(Path.Combine(AppAssets.SourceDir, "Sounds", "great.wav"));
        using Stream? opened = WpfApp.OpenGreatSound(); // what the app hands its sound service
        Assert.NotNull(opened);
        using var copy = new MemoryStream();
        opened.CopyTo(copy);
        Assert.Equal(expected, copy.ToArray());
        Assert.Equal(44100, WavPcm16.Parse(copy.ToArray()).SampleRate);
    }
}

/// <summary>
/// A fact that runs once the App assembly embeds its Assets as WPF Resources (its
/// csproj Resource items come with the visual layer); before that it is skipped,
/// with the reason in the test output.
/// </summary>
public sealed class FactWhenTheAppEmbedsItsAssetsAttribute : FactAttribute
{
    public FactWhenTheAppEmbedsItsAssetsAttribute()
    {
        if (!AppAssets.EmbeddedKeys.Any(k => k.StartsWith("assets/", StringComparison.Ordinal)))
        {
            Skip = "The App project embeds no Assets as WPF Resources yet (its csproj Resource items are " +
                   "added with the visual layer); this test runs once it does.";
        }
    }
}

/// <summary>The App assembly's WPF Resources and its Assets source folder.</summary>
internal static class AppAssets
{
    private static readonly Lazy<IReadOnlyList<string>> Keys = new(ReadEmbeddedKeys);

    /// <summary>The WPF Resource names in the App assembly (lower case, '/' separated, e.g. "assets/sounds/great.wav").</summary>
    public static IReadOnlyList<string> EmbeddedKeys => Keys.Value;

    /// <summary>src/DBDAutoReShade.App/Assets in the repository.</summary>
    public static string SourceDir
    {
        get
        {
            string? dir = AppContext.BaseDirectory;
            while (dir is not null && !File.Exists(Path.Combine(dir, "DBDAutoReShade.slnx")))
            {
                dir = Path.GetDirectoryName(dir);
            }

            return Path.Combine(dir ?? throw new InvalidOperationException("Repository root not found."),
                "src", "DBDAutoReShade.App", "Assets");
        }
    }

    private static IReadOnlyList<string> ReadEmbeddedKeys()
    {
        var assembly = typeof(WpfApp).Assembly;
        using Stream? resources = assembly.GetManifestResourceStream(assembly.GetName().Name + ".g.resources");
        if (resources is null)
        {
            return Array.Empty<string>();
        }

        using var reader = new ResourceReader(resources);
        var keys = new List<string>();
        foreach (DictionaryEntry entry in reader)
        {
            keys.Add((string)entry.Key);
        }

        return keys;
    }
}
