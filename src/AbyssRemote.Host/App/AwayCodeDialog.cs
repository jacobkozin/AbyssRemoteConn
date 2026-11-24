namespace AbyssRemote.Host.App;

/// <summary>The away code, shown when the user asks for it: the address to open, a QR code that
/// carries the address and the code, and the code to type. A device that uses it is added and
/// works at home and away. The code is on screen only while this box is open, and closing the
/// box takes back a code nobody used (see WindowApp.ShowAwayCode).</summary>
internal sealed class AwayCodeDialog : Form
{
    private readonly HostRuntime _runtime;
    private readonly AwayCodeView _view;
    private readonly System.Windows.Forms.Timer _clock = new() { Interval = 500 };
    private DateTimeOffset _shownSince;
    private int _goneTicks;

    /// <summary>Use <see cref="Show(Form, HostRuntime, AwayCodeOffer)"/>. A test makes the box
    /// without showing it.</summary>
    internal AwayCodeDialog(HostRuntime runtime, AwayCodeOffer offer, float scale)
    {
        _runtime = runtime;
        Text = AppInfo.Name;
        Icon = AppIcon.Load();
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Palette.Background;
        StartPosition = FormStartPosition.CenterParent;
        _view = new AwayCodeView(scale);
        Controls.Add(_view);
        Show(offer);
        AcceptButton = CancelButton = _view.Done;
        _view.Done.DialogResult = DialogResult.OK;
        _view.NewCode.Click += (_, _) => { if (_runtime.NewAwayCode() is { } next) Show(next); };
        _clock.Tick += (_, _) => Watch();
        _clock.Start();
    }

    internal AwayCodeView View => _view;

    /// <summary>Shows the code until the user closes the box. It appears over the window, or
    /// in the middle of the screen while the window is hidden in the tray.</summary>
    public static void Show(Form owner, HostRuntime runtime, AwayCodeOffer offer)
    {
        using var dialog = new AwayCodeDialog(runtime, offer, owner.DeviceDpi / 96f);
        if (owner.Visible) dialog.ShowDialog(owner);
        else
        {
            dialog.StartPosition = FormStartPosition.CenterScreen;
            dialog.ShowDialog();
        }
    }

    private void Show(AwayCodeOffer offer)
    {
        // A device that appears from now on used a code of this box. Two seconds of slack,
        // because the device's time is the server's and this one is the window's.
        _shownSince = DateTimeOffset.UtcNow.AddSeconds(-2);
        _goneTicks = 0;
        _view.Show(offer, DateTimeOffset.UtcNow);
        ClientSize = _view.Size;
    }

    /// <summary>Twice a second: the countdown, and what became of the code.</summary>
    private void Watch()
    {
        var now = DateTimeOffset.UtcNow;
        if (_view.Finished) return;
        if (_runtime.AwayCodeOpen)
        {
            _view.Tick(now);
            return;
        }
        // The code is gone: a device used it, it ran out, or wrong guesses destroyed it. A
        // device that used it shows up in the list a moment later, so wait for it briefly.
        var added = _runtime.Devices.Where(d => d.Away && d.CreatedUtc >= _shownSince).OrderByDescending(d => d.CreatedUtc).FirstOrDefault();
        if (added is null && ++_goneTicks < 4) return;
        _view.ShowOutcome(added?.Name);
        ClientSize = _view.Size;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        WindowNative.UseDarkTitleBar(Handle);
        WindowNative.UseFrameColors(Handle, Palette.Band, Palette.Border);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _clock.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>The content of an <see cref="AwayCodeDialog"/>. It shows one of three things: the
/// code with what to do with it, the device that was added with it, or that the code no longer
/// works. It sizes itself to its text.</summary>
internal sealed class AwayCodeView : Control, IBackdrop
{
    private const string AnIcon = "On an iPhone or iPad with a Home Screen icon: add the address to the Home Screen first, then type the code in the icon.";
    private readonly Ui _ui;
    private AwayCodeOffer? _offer;
    private DateTimeOffset _now;
    private string? _added;
    private Bitmap? _qr;

    public AwayCodeView(float scale)
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        _ui = new Ui(scale);
        NewCode = new RaisedButton("New code") { Font = _ui.Bold, UiScale = scale };
        Done = new RaisedButton("Done", ButtonKind.Primary) { Font = _ui.Bold, UiScale = scale };
        Controls.Add(NewCode);
        Controls.Add(Done);
    }

    public RaisedButton NewCode { get; }
    public RaisedButton Done { get; }

    /// <summary>True once the box shows an outcome instead of a code.</summary>
    public bool Finished => _offer is null;

    /// <summary>Shows a code and how to use it.</summary>
    public void Show(AwayCodeOffer offer, DateTimeOffset now)
    {
        (_offer, _now, _added) = (offer, now, null);
        _qr?.Dispose();
        _qr = null;
        NewCode.Text = "New code";
        Arrange();
    }

    /// <summary>The clock moved on: draw again when the countdown shows another second.</summary>
    public void Tick(DateTimeOffset now)
    {
        string before = Remaining;
        _now = now;
        if (Remaining != before) Invalidate();
    }

    /// <summary>The code is gone. <paramref name="addedDevice"/> is the device that used it, or
    /// null when nobody did.</summary>
    public void ShowOutcome(string? addedDevice)
    {
        (_offer, _added) = (null, addedDevice);
        NewCode.Text = addedDevice is null ? "New code" : "Add another device";
        Arrange();
    }

    private string Remaining
    {
        get
        {
            var left = _offer is null ? TimeSpan.Zero : _offer.ExpiresUtc - _now;
            if (left < TimeSpan.Zero) left = TimeSpan.Zero;
            return $"Valid for {(int)left.TotalMinutes}:{left.Seconds:00}. It works once, and closing this box cancels it.";
        }
    }

    private void Arrange()
    {
        int width = _ui.Px(560);
        Size = new Size(width, Compose(new Pass(null, _ui), width));
        Invalidate();
    }

    /// <summary>The content, top to bottom, measured or drawn (see <see cref="Pass"/>).</summary>
    private int Compose(Pass pass, int width)
    {
        int pad = _ui.Px(24), inner = width - 2 * pad, y = pad;
        if (_offer is { } offer)
        {
            y += pass.Text("Add a device with an away code", _ui.Heading, Palette.Text, pad, y, inner) + _ui.Px(6);
            y += pass.Text("The device then works at home and away from home.", _ui.Body, Palette.Muted, pad, y, inner) + _ui.Px(18);

            // Step 1 with the QR code beside it; step 2 below, across the whole box.
            int side = _ui.Px(164), badge = _ui.Px(24), textX = pad + badge + _ui.Px(12), column = inner - side - _ui.Px(20) - (textX - pad);
            int top = y;
            if (pass.Graphics is { } g)
            {
                DrawBadge(g, "1", pad, y);
                DrawQr(g, offer.Url, width - pad - side, top, side);
            }
            y += pass.Text("On the phone, tablet or computer, open", _ui.Body, Palette.Text, textX, y, column) + _ui.Px(2);
            y += pass.Text(offer.Address, _ui.Bold, Palette.Accent, textX, y, column) + _ui.Px(6);
            y += pass.Text("Or scan the square with its camera: that opens the address with the code filled in.", _ui.Small, Palette.Muted, textX, y, column);
            y = Math.Max(y, top + side) + _ui.Px(18);

            if (pass.Graphics is { } g2) DrawBadge(g2, "2", pad, y);
            y += pass.Text("Type this away code there and choose “Add this device”:", _ui.Body, Palette.Text, textX, y, inner - (textX - pad)) + _ui.Px(2);
            y += pass.Line(offer.Code, _ui.Code, Palette.Text, textX, y, inner - (textX - pad));
            y += pass.Text(Remaining, _ui.Small, Palette.Muted, textX, y, inner - (textX - pad)) + _ui.Px(16);
            y += pass.Text(AnIcon, _ui.Small, Palette.Muted, pad, y, inner);
        }
        else if (_added is { } name)
        {
            y += pass.Text($"“{name}” was added", _ui.Heading, Palette.Success, pad, y, inner) + _ui.Px(8);
            y += pass.Text("It works at home and away from home. You can revoke it under Devices at any time.", _ui.Body, Palette.Muted, pad, y, inner);
        }
        else
        {
            y += pass.Text("That code no longer works", _ui.Heading, Palette.Text, pad, y, inner) + _ui.Px(8);
            y += pass.Text("It ran out, or too many wrong codes were typed. Make a new one when you are ready.", _ui.Body, Palette.Muted, pad, y, inner);
        }

        // The two buttons are one width, like the buttons of any set.
        y += _ui.Px(22);
        int buttonHeight = _ui.ButtonHeight(34), buttonWidth = Math.Max(Math.Max(NewCode.WidthFor(_ui), Done.WidthFor(_ui)), _ui.Px(96));
        pass.Place(Done, width - pad - buttonWidth, y, buttonWidth, buttonHeight);
        pass.Place(NewCode, width - pad - 2 * buttonWidth - _ui.Px(10), y, buttonWidth, buttonHeight);
        return y + buttonHeight + pad;
    }

    /// <summary>The number of a step, in a badge like the steps of the browser client.</summary>
    private void DrawBadge(Graphics g, string number, int x, int y)
    {
        int size = _ui.Px(24);
        var circle = new Rectangle(x, y - _ui.Px(2), size, size);
        Ui.FillRounded(g, circle, size / 2f, Palette.Accent, Palette.Border, _ui.Outline);
        TextRenderer.DrawText(g, number, _ui.Bold, circle, Palette.OnAccent,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
    }

    /// <summary>The QR code on a white tile. It holds the away address with the code.</summary>
    private void DrawQr(Graphics g, string url, int x, int y, int side)
    {
        if (_qr is null)
        {
            var code = QrCode.Encode(url);
            _qr = QrRenderer.ToBitmap(code, QrRenderer.PixelsPerModule(code, side - _ui.Px(8)), Palette.Border, Color.White);
        }
        Ui.FillRounded(g, new Rectangle(x, y, side, side), _ui.Corner, Color.White, Palette.Border, _ui.Outline);
        g.DrawImageUnscaled(_qr, x + (side - _qr.Width) / 2, y + (side - _qr.Height) / 2);
    }

    public void PaintBehind(Graphics g, Control child, Rectangle area)
    {
        var face = ClientRectangle;
        face.Offset(-child.Left, -child.Top);
        using var brush = Ui.FaceBrush(face);
        g.FillRectangle(brush, area);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        using (var face = Ui.FaceBrush(ClientRectangle)) e.Graphics.FillRectangle(face, ClientRectangle);
        Compose(new Pass(e.Graphics, _ui), Width);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _qr?.Dispose();
            _ui.Dispose();
        }
        base.Dispose(disposing);
    }
}
