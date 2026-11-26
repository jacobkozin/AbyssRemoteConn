using System.Drawing.Drawing2D;

namespace AbyssRemote.Host.App;

/// <summary>Something that controls lie on: the page under the cards, a card under its
/// buttons. A rounded control cannot be see-through, so before it draws itself it has what it
/// lies on paint the part behind it. That is what then shows outside its round corners.</summary>
internal interface IBackdrop
{
    /// <summary>Paints, into the drawing of <paramref name="child"/>, what lies behind
    /// <paramref name="area"/> of it (in the child's own coordinates).</summary>
    void PaintBehind(Graphics g, Control child, Rectangle area);
}

internal static class Backdrop
{
    /// <summary>Paints what is behind a part of <paramref name="control"/>: the backdrop it
    /// lies on, or the plain page colour where it lies on something else.</summary>
    public static void Paint(Graphics g, Control control, Rectangle area)
    {
        if (control.Parent is IBackdrop parent)
        {
            parent.PaintBehind(g, control, area);
            return;
        }
        using var page = new SolidBrush(Palette.Background);
        g.FillRectangle(page, area);
    }
}

internal enum ButtonKind { Primary, Secondary, Danger }

/// <summary>A button in the game style: a face with one outline, standing on a lip. Pressing
/// sinks the face into the lip. Lagoon is the ordinary button, teal the main one, coral what
/// cannot be undone. It is a real <see cref="Button"/> underneath, so the keyboard (Tab,
/// Space, Enter) and screen readers work as usual; only the drawing is ours.</summary>
internal sealed class RaisedButton : Button
{
    private bool _hover, _pressed;

    public RaisedButton(string text, ButtonKind kind = ButtonKind.Secondary)
    {
        Text = text;
        Kind = kind;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        Cursor = Cursors.Hand;
    }

    public ButtonKind Kind { get; set; }

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
        Backdrop.Paint(g, this, ClientRectangle); // shows outside the round corners, and above a pressed face
        var bounds = ClientRectangle;
        float radius = 10 * UiScale;
        int lip = Math.Max(2, (int)Math.Round(3 * UiScale)), outline = Math.Max(1, (int)Math.Round(2 * UiScale));
        int sunk = _pressed && Enabled ? Math.Min(lip - 1, (int)Math.Round(2 * UiScale)) : 0;
        Color face, text;
        if (!Enabled)
        {
            face = Palette.Tint(Palette.Raised, 45);
            text = Palette.Tint(Palette.Muted, 60, face);
        }
        else
        {
            (face, text) = Kind switch
            {
                ButtonKind.Primary => (Palette.Teal, Palette.OnAccent),
                ButtonKind.Danger => (Palette.Danger, Palette.OnAccent),
                _ => (Palette.Raised, Palette.Text),
            };
            if (_hover && !_pressed) face = Palette.Tint(Color.White, 10, face); // a little lighter under the mouse
        }
        Ui.FillRaised(g, bounds, radius, face, outline, lip, sunk);
        var label = new Rectangle(bounds.X, bounds.Y + sunk, bounds.Width, bounds.Height - lip);
        TextRenderer.DrawText(g, Text, Font, label, text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        if (Focused && ShowFocusCues)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var ring = new Pen(Kind == ButtonKind.Secondary ? Palette.Accent : Palette.OnAccent, Math.Max(1, UiScale));
            float gap = outline + 1.5f;
            using var path = Ui.RoundedRectangle(new RectangleF(label.X + gap, label.Y + gap, label.Width - 2 * gap, label.Height - 2 * gap), Math.Max(0, radius - gap));
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
        Backdrop.Paint(g, this, ClientRectangle);
        int lineHeight = Ui.MeasureLine("Ag", Font).Height;
        var box = new Rectangle(0, Math.Max(0, (lineHeight - BoxSize) / 2) + 1, BoxSize, BoxSize);
        float radius = BoxSize * 0.28f;
        if (Checked)
        {
            Ui.FillRounded(g, box, radius, Enabled ? Palette.Teal : Palette.Muted, Palette.Border, Math.Max(1f, BoxSize / 9f));
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
            // An empty box is a small button at rest. Darker, it would vanish at the dark bottom of a card.
            Ui.FillRounded(g, box, radius, _hover ? Palette.RaisedHover : Palette.Raised, Palette.Border, Math.Max(1f, BoxSize / 9f));
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
/// while away access is off. <paramref name="Away"/> says whether that address is being
/// forwarded to this program right now.</summary>
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
    string AwayHost = "",
    AwayVerdict Away = AwayVerdict.Unknown);

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

/// <summary>A panel with a title: the building block of the window. It is a face with one
/// outline, standing on a lip, and the face is lit from above. A card draws its own text and
/// hosts real controls for anything that can be clicked or typed into.</summary>
internal abstract class Card : Control, IBackdrop
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

    /// <summary>The colour of the lip under the card; a problem card's is coral.</summary>
    protected virtual Color Lip => Palette.Border;
    protected Ui Ui { get; private set; } = null!;
    protected int Pad => Ui.Px(18);

    /// <summary>Called when the window first appears and whenever it moves to a monitor with
    /// another scale: new fonts for the card and for every control on it.</summary>
    public void UseScale(Ui ui)
    {
        Ui = ui;
        foreach (Control child in Controls)
        {
            child.Font = child is RaisedButton ? ui.Bold : ui.Body;
            if (child is RaisedButton button) button.UiScale = ui.Scale;
        }
        OnScaleChanged();
    }

    protected virtual void OnScaleChanged() { }

    /// <summary>Lays the card out for <paramref name="width"/> and returns the height it needs:
    /// its content, plus the lip under it.</summary>
    public int Arrange(int width) => Compose(new Pass(null, Ui), width) + Ui.CardLip;

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

    /// <summary>The card without the lip under it.</summary>
    private Rectangle FaceArea => new(0, 0, Width, Height - Ui.CardLip);

    /// <summary>For the buttons and boxes on the card: the part of its face behind them.</summary>
    public void PaintBehind(Graphics g, Control child, Rectangle area)
    {
        // The face as the child sees it: the same picture, starting where the card starts for the child.
        var face = FaceArea;
        face.Offset(-child.Left, -child.Top);
        using var brush = Ui.FaceBrush(face);
        g.FillRectangle(brush, area);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        // What is behind the card shows only at its corners: in the strip with the top
        // corners, and in the strip with the bottom corners and the lip.
        int corner = Ui.CardCorner, foot = corner + Ui.CardLip;
        Backdrop.Paint(g, this, new Rectangle(0, 0, Width, corner));
        Backdrop.Paint(g, this, new Rectangle(0, Height - foot, Width, foot));
        using (var face = Ui.FaceBrush(FaceArea))
            Ui.FillRaised(g, ClientRectangle, corner, face, Ui.CardOutline, Ui.CardLip, lipColor: Lip);
        Compose(new Pass(g, Ui), Width);
    }
}
