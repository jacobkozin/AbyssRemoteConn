namespace AbyssRemote.Host.App;

/// <summary>The paired devices: name, when it was paired and last seen, a green dot while it
/// is connected, and a way to take its access away.</summary>
internal sealed class DevicesCard : Card
{
    private readonly FlatButton _revokeAll;
    private readonly List<FlatButton> _revokeButtons = new();
    private IReadOnlyList<PairedDevice> _devices = [];
    private IReadOnlySet<string> _connected = new HashSet<string>();
    private string _shown = "";
    private DateTimeOffset _now;

    public DevicesCard(IWindowActions actions) : base("Devices", actions)
    {
        _revokeAll = Add(new FlatButton("Revoke all", ButtonKind.Danger));
        _revokeAll.Click += (_, _) => Actions.RevokeAllDevices();
    }

    /// <summary>Returns true when the number of rows changed, so the window lays itself out again.</summary>
    public bool Show(IReadOnlyList<PairedDevice> devices, IReadOnlySet<string> connected, DateTimeOffset now)
    {
        bool rowsChanged = devices.Count != _devices.Count;
        (_devices, _connected, _now) = (devices, connected, now);

        // One "Revoke" button per row; each remembers its device.
        while (_revokeButtons.Count < devices.Count)
        {
            var button = Add(new FlatButton("Revoke") { Font = Ui.Bold, UiScale = Ui.Scale });
            button.Click += (_, _) => { if (button.Tag is PairedDevice device) Actions.RevokeDevice(device); };
            _revokeButtons.Add(button);
        }
        while (_revokeButtons.Count > devices.Count)
        {
            var extra = _revokeButtons[^1];
            _revokeButtons.RemoveAt(_revokeButtons.Count - 1);
            Controls.Remove(extra);
            extra.Dispose();
        }
        for (int i = 0; i < devices.Count; i++) _revokeButtons[i].Tag = devices[i];

        // Redraw only when a visible word changed ("5 minutes ago" becomes "6 minutes ago").
        string shown = string.Join("|", devices.Select(d => $"{d.Id}:{d.Name}:{Detail(d)}"));
        if (shown != _shown || rowsChanged) Invalidate();
        _shown = shown;
        return rowsChanged;
    }

    private string Detail(PairedDevice device) =>
        (_connected.Contains(device.Id) ? "Connected now" : $"Last seen {RelativeTime.Ago(device.LastSeenUtc, _now)}")
        + $"  ·  paired {RelativeTime.Ago(device.CreatedUtc, _now)}";

    protected override int Compose(Pass pass, int width)
    {
        int revokeAllWidth = _revokeAll.WidthFor(Ui), buttonHeight = Ui.Px(30);
        _revokeAll.Visible = _devices.Count > 0;
        int y = Header(pass, width, _revokeAll.Visible ? revokeAllWidth + Ui.Px(12) : 0);
        pass.Place(_revokeAll, width - Pad - revokeAllWidth, Pad - Ui.Px(4), revokeAllWidth, buttonHeight);
        if (_devices.Count == 0)
            return y + pass.Text("No devices are paired yet. Open the address on a device and type in the pairing code.",
                Ui.Body, Palette.Muted, Pad, y, width - 2 * Pad) + Pad;

        int rowHeight = Ui.Px(50), dot = Ui.Px(10), revokeWidth = _revokeButtons[0].WidthFor(Ui);
        int textX = Pad + dot + Ui.Px(12), textWidth = width - Pad - revokeWidth - Ui.Px(12) - textX;
        y -= Ui.Px(4);
        for (int i = 0; i < _devices.Count; i++)
        {
            var device = _devices[i];
            bool connected = _connected.Contains(device.Id);
            if (pass.Graphics is { } g)
            {
                if (i > 0)
                {
                    using var line = new Pen(Palette.Border);
                    g.DrawLine(line, Pad, y, width - Pad, y);
                }
                var circle = new Rectangle(Pad, y + (rowHeight - dot) / 2, dot, dot);
                if (connected) Ui.FillCircle(g, circle, Palette.Success);
                else Ui.FillRounded(g, circle, dot / 2f, Palette.Surface, Palette.Muted); // a hollow ring: paired, not connected
            }
            int nameHeight = Ui.MeasureLine("Ag", Ui.Bold).Height, detailHeight = Ui.MeasureLine("Ag", Ui.Small).Height;
            int textY = y + (rowHeight - nameHeight - detailHeight - Ui.Px(2)) / 2;
            pass.Line(device.Name, Ui.Bold, Palette.Text, textX, textY, textWidth);
            pass.Line(Detail(device), Ui.Small, connected ? Palette.Success : Palette.Muted, textX, textY + nameHeight + Ui.Px(2), textWidth);
            pass.Place(_revokeButtons[i], width - Pad - revokeWidth, y + (rowHeight - buttonHeight) / 2, revokeWidth, buttonHeight);
            y += rowHeight;
        }
        return y + Pad - Ui.Px(8);
    }
}

/// <summary>Turns a time into words like "5 minutes ago".</summary>
public static class RelativeTime
{
    public static string Ago(DateTimeOffset then, DateTimeOffset now)
    {
        var span = now - then;
        if (span < TimeSpan.FromMinutes(1)) return "just now";
        if (span < TimeSpan.FromHours(1)) return Count((int)span.TotalMinutes, "minute");
        if (span < TimeSpan.FromDays(1)) return Count((int)span.TotalHours, "hour");
        if (span < TimeSpan.FromDays(2)) return "yesterday";
        return Count((int)span.TotalDays, "day");
    }

    private static string Count(int number, string unit) => $"{number} {unit}{(number == 1 ? "" : "s")} ago";
}
