using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DBDAutoReShade.App.Services;
using DBDAutoReShade.Core.Services;

namespace DBDAutoReShade.Tests.Campfire;

/// <summary>
/// The map pictures: every map of the built-in database has a picture that loads from the assembly's resources
/// (pack URIs, so they work in the single-file publish), decoded once, frozen and at 96 DPI; unknown maps have none.
/// </summary>
public sealed class MapImageCatalogTests
{
    private static RealmDatabaseService BuiltInDatabase()
    {
        var db = new RealmDatabaseService();
        db.LoadAsync(null).GetAwaiter().GetResult();
        return db;
    }

    [Fact]
    public void EveryMapInTheBuiltInDatabase_HasAPictureThatLoads()
    {
        RealmDatabaseService db = BuiltInDatabase();
        List<string> maps = db.Realms.SelectMany(r => r.Maps).ToList();
        Assert.NotEmpty(maps);
        var missing = new List<string>();
        foreach (string map in maps)
        {
            BitmapSource? image = MapImageCatalog.Default.TryGetImage(map);
            if (image is null)
            {
                missing.Add(map);
                continue;
            }

            Assert.True(image.IsFrozen, map);
            Assert.True(image.PixelWidth >= 300 && image.PixelHeight >= 200, $"{map}: {image.PixelWidth}x{image.PixelHeight}");
            Assert.Equal(96, image.DpiX, 3);
            Assert.True(MapImageCatalog.Default.HasPicture(map));
        }

        Assert.True(missing.Count == 0, "Maps without a picture: " + string.Join(", ", missing));
    }

    [Fact]
    public void EveryIndexedPicture_IsACompiledResource()
    {
        string json = File.ReadAllText(Path.Combine(CampfireTestHost.RepoRoot(), "src", "DBDAutoReShade.App", "Assets", "Maps", "map-images.json"));
        IReadOnlyDictionary<string, string> index = MapImageCatalog.ParseIndex(json);
        Assert.Equal(index.Count, MapImageCatalog.Default.MapNames.Count);
        foreach ((string map, string file) in index)
        {
            Assert.Equal(file, MapImageCatalog.Default.GetFileName(map));
            using Stream? stream = Application.GetResourceStream(MapImageCatalog.PictureUri(file))?.Stream;
            Assert.True(stream is not null && stream.Length > 1000, file);
        }
    }

    [Fact]
    public void Pictures_AreDecodedOnce_AndShared()
    {
        BitmapSource? a = MapImageCatalog.Default.TryGetImage("Coal Tower");
        BitmapSource? b = MapImageCatalog.Default.TryGetImage("coal tower");
        Assert.NotNull(a);
        Assert.Same(a, b);
        Assert.Equal(PixelFormats.Bgr32, a!.Format); // opaque pictures are stored opaque
    }

    [Fact]
    public void UnknownMaps_HaveNoPicture()
    {
        Assert.Null(MapImageCatalog.Default.TryGetImage("A Map From The Future"));
        Assert.False(MapImageCatalog.Default.HasPicture("A Map From The Future"));
        Assert.Null(MapImageCatalog.Default.TryGetImage(null));
        Assert.False(MapImageCatalog.Default.HasPicture(null));
    }

    [Fact]
    public void Lookup_ToleratesCaseSpacingAndCurlyApostrophes()
    {
        Assert.True(MapImageCatalog.Default.HasPicture("Father Campbell’s Chapel"));
        Assert.True(MapImageCatalog.Default.HasPicture("  wreckers'   yard "));
        Assert.Equal("father campbell's chapel", MapImageCatalog.Normalize("Father  Campbell’s Chapel "));
    }

    [Fact]
    public void ACustomCatalog_ReadsFromItsOwnSource_AndSurvivesBrokenFiles()
    {
        var catalog = new MapImageCatalog(
            new Dictionary<string, string> { ["Good"] = "good.png", ["Broken"] = "broken.png", ["Gone"] = "gone.png" },
            file => file switch
            {
                "good.png" => new MemoryStream(TinyPng(40, 20)),
                "broken.png" => new MemoryStream(new byte[] { 1, 2, 3 }),
                _ => null,
            });
        BitmapSource? good = catalog.TryGetImage("Good");
        Assert.NotNull(good);
        Assert.Equal(40, good!.PixelWidth);
        Assert.Null(catalog.TryGetImage("Broken"));
        Assert.Null(catalog.TryGetImage("Gone"));
        Assert.True(catalog.HasPicture("Broken")); // indexed, but it does not decode: the placeholder shows
    }

    [Fact]
    public void TranslucentEdges_AreTrimmed()
    {
        byte[] png = TinyPng(30, 20, translucentBorder: true);
        var catalog = new MapImageCatalog(new Dictionary<string, string> { ["Torn"] = "torn.png" }, _ => new MemoryStream(png));
        BitmapSource image = catalog.TryGetImage("Torn")!;
        Assert.Equal(28, image.PixelWidth);
        Assert.Equal(18, image.PixelHeight);
    }

    [Fact]
    public void TheBuiltInReferenceWidth_IsTheNarrowestDecodedPicture()
    {
        // the constant the frames are capped by (no decoding on the UI thread), checked against the real set
        int narrowest = MapImageCatalog.Default.MapNames
            .Select(map => MapImageCatalog.Default.TryGetImage(map))
            .Where(image => image is not null)
            .Min(image => image!.PixelWidth);
        Assert.Equal(MapImageCatalog.BuiltInReferencePixelWidth, narrowest);
        Assert.Equal(362, MapImageCatalog.BuiltInReferencePixelWidth);
        Assert.Equal(MapImageCatalog.BuiltInReferencePixelWidth, MapImageCatalog.Default.ReferencePixelWidth);
    }

    [Fact]
    public void TheBuiltInReferenceHeight_IsTheShortestDecodedPicture()
    {
        // the height cap of frames taller than the pictures' aspect (the Status plate), checked against the real set
        int shortest = MapImageCatalog.Default.MapNames
            .Select(map => MapImageCatalog.Default.TryGetImage(map))
            .Where(image => image is not null)
            .Min(image => image!.PixelHeight);
        Assert.Equal(MapImageCatalog.BuiltInReferencePixelHeight, shortest);
        Assert.Equal(MapImageCatalog.BuiltInReferencePixelHeight, MapImageCatalog.Default.ReferencePixelHeight);
    }

    [Fact]
    public void AKnownReferenceWidth_DecodesNothing_AnUnknownOneIsMeasuredOnce()
    {
        int opened = 0;
        var index = new Dictionary<string, string> { ["Wide"] = "wide.png", ["Narrow"] = "narrow.png" };
        Stream? Open(string file)
        {
            opened++;
            return new MemoryStream(TinyPng(file == "wide.png" ? 40 : 30, 20));
        }

        var known = new MapImageCatalog(index, Open, referencePixelWidth: 362, referencePixelHeight: 239);
        Assert.Equal(362, known.ReferencePixelWidth);
        Assert.Equal(239, known.ReferencePixelHeight);
        Assert.Equal(0, opened);

        var measured = new MapImageCatalog(index, Open);
        Assert.Equal(30, measured.ReferencePixelWidth);
        Assert.Equal(2, opened);
        Assert.Equal(30, measured.ReferencePixelWidth);
        Assert.Equal(20, measured.ReferencePixelHeight);
        Assert.Equal(2, opened); // measured once; the pictures stay cached
    }

    [Fact]
    public void Prewarming_DecodesOnePicturePerIdleMoment_UntilEveryPictureIsCached()
    {
        CampfireTestHost.RunSta(() =>
        {
            int opened = 0;
            var index = new Dictionary<string, string> { ["Coal Tower"] = "a.png", ["Groaning Storehouse"] = "b.png", ["Ironworks of Misery"] = "c.png" };
            Stream? Open(string file)
            {
                opened++;
                return new MemoryStream(TinyPng(30, 20));
            }

            var catalog = new MapImageCatalog(index, Open, referencePixelWidth: 30);
            _ = catalog.TryGetImage("Groaning Storehouse"); // one is already cached
            opened = 0;
            Task done = catalog.PrewarmOnIdle(Dispatcher.CurrentDispatcher);
            Assert.Equal(0, opened); // nothing until the window is idle
            CampfireTestHost.Pump(DispatcherPriority.ApplicationIdle);
            Assert.Equal(1, opened); // one picture per idle moment, then the window's own work comes first again
            WindowScene.PumpUntil(() => done.IsCompleted, "the prewarm");
            Assert.True(done.IsCompletedSuccessfully);
            Assert.Equal(2, opened); // each picture once; the cached one is not read again
            Assert.All(index.Keys, map => Assert.True(catalog.IsDecoded(map)));
            _ = catalog.TryGetImage("Coal Tower");
            Assert.Equal(2, opened);
        });
    }

    [Theory]
    [InlineData(362, 1.0, 651.6)]
    [InlineData(364, 1.0, 655.2)]
    [InlineData(364, 1.5, 436.8)]
    [InlineData(726, 1.25, 1045.44)]
    public void FrameCap_Is18TimesTheSourceWidth_InPhysicalPixels(double pixels, double dpiScale, double expected)
    {
        Assert.Equal(expected, MapImageCatalog.MaxFrameWidth(pixels, dpiScale), 2);
    }

    private static byte[] TinyPng(int width, int height, bool translucentBorder = false)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = (y * width + x) * 4;
                pixels[i] = 40;
                pixels[i + 1] = 80;
                pixels[i + 2] = 120;
                bool edge = x == 0 || y == 0 || x == width - 1 || y == height - 1;
                pixels[i + 3] = translucentBorder && edge ? (byte)90 : (byte)255;
            }
        }

        BitmapSource source = BitmapSource.Create(width, height, 72, 72, PixelFormats.Bgra32, null, pixels, width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }
}
