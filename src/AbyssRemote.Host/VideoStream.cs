using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;

namespace AbyssRemote.Host;

/// <summary>Game-mode video protocol over one WebSocket (see docs/API.md).
/// <para>Host to browser: a JSON <c>config</c> message (codec string, size) before the first
/// frame; binary frames = 12-byte header + H.264 Annex B; JSON <c>cursor</c>, <c>cursorImage</c>,
/// <c>unavailable</c>/<c>resumed</c> and once-a-second <c>stats</c>.</para>
/// <para>Browser to host: <c>ack &lt;frame id&gt;</c> after each decoded frame (one credit of flow
/// control, at most <see cref="FramesInFlight"/> outstanding) and <c>key</c> to ask for a keyframe.</para></summary>
public static class VideoStream
{
    public const int FramesInFlight = 2;
    public const int HeaderSize = 12;

    public static async Task RunAsync(LiveSocket live, IVideoSource source, Func<bool> stillPaired, CancellationToken aborted)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(aborted);
        var sentAt = new ConcurrentDictionary<long, long>();
        var stats = new Stats();
        var receive = ReceiveAsync(live, source, sentAt, stats, stop);
        source.AllowFrames(FramesInFlight);
        try
        {
            var reportAt = Stopwatch.GetTimestamp() + Stopwatch.Frequency;
            await foreach (var item in source.Events.ReadAllAsync(stop.Token))
            {
                if (!stillPaired()) return;
                bool sent = item switch
                {
                    VideoConfig c => await live.SendJsonAsync(new { type = "config", codec = c.Codec, description = Convert.ToBase64String(c.Description),
                        width = c.Width, height = c.Height, desktopWidth = c.DesktopWidth, desktopHeight = c.DesktopHeight, encoder = c.Encoder }, stop.Token),
                    EncodedVideoFrame f => await SendFrameAsync(live, f, sentAt, stats, stop.Token),
                    CursorPosition p => await live.SendJsonAsync(new { type = "cursor", x = p.X, y = p.Y, visible = p.Visible }, stop.Token),
                    CursorImage i => await live.SendJsonAsync(new { type = "cursorImage", png = Convert.ToBase64String(i.Png),
                        hotX = i.HotX, hotY = i.HotY, width = i.Width, height = i.Height }, stop.Token),
                    VideoStatus s => await live.SendJsonAsync(new { type = s.Type, message = s.Message }, stop.Token),
                    _ => true,
                };
                if (!sent) return;
                if (item is VideoStatus { Type: "restart" or "error" })
                {
                    await live.CloseAsync(1012 /* service restart: the browser reconnects */, "Video pipeline restarted");
                    return;
                }
                if (Stopwatch.GetTimestamp() >= reportAt)
                {
                    reportAt = Stopwatch.GetTimestamp() + Stopwatch.Frequency;
                    if (!await live.SendJsonAsync(stats.Report(), stop.Token)) return;
                }
            }
        }
        finally
        {
            stop.Cancel();
            try { await receive; } catch (Exception ex) when (ex is OperationCanceledException or WebSocketException) { }
        }
    }

    private static async Task<bool> SendFrameAsync(LiveSocket live, EncodedVideoFrame frame, ConcurrentDictionary<long, long> sentAt, Stats stats, CancellationToken ct)
    {
        var message = new byte[HeaderSize + frame.Data.Length];
        message[0] = 1;                                    // message type: video frame
        message[1] = (byte)(frame.Keyframe ? 1 : 0);       // flags: bit 0 = keyframe
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(4), (uint)frame.Id);
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(8), frame.TimestampMs);
        frame.Data.CopyTo(message, HeaderSize);
        sentAt[frame.Id] = Stopwatch.GetTimestamp();
        stats.Frame(frame.Data.Length, frame.EncodeMs);
        return await live.SendAsync(message, WebSocketMessageType.Binary, ct);
    }

    private static async Task ReceiveAsync(LiveSocket live, IVideoSource source, ConcurrentDictionary<long, long> sentAt, Stats stats, CancellationTokenSource stop)
    {
        var buffer = new byte[64];
        try
        {
            while (live.Socket.State == WebSocketState.Open && !stop.IsCancellationRequested)
            {
                var message = await live.Socket.ReceiveAsync(buffer.AsMemory(), stop.Token);
                if (message.MessageType == WebSocketMessageType.Close) return;
                if (message.MessageType != WebSocketMessageType.Text || !message.EndOfMessage) return;
                string text = Encoding.ASCII.GetString(buffer, 0, message.Count);
                if (text == "key")
                {
                    source.RequestKeyframe();
                }
                else if (text.StartsWith("ack ", StringComparison.Ordinal) && long.TryParse(text.AsSpan(4), out long id))
                {
                    // Only frames we actually sent earn a credit, so a client cannot inflate its allowance.
                    if (sentAt.TryRemove(id, out long sent))
                    {
                        stats.RoundTrip(Stopwatch.GetElapsedTime(sent).TotalMilliseconds);
                        source.AllowFrames(1);
                    }
                }
                else return; // unknown message: end the stream
            }
        }
        finally { stop.Cancel(); }
    }

    /// <summary>Numbers for the stats line in the browser, measured on the host.</summary>
    private sealed class Stats
    {
        private readonly object _sync = new();
        private int _frames;
        private long _bytes;
        private double _encodeMs, _roundTripMs;
        private int _roundTrips;
        private long _since = Stopwatch.GetTimestamp();

        public void Frame(int bytes, double encodeMs)
        {
            lock (_sync) { _frames++; _bytes += bytes; _encodeMs += encodeMs; }
        }

        public void RoundTrip(double ms)
        {
            lock (_sync) { _roundTrips++; _roundTripMs += ms; }
        }

        public object Report()
        {
            lock (_sync)
            {
                double seconds = Math.Max(0.001, Stopwatch.GetElapsedTime(_since).TotalSeconds);
                var report = new
                {
                    type = "stats",
                    fps = Math.Round(_frames / seconds, 1),
                    mbps = Math.Round(_bytes * 8 / seconds / 1_000_000, 1),
                    encodeMs = _frames == 0 ? 0 : Math.Round(_encodeMs / _frames, 1),
                    // Host sends a frame -> browser receives and decodes it -> its ack comes back.
                    deliverMs = _roundTrips == 0 ? 0 : Math.Round(_roundTripMs / _roundTrips, 1),
                };
                _frames = 0; _bytes = 0; _encodeMs = 0; _roundTripMs = 0; _roundTrips = 0;
                _since = Stopwatch.GetTimestamp();
                return report;
            }
        }
    }
}
