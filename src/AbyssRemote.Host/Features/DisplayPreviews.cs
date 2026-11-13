using System.Globalization;

namespace AbyssRemote.Host.Features;

/// <summary>Still pictures of each monitor: a small preview for the monitor picker and a
/// full-size screenshot that a phone can save. Both come from the Desktop-mode capture, so they
/// show what a stream would show (mouse pointer included) and are refused in the same way
/// while Windows blocks capture.</summary>
public static class DisplayPreviewEndpoints
{
    public const int DefaultPreviewWidth = 480, MinPreviewWidth = 160, MaxPreviewWidth = 960;
    public const int PreviewQuality = 75, ScreenshotQuality = 95;

    public static void Map(WebApplication app, ServerContext c)
    {
        // One capture at a time. Each holds two full-size copies of the monitor (about 66 MB
        // for a 4K screen), and a monitor picker asks for every monitor at once.
        var oneAtATime = new SemaphoreSlim(1, 1);

        app.MapGet("/api/v1/displays/{id:int}/preview", (HttpContext ctx, int id) =>
        {
            int width = AbyssServer.QueryInt(ctx, "width", DefaultPreviewWidth, MinPreviewWidth, MaxPreviewWidth);
            return CaptureAsync(ctx, c, oneAtATime, id, width, PreviewQuality, saveAs: null);
        });

        app.MapGet("/api/v1/displays/{id:int}/screenshot", (HttpContext ctx, int id) =>
        {
            // The PC's own clock, in a form that sorts by date and is a legal file name everywhere.
            string name = $"{AppInfo.Name} {DateTime.Now.ToString("yyyy-MM-dd HH.mm.ss", CultureInfo.InvariantCulture)}.jpg";
            return CaptureAsync(ctx, c, oneAtATime, id, maxWidth: int.MaxValue, ScreenshotQuality, saveAs: name);
        });
    }

    /// <summary>Captures one monitor as a JPEG. With <paramref name="saveAs"/> the browser
    /// saves it as a file of that name instead of showing it.</summary>
    private static async Task<IResult> CaptureAsync(HttpContext ctx, ServerContext c, SemaphoreSlim oneAtATime,
        int display, int maxWidth, int quality, string? saveAs)
    {
        if (c.Device(ctx) is null) return Results.Unauthorized();
        if (display < 0 || display >= c.Desktop.Displays().Count) return Results.NotFound();

        await oneAtATime.WaitAsync();
        try
        {
            using var source = c.Desktop.CreateFrameSource(display, maxWidth, quality);
            byte[] jpeg = source.Capture(forceFullFrame: true)
                ?? throw new DesktopUnavailableException("The PC did not return a picture. Try again.");
            return Results.File(jpeg, "image/jpeg", fileDownloadName: saveAs);
        }
        catch (DesktopUnavailableException ex)
        {
            // The lock screen, a UAC prompt, or the monitor was unplugged a moment ago.
            return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        finally { oneAtATime.Release(); }
    }
}
