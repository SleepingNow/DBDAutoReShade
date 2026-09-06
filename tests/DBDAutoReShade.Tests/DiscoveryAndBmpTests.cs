using DBDAutoReShade.Core.Services;
using DBDAutoReShade.Desktop.Services;

namespace DBDAutoReShade.Tests;

public sealed class DiscoveryAndBmpTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dbd-disc-" + Guid.NewGuid().ToString("N"));

    public DiscoveryAndBmpTests() => Directory.CreateDirectory(_dir);

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

    [Fact]
    public void Discover_DoesNotThrow_WithoutDbd()
    {
        IReadOnlyList<DbdLocation> found = DbdDiscoveryService.Discover();
        Assert.NotNull(found);
    }

    [Fact]
    public void ReshadeDiscovery_FindsIniNextToGame()
    {
        string gameDir = Path.Combine(_dir, "DeadByDaylight", "Binaries", "Win64");
        Directory.CreateDirectory(gameDir);
        string exe = Path.Combine(gameDir, "DeadByDaylight-Win64-Shipping.exe");
        File.WriteAllText(exe, "x");
        File.WriteAllText(Path.Combine(gameDir, "ReShade.ini"), "[GENERAL]\n");
        File.WriteAllText(Path.Combine(gameDir, "dxgi.dll"), "x");

        ReshadeLocation? loc = ReshadeDiscoveryService.Discover(exe);
        Assert.NotNull(loc);
        Assert.EndsWith("ReShade.ini", loc.IniPath);
        Assert.NotNull(loc.HookDllPath);
    }

    [Fact]
    public void ReshadeDiscovery_ReturnsNull_WhenAbsent()
    {
        Assert.Null(ReshadeDiscoveryService.Discover(null));
        Assert.Null(ReshadeDiscoveryService.Discover(Path.Combine(_dir, "nothing.exe")));
    }

    [Fact]
    public void BmpWriter_WritesReadableFile()
    {
        var px = new byte[4 * 2 * 4];
        using var img = new CapturedImage(4, 2, px);
        string path = Path.Combine(_dir, "crop.bmp");
        BmpWriter.Save24(path, img);

        Assert.True(File.Exists(path));
        byte[] bytes = File.ReadAllBytes(path);
        Assert.Equal((byte)'B', bytes[0]);
        Assert.Equal((byte)'M', bytes[1]);
        Assert.True(bytes.Length > 54);
    }
}
