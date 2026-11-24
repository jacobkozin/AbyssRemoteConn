namespace AbyssRemote.Host.App;

/// <summary>The settings: home-network mode, the port, away access with its address, starting
/// with Windows and what closing the window does. A change is applied (and saved) at once; there
/// is no OK button.</summary>
internal sealed class SettingsCard : Card
{
    private readonly CheckRow _lan, _away, _autostart, _tray;
    private readonly TextBox _port, _awayHost;
    private readonly FlatButton _apply, _awayApply;
    private int _currentPort = LaunchOptions.DefaultPort;
    private string _savedAwayHost = "";
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
        _away = Add(new CheckRow("Let added devices connect away from home"));
        _awayHost = Add(new TextBox
        {
            BorderStyle = BorderStyle.None, BackColor = Palette.Background, ForeColor = Palette.Text, MaxLength = 253,
            AccessibleName = "Away address", PlaceholderText = "remote.example.duckdns.org",
        });
        _awayApply = Add(new FlatButton("Apply") { Enabled = false, AccessibleName = "Apply the away address" });
        _autostart = Add(new CheckRow("Start with Windows"));
        _tray = Add(new CheckRow("Keep running in the tray when I close the window"));

        _lan.Click += (_, _) => Actions.SetHomeNetwork(!_lan.Checked);
        _away.Click += (_, _) => Actions.SetAway(!_away.Checked);
        _awayApply.Click += (_, _) => ApplyAwayHost();
        _awayHost.TextChanged += (_, _) => { _awayApply.Enabled = CanApplyAwayHost; Invalidate(); };
        _awayHost.GotFocus += (_, _) => Invalidate();
        _awayHost.LostFocus += (_, _) => Invalidate();
        _awayHost.KeyPress += (_, e) =>
        {
            if (e.KeyChar == (char)Keys.Enter) { e.Handled = true; ApplyAwayHost(); }
        };
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

    /// <summary>The name in the away box, tidied, when it is usable; otherwise null.</summary>
    private string? TypedAwayHost => AwayAccess.IsValidHost(_awayHost.Text) ? _awayHost.Text.Trim().ToLowerInvariant() : null;

    private bool CanApplyAwayHost => TypedAwayHost is { } host && host != _savedAwayHost && !_starting;

    private void ApplyAwayHost()
    {
        if (CanApplyAwayHost) Actions.SetAwayHost(TypedAwayHost!);
    }

    /// <param name="awayHost">The saved away address, shown also while away access is off.</param>
    public void Show(HostStatus status, bool startWithWindows, bool closeToTray, string awayHost = "")
    {
        _lan.Checked = status.Options.Lan;
        // Checked only while the server really uses it (a saved "on" without a usable address is off).
        _away.Checked = status.Options.AwayHost is not null;
        _autostart.Checked = startWithWindows;
        _tray.Checked = closeToTray;
        _starting = status.Phase == HostPhase.Starting;
        // Show the port in use, but never overwrite what the user is typing.
        if (status.Options.Port != _currentPort || (!_port.Focused && TypedPort is null))
        {
            _currentPort = status.Options.Port;
            _port.Text = _currentPort.ToString();
        }
        // Show the saved away address, but never overwrite what the user is typing.
        if (awayHost != _savedAwayHost)
        {
            _savedAwayHost = awayHost;
            if (!_awayHost.Focused) _awayHost.Text = awayHost;
        }
        _lan.Enabled = !_starting; // one restart at a time
        _away.Enabled = !_starting;
        _apply.Enabled = TypedPort is { } port && port != _currentPort && !_starting;
        _awayApply.Enabled = CanApplyAwayHost;
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

        // Away from home: the switch, what it means, then the address on one line like the port.
        height = _away.HeightFor(inner);
        pass.Place(_away, Pad, y, inner, height);
        y += height + Ui.Px(3);
        string address = TypedAwayHost ?? (_savedAwayHost.Length > 0 ? _savedAwayHost : "the away address");
        y += pass.Text($"Through the Gaming Hub at https://{address}. Each device is set up once at home; new ones can only be added at home.",
            Ui.Small, Palette.Muted, Pad + indent, y, inner - indent) + Ui.Px(12);
        int awayLabelWidth = Ui.MeasureLine("Away address", Ui.Body).Width, awayApplyWidth = _awayApply.WidthFor(Ui);
        int awayFieldWidth = Math.Max(Ui.Px(120), inner - awayLabelWidth - Ui.Px(12) - awayApplyWidth - Ui.Px(8));
        pass.Line("Away address", Ui.Body, Palette.Text, Pad, y + (fieldHeight - labelHeight) / 2, awayLabelWidth + 1);
        var awayField = new Rectangle(Pad + awayLabelWidth + Ui.Px(12), y, awayFieldWidth, fieldHeight);
        bool awayTyped = _awayHost.Text.Trim().Length > 0;
        if (pass.Graphics is { } awayGraphics)
            Ui.FillRounded(awayGraphics, awayField, Ui.Px(8), Palette.Background,
                _awayHost.Focused ? Palette.Accent : awayTyped && TypedAwayHost is null ? Palette.Danger : Palette.Border);
        pass.Place(_awayHost, awayField.X + Ui.Px(10), awayField.Y + (fieldHeight - _awayHost.PreferredHeight) / 2,
            awayFieldWidth - Ui.Px(20), _awayHost.PreferredHeight);
        pass.Place(_awayApply, awayField.Right + Ui.Px(8), y, awayApplyWidth, fieldHeight);
        y += fieldHeight + Ui.Px(12);
        if (awayTyped && TypedAwayHost is null)
            y += pass.Text("Use a name like remote.example.duckdns.org, without https:// or a port.", Ui.Small, Palette.Danger, Pad, y, inner) + Ui.Px(12);

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
