namespace AbyssRemote.Host.App;

/// <summary>The icon next to the clock. A left click shows or hides the window, a right click
/// opens a short menu, and the tooltip says what the server is doing.</summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _copy, _newCode, _revokeAll;

    /// <param name="visible">False only for the snapshot tool, which draws the menu without
    /// putting an icon in the real tray.</param>
    public TrayIcon(Action toggleWindow, Action openWindow, Action copyAddress, Action newCode, Action revokeAll, Action quit, bool visible = true)
    {
        Menu = new ContextMenuStrip
        {
            ShowImageMargin = false, Renderer = new ToolStripProfessionalRenderer(new MenuColors()),
            BackColor = Palette.Surface, ForeColor = Palette.Text,
        };
        var open = new ToolStripMenuItem("Open Abyss Remote", null, (_, _) => openWindow());
        open.Font = new Font(open.Font, FontStyle.Bold); // the default action, like a left click
        _copy = new ToolStripMenuItem("Copy address", null, (_, _) => copyAddress());
        _newCode = new ToolStripMenuItem("New pairing code", null, (_, _) => newCode());
        _revokeAll = new ToolStripMenuItem("Revoke all devices…", null, (_, _) => revokeAll());
        var quitItem = new ToolStripMenuItem("Quit", null, (_, _) => quit());
        Menu.Items.AddRange([open, _copy, _newCode, _revokeAll, new ToolStripSeparator(), quitItem]);

        _icon = new NotifyIcon
        {
            Icon = AppIcon.Load(SystemInformation.SmallIconSize.Width), Text = AppInfo.Name, ContextMenuStrip = Menu, Visible = visible,
        };
        _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) toggleWindow(); };
    }

    public ContextMenuStrip Menu { get; }

    /// <summary>Updates the tooltip and greys out what makes no sense right now.</summary>
    public void Show(HostStatus status, int deviceCount)
    {
        string text = $"{AppInfo.Name} - {status.Summary}";
        _icon.Text = text.Length <= 120 ? text : text[..119] + "…"; // Windows limits tooltips to 127 characters
        _copy.Enabled = _newCode.Enabled = status.Running is not null;
        _revokeAll.Enabled = deviceCount > 0;
    }

    /// <summary>A notification from the tray icon.</summary>
    public void Tip(string title, string text) => _icon.ShowBalloonTip(5000, title, text, ToolTipIcon.None);

    public void Dispose()
    {
        _icon.Visible = false; // otherwise a dead icon stays until the mouse moves over it
        _icon.Dispose();
        Menu.Dispose();
    }

    /// <summary>The menu in the window's colours instead of Windows' light grey.</summary>
    private sealed class MenuColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Palette.Surface;
        public override Color ImageMarginGradientBegin => Palette.Surface;
        public override Color ImageMarginGradientMiddle => Palette.Surface;
        public override Color ImageMarginGradientEnd => Palette.Surface;
        public override Color MenuBorder => Palette.Border;
        public override Color MenuItemBorder => Palette.RaisedHover;
        public override Color MenuItemSelected => Palette.RaisedHover;
        public override Color SeparatorDark => Palette.Border;
        public override Color SeparatorLight => Palette.Surface;
    }
}
