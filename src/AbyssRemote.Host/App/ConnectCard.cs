namespace AbyssRemote.Host.App;

/// <summary>The top strip of the window: icon, name, version and the status pill.</summary>
internal sealed class HeaderBar : Control
{
    private Ui _ui = null!;
    private Icon? _icon;
    private HostStatus? _status;

    public HeaderBar()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    public void UseScale(Ui ui)
    {
        _ui = ui;
        _icon?.Dispose();
        _icon = AppIcon.Load(ui.Px(36));
        Invalidate();
    }

    public void Show(HostStatus status)
    {
        if (Equals(status, _status)) return;
        _status = status;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        // One solid band with a dark edge, like the header of the browser client.
        g.Clear(Palette.Band);
        using (var edge = new SolidBrush(Palette.Border)) g.FillRectangle(edge, 0, Height - _ui.Outline, Width, _ui.Outline);
        int pad = _ui.Px(20), iconSize = _ui.Px(36);
        if (_icon is not null)
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.DrawIcon(_icon, new Rectangle(pad, (Height - iconSize) / 2, iconSize, iconSize));
        }

        int x = pad + iconSize + _ui.Px(12);
        var title = Ui.MeasureLine(AppInfo.Name, _ui.Title);
        int titleY = (Height - title.Height) / 2;
        TextRenderer.DrawText(g, AppInfo.Name, _ui.Title, new Point(x, titleY), Palette.Text, TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
        // The version sits on the title's baseline, a little to the right.
        var version = Ui.MeasureLine(AppInfo.Version, _ui.Small);
        int versionX = x + title.Width + _ui.Px(10), versionY = titleY + title.Height - version.Height - _ui.Px(3);
        TextRenderer.DrawText(g, AppInfo.Version, _ui.Small, new Point(versionX, versionY), Palette.Muted, TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);

        if (_status is null) return;
        Color color = _status.Phase switch
        {
            HostPhase.Running => Palette.Success,
            HostPhase.Starting => Palette.Warning,
            HostPhase.Failed => Palette.Danger,
            _ => Palette.Muted,
        };
        // The pill: a dot and the status in words. A long failure reason is cut off with "…";
        // the full sentence is in the card below the header.
        int dot = _ui.Px(8), inner = _ui.Px(14), gap = _ui.Px(8), height = _ui.Px(30);
        int room = Width - pad - (versionX + version.Width + _ui.Px(24));
        int textWidth = Math.Min(Ui.MeasureLine(_status.Summary, _ui.Bold).Width, room - 2 * inner - dot - gap);
        if (textWidth < _ui.Px(40)) return;
        var pill = new Rectangle(Width - pad - (2 * inner + dot + gap + textWidth), (Height - height) / 2, 2 * inner + dot + gap + textWidth, height);
        Ui.FillRounded(g, pill, height / 2f, Palette.Surface, Palette.Border, _ui.Outline);
        Ui.FillCircle(g, new Rectangle(pill.X + inner, pill.Y + (height - dot) / 2, dot, dot), color);
        TextRenderer.DrawText(g, _status.Summary, _ui.Bold, new Rectangle(pill.X + inner + dot + gap, pill.Y, textWidth + 1, height), Palette.Text,
            Ui.OneLine | TextFormatFlags.VerticalCenter);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _icon?.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>Shown above everything else when the server could not start: the reason, what to
/// do about it, and a button to try again.</summary>
internal sealed class ProblemCard : Card
{
    private readonly RaisedButton _retry;
    private StartProblem? _problem;

    public ProblemCard(IWindowActions actions) : base("Abyss Remote is not running", actions)
    {
        _retry = Add(new RaisedButton("Try again", ButtonKind.Primary));
        _retry.Click += (_, _) => Actions.TryAgain();
    }

    protected override Color Lip => Palette.Danger;

    public void Show(StartProblem? problem)
    {
        if (Equals(problem, _problem)) return;
        _problem = problem;
        Invalidate();
    }

    private string Advice => _problem?.Kind switch
    {
        StartProblemKind.PortInUse => "Close the other program, or choose another port under Settings. Then try again.",
        StartProblemKind.PortNotAllowed => "Choose another port under Settings, for example 8555.",
        StartProblemKind.StateFile => "After moving the file, try again.",
        StartProblemKind.WebUiMissing => "",
        _ => "Try again. If it keeps happening, restart the PC.",
    };

    protected override int Compose(Pass pass, int width)
    {
        int buttonWidth = _retry.WidthFor(Ui), buttonHeight = Ui.ButtonHeight(34);
        int textWidth = width - 2 * Pad - buttonWidth - Ui.Px(16);
        int y = Header(pass, width, buttonWidth + Ui.Px(16));
        y += pass.Text(_problem?.Message ?? "", Ui.Body, Palette.Text, Pad, y, textWidth);
        if (Advice.Length > 0) y += Ui.Px(4) + pass.Text(Advice, Ui.Small, Palette.Muted, Pad, y + Ui.Px(4), textWidth);
        int height = Math.Max(y + Pad, 2 * Pad + buttonHeight);
        pass.Place(_retry, width - Pad - buttonWidth, (height - buttonHeight) / 2, buttonWidth, buttonHeight);
        return height;
    }
}

/// <summary>How to connect: the address to open, the pairing code with its remaining time,
/// and in home-network mode a QR code that holds both. With away access on, also the address
/// that works at home and away, and the button that shows the code for it.</summary>
internal sealed class ConnectCard : Card
{
    private readonly RaisedButton _copy, _newCode, _awayCode;
    private readonly System.Windows.Forms.Timer _copiedTimer = new() { Interval = 1500 };
    private HostStatus? _status;
    private PairingCode _code = PairingCode.None;
    private DateTimeOffset _now;
    private Bitmap? _qr;
    private (string Text, int Pixels) _qrKey;

    public ConnectCard(IWindowActions actions) : base("Connect", actions)
    {
        _copy = Add(new RaisedButton("Copy address"));
        _newCode = Add(new RaisedButton("New code", ButtonKind.Primary));
        _awayCode = Add(new RaisedButton("Show away code") { Visible = false });
        _awayCode.Click += (_, _) => Actions.ShowAwayCode();
        _newCode.Click += (_, _) => Actions.NewPairingCode();
        _copy.Click += (_, _) =>
        {
            Actions.CopyAddress();
            _copy.Text = "Copied";
            _copiedTimer.Stop();
            _copiedTimer.Start();
        };
        _copiedTimer.Tick += (_, _) =>
        {
            _copiedTimer.Stop();
            _copy.Text = "Copy address";
        };
    }

    public void Show(HostStatus status, PairingCode code, DateTimeOffset now)
    {
        // The countdown changes every second, so compare what will be drawn, not the clock.
        bool same = Equals(status, _status) && Equals(code, _code) && CodeStatus(code, now) == CodeStatus(_code, _now);
        (_status, _code, _now) = (status, code, now);
        if (!same) Invalidate();
    }

    private static string CodeStatus(PairingCode code, DateTimeOffset now)
    {
        if (code.IsUsable(now))
        {
            var left = code.Remaining(now);
            return $"Valid for {(int)left.TotalMinutes}:{left.Seconds:00}. Works once.";
        }
        return code.State switch
        {
            PairingCodeState.Active => "This code has expired. Make a new one to pair a device.",
            PairingCodeState.Used => "This code was used. Make a new one to pair another device.",
            PairingCodeState.Locked => "Turned off after too many wrong guesses. Make a new code when you are ready.",
            _ => "No code right now. Make one when you want to pair a device.",
        };
    }

    protected override int Compose(Pass pass, int width)
    {
        int y = Header(pass, width), inner = width - 2 * Pad;
        var running = _status?.Running;
        _copy.Visible = _newCode.Visible = running is not null;
        _awayCode.Visible = running?.AwayUrl is not null;
        if (running is null)
        {
            string waiting = _status?.Phase == HostPhase.Starting
                ? "Starting…"
                : "Nothing to connect to yet. The address and the pairing code appear here once Abyss Remote is running.";
            return y + pass.Text(waiting, Ui.Body, Palette.Muted, Pad, y, inner) + Pad;
        }

        // In home-network mode the QR code sits to the right of the text, or below it when the
        // card is narrow.
        int qrSide = Ui.Px(176), gap = Ui.Px(24);
        bool qrBeside = running.Lan && inner - qrSide - gap >= Ui.Px(250);
        int column = qrBeside ? inner - qrSide - gap : inner;
        int top = y;

        string lead = running.Lan ? "Open this address on the phone, tablet or other computer:" : "Open this address in a browser on this PC:";
        y += pass.Text(lead, Ui.Small, Palette.Muted, Pad, y, column) + Ui.Px(4);
        y += pass.Line(running.MainUrl, Ui.Heading, Palette.Accent, Pad, y, column) + Ui.Px(2);
        foreach (string other in running.LanUrls.Skip(1))
            y += pass.Line($"or {other}", Ui.Small, Palette.Muted, Pad, y, column);
        if (running.Lan) y += pass.Line($"On this PC: {running.LocalUrl}", Ui.Small, Palette.Muted, Pad, y, column);
        y += Ui.Px(10);
        pass.Place(_copy, Pad, y, _copy.WidthFor(Ui), Ui.ButtonHeight(32));
        y += Ui.ButtonHeight(32) + Ui.Px(14);

        y += pass.Line("Pairing code", Ui.Bold, Palette.Accent, Pad, y, column);
        bool usable = _code.IsUsable(_now);
        // An unusable code is not shown at all; the dashes keep the layout from jumping.
        y += pass.Line(usable ? _code.Grouped : "–––– ––––", Ui.Code, usable ? Palette.Text : Palette.Tint(Palette.Muted, 45), Pad, y, column);
        y += pass.Text(CodeStatus(_code, _now), Ui.Small, usable ? Palette.Muted : Palette.Text, Pad, y, column) + Ui.Px(12);
        pass.Place(_newCode, Pad, y, _newCode.WidthFor(Ui), Ui.ButtonHeight(34));
        y += Ui.ButtonHeight(34);

        if (running.Lan)
        {
            int qrX = qrBeside ? width - Pad - qrSide : Pad, qrY = qrBeside ? top : y + Ui.Px(20);
            int qrBottom = DrawQr(pass, _code.PairingUrl(running.MainUrl, _now), qrX, qrY, qrSide, usable);
            y = Math.Max(y, qrBottom);
        }
        else if (running.AwayUrl is null)
        {
            y += Ui.Px(16);
            y += pass.Text("Phones and tablets need home-network mode. Turn on “Allow devices on my home network” under Settings.",
                Ui.Small, Palette.Muted, Pad, y, inner);
        }
        if (running.AwayUrl is { } awayUrl) y = AwayRow(pass, width, y, awayUrl);
        return y + Pad;
    }

    /// <summary>With away access on: the address that works at home and away, and the button
    /// that shows the code for it. The code itself is never on this card: it appears in a box
    /// of its own, only when asked for.</summary>
    private int AwayRow(Pass pass, int width, int y, string awayUrl)
    {
        int buttonWidth = _awayCode.WidthFor(Ui), buttonHeight = Ui.ButtonHeight(32);
        int textWidth = width - 2 * Pad - buttonWidth - Ui.Px(12);
        y += Ui.Px(14);
        if (pass.Graphics is { } g)
        {
            using var line = new Pen(Palette.Border, Ui.Outline);
            g.DrawLine(line, Pad, y, width - Pad, y);
        }
        y += Ui.Px(12);
        int labelHeight = pass.Line("At home and away", Ui.Bold, Palette.Accent, Pad, y, textWidth);
        int addressHeight = pass.Line(awayUrl, Ui.Body, Palette.Text, Pad, y + labelHeight + Ui.Px(1), textWidth);
        int rowHeight = Math.Max(labelHeight + Ui.Px(1) + addressHeight, buttonHeight);
        pass.Place(_awayCode, width - Pad - buttonWidth, y + (rowHeight - buttonHeight) / 2, buttonWidth, buttonHeight);
        return y + rowHeight;
    }

    /// <summary>The QR code on a white tile with a caption. Returns the y coordinate below it.</summary>
    private int DrawQr(Pass pass, string url, int x, int y, int side, bool withCode)
    {
        if (pass.Graphics is { } g)
        {
            var code = QrCode.Encode(url);
            int pixels = QrRenderer.PixelsPerModule(code, side - Ui.Px(8)); // stay clear of the tile's round corners
            if (_qr is null || _qrKey != (url, pixels))
            {
                _qr?.Dispose();
                _qr = QrRenderer.ToBitmap(code, pixels, Palette.Border, Color.White);
                _qrKey = (url, pixels);
            }
            Ui.FillRounded(g, new Rectangle(x, y, side, side), Ui.Corner, Color.White, Palette.Border, Ui.Outline);
            // The picture is a whole number of pixels per module, so it is centred, not stretched.
            g.DrawImageUnscaled(_qr, x + (side - _qr.Width) / 2, y + (side - _qr.Height) / 2);
        }
        string caption = withCode
            ? "Scan with the phone’s camera. It opens the address and fills in the code."
            : "Scan with the phone’s camera to open the address.";
        return y + side + Ui.Px(8) + pass.Text(caption, Ui.Small, Palette.Muted, x, y + side + Ui.Px(8), side);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _qr?.Dispose();
            _copiedTimer.Dispose();
        }
        base.Dispose(disposing);
    }
}
