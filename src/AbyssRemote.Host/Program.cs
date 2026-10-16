using System.Net;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AbyssRemote.Host;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;

const int Port = 8443;
const string SessionCookie = "abyss_remote_session";

try { NativeDpi.Enable(); } catch { /* Windows can already be DPI-aware. */ }

var isLan = args.Contains("--lan", StringComparer.OrdinalIgnoreCase);
var isSmokeTest = args.Contains("--smoke-test", StringComparer.OrdinalIgnoreCase);
var stateDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AbyssRemote");
var state = new StateStore(stateDir);
var (cert, rootCert) = HostCertificates.GetOrCreate(state, stateDir, args.Contains("--renew-cert", StringComparer.OrdinalIgnoreCase));
var pairing = new PairingManager();
var protection = new RequestProtection();
var desktop = new DesktopEngine();
var streamSlots = new SemaphoreSlim(2, 2);
var controlSlots = new SemaphoreSlim(2, 2);

// Host-specific flags are handled above, not passed into the ASP.NET command-line configuration parser.
// Configure both roots at creation time. ASP.NET Core forbids changing WebRoot afterwards.
// Using BaseDirectory also makes self-contained publish work from arbitrary launch locations.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = Array.Empty<string>(),
    ContentRootPath = AppContext.BaseDirectory,
    WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot")
});
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.WebHost.ConfigureKestrel(k =>
{
    k.Limits.MaxRequestBodySize = 8192;
    k.Listen(isLan ? IPAddress.Any : IPAddress.Loopback, Port, listen => listen.UseHttps(cert));
});
if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "wwwroot", "index.html")))
    throw new InvalidOperationException("Published web UI is missing. Run BUILD-WINDOWS.bat to rebuild the complete project.");
var app = builder.Build();

app.Use(async (ctx, next) =>
{
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
    ctx.Response.Headers["X-Frame-Options"] = "DENY";
    ctx.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
    ctx.Response.Headers["Content-Security-Policy"] = "default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self' blob:; connect-src 'self' wss:; base-uri 'none'; form-action 'self'; frame-ancestors 'none'";
    ctx.Response.Headers["Cache-Control"] = "no-store";
    if (!ctx.Request.IsHttps) { ctx.Response.StatusCode = 400; return; }
    await next();
});
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });
app.UseDefaultFiles();
app.UseStaticFiles();

DeviceRecord? Device(HttpContext ctx) => state.Authenticate(ctx.Request.Cookies[SessionCookie]);
bool AuthorizedPost(HttpContext ctx, DeviceRecord? d) =>
    d is not null && RequestProtection.SameOrigin(ctx) && protection.ValidCsrf(ctx.Request.Headers["X-Abyss-CSRF"], d.Id);

app.MapGet("/api/v1/info", () => Results.Ok(new { app = "Abyss Remote", version = "0.3.3", pairing = "on-demand", lan = isLan }));

app.MapPost("/api/v1/pair", IResult (HttpContext ctx, PairRequest request) =>
{
    if (!RequestProtection.SameOrigin(ctx)) return Results.StatusCode(403);
    if (request.Code is null || request.Label is null || request.Label.Length is < 1 or > 60)
        return Results.BadRequest(new { error = "Enter your pairing code and device name." });
    var addr = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    if (!pairing.TryUse(request.Code, addr)) return Results.BadRequest(new { error = "Invalid, expired, or rate-limited pairing code. Press P on the host to generate another." });
    var (id, secret) = state.Pair(request.Label);
    ctx.Response.Cookies.Append(SessionCookie, $"{id}.{secret}", new CookieOptions
    {
        HttpOnly = true, Secure = true, SameSite = SameSiteMode.Strict,
        IsEssential = true, Path = "/", Expires = DateTimeOffset.UtcNow.AddDays(30)
    });
    return Results.Ok(new { paired = true, name = request.Label });
});

app.MapGet("/api/v1/me", IResult (HttpContext ctx) =>
{
    var device = Device(ctx);
    if (device is null) return Results.Unauthorized();
    state.UpdateSeen(device.Id);
    return Results.Ok(new { id = device.Id, device = device.Name, csrf = protection.Csrf(device.Id), version = "0.3.3" });
});

app.MapGet("/api/v1/displays", (HttpContext ctx) =>
    Device(ctx) is null ? (IResult)Results.Unauthorized() : Results.Ok(desktop.Displays()));

app.MapGet("/api/v1/devices", (HttpContext ctx) =>
    Device(ctx) is null ? (IResult)Results.Unauthorized() : Results.Ok(state.Devices()));

app.MapDelete("/api/v1/devices/{id}", IResult (HttpContext ctx, string id) =>
{
    var device = Device(ctx);
    if (!AuthorizedPost(ctx, device)) return Results.StatusCode(403);
    if (!state.Revoke(id)) return Results.NotFound();
    if (device!.Id == id) ctx.Response.Cookies.Delete(SessionCookie, new CookieOptions { Secure = true, SameSite = SameSiteMode.Strict, Path = "/" });
    return Results.Ok(new { revoked = true });
});

app.MapPost("/api/v1/logout", IResult (HttpContext ctx) =>
{
    var device = Device(ctx);
    if (!AuthorizedPost(ctx, device)) return Results.StatusCode(403);
    state.Revoke(device!.Id);
    ctx.Response.Cookies.Delete(SessionCookie, new CookieOptions { Secure = true, SameSite = SameSiteMode.Strict, Path = "/" });
    return Results.Ok(new { loggedOut = true });
});

app.MapGet("/api/v1/health", (HttpContext ctx) =>
    Device(ctx) is null ? (IResult)Results.Unauthorized() : Results.Ok(new { healthy = true, time = DateTimeOffset.UtcNow }));

app.MapGet("/api/v1/clipboard", IResult (HttpContext ctx) =>
{
    if (Device(ctx) is null) return Results.Unauthorized();
    try { return Results.Ok(new { text = DesktopClipboard.Read() }); }
    catch (Exception ex) when (ex is InvalidOperationException or TimeoutException) { return Results.Problem("Windows clipboard is busy or inaccessible.", statusCode: 503); }
});
app.MapPost("/api/v1/clipboard", IResult (HttpContext ctx, ClipboardRequest request) =>
{
    if (!AuthorizedPost(ctx, Device(ctx))) return Results.StatusCode(403);
    if (request.Text is null || request.Text.Length > 4096) return Results.BadRequest(new { error = "Text is limited to 4,096 characters." });
    try { DesktopClipboard.Write(request.Text); return Results.Ok(new { updated = true }); }
    catch (Exception ex) when (ex is InvalidOperationException or TimeoutException) { return Results.Problem("Windows clipboard is busy or inaccessible.", statusCode: 503); }
});

// Video channel: authenticated, same-origin, single-frame-in-flight.
// The client explicitly requests its next JPEG after it finished drawing the last one.
// This prevents unbounded old frames backing up inside TCP/WebSocket on slow iOS devices.
app.Map("/api/v1/stream", async (HttpContext ctx) =>
{
    var device = Device(ctx);
    if (device is null || !RequestProtection.SameOrigin(ctx)) { ctx.Response.StatusCode = 403; return; }
    if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
    int display = QueryInt(ctx, "display", 0, 0, 64);
    int fps = QueryInt(ctx, "fps", 18, 1, 24);
    int quality = QueryInt(ctx, "quality", 87, 30, 90);
    int maxWidth = QueryInt(ctx, "width", 1920, 480, 3200);
    if (display >= desktop.Displays().Count) { ctx.Response.StatusCode = 400; return; }
    if (!await streamSlots.WaitAsync(0, ctx.RequestAborted)) { ctx.Response.StatusCode = 429; return; }
    try
    {
        using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
        using var capture = desktop.CreateCaptureSession(display, maxWidth, quality);
        var request = new byte[32];
        var clock = Stopwatch.StartNew();
        var nextAt = TimeSpan.Zero;
        try
        {
            while (socket.State == WebSocketState.Open && !ctx.RequestAborted.IsCancellationRequested && state.StillPaired(device.Id))
            {
                var message = await socket.ReceiveAsync(new ArraySegment<byte>(request), ctx.RequestAborted);
                if (message.MessageType == WebSocketMessageType.Close) break;
                if (!state.StillPaired(device.Id)) break;
                if (message.MessageType != WebSocketMessageType.Text || !message.EndOfMessage ||
                    message.Count != 4 || request[0] != (byte)'n' || request[1] != (byte)'e' ||
                    request[2] != (byte)'x' || request[3] != (byte)'t')
                {
                    await socket.CloseOutputAsync(WebSocketCloseStatus.InvalidPayloadData, "Expected frame request", ctx.RequestAborted);
                    break;
                }
                // Limit capture work to configured FPS even if the browser ACKs rapidly.
                var remaining = nextAt - clock.Elapsed;
                if (remaining > TimeSpan.Zero) await Task.Delay(remaining, ctx.RequestAborted);
                if (!state.StillPaired(device.Id)) break;
                var jpeg = capture.Capture();
                if (!state.StillPaired(device.Id)) break;
                await socket.SendAsync(new ArraySegment<byte>(jpeg), WebSocketMessageType.Binary, true, ctx.RequestAborted);
                nextAt = clock.Elapsed + TimeSpan.FromSeconds(1.0 / fps);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException
                                   or ExternalException or InvalidOperationException or ArgumentOutOfRangeException)
        {
            Console.Error.WriteLine($"Stream ended: {ex.GetType().Name}: {ex.Message}");
        }
        finally { await EndSocket(socket); }
    }
    finally { streamSlots.Release(); }
});

// Control channel: same-origin, paired cookie and per-device challenge on WebSocket upgrade.
app.Map("/api/v1/control", async (HttpContext ctx) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
    var device = Device(ctx);
    if (device is null || !RequestProtection.SameOrigin(ctx)
        || ctx.WebSockets.WebSocketRequestedProtocols.Count != 2
        || ctx.WebSockets.WebSocketRequestedProtocols[0] != "abyss-v1"
        || !protection.ValidCsrf(ctx.WebSockets.WebSocketRequestedProtocols[1], device.Id))
    {
        ctx.Response.StatusCode = 403;
        return;
    }
    if (!await controlSlots.WaitAsync(0, ctx.RequestAborted)) { ctx.Response.StatusCode = 429; return; }
    try
    {
        using var socket = await ctx.WebSockets.AcceptWebSocketAsync("abyss-v1");
        var buffer = new byte[8192];
        try
        {
            while (socket.State == WebSocketState.Open && !ctx.RequestAborted.IsCancellationRequested && state.StillPaired(device.Id))
            {
                var response = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ctx.RequestAborted);
                if (response.MessageType == WebSocketMessageType.Close) break;
                if (!state.StillPaired(device.Id)) break;
                if (response.MessageType != WebSocketMessageType.Text || !response.EndOfMessage)
                {
                    await socket.CloseOutputAsync(WebSocketCloseStatus.MessageTooBig, "Invalid control message", ctx.RequestAborted);
                    break;
                }
                try
                {
                    using var json = JsonDocument.Parse(buffer.AsMemory(0, response.Count));
                    if (!state.StillPaired(device.Id)) break;
                    desktop.Handle(json.RootElement);
                }
                catch (JsonException) { /* Discard malformed input. */ }
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException)
        {
            Console.Error.WriteLine($"Control disconnected: {ex.GetType().Name}");
        }
        finally
        {
            // Never leave mouse buttons pressed after a lost mobile session.
            NativeRelease.ReleaseButtons();
            await EndSocket(socket);
        }
    }
    finally { controlSlots.Release(); }
});

// Fully integrated startup verification. Runs inside .NET rather than Windows PowerShell 5.1,
// whose scriptblock certificate callbacks can fail on a TLS worker thread. This uses the REAL
// HTTPS server, the REAL published assets and the REAL routing/authentication middleware.
// The smoke client pins the exact certificate presented by this process: it does not bypass
// certificate verification for any other server and never changes browser trust settings.
if (isSmokeTest)
{
    Console.WriteLine("SELF-TEST: starting HTTPS host on 127.0.0.1:8443...");
    await app.StartAsync();
    try
    {
        var expectedCertHash = cert.GetCertHash(HashAlgorithmName.SHA256);
        using var handler = new HttpClientHandler
        {
            UseProxy = false, // Loopback check must not route through a Windows proxy.
            ServerCertificateCustomValidationCallback = (_, presented, _, _) =>
                presented is not null && CryptographicOperations.FixedTimeEquals(
                    presented.GetCertHash(HashAlgorithmName.SHA256), expectedCertHash)
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
        const string local = "https://127.0.0.1:8443";

        using (var infoReply = await client.GetAsync(local + "/api/v1/info"))
        {
            var payload = await infoReply.Content.ReadAsStringAsync();
            if (infoReply.StatusCode != HttpStatusCode.OK)
                throw new InvalidOperationException($"SELF-TEST: GET /api/v1/info expected 200; received {(int)infoReply.StatusCode}. Body: {payload}");
            using var infoJson = JsonDocument.Parse(payload);
            if (!infoJson.RootElement.TryGetProperty("app", out var identity) || identity.GetString() != "Abyss Remote")
                throw new InvalidOperationException($"SELF-TEST: unexpected app identity. Body: {payload}");
            Console.WriteLine("PASS: HTTPS handshake with this host's pinned certificate and correct /api/v1/info identity");
        }

        using (var homeReply = await client.GetAsync(local + "/"))
        {
            var html = await homeReply.Content.ReadAsStringAsync();
            if (homeReply.StatusCode != HttpStatusCode.OK || !html.Contains("Abyss Remote", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"SELF-TEST: web UI not served correctly (HTTP {(int)homeReply.StatusCode}).");
            Console.WriteLine("PASS: published browser UI is served via HTTPS");
        }

        foreach (var route in new[] { "/api/v1/me", "/api/v1/displays", "/api/v1/devices", "/api/v1/health" })
        {
            using var reply = await client.GetAsync(local + route);
            if (reply.StatusCode != HttpStatusCode.Unauthorized)
                throw new InvalidOperationException($"SELF-TEST: {route} should reject unauthenticated access (401), got {(int)reply.StatusCode}.");
            Console.WriteLine($"PASS: anonymous GET {route} rejected (401)");
        }

        Console.WriteLine("SELF-TEST PASSED: HTTPS host, web UI, identity and protected routes.");
        Console.WriteLine("Live screen capture, input and iOS pairing require separate device testing.");
    }
    finally
    {
        await app.StopAsync();
    }
    return;
}

string pin = pairing.Rotate();
Console.WriteLine("\n===================================");
Console.WriteLine("         ABYSS REMOTE R3.3     ");
Console.WriteLine("===================================");
Console.WriteLine($"Access mode : {(isLan ? "LAN (private networks only)" : "THIS COMPUTER ONLY")}");
Console.WriteLine($"URL         : https://localhost:{Port}");
if (isLan) Console.WriteLine("iPhone/iPad: https://YOUR-PC-LAN-IP:8443 (same Wi-Fi)");
Console.WriteLine($"PAIR CODE   : {pin} (10 minutes; single use)");
Console.WriteLine($"TLS SHA-256 : {cert.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256)}");
Console.WriteLine($"ROOT SHA-256: {rootCert.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256)}");
Console.WriteLine($"iOS root CA : {Path.Combine(stateDir, "AbyssRemote-RootCA.cer")}");
Console.WriteLine("Press P for new PIN, R to revoke ALL devices, Q to stop. Keep console running.");
Console.WriteLine("DO NOT port-forward this experimental release to the public internet.\n");

_ = Task.Run(() =>
{
    while (true)
    {
        try
        {
            var key = Console.ReadKey(intercept: true).Key;
            if (key == ConsoleKey.P) Console.WriteLine($"New pairing code: {pairing.Rotate()} (10 minutes; single use)");
            if (key == ConsoleKey.R) Console.WriteLine($"Revoked {state.RevokeAll()} paired device(s). Press P for a new pairing code.");
            if (key == ConsoleKey.Q) { _ = app.StopAsync(); break; }
        }
        catch (InvalidOperationException) { break; } // Headless/redirected console.
    }
});
await app.RunAsync();

static async Task EndSocket(WebSocket socket)
{
    if (socket.State != WebSocketState.Open) return;
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
    try { await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Ended", timeout.Token); }
    catch (WebSocketException) { /* Already disconnected. */ }
    catch (OperationCanceledException) { /* Avoid hanging on lost connection. */ }
}

static int QueryInt(HttpContext ctx, string key, int fallback, int min, int max) =>
    int.TryParse(ctx.Request.Query[key], out int value) ? Math.Clamp(value, min, max) : fallback;

public sealed record PairRequest(string? Code, string? Label);
public sealed record ClipboardRequest(string? Text);

internal static class NativeDpi
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    internal static void Enable() => SetProcessDpiAwarenessContext(new IntPtr(-4));
}

internal static class NativeRelease
{
    internal static void ReleaseButtons()
    {
        AbyssRemote.Host.NativeInput.Mouse(0, false);
        AbyssRemote.Host.NativeInput.Mouse(1, false);
        AbyssRemote.Host.NativeInput.Mouse(2, false);
    }
}
