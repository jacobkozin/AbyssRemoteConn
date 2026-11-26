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
    /// <summary>The root certificate that signed <see cref="Certificate"/>. Phones download its
    /// public part to trust this host. Null when there is no separate root (a test server with
    /// one self-signed certificate).</summary>
    public X509Certificate2? RootCertificate { get; init; }
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

    // Feature modules (see the Features folder). Each default is the real Windows
    // implementation; tests pass a temporary folder and fakes instead.
    public Features.TransferFolder Transfer { get; init; } = Features.TransferFolder.InDownloads();
    public Features.IHostActions Actions { get; init; } = new Features.WindowsHostActions();
    public Features.ISteam Steam { get; init; } = new Features.WindowsSteam();

    /// <summary>Away from home (1.2): the public address the Gaming Hub's proxy forwards here, and
    /// its one-time codes. Null while the setting is off; then that address is refused like any
    /// unknown host name.</summary>
    public AwayAccess? Away { get; init; }

    /// <summary>Whether the away address is being forwarded here right now (see
    /// <see cref="AwayProbe"/>). While it is not, no browser is sent to it.</summary>
    public Func<bool> AwayWorks { get; init; } = () => true;

    /// <summary>The paired device this request's session cookie belongs to, or null. A request
    /// through the away address must carry the device's away key; any other needs its home key.</summary>
    public DeviceRecord? Device(HttpContext ctx) =>
        State.Authenticate(ctx.Request.Cookies[AbyssServer.SessionCookie], AwayRequests.IsAway(ctx));

    /// <summary>The check for every state-changing request: paired cookie + exact Origin +
    /// this device's CSRF token in the X-Abyss-CSRF header.</summary>
    public bool Authorized(HttpContext ctx, DeviceRecord? device) =>
        device is not null && RequestProtection.SameOrigin(ctx) && Protection.ValidCsrf(ctx.Request.Headers["X-Abyss-CSRF"], device.Id);
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
            switch (AwayRequests.Route(ctx, c.Protection, c.Away))
            {
                case RequestRoute.Away:
                    AwayRequests.MarkAway(ctx);
                    // The away address has a public certificate (the proxy's), so browsers may pin HTTPS for it.
                    headers["Strict-Transport-Security"] = "max-age=31536000";
                    break;
                case RequestRoute.Refused:
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

    /// <summary>True while a browser can be sent to the away address: the PC has one and it is
    /// being forwarded here. A request that came through it proves that by itself.</summary>
    private static bool AwayOffered(ServerContext c, HttpContext ctx) => c.Away is not null && (AwayRequests.IsAway(ctx) || c.AwayWorks());

    private static void MapApi(WebApplication app, ServerContext c)
    {
        DeviceRecord? Device(HttpContext ctx) => c.Device(ctx);
        bool Authorized(HttpContext ctx, DeviceRecord? d) => c.Authorized(ctx, d);

        void DeleteCookie(HttpContext ctx) =>
            ctx.Response.Cookies.Delete(SessionCookie, new CookieOptions { Secure = true, HttpOnly = true, SameSite = SameSiteMode.Strict, Path = "/" });

        // For the pairing screen, which is the same at both addresses. away: this request came
        // through the away address. awayAvailable: the PC has one and it works; awayUrl is it.
        app.MapGet("/api/v1/info", (HttpContext ctx) =>
        {
            bool offered = AwayOffered(c, ctx);
            return Results.Ok(new { app = AppInfo.Name, version = AppInfo.Version, away = AwayRequests.IsAway(ctx), awayAvailable = offered, awayUrl = offered ? c.Away?.Url : null });
        });

        // The root certificate a phone has to trust before its browser accepts this host. No
        // pairing needed: Export(Cert) writes the public certificate only, never the private
        // key, and it is the same file the PC keeps in %LOCALAPPDATA%\AbyssRemote. The away
        // address has a public certificate, so it has no use for this one.
        app.MapGet("/AbyssRemote-RootCA.cer", (HttpContext ctx) => c.RootCertificate is null || AwayRequests.IsAway(ctx)
            ? Results.NotFound()
            : Results.File(c.RootCertificate.Export(X509ContentType.Cert), "application/x-x509-ca-cert", "AbyssRemote-RootCA.cer"));

        app.MapPost("/api/v1/pair", IResult (HttpContext ctx, PairRequest request) =>
        {
            if (!RequestProtection.SameOrigin(ctx)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            string label = StateStore.CleanName(request.Label ?? "");
            string code = (request.Code ?? "").Trim();
            if (code.Length == 0 || label.Length == 0)
                return Results.BadRequest(new { error = "Enter the pairing code and a name for this device." });
            // The same code pairs at home and at the away address. Away, the device's own
            // address is the one the proxy reports, so the limit per address still means something.
            bool away = AwayRequests.IsAway(ctx);
            string address = away ? AwayRequests.ClientAddress(ctx) : ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            switch (c.Pairing.TryUse(code, address))
            {
                case PairingResult.Paired:
                    break;
                case PairingResult.TooManyAttempts:
                    return Results.Json(new { error = "Too many tries from this device. Wait 5 minutes, then try again." }, statusCode: StatusCodes.Status429TooManyRequests);
                case PairingResult.Locked:
                    return Results.BadRequest(new { error = "Pairing is locked after too many wrong codes. Click New code in Abyss Remote on the PC." });
                default:
                    return Results.BadRequest(new { error = "That code is wrong or expired. Check the PC, or click New code in Abyss Remote there." });
            }
            var (id, secret) = c.State.Pair(label);
            var expires = DateTimeOffset.UtcNow + StateStore.DeviceLifetime;
            if (away && c.State.SetAwayKey(id) is { } key)
            {
                // A browser at the away address holds the device's away key, never its home key.
                (secret, expires) = (key.Secret, key.ExpiresUtc);
            }
            c.Notice(away ? $"Paired a new device at the away address: \"{label}\" from {address}." : $"Paired a new device: \"{label}\" from {address}.");
            ctx.Response.Cookies.Append(SessionCookie, $"{id}.{secret}", new CookieOptions
            {
                HttpOnly = true, Secure = true, SameSite = SameSiteMode.Strict,
                IsEssential = true, Path = "/", Expires = expires,
            });
            return Results.Ok(new { paired = true, name = label });
        });

        app.MapGet("/api/v1/me", IResult (HttpContext ctx) =>
        {
            var device = Device(ctx);
            if (device is null) return Results.Unauthorized();
            c.State.UpdateSeen(device.Id);
            return Results.Ok(new
            {
                id = device.Id, device = device.Name, csrf = c.Protection.Csrf(device.Id), version = AppInfo.Version,
                features = new { gameMode = c.Desktop.HardwareVideoAvailable },
                // available: the PC allows away access; here: this request came that way;
                // ready: this device has its away key.
                away = new { available = AwayOffered(c, ctx), url = c.Away?.Url, here = AwayRequests.IsAway(ctx), ready = device.AwayTokenHash is not null },
            });
        });

        // Away from home, step 1, at home: a paired device asks for a one-time code for itself.
        app.MapPost("/api/v1/away/code", IResult (HttpContext ctx) =>
        {
            var device = Device(ctx);
            if (!Authorized(ctx, device)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (c.Away is not { } away)
                return Results.Json(new { error = "Away access is turned off in Abyss Remote on the PC." }, statusCode: StatusCodes.Status409Conflict);
            if (AwayRequests.IsAway(ctx))
                return Results.Json(new { error = "That is only possible at home, at your home network address." }, statusCode: StatusCodes.Status403Forbidden);
            if (!c.AwayWorks())
                return Results.Json(new { error = "The away address does not answer right now: the web server on the PC that forwards it is not running." }, statusCode: StatusCodes.Status409Conflict);
            var (code, expires) = away.Codes.Issue(device!.Id);
            c.Notice($"\"{device.Name}\" is moving on to the away address.");
            // The code travels in the fragment, so it never reaches the proxy's or this server's request logs.
            return Results.Ok(new { code = AwayCodes.Format(code), url = $"{away.Url}/#away={code}", address = away.Url, expiresUtc = expires });
        });

        // Away from home, step 2, at the away address: the link the device brought from home
        // carries its code, which gives this browser the device's away key.
        app.MapPost("/api/v1/away/redeem", IResult (HttpContext ctx, AwayRedeemRequest request) =>
        {
            if (!AwayRequests.IsAway(ctx) || c.Away is not { } away) return Results.NotFound();
            if (!RequestProtection.SameOrigin(ctx)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            string from = AwayRequests.ClientAddress(ctx);
            var (result, deviceId) = away.Codes.TryUse(request.Code);
            if (result == AwayCodeResult.Locked)
            {
                c.Notice($"Moving on to the away address was locked after too many wrong tries (last from {from}). A device can ask again at home.");
                return Results.BadRequest(new { error = "Too many wrong codes. Open Abyss Remote at home and try again from there." });
            }
            var key = result == AwayCodeResult.Accepted ? c.State.SetAwayKey(deviceId!) : null;
            if (key is not { } given)
            {
                c.Notice($"Refused a wrong or expired link to the away address from {from}.");
                return Results.BadRequest(new { error = "That link is no longer good. Open Abyss Remote at home and try again from there, or pair this device here with a code from the PC." });
            }
            c.Notice($"\"{given.Name}\" can now connect away from home (set up from {from}).");
            ctx.Response.Cookies.Append(SessionCookie, $"{deviceId}.{given.Secret}", new CookieOptions
            {
                HttpOnly = true, Secure = true, SameSite = SameSiteMode.Strict,
                IsEssential = true, Path = "/", Expires = given.ExpiresUtc,
            });
            return Results.Ok(new { ready = true, name = given.Name });
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

        // Feature modules: each maps its own endpoints and uses c.Device / c.Authorized.
        Features.DisplayPreviewEndpoints.Map(app, c);
        Features.FileTransferEndpoints.Map(app, c);
        Features.HostActionEndpoints.Map(app, c);
        Features.SteamEndpoints.Map(app, c);

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
            source.IncludeCursor = ctx.Request.Query["cursor"] != "separate";
            await JpegStream.RunAsync(live, source, fps, () => c.State.IsActive(device.Id), ctx.RequestAborted);
        }));

        // Game mode video: hardware H.264, paced by the browser's acks (see VideoStream).
        app.Map("/api/v1/video", (HttpContext ctx) => RunSocketAsync(ctx, c, SocketKind.Video, async (live, device) =>
        {
            var settings = new VideoSettings(
                Display: QueryInt(ctx, "display", 0, 0, 64),
                Fps: QueryInt(ctx, "fps", 60, 10, 120),
                BitrateKbps: QueryInt(ctx, "bitrate", 20_000, 2_000, 80_000),
                MaxHeight: QueryInt(ctx, "height", 1080, 360, 2160));
            if (settings.Display >= c.Desktop.Displays().Count)
            {
                await live.CloseAsync((int)WebSocketCloseStatus.InvalidPayloadData, "That monitor does not exist");
                return;
            }
            IVideoSource source;
            try { source = c.Desktop.CreateVideoSource(settings); }
            catch (Exception ex) when (ex is GameMode.VideoUnsupportedException or GameMode.NativeCallException)
            {
                c.Notice($"Game mode is not available: {ex.Message}");
                await live.CloseAsync(CloseCodes.Unsupported, ex.Message.Length <= 120 ? ex.Message : ex.Message[..117] + "...");
                return;
            }
            catch (DesktopUnavailableException ex)
            {
                await live.SendJsonAsync(new { type = "unavailable", message = ex.Message }, ctx.RequestAborted);
                await live.CloseAsync(1013 /* try again later */, "Desktop unavailable");
                return;
            }
            using (source) await VideoStream.RunAsync(live, source, () => c.State.IsActive(device.Id), ctx.RequestAborted);
        }));

        // PC sound: a config message, then 10 ms chunks of 48 kHz stereo 16-bit PCM.
        app.Map("/api/v1/audio", (HttpContext ctx) => RunSocketAsync(ctx, c, SocketKind.Audio, async (live, device) =>
        {
            IAudioSource source;
            try { source = c.Desktop.CreateAudioSource(); }
            catch (Exception ex) when (ex is AudioUnavailableException or GameMode.NativeCallException)
            {
                await live.CloseAsync(CloseCodes.Unsupported, ex.Message.Length <= 120 ? ex.Message : ex.Message[..117] + "...");
                return;
            }
            using (source)
            {
                if (!await live.SendJsonAsync(new { type = "config", sampleRate = IAudioSource.SampleRate, channels = IAudioSource.Channels,
                        format = "s16le", chunkMs = 10 }, ctx.RequestAborted)) return;
                // The browser never sends anything here; reading just notices when it closes.
                using var closed = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
                var watch = WaitForCloseAsync(live, closed);
                try
                {
                    await foreach (var chunk in source.Chunks.ReadAllAsync(closed.Token))
                        if (!c.State.IsActive(device.Id) || !await live.SendAsync(chunk, WebSocketMessageType.Binary, closed.Token)) return;
                }
                finally
                {
                    closed.Cancel();
                    try { await watch; } catch (Exception ex) when (ex is OperationCanceledException or WebSocketException) { }
                }
            }
        }));

        // Mouse, keyboard and text input. Small JSON messages, see docs/API.md.
        app.Map("/api/v1/control", (HttpContext ctx) => RunSocketAsync(ctx, c, SocketKind.Control, async (live, device) =>
        {
            using var input = c.Desktop.CreateInputSink(message => _ = live.SendJsonAsync(message, CancellationToken.None));
            using var cursor = c.Desktop.CreateCursorSource();
            int pointerSequence = 0;
            long cursorReadAt = 0;
            var buffer = new byte[8192];
            while (live.Socket.State == WebSocketState.Open)
            {
                var message = await live.ReceiveAsync(buffer.AsMemory(), ctx.RequestAborted);
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
                    else if (InputCommand.Kind(root) == "cursor")
                    {
                        // A read-only request on the existing authenticated control socket.
                        // Rate-limit image work even for a paired client that floods requests.
                        if (cursorReadAt != 0 && System.Diagnostics.Stopwatch.GetElapsedTime(cursorReadAt).TotalMilliseconds < 12) continue;
                        cursorReadAt = System.Diagnostics.Stopwatch.GetTimestamp();
                        var snapshot = cursor?.Read();
                        var image = snapshot?.Image;
                        await live.SendJsonAsync(new { type = "cursorState", supported = snapshot is not null,
                            sequence = pointerSequence, x = snapshot?.X ?? 0, y = snapshot?.Y ?? 0,
                            visible = snapshot?.Visible ?? false,
                            image = image is null ? null : new { png = Convert.ToBase64String(image.Png),
                                hotX = image.HotX, hotY = image.HotY, width = image.Width, height = image.Height } }, ctx.RequestAborted);
                    }
                    else
                    {
                        input.Handle(root);
                        if (InputCommand.Kind(root) == "pointer")
                            pointerSequence = Math.Max(pointerSequence, InputCommand.Int(root, "sequence", 0, int.MaxValue));
                    }
                }
            }
        }));
    }

    /// <summary>The single gate for every WebSocket: paired cookie, exact Origin, and this
    /// device's CSRF token as the second requested subprotocol. Rejected requests get a
    /// plain HTTP 403 and never reach the desktop.</summary>
    internal static async Task RunSocketAsync(HttpContext ctx, ServerContext c, SocketKind kind, Func<LiveSocket, DeviceRecord, Task> body)
    {
        if (!ctx.WebSockets.IsWebSocketRequest)
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        var device = c.Device(ctx); // the home key, or the away key at the away address
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
        if (!c.State.IsActive(device.Id))
        {
            // Revoked after the cookie check but before Register, so CloseDevice never saw this socket.
            await live.CloseAsync(CloseCodes.Revoked, "This device is no longer paired");
            return;
        }
        try { await body(live, device); }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException or ObjectDisposedException)
        {
            // The browser went away; nothing to report.
        }
        finally { await live.CloseAsync((int)WebSocketCloseStatus.NormalClosure, "Ended"); }
    }

    /// <summary>Reads (and ignores) incoming messages until the browser closes the socket, then cancels.</summary>
    internal static async Task WaitForCloseAsync(LiveSocket live, CancellationTokenSource onClose)
    {
        var buffer = new byte[256];
        try
        {
            while (live.Socket.State == WebSocketState.Open)
                if ((await live.Socket.ReceiveAsync(buffer.AsMemory(), onClose.Token)).MessageType == WebSocketMessageType.Close) return;
        }
        finally { onClose.Cancel(); }
    }

    internal static int QueryInt(HttpContext ctx, string key, int fallback, int min, int max) =>
        int.TryParse(ctx.Request.Query[key], out int value) ? Math.Clamp(value, min, max) : fallback;
}

public sealed record PairRequest(string? Code, string? Label);
public sealed record ClipboardRequest(string? Text);
