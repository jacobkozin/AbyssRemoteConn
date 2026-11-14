using System.Drawing.Drawing2D;
using System.Reflection;
using System.Runtime.InteropServices;

namespace AbyssRemote.Host.App;

/// <summary>The colours of the window: the same dark blue as the app icon and the browser client.</summary>
internal static class Palette
{
    public static readonly Color Background = ColorTranslator.FromHtml("#0A0E17");
    public static readonly Color Surface = ColorTranslator.FromHtml("#131A2A");
    public static readonly Color Raised = ColorTranslator.FromHtml("#1B2438");   // buttons and hovered rows on a surface
    public static readonly Color RaisedHover = ColorTranslator.FromHtml("#243049");
    public static readonly Color Border = ColorTranslator.FromHtml("#24304A");
    public static readonly Color Text = ColorTranslator.FromHtml("#E6EDF7");
    public static readonly Color Muted = ColorTranslator.FromHtml("#8A97B1");
    public static readonly Color Accent = ColorTranslator.FromHtml("#38BDF8");
    public static readonly Color AccentBlue = ColorTranslator.FromHtml("#3B82F6");
    public static readonly Color Success = ColorTranslator.FromHtml("#34D399");
    public static readonly Color Danger = ColorTranslator.FromHtml("#F87171");
    public static readonly Color OnAccent = ColorTranslator.FromHtml("#06121F");  // text on a bright button

    /// <summary>A colour at reduced strength over the surface, for tinted backgrounds.</summary>
    public static Color Tint(Color color, int percent, Color? over = null)
    {
        Color b = over ?? Surface;
        int Mix(int top, int bottom) => (top * percent + bottom * (100 - percent)) / 100;
        return Color.FromArgb(Mix(color.R, b.R), Mix(color.G, b.G), Mix(color.B, b.B));
    }
}

/// <summary>Fonts and sizes for one scale factor. The window lays everything out in "logical"
/// pixels (what a pixel is at 100%) and multiplies by the scale of the monitor it is on, so
/// it looks the same at 100%, 150% or 200% and can move between monitors that differ.
/// Fonts are created in pixels for the same reason: a point size would follow the scale of
/// the primary monitor instead.</summary>
internal sealed class Ui : IDisposable
{
    public const TextFormatFlags Wrapped = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
    public const TextFormatFlags OneLine = TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;

    public Ui(float scale)
    {
        Scale = scale;
        Small = Make("Segoe UI", 12);
        Body = Make("Segoe UI", 13.5f);
        Bold = Make("Segoe UI Semibold", 13.5f);
        Heading = Make("Segoe UI Semibold", 16);
        Title = Make("Segoe UI Semibold", 21);
        Code = Make("Segoe UI Semibold", 38);
        Mono = Make("Consolas", 12.5f);
    }

    public float Scale { get; }
    public Font Small { get; }
    public Font Body { get; }
    public Font Bold { get; }
    public Font Heading { get; }
    public Font Title { get; }
    public Font Code { get; }
    public Font Mono { get; }

    /// <summary>Logical pixels to real pixels on this monitor.</summary>
    public int Px(float logical) => (int)Math.Round(logical * Scale);

    /// <summary>The size of text on one line.</summary>
    public static Size MeasureLine(string text, Font font) =>
        TextRenderer.MeasureText(text, font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);

    /// <summary>The height of text that wraps at <paramref name="width"/>.</summary>
    public static int MeasureWrapped(string text, Font font, int width) =>
        text.Length == 0 ? 0 : TextRenderer.MeasureText(text, font, new Size(Math.Max(1, width), int.MaxValue), Wrapped).Height;

    public static GraphicsPath RoundedRectangle(RectangleF r, float radius)
    {
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        var path = new GraphicsPath();
        if (d <= 0) { path.AddRectangle(r); return path; }
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>A filled rounded rectangle with an optional one-pixel outline.</summary>
    public static void FillRounded(Graphics g, Rectangle bounds, float radius, Color fill, Color? outline = null)
    {
        var previous = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        // Half a pixel in from the edge, so the outline falls on whole pixels and stays sharp.
        var r = new RectangleF(bounds.X + 0.5f, bounds.Y + 0.5f, bounds.Width - 1, bounds.Height - 1);
        using var path = RoundedRectangle(r, radius);
        using (var brush = new SolidBrush(fill)) g.FillPath(brush, path);
        if (outline is { } color)
        {
            using var pen = new Pen(color);
            g.DrawPath(pen, path);
        }
        g.SmoothingMode = previous;
    }

    public static void FillCircle(Graphics g, Rectangle bounds, Color fill)
    {
        var previous = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(fill);
        g.FillEllipse(brush, bounds);
        g.SmoothingMode = previous;
    }

    public void Dispose()
    {
        foreach (var font in new[] { Small, Body, Bold, Heading, Title, Code, Mono }) font.Dispose();
    }

    private Font Make(string family, float logicalPixels) => new(family, logicalPixels * Scale, FontStyle.Regular, GraphicsUnit.Pixel);
}

/// <summary>The app icon, built into the program so the window and the tray icon work even
/// when the .ico file is missing from a source checkout (then Windows' generic icon is used).</summary>
internal static class AppIcon
{
    private const string ResourceName = "AbyssRemote.ico";

    /// <summary>All sizes in one icon, for a window: Windows picks the size it needs.</summary>
    public static Icon Load()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
        return stream is null ? (Icon)SystemIcons.Application.Clone() : new Icon(stream);
    }

    /// <summary>The picture closest to <paramref name="pixels"/>, for the tray and for drawing.</summary>
    public static Icon Load(int pixels)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
        return stream is null ? new Icon(SystemIcons.Application, pixels, pixels) : new Icon(stream, pixels, pixels);
    }
}

/// <summary>The few Windows calls the window needs that WinForms does not offer.</summary>
internal static class WindowNative
{
    /// <summary>Makes the title bar dark, like the rest of the window (Windows 10 2004 and later;
    /// older versions ignore it and keep the light title bar).</summary>
    public static void UseDarkTitleBar(IntPtr window)
    {
        int enabled = 1;
        DwmSetWindowAttribute(window, UseImmersiveDarkMode, ref enabled, sizeof(int));
    }

    /// <summary>Gives a scrollable control Windows' dark scroll bars.</summary>
    public static void UseDarkScrollBars(IntPtr control) => SetWindowTheme(control, "DarkMode_Explorer", null);

    public static void BringToFront(IntPtr window) => SetForegroundWindow(window);

    private const int UseImmersiveDarkMode = 20; // DWMWA_USE_IMMERSIVE_DARK_MODE

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] private static extern int SetWindowTheme(IntPtr window, string? application, string? idList);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
}
