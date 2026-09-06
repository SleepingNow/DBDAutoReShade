using DBDAutoReShade.Core.Services;

namespace DBDAutoReShade.Desktop.Services;

/// <summary>
/// Minimal BMP writer for Debug Mode failure crops (no System.Drawing needed).
/// Bounded retention is enforced by the caller.
/// </summary>
internal static class BmpWriter
{
    public static void Save24(string path, CapturedImage image)
    {
        int width = image.Width;
        int height = image.Height;
        int rowSize = ((width * 3 + 3) / 4) * 4;
        int pixelDataSize = rowSize * height;

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        using var bw = new BinaryWriter(fs);

        bw.Write((ushort)0x4D42); // 'BM'
        bw.Write(54 + pixelDataSize);
        bw.Write((ushort)0);
        bw.Write((ushort)0);
        bw.Write(54); // pixel data offset
        bw.Write(40); // DIB header size
        bw.Write(width);
        bw.Write(height);
        bw.Write((ushort)1); // planes
        bw.Write((ushort)24); // bpp
        bw.Write(0); // BI_RGB
        bw.Write(pixelDataSize);
        bw.Write(2835); // ~72 DPI
        bw.Write(2835);
        bw.Write(0);
        bw.Write(0);

        byte[] pad = new byte[rowSize - width * 3];
        byte[] src = image.Pixels;
        for (int y = height - 1; y >= 0; y--)
        {
            for (int x = 0; x < width; x++)
            {
                int i = (y * width + x) * 4;
                bw.Write(src[i]); // B
                bw.Write(src[i + 1]); // G
                bw.Write(src[i + 2]); // R
            }

            if (pad.Length > 0)
            {
                bw.Write(pad);
            }
        }
    }
}
