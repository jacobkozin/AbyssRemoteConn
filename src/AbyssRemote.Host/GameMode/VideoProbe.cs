using System.Diagnostics;

namespace AbyssRemote.Host.GameMode;

/// <summary><c>AbyssRemote.Host.exe --probe-video</c>: checks Game mode on every monitor without
/// a browser. For each monitor it builds the real pipeline, encodes for a few seconds (repeating
/// the image when the screen is still, so the encoder is actually exercised) and prints timings.</summary>
public static class VideoProbe
{
    public static int Run(IDesktop desktop, int fps = 60, int bitrateKbps = 20_000, int maxHeight = 1080, double seconds = 3)
    {
        Console.WriteLine($"Game mode check: GPU desktop duplication + hardware H.264, {fps} fps target, {bitrateKbps / 1000} Mbps, up to {maxHeight}p");
        bool anyWorked = false;
        foreach (var display in desktop.Displays())
        {
            Console.WriteLine($"\n{display.Name}  {display.Width}x{display.Height}{(display.Primary ? "  (primary)" : "")}");
            try
            {
                using var source = new GpuVideoSource(display, new VideoSettings(display.Id, fps, bitrateKbps, maxHeight), repeatStaticFrames: true);
                var (width, height) = source.OutputSize;
                Console.WriteLine($"  GPU       : {source.AdapterName}");
                Console.WriteLine($"  Encoder   : {source.EncoderName}");
                Console.WriteLine($"  Stream    : {width}x{height}");
                source.AllowFrames(2);
                var clock = Stopwatch.StartNew();
                var encodeTimes = new List<double>();
                long bytes = 0;
                int keyframes = 0;
                string codec = "?";
                while (clock.Elapsed.TotalSeconds < seconds)
                {
                    if (!source.Events.TryRead(out var item)) { Thread.Sleep(1); continue; }
                    switch (item)
                    {
                        case VideoConfig config: codec = config.Codec; break;
                        case EncodedVideoFrame frame:
                            encodeTimes.Add(frame.EncodeMs);
                            bytes += frame.Data.Length;
                            if (frame.Keyframe) keyframes++;
                            source.AllowFrames(1); // like a browser that decodes instantly
                            break;
                        case VideoStatus status: Console.WriteLine($"  Status    : {status.Type} {status.Message}"); break;
                    }
                }
                double elapsed = clock.Elapsed.TotalSeconds;
                if (encodeTimes.Count == 0)
                {
                    Console.WriteLine("  Result    : no frames were produced");
                    continue;
                }
                encodeTimes.Sort();
                Console.WriteLine($"  Codec     : {codec}");
                Console.WriteLine($"  Frames    : {encodeTimes.Count} in {elapsed:F1} s = {encodeTimes.Count / elapsed:F1} fps ({keyframes} keyframe(s))");
                Console.WriteLine($"  Encode    : median {encodeTimes[encodeTimes.Count / 2]:F1} ms, 95th percentile {encodeTimes[(int)(encodeTimes.Count * 0.95)]:F1} ms (input to output)");
                Console.WriteLine($"  Bitrate   : {bytes * 8 / elapsed / 1_000_000:F1} Mbps, average frame {bytes / encodeTimes.Count / 1024.0:F1} KB");
                anyWorked = true;
            }
            catch (Exception ex) when (ex is VideoUnsupportedException or DesktopUnavailableException or NativeCallException)
            {
                Console.WriteLine($"  Not available: {ex.Message}");
            }
        }
        Console.WriteLine(anyWorked ? "\nGame mode works on this PC." : "\nGame mode is not available on this PC; Desktop mode still works.");
        return anyWorked ? 0 : 1;
    }
}
