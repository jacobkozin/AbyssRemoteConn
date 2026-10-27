using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;

namespace AbyssRemote.Host;

/// <summary>Desktop-mode video loop. The browser sends "next" after it has drawn a frame, so at
/// most one JPEG is ever in flight and a slow phone never builds a backlog of stale frames.
/// Each request gets exactly one reply:
/// <list type="bullet">
/// <item>binary: a JPEG of the monitor (the screen changed, or the browser asked for "full")</item>
/// <item>text <c>{"type":"idle"}</c>: nothing changed for a second</item>
/// <item>text <c>{"type":"unavailable","message":...}</c>: Windows is blocking capture (lock screen, UAC)</item>
/// </list></summary>
public static class JpegStream
{
    public static readonly TimeSpan IdleReply = TimeSpan.FromSeconds(1);

    public static async Task RunAsync(LiveSocket live, IFrameSource source, int fps, Func<bool> stillPaired, CancellationToken aborted)
    {
        using var timer = TimerResolution.OneMillisecond();
        var request = new byte[16];
        var interval = TimeSpan.FromSeconds(1.0 / fps);
        var clock = Stopwatch.StartNew();
        var nextCaptureAt = TimeSpan.Zero;
        while (live.Socket.State == WebSocketState.Open)
        {
            var message = await live.ReceiveAsync(request.AsMemory(), aborted);
            if (message.MessageType == WebSocketMessageType.Close || !stillPaired()) return;
            string verb = message.MessageType == WebSocketMessageType.Text && message.EndOfMessage
                ? Encoding.ASCII.GetString(request, 0, message.Count) : "";
            if (verb is not ("next" or "full"))
            {
                await live.CloseAsync((int)WebSocketCloseStatus.InvalidPayloadData, "Expected \"next\" or \"full\"");
                return;
            }

            var waitingSince = clock.Elapsed;
            while (true)
            {
                // Pace from the START of each capture so capture time overlaps the frame interval.
                // R3.3 started the interval after sending, which capped this PC at about 8 fps.
                var delay = nextCaptureAt - clock.Elapsed;
                if (delay > TimeSpan.Zero) await Task.Delay(delay, aborted);
                nextCaptureAt = clock.Elapsed + interval;

                byte[]? jpeg;
                try { jpeg = source.Capture(forceFullFrame: verb == "full"); }
                catch (DesktopUnavailableException ex)
                {
                    nextCaptureAt = clock.Elapsed + TimeSpan.FromSeconds(1);
                    if (!await live.SendJsonAsync(new { type = "unavailable", message = ex.Message }, aborted)) return;
                    break;
                }
                if (jpeg is not null)
                {
                    if (!await live.SendAsync(jpeg, WebSocketMessageType.Binary, aborted)) return;
                    break;
                }
                if (clock.Elapsed - waitingSince >= IdleReply)
                {
                    if (!await live.SendJsonAsync(new { type = "idle" }, aborted)) return;
                    break;
                }
                if (!stillPaired()) return;
            }
        }
    }
}
