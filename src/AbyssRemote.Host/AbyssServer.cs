using System.Net;
using System.Net.WebSockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.AspNetCore.Server.Kestrel.Https;

namespace AbyssRemote.Host;

/// <summary>Everything the web server needs. Production passes the Windows implementations;
/// tests pass fakes and a throwaway certificate.</summary>
public sealed class ServerContext
{
    public required LaunchOptions Options { get; init; }
    public required X509Certificate2 Certificate { get; init; }
    public required StateStore State { get; init; }
    public required PairingManager Pairing { get; init; }
    public required RequestProtection Protection { get; init; }
    public required SessionRegistry Sessions { get; init; }
    public required IDesktop Desktop { get; init; }
    public required IClipboard Clipboard { get; init; }
    public required string WebRoot { get; init; }

    /// <summary>Security-relevant events for the host console: pairings, revocations, refusals.</summary>
    public Action<string> Notice { get; init; } = _ => { };
    public bool ConsoleLogging { get; init; } = true;
}

public static class AbyssServer
{
    public const string SessionCookie = "abyss_remote_session";
    public const string SocketProtocol = "abyss-v1";

    public static WebApplication Build(ServerContext c)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = [],
            // Always Production: the Development error page would show stack traces to clients.
            EnvironmentName = Environments.Production,
            ContentRootPath = AppContext.BaseDirectory,
            WebRootPath = c.WebRoot,
        });
        builder.Logging.ClearProviders();
        if (c.ConsoleLogging)
        {
            builder.Logging.AddSimpleConsole(o => o.SingleLine = true);
            builder.Logging.AddFilter("Microsoft", LogLevel.Warning); // per-request logs are noise here
        }
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.AddServerHeader = false;
            k.Limits.MaxRequestBodySize = 64 * 1024;
            k.Limits.MaxConcurrentConnections = 64;
            k.Listen(c.Options.Lan ? IPAddress.Any : IPAddress.Loopback, c.Options.Port, listen =>
            {
                if (c.Options.Lan) listen.Use(next => connection => NetworkGuard.FilterAsync(connection, next, c.Notice));
                listen.UseHttps(new HttpsConnectionAdapterOptions
                {
                    ServerCertificate = c.Certificate,
                    SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                });
            });
        });

        var app = builder.Build();
        app.Use(async (ctx, next) =>
        {
            var headers = ctx.Response.Headers;
            headers["X-Content-Type-Options"] = "nosniff";
            headers["Referrer-Policy"] = "no-referrer";
            headers["X-Frame-Options"] = "DENY";
            headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
            headers["Content-Security-Policy"] = "default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self' blob: data:; " +
                "connect-src 'self' wss:; manifest-src 'self'; base-uri 'none'; form-action 'self'; frame-ancestors 'none'";
            headers["Cache-Control"] = "no-store";
            if (!c.Protection.KnownHost(ctx))
            {
                ctx.Response.StatusCode = StatusCodes.Status421MisdirectedRequest;
                return;
            }
            await next();
        });
        // Pings every 10 s; a peer that stops answering (phone asleep, Wi-Fi gone) is dropped
        // after 15 s more, which frees its slot for the reconnect.
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(10), KeepAliveTimeout = TimeSpan.FromSeconds(15) });
        app.UseDefaultFiles();
        app.UseStaticFiles();
        MapApi(app, c);
        return app;
    }

    private static void MapApi(WebApplication app, ServerContext c)
    {
        DeviceRecord? Device(HttpContext ctx) => c.State.Authenticate(ctx.Request.Cookies[SessionCookie]);

        // Every state-changing request: paired cookie + exact Origin + this device's CSRF token.
        bool Authorized(HttpContext ctx, DeviceRecord? d) =>
            d is not null && RequestProtection.SameOrigin(ctx) && c.Protection.ValidCsrf(ctx.Request.Headers["X-Abyss-CSRF"], d.Id);

        void DeleteCookie(HttpContext ctx) =>
            ctx.Response.Cookies.Delete(SessionCookie, new CookieOptions { Secure = true, HttpOnly = true, SameSite = SameSiteMode.Strict, Path = "/" });

        app.MapGet("/api/v1/info", () => Results.Ok(new { app = AppInfo.Name, version = AppInfo.Version }));

        app.MapPost("/api/v1/pair", IResult (HttpContext ctx, PairRequest request) =>
        {
            if (!RequestProtection.SameOrigin(ctx)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            string label = StateStore.CleanName(request.Label ?? "");
            string code = (request.Code ?? "").Trim();
            if (code.Length == 0 || label.Length == 0)
                return Results.BadRequest(new { error = "Enter the pairing code and a name for this device." });
            string address = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            switch (c.Pairing.TryUse(code, address))
            {
                case PairingResult.Paired:
                    break;
                case PairingResult.TooManyAttempts:
                    return Results.Json(new { error = "Too many tries from this device. Wait 5 minutes, then try again." }, statusCode: StatusCodes.Status429TooManyRequests);
                case PairingResult.Locked:
                    return Results.BadRequest(new { error = "Pairing is locked after too many wrong codes. Press P on the PC for a new code." });
                default:
                    return Results.BadRequest(new { error = "That code is wrong or expired. Check the PC, or press P there for a new code." });
            }
            var (id, secret) = c.State.Pair(label);
            c.Notice($"Paired a new device: \"{label}\" from {address}.");
            ctx.Response.Cookies.Append(SessionCookie, $"{id}.{secret}", new CookieOptions
            {
                HttpOnly = true, Secure = true, SameSite = SameSiteMode.Strict,
                IsEssential = true, Path = "/", Expires = DateTimeOffset.UtcNow + StateStore.DeviceLifetime,
            });
            return Results.Ok(new { paired = true, name = label });
        });

        app.MapGet("/api/v1/me", IResult (HttpContext ctx) =>
        {
            var device = Device(ctx);
            if (device is null) return Results.Unauthorized();
            c.State.UpdateSeen(device.Id);
            return Results.Ok(new { id = device.Id, device = device.Name, csrf = c.Protection.Csrf(device.Id), version = AppInfo.Version });
        });

        app.MapGet("/api/v1/displays", (HttpContext ctx) =>
            Device(ctx) is null ? Results.Unauthorized() : Results.Ok(c.Desktop.Displays()));

        app.MapGet("/api/v1/devices", (HttpContext ctx) =>
            Device(ctx) is null ? Results.Unauthorized() : Results.Ok(c.State.Devices()));

        app.MapDelete("/api/v1/devices/{id}", IResult (HttpContext ctx, string id) =>
        {
            var device = Device(ctx);
            if (!Authorized(ctx, device)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (!c.State.Revoke(id)) return Results.NotFound();
            c.Sessions.CloseDevice(id);
            c.Notice($"Device {id[..8]} was revoked by \"{device!.Name}\".");
            if (device.Id == id) DeleteCookie(ctx);
            return Results.Ok(new { revoked = true });
        });

        app.MapPost("/api/v1/logout", IResult (HttpContext ctx) =>
        {
            var device = Device(ctx);
            if (!Authorized(ctx, device)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            c.State.Revoke(device!.Id);
            c.Sessions.CloseDevice(device.Id);
            c.Notice($"\"{device.Name}\" signed out.");
            DeleteCookie(ctx);
            return Results.Ok(new { loggedOut = true });
        });

        app.MapGet("/api/v1/health", (HttpContext ctx) =>
            Device(ctx) is null ? Results.Unauthorized() : Results.Ok(new { healthy = true, time = DateTimeOffset.UtcNow }));

        app.MapGet("/api/v1/clipboard", IResult (HttpContext ctx) =>
        {
            if (Device(ctx) is null) return Results.Unauthorized();
            try { return Results.Ok(new { text = c.Clipboard.Read() }); }
            catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
            { return Results.Json(new { error = "The Windows clipboard is busy. Try again." }, statusCode: StatusCodes.Status503ServiceUnavailable); }
        });

        app.MapPost("/api/v1/clipboard", IResult (HttpContext ctx, ClipboardRequest request) =>
        {
            if (!Authorized(ctx, Device(ctx))) return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (request.Text is null || request.Text.Length > 4096) return Results.BadRequest(new { error = "Clipboard text is limited to 4,096 characters." });
            try { c.Clipboard.Write(request.Text); return Results.Ok(new { updated = true }); }
            catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
            { return Results.Json(new { error = "The Windows clipboard is busy. Try again." }, statusCode: StatusCodes.Status503ServiceUnavailable); }
        });

        // Desktop mode video: JPEG frames, one in flight (see JpegStream).
        app.Map("/api/v1/stream", (HttpContext ctx) => RunSocketAsync(ctx, c, SocketKind.Video, async (live, device) =>
        {
            int display = QueryInt(ctx, "display", 0, 0, 64);
            int fps = QueryInt(ctx, "fps", 18, 1, 30);
            int quality = QueryInt(ctx, "quality", 85, 30, 90);
            int width = QueryInt(ctx, "width", 1920, 480, 3840);
            if (display >= c.Desktop.Displays().Count)
            {
                await live.CloseAsync((int)WebSocketCloseStatus.InvalidPayloadData, "That monitor does not exist");
                return;
            }
            using var source = c.Desktop.CreateFrameSource(display, width, quality);
            await JpegStream.RunAsync(live, source, fps, () => c.State.IsActive(device.Id), ctx.RequestAborted);
        }));

        // Mouse, keyboard and text input. Small JSON messages, see docs/API.md.
        app.Map("/api/v1/control", (HttpContext ctx) => RunSocketAsync(ctx, c, SocketKind.Control, async (live, device) =>
        {
            using var input = c.Desktop.CreateInputSink(message => _ = live.SendJsonAsync(message, CancellationToken.None));
            var buffer = new byte[8192];
            while (live.Socket.State == WebSocketState.Open)
            {
                var message = await live.Socket.ReceiveAsync(buffer.AsMemory(), ctx.RequestAborted);
                if (message.MessageType == WebSocketMessageType.Close || !c.State.IsActive(device.Id)) return;
                if (message.MessageType != WebSocketMessageType.Text || !message.EndOfMessage)
                {
                    await live.CloseAsync((int)WebSocketCloseStatus.MessageTooBig, "Control messages are small JSON text");
                    return;
                }
                JsonDocument json;
                try { json = JsonDocument.Parse(buffer.AsMemory(0, message.Count)); }
                catch (JsonException) { continue; } // ignore malformed input
                using (json)
                {
                    var root = json.RootElement;
                    if (InputCommand.Kind(root) == "ping")
                        await live.SendJsonAsync(new { type = "pong", t = InputCommand.Double(root, "t", 0, double.MaxValue) }, ctx.RequestAborted);
                    else
                        input.Handle(root);
                }
            }
        }));
    }

    /// <summary>The single gate for every WebSocket: paired cookie, exact Origin, and this
    /// device's CSRF token as the second requested subprotocol. Rejected requests get a
    /// plain HTTP 403 and never reach the desktop.</summary>
    private static async Task RunSocketAsync(HttpContext ctx, ServerContext c, SocketKind kind, Func<LiveSocket, DeviceRecord, Task> body)
    {
        if (!ctx.WebSockets.IsWebSocketRequest)
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        var device = c.State.Authenticate(ctx.Request.Cookies[SessionCookie]);
        var protocols = ctx.WebSockets.WebSocketRequestedProtocols;
        if (device is null || !RequestProtection.SameOrigin(ctx) || protocols.Count != 2
            || protocols[0] != SocketProtocol || !c.Protection.ValidCsrf(protocols[1], device.Id))
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        using var socket = await ctx.WebSockets.AcceptWebSocketAsync(SocketProtocol);
        var live = new LiveSocket(socket);
        using var registration = c.Sessions.Register(device.Id, kind, live);
        if (registration is null)
        {
            await live.CloseAsync(CloseCodes.Busy, "Too many connections to this PC");
            return;
        }
        try { await body(live, device); }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException or ObjectDisposedException)
        {
            // The browser went away; nothing to report.
        }
        finally { await live.CloseAsync((int)WebSocketCloseStatus.NormalClosure, "Ended"); }
    }

    private static int QueryInt(HttpContext ctx, string key, int fallback, int min, int max) =>
        int.TryParse(ctx.Request.Query[key], out int value) ? Math.Clamp(value, min, max) : fallback;
}

public sealed record PairRequest(string? Code, string? Label);
public sealed record ClipboardRequest(string? Text);
