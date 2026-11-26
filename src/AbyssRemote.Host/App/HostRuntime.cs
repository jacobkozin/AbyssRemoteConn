using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.AspNetCore.Connections;

namespace AbyssRemote.Host.App;

/// <summary>The running Abyss Remote server and everything around it: the paired devices, the
/// certificates, the pairing code and the live connections. It can start, stop and restart with
/// different options, and it raises an event whenever something a person would want to see
/// changes. The window and the console screen are both thin layers on top of this class.
///
/// Events arrive on whatever thread caused them (a request, a timer, the caller), so a window
/// has to hand them over to its own thread. Each event has a matching property that holds the
/// current value.</summary>
public sealed class HostRuntime : IAsyncDisposable
{
    public const int MaxNotices = 500;
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan AwayCheckInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan AwayAskInterval = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(3);
    private static readonly JsonSerializerOptions DeviceJson = new() { PropertyNameCaseInsensitive = true };

    private readonly HostEnvironment _env;
    private readonly PairingManager _pairing;
    private readonly SessionRegistry _sessions = new();
    private readonly SemaphoreSlim _startStop = new(1, 1); // one start, stop or restart at a time
    private readonly object _sync = new();                 // guards the fields below
    private readonly object _refreshing = new();
    private readonly List<NoticeLine> _notices = new();
    private volatile StateStore? _state;
    private volatile AwayAccess? _away; // while the server runs with away access on
    private WebApplication? _app;
    private HostCertificateSet? _certificates;
    private System.Threading.Timer? _refreshTimer, _awayTimer;
    private AwayVerdict _awayVerdict;
    private DateTimeOffset _awayAsked; // the last time the away address was asked through the proxy
    private HostStatus _status = new(HostPhase.Stopped, new HostRunOptions(Lan: false, LaunchOptions.DefaultPort));
    private PairingCode _code = PairingCode.None;
    private IReadOnlyList<PairedDevice> _devices = [];
    private IReadOnlySet<string> _connected = new HashSet<string>();
    private bool _devicesLoaded;

    public HostRuntime(HostEnvironment environment)
    {
        _env = environment;
        _pairing = new PairingManager(environment.Clock);
        _pairing.CodeLocked += OnCodeLocked;
    }

    /// <summary>Starting, running, stopped, or failed with the reason.</summary>
    public event Action? StatusChanged;
    /// <summary>A new line for the activity list.</summary>
    public event Action<NoticeLine>? NoticeAdded;
    /// <summary>A new pairing code was made, or the current one was used or locked.</summary>
    public event Action? PairingCodeChanged;
    /// <summary>A device was paired, revoked or expired, or was seen again.</summary>
    public event Action? DevicesChanged;
    /// <summary>A paired device connected or disconnected.</summary>
    public event Action? ConnectionsChanged;
    /// <summary>A device finished pairing just now.</summary>
    public event Action<PairedDevice>? DevicePaired;
    /// <summary>The away address started or stopped being forwarded here.</summary>
    public event Action? AwayChanged;

    public HostStatus Status { get { lock (_sync) return _status; } }
    public PairingCode PairingCode { get { lock (_sync) return _code; } }
    public IReadOnlyList<PairedDevice> Devices { get { lock (_sync) return _devices; } }
    public IReadOnlySet<string> ConnectedDeviceIds { get { lock (_sync) return _connected; } }
    public IReadOnlyList<NoticeLine> Notices { get { lock (_sync) return _notices.ToList(); } }

    /// <summary>Whether the away address leads here right now. Unknown while away access is
    /// off, and for the first moment after a start.</summary>
    public AwayVerdict Away { get { lock (_sync) return _awayVerdict; } }

    /// <summary>Starts the server. The things that go wrong on a real PC (the port is taken, the
    /// settings file is damaged, the certificate store refuses) do not throw: the returned
    /// status is Failed and carries a sentence for the user.</summary>
    public async Task<HostStatus> StartAsync(HostRunOptions options)
    {
        await _startStop.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Status.Phase == HostPhase.Running) throw new InvalidOperationException("The server is already running. Use RestartAsync to change its options.");
            // Task.Run: opening the certificate store and binding the port must not block a window.
            return await Task.Run(() => StartCoreAsync(options)).ConfigureAwait(false);
        }
        finally { _startStop.Release(); }
    }

    public async Task StopAsync()
    {
        await _startStop.WaitAsync().ConfigureAwait(false);
        try
        {
            await Task.Run(StopCoreAsync).ConfigureAwait(false);
            SetStatus(new HostStatus(HostPhase.Stopped, Status.Options));
        }
        finally { _startStop.Release(); }
    }

    /// <summary>Stops the server and starts it again with new options (another port, or
    /// home-network mode on or off). Paired devices and the pairing code stay as they are.</summary>
    public async Task<HostStatus> RestartAsync(HostRunOptions options)
    {
        await _startStop.WaitAsync().ConfigureAwait(false);
        try
        {
            return await Task.Run(async () =>
            {
                await StopCoreAsync().ConfigureAwait(false);
                return await StartCoreAsync(options).ConfigureAwait(false);
            }).ConfigureAwait(false);
        }
        finally { _startStop.Release(); }
    }

    /// <summary>Makes a new one-time pairing code; the previous one stops working.</summary>
    public PairingCode NewPairingCode()
    {
        var now = _env.Clock.GetUtcNow();
        var code = new PairingCode(PairingCodeState.Active, _pairing.Rotate(), now, now + PairingManager.CodeLifetime);
        lock (_sync) _code = code;
        PairingCodeChanged?.Invoke();
        return code;
    }

    /// <summary>Checks now whether the away address is forwarded to this program, and says so
    /// in the activity list when that changed. It runs by itself every half minute while away
    /// access is on, because the proxy is another program that can be closed at any time.
    /// Those rounds are not <paramref name="thorough"/>: while the address works they only
    /// look whether the proxy still listens, and ask through it every ten minutes, so the
    /// proxy's access log does not fill with this program's own visits.</summary>
    public async Task CheckAwayAsync(bool thorough = true)
    {
        if (_away is not { } away) return;
        var now = _env.Clock.GetUtcNow();
        bool quick;
        lock (_sync) quick = !thorough && _awayVerdict == AwayVerdict.Working && now - _awayAsked < AwayAskInterval;
        AwayVerdict verdict;
        try
        {
            verdict = !quick ? await _env.CheckAway(away.Host).ConfigureAwait(false)
                : await _env.AwayListening().ConfigureAwait(false) ? AwayVerdict.Working : AwayVerdict.NotAnswering;
        }
        catch (Exception) { verdict = AwayVerdict.NotAnswering; }
        if (!ReferenceEquals(_away, away)) return; // restarted or stopped meanwhile
        if (!quick) lock (_sync) _awayAsked = now;
        SetAway(verdict, away.Url);
    }

    private void SetAway(AwayVerdict verdict, string? url)
    {
        AwayVerdict before;
        lock (_sync)
        {
            before = _awayVerdict;
            _awayVerdict = verdict;
        }
        if (before == verdict) return;
        if (verdict == AwayVerdict.Working) AddNotice($"The away address works: {url} leads to Abyss Remote.");
        else if (verdict == AwayVerdict.NotAnswering) AddNotice($"The away address {url} does not answer: the web server on this PC that forwards it is not running. Devices only work at home until it is started.");
        else if (verdict == AwayVerdict.SomethingElse) AddNotice($"The away address {url} is answered by something else on this PC, not by Abyss Remote. Check the web server that should forward it.");
        AwayChanged?.Invoke();
    }

    /// <summary>Makes the current pairing code useless, so nothing can be paired until someone
    /// asks for a new one.</summary>
    public void DropPairingCode()
    {
        _pairing.Clear();
        lock (_sync) _code = PairingCode.None;
        PairingCodeChanged?.Invoke();
    }

    /// <summary>Removes one paired device and closes its connections at once.</summary>
    public bool RevokeDevice(string id)
    {
        if (_state is not { } state) return false;
        string name = Devices.FirstOrDefault(d => d.Id == id)?.Name ?? "a device";
        if (!state.Revoke(id)) return false;
        _sessions.CloseDevice(id);
        AddNotice($"Revoked \"{name}\" and closed its connections.");
        RefreshDevices();
        return true;
    }

    /// <summary>Removes every paired device and closes all connections. Returns how many there were.</summary>
    public int RevokeAllDevices()
    {
        if (_state is not { } state) return 0;
        int count = state.RevokeAll();
        _sessions.CloseAll();
        AddNotice($"Revoked {count} paired device(s) and closed their connections.");
        RefreshDevices();
        return count;
    }

    /// <summary>Adds a line to the activity list.</summary>
    public void AddNotice(string message)
    {
        var line = new NoticeLine(_env.Clock.GetLocalNow(), message);
        lock (_sync)
        {
            _notices.Add(line);
            if (_notices.Count > MaxNotices) _notices.RemoveAt(0);
        }
        NoticeAdded?.Invoke(line);
    }

    /// <summary>Reads the paired devices and the live connections again and raises the events
    /// for whatever changed. The server pairs and revokes devices on its own threads without
    /// telling anyone, so this runs once a second while the server is up, and right after
    /// every notice.</summary>
    public void RefreshDevices()
    {
        if (_state is not { } state) return;
        lock (_refreshing) // one reading at a time, so two threads cannot report the same change twice
        {
            var devices = JsonSerializer.Deserialize<List<PairedDevice>>(JsonSerializer.SerializeToUtf8Bytes(state.Devices()), DeviceJson) ?? [];
            var connected = _sessions.ConnectedDeviceIds();
            bool devicesChanged, connectionsChanged, codeUsed = false;
            List<PairedDevice> paired = [];
            lock (_sync)
            {
                devicesChanged = !devices.SequenceEqual(_devices);
                connectionsChanged = !connected.SetEquals(_connected);
                // The first reading is the list from disk, not devices that paired just now.
                if (_devicesLoaded) paired = devices.Where(d => _devices.All(known => known.Id != d.Id)).ToList();
                _devices = devices;
                _connected = connected;
                _devicesLoaded = true;
                // A code works once. A device that appeared after the code was made used it up.
                if (_code.State == PairingCodeState.Active && paired.Any(d => d.CreatedUtc >= _code.IssuedUtc))
                {
                    _code = _code with { State = PairingCodeState.Used, Digits = "" };
                    codeUsed = true;
                }
            }
            if (devicesChanged) DevicesChanged?.Invoke();
            if (connectionsChanged) ConnectionsChanged?.Invoke();
            if (codeUsed) PairingCodeChanged?.Invoke();
            foreach (var device in paired) DevicePaired?.Invoke(device);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _pairing.CodeLocked -= OnCodeLocked;
    }

    private async Task<HostStatus> StartCoreAsync(HostRunOptions options)
    {
        SetStatus(new HostStatus(HostPhase.Starting, options));
        WebApplication? app = null;
        HostCertificateSet? certificates = null;
        try
        {
            if (!File.Exists(Path.Combine(_env.WebRoot, "index.html")))
                throw new StartProblemException(StartProblemKind.WebUiMissing,
                    "The browser UI (wwwroot) is missing next to the program. Run BUILD-AND-TEST.bat to rebuild.");
            try { _state ??= new StateStore(_env.DataDirectory, _env.Clock); }
            catch (InvalidOperationException ex) { throw new StartProblemException(StartProblemKind.StateFile, ex.Message); }
            int expired = _state.PruneExpired();
            // Away access off means no device may keep an away key, even if it is turned on again later.
            var away = options.AwayHost is { } awayHost ? new AwayAccess(awayHost, _env.Clock) : null;
            int awayKeysRemoved = away is null ? _state.ClearAwayKeys() : 0;
            certificates = _env.Certificates.GetOrCreate(_state, _env.DataDirectory, options.RenewCertificate, options.Lan);

            // The names and addresses this PC really answers to (see RequestProtection.KnownHost).
            var addresses = _env.NetworkAddresses();
            var hosts = new List<string> { "localhost", "127.0.0.1" };
            if (options.Lan)
            {
                hosts.Add(Environment.MachineName);
                hosts.AddRange(addresses.Select(a => a.ToString()));
            }

            app = AbyssServer.Build(new ServerContext
            {
                Options = new LaunchOptions(options.Lan, options.Port, options.RenewCertificate, SmokeTest: false),
                Certificate = certificates.Server,
                RootCertificate = certificates.Root,
                State = _state,
                Pairing = _pairing,
                Protection = new RequestProtection(hosts),
                Away = away,
                AwayWorks = () => Away == AwayVerdict.Working,
                Sessions = _sessions,
                Desktop = _env.Desktop,
                Clipboard = _env.Clipboard,
                WebRoot = _env.WebRoot,
                Transfer = _env.Transfer,
                Actions = _env.Actions,
                Steam = _env.Steam,
                Notice = ServerNotice,
                ConsoleLogging = _env.ConsoleLogging,
            });
            await app.StartAsync().ConfigureAwait(false);

            int port = new Uri(app.Urls.First()).Port; // the real port when "any free port" (0) was asked for
            var running = new RunningHost(
                Port: port,
                Lan: options.Lan,
                LocalUrl: $"https://127.0.0.1:{port}",
                LanUrls: options.Lan ? addresses.Where(NetworkGuard.IsLocalNetwork).Select(a => $"https://{a}:{port}").ToList() : [],
                ServerFingerprint: certificates.Server.GetCertHashString(HashAlgorithmName.SHA256),
                RootFingerprint: certificates.Root.GetCertHashString(HashAlgorithmName.SHA256),
                RootCertificate: certificates.Root.Export(X509ContentType.Cert),
                RootCertificateFile: Path.Combine(_env.DataDirectory, "AbyssRemote-RootCA.cer"),
                AwayUrl: away?.Url);
            lock (_sync)
            {
                _app = app;
                _certificates = certificates;
            }
            _away = away;
            // Ctrl+C in a console stops the web server behind our back; follow it.
            var started = app;
            app.Lifetime.ApplicationStopping.Register(() => { if (IsCurrent(started)) _ = Task.Run(StopAsync); });
            var status = new HostStatus(HostPhase.Running, options, running);
            SetStatus(status);
            if (certificates.Notice is not null) AddNotice(certificates.Notice);
            if (expired > 0) AddNotice($"Removed {expired} device(s) whose 30-day pairing expired.");
            if (awayKeysRemoved > 0) AddNotice($"Away access is off, so {awayKeysRemoved} device(s) lost their away setup.");
            if (away is not null) AddNotice($"Away access is on: devices can pair at {away.Url} and use it at home and away.");
            RefreshDevices();
            _refreshTimer = new System.Threading.Timer(_ => RefreshDevices(), null, RefreshInterval, RefreshInterval);
            if (away is not null)
            {
                _awayTimer = new System.Threading.Timer(_ => _ = CheckAwayAsync(thorough: false), null, AwayCheckInterval, AwayCheckInterval);
                _ = CheckAwayAsync(); // the first look starts now and does not hold the start up
            }
            return status;
        }
        catch (Exception ex)
        {
            if (app is not null) await app.DisposeAsync().ConfigureAwait(false);
            certificates?.Server.Dispose();
            certificates?.Root.Dispose();
            var status = new HostStatus(HostPhase.Failed, options, Problem: StartProblems.Describe(ex, options.Port));
            SetStatus(status);
            RefreshDevices(); // the paired devices can still be listed and revoked
            return status;
        }
    }

    private async Task StopCoreAsync()
    {
        WebApplication? app;
        HostCertificateSet? certificates;
        lock (_sync)
        {
            app = _app;
            certificates = _certificates;
            _app = null;
            _certificates = null;
        }
        _refreshTimer?.Dispose();
        _refreshTimer = null;
        _awayTimer?.Dispose();
        _awayTimer = null;
        _away = null; // its codes go with it
        lock (_sync) _awayAsked = default;
        SetAway(AwayVerdict.Unknown, null);
        if (app is null) return;

        // Tell browsers to reconnect, then give their connections a moment to close. Whatever
        // is still open when the time is up gets cut off, so a restart never hangs on a phone
        // that went to sleep.
        _sessions.CloseAllForRestart();
        using (var timeout = new CancellationTokenSource(StopTimeout))
        {
            try { await app.StopAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        await app.DisposeAsync().ConfigureAwait(false);
        certificates?.Server.Dispose();
        certificates?.Root.Dispose();
        RefreshDevices();
    }

    private bool IsCurrent(WebApplication app)
    {
        lock (_sync) return ReferenceEquals(_app, app);
    }

    private void ServerNotice(string message)
    {
        AddNotice(message);
        RefreshDevices(); // most notices mean a device was paired or removed
    }

    private void OnCodeLocked()
    {
        lock (_sync) _code = _code with { State = PairingCodeState.Locked, Digits = "" };
        PairingCodeChanged?.Invoke();
        AddNotice("The pairing code was turned off after too many wrong guesses. Make a new one when you want to pair a device.");
    }

    private void SetStatus(HostStatus status)
    {
        lock (_sync) _status = status;
        StatusChanged?.Invoke();
    }
}

/// <summary>A start-up failure that already comes with a sentence for the user.</summary>
public sealed class StartProblemException(StartProblemKind kind, string message) : Exception(message)
{
    public StartProblemKind Kind { get; } = kind;
}

public static class StartProblems
{
    /// <summary>Turns whatever stopped the server from starting into a sentence the user can
    /// act on. Kestrel wraps the real cause (for example "address already in use") in other
    /// exceptions, so the whole chain is searched.</summary>
    public static StartProblem Describe(Exception error, int port)
    {
        for (Exception? ex = error; ex is not null; ex = ex.InnerException)
        {
            switch (ex)
            {
                case StartProblemException known:
                    return new StartProblem(known.Kind, known.Message);
                case AddressInUseException or SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse }:
                    return new StartProblem(StartProblemKind.PortInUse, $"Port {port} is already used by another program.");
                case SocketException { SocketErrorCode: SocketError.AccessDenied }:
                    return new StartProblem(StartProblemKind.PortNotAllowed, $"Windows does not let programs use port {port}. Choose another port.");
                case CryptographicException:
                    return new StartProblem(StartProblemKind.Certificates, $"Windows could not open or create the Abyss Remote certificates: {ex.Message}");
            }
        }
        return new StartProblem(StartProblemKind.Other, $"Abyss Remote could not start: {error.Message}");
    }
}
