using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using AbyssRemote.Host.App;

namespace AbyssRemote.Host;

/// <summary><c>AbyssRemote.exe --smoke-test</c>: starts the REAL published host (real
/// certificate, real web UI, real middleware) on a free loopback port and checks it from the
/// outside. The test client trusts only the exact certificate this process presents; it never
/// changes system or browser trust.</summary>
public static class SmokeTest
{
    public static async Task<int> RunAsync(HostRuntime runtime, bool renewCertificate)
    {
        Console.WriteLine("SELF-TEST: starting the HTTPS host on a free loopback port...");
        // Port 0 = a free port picked by Windows, so the test never collides with a running host.
        var status = await runtime.StartAsync(new HostRunOptions(Lan: false, Port: 0, renewCertificate));
        if (status.Running is not { } running)
        {
            Console.Error.WriteLine($"SELF-TEST FAILED: {status.Problem!.Message}");
            return 1;
        }
        try
        {
            var baseUri = new Uri(running.LocalUrl);
            byte[] pinned = Convert.FromHexString(running.ServerFingerprint);
            bool Pinned(X509Certificate2? presented) =>
                presented is not null && CryptographicOperations.FixedTimeEquals(presented.GetCertHash(HashAlgorithmName.SHA256), pinned);
            using var handler = new HttpClientHandler
            {
                UseProxy = false,
                ServerCertificateCustomValidationCallback = (_, presented, _, _) => Pinned(presented),
            };
            using var http = new HttpClient(handler) { BaseAddress = baseUri, Timeout = TimeSpan.FromSeconds(8) };

            var info = await http.GetFromJsonAsync<Dictionary<string, string>>("/api/v1/info");
            Check(info?["app"] == AppInfo.Name && info["version"] == AppInfo.Version, $"/api/v1/info identifies {AppInfo.Name} {AppInfo.Version} over pinned TLS");

            using (var home = await http.GetAsync("/"))
            {
                string html = await home.Content.ReadAsStringAsync();
                Check(home.StatusCode == HttpStatusCode.OK && html.Contains(AppInfo.Name), "the browser UI is served");
                Check(home.Headers.Contains("Content-Security-Policy") && home.Headers.GetValues("X-Frame-Options").Single() == "DENY",
                    "security headers (CSP, X-Frame-Options) are present");
            }

            using (var download = await http.GetAsync("/AbyssRemote-RootCA.cer"))
            {
                byte[] file = await download.Content.ReadAsByteArrayAsync();
                using var certificate = X509CertificateLoader.LoadCertificate(file);
                Check(download.StatusCode == HttpStatusCode.OK && file.AsSpan().SequenceEqual(running.RootCertificate) && !certificate.HasPrivateKey,
                    "the root certificate download is the public certificate only");
            }

            foreach (var route in new[] { "/api/v1/me", "/api/v1/displays", "/api/v1/devices", "/api/v1/health", "/api/v1/clipboard" })
            {
                using var reply = await http.GetAsync(route);
                Check(reply.StatusCode == HttpStatusCode.Unauthorized, $"anonymous GET {route} is refused (401)");
            }

            string origin = $"https://{baseUri.Authority}";
            using (var noOrigin = await http.PostAsync("/api/v1/pair", JsonBody("""{"code":"00000000","label":"smoke"}""")))
                Check(noOrigin.StatusCode == HttpStatusCode.Forbidden, "pairing without an Origin header is refused (403)");
            using (var wrong = new HttpRequestMessage(HttpMethod.Post, "/api/v1/pair") { Content = JsonBody("""{"code":"00000000","label":"smoke"}""") })
            {
                wrong.Headers.Add("Origin", origin);
                using var reply = await http.SendAsync(wrong);
                Check(reply.StatusCode == HttpStatusCode.BadRequest, "pairing with a wrong code is refused (400)");
            }
            using (var rebound = new HttpRequestMessage(HttpMethod.Get, "/"))
            {
                rebound.Headers.Host = "attacker.example";
                using var reply = await http.SendAsync(rebound);
                Check(reply.StatusCode == HttpStatusCode.MisdirectedRequest, "an unknown Host header is refused (421, DNS-rebinding defense)");
            }

            foreach (var path in new[] { "/api/v1/stream", "/api/v1/video", "/api/v1/audio", "/api/v1/control" })
            {
                using var socket = new ClientWebSocket();
                socket.Options.RemoteCertificateValidationCallback = (_, presented, _, _) => Pinned(presented as X509Certificate2);
                socket.Options.SetRequestHeader("Origin", origin);
                socket.Options.AddSubProtocol(AbyssServer.SocketProtocol);
                socket.Options.AddSubProtocol(new string('0', 64));
                bool refused;
                try
                {
                    await socket.ConnectAsync(new Uri($"wss://{baseUri.Authority}{path}"), CancellationToken.None);
                    refused = false;
                }
                catch (WebSocketException) { refused = true; }
                Check(refused, $"anonymous WebSocket {path} is refused");
            }

            Console.WriteLine("SELF-TEST PASSED: HTTPS host, web UI, headers and anonymous-access checks.");
            Console.WriteLine("Screen capture, input and phone pairing are covered by manual testing (see docs/ROADMAP.md).");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"SELF-TEST FAILED: {ex.Message}");
            return 1;
        }
        finally
        {
            await runtime.StopAsync();
        }
    }

    private static StringContent JsonBody(string json) => new(json, Encoding.UTF8, "application/json");

    private static void Check(bool passed, string description)
    {
        if (!passed) throw new InvalidOperationException(description + " - FAILED");
        Console.WriteLine($"PASS: {description}");
    }
}
