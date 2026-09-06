namespace DBDAutoReShade.Core.Services;

/// <summary>
/// A captured image in 32-bit BGRA, top-down, stride = Width * 4.
/// Plain managed bytes: no GDI/System.Drawing handles leak through this type,
/// so the OCR pipeline can run fully in memory and be unit-tested.
/// </summary>
public sealed class CapturedImage : IDisposable
{
    private bool _disposed;

    public CapturedImage(int width, int height, byte[] bgra)
    {
        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        if (height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height));
        }

        ArgumentNullException.ThrowIfNull(bgra);
        if (bgra.Length != width * height * 4)
        {
            throw new ArgumentException("Pixel buffer length must equal width*height*4.", nameof(bgra));
        }

        Width = width;
        Height = height;
        Pixels = bgra;
    }

    public int Width { get; }

    public int Height { get; }

    public byte[] Pixels { get; }

    public int Stride => Width * 4;

    public CapturedImage Clone()
    {
        var copy = new byte[Pixels.Length];
        Buffer.BlockCopy(Pixels, 0, copy, 0, Pixels.Length);
        return new CapturedImage(Width, Height, copy);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            Array.Clear(Pixels, 0, Pixels.Length);
            _disposed = true;
        }
    }
}
