using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace AbyssRemote.Host;

public record CursorSnapshot(int X, int Y, bool Visible, CursorImage? Image = null);
public interface ICursorSource : IDisposable { CursorSnapshot Read(); }

/// <summary>Read the cursor without screen capture or encoding a desktop frame. One reader
/// per control socket; the small PNG is regenerated only when the Windows cursor changes.</summary>
public sealed class WindowsCursorSource : ICursorSource
{
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Info { public int Size, Flags; public IntPtr Cursor; public Point Position; }
    [StructLayout(LayoutKind.Sequential)] private struct IconInfo { public int IsIcon, HotX, HotY; public IntPtr Mask, Color; }
    [DllImport("user32.dll")] private static extern bool GetCursorInfo(ref Info info);
    [DllImport("user32.dll")] private static extern bool GetIconInfo(IntPtr cursor, out IconInfo info);
    [DllImport("user32.dll")] private static extern bool DrawIconEx(IntPtr dc, int x, int y, IntPtr icon, int width, int height, int step, IntPtr brush, int flags);
    private IntPtr _last;

    public CursorSnapshot Read()
    {
        var info = new Info { Size = Marshal.SizeOf<Info>() };
        if (!GetCursorInfo(ref info) || (info.Flags & 1) == 0 || info.Cursor == IntPtr.Zero)
            return new(0, 0, false);
        CursorImage? image = null;
        if (info.Cursor != _last)
        {
            image = Image(info.Cursor);
            if (image is not null) _last = info.Cursor;
        }
        return new(info.Position.X, info.Position.Y, true, image);
    }

    private static CursorImage? Image(IntPtr cursor)
    {
        if (!GetIconInfo(cursor, out var icon)) return null;
        try
        {
            if (icon.Mask == IntPtr.Zero) return null;
            using var mask = System.Drawing.Image.FromHbitmap(icon.Mask);
            int width = mask.Width, height = icon.Color == IntPtr.Zero ? mask.Height / 2 : mask.Height;
            if (width is < 1 or > 256 || height is < 1 or > 256) return null;
            using var black = Render(cursor, width, height, System.Drawing.Color.Black);
            using var white = Render(cursor, width, height, System.Drawing.Color.White);
            using var result = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            var inverted = new bool[width * height];
            // Drawing against black and white recovers alpha, including monochrome cursors.
            // XOR pixels cannot invert a separate DOM image: use white with a dark outline.
            for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
            {
                var b = black.GetPixel(x, y); var w = white.GetPixel(x, y);
                int difference = Math.Max(w.R - b.R, Math.Max(w.G - b.G, w.B - b.B));
                if (w.R < b.R || w.G < b.G || w.B < b.B) { inverted[y * width + x] = true; continue; }
                int alpha = Math.Clamp(255 - difference, 0, 255);
                if (alpha > 0) result.SetPixel(x, y, System.Drawing.Color.FromArgb(alpha,
                    Math.Min(255, b.R * 255 / alpha), Math.Min(255, b.G * 255 / alpha), Math.Min(255, b.B * 255 / alpha)));
            }
            for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
            {
                if (!inverted[y * width + x]) continue;
                result.SetPixel(x, y, System.Drawing.Color.White);
                for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++)
                {
                    int nx = x + dx, ny = y + dy;
                    if (nx < 0 || ny < 0 || nx >= width || ny >= height || inverted[ny * width + nx]) continue;
                    if (result.GetPixel(nx, ny).A == 0) result.SetPixel(nx, ny, System.Drawing.Color.Black);
                }
            }
            using var png = new MemoryStream(); result.Save(png, ImageFormat.Png);
            return new(png.ToArray(), icon.HotX, icon.HotY, width, height);
        }
        finally
        {
            if (icon.Mask != IntPtr.Zero) Gdi.DeleteObject(icon.Mask);
            if (icon.Color != IntPtr.Zero) Gdi.DeleteObject(icon.Color);
        }
    }
    private static Bitmap Render(IntPtr cursor, int width, int height, Color background)
    {
        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap); graphics.Clear(background);
        var dc = graphics.GetHdc();
        try { DrawIconEx(dc, 0, 0, cursor, width, height, 0, IntPtr.Zero, 3); }
        finally { graphics.ReleaseHdc(dc); }
        return bitmap;
    }
    public void Dispose() { } // Cursor handles are owned by Windows; never destroy them.
}
