using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DBDAutoReShade.App.Media;

namespace DBDAutoReShade.App.Services;

/// <summary>
/// Map name → picture. Reads <c>Assets/Maps/map-images.json</c> (a WPF Resource: map display name as in
/// mapDatabase.json → PNG file name) and returns each picture decoded once, normalised to 96 DPI (so one source
/// pixel is one DIP at 100 % scaling), frozen and cached. The originals are shown as they are: there is no
/// upscaling of any kind. Draw them with <c>RenderOptions.BitmapScalingMode="HighQuality"</c>, UniformToFill
/// (centred crop) and never more than <see cref="MaxUpscale"/> × their pixel size (see <see cref="MaxFrameWidth"/>; a
/// frame taller than the pictures' aspect is capped by height too, see <see cref="ReferencePixelHeight"/>).
/// Unknown maps (a future database can add maps) return null: show the fog placeholder with the map name.
/// Thread-safe; the images are frozen, so any thread may use them.
/// </summary>
public sealed class MapImageCatalog
{
    /// <summary>The largest on-screen enlargement of a picture: 1.8 × its pixel width, in physical pixels.</summary>
    public const double MaxUpscale = 1.8;

    /// <summary>
    /// The narrowest built-in picture's width as decoded, translucent edges trimmed (362 px: Ion_Ionlevel01 and
    /// Sub_Street). Frames that show any built-in map are capped at 1.8 × this, so a plate does not change size from
    /// one map to the next and no picture is enlarged more than 1.8 ×. A unit test keeps it equal to the decoded set.
    /// </summary>
    public const int BuiltInReferencePixelWidth = 362;

    /// <summary>
    /// The shortest built-in picture's height as decoded, translucent edges trimmed (239 px: Ion_Ionlevel01 and
    /// Sub_Street). UniformToFill scales a picture by the side it is short of: a frame taller than the picture's aspect
    /// (the Status plate goes up to 3:2, the pictures are about 1.51:1) enlarges it by its height, so such a frame is
    /// also capped at 1.8 × this. A unit test keeps it equal to the decoded set.
    /// </summary>
    public const int BuiltInReferencePixelHeight = 239;

    /// <summary>Resource path of the index.</summary>
    public const string IndexPath = "Assets/Maps/map-images.json";

    private const string MapsFolder = "Assets/Maps/";

    // (PublicationOnly: reading the index never runs under the Lazy's lock)
    private static readonly Lazy<MapImageCatalog> DefaultCatalog = new(() => new MapImageCatalog(LoadIndex(), OpenResource, BuiltInReferencePixelWidth, BuiltInReferencePixelHeight), LazyThreadSafetyMode.PublicationOnly);

    private readonly Dictionary<string, string> _files;
    private readonly Func<string, Stream?> _open;
    private readonly Dictionary<string, BitmapSource?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private readonly Lazy<int> _referencePixelWidth;
    private readonly Lazy<int> _referencePixelHeight;

    /// <summary>
    /// A catalog over <paramref name="mapToFile"/> (map name → file name); <paramref name="open"/> opens a file
    /// by name (null when missing). Tests use this; the app uses <see cref="Default"/>.
    /// <paramref name="referencePixelWidth"/> is the narrowest picture's width when it is known; without it,
    /// <see cref="ReferencePixelWidth"/> decodes every picture once to find it. <paramref name="referencePixelHeight"/>
    /// is the shortest picture's height (<see cref="ReferencePixelHeight"/>) likewise.
    /// </summary>
    public MapImageCatalog(IReadOnlyDictionary<string, string> mapToFile, Func<string, Stream?> open, int? referencePixelWidth = null, int? referencePixelHeight = null)
    {
        ArgumentNullException.ThrowIfNull(mapToFile);
        ArgumentNullException.ThrowIfNull(open);
        _files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, string> pair in mapToFile)
        {
            _files[Normalize(pair.Key)] = pair.Value;
        }

        MapNames = mapToFile.Keys.ToList();
        _open = open;
        _referencePixelWidth = referencePixelWidth is int known && known > 0
            ? new Lazy<int>(known)
            : new Lazy<int>(() => MeasureSmallest(image => image.PixelWidth, BuiltInReferencePixelWidth), LazyThreadSafetyMode.ExecutionAndPublication);
        _referencePixelHeight = referencePixelHeight is int knownHeight && knownHeight > 0
            ? new Lazy<int>(knownHeight)
            : new Lazy<int>(() => MeasureSmallest(image => image.PixelHeight, BuiltInReferencePixelHeight), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>The catalog of the pictures embedded in this assembly.</summary>
    public static MapImageCatalog Default => DefaultCatalog.Value;

    /// <summary>
    /// The widest a map thumbnail's frame may be, in physical pixels: the narrowest built-in picture's width, so a
    /// thumbnail is never enlarged (2:1 thumbnails crop by width: their scale is frame width / picture width).
    /// </summary>
    public static double ThumbnailMaxWidthPixels => Default.ReferencePixelWidth;

    /// <summary>Every map name the index lists (as written in the index).</summary>
    public IReadOnlyList<string> MapNames { get; }

    /// <summary>
    /// The narrowest picture's pixel width as decoded (<see cref="BuiltInReferencePixelWidth"/> = 362 for
    /// <see cref="Default"/>, a constant: reading it decodes nothing); frames that show any map are capped at
    /// <see cref="MaxUpscale"/> × it, so a plate does not change size from one map to the next. A catalog built without
    /// a known width decodes every picture once, on first access, to find it.
    /// </summary>
    public int ReferencePixelWidth => _referencePixelWidth.Value;

    /// <summary>
    /// The shortest picture's pixel height as decoded (<see cref="BuiltInReferencePixelHeight"/> = 239 for
    /// <see cref="Default"/>, a constant). A frame that can be taller than the pictures' aspect is capped at
    /// <see cref="MaxUpscale"/> × it as well as by width, so UniformToFill enlarges no picture beyond the limit by its
    /// height either. A catalog built without a known height decodes every picture once, on first access, to find it.
    /// </summary>
    public int ReferencePixelHeight => _referencePixelHeight.Value;

    /// <summary>True when the index lists a picture for <paramref name="mapName"/>.</summary>
    public bool HasPicture(string? mapName) => mapName is not null && _files.ContainsKey(Normalize(mapName));

    /// <summary>The picture's file name for <paramref name="mapName"/>, or null.</summary>
    public string? GetFileName(string? mapName) =>
        mapName is not null && _files.TryGetValue(Normalize(mapName), out string? file) ? file : null;

    /// <summary>
    /// The decoded, frozen picture for <paramref name="mapName"/> (96 DPI, opaque pictures as Bgr32), or null when
    /// the map has no picture or it cannot be decoded. Decoded once and cached.
    /// </summary>
    public BitmapSource? TryGetImage(string? mapName)
    {
        string? file = GetFileName(mapName);
        if (file is null)
        {
            return null;
        }

        lock (_gate)
        {
            if (_cache.TryGetValue(file, out BitmapSource? cached))
            {
                return cached;
            }
        }

        BitmapSource? image = Decode(file);
        lock (_gate)
        {
            _cache[file] = image;
        }

        return image;
    }

    /// <summary>True when the picture for <paramref name="mapName"/> has been decoded (or found missing) already.</summary>
    public bool IsDecoded(string? mapName)
    {
        string? file = GetFileName(mapName);
        if (file is null)
        {
            return false;
        }

        lock (_gate)
        {
            return _cache.ContainsKey(file);
        }
    }

    /// <summary>
    /// Decodes every picture ahead of need, one per idle moment of <paramref name="dispatcher"/> (its
    /// <see cref="DispatcherPriority.ApplicationIdle"/> queue; a few milliseconds each), so the first look at the
    /// Presets list finds them ready instead of decoding all of them in one layout pass while its tab crossfades in.
    /// The pictures are read on the dispatcher's thread (the pack resource streams must not be read from several
    /// threads at once). Returns at once; the task completes when every picture is cached, or is cancelled when the
    /// dispatcher shuts down first.
    /// </summary>
    public Task PrewarmOnIdle(Dispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int next = 0;
        void Step()
        {
            if (dispatcher.HasShutdownStarted)
            {
                done.TrySetCanceled();
                return;
            }

            // one picture that still needs decoding, then yield to whatever the window has to do
            while (next < MapNames.Count)
            {
                string map = MapNames[next++];
                if (!IsDecoded(map))
                {
                    _ = TryGetImage(map);
                    break;
                }
            }

            if (next < MapNames.Count)
            {
                dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(Step));
            }
            else
            {
                done.TrySetResult();
            }
        }

        dispatcher.ShutdownStarted += (_, _) => done.TrySetCanceled();
        dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(Step));
        return done.Task;
    }

    /// <summary>
    /// The widest a frame showing a picture of <paramref name="pixelWidth"/> source pixels may be, in DIPs, at a
    /// display scale of <paramref name="dpiScaleX"/> (1.0 = 96 DPI): <c>pixelWidth × 1.8 / dpiScaleX</c>.
    /// Beyond it, cap the frame and let the fog fill the space.
    /// </summary>
    public static double MaxFrameWidth(double pixelWidth, double dpiScaleX) =>
        pixelWidth * MaxUpscale / (dpiScaleX > 0 ? dpiScaleX : 1.0);

    /// <summary>Applies the scaling guidance to an element that draws map pictures (HighQuality = Fant).</summary>
    public static void ApplyQuality(DependencyObject element) =>
        RenderOptions.SetBitmapScalingMode(element, BitmapScalingMode.HighQuality);

    /// <summary>Pack URI of a map picture file.</summary>
    public static Uri PictureUri(string file) => PackResources.Pack(MapsFolder + file);

    /// <summary>Normalises a map name for lookup (case, curly apostrophes, spacing).</summary>
    public static string Normalize(string name)
    {
        var sb = new StringBuilder(name.Length);
        bool space = false;
        foreach (char c in name.Normalize(NormalizationForm.FormC))
        {
            char ch = c is '’' or '‘' or '`' or '´' ? '\'' : c;
            if (char.IsWhiteSpace(ch))
            {
                space = sb.Length > 0;
                continue;
            }

            if (space)
            {
                sb.Append(' ');
                space = false;
            }

            sb.Append(char.ToLower(ch, CultureInfo.InvariantCulture));
        }

        return sb.ToString();
    }

    /// <summary>Parses the index JSON (map name → file name).</summary>
    public static IReadOnlyDictionary<string, string> ParseIndex(string json)
    {
        var map = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
        return map ?? new Dictionary<string, string>();
    }

    private static IReadOnlyDictionary<string, string> LoadIndex()
    {
        using Stream? stream = OpenResource("map-images.json");
        if (stream is null)
        {
            return new Dictionary<string, string>();
        }

        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return ParseIndex(reader.ReadToEnd());
    }

    private static Stream? OpenResource(string file)
    {
        try
        {
            return Application.GetResourceStream(PictureUri(file))?.Stream;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private int MeasureSmallest(Func<BitmapSource, int> size, int fallback)
    {
        int min = int.MaxValue;
        foreach (string map in MapNames)
        {
            if (TryGetImage(map) is BitmapSource image)
            {
                min = Math.Min(min, size(image));
            }
        }

        return min == int.MaxValue ? fallback : min;
    }

    private BitmapSource? Decode(string file)
    {
        try
        {
            using Stream? stream = _open(file);
            if (stream is null)
            {
                return null;
            }

            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            BitmapSource frame = decoder.Frames[0];
            return Normalise(frame);
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or FileFormatException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// 96 DPI, edges trimmed where a picture has a translucent border (a torn-frame remnant), and stored opaque
    /// (Bgr32) when every pixel is opaque, which renders faster.
    /// </summary>
    private static BitmapSource Normalise(BitmapSource frame)
    {
        var bgra = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        int width = bgra.PixelWidth;
        int height = bgra.PixelHeight;
        int stride = width * 4;
        byte[] pixels = new byte[stride * height];
        bgra.CopyPixels(pixels, stride, 0);

        // Trim up to 2 px from any edge whose average alpha shows (below 250).
        int top = 0, bottom = 0, left = 0, right = 0;
        while (top < 2 && RowAlpha(pixels, width, top) < 250)
        {
            top++;
        }

        while (bottom < 2 && RowAlpha(pixels, width, height - 1 - bottom) < 250)
        {
            bottom++;
        }

        while (left < 2 && ColumnAlpha(pixels, width, height, left) < 250)
        {
            left++;
        }

        while (right < 2 && ColumnAlpha(pixels, width, height, width - 1 - right) < 250)
        {
            right++;
        }

        int w = width - left - right;
        int h = height - top - bottom;
        bool opaque = true;
        byte[] output = new byte[w * 4 * h];
        for (int y = 0; y < h; y++)
        {
            Buffer.BlockCopy(pixels, ((y + top) * width + left) * 4, output, y * w * 4, w * 4);
        }

        for (int i = 3; i < output.Length; i += 4)
        {
            if (output[i] != 255)
            {
                opaque = false;
                break;
            }
        }

        BitmapSource result = BitmapSource.Create(w, h, 96, 96, opaque ? PixelFormats.Bgr32 : PixelFormats.Bgra32, null, output, w * 4);
        result.Freeze();
        return result;
    }

    // The average alpha of a row / column, ignoring the 2 px at each end (they belong to the other edges).
    private static double RowAlpha(byte[] pixels, int width, int y)
    {
        long sum = 0;
        int n = 0;
        for (int x = Math.Min(2, width - 1); x < Math.Max(width - 2, 1); x++, n++)
        {
            sum += pixels[(y * width + x) * 4 + 3];
        }

        return n == 0 ? 255 : (double)sum / n;
    }

    private static double ColumnAlpha(byte[] pixels, int width, int height, int x)
    {
        long sum = 0;
        int n = 0;
        for (int y = Math.Min(2, height - 1); y < Math.Max(height - 2, 1); y++, n++)
        {
            sum += pixels[(y * width + x) * 4 + 3];
        }

        return n == 0 ? 255 : (double)sum / n;
    }
}
