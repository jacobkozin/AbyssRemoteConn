using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using AbyssRemote.Host;
using Microsoft.AspNetCore.Builder;

namespace AbyssRemote.Tests;

/// <summary>A clock the tests move forward by hand.</summary>
public sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>Stands in for the Windows desktop and records everything that reaches it.</summary>
public sealed class FakeDesktop : IDesktop
{
    public static readonly byte[] FakeJpeg = [0xFF, 0xD8, 0xFF, 0xD9];
    private readonly List<string> _commands = new();
    private int _sinksOpen;

    /// <summary>What Capture returns (argument: forceFullFrame). Default: a frame every time.</summary>
    public Func<bool, byte[]?> OnCapture { get; set; } = _ => FakeJpeg;

    public IReadOnlyList<string> Commands { get { lock (_commands) return _commands.ToList(); } }
    public int SinksOpen => Volatile.Read(ref _sinksOpen);

    public IReadOnlyList<DisplayInfo> Displays() => [new DisplayInfo(0, @"\\.\FAKE1", 0, 0, 1920, 1080, true)];

    public IFrameSource CreateFrameSource(int display, int maxWidth, int quality) => new Source(this);

    public IInputSink CreateInputSink(Action<object> notify)
    {
        Interlocked.Increment(ref _sinksOpen);
        return new Sink(this);
    }

    /// <summary>What Game mode creates. Default: a fake encoder that emits a frame per credit.</summary>
    public Func<VideoSettings, IVideoSource> OnCreateVideo { get; set; } = _ => new FakeVideoSource();
    public FakeVideoSource? LastVideo { get; private set; }

    public IVideoSource CreateVideoSource(VideoSettings settings)
    {
        var source = OnCreateVideo(settings);
        LastVideo = source as FakeVideoSource;
        return source;
    }

    public bool HardwareVideoAvailable => true;

    private sealed class Source(FakeDesktop owner) : IFrameSource
    {
        public byte[]? Capture(bool forceFullFrame) => owner.OnCapture(forceFullFrame);
        public void Dispose() { }
    }

    private sealed class Sink(FakeDesktop owner) : IInputSink
    {
        public void Handle(JsonElement command) { lock (owner._commands) owner._commands.Add(command.GetRawText()); }
        public void Dispose() => Interlocked.Decrement(ref owner._sinksOpen);
    }
}

/// <summary>A pretend hardware encoder: every credit the server grants immediately produces one
/// frame (as if the screen were always changing), so tests can observe the flow control.</summary>
public sealed class FakeVideoSource : IVideoSource
{
    private readonly System.Threading.Channels.Channel<VideoEvent> _events = System.Threading.Channels.Channel.CreateUnbounded<VideoEvent>();
    private readonly object _sync = new();
    private bool _configSent, _keyframeNext = true;
    private long _frames;

    public long FramesProduced { get { lock (_sync) return _frames; } }
    public int KeyframeRequests { get; private set; }
    public bool Disposed { get; private set; }

    public System.Threading.Channels.ChannelReader<VideoEvent> Events => _events.Reader;

    public void AllowFrames(int count)
    {
        lock (_sync)
        {
            for (int i = 0; i < count; i++)
            {
                if (!_configSent)
                {
                    _events.Writer.TryWrite(new VideoConfig("avc1.64002a", [1, 0x64, 0, 0x2A], 1280, 720, 1920, 1080, "Fake encoder"));
                    _configSent = true;
                }
                bool key = _keyframeNext;
                _keyframeNext = false;
                _frames++;
                _events.Writer.TryWrite(new EncodedVideoFrame(_frames, key, (uint)(_frames * 16), [0, 0, 0, 1, key ? (byte)0x65 : (byte)0x41, 0xAA], 1.5));
            }
        }
    }

    public void RequestKeyframe()
    {
        lock (_sync) { KeyframeRequests++; _keyframeNext = true; }
    }

    public void Dispose()
    {
        Disposed = true;
        _events.Writer.TryComplete();
    }
}

public sealed class FakeClipboard : IClipboard
{
    public string Text { get; set; } = "";
    public string Read() => Text;
    public void Write(string text) => Text = text;
}

/// <summary>The real web server (real Kestrel, TLS, middleware and endpoints) on a free
/// loopback port, with a throwaway certificate, a temporary state folder and fake desktop.</summary>
public sealed class TestServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly X509Certificate2 _certificate;
    private readonly string _dataDir;
    private readonly List<string> _notices = new();

    public FakeDesktop Desktop { get; } = new();
    public FakeClipboard Clipboard { get; } = new();
    public PairingManager Pairing { get; } = new();
    public SessionRegistry Sessions { get; } = new();
    public StateStore State { get; }
    public Uri BaseUri { get; private set; } = null!;
    public string Origin => $"https://{BaseUri.Authority}";
    public IReadOnlyList<string> Notices { get { lock (_notices) return _notices.ToList(); } }

    private TestServer()
    {
        _dataDir = Directory.CreateTempSubdirectory("abyss-remote-test-").FullName;
        State = new StateStore(_dataDir);
        _certificate = CreateCertificate();
        _app = AbyssServer.Build(new ServerContext
        {
            Options = new LaunchOptions(Lan: false, Port: 0, RenewCert: false, SmokeTest: false),
            Certificate = _certificate,
            State = State,
            Pairing = Pairing,
            Protection = new RequestProtection(["127.0.0.1", "localhost"]),
            Sessions = Sessions,
            Desktop = Desktop,
            Clipboard = Clipboard,
            WebRoot = FindWebRoot(),
            Notice = message => { lock (_notices) _notices.Add(message); },
            ConsoleLogging = false,
        });
    }

    public static async Task<TestServer> StartAsync()
    {
        var server = new TestServer();
        await server._app.StartAsync();
        server.BaseUri = new Uri(server._app.Urls.First());
        return server;
    }

    public bool Trusts(X509Certificate? presented) =>
        presented is not null && presented.GetCertHashString() == _certificate.GetCertHashString();

    public TestClient CreateClient() => new(this);

    /// <summary>Pairs a new browser exactly like the UI does and loads its CSRF token.</summary>
    public async Task<TestClient> PairAsync(string name = "Test phone")
    {
        var client = CreateClient();
        using var reply = await client.PostJsonAsync("/api/v1/pair", new { code = Pairing.Rotate(), label = name });
        reply.EnsureSuccessStatusCode();
        await client.LoadIdentityAsync();
        return client;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        _certificate.Dispose();
        try { Directory.Delete(_dataDir, recursive: true); } catch (IOException) { }
    }

    private static X509Certificate2 CreateCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=Abyss Remote Test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddIpAddress(IPAddress.Loopback);
        san.AddDnsName("localhost");
        request.CertificateExtensions.Add(san.Build());
        using var temporary = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(2));
        // Not persisted: the key lives in a temporary container that is deleted on Dispose.
        return X509CertificateLoader.LoadPkcs12(temporary.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.Exportable);
    }

    private static string FindWebRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "src", "AbyssRemote.Host", "wwwroot");
            if (File.Exists(Path.Combine(candidate, "index.html"))) return candidate;
        }
        throw new DirectoryNotFoundException("Could not find src/AbyssRemote.Host/wwwroot above the test output folder.");
    }
}

/// <summary>One browser: a cookie jar, pinned TLS, and helpers that send the same headers the UI sends.</summary>
public sealed class TestClient : IDisposable
{
    private readonly TestServer _server;

    public TestClient(TestServer server)
    {
        _server = server;
        Http = new HttpClient(new HttpClientHandler
        {
            CookieContainer = Cookies,
            UseProxy = false,
            ServerCertificateCustomValidationCallback = (_, presented, _, _) => server.Trusts(presented),
        }) { BaseAddress = server.BaseUri, Timeout = TimeSpan.FromSeconds(10) };
    }

    public CookieContainer Cookies { get; } = new();
    public HttpClient Http { get; }
    public string? Csrf { get; set; }
    public string? DeviceId { get; private set; }

    public Cookie? SessionCookie => Cookies.GetAllCookies().FirstOrDefault(c => c.Name == AbyssServer.SessionCookie);

    public async Task LoadIdentityAsync()
    {
        var me = await Http.GetFromJsonAsync<JsonElement>("/api/v1/me");
        Csrf = me.GetProperty("csrf").GetString();
        DeviceId = me.GetProperty("id").GetString();
    }

    public Task<HttpResponseMessage> PostJsonAsync(string path, object body, bool withOrigin = true, bool withCsrf = true) =>
        SendAsync(HttpMethod.Post, path, JsonContent.Create(body), withOrigin, withCsrf);

    public Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, HttpContent? content = null, bool withOrigin = true, bool withCsrf = true)
    {
        var request = new HttpRequestMessage(method, path) { Content = content };
        if (withOrigin) request.Headers.Add("Origin", _server.Origin);
        if (withCsrf && Csrf is not null) request.Headers.Add("X-Abyss-CSRF", Csrf);
        return Http.SendAsync(request);
    }

    /// <summary>Opens a WebSocket the way app.js does. Pass overrides to simulate attacks.</summary>
    public async Task<ClientWebSocket> ConnectAsync(string path, string? origin = null, string? csrf = null, bool sendCookies = true)
    {
        var socket = new ClientWebSocket();
        socket.Options.RemoteCertificateValidationCallback = (_, presented, _, _) => _server.Trusts(presented);
        if (sendCookies) socket.Options.Cookies = Cookies;
        socket.Options.SetRequestHeader("Origin", origin ?? _server.Origin);
        socket.Options.AddSubProtocol(AbyssServer.SocketProtocol);
        socket.Options.AddSubProtocol(csrf ?? Csrf ?? new string('0', 64));
        await socket.ConnectAsync(new Uri($"wss://{_server.BaseUri.Authority}{path}"), CancellationToken.None);
        return socket;
    }

    public void Dispose() => Http.Dispose();
}

public static class SocketExtensions
{
    public static Task SendTextAsync(this WebSocket socket, string text) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None);

    public static Task SendJsonAsync(this WebSocket socket, object message) =>
        socket.SendTextAsync(JsonSerializer.Serialize(message));

    /// <summary>Receives one whole message (or the close frame) with a timeout.</summary>
    public static async Task<(WebSocketMessageType Type, byte[] Data)> ReceiveMessageAsync(this WebSocket socket, int timeoutMs = 5000)
    {
        using var timeout = new CancellationTokenSource(timeoutMs);
        using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        while (true)
        {
            var result = await socket.ReceiveAsync(chunk, timeout.Token);
            buffer.Write(chunk, 0, result.Count);
            if (result.EndOfMessage || result.MessageType == WebSocketMessageType.Close) return (result.MessageType, buffer.ToArray());
        }
    }

    /// <summary>Waits until the server closes the socket and returns its close code.</summary>
    public static async Task<int?> WaitForCloseAsync(this WebSocket socket, int timeoutMs = 5000)
    {
        while (true)
        {
            var (type, _) = await socket.ReceiveMessageAsync(timeoutMs);
            if (type == WebSocketMessageType.Close) return (int?)socket.CloseStatus;
        }
    }
}

public static class Wait
{
    /// <summary>Polls a condition that becomes true on a server thread.</summary>
    public static async Task UntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition was not met in time.");
            await Task.Delay(20);
        }
    }
}
