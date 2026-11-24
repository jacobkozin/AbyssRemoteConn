using System.Drawing.Drawing2D;

namespace AbyssRemote.Host.App;

/// <summary>The checklist for using Abyss Remote from a phone: the firewall rule, the network
/// type, the certificate the phone has to trust, and the controller driver. The card only
/// reports; its buttons hand the actual changes to Windows (a UAC prompt, the Settings app).</summary>
internal sealed class PhoneSetupCard : Card
{
    private enum Mark { Done, ToDo, Problem, Info }

    private readonly RaisedButton _checkAgain, _addRule, _openSettings, _saveCertificate;
    private PhoneSetup _setup = PhoneSetup.NotChecked;
    private HostStatus? _status;

    public PhoneSetupCard(IWindowActions actions) : base("Phone setup", actions)
    {
        _checkAgain = Add(new RaisedButton("Check again"));
        _addRule = Add(new RaisedButton("Add firewall rule…"));
        _openSettings = Add(new RaisedButton("Open network settings"));
        _saveCertificate = Add(new RaisedButton("Save certificate file…"));
        _checkAgain.Click += (_, _) => Actions.CheckSetupAgain();
        _addRule.Click += (_, _) => Actions.AddFirewallRule();
        _openSettings.Click += (_, _) => Actions.OpenNetworkSettings();
        _saveCertificate.Click += (_, _) => Actions.SaveCertificate();
    }

    public void Show(PhoneSetup setup, HostStatus status)
    {
        if (Equals(setup, _setup) && Equals(status, _status)) return;
        (_setup, _status) = (setup, status);
        Invalidate();
    }

    protected override int Compose(Pass pass, int width)
    {
        int checkWidth = _checkAgain.WidthFor(Ui), buttonHeight = Ui.ButtonHeight(30);
        int y = Header(pass, width, checkWidth + Ui.Px(12));
        pass.Place(_checkAgain, width - Pad - checkWidth, Pad - Ui.Px(4), checkWidth, buttonHeight);
        int port = _status?.Running?.Port ?? _status?.Options.Port ?? LaunchOptions.DefaultPort;

        // 1. The firewall rule for this port.
        var (mark, title, detail) = _setup.Firewall switch
        {
            FirewallVerdict.Allowed => (Mark.Done, $"Windows Firewall lets devices reach port {port}",
                "A rule for Private networks is in place."),
            FirewallVerdict.NoRule => (Mark.ToDo, $"Windows Firewall has no rule for port {port}",
                $"Add one rule: TCP {port}, Private networks, devices on your own network only. Windows asks for permission first."),
            FirewallVerdict.Blocked => (Mark.Problem, "A firewall rule blocks Abyss Remote",
                "Windows adds it when its “allow access?” question is answered with Cancel. Remove it under Windows Security > Firewall & network protection > Allow an app through firewall, then check again."),
            FirewallVerdict.FirewallOff => (Mark.Info, "Windows Firewall is off for Private networks",
                "Windows itself blocks nothing here. Another security program may still do so."),
            _ => (Mark.Info, "The firewall rules could not be read",
                "Look under Windows Security > Firewall & network protection."),
        };
        _addRule.Visible = _setup.Firewall == FirewallVerdict.NoRule;
        y = Row(pass, width, y, mark, title, detail, _addRule.Visible ? _addRule : null);

        // 2. Private or Public network.
        string network = string.IsNullOrEmpty(_setup.NetworkName) ? "this network" : $"“{_setup.NetworkName}”";
        (mark, title, detail) = _setup.Network switch
        {
            NetworkKind.Private => (Mark.Done, "Your network is marked Private",
                $"Windows treats {network} as a home network, so the firewall rule applies to it."),
            NetworkKind.Public => (Mark.Problem, "Your network is marked Public",
                $"Windows blocks incoming connections on Public networks. If {network} is your home network, change it to Private."),
            NetworkKind.NotConnected => (Mark.Info, "This PC is not connected to a network", "Phones connect over your Wi-Fi or wired network."),
            _ => (Mark.Info, "The network type could not be read", "Look under Settings > Network & internet."),
        };
        _openSettings.Visible = _setup.Network == NetworkKind.Public;
        y = Row(pass, width, y, mark, title, detail, _openSettings.Visible ? _openSettings : null);

        // 3. The certificate, which only the phone can confirm, so this row is never "done".
        y = CertificateRow(pass, width, y);

        // 4. The controller driver.
        (mark, title) = _setup.ControllerDriverInstalled
            ? (Mark.Done, "Controller driver (ViGEmBus) is installed")
            : (Mark.Info, "Controller driver (ViGEmBus) is not installed");
        detail = _setup.ControllerDriverInstalled
            ? "A game controller connected to the phone or tablet works as an Xbox controller in PC games."
            : "Only needed to play with a game controller through the phone or tablet. See “Controllers” in the README.";
        y = Row(pass, width, y, mark, title, detail, null);
        return y + Pad - Ui.Px(12);
    }

    private int TextX => Pad + Ui.Px(20) + Ui.Px(12);

    /// <summary>One checklist row: a mark, a title, an explanation and an optional button.</summary>
    private int Row(Pass pass, int width, int y, Mark mark, string title, string detail, RaisedButton? button)
    {
        int textWidth = width - Pad - TextX;
        if (pass.Graphics is { } g) DrawMark(g, mark, Pad, y);
        y += pass.Text(title, Ui.Bold, Palette.Text, TextX, y, textWidth) + Ui.Px(2);
        y += pass.Text(detail, Ui.Small, Palette.Muted, TextX, y, textWidth);
        if (button is not null)
        {
            y += Ui.Px(8);
            pass.Place(button, TextX, y, button.WidthFor(Ui), Ui.ButtonHeight(30));
            y += Ui.ButtonHeight(30);
        }
        return y + Ui.Px(12);
    }

    private int CertificateRow(Pass pass, int width, int y)
    {
        int textWidth = width - Pad - TextX;
        var running = _status?.Running;
        if (pass.Graphics is { } g) DrawMark(g, Mark.Info, Pad, y);
        y += pass.Text("Trust the Abyss Remote certificate on the phone (once)", Ui.Bold, Palette.Text, TextX, y, textWidth) + Ui.Px(2);
        _saveCertificate.Visible = running is not null;
        if (running is null)
            return y + pass.Text("The certificate is shown here once Abyss Remote is running.", Ui.Small, Palette.Muted, TextX, y, textWidth) + Ui.Px(12);

        y += pass.Text("Before trusting it, check that the phone shows this SHA-256 fingerprint:", Ui.Small, Palette.Muted, TextX, y, textWidth) + Ui.Px(4);
        foreach (string line in FingerprintLines(running.RootFingerprint, textWidth))
            y += pass.Line(line, Ui.Mono, Palette.Text, TextX, y, textWidth);
        y += Ui.Px(8);
        pass.Place(_saveCertificate, TextX, y, _saveCertificate.WidthFor(Ui), Ui.ButtonHeight(30));
        y += Ui.ButtonHeight(30) + Ui.Px(8);

        if (running.RootCertificateUrl is { } download)
        {
            y += pass.Text("Or download it on the phone:", Ui.Small, Palette.Muted, TextX, y, textWidth);
            y += pass.Line(download, Ui.Small, Palette.Accent, TextX, y, textWidth) + Ui.Px(6);
        }
        else
        {
            y += pass.Text("In home-network mode the phone can also download it from this PC.", Ui.Small, Palette.Muted, TextX, y, textWidth) + Ui.Px(6);
        }
        string[] steps =
        [
            "Get the certificate onto the iPhone or iPad: open the download address in Safari, or AirDrop or email the saved file to yourself and open it.",
            "Settings > General > VPN & Device Management: install the downloaded profile.",
            "Settings > General > About > Certificate Trust Settings: turn on full trust for “Abyss Remote Personal Root CA”.",
        ];
        int number = Ui.Px(18);
        for (int i = 0; i < steps.Length; i++)
        {
            pass.Line($"{i + 1}.", Ui.Small, Palette.Muted, TextX, y, number);
            y += pass.Text(steps[i], Ui.Small, Palette.Muted, TextX + number, y, textWidth - number) + Ui.Px(3);
        }
        return y + Ui.Px(9);
    }

    /// <summary>The fingerprint as pairs of hex digits, on as few lines as fit the width
    /// (iPhones show it in pairs too, which makes comparing easier).</summary>
    private IEnumerable<string> FingerprintLines(string hex, int width)
    {
        var pairs = Enumerable.Range(0, hex.Length / 2).Select(i => hex.Substring(i * 2, 2)).ToArray();
        int perLine = pairs.Length;
        while (perLine > 4 && Ui.MeasureLine(string.Join(' ', pairs.Take(perLine)), Ui.Mono).Width > width) perLine /= 2;
        return pairs.Chunk(perLine).Select(line => string.Join(' ', line));
    }

    private void DrawMark(Graphics g, Mark mark, int x, int y)
    {
        int size = Ui.Px(20);
        var circle = new Rectangle(x, y, size, size);
        Color color = mark switch { Mark.Done => Palette.Success, Mark.Problem => Palette.Danger, Mark.ToDo => Palette.Accent, _ => Palette.Muted };
        // A status mark is its colour as the face, with the sign drawn in the dark outline colour.
        Ui.FillRounded(g, circle, size / 2f, color, Palette.Border, Math.Max(1f, size / 10f));
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(Palette.OnAccent, Math.Max(1.5f, size / 10f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        float s = size;
        switch (mark)
        {
            case Mark.Done: // a tick
                g.DrawLines(pen, [new PointF(x + s * 0.29f, y + s * 0.52f), new PointF(x + s * 0.44f, y + s * 0.67f), new PointF(x + s * 0.72f, y + s * 0.36f)]);
                break;
            case Mark.Problem: // an exclamation mark
                g.DrawLine(pen, x + s * 0.5f, y + s * 0.27f, x + s * 0.5f, y + s * 0.56f);
                g.DrawLine(pen, x + s * 0.5f, y + s * 0.72f, x + s * 0.5f, y + s * 0.73f);
                break;
            case Mark.ToDo: // an arrow pointing at the thing to do
                g.DrawLine(pen, x + s * 0.30f, y + s * 0.5f, x + s * 0.68f, y + s * 0.5f);
                g.DrawLines(pen, [new PointF(x + s * 0.54f, y + s * 0.35f), new PointF(x + s * 0.69f, y + s * 0.5f), new PointF(x + s * 0.54f, y + s * 0.65f)]);
                break;
            default: // "i" for information
                g.DrawLine(pen, x + s * 0.5f, y + s * 0.46f, x + s * 0.5f, y + s * 0.73f);
                g.DrawLine(pen, x + s * 0.5f, y + s * 0.28f, x + s * 0.5f, y + s * 0.29f);
                break;
        }
        g.SmoothingMode = SmoothingMode.Default;
    }
}
