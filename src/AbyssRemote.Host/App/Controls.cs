using System.Drawing.Drawing2D;

namespace AbyssRemote.Host.App;

internal enum ButtonKind { Primary, Secondary, Danger }

/// <summary>A rounded button in the window's colours. It is a real <see cref="Button"/>
/// underneath, so the keyboard (Tab, Space, Enter) and screen readers work as usual; only the
/// drawing is ours.</summary>
internal sealed class FlatButton : Button
{
    private bool _hover, _pressed;

    public FlatButton(string text, ButtonKind kind = ButtonKind.Secondary)
    {
        Text = text;
        Kind = kind;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        Cursor = Cursors.Hand;
    }

    public ButtonKind Kind { get; set; }

    /// <summary>The colour behind the button, visible outside its rounded corners.</summary>
    public Color Backdrop { get; set; } = Palette.Surface;

    /// <summary>The scale of the monitor the button is on (1 = 100%); see <see cref="Ui"/>.</summary>
    public float UiScale { get; set; } = 1;

    /// <summary>The width that fits the label with comfortable padding.</summary>
    public int WidthFor(Ui ui) => Ui.MeasureLine(Text, ui.Bold).Width + ui.Px(28);

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = _pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _pressed = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Backdrop);
        var bounds = ClientRectangle;
        float radius = 8 * UiScale;
        Color text;
        if (!Enabled)
        {
            Ui.FillRounded(g, bounds, radius, Palette.Raised, Palette.Border);
            text = Palette.Tint(Palette.Muted, 60, Palette.Raised);
        }
        else if (Kind == ButtonKind.Primary)
        {
            // The blue-to-cyan gradient of the app icon; a little lighter under the mouse.
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = Ui.RoundedRectangle(new RectangleF(0, 0, bounds.Width, bounds.Height), radius);
            Color from = _pressed ? Palette.AccentBlue : _hover ? Palette.Tint(Color.White, 12, Palette.AccentBlue) : Palette.AccentBlue;
            Color to = _pressed ? Palette.AccentBlue : _hover ? Palette.Tint(Color.White, 12, Palette.Accent) : Palette.Accent;
            using var brush = new LinearGradientBrush(new Rectangle(0, 0, Math.Max(1, bounds.Width), Math.Max(1, bounds.Height)), from, to, 0f);
            g.FillPath(brush, path);
            text = Palette.OnAccent;
        }
        else
        {
            Color ink = Kind == ButtonKind.Danger ? Palette.Danger : Palette.Text;
            Color fill = Kind == ButtonKind.Danger
                ? Palette.Tint(Palette.Danger, _pressed ? 28 : _hover ? 20 : 10, Backdrop)
                : _pressed ? Palette.Border : _hover ? Palette.RaisedHover : Palette.Raised;
            Color outline = Kind == ButtonKind.Danger ? Palette.Tint(Palette.Danger, 45, Backdrop) : Palette.Border;
            Ui.FillRounded(g, bounds, radius, fill, outline);
            text = ink;
        }
        TextRenderer.DrawText(g, Text, Font, bounds, text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        if (Focused && ShowFocusCues)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var ring = new Pen(Kind == ButtonKind.Primary ? Palette.Text : Palette.Accent, Math.Max(1, UiScale));
            using var path = Ui.RoundedRectangle(new RectangleF(1.5f, 1.5f, bounds.Width - 3, bounds.Height - 3), Math.Max(0, radius - 1));
            g.DrawPath(ring, path);
        }
    }
}

/// <summary>A check box with a wrapped label, drawn in the window's colours. It never ticks
/// itself: a click asks the app, and the box shows whatever the app then reports. That way a
/// change the user cancels (or one that fails) never leaves a wrong tick behind.</summary>
internal sealed class CheckRow : CheckBox
{
    private bool _hover;

    public CheckRow(string text)
    {
        Text = text;
        AutoCheck = false;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Cursor = Cursors.Hand;
    }

    public Color Backdrop { get; set; } = Palette.Surface;

    private int BoxSize => (int)Math.Round(Font.Size * 1.35f);
    private int Gap => (int)Math.Round(Font.Size * 0.75f);

    /// <summary>The height the label needs when the control is <paramref name="width"/> wide.</summary>
    public int HeightFor(int width) =>
        Math.Max(BoxSize, Ui.MeasureWrapped(Text, Font, width - BoxSize - Gap)) + 2;

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnCheckedChanged(EventArgs e) { Invalidate(); base.OnCheckedChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Backdrop);
        int lineHeight = Ui.MeasureLine("Ag", Font).Height;
        var box = new Rectangle(0, Math.Max(0, (lineHeight - BoxSize) / 2) + 1, BoxSize, BoxSize);
        float radius = BoxSize * 0.28f;
        if (Checked)
        {
            Ui.FillRounded(g, box, radius, Enabled ? Palette.Accent : Palette.Muted);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var pen = new Pen(Palette.OnAccent, Math.Max(1.6f, BoxSize / 9f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            g.DrawLines(pen,
            [
                new PointF(box.X + BoxSize * 0.24f, box.Y + BoxSize * 0.52f),
                new PointF(box.X + BoxSize * 0.43f, box.Y + BoxSize * 0.70f),
                new PointF(box.X + BoxSize * 0.76f, box.Y + BoxSize * 0.32f),
            ]);
        }
        else
        {
            Ui.FillRounded(g, box, radius, _hover ? Palette.RaisedHover : Palette.Background, _hover ? Palette.Muted : Palette.Border);
        }
        var label = new Rectangle(BoxSize + Gap, 0, Width - BoxSize - Gap, Height);
        TextRenderer.DrawText(g, Text, Font, label, Enabled ? Palette.Text : Palette.Muted, Ui.Wrapped);
        if (Focused && ShowFocusCues)
        {
            using var ring = new Pen(Palette.Accent);
            var outline = Rectangle.Inflate(box, 2, 2);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = Ui.RoundedRectangle(outline, radius + 2);
            g.DrawPath(ring, path);
        }
    }
}

/// <summary>What the user can ask for in the window. The cards only call these; WindowApp
/// carries them out and then shows the new state.</summary>
internal interface IWindowActions
{
    void NewPairingCode();
    void CopyAddress();
    void RevokeDevice(PairedDevice device);
    void RevokeAllDevices();
    void TryAgain();
    void SaveCertificate();
    void AddFirewallRule();
    void OpenNetworkSettings();
    void CheckSetupAgain();
    void SetHomeNetwork(bool on);
    void SetPort(int port);
    void SetAway(bool on);
    void SetAwayHost(string host);
    void SetStartWithWindows(bool on);
    void SetCloseToTray(bool on);
}

/// <summary>Everything the window shows, as plain data. WindowApp builds one from the runtime
/// and the settings whenever something changes; the snapshot tool builds them from samples.
/// <paramref name="AwayHost"/> is the saved away address, which the settings card shows also
/// while away access is off.</summary>
internal sealed record WindowState(
    HostStatus Status,
    PairingCode Code,
    IReadOnlyList<PairedDevice> Devices,
    IReadOnlySet<string> ConnectedDeviceIds,
    IReadOnlyList<NoticeLine> Notices,
    PhoneSetup Setup,
    bool CloseToTray,
    bool StartWithWindows,
    DateTimeOffset Now,
    string AwayHost = "");

/// <summary>One walk over a card's content. The same code runs twice: once without a
/// <see cref="Graphics"/> to measure the card and put its buttons in place, and once with one
/// to draw. Because both passes share every line, what is measured is exactly what is drawn.</summary>
internal readonly struct Pass(Graphics? graphics, Ui ui)
{
    public Graphics? Graphics => graphics;
    public Ui Ui => ui;
    public bool Painting => graphics is not null;

    /// <summary>Wrapped text. Returns its height.</summary>
    public int Text(string text, Font font, Color color, int x, int y, int width)
    {
        int height = Ui.MeasureWrapped(text, font, width);
        if (graphics is not null && height > 0)
            TextRenderer.DrawText(graphics, text, font, new Rectangle(x, y, Math.Max(1, width), height), color, Ui.Wrapped);
        return height;
    }

    /// <summary>One line, cut off with "…" when it does not fit. Returns its height.</summary>
    public int Line(string text, Font font, Color color, int x, int y, int width)
    {
        int height = Ui.MeasureLine("Ag", font).Height;
        if (graphics is not null)
            TextRenderer.DrawText(graphics, text, font, new Rectangle(x, y, Math.Max(1, width), height), color, Ui.OneLine);
        return height;
    }

    /// <summary>Puts a child control in place (measuring pass only; drawing never moves anything).</summary>
    public void Place(Control control, int x, int y, int width, int height)
    {
        if (graphics is null) control.SetBounds(x, y, width, height);
    }
}

/// <summary>A rounded panel with a title: the building block of the window. A card draws its
/// own text and hosts real controls for anything that can be clicked or typed into.</summary>
internal abstract class Card : Control
{
    protected Card(string title, IWindowActions actions)
    {
        Title = title;
        Actions = actions;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Palette.Background;
    }

    protected string Title { get; }
    protected IWindowActions Actions { get; }

    /// <summary>The colour of the card's outline; a problem card uses red.</summary>
    protected virtual Color Outline => Palette.Border;
    protected Ui Ui { get; private set; } = null!;
    protected int Pad => Ui.Px(18);

    /// <summary>Called when the window first appears and whenever it moves to a monitor with
    /// another scale: new fonts for the card and for every control on it.</summary>
    public void UseScale(Ui ui)
    {
        Ui = ui;
        foreach (Control child in Controls)
        {
            child.Font = child is FlatButton ? ui.Bold : ui.Body;
            if (child is FlatButton button) button.UiScale = ui.Scale;
        }
        OnScaleChanged();
    }

    protected virtual void OnScaleChanged() { }

    /// <summary>Lays the card out for <paramref name="width"/> and returns the height it needs.</summary>
    public int Arrange(int width) => Compose(new Pass(null, Ui), width);

    /// <summary>Adds a button (or another control) to the card.</summary>
    protected T Add<T>(T control) where T : Control
    {
        Controls.Add(control);
        return control;
    }

    /// <summary>The card's content, top to bottom. Returns the total height.</summary>
    protected abstract int Compose(Pass pass, int width);

    /// <summary>Draws the title and returns the y coordinate where the content starts.</summary>
    protected int Header(Pass pass, int width, int reservedRight = 0)
    {
        int height = pass.Line(Title, Ui.Heading, Palette.Text, Pad, Pad, width - 2 * Pad - reservedRight);
        return Pad + height + Ui.Px(12);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Palette.Background);
        Ui.FillRounded(e.Graphics, ClientRectangle, Ui.Px(12), Palette.Surface, Outline);
        Compose(new Pass(e.Graphics, Ui), Width);
    }
}
