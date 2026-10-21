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
