using System.Drawing.Drawing2D;
using System.Reflection;
using System.Runtime.InteropServices;

namespace AbyssRemote.Host.App;

/// <summary>The colours of the window: the Abyss game style, the same as the browser client
/// (a name in brackets is the colour's name in its style.css). Faces are ocean colours, every
/// box has one dark outline, and anything that can be pressed stands on a darker lip.</summary>
internal static class Palette
{
    // The page behind the cards, from its top to its bottom, and the strip above it
    public static readonly Color PageTop = ColorTranslator.FromHtml("#0B2838");
    public static readonly Color PageMiddle = ColorTranslator.FromHtml("#081E2C");
    public static readonly Color Background = ColorTranslator.FromHtml("#05131D");   // the bottom of the page (page)
    public static readonly Color Band = ColorTranslator.FromHtml("#040C18");         // the header strip and the title bar
    // Faces
    public static readonly Color Raised = ColorTranslator.FromHtml("#1B5B74");       // buttons at rest; the top of a card (lagoon)
    public static readonly Color RaisedHover = ColorTranslator.FromHtml("#24708E");  // a menu line or an empty check box under the mouse
    public static readonly Color Surface = ColorTranslator.FromHtml("#123D52");      // text boxes; the middle of a card (deep)
    public static readonly Color Inset = ColorTranslator.FromHtml("#0D3042");        // the bottom of a card; the shadow inside a text box
    public static readonly Color Border = ColorTranslator.FromHtml("#0B2838");       // every outline and lip (abyss)
    // Text
    public static readonly Color Text = ColorTranslator.FromHtml("#F4FBFD");         // (foam)
    public static readonly Color Muted = ColorTranslator.FromHtml("#A9C4D0");        // (mist)
    public static readonly Color OnAccent = ColorTranslator.FromHtml("#0B2838");     // on a bright face (abyss)
    // What something means
    public static readonly Color Accent = ColorTranslator.FromHtml("#7FE6F2");       // labels, addresses, the focus ring (aqua)
    public static readonly Color Teal = ColorTranslator.FromHtml("#27B9CC");         // the main button of a card; ticked boxes
    public static readonly Color Success = ColorTranslator.FromHtml("#70D69B");      // connected, done (seafoam)
    public static readonly Color Warning = ColorTranslator.FromHtml("#FFC65B");      // starting, waiting (gold)
    public static readonly Color Danger = ColorTranslator.FromHtml("#FF7968");       // errors, and what cannot be undone (coral)

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
        Bold = Make("Segoe UI", 13.5f, FontStyle.Bold);
        Heading = MakeHeavy(16);
        Title = MakeHeavy(21);
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

    /// <summary>The game style's shapes at this scale: the outline of a card, the outline of
    /// everything inside one, the lips under cards and buttons, and how round the corners of
    /// a card and of everything inside one are.</summary>
    public int CardOutline => Math.Max(2, Px(3));
    public int Outline => Math.Max(1, Px(2));
    public int CardLip => Px(6);
    public int ButtonLip => Px(3);
    public int CardCorner => Px(26);
    public int Corner => Px(10);

    /// <summary>The height of a button whose face is <paramref name="face"/> logical pixels
    /// tall: the face plus the lip under it.</summary>
    public int ButtonHeight(float face) => Px(face) + ButtonLip;

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

    /// <summary>A filled rounded rectangle with an optional outline. The outline is drawn
    /// inside the bounds, whatever its width.</summary>
    public static void FillRounded(Graphics g, Rectangle bounds, float radius, Color fill, Color? outline = null, float outlineWidth = 1)
    {
        using var brush = new SolidBrush(fill);
        FillRounded(g, bounds, radius, brush, outline, outlineWidth);
    }

    public static void FillRounded(Graphics g, Rectangle bounds, float radius, Brush fill, Color? outline = null, float outlineWidth = 1)
    {
        var previous = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        // Half the outline in from the edge: all of it falls inside the bounds, and a
        // one-pixel outline lands on whole pixels and stays sharp.
        float inset = outline is null ? 0.5f : outlineWidth / 2f;
        var r = new RectangleF(bounds.X + inset, bounds.Y + inset, bounds.Width - 2 * inset, bounds.Height - 2 * inset);
        using var path = RoundedRectangle(r, Math.Max(0, radius - inset));
        g.FillPath(fill, path);
        if (outline is { } color)
        {
            using var pen = new Pen(color, outlineWidth);
            g.DrawPath(pen, path);
        }
        g.SmoothingMode = previous;
    }

    /// <summary>A face standing on a lip: the game style's card or button. The lip is the part
    /// of <paramref name="bounds"/> below the face. <paramref name="sunk"/> moves the face
    /// down into the lip, for a button that is being pressed.</summary>
    public static void FillRaised(Graphics g, Rectangle bounds, float radius, Brush face, float outlineWidth, int lip, int sunk = 0, Color? lipColor = null)
    {
        var whole = new Rectangle(bounds.X, bounds.Y + sunk, bounds.Width, bounds.Height - sunk);
        var top = new Rectangle(whole.X, whole.Y, whole.Width, bounds.Height - lip);
        // The lip is drawn only from where the face's bottom corners begin. Higher up the face
        // hides it anyway, and it would give the face a thin fringe in the lip's colour.
        int from = top.Bottom - (int)Math.Ceiling(radius);
        var state = g.Save();
        g.SetClip(new Rectangle(whole.X, from, whole.Width, whole.Bottom - from), CombineMode.Intersect);
        FillRounded(g, whole, radius, lipColor ?? Palette.Border);
        g.Restore(state);
        FillRounded(g, top, radius, face, Palette.Border, outlineWidth);
    }

    public static void FillRaised(Graphics g, Rectangle bounds, float radius, Color face, float outlineWidth, int lip, int sunk = 0, Color? lipColor = null)
    {
        using var brush = new SolidBrush(face);
        FillRaised(g, bounds, radius, brush, outlineWidth, lip, sunk, lipColor);
    }

    /// <summary>A brush that changes colour from the top of <paramref name="area"/> to its
    /// bottom, through the given colours at the given places (0 is the top, 1 the bottom).
    /// It is tied to the area, not to what is filled with it: filling a part of the area
    /// gives that part of the picture.</summary>
    public static LinearGradientBrush TopToBottom(Rectangle area, Color[] colors, float[] places)
    {
        // Outside its rectangle a gradient brush starts over, and rounding can put the very
        // last row there. One spare row above and below keeps that row from getting the
        // colour of the other end.
        var span = new Rectangle(area.X, area.Y - 1, Math.Max(1, area.Width), Math.Max(1, area.Height) + 2);
        return new LinearGradientBrush(span, colors[0], colors[^1], LinearGradientMode.Vertical)
        {
            InterpolationColors = new ColorBlend { Colors = colors, Positions = places },
        };
    }

    /// <summary>The face of a card or a dialog, lit from above: lagoon at the top, darker
    /// towards the bottom.</summary>
    public static LinearGradientBrush FaceBrush(Rectangle face) =>
        TopToBottom(face, [Palette.Raised, Palette.Surface, Palette.Inset], [0f, 0.46f, 1f]);

    /// <summary>A text box: a dark face set into its card, with a shadow under its top edge.
    /// The outline says more than the shape: aqua while typing, coral when the text is wrong.</summary>
    public void FillField(Graphics g, Rectangle bounds, Color outline)
    {
        int edge = Outline, shadow = Px(2);
        FillRounded(g, bounds, Corner, Palette.Inset, outline, edge);
        var face = new Rectangle(bounds.X + edge, bounds.Y + edge + shadow, bounds.Width - 2 * edge, bounds.Height - 2 * edge - shadow);
        FillRounded(g, face, Corner - edge, Palette.Surface);
    }

    public static void FillCircle(Graphics g, Rectangle bounds, Color fill)
    {
        var previous = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(fill);
        g.FillEllipse(brush, bounds);
        g.SmoothingMode = previous;
    }

    /// <summary>A ring: a circle that is not filled.</summary>
    public static void DrawCircle(Graphics g, Rectangle bounds, Color color)
    {
        var previous = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float width = Math.Max(1f, bounds.Width / 7f);
        using var pen = new Pen(color, width);
        g.DrawEllipse(pen, bounds.X + width / 2, bounds.Y + width / 2, bounds.Width - width, bounds.Height - width);
        g.SmoothingMode = previous;
    }

    public void Dispose()
    {
        foreach (var font in new[] { Small, Body, Bold, Heading, Title, Code, Mono }) font.Dispose();
    }

    private Font Make(string family, float logicalPixels, FontStyle style = FontStyle.Regular) => new(family, logicalPixels * Scale, style, GraphicsUnit.Pixel);

    /// <summary>The heaviest Segoe UI for headings, as in the browser client. Where Windows
    /// does not have it, plain bold.</summary>
    private Font MakeHeavy(float logicalPixels)
    {
        var heavy = Make("Segoe UI Black", logicalPixels);
        if (heavy.Name == "Segoe UI Black") return heavy;
        heavy.Dispose();
        return Make("Segoe UI", logicalPixels, FontStyle.Bold);
    }
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

    /// <summary>Gives the title bar the colour of the header strip under it, and the thin frame
    /// around the window the colour of every other outline (Windows 11; older versions ignore
    /// both and keep their dark title bar).</summary>
    public static void UseFrameColors(IntPtr window, Color titleBar, Color frame)
    {
        int titleBarRef = ColorRef(titleBar), frameRef = ColorRef(frame);
        DwmSetWindowAttribute(window, CaptionColor, ref titleBarRef, sizeof(int));
        DwmSetWindowAttribute(window, BorderColor, ref frameRef, sizeof(int));
    }

    private static int ColorRef(Color color) => color.R | (color.G << 8) | (color.B << 16); // Windows' COLORREF: 0x00BBGGRR

    /// <summary>Gives a scrollable control Windows' dark scroll bars.</summary>
    public static void UseDarkScrollBars(IntPtr control) => SetWindowTheme(control, "DarkMode_Explorer", null);

    public static void BringToFront(IntPtr window) => SetForegroundWindow(window);

    /// <summary>Scrolls a text box of several lines to its last line. Windows Forms' own
    /// "scroll to the caret" does nothing while the window is hidden, and the window is in the
    /// tray most of the time.</summary>
    public static void ScrollToEnd(IntPtr textBox) => SendMessageW(textBox, VerticalScroll, ToTheBottom, 0);

    /// <summary>The scale (1 = 100%) of the monitor that shows a point of the desktop. Lets the
    /// window pick the right size for a monitor before it has been shown there.</summary>
    public static float ScaleAt(Point desktopPoint)
    {
        IntPtr monitor = MonitorFromPoint(desktopPoint, NearestMonitor);
        return GetDpiForMonitor(monitor, EffectiveDpi, out uint dpi, out _) == 0 ? dpi / 96f : 1f;
    }

    private const int UseImmersiveDarkMode = 20; // DWMWA_USE_IMMERSIVE_DARK_MODE
    private const int BorderColor = 34;          // DWMWA_BORDER_COLOR
    private const int CaptionColor = 35;         // DWMWA_CAPTION_COLOR
    private const uint VerticalScroll = 0x0115;  // WM_VSCROLL
    private const nint ToTheBottom = 7;          // SB_BOTTOM
    private const uint NearestMonitor = 2;       // MONITOR_DEFAULTTONEAREST
    private const int EffectiveDpi = 0;          // MDT_EFFECTIVE_DPI

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] private static extern int SetWindowTheme(IntPtr window, string? application, string? idList);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern nint SendMessageW(IntPtr window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(Point point, uint flags);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int kind, out uint dpiX, out uint dpiY);
}
