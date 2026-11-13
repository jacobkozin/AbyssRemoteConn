using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace AbyssRemote.Host.Features;

/// <summary>A short, fixed list of things a paired device may ask the PC to do (for example
/// lock the screen). Never a way to run arbitrary commands: there is one method per action and
/// none of them takes a parameter, so nothing a browser sends can turn into a command line, a
/// path or a program name.</summary>
public interface IHostActions
{
    /// <summary>Locks the screen, like Win+L. Throws <see cref="HostActionException"/> when
    /// Windows refuses.</summary>
    void Lock();

    /// <summary>Puts the PC to sleep and returns once it is awake again. Throws
    /// <see cref="HostActionException"/> when Windows refuses.</summary>
    void Sleep();
}

/// <summary>Windows did not do what was asked. The message is shown to the user.</summary>
public sealed class HostActionException(string message) : Exception(message);

public sealed class WindowsHostActions : IHostActions
{
    /// <summary>How long the PC stays awake after it was asked to sleep. The answer to the phone
    /// is sent just before; a PC that is already suspending might never get it out, and the
    /// phone would report an error for something that worked.</summary>
    public static readonly TimeSpan SleepHeadStart = TimeSpan.FromSeconds(1.5);

    public void Lock()
    {
        // Only starts the lock; Windows switches to the lock screen a moment later.
        if (!LockWorkStation())
            throw new HostActionException("Windows did not lock the screen.");
    }

    public void Sleep()
    {
        Thread.Sleep(SleepHeadStart);
        // Windows Forms wraps SetSuspendState (powrprof.dll): sleep rather than hibernate, and
        // wake events stay on so the keyboard, the mouse or the power button wake the PC.
        if (!Application.SetSuspendState(PowerState.Suspend, force: false, disableWakeEvent: false))
            throw new HostActionException("Windows did not put the PC to sleep. Sleep may be turned off on this PC.");
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool LockWorkStation();
}

public static class HostActionEndpoints
{
    private static readonly object[] Catalog =
    [
        new { id = "lock", label = "Lock the PC" },
        new { id = "sleep", label = "Put the PC to sleep" },
    ];

    public static void Map(WebApplication app, ServerContext c)
    {
        // 1 from a sleep request until the PC is awake again. Another request in that time is
        // ignored; a double tap would otherwise put the PC back to sleep right after it wakes.
        int sleepPending = 0;

        app.MapGet("/api/v1/actions", (HttpContext ctx) =>
            c.Device(ctx) is null ? Results.Unauthorized() : Results.Ok(new { actions = Catalog }));

        app.MapPost("/api/v1/actions/{id}", IResult (HttpContext ctx, string id) =>
        {
            var device = c.Device(ctx);
            if (!c.Authorized(ctx, device)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            switch (id)
            {
                case "lock":
                    try { c.Actions.Lock(); }
                    catch (HostActionException ex)
                    { return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status500InternalServerError); }
                    c.Notice($"\"{device!.Name}\" locked this PC.");
                    return Results.Ok(new { done = true });

                case "sleep":
                    // Answer now and sleep in the background: Sleep() only returns after the PC
                    // woke up, and a sleeping PC cannot send the answer.
                    if (Interlocked.Exchange(ref sleepPending, 1) == 0)
                    {
                        c.Notice($"\"{device!.Name}\" is putting this PC to sleep.");
                        _ = Task.Run(() =>
                        {
                            try { c.Actions.Sleep(); }
                            catch (HostActionException ex) { c.Notice(ex.Message); } // the phone is gone; only the PC can show it
                            finally { Volatile.Write(ref sleepPending, 0); }
                        });
                    }
                    return Results.Ok(new { done = true });

                default:
                    return Results.NotFound();
            }
        });
    }
}
