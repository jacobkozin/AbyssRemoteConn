namespace AbyssRemote.Host.App;

/// <summary>The four settings: home-network mode, the port, starting with Windows and what
/// closing the window does. A change is applied (and saved) at once; there is no OK button.</summary>
internal sealed class SettingsCard : Card
{
    private readonly CheckRow _lan, _autostart, _tray;
    private readonly TextBox _port;
    private readonly FlatButton _apply;
    private int _currentPort = LaunchOptions.DefaultPort;
    private bool _starting;

    public SettingsCard(IWindowActions actions) : base("Settings", actions)
    {
        _lan = Add(new CheckRow("Allow devices on my home network"));
        _port = Add(new TextBox
        {
            BorderStyle = BorderStyle.None, BackColor = Palette.Background, ForeColor = Palette.Text, MaxLength = 5,
            Text = LaunchOptions.DefaultPort.ToString(), AccessibleName = "Port",
        });
        _apply = Add(new FlatButton("Apply") { Enabled = false });
        _autostart = Add(new CheckRow("Start with Windows"));
        _tray = Add(new CheckRow("Keep running in the tray when I close the window"));

        _lan.Click += (_, _) => Actions.SetHomeNetwork(!_lan.Checked);
        _autostart.Click += (_, _) => Actions.SetStartWithWindows(!_autostart.Checked);
        _tray.Click += (_, _) => Actions.SetCloseToTray(!_tray.Checked);
        _apply.Click += (_, _) => ApplyPort();
        _port.TextChanged += (_, _) => { _apply.Enabled = TypedPort is { } port && port != _currentPort && !_starting; Invalidate(); };
        _port.GotFocus += (_, _) => Invalidate();
        _port.LostFocus += (_, _) => Invalidate();
        _port.KeyPress += (_, e) =>
        {
            if (e.KeyChar == (char)Keys.Enter) { e.Handled = true; ApplyPort(); }
            else if (!char.IsControl(e.KeyChar) && !char.IsAsciiDigit(e.KeyChar)) e.Handled = true; // digits only
        };
    }

    /// <summary>The number in the box when it is a usable port, otherwise null.</summary>
    private int? TypedPort => int.TryParse(_port.Text, out int port) && Preferences.IsValidPort(port) ? port : null;

    private void ApplyPort()
    {
        if (TypedPort is { } port && port != _currentPort && !_starting) Actions.SetPort(port);
    }

    public void Show(HostStatus status, bool startWithWindows, bool closeToTray)
    {
        _lan.Checked = status.Options.Lan;
        _autostart.Checked = startWithWindows;
        _tray.Checked = closeToTray;
        _starting = status.Phase == HostPhase.Starting;
        // Show the port in use, but never overwrite what the user is typing.
        if (status.Options.Port != _currentPort || (!_port.Focused && TypedPort is null))
        {
            _currentPort = status.Options.Port;
            _port.Text = _currentPort.ToString();
        }
        _lan.Enabled = !_starting; // one restart at a time
        _apply.Enabled = TypedPort is { } port && port != _currentPort && !_starting;
    }

    protected override int Compose(Pass pass, int width)
    {
        int y = Header(pass, width), inner = width - 2 * Pad;
        int indent = (int)Math.Round(Ui.Body.Size * 2.1f); // lines up with the check box labels

        int height = _lan.HeightFor(inner);
        pass.Place(_lan, Pad, y, inner, height);
        y += height + Ui.Px(3);
        y += pass.Text("Phones, tablets and other computers on your Wi-Fi or wired network can then open Abyss Remote. Each one still needs the pairing code.",
            Ui.Small, Palette.Muted, Pad + indent, y, inner - indent) + Ui.Px(12);

        // The port: a label, a small text field and a button on one line.
        int fieldHeight = Ui.Px(32), fieldWidth = Ui.Px(84), labelWidth = Ui.MeasureLine("Port", Ui.Body).Width;
        int labelHeight = Ui.MeasureLine("Ag", Ui.Body).Height;
        pass.Line("Port", Ui.Body, Palette.Text, Pad, y + (fieldHeight - labelHeight) / 2, labelWidth + 1);
        var field = new Rectangle(Pad + labelWidth + Ui.Px(12), y, fieldWidth, fieldHeight);
        if (pass.Graphics is { } g)
            Ui.FillRounded(g, field, Ui.Px(8), Palette.Background, _port.Focused ? Palette.Accent : TypedPort is null ? Palette.Danger : Palette.Border);
        pass.Place(_port, field.X + Ui.Px(10), field.Y + (fieldHeight - _port.PreferredHeight) / 2, fieldWidth - Ui.Px(20), _port.PreferredHeight);
        pass.Place(_apply, field.Right + Ui.Px(8), y, _apply.WidthFor(Ui), fieldHeight);
        y += fieldHeight + Ui.Px(6);
        y += TypedPort is null
            ? pass.Text("Use a number from 1024 to 65535.", Ui.Small, Palette.Danger, Pad, y, inner)
            : pass.Text("Changing the port restarts the server. The firewall rule is per port.", Ui.Small, Palette.Muted, Pad, y, inner);
        y += Ui.Px(12);

        height = _autostart.HeightFor(inner);
        pass.Place(_autostart, Pad, y, inner, height);
        y += height + Ui.Px(10);
        height = _tray.HeightFor(inner);
        pass.Place(_tray, Pad, y, inner, height);
        return y + height + Pad;
    }
}

/// <summary>What happened, newest last: pairings, removals, refused connections.</summary>
internal sealed class ActivityCard : Card
{
    private const string Empty = "Nothing has happened yet. Pairings, removals and refused connections show up here.";
    private readonly TextBox _log;
    private NoticeLine? _last;
    private int _count = -1;

    public ActivityCard(IWindowActions actions) : base("Activity", actions)
    {
        // A read-only text box gives scrolling, selecting and copying for free.
        _log = Add(new TextBox
        {
            Multiline = true, ReadOnly = true, BorderStyle = BorderStyle.None, ScrollBars = ScrollBars.Vertical, TabStop = false,
            BackColor = Palette.Surface, ForeColor = Palette.Muted, Text = Empty, AccessibleName = "Activity",
        });
        _log.HandleCreated += (_, _) => WindowNative.UseDarkScrollBars(_log.Handle);
    }

    /// <summary>The height the window gives this card beyond what it asks for; the list grows into it.</summary>
    public int ExtraHeight { get; set; }

    protected override void OnScaleChanged() => _log.Font = Ui.Small;

    public void Show(IReadOnlyList<NoticeLine> notices)
    {
        var last = notices.Count > 0 ? notices[^1] : null;
        if (notices.Count == _count && ReferenceEquals(last, _last)) return;
        (_count, _last) = (notices.Count, last);
        _log.ForeColor = notices.Count == 0 ? Palette.Muted : Palette.Text;
        _log.Text = notices.Count == 0 ? Empty : string.Join(Environment.NewLine, notices.Select(n => $"{n.Time.ToLocalTime():HH:mm:ss}   {n.Message}"));
        // Keep the newest line in view.
        _log.SelectionStart = _log.TextLength;
        _log.ScrollToCaret();
    }

    protected override int Compose(Pass pass, int width)
    {
        int y = Header(pass, width), listHeight = Ui.Px(88) + ExtraHeight;
        pass.Place(_log, Pad, y, width - 2 * Pad + Ui.Px(8), listHeight);
        return y + listHeight + Pad - Ui.Px(4);
    }
}
