namespace AbyssRemote.Host;

/// <summary>Command-line options. Unknown options are errors, so a typo can never silently
/// fall back to a different (possibly less safe) mode.</summary>
public sealed record LaunchOptions(bool Lan, int Port, bool RenewCert, bool SmokeTest, bool ProbeVideo = false)
{
    // 8443 is the most common "alternate HTTPS" port and collided with another local server
    // (a Caddy reverse proxy) on the development PC, so v1.0 moved the default.
    public const int DefaultPort = 8444;

    public const string Usage = "Options: --lan, --port <1024-65535>, --renew-cert, --smoke-test, --probe-video";

    public static LaunchOptions Parse(IReadOnlyList<string> args)
    {
        bool lan = false, renewCert = false, smokeTest = false, probeVideo = false;
        int port = DefaultPort;
        for (int i = 0; i < args.Count; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--lan": lan = true; break;
                case "--renew-cert": renewCert = true; break;
                case "--smoke-test": smokeTest = true; break;
                case "--probe-video": probeVideo = true; break;
                case "--port":
                    if (i + 1 >= args.Count || !int.TryParse(args[++i], out port) || port is < 1024 or > 65535)
                        throw new ArgumentException("--port needs a number from 1024 to 65535.");
                    break;
                default:
                    throw new ArgumentException($"Unknown option '{args[i]}'. {Usage}");
            }
        }
        return new LaunchOptions(lan, port, renewCert, smokeTest, probeVideo);
    }
}
