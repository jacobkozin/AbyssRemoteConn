using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using AbyssRemote.Host;
using AbyssRemote.Host.App;
using AbyssRemote.Host.Features;
using Microsoft.AspNetCore.Connections;

namespace AbyssRemote.Tests;

/// <summary>Makes a root certificate and a server certificate signed by it, in memory. It
/// stands in for the Windows certificate store, which these tests never open.</summary>
public sealed class ThrowawayCertificates : ICertificateSource
{
    public int Requests { get; private set; }
    public X509Certificate2? LastRoot { get; private set; }

    public HostCertificateSet GetOrCreate(StateStore state, string exportDirectory, bool renewServerCertificate, bool lan)
    {
        Requests++;
        var now = DateTimeOffset.UtcNow;
        using var rootKey = RSA.Create(2048);
        var rootRequest = new CertificateRequest("CN=Abyss Remote Test Root", rootKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        var root = rootRequest.CreateSelfSigned(now.AddMinutes(-5), now.AddHours(3)); // keeps its private key, like the real root

        using var serverKey = RSA.Create(2048);
        var request = new CertificateRequest("CN=Abyss Remote Test Host", serverKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(IPAddress.Loopback);
        names.AddDnsName("localhost");
        request.CertificateExtensions.Add(names.Build());
        using var unsigned = request.Create(root, now.AddMinutes(-5), now.AddHours(2), RandomNumberGenerator.GetBytes(8));
        using var withKey = unsigned.CopyWithPrivateKey(serverKey);
        // Not persisted: the key lives in a temporary container that is deleted on Dispose.
        var server = X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.Exportable);
        LastRoot = root;
        return new HostCertificateSet(server, root, Notice: null);
    }
}

/// <summary>A <see cref="HostRuntime"/> with a temporary state folder, throwaway certificates
/// and fakes for everything that would touch the PC, plus a record of the events it raised.</summary>
public sealed class TestRuntime : IAsyncDisposable
{
    private readonly List<string> _events = new();

    public string DataDirectory { get; } = Directory.CreateTempSubdirectory("abyss-runtime-test-").FullName;
    public ThrowawayCertificates Certificates { get; } = new();
    public FakeDesktop Desktop { get; } = new();
    public HostRuntime Runtime { get; }
    public IReadOnlyList<string> Events { get { lock (_events) return _events.ToList(); } }

    /// <summary>An environment in which nothing reaches the real PC: its files live in
    /// <paramref name="dataDirectory"/>, the certificates are throwaway and the desktop is a fake.</summary>
    public static HostEnvironment Environment(string dataDirectory, ICertificateSource certificates, IDesktop desktop,
        TimeProvider? clock = null, string? webRoot = null) => new()
    {
        DataDirectory = dataDirectory,
        WebRoot = webRoot ?? FindWebRoot(),
        Certificates = certificates,
        Desktop = desktop,
        Clipboard = new FakeClipboard(),
        Transfer = new TransferFolder(Path.Combine(dataDirectory, "transfer")),
        Actions = new FakeHostActions(),
        Steam = new FakeSteam(),
        NetworkAddresses = () => [],
        Clock = clock ?? TimeProvider.System,
    };

    public TestRuntime(TimeProvider? clock = null, string? webRoot = null)
    {
        Runtime = new HostRuntime(Environment(DataDirectory, Certificates, Desktop, clock, webRoot));
        Runtime.StatusChanged += () => Record("status:" + Runtime.Status.Phase);
        Runtime.NoticeAdded += line => Record("notice:" + line.Message);
        Runtime.PairingCodeChanged += () => Record("code:" + Runtime.PairingCode.State);
        Runtime.DevicesChanged += () => Record("devices:" + Runtime.Devices.Count);
        Runtime.ConnectionsChanged += () => Record("connections:" + Runtime.ConnectedDeviceIds.Count);
        Runtime.DevicePaired += device => Record("paired:" + device.Name);
    }

    public static readonly HostRunOptions AnyFreePort = new(Lan: false, Port: 0);

    public Uri BaseUri => new(Runtime.Status.Running!.LocalUrl);

    /// <summary>A browser that trusts exactly the certificate the runtime serves right now.</summary>
    public HttpClient CreateClient(CookieContainer? cookies = null)
    {
        string fingerprint = Runtime.Status.Running!.ServerFingerprint;
        return new HttpClient(new HttpClientHandler
        {
            UseProxy = false,
            CookieContainer = cookies ?? new CookieContainer(),
            ServerCertificateCustomValidationCallback = (_, presented, _, _) =>
                presented is not null && presented.GetCertHashString(HashAlgorithmName.SHA256) == fingerprint,
        }) { BaseAddress = BaseUri, Timeout = TimeSpan.FromSeconds(10) };
    }

    /// <summary>Sends a pairing request the way the browser client does.</summary>
    public async Task<HttpResponseMessage> PairAsync(HttpClient client, string code, string name)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/pair") { Content = JsonContent.Create(new { code, label = name }) };
        request.Headers.Add("Origin", $"https://{BaseUri.Authority}");
        return await client.SendAsync(request);
    }

    public async ValueTask DisposeAsync()
    {
        await Runtime.DisposeAsync();
        try { Directory.Delete(DataDirectory, recursive: true); } catch (IOException) { }
    }

    private void Record(string what) { lock (_events) _events.Add(what); }

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

[TestClass]
public sealed class HostRuntimeTests
{
    [TestMethod]
    public async Task StartsOnAFreePortAndServesTheApi()
    {
        await using var host = new TestRuntime();
        Assert.AreEqual(HostPhase.Stopped, host.Runtime.Status.Phase);

        var status = await host.Runtime.StartAsync(TestRuntime.AnyFreePort);

        Assert.AreEqual(HostPhase.Running, status.Phase);
        Assert.AreEqual("Running: this PC only", status.Summary);
        Assert.IsTrue(status.Running!.Port > 0, "port 0 means: tell me the real one");
        Assert.AreEqual($"https://127.0.0.1:{status.Running.Port}", status.Running.LocalUrl);
        Assert.AreEqual(0, status.Running.LanUrls.Count);
        Assert.AreEqual(status.Running.LocalUrl, status.Running.MainUrl);
        Assert.IsNull(status.Running.RootCertificateUrl, "phones cannot reach a this-PC-only host");
        Assert.AreEqual(64, status.Running.RootFingerprint.Length);
        using var client = host.CreateClient();
        var info = await client.GetFromJsonAsync<JsonElement>("/api/v1/info");
        Assert.AreEqual(AppInfo.Name, info.GetProperty("app").GetString());
        CollectionAssert.AreEqual(new[] { "status:Starting", "status:Running" }, host.Events.Where(e => e.StartsWith("status:")).ToArray());
    }

    [TestMethod]
    public async Task StopClosesThePortAndStartOpensItAgain()
    {
        await using var host = new TestRuntime();
        await host.Runtime.StartAsync(TestRuntime.AnyFreePort);
        int port = host.Runtime.Status.Running!.Port;

        await host.Runtime.StopAsync();
        Assert.AreEqual(HostPhase.Stopped, host.Runtime.Status.Phase);
        Assert.AreEqual("Stopped", host.Runtime.Status.Summary);
        Assert.IsNull(host.Runtime.Status.Running);
        using (var probe = new TcpClient())
            await Assert.ThrowsAsync<SocketException>(() => probe.ConnectAsync(IPAddress.Loopback, port));

        var again = await host.Runtime.StartAsync(new HostRunOptions(Lan: false, port));
        Assert.AreEqual(HostPhase.Running, again.Phase, again.Problem?.Message);
        Assert.AreEqual(port, again.Running!.Port, "the same port can be used again at once");
        Assert.AreEqual(2, host.Certificates.Requests, "certificates are looked up on every start");
    }

    [TestMethod]
    public async Task StartingTwiceIsRefused()
    {
        await using var host = new TestRuntime();
        await host.Runtime.StartAsync(TestRuntime.AnyFreePort);
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Runtime.StartAsync(TestRuntime.AnyFreePort));
        Assert.AreEqual(HostPhase.Running, host.Runtime.Status.Phase);
    }

    [TestMethod]
    public async Task RestartKeepsThePairingCodeAndThePairedDevices()
    {
        await using var host = new TestRuntime();
        await host.Runtime.StartAsync(TestRuntime.AnyFreePort);
        var cookies = new CookieContainer();
        using (var client = host.CreateClient(cookies))
        using (var reply = await host.PairAsync(client, host.Runtime.NewPairingCode().Digits, "Tablet"))
            Assert.AreEqual(HttpStatusCode.OK, reply.StatusCode);
        string code = host.Runtime.NewPairingCode().Digits;

        var status = await host.Runtime.RestartAsync(TestRuntime.AnyFreePort);

        Assert.AreEqual(HostPhase.Running, status.Phase);
        Assert.AreEqual(PairingCodeState.Active, host.Runtime.PairingCode.State, "a restart does not use up the code");
        Assert.AreEqual("Tablet", host.Runtime.Devices.Single().Name);
        using var after = host.CreateClient();
        using (var reply = await host.PairAsync(after, code, "Phone"))
            Assert.AreEqual(HttpStatusCode.OK, reply.StatusCode, "the code from before the restart still pairs");
        CollectionAssert.AreEquivalent(new[] { "Tablet", "Phone" }, host.Runtime.Devices.Select(d => d.Name).ToArray());
    }

    [TestMethod]
    public async Task APortThatIsTakenIsReportedInPlainWords()
    {
        await using var first = new TestRuntime();
        await using var second = new TestRuntime();
        await first.Runtime.StartAsync(TestRuntime.AnyFreePort);
        int port = first.Runtime.Status.Running!.Port;

        var status = await second.Runtime.StartAsync(new HostRunOptions(Lan: false, port));

        Assert.AreEqual(HostPhase.Failed, status.Phase);
        Assert.AreEqual(StartProblemKind.PortInUse, status.Problem!.Kind);
        Assert.AreEqual($"Port {port} is already used by another program.", status.Problem.Message);
        Assert.AreEqual($"Port {port} is already used by another program", status.Summary);
        Assert.IsNull(status.Running);

        // Once the other program is gone, trying again works.
        await first.Runtime.StopAsync();
        var retry = await second.Runtime.RestartAsync(new HostRunOptions(Lan: false, port));
        Assert.AreEqual(HostPhase.Running, retry.Phase, retry.Problem?.Message);
    }

    [TestMethod]
    public async Task ADamagedStateFileIsAFailureWithAReasonNotACrash()
    {
        await using var host = new TestRuntime();
        File.WriteAllText(Path.Combine(host.DataDirectory, "settings.json"), "{ not json");

        var status = await host.Runtime.StartAsync(TestRuntime.AnyFreePort);

        Assert.AreEqual(HostPhase.Failed, status.Phase);
        Assert.AreEqual(StartProblemKind.StateFile, status.Problem!.Kind);
        StringAssert.Contains(status.Problem.Message, "settings.json");
        Assert.AreEqual("{ not json", File.ReadAllText(Path.Combine(host.DataDirectory, "settings.json")), "the file is left alone");
        Assert.AreEqual(0, host.Certificates.Requests);
    }

    [TestMethod]
    public async Task AMissingBrowserClientIsAFailureWithAReason()
    {
        string empty = Directory.CreateTempSubdirectory("abyss-empty-webroot-").FullName;
        try
        {
            await using var host = new TestRuntime(webRoot: empty);
            var status = await host.Runtime.StartAsync(TestRuntime.AnyFreePort);
            Assert.AreEqual(StartProblemKind.WebUiMissing, status.Problem!.Kind);
        }
        finally { Directory.Delete(empty, recursive: true); }
    }

    [TestMethod]
    public async Task PairingRaisesEventsAndUsesUpTheCode()
    {
        await using var host = new TestRuntime();
        await host.Runtime.StartAsync(TestRuntime.AnyFreePort);
        var code = host.Runtime.NewPairingCode();
        Assert.AreEqual(PairingCodeState.Active, code.State);
        Assert.AreEqual(8, code.Digits.Length);
        Assert.AreEqual(PairingManager.CodeLifetime, code.ExpiresUtc - code.IssuedUtc);

        using var client = host.CreateClient();
        using (var reply = await host.PairAsync(client, code.Digits, "Living room iPad"))
            Assert.AreEqual(HttpStatusCode.OK, reply.StatusCode);

        // The pairing request's notice refreshes the device list before the reply is sent.
        Assert.AreEqual("Living room iPad", host.Runtime.Devices.Single().Name);
        Assert.AreEqual(PairingCodeState.Used, host.Runtime.PairingCode.State);
        Assert.AreEqual("", host.Runtime.PairingCode.Digits, "a used code is not kept");
        var events = host.Events;
        Assert.IsTrue(events.Any(e => e.StartsWith("notice:Paired a new device: \"Living room iPad\"")));
        CollectionAssert.Contains(events.ToList(), "devices:1");
        CollectionAssert.Contains(events.ToList(), "paired:Living room iPad");
        CollectionAssert.Contains(events.ToList(), "code:Used");
        Assert.AreEqual(1, host.Runtime.Notices.Count(n => n.Message.Contains("Living room iPad")));
    }

    [TestMethod]
    public async Task DevicesFromDiskAreNotAnnouncedAsNewlyPaired()
    {
        await using var host = new TestRuntime();
        new StateStore(host.DataDirectory).Pair("Old laptop");

        await host.Runtime.StartAsync(TestRuntime.AnyFreePort);

        Assert.AreEqual("Old laptop", host.Runtime.Devices.Single().Name);
        Assert.IsFalse(host.Events.Any(e => e.StartsWith("paired:")));
    }

    [TestMethod]
    public async Task LiveConnectionsAreReportedAndRevokingClosesThem()
    {
        await using var host = new TestRuntime();
        await host.Runtime.StartAsync(TestRuntime.AnyFreePort);
        var cookies = new CookieContainer();
        using var client = host.CreateClient(cookies);
        using (var reply = await host.PairAsync(client, host.Runtime.NewPairingCode().Digits, "Phone"))
            reply.EnsureSuccessStatusCode();
        var me = await client.GetFromJsonAsync<JsonElement>("/api/v1/me");
        string id = me.GetProperty("id").GetString()!, csrf = me.GetProperty("csrf").GetString()!;
        Assert.AreEqual(0, host.Runtime.ConnectedDeviceIds.Count);

        using var socket = new ClientWebSocket();
        string fingerprint = host.Runtime.Status.Running!.ServerFingerprint;
        socket.Options.RemoteCertificateValidationCallback = (_, presented, _, _) => presented!.GetCertHashString(HashAlgorithmName.SHA256) == fingerprint;
        socket.Options.Cookies = cookies;
        socket.Options.SetRequestHeader("Origin", $"https://{host.BaseUri.Authority}");
        socket.Options.AddSubProtocol(AbyssServer.SocketProtocol);
        socket.Options.AddSubProtocol(csrf);
        await socket.ConnectAsync(new Uri($"wss://{host.BaseUri.Authority}/api/v1/control"), CancellationToken.None);

        // Nobody calls the runtime here: its own once-a-second check has to notice.
        await Wait.UntilAsync(() => host.Runtime.ConnectedDeviceIds.Contains(id));
        CollectionAssert.Contains(host.Events.ToList(), "connections:1");

        Assert.IsTrue(host.Runtime.RevokeDevice(id));
        Assert.AreEqual(CloseCodes.Revoked, await socket.WaitForCloseAsync());
        Assert.AreEqual(0, host.Runtime.Devices.Count);
        Assert.AreEqual(0, host.Runtime.ConnectedDeviceIds.Count);
        Assert.IsTrue(host.Runtime.Notices.Any(n => n.Message == "Revoked \"Phone\" and closed its connections."));
        Assert.IsFalse(host.Runtime.RevokeDevice(id), "already gone");
    }

    [TestMethod]
    public async Task RevokeAllRemovesEveryDevice()
    {
        await using var host = new TestRuntime();
        await host.Runtime.StartAsync(TestRuntime.AnyFreePort);
        using var client = host.CreateClient();
        foreach (string name in new[] { "One", "Two" })
            using (var reply = await host.PairAsync(client, host.Runtime.NewPairingCode().Digits, name))
                reply.EnsureSuccessStatusCode();
        Assert.AreEqual(2, host.Runtime.Devices.Count);

        Assert.AreEqual(2, host.Runtime.RevokeAllDevices());

        Assert.AreEqual(0, host.Runtime.Devices.Count);
        using var me = await client.GetAsync("/api/v1/me");
        Assert.AreEqual(HttpStatusCode.Unauthorized, me.StatusCode);
    }

    [TestMethod]
    public async Task RestartTellsConnectedBrowsersToReconnect()
    {
        await using var host = new TestRuntime();
        await host.Runtime.StartAsync(TestRuntime.AnyFreePort);
        var cookies = new CookieContainer();
        using var client = host.CreateClient(cookies);
        using (var reply = await host.PairAsync(client, host.Runtime.NewPairingCode().Digits, "Phone"))
            reply.EnsureSuccessStatusCode();
        string csrf = (await client.GetFromJsonAsync<JsonElement>("/api/v1/me")).GetProperty("csrf").GetString()!;
        using var socket = new ClientWebSocket();
        string fingerprint = host.Runtime.Status.Running!.ServerFingerprint;
        socket.Options.RemoteCertificateValidationCallback = (_, presented, _, _) => presented!.GetCertHashString(HashAlgorithmName.SHA256) == fingerprint;
        socket.Options.Cookies = cookies;
        socket.Options.SetRequestHeader("Origin", $"https://{host.BaseUri.Authority}");
        socket.Options.AddSubProtocol(AbyssServer.SocketProtocol);
        socket.Options.AddSubProtocol(csrf);
        await socket.ConnectAsync(new Uri($"wss://{host.BaseUri.Authority}/api/v1/control"), CancellationToken.None);

        var restart = host.Runtime.RestartAsync(TestRuntime.AnyFreePort);

        Assert.AreEqual(CloseCodes.Restart, await socket.WaitForCloseAsync(), "1012 = restarting, not 4001 = revoked");
        Assert.AreEqual(HostPhase.Running, (await restart).Phase);
        Assert.AreEqual(1, host.Runtime.Devices.Count, "the device is still paired");
    }

    [TestMethod]
    public async Task TooManyWrongGuessesLockTheCode()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        await using var host = new TestRuntime(clock);
        await host.Runtime.StartAsync(TestRuntime.AnyFreePort);
        string code = host.Runtime.NewPairingCode().Digits;
        string wrong = code == "00000000" ? "11111111" : "00000000";
        using var client = host.CreateClient();

        // Five tries per address every five minutes; twenty wrong tries in total destroy the code.
        for (int round = 0; round < 4; round++)
        {
            for (int i = 0; i < PairingManager.AttemptsPerAddress; i++)
                using (var reply = await host.PairAsync(client, wrong, "guess"))
                    Assert.AreEqual(HttpStatusCode.BadRequest, reply.StatusCode);
            clock.Advance(PairingManager.AttemptWindow + TimeSpan.FromSeconds(1));
        }

        Assert.AreEqual(PairingCodeState.Locked, host.Runtime.PairingCode.State);
        Assert.AreEqual("", host.Runtime.PairingCode.Digits);
        CollectionAssert.Contains(host.Events.ToList(), "code:Locked");
        Assert.IsTrue(host.Runtime.Notices.Any(n => n.Message.StartsWith("The pairing code was turned off")));
        Assert.AreEqual(PairingCodeState.Active, host.Runtime.NewPairingCode().State, "a new code works again");
    }

    [TestMethod]
    public async Task NoticesKeepOnlyTheNewestLines()
    {
        await using var host = new TestRuntime();
        for (int i = 0; i < HostRuntime.MaxNotices + 5; i++) host.Runtime.AddNotice($"line {i}");
        var notices = host.Runtime.Notices;
        Assert.AreEqual(HostRuntime.MaxNotices, notices.Count);
        Assert.AreEqual("line 5", notices[0].Message);
        Assert.AreEqual($"line {HostRuntime.MaxNotices + 4}", notices[^1].Message, "newest last");
    }
}

[TestClass]
public sealed class HostStatusTests
{
    [TestMethod]
    public void TheCommandLineWinsOverSavedSettingsForOneRun()
    {
        var saved = new Preferences { Lan = true, Port = 9100 };
        Assert.AreEqual(new HostRunOptions(Lan: true, 9100), HostRunOptions.From(LaunchOptions.Parse([]), saved));
        Assert.AreEqual(new HostRunOptions(Lan: false, 9100), HostRunOptions.From(LaunchOptions.Parse(["--local"]), saved));
        Assert.AreEqual(new HostRunOptions(Lan: true, 8555), HostRunOptions.From(LaunchOptions.Parse(["--port", "8555"]), saved));
        Assert.AreEqual(new HostRunOptions(Lan: true, LaunchOptions.DefaultPort), HostRunOptions.From(LaunchOptions.Parse(["--lan"]), new Preferences()));
        Assert.AreEqual(new HostRunOptions(Lan: false, LaunchOptions.DefaultPort), HostRunOptions.From(LaunchOptions.Parse([]), new Preferences()));
        Assert.IsTrue(HostRunOptions.From(LaunchOptions.Parse(["--renew-cert"]), saved).RenewCertificate);
    }

    [TestMethod]
    public void ThePairingCodeIsShownInTwoGroupsAndGoesIntoTheQrAddress()
    {
        var now = DateTimeOffset.UtcNow;
        var code = new PairingCode(PairingCodeState.Active, "12345678", now, now.AddMinutes(10));
        Assert.AreEqual("1234 5678", code.Grouped);
        Assert.AreEqual("https://192.168.1.20:8444/#pair=12345678", code.PairingUrl("https://192.168.1.20:8444", now));
        Assert.AreEqual(TimeSpan.FromMinutes(4), code.Remaining(now.AddMinutes(6)));

        // Expired, used or missing codes never end up in a QR code.
        Assert.IsFalse(code.IsUsable(now.AddMinutes(10)));
        Assert.AreEqual(TimeSpan.Zero, code.Remaining(now.AddMinutes(11)));
        Assert.AreEqual("https://192.168.1.20:8444/", code.PairingUrl("https://192.168.1.20:8444", now.AddMinutes(11)));
        Assert.AreEqual("https://192.168.1.20:8444/", PairingCode.None.PairingUrl("https://192.168.1.20:8444", now));
        Assert.AreEqual("https://192.168.1.20:8444/", (code with { State = PairingCodeState.Used, Digits = "" }).PairingUrl("https://192.168.1.20:8444", now));
    }

    [TestMethod]
    public void HomeNetworkAddressesComeFirstInTheStatus()
    {
        var running = new RunningHost(8444, Lan: true, "https://127.0.0.1:8444", ["https://192.168.1.20:8444", "https://172.20.0.1:8444"], "AA", "BB", [], "x.cer");
        var status = new HostStatus(HostPhase.Running, new HostRunOptions(true, 8444), running);
        Assert.AreEqual("Running: home network", status.Summary);
        Assert.AreEqual("https://192.168.1.20:8444", running.MainUrl);
        Assert.AreEqual("https://192.168.1.20:8444/AbyssRemote-RootCA.cer", running.RootCertificateUrl);
        Assert.AreEqual("Starting…", new HostStatus(HostPhase.Starting, status.Options).Summary);
    }

    [TestMethod]
    public void AdaptersWithAGatewayAreListedFirst()
    {
        IPAddress virtualSwitch = IPAddress.Parse("172.20.0.1"), wifi = IPAddress.Parse("192.168.1.20"), other = IPAddress.Parse("10.0.0.7");
        var ordered = LanAddresses.Order([virtualSwitch, wifi, other], new HashSet<IPAddress> { wifi });
        CollectionAssert.AreEqual(new[] { wifi, virtualSwitch, other }, ordered.ToArray());
        CollectionAssert.AreEqual(new[] { virtualSwitch, wifi }, LanAddresses.Order([virtualSwitch, wifi], new HashSet<IPAddress>()).ToArray());
    }

    [TestMethod]
    public void StartProblemsAreFoundInsideWrappedExceptions()
    {
        // Kestrel reports a taken port as an IOException that wraps the real cause.
        var wrapped = new IOException("Failed to bind to address", new AddressInUseException("in use", new SocketException((int)SocketError.AddressAlreadyInUse)));
        Assert.AreEqual(new StartProblem(StartProblemKind.PortInUse, "Port 8444 is already used by another program."), StartProblems.Describe(wrapped, 8444));

        var denied = new IOException("bind", new SocketException((int)SocketError.AccessDenied));
        Assert.AreEqual(StartProblemKind.PortNotAllowed, StartProblems.Describe(denied, 8444).Kind);

        Assert.AreEqual(StartProblemKind.Certificates, StartProblems.Describe(new CryptographicException("Keyset does not exist"), 8444).Kind);
        Assert.AreEqual(new StartProblem(StartProblemKind.StateFile, "Cannot read it."),
            StartProblems.Describe(new StartProblemException(StartProblemKind.StateFile, "Cannot read it."), 8444));
        Assert.AreEqual("Abyss Remote could not start: something odd", StartProblems.Describe(new InvalidOperationException("something odd"), 8444).Message);
    }
}

/// <summary>The public download of the root certificate (GET /AbyssRemote-RootCA.cer).</summary>
[TestClass]
public sealed class RootCertificateDownloadTests
{
    [TestMethod]
    public async Task AnyoneCanDownloadThePublicRootCertificate()
    {
        await using var host = new TestRuntime();
        await host.Runtime.StartAsync(TestRuntime.AnyFreePort);
        var root = host.Certificates.LastRoot!;
        Assert.IsTrue(root.HasPrivateKey, "the server itself holds the root's private key");

        using var client = host.CreateClient(); // no cookie: not paired
        using var reply = await client.GetAsync("/AbyssRemote-RootCA.cer");
        byte[] file = await reply.Content.ReadAsByteArrayAsync();

        Assert.AreEqual(HttpStatusCode.OK, reply.StatusCode);
        Assert.AreEqual("application/x-x509-ca-cert", reply.Content.Headers.ContentType?.MediaType);
        Assert.AreEqual("attachment", reply.Content.Headers.ContentDisposition?.DispositionType);
        Assert.AreEqual("AbyssRemote-RootCA.cer", reply.Content.Headers.ContentDisposition?.FileName);
        Assert.AreEqual("nosniff", reply.Headers.GetValues("X-Content-Type-Options").Single());

        // Exactly the public certificate: the same bytes as the certificate without its key.
        CollectionAssert.AreEqual(root.RawData, file);
        CollectionAssert.AreEqual(host.Runtime.Status.Running!.RootCertificate, file);
        Assert.AreEqual(X509ContentType.Cert, X509Certificate2.GetCertContentType(file), "a bare certificate, not a PFX with a key inside");
        using var downloaded = X509CertificateLoader.LoadCertificate(file);
        Assert.IsFalse(downloaded.HasPrivateKey);
        Assert.AreEqual(host.Runtime.Status.Running.RootFingerprint, downloaded.GetCertHashString(HashAlgorithmName.SHA256));
        Assert.AreNotEqual(host.Runtime.Status.Running.ServerFingerprint, host.Runtime.Status.Running.RootFingerprint);
    }

    [TestMethod]
    public async Task TheDownloadIsReadOnlyAndStillBehindTheHostCheck()
    {
        await using var host = new TestRuntime();
        await host.Runtime.StartAsync(TestRuntime.AnyFreePort);
        using var client = host.CreateClient();

        foreach (var method in new[] { HttpMethod.Post, HttpMethod.Put, HttpMethod.Delete })
        {
            var change = new HttpRequestMessage(method, "/AbyssRemote-RootCA.cer");
            change.Headers.Add("Origin", $"https://{host.BaseUri.Authority}");
            using var reply = await client.SendAsync(change);
            Assert.AreEqual(HttpStatusCode.MethodNotAllowed, reply.StatusCode, method.Method);
        }

        var rebound = new HttpRequestMessage(HttpMethod.Get, "/AbyssRemote-RootCA.cer");
        rebound.Headers.Host = "rebind.attacker.example";
        using var refused = await client.SendAsync(rebound);
        Assert.AreEqual(HttpStatusCode.MisdirectedRequest, refused.StatusCode);
    }

    [TestMethod]
    public async Task WithoutASeparateRootThereIsNothingToDownload()
    {
        await using var server = await TestServer.StartAsync(); // one self-signed certificate, no root
        using var client = server.CreateClient();
        using var reply = await client.Http.GetAsync("/AbyssRemote-RootCA.cer");
        Assert.AreEqual(HttpStatusCode.NotFound, reply.StatusCode);
    }
}

/// <summary>The window's question to the session registry: which devices are connected right now?</summary>
[TestClass]
public sealed class ConnectedDevicesTests
{
    [TestMethod]
    public async Task ConnectedDevicesAreListedWhileTheirSocketsAreOpen()
    {
        await using var server = await TestServer.StartAsync();
        using var phone = await server.PairAsync("phone");
        using var laptop = await server.PairAsync("laptop");
        Assert.AreEqual(0, server.Sessions.ConnectedDeviceIds().Count);

        using var control = await phone.ConnectAsync("/api/v1/control");
        using var video = await phone.ConnectAsync("/api/v1/stream");
        await Wait.UntilAsync(() => server.Sessions.Count(SocketKind.Video) == 1);

        CollectionAssert.AreEqual(new[] { phone.DeviceId }, server.Sessions.ConnectedDeviceIds().ToArray(), "one entry per device, however many sockets");

        await control.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        await video.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        await Wait.UntilAsync(() => server.Sessions.ConnectedDeviceIds().Count == 0);
    }

    [TestMethod]
    public async Task ARestartClosesEverySocketWithTheReconnectCode()
    {
        await using var server = await TestServer.StartAsync();
        using var phone = await server.PairAsync("phone");
        using var control = await phone.ConnectAsync("/api/v1/control");
        await Wait.UntilAsync(() => server.Sessions.ConnectedDeviceIds().Count == 1);

        server.Sessions.CloseAllForRestart();

        Assert.AreEqual(CloseCodes.Restart, await control.WaitForCloseAsync());
        Assert.AreEqual(0, server.Sessions.ConnectedDeviceIds().Count);
        Assert.IsTrue(server.State.IsActive(phone.DeviceId!), "the device stays paired");
    }
}
