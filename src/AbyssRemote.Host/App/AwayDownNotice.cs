namespace AbyssRemote.Host.App;

/// <summary>Decides when the tray icon says that the away address does not work. To a phone that
/// address simply does not load, and the window is usually closed, so nothing else would say
/// it. It waits a moment first, in case the other program is only still starting (after a
/// restart of the PC, say), and it speaks once per outage.</summary>
internal sealed class AwayDownNotice
{
    public static readonly TimeSpan Delay = TimeSpan.FromSeconds(45);
    public const string Title = "Away from home is not working";

    private DateTimeOffset? _due;
    private bool _said;

    /// <summary>The verdict changed. <paramref name="awayOn"/>: away access is turned on.</summary>
    public void Changed(AwayVerdict away, bool awayOn, DateTimeOffset now)
    {
        if (Down(away))
        {
            if (!_said) _due ??= now + Delay;
        }
        else if (away == AwayVerdict.Working || !awayOn)
        {
            _due = null;
            _said = false;
        }
        // Unknown with away access on: the server is restarting. What was pending stays so.
    }

    /// <summary>Asked once a second. True exactly once per outage, when the address has stayed
    /// down for <see cref="Delay"/>.</summary>
    public bool ShouldSpeak(AwayVerdict away, DateTimeOffset now)
    {
        if (_due is not { } due || now < due) return false;
        _due = null;
        if (!Down(away)) return false;
        _said = true;
        return true;
    }

    public static string Text(AwayVerdict away) => away == AwayVerdict.NotAnswering
        ? "The web server on this PC that forwards your away address is not running. Devices only work at home until it is started."
        : "Another program on this PC answers at your away address, not Abyss Remote. Devices only work at home until that is fixed.";

    private static bool Down(AwayVerdict away) => away is AwayVerdict.NotAnswering or AwayVerdict.SomethingElse;
}
