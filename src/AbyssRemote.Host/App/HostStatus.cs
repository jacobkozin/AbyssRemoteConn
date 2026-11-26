namespace AbyssRemote.Host.App;

/// <summary>How one run of the server is set up. Changing a setting in the window restarts the
/// server with new options.</summary>
/// <param name="Lan">False: this PC only (loopback). True: devices on the home network too.</param>
/// <param name="RenewCertificate">Issue a fresh server certificate when starting.</param>
/// <param name="AwayHost">The away address when away access is on, otherwise null.</param>
public sealed record HostRunOptions(bool Lan, int Port, bool RenewCertificate = false, string? AwayHost = null)
{
    /// <summary>What the command line asks for wins for this run; everything else comes from
    /// the saved settings. Away access needs the setting and a usable address; the away
    /// address reaches this PC through loopback, so it works in both network modes.</summary>
    public static HostRunOptions From(LaunchOptions commandLine, Preferences saved) => new(
        Lan: commandLine.Lan || (!commandLine.LocalOnly && saved.Lan),
        Port: commandLine.PortGiven ? commandLine.Port : saved.Port,
        RenewCertificate: commandLine.RenewCert,
        AwayHost: saved.Away && AwayAccess.IsValidHost(saved.AwayHost) ? saved.AwayHost.Trim().ToLowerInvariant() : null);
}

public enum HostPhase { Stopped, Starting, Running, Failed }

public enum StartProblemKind { PortInUse, PortNotAllowed, StateFile, WebUiMissing, Certificates, Other }

/// <summary>Why the server could not start, as a sentence the user can act on.</summary>
public sealed record StartProblem(StartProblemKind Kind, string Message);

/// <summary>Facts that only exist while the server is running.</summary>
/// <param name="Port">The real port, also when port 0 ("any free port") was asked for.</param>
/// <param name="LocalUrl">The address for a browser on this PC.</param>
/// <param name="LanUrls">Addresses for other devices, the best one first. Empty in this-PC-only mode.</param>
/// <param name="ServerFingerprint">SHA-256 of the server certificate, as hex.</param>
/// <param name="RootFingerprint">SHA-256 of the root certificate, as hex. Phones show the same value.</param>
/// <param name="RootCertificate">The root certificate's public part (DER), ready to save as a .cer file.</param>
/// <param name="RootCertificateFile">Where the certificate source keeps a copy of that file.</param>
/// <param name="AwayUrl">The away address while away access is on, otherwise null.</param>
public sealed record RunningHost(
    int Port,
    bool Lan,
    string LocalUrl,
    IReadOnlyList<string> LanUrls,
    string ServerFingerprint,
    string RootFingerprint,
    byte[] RootCertificate,
    string RootCertificateFile,
    string? AwayUrl = null)
{
    /// <summary>The address to give to another device: the best network address in
    /// home-network mode, otherwise the one for this PC.</summary>
    public string MainUrl => LanUrls.Count > 0 ? LanUrls[0] : LocalUrl;

    /// <summary>The address to show first, to copy and to put into the QR code. While the away
    /// address works it is that one: it works at home and away, and a device needs no
    /// certificate for it. Otherwise it is <see cref="MainUrl"/>.</summary>
    public string AddressToGiveOut(AwayVerdict away) => away == AwayVerdict.Working && AwayUrl is not null ? AwayUrl : MainUrl;

    /// <summary>Where a phone can download the root certificate, or null in this-PC-only mode.</summary>
    public string? RootCertificateUrl => LanUrls.Count > 0 ? LanUrls[0] + "/AbyssRemote-RootCA.cer" : null;
}

/// <summary>One immutable picture of what the server is doing. The window and the console read
/// it from any thread.</summary>
public sealed record HostStatus(HostPhase Phase, HostRunOptions Options, RunningHost? Running = null, StartProblem? Problem = null)
{
    /// <summary>The one-line status for the window's header and the tray tooltip.</summary>
    public string Summary => Phase switch
    {
        HostPhase.Running => Running!.Lan ? "Running: home network" : "Running: this PC only",
        HostPhase.Starting => "Starting…",
        HostPhase.Failed => Problem!.Message.TrimEnd('.'),
        _ => "Stopped",
    };
}

public enum PairingCodeState { None, Active, Used, Locked }

/// <summary>The one-time pairing code as the PC shows it. The digits are only kept while the
/// code can still be used.</summary>
public sealed record PairingCode(PairingCodeState State, string Digits, DateTimeOffset IssuedUtc, DateTimeOffset ExpiresUtc)
{
    public static readonly PairingCode None = new(PairingCodeState.None, "", default, default);

    public bool IsUsable(DateTimeOffset now) => State == PairingCodeState.Active && now < ExpiresUtc;

    public TimeSpan Remaining(DateTimeOffset now) => IsUsable(now) ? ExpiresUtc - now : TimeSpan.Zero;

    /// <summary>"1234 5678": two groups are easier to read across a room and to type.</summary>
    public string Grouped => Digits.Length == 8 ? $"{Digits[..4]} {Digits[4..]}" : Digits;

    /// <summary>What the QR code holds: the address plus the code in the fragment. A fragment
    /// never travels to the server, so the code stays out of every request line; the browser
    /// client reads it and fills in the pairing form.</summary>
    public string PairingUrl(string baseUrl, DateTimeOffset now) =>
        IsUsable(now) ? $"{baseUrl}/#pair={Digits}" : baseUrl + "/";
}

/// <summary>A paired device, as the window lists it (the same fields /api/v1/devices returns).
/// <paramref name="Away"/>: it is set up for away access.</summary>
public sealed record PairedDevice(string Id, string Name, DateTimeOffset CreatedUtc, DateTimeOffset LastSeenUtc, DateTimeOffset ExpiresUtc, bool Away = false);

/// <summary>One line of the activity list: a pairing, a revocation, a refused connection.</summary>
public sealed record NoticeLine(DateTimeOffset Time, string Message);
