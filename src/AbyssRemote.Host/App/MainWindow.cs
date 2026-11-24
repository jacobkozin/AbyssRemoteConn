namespace AbyssRemote.Host.App;

/// <summary>Everything inside the window: the header and, below it, a scrolling area with the
/// cards. Wide windows get two columns, narrow ones a single column. It is a control of its
/// own (not the form) so the snapshot tool can draw it at any scale without a visible window.</summary>
internal sealed class MainView : Control
{
    private readonly HeaderBar _header = new();
    private readonly Panel _scroll = new() { AutoScroll = true, BackColor = Palette.Background };
    private readonly ProblemCard _problem;
    private readonly ConnectCard _connect;
    private readonly DevicesCard _devices;
    private readonly PhoneSetupCard _setup;
    private readonly SettingsCard _settings;
    private readonly ActivityCard _activity;
    private readonly Card[] _cards;
    private Ui _ui;
    private bool _arranging;

    public MainView(IWindowActions actions, float scale)
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        BackColor = Palette.Background;
        _ui = new Ui(scale);
        _problem = new ProblemCard(actions) { Visible = false };
        _connect = new ConnectCard(actions);
        _devices = new DevicesCard(actions);
        _setup = new PhoneSetupCard(actions);
        _settings = new SettingsCard(actions);
        _activity = new ActivityCard(actions);
        _cards = [_problem, _connect, _devices, _setup, _settings, _activity];
        // Tab moves through the cards in reading order.
        for (int i = 0; i < _cards.Length; i++) _cards[i].TabIndex = i;
        _scroll.Controls.AddRange(_cards);
        _scroll.HandleCreated += (_, _) => WindowNative.UseDarkScrollBars(_scroll.Handle);
        Controls.Add(_header);
        Controls.Add(_scroll);
        ApplyScale();
    }

    /// <summary>Switches to another scale, when the window moves to a monitor that differs.</summary>
    public void SetScale(float scale)
    {
        if (Math.Abs(scale - _ui.Scale) < 0.001f) return;
        var old = _ui;
        _ui = new Ui(scale);
        ApplyScale();
        old.Dispose();
    }

    public void Show(WindowState state)
    {
        _header.Show(state.Status);
        _problem.Show(state.Status.Problem);
        _connect.Show(state.Status, state.Code, state.Now);
        _devices.Show(state.Devices, state.ConnectedDeviceIds, state.Now);
        _setup.Show(state.Setup, state.Status);
        _settings.Show(state.Status, state.StartWithWindows, state.CloseToTray, state.AwayHost);
        _activity.Show(state.Notices);
        _problem.Visible = state.Status.Phase == HostPhase.Failed;
        Arrange(); // heights depend on the content (a wrapped sentence, one more device)
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Arrange();
    }

    private void ApplyScale()
    {
        _header.UseScale(_ui);
        foreach (var card in _cards) card.UseScale(_ui);
        Arrange();
        Invalidate(true);
    }

    /// <summary>Places the header and the cards. Every size here is in logical pixels times
    /// the scale, so the layout is identical on every monitor.</summary>
    private void Arrange()
    {
        if (_arranging || Width <= 0 || Height <= 0) return;
        _arranging = true;
        try
        {
            int headerHeight = _ui.Px(68);
            _header.SetBounds(0, 0, Width, headerHeight);
            _scroll.SetBounds(0, headerHeight, Width, Height - headerHeight);

            // Lay out as if no scroll bar were needed; if the cards turn out taller than the
            // window, do it once more with the scroll bar's width taken off.
            int needed = ArrangeCards(_scroll.Width);
            if (needed > _scroll.Height)
                needed = ArrangeCards(_scroll.Width - SystemInformation.GetVerticalScrollBarWidthForDpi(DeviceDpi));
            _scroll.AutoScrollMinSize = new Size(0, needed);
        }
        finally { _arranging = false; }
    }

    private int ArrangeCards(int available)
    {
        int margin = _ui.Px(20), gap = _ui.Px(14);
        int width = available - 2 * margin;
        var offset = _scroll.AutoScrollPosition; // negative while scrolled down
        int top = _ui.Px(4);
        _activity.ExtraHeight = 0;

        void Put(Card card, int x, int y, int cardWidth, int height) =>
            card.SetBounds(margin + x + offset.X, y + offset.Y, cardWidth, height);

        if (_problem.Visible)
        {
            int height = _problem.Arrange(width);
            Put(_problem, 0, top, width, height);
            top += height + gap;
        }

        if (width < _ui.Px(840))
        {
            // One column, in the order a new user needs the cards.
            int y = top;
            foreach (Card card in new Card[] { _connect, _devices, _setup, _settings, _activity })
            {
                int height = card.Arrange(width);
                Put(card, 0, y, width, height);
                y += height + gap;
            }
            return y;
        }

        // Two columns: what you use every day on the left, the phone checklist and the
        // activity list on the right. This split keeps both columns about the same height.
        int leftWidth = (width - gap) / 2, rightWidth = width - gap - leftWidth;
        int connect = _connect.Arrange(leftWidth), devices = _devices.Arrange(leftWidth), settings = _settings.Arrange(leftWidth);
        int setup = _setup.Arrange(rightWidth), activity = _activity.Arrange(rightWidth);
        int left = connect + gap + devices + gap + settings, right = setup + gap + activity;
        if (right < left)
        {
            // The activity list takes up the slack, so both columns end on the same line.
            _activity.ExtraHeight = left - right;
            activity = _activity.Arrange(rightWidth);
            right = left;
        }
        Put(_connect, 0, top, leftWidth, connect);
        Put(_devices, 0, top + connect + gap, leftWidth, devices);
        Put(_settings, 0, top + connect + gap + devices + gap, leftWidth, settings);
        Put(_setup, leftWidth + gap, top, rightWidth, setup);
        Put(_activity, leftWidth + gap, top + setup + gap, rightWidth, activity);
        return top + Math.Max(left, right) + gap;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _ui.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>The Abyss Remote window: a dark title bar around a <see cref="MainView"/>. Closing
/// it normally hides it to the tray; the app decides when it really closes.</summary>
internal sealed class MainWindow : Form
{
    private static readonly Size StartSize = new(1100, 940), SmallestSize = new(600, 480); // logical pixels
    private bool _placed, _closeButtonUsed;

    public MainWindow(IWindowActions actions)
    {
        Text = AppInfo.Name;
        Icon = AppIcon.Load();
        BackColor = Palette.Background;
        ForeColor = Palette.Text;
        AutoScaleMode = AutoScaleMode.None; // MainView does its own scaling
        StartPosition = FormStartPosition.Manual;
        View = new MainView(actions, DeviceDpi / 96f) { Dock = DockStyle.Fill };
        Controls.Add(View);
    }

    public MainView View { get; }

    /// <summary>True: the close button hides the window to the tray. False: it quits the app.</summary>
    public bool CloseToTray { get; set; } = true;

    /// <summary>Set when the app is quitting, so the window closes for real.</summary>
    public bool Quitting { get; set; }

    /// <summary>The window was hidden to the tray instead of closed.</summary>
    public event Action? HiddenToTray;

    /// <summary>Shows the window (again) and puts it in front of other windows.</summary>
    public void ShowInFront()
    {
        if (!_placed) PlaceOnScreen();
        if (!Visible) Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
        WindowNative.BringToFront(Handle);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        WindowNative.UseDarkTitleBar(Handle);
        ApplyMonitorScale();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e); // Windows suggests the new size and position; the base class applies it
        ApplyMonitorScale();
    }

    protected override void WndProc(ref Message m)
    {
        // The close button and Alt+F4 arrive as this system command. Only they hide the window
        // to the tray; a close request from outside (Task Manager's "End task", Windows shutting
        // down) must really close it. Windows Forms' own CloseReason cannot tell them apart
        // reliably: it still says "user" after a close that was cancelled.
        if (m.Msg == SystemCommand && ((int)m.WParam & 0xFFF0) == CloseCommand) _closeButtonUsed = true;
        base.WndProc(ref m);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_closeButtonUsed && CloseToTray && !Quitting)
        {
            e.Cancel = true;
            Hide();
            HiddenToTray?.Invoke();
        }
        _closeButtonUsed = false;
        base.OnFormClosing(e);
    }

    private const int SystemCommand = 0x0112, CloseCommand = 0xF060; // WM_SYSCOMMAND, SC_CLOSE

    private void ApplyMonitorScale()
    {
        float scale = DeviceDpi / 96f;
        View.SetScale(scale);
        MinimumSize = SizeFromClientSize(Scaled(SmallestSize, scale));
    }

    /// <summary>The first time the window appears: the default size (smaller if the screen is
    /// small), centred on the monitor with the mouse pointer.</summary>
    private void PlaceOnScreen()
    {
        _placed = true;
        var pointer = Cursor.Position;
        var area = Screen.FromPoint(pointer).WorkingArea;
        // That monitor's scale, not this window's: the window has not been there yet.
        var wanted = SizeFromClientSize(Scaled(StartSize, WindowNative.ScaleAt(pointer)));
        var size = new Size(Math.Min(wanted.Width, area.Width * 94 / 100), Math.Min(wanted.Height, area.Height * 94 / 100));
        Bounds = new Rectangle(area.X + (area.Width - size.Width) / 2, area.Y + (area.Height - size.Height) / 2, size.Width, size.Height);
    }

    private static Size Scaled(Size logical, float scale) => new((int)(logical.Width * scale), (int)(logical.Height * scale));
}

/// <summary>A small yes/no question in the window's colours, for the two things worth asking
/// about: revoking every device, and turning on home-network mode for the first time.</summary>
internal sealed class ConfirmDialog : Form
{
    private ConfirmDialog(string title, string message, string confirm, bool danger, float scale)
    {
        Text = AppInfo.Name;
        Icon = AppIcon.Load();
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Palette.Background;
        StartPosition = FormStartPosition.CenterParent;
        var view = new ConfirmView(title, message, confirm, danger, scale);
        ClientSize = view.Size;
        Controls.Add(view);
        AcceptButton = danger ? view.Cancel : view.Confirm; // Enter never triggers something destructive
        CancelButton = view.Cancel;
        view.Confirm.DialogResult = DialogResult.OK;
        view.Cancel.DialogResult = DialogResult.Cancel;
    }

    /// <summary>Asks and returns true when the user confirmed. The question appears over the
    /// window, or in the middle of the screen while the window is hidden in the tray.</summary>
    public static bool Ask(Form owner, string title, string message, string confirm, bool danger = false)
    {
        using var dialog = new ConfirmDialog(title, message, confirm, danger, owner.DeviceDpi / 96f);
        if (owner.Visible) return dialog.ShowDialog(owner) == DialogResult.OK;
        dialog.StartPosition = FormStartPosition.CenterScreen;
        return dialog.ShowDialog() == DialogResult.OK;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        WindowNative.UseDarkTitleBar(Handle);
    }
}

/// <summary>The content of a <see cref="ConfirmDialog"/>: a headline, a sentence or two, and
/// two buttons. It sizes itself to its text.</summary>
internal sealed class ConfirmView : Control
{
    private readonly Ui _ui;
    private readonly string _title, _message;

    public ConfirmView(string title, string message, string confirm, bool danger, float scale)
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        (_title, _message, _ui) = (title, message, new Ui(scale));
        Confirm = new FlatButton(confirm, danger ? ButtonKind.Danger : ButtonKind.Primary) { Backdrop = Palette.Background, Font = _ui.Bold, UiScale = scale };
        Cancel = new FlatButton("Cancel") { Backdrop = Palette.Background, Font = _ui.Bold, UiScale = scale };
        Controls.Add(Cancel);
        Controls.Add(Confirm);

        int pad = _ui.Px(24), width = _ui.Px(440), textWidth = width - 2 * pad, buttonHeight = _ui.Px(34);
        int y = pad + Ui.MeasureWrapped(title, _ui.Heading, textWidth) + _ui.Px(10) + Ui.MeasureWrapped(message, _ui.Body, textWidth) + _ui.Px(22);
        int confirmWidth = Math.Max(Confirm.WidthFor(_ui), _ui.Px(96)), cancelWidth = Math.Max(Cancel.WidthFor(_ui), _ui.Px(96));
        Confirm.SetBounds(width - pad - confirmWidth, y, confirmWidth, buttonHeight);
        Cancel.SetBounds(Confirm.Left - _ui.Px(10) - cancelWidth, y, cancelWidth, buttonHeight);
        Size = new Size(width, y + buttonHeight + pad);
    }

    public FlatButton Confirm { get; }
    public FlatButton Cancel { get; }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Palette.Background);
        int pad = _ui.Px(24), textWidth = Width - 2 * pad;
        var pass = new Pass(e.Graphics, _ui);
        int y = pad + pass.Text(_title, _ui.Heading, Palette.Text, pad, pad, textWidth) + _ui.Px(10);
        pass.Text(_message, _ui.Body, Palette.Muted, pad, y, textWidth);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _ui.Dispose();
        base.Dispose(disposing);
    }
}
