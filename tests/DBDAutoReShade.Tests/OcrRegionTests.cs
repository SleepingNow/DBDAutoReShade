using DBDAutoReShade.Core.Services;

namespace DBDAutoReShade.Tests;

public sealed class OcrRegionTests
{
    [Theory]
    [InlineData(1920, 1080)] // 1080p
    [InlineData(2560, 1440)] // 1440p
    [InlineData(3840, 2160)] // 4K
    [InlineData(3440, 1440)] // ultrawide
    [InlineData(1280, 720)] // windowed
    public void Regions_ScaleProportionally_AndStayInsideClient(int w, int h)
    {
        foreach (var region in new[]
                 {
                     DbdLoadingScreenRegions.MapName, DbdLoadingScreenRegions.RealmName,
                     DbdLoadingScreenRegions.Combined
                 })
        {
            IntRegion px = DbdLoadingScreenRegions.ToPixels(region, w, h);
            Assert.True(px.X >= 0 && px.Y >= 0);
            Assert.True(px.X + px.Width <= w);
            Assert.True(px.Y + px.Height <= h);
            Assert.True(px.Width > 0 && px.Height > 0);
        }
    }

    [Fact]
    public void MapNameRegion_CoversLowerLeft()
    {
        IntRegion px = DbdLoadingScreenRegions.ToPixels(DbdLoadingScreenRegions.MapName, 1920, 1080);
        Assert.Equal(57, px.X); // 0.03 * 1920
        Assert.Equal(756, px.Y); // 0.70 * 1080
        Assert.True(px.Width > 900); // wide enough for long map names
    }

    [Fact]
    public void InvalidClientSize_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DbdLoadingScreenRegions.ToPixels(DbdLoadingScreenRegions.MapName, 0, 1080));
    }
}
