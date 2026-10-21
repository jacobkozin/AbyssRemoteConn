using System.Text.Json;

namespace AbyssRemote.Host;

// The web server only talks to the PC through these interfaces. Production uses the Windows
// implementations; the tests use fakes, so they can prove that nothing reaches the real
// screen, keyboard or mouse without passing the security checks.

public record DisplayInfo(int Id, string Name, int X, int Y, int Width, int Height, bool Primary);

public interface IDesktop
{
    IReadOnlyList<DisplayInfo> Displays();

    /// <summary>Desktop mode: JPEG frames of one monitor for one viewer.</summary>
    IFrameSource CreateFrameSource(int display, int maxWidth, int quality);

    /// <summary>One per control connection. Disposing it releases any keys, buttons or
    /// controllers that connection still holds down.</summary>
    IInputSink CreateInputSink(Action<object> notify);

    /// <summary>Game mode: hardware-encoded H.264 of one monitor. Throws
    /// <see cref="GameMode.VideoUnsupportedException"/> when this PC or monitor cannot do it.</summary>
    IVideoSource CreateVideoSource(VideoSettings settings);

    /// <summary>True when at least one hardware H.264 encoder is installed (checked once).</summary>
    bool HardwareVideoAvailable { get; }
}

public sealed record VideoSettings(int Display, int Fps, int BitrateKbps, int MaxHeight);

/// <summary>Everything a Game-mode stream sends to the browser, in order.</summary>
public abstract record VideoEvent;
/// <summary>Sent once before the first frame: what the browser's decoder must be configured for.
/// Frames are AVCC; <paramref name="Description"/> is the avcC record WebCodecs needs.</summary>
public sealed record VideoConfig(string Codec, byte[] Description, int Width, int Height, int DesktopWidth, int DesktopHeight, string Encoder) : VideoEvent;
public sealed record EncodedVideoFrame(long Id, bool Keyframe, uint TimestampMs, byte[] Data, double EncodeMs) : VideoEvent;
/// <summary>Mouse pointer position in monitor pixels; the browser draws the pointer itself.</summary>
public sealed record CursorPosition(int X, int Y, bool Visible) : VideoEvent;
public sealed record CursorImage(byte[] Png, int HotX, int HotY, int Width, int Height) : VideoEvent;
/// <summary>"unavailable" / "resumed" / "restart" / "error" with a message for the user.</summary>
public sealed record VideoStatus(string Type, string Message) : VideoEvent;

public interface IVideoSource : IDisposable
{
    System.Threading.Channels.ChannelReader<VideoEvent> Events { get; }

    /// <summary>The browser decoded frames and has room for this many more (flow control).</summary>
    void AllowFrames(int count);

    /// <summary>Make the next frame a keyframe (new decoder, or a decode error).</summary>
    void RequestKeyframe();
}

public interface IFrameSource : IDisposable
{
    /// <summary>Captures the monitor. Returns null when nothing changed since the last frame
    /// (unless <paramref name="forceFullFrame"/>). Throws <see cref="DesktopUnavailableException"/>
    /// when Windows does not allow capture right now, for example on the lock screen.</summary>
    byte[]? Capture(bool forceFullFrame);
}

public interface IInputSink : IDisposable
{
    void Handle(JsonElement command);
}

public interface IClipboard
{
    string Read();
    void Write(string text);
}

public sealed class DesktopUnavailableException(string message, Exception? inner = null) : Exception(message, inner);
