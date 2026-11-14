namespace AbyssRemote.Host;

/// <summary>Command-line options. Unknown options are errors, so a typo can never silently
/// fall back to a different (possibly less safe) mode.</summary>
public sealed record LaunchOptions(bool Lan, int Port, bool RenewCert, bool SmokeTest, bool ProbeVideo = false)
{
    // 8443 is the most common "alternate HTTPS" port and collided with another local server
    // (a Caddy reverse proxy) on the development PC, so v1.0 moved the default.
    public const int DefaultPort = 8444;

    public const string Usage =
        "Options: --lan, --local, --port <1024-65535>, --renew-cert, --console, --minimized, --smoke-test, --probe-video";

    /// <summary>--local: this PC only for this run, even when home-network mode is saved in the settings.</summary>
    public bool LocalOnly { get; init; }

    /// <summary>True when --port was given. Then <see cref="Port"/> wins over the saved port for this run.</summary>
    public bool PortGiven { get; init; }

    /// <summary>--console: the classic console screen (keys P, R, Q) instead of the window.</summary>
    public bool ConsoleMode { get; init; }

    /// <summary>--minimized: start hidden in the tray. "Start with Windows" uses this.</summary>
    public bool Minimized { get; init; }

    /// <summary>--ui-snapshot &lt;folder&gt;: a developer option that draws the window with sample
    /// data into PNG files, without showing anything on screen, and exits.</summary>
    public string? UiSnapshotFolder { get; init; }

    public static LaunchOptions Parse(IReadOnlyList<string> args)
    {
        bool lan = false, local = false, renewCert = false, smokeTest = false, probeVideo = false;
        bool console = false, minimized = false, portGiven = false;
        string? snapshotFolder = null;
        int port = DefaultPort;
        for (int i = 0; i < args.Count; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--lan": lan = true; break;
                case "--local": local = true; break;
                case "--renew-cert": renewCert = true; break;
                case "--smoke-test": smokeTest = true; break;
                case "--probe-video": probeVideo = true; break;
                case "--console": console = true; break;
                case "--minimized": minimized = true; break;
                case "--port":
                    if (i + 1 >= args.Count || !int.TryParse(args[++i], out port) || port is < 1024 or > 65535)
                        throw new ArgumentException("--port needs a number from 1024 to 65535.");
                    portGiven = true;
                    break;
                case "--ui-snapshot":
                    if (i + 1 >= args.Count || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                        throw new ArgumentException("--ui-snapshot needs a folder for the pictures.");
                    snapshotFolder = args[++i];
                    break;
                default:
                    throw new ArgumentException($"Unknown option '{args[i]}'. {Usage}");
            }
        }
        if (lan && local) throw new ArgumentException("--lan and --local cannot be used together.");
        return new LaunchOptions(lan, port, renewCert, smokeTest, probeVideo)
        {
            LocalOnly = local, PortGiven = portGiven, ConsoleMode = console, Minimized = minimized, UiSnapshotFolder = snapshotFolder,
        };
    }
}
