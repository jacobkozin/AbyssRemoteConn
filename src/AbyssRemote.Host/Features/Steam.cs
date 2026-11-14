using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Security;
using Microsoft.Win32;

namespace AbyssRemote.Host.Features;

/// <summary>The Steam games installed on this PC, and a way to start one. The only thing a
/// device ever chooses is a game's number: nothing here takes a path, a program name or a
/// command line, so nothing a browser sends can turn into one.</summary>
public interface ISteam
{
    /// <summary>False when Steam is not installed on this PC.</summary>
    bool Installed { get; }

    /// <summary>The fully installed games, most recently played first, then by name.</summary>
    IReadOnlyList<SteamGame> Games();

    /// <summary>The cover picture of an installed game, or null when there is none.</summary>
    SteamArt? Art(uint appId);

    /// <summary>Asks Steam to start a game and returns it. Returns null, and starts nothing,
    /// when that number is not one of <see cref="Games"/>. Throws
    /// <see cref="SteamUnavailableException"/> when Steam itself cannot be started.</summary>
    SteamGame? Launch(uint appId);

    /// <summary>Switches Steam to Big Picture, its full-screen view made for a controller.
    /// Throws <see cref="SteamUnavailableException"/> when Steam cannot be started.</summary>
    void OpenBigPicture();

    /// <summary>Leaves Big Picture. Throws <see cref="SteamUnavailableException"/> when Steam
    /// cannot be started.</summary>
    void CloseBigPicture();
}

/// <summary>Steam is not installed or did not start. The message is shown to the user.</summary>
public sealed class SteamUnavailableException(string message) : Exception(message);

/// <summary>Where Steam lives on this PC: its folder and its program file.</summary>
public sealed record SteamInstallation(string Folder, string Program);

/// <summary>Steam on this PC. It finds Steam's folder and starts steam.exe; reading the game
/// files is <see cref="SteamLibrary"/>'s job.</summary>
public sealed class WindowsSteam : ISteam
{
    private readonly Func<SteamInstallation?> _find;
    private readonly Action<ProcessStartInfo> _start;

    public WindowsSteam() : this(FindInRegistry, start => Process.Start(start)?.Dispose()) { }

    /// <summary>For tests: a pretend installation, and a recorder in place of really starting
    /// a program.</summary>
    internal WindowsSteam(Func<SteamInstallation?> find, Action<ProcessStartInfo> start)
    {
        _find = find;
        _start = start;
    }

    public bool Installed => _find() is not null;

    public IReadOnlyList<SteamGame> Games() => Library()?.Games() ?? [];

    public SteamArt? Art(uint appId) => Library()?.Art(appId);

    public SteamGame? Launch(uint appId)
    {
        // Looked up in the installed games first: a number that is not one of them never
        // reaches Steam.
        var game = Library()?.Game(appId);
        if (game is not null) Run("steam://rungameid/" + game.Id.ToString(CultureInfo.InvariantCulture));
        return game;
    }

    public void OpenBigPicture() => Run("steam://open/bigpicture");

    public void CloseBigPicture() => Run("steam://close/bigpicture");

    /// <summary>Steam records where it is installed in the current user's registry, and looks
    /// there itself. Checked on every use, so installing Steam needs no restart here.</summary>
    internal static SteamInstallation? FindInRegistry()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (key?.GetValue("SteamPath") is not string folder || !Path.IsPathFullyQualified(folder)) return null;
            folder = Path.GetFullPath(folder); // the registry has it with forward slashes
            if (!Directory.Exists(folder)) return null;
            string program = key.GetValue("SteamExe") is string exe && Path.IsPathFullyQualified(exe)
                ? Path.GetFullPath(exe)
                : Path.Combine(folder, "steam.exe");
            return new SteamInstallation(folder, program);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private SteamLibrary? Library() => _find() is { } steam ? new SteamLibrary(steam.Folder) : null;

    /// <summary>Hands one of Steam's own "steam://" commands to steam.exe. A Steam that is
    /// already running picks the command up; otherwise Steam starts first.
    /// <para>steam.exe is started directly, with the command as its only argument. The command
    /// is never opened as a link through Windows, because that runs whichever program is
    /// registered for "steam://" links, and no command prompt is involved. Every command is
    /// built in this class from fixed text and, at most, a game's number.</para></summary>
    private void Run(string command)
    {
        var steam = _find() ?? throw new SteamUnavailableException("Steam is not installed on this PC.");
        var start = new ProcessStartInfo { FileName = steam.Program, WorkingDirectory = steam.Folder, UseShellExecute = false };
        start.ArgumentList.Add(command);
        try { _start(start); }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            throw new SteamUnavailableException("Steam could not be started on this PC.");
        }
    }
}

public static class SteamEndpoints
{
    public static void Map(WebApplication app, ServerContext c)
    {
        var steam = c.Steam;

        app.MapGet("/api/v1/steam", (HttpContext ctx) => c.Device(ctx) is null
            ? Results.Unauthorized()
            : Results.Ok(new { installed = steam.Installed, games = steam.Games() }));

        // A plain <img> can load this: reading a cover needs the session cookie only.
        app.MapGet("/api/v1/steam/games/{id}/art", IResult (HttpContext ctx, string id) =>
        {
            if (c.Device(ctx) is null) return Results.Unauthorized();
            var art = SteamLibrary.TryParseId(id, out uint appId) ? steam.Art(appId) : null;
            if (art is null) return Results.NotFound();
            // Every other answer is "no-store". A cover almost never changes, so this device's
            // browser may keep it for a day ("private": no shared cache in between may).
            ctx.Response.Headers.CacheControl = "private, max-age=86400";
            return Results.Bytes(art.Data, art.ContentType);
        });

        app.MapPost("/api/v1/steam/games/{id}/launch", IResult (HttpContext ctx, string id) =>
        {
            var device = c.Device(ctx);
            if (!c.Authorized(ctx, device)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            try
            {
                var game = SteamLibrary.TryParseId(id, out uint appId) ? steam.Launch(appId) : null;
                if (game is null) return Results.NotFound(new { error = "That game is not installed on this PC." });
                c.Notice($"\"{device!.Name}\" started \"{game.Name}\" through Steam.");
                return Results.Ok(new { launched = true, name = game.Name });
            }
            catch (SteamUnavailableException ex) { return Unavailable(ex); }
        });

        app.MapPost("/api/v1/steam/bigpicture", (HttpContext ctx) => BigPicture(ctx, open: true));
        app.MapPost("/api/v1/steam/bigpicture/close", (HttpContext ctx) => BigPicture(ctx, open: false));

        IResult BigPicture(HttpContext ctx, bool open)
        {
            var device = c.Device(ctx);
            if (!c.Authorized(ctx, device)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            try
            {
                if (open) steam.OpenBigPicture();
                else steam.CloseBigPicture();
            }
            catch (SteamUnavailableException ex) { return Unavailable(ex); }
            c.Notice($"\"{device!.Name}\" {(open ? "opened" : "closed")} Steam Big Picture.");
            return open ? Results.Ok(new { opened = true }) : Results.Ok(new { closed = true });
        }
    }

    private static IResult Unavailable(SteamUnavailableException problem) =>
        Results.Json(new { error = problem.Message }, statusCode: StatusCodes.Status503ServiceUnavailable);
}
