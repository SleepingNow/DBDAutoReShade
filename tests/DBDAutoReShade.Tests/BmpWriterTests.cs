using DBDAutoReShade.Core.Services;
using DBDAutoReShade.Desktop.Services;

namespace DBDAutoReShade.Tests;

public sealed class BmpWriterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dbd-bmp-" + Guid.NewGuid().ToString("N"));

    public BmpWriterTests() => Directory.CreateDirectory(_dir);

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

    /// <summary>3x2 image whose pixel (x, y) is B=10x+y+1, G=+100, R=+200.</summary>
    private static CapturedImage Sample()
    {
        var px = new byte[3 * 2 * 4];
        for (int y = 0; y < 2; y++)
        {
            for (int x = 0; x < 3; x++)
            {
                int i = (y * 3 + x) * 4;
                byte v = (byte)(10 * x + y + 1);
                px[i] = v;
                px[i + 1] = (byte)(v + 100);
                px[i + 2] = (byte)(v + 200);
                px[i + 3] = 255;
            }
        }

        return new CapturedImage(3, 2, px);
    }

    [Fact]
    public void Save24_WritesBottomUpRowsWithPadding()
    {
        string path = Path.Combine(_dir, "a.bmp");
        using CapturedImage img = Sample();
        BmpWriter.Save24(path, img, FileMode.CreateNew);

        byte[] b = File.ReadAllBytes(path);
        const int rowSize = 12; // 3 px * 3 bytes = 9, padded to 12
        Assert.Equal(54 + rowSize * 2, b.Length);
        Assert.Equal((byte)'B', b[0]);
        Assert.Equal((byte)'M', b[1]);
        Assert.Equal(b.Length, BitConverter.ToInt32(b, 2));
        Assert.Equal(54, BitConverter.ToInt32(b, 10));
        Assert.Equal(40, BitConverter.ToInt32(b, 14));
        Assert.Equal(3, BitConverter.ToInt32(b, 18));
        Assert.Equal(2, BitConverter.ToInt32(b, 22)); // positive height: bottom-up
        Assert.Equal(24, BitConverter.ToInt16(b, 28));
        Assert.Equal(rowSize * 2, BitConverter.ToInt32(b, 34));

        // First stored row is the image's bottom row (y = 1), then y = 0.
        for (int fileRow = 0; fileRow < 2; fileRow++)
        {
            int y = 1 - fileRow;
            int rowStart = 54 + fileRow * rowSize;
            for (int x = 0; x < 3; x++)
            {
                byte v = (byte)(10 * x + y + 1);
                Assert.Equal(v, b[rowStart + x * 3]);
                Assert.Equal((byte)(v + 100), b[rowStart + x * 3 + 1]);
                Assert.Equal((byte)(v + 200), b[rowStart + x * 3 + 2]);
            }

            Assert.Equal(new byte[3], b[(rowStart + 9)..(rowStart + 12)]); // padding
        }
    }

    [Fact]
    public void Save24_CreateNew_NeverOverwrites()
    {
        string path = Path.Combine(_dir, "taken.bmp");
        File.WriteAllText(path, "original");
        using CapturedImage img = Sample();

        Assert.Throws<IOException>(() => BmpWriter.Save24(path, img, FileMode.CreateNew));
        Assert.Equal("original", File.ReadAllText(path));
    }

    [Fact]
    public void Save24_DefaultOverload_Overwrites()
    {
        string path = Path.Combine(_dir, "replace.bmp");
        File.WriteAllText(path, "original");
        using CapturedImage img = Sample();

        BmpWriter.Save24(path, img);
        Assert.Equal((byte)'B', File.ReadAllBytes(path)[0]);
    }
}
