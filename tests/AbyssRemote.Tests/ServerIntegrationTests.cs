using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using AbyssRemote.Host;

namespace AbyssRemote.Tests;

/// <summary>End-to-end checks against the real HTTPS server. The desktop is a fake that
/// records what reaches it, which is how these tests prove nothing gets through unchecked.</summary>
[TestClass]
public sealed class ServerIntegrationTests
{
    private TestServer _server = null!;

    [TestInitialize]
    public async Task Start() => _server = await TestServer.StartAsync();

    [TestCleanup]
    public async Task Stop() => await _server.DisposeAsync();

    [TestMethod]
    public async Task AnonymousClientsCannotSeeOrControlThePc()
    {
        using var client = _server.CreateClient();
        foreach (var route in new[] { "/api/v1/me", "/api/v1/displays", "/api/v1/devices", "/api/v1/health", "/api/v1/clipboard" })
        {
            using var reply = await client.Http.GetAsync(route);
            Assert.AreEqual(HttpStatusCode.Unauthorized, reply.StatusCode, route);
        }
        foreach (var path in new[] { "/api/v1/stream", "/api/v1/control" })
            await Assert.ThrowsAsync<WebSocketException>(() => client.ConnectAsync(path));

        Assert.AreEqual(0, _server.Desktop.Commands.Count);
        Assert.AreEqual(0, _server.Desktop.SinksOpen);
    }

    [TestMethod]
    public async Task PairingSetsAHardenedSessionCookie()
    {
        using var client = _server.CreateClient();
        using var reply = await client.PostJsonAsync("/api/v1/pair", new { code = _server.Pairing.Rotate(), label = "iPad" });

        Assert.AreEqual(HttpStatusCode.OK, reply.StatusCode);
        string setCookie = string.Join(";", reply.Headers.GetValues("Set-Cookie")).ToLowerInvariant();
        StringAssert.Contains(setCookie, "secure");
        StringAssert.Contains(setCookie, "httponly");
        StringAssert.Contains(setCookie, "samesite=strict");
        StringAssert.Contains(setCookie, "path=/");
        Assert.IsTrue(_server.Notices.Any(n => n.Contains("iPad")), "the host console should announce new pairings");
    }

    [TestMethod]
    public async Task PairingRefusesWrongCodesAndForeignPages()
    {
        string code = _server.Pairing.Rotate();
        using var client = _server.CreateClient();

        using (var noOrigin = await client.PostJsonAsync("/api/v1/pair", new { code, label = "x" }, withOrigin: false))
            Assert.AreEqual(HttpStatusCode.Forbidden, noOrigin.StatusCode);

        var foreign = new HttpRequestMessage(HttpMethod.Post, "/api/v1/pair") { Content = JsonContent.Create(new { code, label = "x" }) };
        foreign.Headers.Add("Origin", "https://evil.example");
        using (var reply = await client.Http.SendAsync(foreign))
            Assert.AreEqual(HttpStatusCode.Forbidden, reply.StatusCode);

        using (var wrong = await client.PostJsonAsync("/api/v1/pair", new { code = code == "12345678" ? "87654321" : "12345678", label = "x" }))
            Assert.AreEqual(HttpStatusCode.BadRequest, wrong.StatusCode);

        Assert.IsNull(client.SessionCookie);
    }

    [TestMethod]
    public async Task ControlSocketNeedsCookieOriginAndCsrf()
    {
        using var client = await _server.PairAsync();

        await Assert.ThrowsAsync<WebSocketException>(() => client.ConnectAsync("/api/v1/control", origin: "https://evil.example"));
        await Assert.ThrowsAsync<WebSocketException>(() => client.ConnectAsync("/api/v1/control", csrf: new string('0', 64)));
        await Assert.ThrowsAsync<WebSocketException>(() => client.ConnectAsync("/api/v1/control", sendCookies: false));
        Assert.AreEqual(0, _server.Desktop.SinksOpen);

        using var socket = await client.ConnectAsync("/api/v1/control");
        await socket.SendJsonAsync(new { kind = "key", code = "KeyA", action = "down" });
        await Wait.UntilAsync(() => _server.Desktop.Commands.Count == 1);
        StringAssert.Contains(_server.Desktop.Commands[0], "KeyA");
    }

    [TestMethod]
    public async Task StateChangingRequestsNeedCsrf()
    {
        using var client = await _server.PairAsync();

        using (var reply = await client.PostJsonAsync("/api/v1/clipboard", new { text = "hi" }, withCsrf: false))
            Assert.AreEqual(HttpStatusCode.Forbidden, reply.StatusCode);
        using (var reply = await client.PostJsonAsync("/api/v1/clipboard", new { text = "hi" }, withOrigin: false))
            Assert.AreEqual(HttpStatusCode.Forbidden, reply.StatusCode);
        Assert.AreEqual("", _server.Clipboard.Text);

        using (var reply = await client.PostJsonAsync("/api/v1/clipboard", new { text = "hello PC" }))
            Assert.AreEqual(HttpStatusCode.OK, reply.StatusCode);
        Assert.AreEqual("hello PC", _server.Clipboard.Text);

        using (var tooLong = await client.PostJsonAsync("/api/v1/clipboard", new { text = new string('x', 4097) }))
            Assert.AreEqual(HttpStatusCode.BadRequest, tooLong.StatusCode);

        using (var reply = await client.SendAsync(HttpMethod.Delete, $"/api/v1/devices/{client.DeviceId}", withCsrf: false))
            Assert.AreEqual(HttpStatusCode.Forbidden, reply.StatusCode);
        Assert.IsTrue(_server.State.IsActive(client.DeviceId!));
    }

    [TestMethod]
    public async Task RevokingADeviceDisconnectsItImmediately()
    {
        using var phone = await _server.PairAsync("phone");
        using var laptop = await _server.PairAsync("laptop");
        using var phoneControl = await phone.ConnectAsync("/api/v1/control");
        using var phoneVideo = await phone.ConnectAsync("/api/v1/stream");

        using (var reply = await laptop.SendAsync(HttpMethod.Delete, $"/api/v1/devices/{phone.DeviceId}"))
            Assert.AreEqual(HttpStatusCode.OK, reply.StatusCode);

        Assert.AreEqual(CloseCodes.Revoked, await phoneControl.WaitForCloseAsync());
        Assert.AreEqual(CloseCodes.Revoked, await phoneVideo.WaitForCloseAsync());
        using var me = await phone.Http.GetAsync("/api/v1/me");
        Assert.AreEqual(HttpStatusCode.Unauthorized, me.StatusCode);
        await Wait.UntilAsync(() => _server.Desktop.SinksOpen == 0);
    }

    [TestMethod]
    public async Task LogoutRevokesThisDevice()
    {
        using var client = await _server.PairAsync();
        using (var reply = await client.PostJsonAsync("/api/v1/logout", new { }))
            Assert.AreEqual(HttpStatusCode.OK, reply.StatusCode);
        Assert.IsFalse(_server.State.IsActive(client.DeviceId!));
    }

    [TestMethod]
    public async Task AReconnectReplacesTheOlderControlSocket()
    {
        using var client = await _server.PairAsync();
        using var first = await client.ConnectAsync("/api/v1/control");
        using var second = await client.ConnectAsync("/api/v1/control");

        Assert.AreEqual(CloseCodes.Replaced, await first.WaitForCloseAsync());
        await second.SendJsonAsync(new { kind = "key", code = "KeyB", action = "down" });
        await Wait.UntilAsync(() => _server.Desktop.Commands.Any(c => c.Contains("KeyB")));
    }

    [TestMethod]
    public async Task ClosingTheControlSocketReleasesHeldInput()
    {
        using var client = await _server.PairAsync();
        var socket = await client.ConnectAsync("/api/v1/control");
        await socket.SendJsonAsync(new { kind = "key", code = "ControlLeft", action = "down" });
        await Wait.UntilAsync(() => _server.Desktop.SinksOpen == 1 && _server.Desktop.Commands.Count == 1);

        socket.Abort(); // like a phone losing Wi-Fi
        socket.Dispose();
        await Wait.UntilAsync(() => _server.Desktop.SinksOpen == 0, timeoutMs: 30_000);
    }

    [TestMethod]
    public async Task PingIsAnsweredWithPong()
    {
        using var client = await _server.PairAsync();
        using var socket = await client.ConnectAsync("/api/v1/control");
        await socket.SendJsonAsync(new { kind = "ping", t = 1234.5 });

        var (type, data) = await socket.ReceiveMessageAsync();
        Assert.AreEqual(WebSocketMessageType.Text, type);
        using var reply = JsonDocument.Parse(data);
        Assert.AreEqual("pong", reply.RootElement.GetProperty("type").GetString());
        Assert.AreEqual(1234.5, reply.RootElement.GetProperty("t").GetDouble());
        Assert.AreEqual(0, _server.Desktop.Commands.Count, "pings are not input");
    }

    [TestMethod]
    public async Task StreamAnswersEveryRequestWithExactlyOneMessage()
    {
        using var client = await _server.PairAsync();
        using var socket = await client.ConnectAsync("/api/v1/stream?fps=30");

        await socket.SendTextAsync("next");
        var (type, data) = await socket.ReceiveMessageAsync();
        Assert.AreEqual(WebSocketMessageType.Binary, type);
        CollectionAssert.AreEqual(FakeDesktop.FakeJpeg, data);

        _server.Desktop.OnCapture = full => full ? FakeDesktop.FakeJpeg : null; // screen not changing
        await socket.SendTextAsync("next");
        (type, data) = await socket.ReceiveMessageAsync();
        Assert.AreEqual("idle", JsonDocument.Parse(data).RootElement.GetProperty("type").GetString());

        await socket.SendTextAsync("full");
        (type, _) = await socket.ReceiveMessageAsync();
        Assert.AreEqual(WebSocketMessageType.Binary, type);

        _server.Desktop.OnCapture = _ => throw new DesktopUnavailableException("locked");
        await socket.SendTextAsync("next");
        (type, data) = await socket.ReceiveMessageAsync();
        var status = JsonDocument.Parse(data).RootElement;
        Assert.AreEqual("unavailable", status.GetProperty("type").GetString());
        Assert.AreEqual("locked", status.GetProperty("message").GetString());

        await socket.SendTextAsync("give me everything");
        Assert.AreEqual((int)WebSocketCloseStatus.InvalidPayloadData, await socket.WaitForCloseAsync());
    }

    [TestMethod]
    public async Task UiIsServedWithSecurityHeaders()
    {
        using var client = _server.CreateClient();
        using var reply = await client.Http.GetAsync("/");
        string html = await reply.Content.ReadAsStringAsync();

        Assert.AreEqual(HttpStatusCode.OK, reply.StatusCode);
        StringAssert.Contains(html, "Abyss Remote");
        StringAssert.Contains(reply.Headers.GetValues("Content-Security-Policy").Single(), "default-src 'none'");
        Assert.AreEqual("DENY", reply.Headers.GetValues("X-Frame-Options").Single());
        Assert.AreEqual("nosniff", reply.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.IsTrue(reply.Headers.CacheControl?.NoStore);
    }

    [TestMethod]
    public async Task UnknownHostHeaderIsRefused()
    {
        using var client = _server.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/info");
        request.Headers.Host = "rebind.attacker.example";
        using var reply = await client.Http.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.MisdirectedRequest, reply.StatusCode);
    }

    [TestMethod]
    public async Task InfoReportsTheProjectVersion()
    {
        using var client = _server.CreateClient();
        var info = await client.Http.GetFromJsonAsync<JsonElement>("/api/v1/info");
        Assert.AreEqual(AppInfo.Version, info.GetProperty("version").GetString());
        Assert.AreNotEqual("0.0.0", AppInfo.Version);
    }

    [TestMethod]
    public async Task UiCacheBustingMatchesTheProjectVersion()
    {
        using var client = _server.CreateClient();
        string html = await client.Http.GetStringAsync("/");
        StringAssert.Contains(html, $"app.js?v={AppInfo.Version}");
        StringAssert.Contains(html, $"style.css?v={AppInfo.Version}");
    }

    [TestMethod]
    public async Task MalformedControlMessagesAreIgnoredAndOversizedOnesClose()
    {
        using var client = await _server.PairAsync();
        using var socket = await client.ConnectAsync("/api/v1/control");
        await socket.SendTextAsync("{not json");
        await socket.SendAsync(Encoding.UTF8.GetBytes(new string(' ', 10_000)), WebSocketMessageType.Text, true, CancellationToken.None);

        Assert.AreEqual((int)WebSocketCloseStatus.MessageTooBig, await socket.WaitForCloseAsync());
        Assert.AreEqual(0, _server.Desktop.Commands.Count);
    }
}
