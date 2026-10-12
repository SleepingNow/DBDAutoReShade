using DBDAutoReShade.Core.Services;

namespace DBDAutoReShade.Desktop.Services;

/// <summary>
/// Minimal BMP writer for Debug Mode failure crops (no System.Drawing needed).
/// Writes 24-bit bottom-up BI_RGB bitmaps. Bounded retention is enforced by
/// the caller (see <see cref="DebugCropStore"/>).
/// </summary>
internal static class BmpWriter
{
    /// <summary>Write <paramref name="image"/> to <paramref name="path"/>, replacing any existing file.</summary>
    public static void Save24(string path, CapturedImage image) => Save24(path, image, FileMode.Create);

    /// <summary>
    /// Write <paramref name="image"/> to <paramref name="path"/> using
    /// <paramref name="mode"/>; pass <see cref="FileMode.CreateNew"/> to never
    /// overwrite an existing file (throws <see cref="IOException"/> instead).
    /// </summary>
    public static void Save24(string path, CapturedImage image, FileMode mode)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(image);

        int width = image.Width;
        int height = image.Height;
        int rowSize = ((width * 3 + 3) / 4) * 4;
        int pixelDataSize = rowSize * height;

        using var fs = new FileStream(path, mode, FileAccess.Write, FileShare.Read);
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

        // One buffered row at a time (padding bytes stay zero).
        byte[] row = new byte[rowSize];
        byte[] src = image.Pixels;
        for (int y = height - 1; y >= 0; y--)
        {
            int i = y * width * 4;
            for (int x = 0, o = 0; x < width; x++, i += 4, o += 3)
            {
                row[o] = src[i]; // B
                row[o + 1] = src[i + 1]; // G
                row[o + 2] = src[i + 2]; // R
            }

            bw.Write(row);
        }
    }
}
