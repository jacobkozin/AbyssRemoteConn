using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace AbyssRemote.Host;

public record CursorSnapshot(int X, int Y, bool Visible, CursorImage? Image = null);
public interface ICursorSource : IDisposable { CursorSnapshot Read(); }

/// <summary>A cursor as pixels (0xAARRGGBB, top row first) with its hot spot.</summary>
public sealed record CursorPicture(int[] Argb, int Width, int Height, int HotX, int HotY);

/// <summary>Read the cursor without screen capture or encoding a desktop frame. One reader
/// per control socket; a PNG is made only when the cursor's picture really changed.</summary>
public sealed class WindowsCursorSource : ICursorSource
{
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Info { public int Size, Flags; public IntPtr Cursor; public Point Position; }
    [StructLayout(LayoutKind.Sequential)] private struct IconInfo { public int IsIcon, HotX, HotY; public IntPtr Mask, Color; }
    [StructLayout(LayoutKind.Sequential)]
    private struct DibHeader { public int Size, Width, Height; public short Planes, BitCount; public int Compression, SizeImage, XPels, YPels, ColorsUsed, ColorsImportant; }
    [DllImport("user32.dll")] private static extern bool GetCursorInfo(ref Info info);
    [DllImport("user32.dll")] private static extern bool GetIconInfo(IntPtr cursor, out IconInfo info);
    [DllImport("user32.dll")] private static extern bool DrawIconEx(IntPtr dc, int x, int y, IntPtr icon, int width, int height, int step, IntPtr brush, int flags);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref DibHeader header, uint usage, out IntPtr bits, IntPtr section, uint offset);

    /// <summary>How often the picture of an unchanged cursor handle is looked at again.</summary>
    public static readonly TimeSpan LookAgain = TimeSpan.FromMilliseconds(100);

    private readonly Func<IntPtr, CursorPicture?> _look;
    private readonly Func<long> _milliseconds;
    private IntPtr _handle;
    private long _lookedAt;
    private bool _looked, _nothingToSee;
    private ulong _sent;

    public WindowsCursorSource() : this(Picture, () => Environment.TickCount64) { }

    /// <summary>For tests: a pretend way to look at a cursor, and a clock they move by hand.</summary>
    internal WindowsCursorSource(Func<IntPtr, CursorPicture?> look, Func<long> milliseconds)
    {
        _look = look;
        _milliseconds = milliseconds;
    }

    public CursorSnapshot Read()
    {
        var info = new Info { Size = Marshal.SizeOf<Info>() };
        if (!GetCursorInfo(ref info) || (info.Flags & 1) == 0 || info.Cursor == IntPtr.Zero)
            return new(0, 0, false);
        return Describe(info.Cursor, info.Position.X, info.Position.Y);
    }

    /// <summary>What to tell the browser about the cursor Windows shows right now.
    /// The handle alone does not say what a cursor looks like: a program can give a system
    /// cursor a new picture without changing its handle. A cursor hider does exactly that; it
    /// swaps in a blank picture while the mouse rests and the real one when it moves. So the
    /// picture is looked at again several times a second, a blank one is reported as "no
    /// pointer to see" (as on the PC's own screen), and a PNG is sent only when the picture
    /// differs from the last one sent.</summary>
    internal CursorSnapshot Describe(IntPtr cursor, int x, int y)
    {
        long now = _milliseconds();
        CursorImage? image = null;
        if (!_looked || cursor != _handle || now - _lookedAt >= (long)LookAgain.TotalMilliseconds)
        {
            _looked = true;
            _handle = cursor;
            _lookedAt = now;
            var picture = _look(cursor);
            _nothingToSee = picture is null;
            if (picture is not null)
            {
                ulong print = Fingerprint(picture);
                if (print != _sent) { _sent = print; image = Encode(picture); }
            }
        }
        return new(x, y, !_nothingToSee, image);
    }

    /// <summary>The cursor as a PNG, or null when it has nothing to see.</summary>
    internal static CursorImage? Image(IntPtr cursor) => Picture(cursor) is { } picture ? Encode(picture) : null;

    /// <summary>Turns a Windows cursor into pixels with real transparency. The cursor is drawn
    /// twice with plain GDI, once on black and once on white. A pixel that looks the same on
    /// both is solid, one that shows the background is clear, and one that inverts the
    /// background (the text I-beam does) becomes white with a dark outline, because a web
    /// page cannot invert what lies under a picture. Returns null when there is nothing to
    /// see or the cursor cannot be drawn, so a blank picture is never sent as a cursor.</summary>
    internal static CursorPicture? Picture(IntPtr cursor)
    {
        if (!GetIconInfo(cursor, out var icon)) return null;
        try
        {
            if (icon.Mask == IntPtr.Zero) return null;
            using var mask = System.Drawing.Image.FromHbitmap(icon.Mask);
            // A cursor without a colour bitmap keeps its two masks one above the other.
            int width = mask.Width, height = icon.Color == IntPtr.Zero ? mask.Height / 2 : mask.Height;
            if (width is < 1 or > 256 || height is < 1 or > 256) return null;
            int[]? black = Render(cursor, width, height, 0x000000), white = Render(cursor, width, height, 0xFFFFFF);
            if (black is null || white is null) return null;

            var argb = new int[width * height];
            var inverted = new bool[width * height];
            bool somethingToSee = false;
            for (int i = 0; i < argb.Length; i++)
            {
                int br = (black[i] >> 16) & 255, bg = (black[i] >> 8) & 255, bb = black[i] & 255;
                int wr = (white[i] >> 16) & 255, wg = (white[i] >> 8) & 255, wb = white[i] & 255;
                if (wr < br || wg < bg || wb < bb) { inverted[i] = true; somethingToSee = true; continue; }
                int alpha = 255 - Math.Max(wr - br, Math.Max(wg - bg, wb - bb));
                if (alpha <= 0) continue;
                argb[i] = (alpha << 24) | (Math.Min(255, br * 255 / alpha) << 16) | (Math.Min(255, bg * 255 / alpha) << 8) | Math.Min(255, bb * 255 / alpha);
                if (alpha >= 32) somethingToSee = true;
            }
            if (!somethingToSee) return null;

            const int White = unchecked((int)0xFFFFFFFF), Black = unchecked((int)0xFF000000);
            for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
            {
                if (!inverted[y * width + x]) continue;
                argb[y * width + x] = White;
                for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++)
                {
                    int nx = x + dx, ny = y + dy;
                    if (nx < 0 || ny < 0 || nx >= width || ny >= height || inverted[ny * width + nx]) continue;
                    if ((argb[ny * width + nx] >>> 24) == 0) argb[ny * width + nx] = Black;
                }
            }
            return new(argb, width, height, icon.HotX, icon.HotY);
        }
        finally
        {
            if (icon.Mask != IntPtr.Zero) Gdi.DeleteObject(icon.Mask);
            if (icon.Color != IntPtr.Zero) Gdi.DeleteObject(icon.Color);
        }
    }

    /// <summary>Draws the cursor on a plain background with GDI alone and returns the pixels as
    /// 0x00RRGGBB. (Drawing through a GDI+ picture instead is unreliable for GDI output.)</summary>
    private static int[]? Render(IntPtr cursor, int width, int height, int background)
    {
        var header = new DibHeader { Size = Marshal.SizeOf<DibHeader>(), Width = width, Height = -height, Planes = 1, BitCount = 32 };
        IntPtr dc = Gdi.CreateCompatibleDC(IntPtr.Zero);
        if (dc == IntPtr.Zero) return null;
        IntPtr dib = CreateDIBSection(dc, ref header, 0, out IntPtr bits, IntPtr.Zero, 0);
        if (dib == IntPtr.Zero) { Gdi.DeleteDC(dc); return null; }
        IntPtr previous = Gdi.SelectObject(dc, dib);
        try
        {
            var pixels = new int[width * height];
            Array.Fill(pixels, background);
            Marshal.Copy(pixels, 0, bits, pixels.Length);
            if (!DrawIconEx(dc, 0, 0, cursor, width, height, 0, IntPtr.Zero, 3)) return null;
            Gdi.GdiFlush();
            Marshal.Copy(bits, pixels, 0, pixels.Length);
            return pixels;
        }
        finally
        {
            Gdi.SelectObject(dc, previous);
            Gdi.DeleteObject(dib);
            Gdi.DeleteDC(dc);
        }
    }

    private static CursorImage Encode(CursorPicture picture)
    {
        using var bitmap = new Bitmap(picture.Width, picture.Height, PixelFormat.Format32bppArgb);
        var data = bitmap.LockBits(new Rectangle(0, 0, picture.Width, picture.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < picture.Height; y++)
                Marshal.Copy(picture.Argb, y * picture.Width, data.Scan0 + y * data.Stride, picture.Width);
        }
        finally { bitmap.UnlockBits(data); }
        using var png = new MemoryStream();
        bitmap.Save(png, ImageFormat.Png);
        return new(png.ToArray(), picture.HotX, picture.HotY, picture.Width, picture.Height);
    }

    /// <summary>A number that changes when the picture does (FNV-1a over size, hot spot and pixels).</summary>
    private static ulong Fingerprint(CursorPicture picture)
    {
        ulong hash = 14695981039346656037;
        void Add(int value) { unchecked { hash = (hash ^ (uint)value) * 1099511628211; } }
        Add(picture.Width); Add(picture.Height); Add(picture.HotX); Add(picture.HotY);
        foreach (int pixel in picture.Argb) Add(pixel);
        return hash;
    }

    public void Dispose() { } // Cursor handles are owned by Windows; never destroy them.
}
