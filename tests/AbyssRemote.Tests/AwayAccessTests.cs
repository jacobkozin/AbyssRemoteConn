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

    public AwayClient(TestServer server, bool viaProxy = true, bool useCookies = true)
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
        if (viaProxy) Http.DefaultRequestHeaders.Add("X-Forwarded-For", "203.0.113.50");
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

    /// <summary>Uses an away code and loads the CSRF token, like away.js and app.js do.</summary>
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

/// <summary>Away from home (1.2): the away address only works through the proxy on this PC. A
/// device gets onto it with an away code that the window shows on request, or by bringing a
/// code of its own from home. The pairing code never works there.</summary>
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
    public async Task ThePairingCodeNeverWorksAtTheAwayAddress()
    {
        await using var server = await TestServer.StartAsync(AwayClient.Host);
        string pin = server.Pairing.Rotate();
        var (awayCode, _) = server.Away!.Codes.Issue(AwayCodes.NewDevice);
        using var away = new AwayClient(server);
        // More tries than the number of wrong away codes that would destroy every open code:
        // typing the other code is a mistake, not a guess, and is not counted as one.
        for (int i = 0; i <= AwayCodes.MaxFailures; i++)
        {
            using var reply = await away.PostJsonAsync("/api/v1/pair", new { code = pin, label = "Stranger" });
            Assert.AreEqual(HttpStatusCode.BadRequest, reply.StatusCode);
            StringAssert.Contains((await reply.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString(), "not an away code");
        }
        Assert.IsNull(away.SessionCookie);
        Assert.AreEqual(0, server.State.Devices().Count);
        Assert.IsTrue(server.Away.Codes.IsOpen(AwayCodes.NewDevice), "the window's away code is still good");

        // Neither code was used up: the pairing code pairs at home, the away code adds a device away.
        using var home = server.CreateClient();
        using var paired = await home.PostJsonAsync("/api/v1/pair", new { code = pin, label = "Phone at home" });
        Assert.AreEqual(HttpStatusCode.OK, paired.StatusCode);
        using var added = await away.PostJsonAsync("/api/v1/pair", new { code = awayCode, label = "Phone on the train" });
        Assert.AreEqual(HttpStatusCode.OK, added.StatusCode);
    }

    [TestMethod]
    public async Task ADeviceIsAddedAtTheAwayAddressWithTheWindowsAwayCode()
    {
        await using var server = await TestServer.StartAsync(AwayClient.Host);
        var (code, _) = server.Away!.Codes.Issue(AwayCodes.NewDevice);
        string typed = AwayCodes.Format(code).ToLowerInvariant(); // as a person types it: small letters, with the dashes

        using var phone = new AwayClient(server);
        using var added = await phone.PostJsonAsync("/api/v1/pair", new { code = typed, label = "  Kitchen tablet  " });
        Assert.AreEqual(HttpStatusCode.OK, added.StatusCode);
        Assert.AreEqual("Kitchen tablet", (await added.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("name").GetString());
        var cookie = phone.SessionCookie!;
        Assert.IsTrue(cookie.HttpOnly && cookie.Secure, "same cookie rules as at home");
        Assert.IsTrue(server.Notices.Any(n => n.Contains("Added a new device with an away code") && n.Contains("Kitchen tablet") && n.Contains("203.0.113.50")));
        Assert.IsFalse(server.Notices.Any(n => n.Contains(code)), "the code is not written to the activity list");

        // It is a device like any other, signed in at the away address.
        using var me = await phone.Http.GetAsync("/api/v1/me");
        Assert.AreEqual(HttpStatusCode.OK, me.StatusCode);
        var body = await me.Content.ReadFromJsonAsync<JsonElement>();
        Assert.AreEqual("Kitchen tablet", body.GetProperty("device").GetString());
        Assert.IsTrue(body.GetProperty("away").GetProperty("here").GetBoolean());
        Assert.IsTrue(body.GetProperty("away").GetProperty("ready").GetBoolean());
        Assert.AreEqual(1, server.State.Devices().Count);

        // Its key is an away key: it does not open the home address.
        Assert.IsNull(server.State.Authenticate(cookie.Value), "not a home key");
        Assert.IsNotNull(server.State.Authenticate(cookie.Value, away: true));

        // The code is used up.
        using var other = new AwayClient(server);
        using var again = await other.PostJsonAsync("/api/v1/pair", new { code, label = "Someone else" });
        Assert.AreEqual(HttpStatusCode.BadRequest, again.StatusCode);
        Assert.IsNull(other.SessionCookie);
        Assert.AreEqual(1, server.State.Devices().Count);
    }

    [TestMethod]
    public async Task AddingADeviceNeedsANameAndDoesNotWasteTheCodeWithoutOne()
    {
        await using var server = await TestServer.StartAsync(AwayClient.Host);
        var (code, _) = server.Away!.Codes.Issue(AwayCodes.NewDevice);
        using var phone = new AwayClient(server);

        using var nameless = await phone.PostJsonAsync("/api/v1/pair", new { code, label = "   " });
        Assert.AreEqual(HttpStatusCode.BadRequest, nameless.StatusCode);
        Assert.AreEqual(0, server.State.Devices().Count);
        using var forged = await phone.PostJsonAsync("/api/v1/pair", new { code, label = "Phone" }, origin: "https://evil.example");
        Assert.AreEqual(HttpStatusCode.Forbidden, forged.StatusCode, "another site cannot add a device");

        using var named = await phone.PostJsonAsync("/api/v1/pair", new { code, label = "Phone" });
        Assert.AreEqual(HttpStatusCode.OK, named.StatusCode, "the code was still good");
    }

    [TestMethod]
    public async Task TheWindowsAwayCodeAddsNothingThroughTheHandOverLink()
    {
        // The link a paired device brings from home carries no name, so it cannot add a device.
        await using var server = await TestServer.StartAsync(AwayClient.Host);
        var (code, _) = server.Away!.Codes.Issue(AwayCodes.NewDevice);
        using var browser = new AwayClient(server);
        using var reply = await browser.RedeemAsync(code);
        Assert.AreEqual(HttpStatusCode.BadRequest, reply.StatusCode);
        Assert.IsNull(browser.SessionCookie);
        Assert.AreEqual(0, server.State.Devices().Count);
    }

    [TestMethod]
    public async Task TheInfoSaysWhetherThePcOffersAnAwayAddress()
    {
        await using var with = await TestServer.StartAsync(AwayClient.Host);
        using var home = with.CreateClient();
        var info = await home.Http.GetFromJsonAsync<JsonElement>("/api/v1/info");
        Assert.IsTrue(info.GetProperty("awayAvailable").GetBoolean());
        Assert.IsFalse(info.GetProperty("away").GetBoolean(), "this request came from home");

        await using var without = await TestServer.StartAsync();
        using var plain = without.CreateClient();
        Assert.IsFalse((await plain.Http.GetFromJsonAsync<JsonElement>("/api/v1/info")).GetProperty("awayAvailable").GetBoolean());
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
    public void TheWindowsCodeCanBeTakenBackAndIsReplacedByANewerOne()
    {
        var codes = new AwayCodes();
        Assert.IsFalse(codes.IsOpen(AwayCodes.NewDevice));
        var (first, _) = codes.Issue(AwayCodes.NewDevice);
        var (ofADevice, _) = codes.Issue("device-1");
        Assert.IsTrue(codes.IsOpen(AwayCodes.NewDevice));

        var (second, _) = codes.Issue(AwayCodes.NewDevice);
        Assert.AreEqual(AwayCodeResult.WrongOrExpired, codes.TryUse(first).Result, "a newer code replaces the older one");

        Assert.IsTrue(codes.Cancel(AwayCodes.NewDevice));
        Assert.IsFalse(codes.IsOpen(AwayCodes.NewDevice));
        Assert.AreEqual(AwayCodeResult.WrongOrExpired, codes.TryUse(second).Result, "taken back");
        Assert.IsFalse(codes.Cancel(AwayCodes.NewDevice), "nothing left to take back");
        Assert.AreEqual((AwayCodeResult.Accepted, "device-1"), codes.TryUse(ofADevice), "a device's own code is not touched");
    }

    [TestMethod]
    public void OnlyTwelveLettersAndDigitsLookLikeAnAwayCode()
    {
        Assert.IsTrue(AwayCodes.LooksLikeCode("ABCDEFGHJKLM"));
        Assert.IsTrue(AwayCodes.LooksLikeCode(" abcd-efgh-jklm "));
        foreach (string? text in new[] { "12345678", "1234 5678", "ABCD-EFGH-JKL", "ABCD-EFGH-JKLMN", "ABCD-EFGH-JKL0", "", null })
            Assert.IsFalse(AwayCodes.LooksLikeCode(text), text ?? "(nothing)");
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
    public async Task TheWindowShowsAnAwayCodeOnlyWhileAwayAccessIsOnAndCanTakeItBack()
    {
        await using var host = new TestRuntime();
        Assert.IsNull(host.Runtime.NewAwayCode(), "not running yet");
        await host.Runtime.StartAsync(TestRuntime.AnyFreePort); // away access off
        Assert.IsNull(host.Runtime.NewAwayCode());
        Assert.IsFalse(host.Runtime.AwayCodeOpen);

        await host.Runtime.RestartAsync(TestRuntime.AnyFreePort with { AwayHost = AwayClient.Host });
        var offer = host.Runtime.NewAwayCode()!;
        StringAssert.Matches(offer.Code, new System.Text.RegularExpressions.Regex("^[A-HJ-NP-Z2-9]{4}-[A-HJ-NP-Z2-9]{4}-[A-HJ-NP-Z2-9]{4}$"));
        Assert.AreEqual(AwayClient.Origin, offer.Address);
        Assert.AreEqual($"{AwayClient.Origin}/#pair={offer.Code.Replace("-", "")}", offer.Url, "the QR code carries the code in the fragment");
        Assert.IsTrue(host.Runtime.AwayCodeOpen);
        Assert.IsTrue(host.Runtime.Notices.Any(n => n.Message.Contains("away code")));
        Assert.IsFalse(host.Runtime.Notices.Any(n => n.Message.Contains(offer.Code) || n.Message.Contains(offer.Code.Replace("-", ""))), "the code is not written to the activity list");

        // A browser at the away address, the way the proxy delivers it.
        using var phone = host.CreateClient();
        phone.DefaultRequestHeaders.Host = AwayClient.Host;
        phone.DefaultRequestHeaders.Add("X-Forwarded-For", "203.0.113.50");
        async Task<HttpStatusCode> AddAsync(string code)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/pair") { Content = JsonContent.Create(new { code, label = "Phone" }) };
            request.Headers.Add("Origin", AwayClient.Origin);
            using var reply = await phone.SendAsync(request);
            return reply.StatusCode;
        }

        // Closing the box takes the code back.
        host.Runtime.CancelAwayCode();
        Assert.IsFalse(host.Runtime.AwayCodeOpen);
        Assert.AreEqual(HttpStatusCode.BadRequest, await AddAsync(offer.Code));
        Assert.AreEqual(0, host.Runtime.Devices.Count);

        // A new code works, once, and the device shows up with away access.
        var next = host.Runtime.NewAwayCode()!;
        Assert.AreEqual(HttpStatusCode.OK, await AddAsync(next.Code));
        Assert.IsFalse(host.Runtime.AwayCodeOpen, "used");
        await Wait.UntilAsync(() => host.Runtime.Devices.Count == 1);
        Assert.IsTrue(host.Runtime.Devices.Single().Away);
        Assert.IsTrue(host.Events.Any(e => e == "paired:Phone"), "the window is told, so the tray can say so");

        // Turning away access off takes the code with it.
        host.Runtime.NewAwayCode();
        await host.Runtime.RestartAsync(TestRuntime.AnyFreePort);
        Assert.IsFalse(host.Runtime.AwayCodeOpen);
        Assert.IsNull(host.Runtime.NewAwayCode());
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
