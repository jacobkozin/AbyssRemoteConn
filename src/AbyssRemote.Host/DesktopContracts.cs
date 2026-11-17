using System.Text.Json;

namespace AbyssRemote.Host;

// The web server only talks to the PC through these interfaces. Production uses the Windows
// implementations; the tests use fakes, so they can prove that nothing reaches the real
// screen, keyboard or mouse without passing the security checks.

/// <summary>One monitor, in physical pixels. <paramref name="Name"/> is the Windows device name
/// that capture uses (\\.\DISPLAY1). <paramref name="FriendlyName"/> is what a person would call
/// it, normally the model the monitor reports ("DELL U2720Q"). <paramref name="RefreshHz"/> is 0
/// when Windows does not say. <paramref name="Scale"/> is how much Windows enlarges text and
/// controls on that monitor (1 for 100%, 1.5 for 150%); a phone uses it to zoom in to a size
/// at which text can be read.</summary>
public record DisplayInfo(int Id, string Name, int X, int Y, int Width, int Height, bool Primary,
    string FriendlyName = "", int RefreshHz = 0, double Scale = 1);

public interface IDesktop
{
    IReadOnlyList<DisplayInfo> Displays();

    // Optional lightweight cursor feedback, independent of video encoding.
    ICursorSource? CreateCursorSource() => null;

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

    /// <summary>What the PC is playing, for the browser to play back. Throws
    /// <see cref="AudioUnavailableException"/> when there is no output device.</summary>
    IAudioSource CreateAudioSource();
}

/// <summary>PC sound as 10 ms chunks of 48 kHz stereo 16-bit little-endian PCM (1,920 bytes).
/// Chunks keep coming during silence, so the browser can tell a quiet PC from a slow network.</summary>
public interface IAudioSource : IDisposable
{
    public const int SampleRate = 48_000, Channels = 2, ChunkFrames = 480, ChunkBytes = ChunkFrames * Channels * 2;

    System.Threading.Channels.ChannelReader<byte[]> Chunks { get; }
}

public sealed class AudioUnavailableException(string message) : Exception(message);

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

    /// <summary>Queue a bounded bitrate change; unsupported sources keep their current rate.</summary>
    void RequestBitrate(int kbps) { }
}

public interface IFrameSource : IDisposable
{
    bool IncludeCursor { set { } } // Legacy viewers and screenshots keep the baked-in cursor.

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
