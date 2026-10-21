using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Forms;

namespace AbyssRemote.Host;

/// <summary>The real Windows desktop: GDI capture, SendInput and the clipboard.</summary>
public sealed class DesktopEngine : IDesktop
{
    public IReadOnlyList<DisplayInfo> Displays() =>
        Screen.AllScreens.Select((s, i) => new DisplayInfo(
            i, s.DeviceName, s.Bounds.Left, s.Bounds.Top, s.Bounds.Width, s.Bounds.Height, s.Primary)).ToArray();

    public IFrameSource CreateFrameSource(int display, int maxWidth, int quality) =>
        new GdiFrameSource(this, display, maxWidth, quality);

    public IInputSink CreateInputSink(Action<object> notify) => new NativeInputSink(this, notify);

    public IVideoSource CreateVideoSource(VideoSettings settings)
    {
        var display = Displays().ElementAtOrDefault(settings.Display)
            ?? throw new GameMode.VideoUnsupportedException("That monitor is no longer connected.");
        return new GameMode.GpuVideoSource(display, settings);
    }

    private readonly Lazy<bool> _hardwareVideo = new(() =>
    {
        try { return GameMode.HardwareEncoder.AnyAvailable(); }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return false; }
    });

    public bool HardwareVideoAvailable => _hardwareVideo.Value;

    public IAudioSource CreateAudioSource() => new GameMode.SystemAudioCapture();
}

/// <summary>Desktop-mode capture for one viewer. The monitor is copied with BitBlt into a DIB
/// section that is reused every frame; GDI draws the mouse cursor straight into it, and the
/// raw pixels are compared with the previous frame so an unchanged screen costs no JPEG
/// encoding and no network traffic. Still CPU-bound: see Game mode for the GPU path.</summary>
public sealed unsafe class GdiFrameSource : IFrameSource
{
    private readonly DesktopEngine _desktop;
    private readonly int _display, _maxWidth;
    private readonly ImageCodecInfo _jpegEncoder;
    private readonly EncoderParameters _parameters;
    private readonly MemoryStream _jpeg = new(256 * 1024);
    private IntPtr _memoryDc, _dib, _previousObject, _bits;
    private Bitmap? _full, _scaled;
    private Graphics? _resizeGraphics;
    private byte[] _previous = [];
    private int _width, _height;

    internal GdiFrameSource(DesktopEngine desktop, int display, int maxWidth, int quality)
    {
        _desktop = desktop;
        _display = display;
        _maxWidth = maxWidth;
        _jpegEncoder = ImageCodecInfo.GetImageEncoders().First(x => x.MimeType == "image/jpeg");
        _parameters = new EncoderParameters(1);
        _parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)Math.Clamp(quality, 30, 90));
    }

    public byte[]? Capture(bool forceFullFrame)
    {
        var d = _desktop.Displays().ElementAtOrDefault(_display)
            ?? throw new DesktopUnavailableException("That monitor is no longer connected. Pick another one.");
        EnsureBuffers(d.Width, d.Height);

        IntPtr screen = Gdi.GetDC(IntPtr.Zero);
        try
        {
            // Fails while Windows shows the lock screen or a UAC prompt (the "secure desktop").
            if (!Gdi.BitBlt(_memoryDc, 0, 0, _width, _height, screen, d.X, d.Y, Gdi.SrcCopy))
                throw new DesktopUnavailableException("The PC is locked or showing a secure prompt (UAC). Waiting for the desktop…");
        }
        finally { Gdi.ReleaseDC(IntPtr.Zero, screen); }
        CursorPainter.Draw(_memoryDc, d.X, d.Y, _width, _height);
        Gdi.GdiFlush();

        var pixels = new ReadOnlySpan<byte>((void*)_bits, _width * _height * 4);
        if (!forceFullFrame && pixels.SequenceEqual(_previous)) return null;
        pixels.CopyTo(_previous);

        if (_resizeGraphics is not null) _resizeGraphics.DrawImage(_full!, 0, 0, _scaled!.Width, _scaled.Height);
        _jpeg.Position = 0;
        _jpeg.SetLength(0);
        (_scaled ?? _full)!.Save(_jpeg, _jpegEncoder, _parameters);
        return _jpeg.ToArray();
    }

    private void EnsureBuffers(int width, int height)
    {
        if (_full is not null && width == _width && height == _height) return;
        ReleaseBuffers();
        _width = width;
        _height = height;
        var header = new Gdi.BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<Gdi.BITMAPINFOHEADER>(), biWidth = width, biHeight = -height, // negative = top-down rows
            biPlanes = 1, biBitCount = 32,
        };
        _memoryDc = Gdi.CreateCompatibleDC(IntPtr.Zero);
        _dib = Gdi.CreateDIBSection(_memoryDc, ref header, 0, out _bits, IntPtr.Zero, 0);
        if (_dib == IntPtr.Zero) throw new DesktopUnavailableException("Windows could not allocate a capture buffer.");
        _previousObject = Gdi.SelectObject(_memoryDc, _dib);
        _full = new Bitmap(width, height, width * 4, PixelFormat.Format32bppRgb, _bits); // a view of the DIB, no copy
        _previous = new byte[width * height * 4];

        int outputWidth = Math.Max(1, Math.Min(_maxWidth, width));
        if (outputWidth < width)
        {
            int outputHeight = Math.Max(1, (int)Math.Round(height * (outputWidth / (double)width)));
            _scaled = new Bitmap(outputWidth, outputHeight, PixelFormat.Format24bppRgb);
            _resizeGraphics = Graphics.FromImage(_scaled);
            _resizeGraphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
            _resizeGraphics.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
            _resizeGraphics.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighSpeed;
            _resizeGraphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighSpeed;
        }
    }

    private void ReleaseBuffers()
    {
        _resizeGraphics?.Dispose(); _resizeGraphics = null;
        _scaled?.Dispose(); _scaled = null;
        _full?.Dispose(); _full = null;
        if (_memoryDc != IntPtr.Zero)
        {
            Gdi.SelectObject(_memoryDc, _previousObject);
            Gdi.DeleteObject(_dib);
            Gdi.DeleteDC(_memoryDc);
            _memoryDc = _dib = _bits = IntPtr.Zero;
        }
    }

    public void Dispose()
    {
        ReleaseBuffers();
        _parameters.Dispose();
        _jpeg.Dispose();
    }
}

/// <summary>Draws the current mouse cursor into a capture. BitBlt never includes the cursor,
/// and without it trackpad-style control is blind. DrawIconEx also renders the "inverting"
/// text cursor correctly because it draws onto the real captured pixels.</summary>
internal static class CursorPainter
{
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct CURSORINFO { public int cbSize, flags; public IntPtr hCursor; public POINT ptScreenPos; }
    [StructLayout(LayoutKind.Sequential)] private struct ICONINFO { public int fIcon, xHotspot, yHotspot; public IntPtr hbmMask, hbmColor; }

    [DllImport("user32.dll")] private static extern bool GetCursorInfo(ref CURSORINFO info);
    [DllImport("user32.dll")] private static extern bool GetIconInfo(IntPtr icon, out ICONINFO info);
    [DllImport("user32.dll")] private static extern bool DrawIconEx(IntPtr hdc, int x, int y, IntPtr icon, int cx, int cy, int step, IntPtr brush, int flags);

    private const int CursorShowing = 0x1, DiNormal = 0x3;
    private static readonly ConcurrentDictionary<IntPtr, (int X, int Y)> Hotspots = new();

    public static void Draw(IntPtr hdc, int originX, int originY, int width, int height)
    {
        var info = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        if (!GetCursorInfo(ref info) || (info.flags & CursorShowing) == 0 || info.hCursor == IntPtr.Zero) return;
        int x = info.ptScreenPos.X - originX, y = info.ptScreenPos.Y - originY;
        if (x < -64 || y < -64 || x > width + 64 || y > height + 64) return; // cursor is on another monitor
        var hotspot = Hotspots.GetOrAdd(info.hCursor, Hotspot);
        DrawIconEx(hdc, x - hotspot.X, y - hotspot.Y, info.hCursor, 0, 0, 0, IntPtr.Zero, DiNormal);
    }

    private static (int X, int Y) Hotspot(IntPtr cursor)
    {
        if (!GetIconInfo(cursor, out var icon)) return (0, 0);
        if (icon.hbmMask != IntPtr.Zero) Gdi.DeleteObject(icon.hbmMask);
        if (icon.hbmColor != IntPtr.Zero) Gdi.DeleteObject(icon.hbmColor);
        return (icon.xHotspot, icon.yHotspot);
    }
}

internal static class Gdi
{
    public const int SrcCopy = 0x00CC0020;

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER header, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] public static extern bool BitBlt(IntPtr dest, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, int rop);
    [DllImport("gdi32.dll")] public static extern bool GdiFlush();
}

/// <summary>Input for one control connection. It remembers which keys and mouse buttons it
/// pressed so a dropped connection or a lost browser focus can never leave Ctrl, Alt, Shift,
/// Windows or a mouse button stuck down on the PC.</summary>
public sealed class NativeInputSink : IInputSink
{
    private readonly DesktopEngine _desktop;
    private readonly Action<object> _notify;
    private readonly HashSet<string> _keysDown = new(StringComparer.Ordinal);
    private readonly HashSet<int> _buttonsDown = new();

    internal NativeInputSink(DesktopEngine desktop, Action<object> notify)
    {
        _desktop = desktop;
        _notify = notify;
    }

    public void Handle(JsonElement command)
    {
        switch (InputCommand.Kind(command))
        {
            case "pointer":
            {
                var d = _desktop.Displays().ElementAtOrDefault(InputCommand.Int(command, "display", 0, 64));
                if (d is null) return;
                double x = InputCommand.Double(command, "x", 0, 1), y = InputCommand.Double(command, "y", 0, 1);
                NativeInput.SetCursorPos(d.X + Math.Clamp((int)(x * d.Width), 0, d.Width - 1),
                                         d.Y + Math.Clamp((int)(y * d.Height), 0, d.Height - 1));
                break;
            }
            case "move":
                NativeInput.MoveRelative(InputCommand.Int(command, "dx", -4000, 4000), InputCommand.Int(command, "dy", -4000, 4000));
                break;
            case "mouse":
            {
                int button = InputCommand.Int(command, "button", 0, 4);
                bool down = InputCommand.String(command, "action") == "down";
                NativeInput.Mouse(button, down);
                if (down) _buttonsDown.Add(button); else _buttonsDown.Remove(button);
                break;
            }
            case "wheel":
                NativeInput.Wheel(InputCommand.Int(command, "delta", -1200, 1200), horizontal: false);
                break;
            case "hwheel":
                NativeInput.Wheel(InputCommand.Int(command, "delta", -1200, 1200), horizontal: true);
                break;
            case "key":
            {
                string code = InputCommand.String(command, "code");
                bool down = InputCommand.String(command, "action") == "down";
                if (!NativeInput.Key(code, down)) return;
                if (down) _keysDown.Add(code); else _keysDown.Remove(code);
                break;
            }
            case "text":
            {
                string text = InputCommand.String(command, "text");
                if (text.Length <= 2048) NativeInput.TypeText(text);
                break;
            }
            case "release":
                ReleaseAll();
                break;
        }
    }

    private void ReleaseAll()
    {
        foreach (int button in _buttonsDown) NativeInput.Mouse(button, false);
        foreach (string code in _keysDown) NativeInput.Key(code, false);
        _buttonsDown.Clear();
        _keysDown.Clear();
    }

    public void Dispose() => ReleaseAll();
}

/// <summary>Reads fields from untrusted control messages, always clamped to a safe range.</summary>
public static class InputCommand
{
    public static string Kind(JsonElement command) =>
        command.ValueKind == JsonValueKind.Object ? String(command, "kind") : "";

    public static int Int(JsonElement obj, string name, int min, int max)
    {
        if (!obj.TryGetProperty(name, out var n) || n.ValueKind != JsonValueKind.Number || !n.TryGetDouble(out double value) || !double.IsFinite(value)) return Math.Clamp(0, min, max);
        return (int)Math.Clamp(Math.Round(value), min, max);
    }

    public static double Double(JsonElement obj, string name, double min, double max)
    {
        if (!obj.TryGetProperty(name, out var n) || n.ValueKind != JsonValueKind.Number || !n.TryGetDouble(out double value) || !double.IsFinite(value)) return min;
        return Math.Clamp(value, min, max);
    }

    public static string String(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}

internal static class NativeInput
{
    private const uint InputMouse = 0, InputKeyboard = 1;
    private const uint MouseMove = 0x0001, LeftDown = 0x0002, LeftUp = 0x0004, RightDown = 0x0008, RightUp = 0x0010,
        MiddleDown = 0x0020, MiddleUp = 0x0040, XDown = 0x0080, XUp = 0x0100, WheelFlag = 0x0800, HWheelFlag = 0x1000;
    private const uint ExtendedKey = 0x0001, KeyUp = 0x0002, Unicode = 0x0004, ScanCode = 0x0008;

    [StructLayout(LayoutKind.Sequential)] private struct INPUT { public uint type; public Payload data; }
    [StructLayout(LayoutKind.Explicit)] private struct Payload
    {
        [FieldOffset(0)] public MOUSEINPUT mouse;
        [FieldOffset(0)] public KEYBDINPUT key;
    }
    [StructLayout(LayoutKind.Sequential)] private struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public UIntPtr extraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct KEYBDINPUT { public ushort virtualKey, scanCode; public uint flags, time; public UIntPtr extraInfo; }

    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, INPUT[] inputs, int size);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);

    private static void Send(params INPUT[] inputs)
    {
        if (inputs.Length > 0) SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    private static INPUT MouseInput(uint flags, int dx = 0, int dy = 0, uint data = 0) =>
        new() { type = InputMouse, data = new Payload { mouse = new MOUSEINPUT { dx = dx, dy = dy, mouseData = data, dwFlags = flags } } };

    private static INPUT KeyInput(ushort virtualKey, ushort scanCode, uint flags) =>
        new() { type = InputKeyboard, data = new Payload { key = new KEYBDINPUT { virtualKey = virtualKey, scanCode = scanCode, flags = flags } } };

    public static void MoveRelative(int dx, int dy) => Send(MouseInput(MouseMove, dx, dy));

    /// <summary>Buttons follow the browser numbering: 0 left, 1 middle, 2 right, 3 back, 4 forward.</summary>
    public static void Mouse(int button, bool pressed)
    {
        var input = button switch
        {
            0 => MouseInput(pressed ? LeftDown : LeftUp),
            1 => MouseInput(pressed ? MiddleDown : MiddleUp),
            2 => MouseInput(pressed ? RightDown : RightUp),
            3 => MouseInput(pressed ? XDown : XUp, data: 1), // XBUTTON1
            4 => MouseInput(pressed ? XDown : XUp, data: 2), // XBUTTON2
            _ => (INPUT?)null,
        };
        if (input is not null) Send(input.Value);
    }

    public static void Wheel(int delta, bool horizontal) =>
        Send(MouseInput(horizontal ? HWheelFlag : WheelFlag, data: unchecked((uint)delta)));

    /// <summary>Presses or releases one physical key. Returns false for unknown key codes.</summary>
    public static bool Key(string code, bool pressed)
    {
        uint up = pressed ? 0 : KeyUp;
        if (KeyMap.TryGetScanCode(code, out ushort scan, out bool extended))
            Send(KeyInput(0, scan, ScanCode | up | (extended ? ExtendedKey : 0)));
        else if (KeyMap.TryGetVirtualKey(code, out ushort vk, out bool vkExtended))
            Send(KeyInput(vk, 0, up | (vkExtended ? ExtendedKey : 0)));
        else
            return false;
        return true;
    }

    /// <summary>Types text as Unicode key events. Line breaks and tabs become real Enter and
    /// Tab presses, because many apps ignore a Unicode "\n".</summary>
    public static void TypeText(string value)
    {
        var batch = new List<INPUT>(128);
        foreach (char ch in value)
        {
            switch (ch)
            {
                case '\r': continue; // "\r\n" types a single Enter
                case '\n': batch.Add(KeyInput(0, 0x1C, ScanCode)); batch.Add(KeyInput(0, 0x1C, ScanCode | KeyUp)); break;
                case '\t': batch.Add(KeyInput(0, 0x0F, ScanCode)); batch.Add(KeyInput(0, 0x0F, ScanCode | KeyUp)); break;
                default: batch.Add(KeyInput(0, ch, Unicode)); batch.Add(KeyInput(0, ch, Unicode | KeyUp)); break;
            }
            if (batch.Count >= 128) { Send(batch.ToArray()); batch.Clear(); }
        }
        Send(batch.ToArray());
    }
}
