using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace AbyssRemote.Host.GameMode;

/// <summary>Turns the mouse pointer image that desktop duplication reports into a PNG the
/// browser can draw on top of the video. Drawing the pointer in the browser keeps it sharp and
/// lets it move without waiting for a new video frame.</summary>
internal static class CursorShapes
{
    public static byte[] ToPng(byte[] shape, DxgiOutduplPointerShapeInfo info)
    {
        int width = (int)info.Width;
        int height = info.Type == Constants.PointerMonochrome ? (int)info.Height / 2 : (int)info.Height;
        int pitch = (int)info.Pitch;
        var bgra = new byte[width * height * 4];
        var invert = new bool[width * height]; // pixels that XOR with the screen, like the text I-beam

        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int i = (y * width + x) * 4;
            switch (info.Type)
            {
                case Constants.PointerMonochrome:
                {
                    // 1 bit per pixel: AND mask in the top half, XOR mask in the bottom half.
                    int bit = 0x80 >> (x % 8);
                    bool and = (shape[y * pitch + x / 8] & bit) != 0;
                    bool xor = (shape[(y + height) * pitch + x / 8] & bit) != 0;
                    if (and && xor) invert[y * width + x] = true;
                    else if (!and) Set(bgra, i, xor ? (byte)255 : (byte)0, 255);
                    break;
                }
                case Constants.PointerColor: // 32-bit BGRA with real alpha
                    Buffer.BlockCopy(shape, y * pitch + x * 4, bgra, i, 4);
                    break;
                case Constants.PointerMaskedColor:
                {
                    // 32-bit: alpha 0 means "use this color", 0xFF means "XOR this color with the screen".
                    int s = y * pitch + x * 4;
                    if (shape[s + 3] == 0) { Buffer.BlockCopy(shape, s, bgra, i, 3); bgra[i + 3] = 255; }
                    else if (shape[s] != 0 || shape[s + 1] != 0 || shape[s + 2] != 0) invert[y * width + x] = true;
                    break;
                }
            }
        }

        // The browser cannot XOR with the screen, so "inverting" pixels become white with a dark
        // outline, which stays visible on both light and dark backgrounds.
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            if (!invert[y * width + x]) continue;
            Set(bgra, (y * width + x) * 4, 255, 255);
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                int nx = x + dx, ny = y + dy;
                if (nx < 0 || ny < 0 || nx >= width || ny >= height || invert[ny * width + nx]) continue;
                int n = (ny * width + nx) * 4;
                if (bgra[n + 3] == 0) Set(bgra, n, 0, 200);
            }
        }

        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < height; y++)
                Marshal.Copy(bgra, y * width * 4, data.Scan0 + y * data.Stride, width * 4);
        }
        finally { bitmap.UnlockBits(data); }
        using var png = new MemoryStream();
        bitmap.Save(png, ImageFormat.Png);
        return png.ToArray();
    }

    private static void Set(byte[] bgra, int i, byte gray, byte alpha)
    {
        bgra[i] = bgra[i + 1] = bgra[i + 2] = gray;
        bgra[i + 3] = alpha;
    }
}
