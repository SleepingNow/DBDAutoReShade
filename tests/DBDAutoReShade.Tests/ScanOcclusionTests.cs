using DBDAutoReShade.Core.Services;
using DBDAutoReShade.Desktop.Services;

namespace DBDAutoReShade.Tests;

/// <summary>
/// The scan's occlusion check (follow-up to F2): a DBD that is not the
/// foreground window may still be read when no other window covers its
/// loading-screen text, e.g. DBD in plain view on one monitor while the user
/// works on another. Probe points inside the map and realm OCR regions are
/// hit-tested; every one must land on DBD's own window.
/// </summary>
public sealed class ScanOcclusionTests
{
    private static readonly IntPtr Dbd = new(0x1234);
    private static readonly IntPtr Other = new(0x5678);

    /// <summary>A screen rectangle with exclusive right and bottom edges, like a window's.</summary>
    private readonly record struct Rect(int Left, int Top, int Right, int Bottom)
    {
        public bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;

        public int Width => Right - Left;

        public int Height => Bottom - Top;
    }

    /// <summary>Hit-testing of a desktop: DBD's client area, with other windows in front of it; zero off screen.</summary>
    private static Func<int, int, IntPtr> Desktop(DbdClientBounds dbd, params Rect[] inFront) => (x, y) =>
    {
        if (inFront.Any(r => r.Contains(x, y)))
        {
            return Other;
        }

        return new Rect(dbd.ScreenX, dbd.ScreenY, dbd.ScreenX + dbd.Width, dbd.ScreenY + dbd.Height).Contains(x, y)
            ? Dbd
            : IntPtr.Zero;
    };

    private static Rect ScreenRect(DbdClientBounds b, NormalizedRegion region)
    {
        IntRegion px = DbdLoadingScreenRegions.ToPixels(region, b.Width, b.Height);
        return new Rect(b.ScreenX + px.X, b.ScreenY + px.Y, b.ScreenX + px.X + px.Width, b.ScreenY + px.Y + px.Height);
    }

    private static bool Showing(DbdClientBounds b, params Rect[] inFront) =>
        DbdWindowService.AreLoadingScreenRegionsShowing(Dbd, b, Desktop(b, inFront));

    public static TheoryData<int, int, int, int> Layouts => new()
    {
        { 0, 0, 1920, 1080 },        // primary monitor
        { 1920, 0, 1920, 1080 },     // right-hand monitor
        { -2560, -200, 2560, 1440 }, // left-hand monitor, offset upwards
        { 0, 0, 3440, 1440 },        // ultrawide
        { 100, 130, 1280, 720 },     // windowed
    };

    [Theory]
    [MemberData(nameof(Layouts))]
    public void ProbePoints_LieInsideBothTextRegions_OnDbdsOwnMonitor(int x, int y, int width, int height)
    {
        var dbd = new DbdClientBounds(x, y, width, height, 96);
        Rect map = ScreenRect(dbd, DbdLoadingScreenRegions.MapName);
        Rect realm = ScreenRect(dbd, DbdLoadingScreenRegions.RealmName);

        IReadOnlyList<(int X, int Y)> points = DbdWindowService.LoadingScreenProbePoints(dbd);

        Assert.Equal(60, points.Count);
        Assert.All(points, p => Assert.True(map.Contains(p.X, p.Y) || realm.Contains(p.X, p.Y), $"{p} is outside the text"));
        foreach (Rect region in new[] { map, realm })
        {
            var inside = points.Where(p => region.Contains(p.X, p.Y)).ToList();
            Assert.True(inside.Count >= 30, $"only {inside.Count} probes in {region}");
            Assert.True(inside.Max(p => p.X) - inside.Min(p => p.X) >= region.Width * 0.85, "probes span the line");
            Assert.True(inside.Max(p => p.Y) - inside.Min(p => p.Y) >= region.Height * 0.55, "probes span the height");
        }
    }

    [Theory]
    [MemberData(nameof(Layouts))]
    public void NothingInFrontOfTheText_IsShowing(int x, int y, int width, int height)
    {
        var dbd = new DbdClientBounds(x, y, width, height, 96);

        Assert.True(Showing(dbd));

        // This app's window over the top-right of the game does not hide the text either.
        Assert.True(Showing(dbd, new Rect(x + width / 2 + width / 10, y, x + width, y + height / 2)));
    }

    [Fact]
    public void AWindowOverTheText_IsNotShowing()
    {
        var dbd = new DbdClientBounds(0, 0, 1920, 1080, 96);

        Assert.False(Showing(dbd, new Rect(0, 0, 1920, 1080)));        // a maximized browser
        Assert.False(Showing(dbd, new Rect(800, 300, 1920, 1080)));     // Discord over the right half of the text
        Assert.False(Showing(dbd, new Rect(200, 900, 700, 1000)));      // a window over the realm line only
        Assert.False(Showing(dbd, new Rect(-400, 740, 160, 880)));      // a window reaching in from the left
    }

    [Fact]
    public void AnyWindowBigEnoughForALineOfText_InsideEitherRegion_IsSeen()
    {
        // Wherever a window at least ~10% of the line wide and ~30% of its height
        // tall sits inside the text, some probe lands on it.
        var dbd = new DbdClientBounds(1920, 0, 1920, 1080, 96);
        foreach (NormalizedRegion normalized in new[] { DbdLoadingScreenRegions.MapName, DbdLoadingScreenRegions.RealmName })
        {
            Rect region = ScreenRect(dbd, normalized);
            int w = (int)Math.Ceiling(region.Width * 0.1) + 2;
            int h = (int)Math.Ceiling(region.Height * 0.3) + 2;
            int placements = 0;
            for (int left = region.Left; left + w <= region.Right; left += 5)
            {
                for (int top = region.Top; top + h <= region.Bottom; top += 3)
                {
                    var cover = new Rect(left, top, left + w, top + h);
                    Assert.False(Showing(dbd, cover), $"a {w}x{h} window at {cover} went unseen");
                    placements++;
                }
            }

            Assert.True(placements > 1000, $"only {placements} placements tried");
        }
    }

    [Theory]
    [InlineData(1920, 1080, 40, false)]  // Windows 10 taskbar, 100 %
    [InlineData(1920, 1080, 48, false)]  // Windows 11 taskbar, 100 %
    [InlineData(1920, 1080, 60, false)]  // Windows 11 taskbar, 125 %
    [InlineData(2560, 1440, 60, false)]  // 1440p, 125 %
    [InlineData(3840, 2160, 72, false)]  // 4K, 150 %
    [InlineData(1920, 1080, 62, true)]   // Windows 10 taskbar docked left, 100 %
    [InlineData(1920, 1080, 93, true)]   // docked left, 150 %
    public void TheTaskbarShownOverAnUnfocusedDbd_DoesNotHideTheText(int width, int height, int taskbar, bool left)
    {
        // Windows shows the taskbar over a full-screen DBD once DBD is not in front.
        var dbd = new DbdClientBounds(0, 0, width, height, 96);
        Rect bar = left ? new Rect(0, 0, taskbar, height) : new Rect(0, height - taskbar, width, height);

        Assert.True(Showing(dbd, bar));
    }

    [Fact]
    public void ATaskbarTallEnoughToReachTheText_CountsAsCovering()
    {
        var dbd = new DbdClientBounds(0, 0, 1920, 1080, 96);

        Assert.False(Showing(dbd, new Rect(0, 1080 - 96, 1920, 1080))); // two rows at 125 %
    }

    [Fact]
    public void OffScreenText_NoWindow_OrEmptyBounds_IsNotShowing()
    {
        // Windowed DBD dragged partly below the bottom of the screen.
        var dbd = new DbdClientBounds(100, 600, 1280, 720, 96);
        Func<int, int, IntPtr> hitTest = (x, y) => y >= 1080 ? IntPtr.Zero : Dbd;
        Assert.False(DbdWindowService.AreLoadingScreenRegionsShowing(Dbd, dbd, hitTest));

        Assert.False(DbdWindowService.AreLoadingScreenRegionsShowing(IntPtr.Zero, dbd, (_, _) => IntPtr.Zero));
        var empty = new DbdClientBounds(0, 0, 0, 0, 96);
        Assert.False(DbdWindowService.AreLoadingScreenRegionsShowing(Dbd, empty, (_, _) => Dbd));
        Assert.Empty(DbdWindowService.LoadingScreenProbePoints(empty));
        Assert.False(DbdWindowService.IsLoadingScreenTextUncovered(IntPtr.Zero));
    }
}
