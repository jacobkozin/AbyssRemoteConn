using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using AbyssRemote.Host;
using AbyssRemote.Host.App;

namespace AbyssRemote.Tests;

/// <summary>A browser at the away address, delivered the way the Gaming Hub's Caddy does it: the
/// connection comes from this PC (loopback), Host is the away address, and X-Forwarded-For carries
/// the phone's own address. It has its own cookie jar, like a separate browser.</summary>
public sealed class AwayClient : IDisposable
{
    public const string Host = "remote.test.example";
    public const string Origin = "https://" + Host;
    private readonly TestServer _server;

    /// <param name="forwardedFor">The address the proxy reports for this browser.</param>
    public AwayClient(TestServer server, bool viaProxy = true, bool useCookies = true, string forwardedFor = "203.0.113.50")
    {
        _server = server;
        Http = new HttpClient(new HttpClientHandler
        {
            CookieContainer = Cookies,
            UseCookies = useCookies,
            UseProxy = false,
            ServerCertificateCustomValidationCallback = (_, presented, _, _) => server.Trusts(presented),
        }) { BaseAddress = server.BaseUri, Timeout = TimeSpan.FromSeconds(10) };
        Http.DefaultRequestHeaders.Host = Host;
        if (viaProxy) Http.DefaultRequestHeaders.Add("X-Forwarded-For", forwardedFor);
    }

    public CookieContainer Cookies { get; } = new();
    public HttpClient Http { get; }
    public string? Csrf { get; private set; }

    public Cookie? SessionCookie => Cookies.GetAllCookies().FirstOrDefault(c => c.Name == AbyssServer.SessionCookie);

    public Task<HttpResponseMessage> PostJsonAsync(string path, object body, string? origin = Origin, bool withCsrf = true)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        if (origin is not null) request.Headers.Add("Origin", origin);
        if (withCsrf && Csrf is not null) request.Headers.Add("X-Abyss-CSRF", Csrf);
        return Http.SendAsync(request);
    }

    /// <summary>Uses a hand-over code and loads the CSRF token, like away.js and app.js do.</summary>
    public async Task<HttpResponseMessage> RedeemAsync(string code)
    {
        var reply = await PostJsonAsync("/api/v1/away/redeem", new { code });
        if (reply.IsSuccessStatusCode) Csrf = (await Http.GetFromJsonAsync<JsonElement>("/api/v1/me")).GetProperty("csrf").GetString();
        return reply;
    }

    /// <summary>Opens a WebSocket through the "proxy", the way app.js does at the away address.</summary>
    public async Task<ClientWebSocket> ConnectAsync(string path, string? cookie = null)
    {
        var socket = new ClientWebSocket();
        socket.Options.RemoteCertificateValidationCallback = (_, presented, _, _) => _server.Trusts(presented);
        if (cookie is null) socket.Options.Cookies = Cookies;
        else socket.Options.SetRequestHeader("Cookie", $"{AbyssServer.SessionCookie}={cookie}");
        socket.Options.SetRequestHeader("Host", Host);
        socket.Options.SetRequestHeader("X-Forwarded-For", "203.0.113.50");
        socket.Options.SetRequestHeader("Origin", Origin);
        socket.Options.AddSubProtocol(AbyssServer.SocketProtocol);
        socket.Options.AddSubProtocol(Csrf ?? new string('0', 64));
        await socket.ConnectAsync(new Uri($"wss://{_server.BaseUri.Authority}{path}"), CancellationToken.None);
        return socket;
    }

    public void Dispose() => Http.Dispose();
}

/// <summary>Away from home (1.2): the away address works only through the proxy on this PC. A
/// device pairs there with the same code as at home, and a device that is paired at home takes
/// itself there with a one-time code of its own.</summary>
[TestClass]
public sealed class AwayAccessTests
{
    /// <summary>At home: a paired device asks for its away code.</summary>
    private static async Task<(string Code, string Url)> AskForCodeAsync(TestClient home)
    {
        using var reply = await home.PostJsonAsync("/api/v1/away/code", new { });
        Assert.AreEqual(HttpStatusCode.OK, reply.StatusCode);
        var body = await reply.Content.ReadFromJsonAsync<JsonElement>();
        return (body.GetProperty("code").GetString()!, body.GetProperty("url").GetString()!);
    }

    private static async Task<(TestServer Server, TestClient Home, AwayClient Away)> SetUpAsync()
    {
        var server = await TestServer.StartAsync(AwayClient.Host);
        var home = await server.PairAsync("Jacob's iPhone");
        var away = new AwayClient(server);
        var (code, _) = await AskForCodeAsync(home);
        using var reply = await away.RedeemAsync(code);
        Assert.AreEqual(HttpStatusCode.OK, reply.StatusCode);
        return (server, home, away);
    }

    [TestMethod]
    public async Task TheAwayAddressOnlyWorksThroughTheProxyOnThisPc()
    {
        await using var server = await TestServer.StartAsync(AwayClient.Host);
        using var direct = new AwayClient(server, viaProxy: false);
        using (var reply = await direct.Http.GetAsync("/api/v1/info"))
            Assert.AreEqual(HttpStatusCode.MisdirectedRequest, reply.StatusCode, "the away address without the proxy");

        using var proxied = new AwayClient(server);
        using var info = await proxied.Http.GetAsync("/api/v1/info");
        Assert.AreEqual(HttpStatusCode.OK, info.StatusCode);
        Assert.IsTrue((await info.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("away").GetBoolean());
        Assert.IsTrue(info.Headers.Contains("Strict-Transport-Security"), "the away address is HTTPS-only");

        using var home = server.CreateClient();
        using var homeInfo = await home.Http.GetAsync("/api/v1/info");
        Assert.IsFalse((await homeInfo.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("away").GetBoolean());
        Assert.IsFalse(homeInfo.Headers.Contains("Strict-Transport-Security"));
    }

    [TestMethod]
    public async Task AProxiedRequestForAnyOtherNameIsRefused()
    {
        await using var server = await TestServer.StartAsync(AwayClient.Host);
        using var client = server.CreateClient();
        foreach (var header in new[] { "X-Forwarded-For", "Forwarded" })
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/info");
            request.Headers.Add(header, header == "Forwarded" ? "for=203.0.113.50" : "203.0.113.50");
            using var reply = await client.Http.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.MisdirectedRequest, reply.StatusCode, header);
        }
    }

    [TestMethod]
    public async Task WithAwayAccessOffTheAwayAddressIsJustAnUnknownName()
    {
        await using var server = await TestServer.StartAsync(); // away access off
        using var away = new AwayClient(server);
        using var reply = await away.Http.GetAsync("/api/v1/info");
        Assert.AreEqual(HttpStatusCode.MisdirectedRequest, reply.StatusCode);

        using var home = await server.PairAsync();
        using var code = await home.PostJsonAsync("/api/v1/away/code", new { });
        Assert.AreEqual(HttpStatusCode.Conflict, code.StatusCode, "no codes while away access is off");
    }

    [TestMethod]
    public async Task ThePairingCodePairsADeviceAtTheAwayAddress()
    {
        await using var server = await TestServer.StartAsync(AwayClient.Host);
        string pin = server.Pairing.Rotate();
        using var away = new AwayClient(server);
        using var reply = await away.PostJsonAsync("/api/v1/pair", new { code = pin, label = "Phone on the train" });
        Assert.AreEqual(HttpStatusCode.OK, reply.StatusCode);
        var cookie = away.SessionCookie!;
        Assert.IsTrue(cookie.HttpOnly && cookie.Secure, "same cookie rules as at home");
        Assert.IsTrue(server.Notices.Any(n => n.Contains("at the away address") && n.Contains("Phone on the train") && n.Contains("203.0.113.50")),
            "the activity list says where the device was paired from");
        Assert.IsFalse(server.Notices.Any(n => n.Contains(pin)), "the code is not written to the activity list");

        // It is signed in there at once, and what it holds is the device's away key.
        using (var me = await away.Http.GetAsync("/api/v1/me"))
        {
            Assert.AreEqual(HttpStatusCode.OK, me.StatusCode);
            var state = (await me.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("away");
            Assert.IsTrue(state.GetProperty("here").GetBoolean());
            Assert.IsTrue(state.GetProperty("ready").GetBoolean());
        }
        var device = JsonSerializer.SerializeToElement(server.State.Devices().Single());
        Assert.AreEqual("Phone on the train", device.GetProperty("name").GetString());
        Assert.IsTrue(device.GetProperty("away").GetBoolean(), "the window lists it with away access");
        using var atHome = new HttpClient(new HttpClientHandler
        {
            UseCookies = false, UseProxy = false,
            ServerCertificateCustomValidationCallback = (_, presented, _, _) => server.Trusts(presented),
        }) { BaseAddress = server.BaseUri };
        var withAwayKey = new HttpRequestMessage(HttpMethod.Get, "/api/v1/me");
        withAwayKey.Headers.Add("Cookie", $"{AbyssServer.SessionCookie}={cookie.Value}");
        using (var refused = await atHome.SendAsync(withAwayKey))
            Assert.AreEqual(HttpStatusCode.Unauthorized, refused.StatusCode, "an away key is not a home key");

        // The code is used up, at both addresses.
        using var second = new AwayClient(server, forwardedFor: "203.0.113.51");
        using (var again = await second.PostJsonAsync("/api/v1/pair", new { code = pin, label = "Second" }))
            Assert.AreEqual(HttpStatusCode.BadRequest, again.StatusCode);
        using var home = server.CreateClient();
        using (var third = await home.PostJsonAsync("/api/v1/pair", new { code = pin, label = "Third" }))
            Assert.AreEqual(HttpStatusCode.BadRequest, third.StatusCode);
        Assert.AreEqual(1, server.State.Devices().Count);
    }

    [TestMethod]
    public async Task PairingAtTheAwayAddressNeedsTheExactAwayOrigin()
    {
        await using var server = await TestServer.StartAsync(AwayClient.Host);
        string pin = server.Pairing.Rotate();
        using var away = new AwayClient(server);
        foreach (var origin in new string?[] { null, "https://evil.example", "https://remote.test.example:8444", "http://remote.test.example" })
        {
            using var reply = await away.PostJsonAsync("/api/v1/pair", new { code = pin, label = "Phone" }, origin: origin);
            Assert.AreEqual(HttpStatusCode.Forbidden, reply.StatusCode, origin ?? "no Origin");
        }
        using var ok = await away.PostJsonAsync("/api/v1/pair", new { code = pin, label = "Phone" });
        Assert.AreEqual(HttpStatusCode.OK, ok.StatusCode, "the refused tries did not use the code up");
    }

    [TestMethod]
    public async Task GuessingAtTheAwayAddressIsLimitedPerVisitorAndTwentyWrongCodesDestroyTheCode()
    {
        await using var server = await TestServer.StartAsync(AwayClient.Host);
        string pin = server.Pairing.Rotate();
        string wrong = pin == "00000000" ? "00000001" : "00000000";

        // One visitor gets five tries in five minutes, counted by the address the proxy reports.
        using var visitor = new AwayClient(server, forwardedFor: "203.0.113.7");
        for (int i = 0; i < PairingManager.AttemptsPerAddress; i++)
        {
            using var reply = await visitor.PostJsonAsync("/api/v1/pair", new { code = wrong, label = "x" });
            Assert.AreEqual(HttpStatusCode.BadRequest, reply.StatusCode);
        }
        using (var sixth = await visitor.PostJsonAsync("/api/v1/pair", new { code = pin, label = "x" }))
            Assert.AreEqual(HttpStatusCode.TooManyRequests, sixth.StatusCode, "now even the right code is refused from there");

        // Other visitors are not held up by the first. All of them together get twenty wrong
        // tries, and then the code is gone for everyone.
        int failures = PairingManager.AttemptsPerAddress;
        for (int n = 0; failures < PairingManager.MaxFailuresPerCode; n++)
        {
            using var other = new AwayClient(server, forwardedFor: $"203.0.113.{100 + n}");
            for (int i = 0; i < PairingManager.AttemptsPerAddress && failures < PairingManager.MaxFailuresPerCode; i++, failures++)
            {
                using var reply = await other.PostJsonAsync("/api/v1/pair", new { code = wrong, label = "x" });
                Assert.AreEqual(HttpStatusCode.BadRequest, reply.StatusCode);
            }
        }
        using var owner = server.CreateClient();
        using var late = await owner.PostJsonAsync("/api/v1/pair", new { code = pin, label = "Owner at home" });
        Assert.AreEqual(HttpStatusCode.BadRequest, late.StatusCode, "the real code is gone too");
        StringAssert.Contains((await late.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString(), "locked");
        Assert.AreEqual(0, server.State.Devices().Count);
    }

    [TestMethod]
    public async Task WhileTheAwayAddressIsNotForwardedNoBrowserIsSentThere()
    {
        await using var server = await TestServer.StartAsync(AwayClient.Host);
        using var home = await server.PairAsync();
        server.AwayForwarded = false; // the proxy on the PC is not running

        using (var info = await home.Http.GetAsync("/api/v1/info"))
        {
            var body = await info.Content.ReadFromJsonAsync<JsonElement>();
            Assert.IsFalse(body.GetProperty("awayAvailable").GetBoolean());
            Assert.AreEqual(JsonValueKind.Null, body.GetProperty("awayUrl").ValueKind);
        }
        using (var me = await home.Http.GetAsync("/api/v1/me"))
            Assert.IsFalse((await me.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("away").GetProperty("available").GetBoolean());
        using (var code = await home.PostJsonAsync("/api/v1/away/code", new { }))
        {
            Assert.AreEqual(HttpStatusCode.Conflict, code.StatusCode);
            StringAssert.Contains((await code.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString(), "does not answer");
        }
        Assert.AreEqual(0, server.Away!.Codes.OpenCount);

        server.AwayForwarded = true;
        using (var info = await home.Http.GetAsync("/api/v1/info"))
        {
            var body = await info.Content.ReadFromJsonAsync<JsonElement>();
            Assert.IsTrue(body.GetProperty("awayAvailable").GetBoolean());
            Assert.AreEqual(AwayClient.Origin, body.GetProperty("awayUrl").GetString());
        }
        await AskForCodeAsync(home);

        // A request that came through the away address proves by itself that it works.
        server.AwayForwarded = false;
        using var away = new AwayClient(server);
        using var awayInfo = await away.Http.GetAsync("/api/v1/info");
        Assert.IsTrue((await awayInfo.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("awayAvailable").GetBoolean());
    }

    [TestMethod]
    public async Task APairedDeviceSetsItselfUpAtHomeThenConnectsAway()
    {
        await using var server = await TestServer.StartAsync(AwayClient.Host);
        using var home = await server.PairAsync("Jacob's iPhone");

        using (var me = await home.Http.GetAsync("/api/v1/me"))
        {
            var away = (await me.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("away");
            Assert.IsTrue(away.GetProperty("available").GetBoolean());
            Assert.AreEqual(AwayClient.Origin, away.GetProperty("url").GetString());
            Assert.IsFalse(away.GetProperty("here").GetBoolean());
            Assert.IsFalse(away.GetProperty("ready").GetBoolean());
        }

        var (code, url) = await AskForCodeAsync(home);
        StringAssert.Matches(code, new System.Text.RegularExpressions.Regex("^[A-HJ-NP-Z2-9]{4}-[A-HJ-NP-Z2-9]{4}-[A-HJ-NP-Z2-9]{4}$"));
        Assert.AreEqual($"{AwayClient.Origin}/#away={code.Replace("-", "")}", url, "the code travels in the fragment");
        Assert.IsFalse(server.Notices.Any(n => n.Contains(code.Replace("-", ""))), "the code is not written to the activity list");

        using var awayBrowser = new AwayClient(server);
        using var redeemed = await awayBrowser.RedeemAsync(code);
        Assert.AreEqual(HttpStatusCode.OK, redeemed.StatusCode);
        var cookie = awayBrowser.SessionCookie!;
        Assert.IsTrue(cookie.HttpOnly && cookie.Secure, "same cookie rules as at home");
        Assert.IsTrue(server.Notices.Any(n => n.Contains("can now connect away from home") && n.Contains("203.0.113.50")));

        using var awayMe = await awayBrowser.Http.GetAsync("/api/v1/me");
        Assert.AreEqual(HttpStatusCode.OK, awayMe.StatusCode);
        var body = await awayMe.Content.ReadFromJsonAsync<JsonElement>();
        Assert.AreEqual(home.DeviceId, body.GetProperty("id").GetString(), "the same device, not a new one");
        Assert.IsTrue(body.GetProperty("away").GetProperty("here").GetBoolean());
        Assert.IsTrue(body.GetProperty("away").GetProperty("ready").GetBoolean());
        Assert.AreEqual(1, server.State.Devices().Count);

        // The home browser keeps working and now sees that this device is set up.
        using var homeMe = await home.Http.GetAsync("/api/v1/me");
        Assert.IsTrue((await homeMe.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("away").GetProperty("ready").GetBoolean());

        // A state-changing request away works with the away CSRF token and the away origin.
        using var clip = await awayBrowser.PostJsonAsync("/api/v1/clipboard", new { text = "from the train" });
        Assert.AreEqual(HttpStatusCode.OK, clip.StatusCode);
        Assert.AreEqual("from the train", server.Clipboard.Text);
    }

    [TestMethod]
    public async Task AHomeKeyNeverWorksAwayAndAnAwayKeyNeverWorksAtHome()
    {
        var (server, home, away) = await SetUpAsync();
        await using var _ = server;
        using (home) using (away)
        {
            string homeCookie = home.SessionCookie!.Value, awayCookie = away.SessionCookie!.Value;
            Assert.AreNotEqual(homeCookie, awayCookie);

            using var bare = new AwayClient(server, useCookies: false);
            var withHomeKey = new HttpRequestMessage(HttpMethod.Get, "/api/v1/me");
            withHomeKey.Headers.Add("Cookie", $"{AbyssServer.SessionCookie}={homeCookie}");
            using (var reply = await bare.Http.SendAsync(withHomeKey))
                Assert.AreEqual(HttpStatusCode.Unauthorized, reply.StatusCode, "a home key at the away address");

            using var homeBare = new HttpClient(new HttpClientHandler
            {
                UseCookies = false, UseProxy = false,
                ServerCertificateCustomValidationCallback = (_, presented, _, _) => server.Trusts(presented),
            }) { BaseAddress = server.BaseUri };
            var withAwayKey = new HttpRequestMessage(HttpMethod.Get, "/api/v1/me");
            withAwayKey.Headers.Add("Cookie", $"{AbyssServer.SessionCookie}={awayCookie}");
            using (var reply = await homeBare.SendAsync(withAwayKey))
                Assert.AreEqual(HttpStatusCode.Unauthorized, reply.StatusCode, "an away key at home");

            // Sockets follow the same rule.
            using var awaySocket = await away.ConnectAsync("/api/v1/control");
            Assert.AreEqual(WebSocketState.Open, awaySocket.State);
            await Assert.ThrowsAsync<WebSocketException>(() => away.ConnectAsync("/api/v1/control", cookie: homeCookie));
        }
    }

    [TestMethod]
    public async Task ACodeWorksOnceAndIsOnlyGivenAtHome()
    {
        await using var server = await TestServer.StartAsync(AwayClient.Host);
        using var home = await server.PairAsync();
        var (code, _) = await AskForCodeAsync(home);

        using var first = new AwayClient(server);
        using (var reply = await first.RedeemAsync(code)) Assert.AreEqual(HttpStatusCode.OK, reply.StatusCode);
        using var second = new AwayClient(server);
        using (var reply = await second.RedeemAsync(code)) Assert.AreEqual(HttpStatusCode.BadRequest, reply.StatusCode, "used once already");
        Assert.IsNull(second.SessionCookie);

        // A device that is away cannot make codes, even for itself.
        using var more = await first.PostJsonAsync("/api/v1/away/code", new { });
        Assert.AreEqual(HttpStatusCode.Forbidden, more.StatusCode);

        // The redeem endpoint does not exist at home.
        using var atHome = await home.PostJsonAsync("/api/v1/away/redeem", new { code });
        Assert.AreEqual(HttpStatusCode.NotFound, atHome.StatusCode);
    }

    [TestMethod]
    public async Task TheCodeRequestRefusesForgedRequests()
    {
        await using var server = await TestServer.StartAsync(AwayClient.Host);
        using var home = await server.PairAsync();
        await ForgedRequests.AssertAllRefusedAsync(server, home, HttpMethod.Post, "/api/v1/away/code");
    }

    [TestMethod]
    public async Task RedeemingNeedsTheExactAwayOrigin()
    {
        await using var server = await TestServer.StartAsync(AwayClient.Host);
        using var home = await server.PairAsync();
        var (code, _) = await AskForCodeAsync(home);
        using var away = new AwayClient(server);
        foreach (var origin in new string?[] { null, "https://evil.example", "https://remote.test.example:8444", "http://remote.test.example" })
        {
            using var reply = await away.PostJsonAsync("/api/v1/away/redeem", new { code }, origin: origin);
            Assert.AreEqual(HttpStatusCode.Forbidden, reply.StatusCode, origin ?? "no Origin");
        }
        using var ok = await away.RedeemAsync(code);
        Assert.AreEqual(HttpStatusCode.OK, ok.StatusCode, "the refused tries did not use the code up");
    }

    [TestMethod]
    public async Task RevokingADeviceEndsItsAwayAccessAtOnce()
    {
        var (server, home, away) = await SetUpAsync();
        await using var _ = server;
        using (home) using (away)
        {
            using var socket = await away.ConnectAsync("/api/v1/control");
            using var revoked = await home.SendAsync(HttpMethod.Delete, $"/api/v1/devices/{home.DeviceId}");
            Assert.AreEqual(HttpStatusCode.OK, revoked.StatusCode);
            Assert.AreEqual(CloseCodes.Revoked, await socket.WaitForCloseAsync());
            using var me = await away.Http.GetAsync("/api/v1/me");
            Assert.AreEqual(HttpStatusCode.Unauthorized, me.StatusCode);
        }
    }

    [TestMethod]
    public async Task TwentyWrongCodesDestroyEveryOpenCode()
    {
        await using var server = await TestServer.StartAsync(AwayClient.Host);
        using var home = await server.PairAsync();
        var (code, _) = await AskForCodeAsync(home);
        using var attacker = new AwayClient(server);
        for (int i = 1; i <= AwayCodes.MaxFailures; i++)
        {
            using var reply = await attacker.PostJsonAsync("/api/v1/away/redeem", new { code = "AAAA-AAAA-AAAA" });
            Assert.AreEqual(HttpStatusCode.BadRequest, reply.StatusCode);
            if (i == AwayCodes.MaxFailures)
                StringAssert.Contains((await reply.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString(), "Too many wrong codes");
        }
        using var phone = new AwayClient(server);
        using (var late = await phone.RedeemAsync(code)) Assert.AreEqual(HttpStatusCode.BadRequest, late.StatusCode, "the real code is gone too");
        Assert.AreEqual(0, server.Away!.Codes.OpenCount);

        var (fresh, _) = await AskForCodeAsync(home);
        using var again = await phone.RedeemAsync(fresh);
        Assert.AreEqual(HttpStatusCode.OK, again.StatusCode, "a new code from home works");
    }

    [TestMethod]
    public async Task TheRootCertificateIsNotOfferedAtTheAwayAddress()
    {
        await using var host = new TestRuntime();
        await host.Runtime.StartAsync(TestRuntime.AnyFreePort with { AwayHost = AwayClient.Host });
        using var homeClient = host.CreateClient();
        using (var reply = await homeClient.GetAsync("/AbyssRemote-RootCA.cer")) Assert.AreEqual(HttpStatusCode.OK, reply.StatusCode);

        var request = new HttpRequestMessage(HttpMethod.Get, "/AbyssRemote-RootCA.cer");
        request.Headers.Host = AwayClient.Host;
        request.Headers.Add("X-Forwarded-For", "203.0.113.50");
        using var away = await homeClient.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.NotFound, away.StatusCode);
        Assert.AreEqual("https://" + AwayClient.Host, host.Runtime.Status.Running!.AwayUrl);
    }
}

/// <summary>The pieces of away access that need no server.</summary>
[TestClass]
public sealed class AwayPartsTests
{
    [TestMethod]
    public void OnlyRealHostNamesAreAcceptedAsTheAwayAddress()
    {
        foreach (var good in new[] { "remote.example.duckdns.org", "a.b", "Remote.Example.COM", " away.example.org ", "x-1.example.net" })
            Assert.IsTrue(AwayAccess.IsValidHost(good), good);
        foreach (var bad in new[] { null, "", " ", "localhost", "example", "https://remote.example.org", "remote.example.org:443",
                     "remote.example.org/", "203.0.113.5", "::1", "-bad.example.org", "bad-.example.org", "a..b", "remote example.org",
                     "remote.example.123", "rémote.example.org", new string('a', 64) + ".example.org" })
            Assert.IsFalse(AwayAccess.IsValidHost(bad), bad ?? "null");
        Assert.AreEqual("remote.example.com", new AwayAccess(" Remote.Example.COM ").Host);
        Assert.AreEqual("https://remote.example.com", new AwayAccess("remote.example.com").Url);
    }

    [TestMethod]
    public void CodesExpireAfterTenMinutesAndANewOneReplacesTheOld()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 11, 23, 18, 0, 0, TimeSpan.Zero));
        var codes = new AwayCodes(clock);
        var (first, expires) = codes.Issue("device-a");
        Assert.AreEqual(clock.GetUtcNow() + AwayCodes.Lifetime, expires);
        var (second, _) = codes.Issue("device-a");
        Assert.AreEqual(AwayCodeResult.WrongOrExpired, codes.TryUse(first).Result, "a newer code replaced it");
        clock.Advance(AwayCodes.Lifetime);
        Assert.AreEqual(AwayCodeResult.WrongOrExpired, codes.TryUse(second).Result, "expired");

        var (third, _) = codes.Issue("device-a");
        var (other, _) = codes.Issue("device-b");
        Assert.AreEqual(2, codes.OpenCount, "each device has its own code");
        var used = codes.TryUse(AwayCodes.Format(third).ToLowerInvariant().Replace("-", " - "));
        Assert.AreEqual((AwayCodeResult.Accepted, "device-a"), used, "typed in small letters with spaces still counts");
        Assert.AreEqual((AwayCodeResult.Accepted, "device-b"), codes.TryUse(other));
    }

    [TestMethod]
    public void CodesUseTheUnambiguousAlphabet()
    {
        var codes = new AwayCodes();
        for (int i = 0; i < 200; i++)
        {
            var (code, _) = codes.Issue("d");
            Assert.AreEqual(AwayCodes.Length, code.Length);
            Assert.IsTrue(code.All(AwayCodes.Alphabet.Contains), code);
        }
        Assert.AreEqual("ABCD-EFGH-JKLM", AwayCodes.Format("ABCDEFGHJKLM"));
        Assert.AreEqual("ABCDEFGHJKLM", AwayCodes.Normalize(" abcd-efgh\tjklm "));
    }

    [TestMethod]
    public void TurningAwayAccessOffForgetsEveryAwayKeyButNoHomeKey()
    {
        string dir = Directory.CreateTempSubdirectory("abyss-remote-away-").FullName;
        try
        {
            var state = new StateStore(dir);
            var (id, secret) = state.Pair("Phone");
            var key = state.SetAwayKey(id)!.Value;
            Assert.IsNotNull(state.Authenticate($"{id}.{key.Secret}", away: true));
            Assert.IsNull(state.Authenticate($"{id}.{key.Secret}"), "an away key is not a home key");
            Assert.IsNull(state.Authenticate($"{id}.{secret}", away: true), "a home key is not an away key");

            // A second set-up replaces the first key.
            var newer = state.SetAwayKey(id)!.Value;
            Assert.IsNull(state.Authenticate($"{id}.{key.Secret}", away: true));
            Assert.IsNotNull(state.Authenticate($"{id}.{newer.Secret}", away: true));

            Assert.AreEqual(1, new StateStore(dir).ClearAwayKeys(), "read back from disk, then cleared");
            var reread = new StateStore(dir);
            Assert.IsNull(reread.Authenticate($"{id}.{newer.Secret}", away: true));
            Assert.IsNotNull(reread.Authenticate($"{id}.{secret}"), "the home pairing stays");
            Assert.IsNull(reread.SetAwayKey("0123456789abcdef0123456789abcdef"), "no key for a device that does not exist");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [TestMethod]
    public void AwayAccessNeedsTheSettingAndAUsableAddress()
    {
        var none = LaunchOptions.Parse([]);
        Assert.IsNull(HostRunOptions.From(none, new Preferences { Away = true, AwayHost = "" }).AwayHost);
        Assert.IsNull(HostRunOptions.From(none, new Preferences { Away = true, AwayHost = "https://remote.example.org" }).AwayHost);
        Assert.IsNull(HostRunOptions.From(none, new Preferences { Away = false, AwayHost = "remote.example.org" }).AwayHost);
        Assert.AreEqual("remote.example.org", HostRunOptions.From(none, new Preferences { Away = true, AwayHost = " Remote.Example.org " }).AwayHost);
        // It reaches the PC through loopback, so --local does not switch it off.
        Assert.AreEqual("remote.example.org", HostRunOptions.From(LaunchOptions.Parse(["--local"]), new Preferences { Away = true, AwayHost = "remote.example.org" }).AwayHost);
    }

    [TestMethod]
    public async Task WithAwayAccessOnTheWindowListsWhichDevicesAreSetUp()
    {
        await using var host = new TestRuntime();
        var before = new StateStore(host.DataDirectory);
        var (id, _) = before.Pair("Phone");
        before.Pair("Laptop");
        Assert.IsNotNull(before.SetAwayKey(id));
        await host.Runtime.StartAsync(TestRuntime.AnyFreePort with { AwayHost = AwayClient.Host });
        Assert.IsTrue(host.Runtime.Devices.Single(d => d.Name == "Phone").Away);
        Assert.IsFalse(host.Runtime.Devices.Single(d => d.Name == "Laptop").Away);
        Assert.IsTrue(host.Runtime.Notices.Any(n => n.Message.Contains("Away access is on")));
    }

    [TestMethod]
    public async Task TheRuntimeSaysWhetherTheAwayAddressIsForwarded()
    {
        await using var host = new TestRuntime { AwayAnswer = AwayVerdict.NotAnswering };
        Assert.AreEqual(AwayVerdict.Unknown, host.Runtime.Away);
        await host.Runtime.StartAsync(TestRuntime.AnyFreePort with { AwayHost = AwayClient.Host });
        Assert.AreEqual(AwayVerdict.NotAnswering, host.Runtime.Away, "looked at once, with the start");
        Assert.IsTrue(host.Runtime.Notices.Any(n => n.Message.Contains("does not answer")));
        CollectionAssert.Contains(host.Events.ToList(), "away:NotAnswering");

        // Meanwhile a browser at home is not offered the away address.
        using var client = host.CreateClient();
        Assert.IsFalse((await client.GetFromJsonAsync<JsonElement>("/api/v1/info")).GetProperty("awayAvailable").GetBoolean());

        host.AwayAnswer = AwayVerdict.Working; // the proxy was started
        await host.Runtime.CheckAwayAsync();
        Assert.AreEqual(AwayVerdict.Working, host.Runtime.Away);
        Assert.IsTrue(host.Runtime.Notices.Any(n => n.Message.Contains("The away address works")));
        Assert.IsTrue((await client.GetFromJsonAsync<JsonElement>("/api/v1/info")).GetProperty("awayAvailable").GetBoolean());

        // Said when it changes, not on every round.
        int lines = host.Runtime.Notices.Count;
        await host.Runtime.CheckAwayAsync();
        Assert.AreEqual(lines, host.Runtime.Notices.Count);

        host.AwayAnswer = AwayVerdict.SomethingElse;
        await host.Runtime.CheckAwayAsync();
        Assert.IsTrue(host.Runtime.Notices.Any(n => n.Message.Contains("answered by something else")));

        await host.Runtime.StopAsync();
        Assert.AreEqual(AwayVerdict.Unknown, host.Runtime.Away, "nothing is known about a server that is not running");
    }

    [TestMethod]
    public async Task InBetweenTheRuntimeOnlyLooksWhetherTheProxyStillListens()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 11, 26, 18, 0, 0, TimeSpan.Zero));
        await using var host = new TestRuntime(clock);
        await host.Runtime.StartAsync(TestRuntime.AnyFreePort with { AwayHost = AwayClient.Host });
        Assert.AreEqual(AwayVerdict.Working, host.Runtime.Away);
        int asked = host.AwayAsked;
        Assert.AreEqual(1, asked, "asked through the proxy once, with the start");

        // The rounds in between leave no line in the proxy's access log.
        await host.Runtime.CheckAwayAsync(thorough: false);
        await host.Runtime.CheckAwayAsync(thorough: false);
        Assert.AreEqual(asked, host.AwayAsked);
        Assert.AreEqual(2, host.AwayLooked);

        host.AwayStillListening = false; // the proxy was closed
        host.AwayAnswer = AwayVerdict.NotAnswering;
        await host.Runtime.CheckAwayAsync(thorough: false);
        Assert.AreEqual(AwayVerdict.NotAnswering, host.Runtime.Away);

        // While it does not work every round asks properly, so a restart of the proxy is noticed.
        host.AwayStillListening = true;
        host.AwayAnswer = AwayVerdict.Working;
        await host.Runtime.CheckAwayAsync(thorough: false);
        Assert.AreEqual(asked + 1, host.AwayAsked);
        Assert.AreEqual(AwayVerdict.Working, host.Runtime.Away);

        // And while it works, it is asked properly every ten minutes.
        clock.Advance(TimeSpan.FromMinutes(11));
        await host.Runtime.CheckAwayAsync(thorough: false);
        Assert.AreEqual(asked + 2, host.AwayAsked);
    }

    [TestMethod]
    public async Task WithAwayAccessOffNothingIsChecked()
    {
        await using var host = new TestRuntime();
        await host.Runtime.StartAsync(TestRuntime.AnyFreePort);
        await host.Runtime.CheckAwayAsync();
        Assert.AreEqual(0, host.AwayAsked);
        Assert.AreEqual(AwayVerdict.Unknown, host.Runtime.Away);
    }

    [TestMethod]
    public async Task ACodeCanBeDroppedSoNothingPairsUntilANewOneIsAskedFor()
    {
        await using var host = new TestRuntime();
        await host.Runtime.StartAsync(TestRuntime.AnyFreePort);
        string digits = host.Runtime.NewPairingCode().Digits;
        host.Runtime.DropPairingCode();
        Assert.AreEqual(PairingCodeState.None, host.Runtime.PairingCode.State);
        using var client = host.CreateClient();
        using (var refused = await host.PairAsync(client, digits, "Phone"))
            Assert.AreEqual(HttpStatusCode.BadRequest, refused.StatusCode, "the dropped code is useless");
        using var paired = await host.PairAsync(client, host.Runtime.NewPairingCode().Digits, "Phone");
        Assert.AreEqual(HttpStatusCode.OK, paired.StatusCode);
    }

    [TestMethod]
    public async Task TheProbeTellsThisProgramFromEverythingElse()
    {
        static HttpMessageHandler Answers(HttpStatusCode status, string body) => new StubHandler(_ => new HttpResponseMessage(status) { Content = new StringContent(body) });
        string ours = JsonSerializer.Serialize(new { app = AppInfo.Name, version = AppInfo.Version, away = true });
        Assert.AreEqual(AwayVerdict.Working, await AwayProbe.CheckAsync(AwayClient.Host, handler: Answers(HttpStatusCode.OK, ours)));

        // Reached this program, but not as an away request: the proxy does not forward the name.
        string notAway = JsonSerializer.Serialize(new { app = AppInfo.Name, version = AppInfo.Version, away = false });
        Assert.AreEqual(AwayVerdict.SomethingElse, await AwayProbe.CheckAsync(AwayClient.Host, handler: Answers(HttpStatusCode.OK, notAway)));
        Assert.AreEqual(AwayVerdict.SomethingElse, await AwayProbe.CheckAsync(AwayClient.Host, handler: Answers(HttpStatusCode.OK, "<html>Another site</html>")));
        Assert.AreEqual(AwayVerdict.SomethingElse, await AwayProbe.CheckAsync(AwayClient.Host, handler: Answers(HttpStatusCode.OK, """{"app":"Something else","away":true}""")));
        Assert.AreEqual(AwayVerdict.SomethingElse, await AwayProbe.CheckAsync(AwayClient.Host, handler: Answers(HttpStatusCode.BadGateway, "")));
        Assert.AreEqual(AwayVerdict.SomethingElse, await AwayProbe.CheckAsync(AwayClient.Host, handler: Answers(HttpStatusCode.MisdirectedRequest, "")));

        var refused = new StubHandler(_ => throw new HttpRequestException("refused", new System.Net.Sockets.SocketException(10061)));
        Assert.AreEqual(AwayVerdict.NotAnswering, await AwayProbe.CheckAsync(AwayClient.Host, handler: refused));
        var silent = new StubHandler(_ => throw new TaskCanceledException());
        Assert.AreEqual(AwayVerdict.NotAnswering, await AwayProbe.CheckAsync(AwayClient.Host, handler: silent));
        var wrongCertificate = new StubHandler(_ => throw new HttpRequestException("TLS", new System.Security.Authentication.AuthenticationException("name mismatch")));
        Assert.AreEqual(AwayVerdict.SomethingElse, await AwayProbe.CheckAsync(AwayClient.Host, handler: wrongCertificate));
    }

    [TestMethod]
    public async Task TheProbeAsksTheProxyOnThisPcUnderTheAwayName()
    {
        // A listener on a free loopback port stands in for the proxy. The probe must connect
        // there, whatever the away name would resolve to, and nothing listening means "not answering".
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Assert.IsTrue(await AwayProbe.ListeningAsync(port));
        var accepted = listener.AcceptTcpClientAsync();
        var verdict = AwayProbe.CheckAsync("name-that-does-not-resolve.example", port);
        using (var connection = await accepted.WaitAsync(TimeSpan.FromSeconds(5))) { } // it came here; closed without an answer
        Assert.AreNotEqual(AwayVerdict.Working, await verdict);
        listener.Stop();
        Assert.IsFalse(await AwayProbe.ListeningAsync(port));
        Assert.AreEqual(AwayVerdict.NotAnswering, await AwayProbe.CheckAsync(AwayClient.Host, port));
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.AreEqual($"https://{AwayClient.Host}/api/v1/info", request.RequestUri!.ToString());
            return Task.FromResult(answer(request));
        }
    }

    [TestMethod]
    public void TheTrayIconSpeaksOncePerOutageAndOnlyWhenItLasts()
    {
        var start = new DateTimeOffset(2026, 11, 26, 8, 0, 0, TimeSpan.Zero);
        var notice = new AwayDownNotice();

        // The PC just started and the proxy is only a little late: nothing is said.
        notice.Changed(AwayVerdict.NotAnswering, awayOn: true, start);
        Assert.IsFalse(notice.ShouldSpeak(AwayVerdict.NotAnswering, start.AddSeconds(10)));
        notice.Changed(AwayVerdict.Working, awayOn: true, start.AddSeconds(20));
        Assert.IsFalse(notice.ShouldSpeak(AwayVerdict.Working, start.AddMinutes(2)));

        // The proxy is closed and stays closed: said once, after the delay.
        var closed = start.AddHours(1);
        notice.Changed(AwayVerdict.NotAnswering, awayOn: true, closed);
        Assert.IsFalse(notice.ShouldSpeak(AwayVerdict.NotAnswering, closed + AwayDownNotice.Delay - TimeSpan.FromSeconds(1)));
        Assert.IsTrue(notice.ShouldSpeak(AwayVerdict.NotAnswering, closed + AwayDownNotice.Delay));
        Assert.IsFalse(notice.ShouldSpeak(AwayVerdict.NotAnswering, closed + AwayDownNotice.Delay + TimeSpan.FromSeconds(1)));

        // The server restarting in between (a changed setting) does not make it say so again.
        notice.Changed(AwayVerdict.Unknown, awayOn: true, closed.AddMinutes(5));
        notice.Changed(AwayVerdict.NotAnswering, awayOn: true, closed.AddMinutes(5));
        Assert.IsFalse(notice.ShouldSpeak(AwayVerdict.NotAnswering, closed.AddMinutes(10)));

        // After it worked again, the next outage is a new one.
        notice.Changed(AwayVerdict.Working, awayOn: true, closed.AddMinutes(20));
        notice.Changed(AwayVerdict.SomethingElse, awayOn: true, closed.AddMinutes(30));
        Assert.IsTrue(notice.ShouldSpeak(AwayVerdict.SomethingElse, closed.AddMinutes(31)));
        StringAssert.Contains(AwayDownNotice.Text(AwayVerdict.SomethingElse), "Another program");
        StringAssert.Contains(AwayDownNotice.Text(AwayVerdict.NotAnswering), "is not running");

        // Turning away access off while the wait runs ends the matter.
        var other = new AwayDownNotice();
        other.Changed(AwayVerdict.NotAnswering, awayOn: true, start);
        other.Changed(AwayVerdict.Unknown, awayOn: false, start.AddSeconds(5));
        Assert.IsFalse(other.ShouldSpeak(AwayVerdict.Unknown, start.AddMinutes(5)));
    }

    [TestMethod]
    public async Task StartingWithAwayAccessOffClearsTheAwayKeys()
    {
        await using var host = new TestRuntime();
        var before = new StateStore(host.DataDirectory);
        var (id, secret) = before.Pair("Phone");
        var key = before.SetAwayKey(id)!.Value;
        await host.Runtime.StartAsync(TestRuntime.AnyFreePort); // away access off
        Assert.IsFalse(host.Runtime.Devices.Single().Away);
        Assert.IsTrue(host.Runtime.Notices.Any(n => n.Message.Contains("lost their away setup")));
        Assert.IsNull(host.Runtime.Status.Running!.AwayUrl);
        var after = new StateStore(host.DataDirectory);
        Assert.IsNull(after.Authenticate($"{id}.{key.Secret}", away: true), "cleared on disk too");
        Assert.IsNotNull(after.Authenticate($"{id}.{secret}"), "the home pairing stays");
    }
}
