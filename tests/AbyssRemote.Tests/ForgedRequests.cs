using System.Net;

namespace AbyssRemote.Tests;

/// <summary>Sends one state-changing request in every way that must fail: without the session
/// cookie, without or with a foreign Origin, and without, with a made-up or with another
/// device's CSRF token.</summary>
public static class ForgedRequests
{
    public static async Task AssertAllRefusedAsync(TestServer server, TestClient paired, HttpMethod method, string path,
        Func<HttpContent>? body = null)
    {
        using var stranger = server.CreateClient();
        using var other = await server.PairAsync("Other device");

        var attempts = new (string What, TestClient From, string? Origin, string? Csrf)[]
        {
            ("no session cookie", stranger, server.Origin, paired.Csrf),
            ("no Origin header", paired, null, paired.Csrf),
            ("a foreign Origin", paired, "https://evil.example", paired.Csrf),
            ("the right host on another port", paired, $"https://{server.BaseUri.Host}:1", paired.Csrf),
            ("no CSRF token", paired, server.Origin, null),
            ("a made-up CSRF token", paired, server.Origin, new string('0', 64)),
            ("another device's CSRF token", paired, server.Origin, other.Csrf),
        };
        foreach (var (what, from, origin, csrf) in attempts)
        {
            using var request = new HttpRequestMessage(method, path) { Content = body?.Invoke() };
            if (origin is not null) request.Headers.Add("Origin", origin);
            if (csrf is not null) request.Headers.Add("X-Abyss-CSRF", csrf);
            using var reply = await from.Http.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.Forbidden, reply.StatusCode, $"{method} {path} with {what}");
        }
    }
}
