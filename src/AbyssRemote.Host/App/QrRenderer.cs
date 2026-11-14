using System.Drawing.Imaging;

namespace AbyssRemote.Host.App;

/// <summary>Draws a <see cref="QrCode"/> as a picture. Every module becomes a square of whole
/// pixels (no smoothing, which would blur the edges a scanner looks for), and the symbol gets
/// the light border the standard requires.</summary>
public static class QrRenderer
{
    /// <summary>The "quiet zone": four light modules on every side, so a scanner can tell
    /// where the symbol ends.</summary>
    public const int QuietZone = 4;

    /// <summary>Width and height of the picture, in modules, including the quiet zone.</summary>
    public static int ModulesPerSide(QrCode code) => code.Size + 2 * QuietZone;

    /// <summary>The largest whole number of pixels per module that fits into
    /// <paramref name="availablePixels"/>.</summary>
    public static int PixelsPerModule(QrCode code, int availablePixels) => Math.Max(1, availablePixels / ModulesPerSide(code));

    public static Bitmap ToBitmap(QrCode code, int pixelsPerModule, Color dark, Color light)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pixelsPerModule, 1);
        int side = ModulesPerSide(code) * pixelsPerModule;
        var bitmap = new Bitmap(side, side, PixelFormat.Format32bppRgb);
        using var graphics = Graphics.FromImage(bitmap);
        using var darkBrush = new SolidBrush(dark);
        graphics.Clear(light);
        for (int row = 0; row < code.Size; row++)
            for (int column = 0; column < code.Size; column++)
                if (code.IsDark(row, column))
                    graphics.FillRectangle(darkBrush, (column + QuietZone) * pixelsPerModule, (row + QuietZone) * pixelsPerModule,
                        pixelsPerModule, pixelsPerModule);
        return bitmap;
    }
}
