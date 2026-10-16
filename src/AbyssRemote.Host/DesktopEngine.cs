using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Forms;

namespace AbyssRemote.Host;

public record DisplayInfo(int Id, string Name, int X, int Y, int Width, int Height, bool Primary);

public sealed class DesktopEngine
{
    public IReadOnlyList<DisplayInfo> Displays()
    {
        return Screen.AllScreens.Select((s, i) => new DisplayInfo(
            i, s.DeviceName, s.Bounds.Left, s.Bounds.Top, s.Bounds.Width, s.Bounds.Height, s.Primary)).ToArray();
    }

    // A WebSocket stream owns one session. Reusing its bitmap/graphics buffers avoids
    // allocating a full monitor-sized Bitmap twice for every frame.
    public DesktopCaptureSession CreateCaptureSession(int display, int maxWidth, int quality) =>
        new(this, display, maxWidth, quality);

    public void Handle(JsonElement command)
    {
        if (command.ValueKind != JsonValueKind.Object || !command.TryGetProperty("kind", out var type) || type.ValueKind != JsonValueKind.String)
            return;
        switch (type.GetString())
        {
            case "pointer":
            {
                int display = Int(command, "display", 0, 64);
                var d = Displays().ElementAtOrDefault(display);
                if (d is null) return;
                double x = Double(command, "x", 0, 1), y = Double(command, "y", 0, 1);
                NativeInput.SetCursorPos(d.X + Math.Clamp((int)(x * d.Width), 0, d.Width - 1),
                                         d.Y + Math.Clamp((int)(y * d.Height), 0, d.Height - 1));
                break;
            }
            case "move":
                NativeInput.MoveRelative(Int(command, "dx", -400, 400), Int(command, "dy", -400, 400));
                break;
            case "mouse":
                NativeInput.Mouse(Int(command, "button", 0, 2), String(command, "action") == "down");
                break;
            case "wheel":
                NativeInput.Wheel(Int(command, "delta", -1200, 1200));
                break;
            case "key":
                NativeInput.Key(String(command, "code"), String(command, "action") == "down");
                break;
            case "text":
            {
                var text = String(command, "text");
                if (text.Length <= 2048) NativeInput.TypeText(text);
                break;
            }
        }
    }

    private static int Int(JsonElement obj, string name, int min, int max)
    {
        if (!obj.TryGetProperty(name, out var n) || n.ValueKind != JsonValueKind.Number || !n.TryGetInt32(out int i)) return min;
        return Math.Clamp(i, min, max);
    }
    private static double Double(JsonElement obj, string name, double min, double max)
    {
        if (!obj.TryGetProperty(name, out var n) || n.ValueKind != JsonValueKind.Number || !n.TryGetDouble(out double val) || !double.IsFinite(val)) return min;
        return Math.Clamp(val, min, max);
    }
    private static string String(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}

/// <summary>Reusable per-viewer capture buffers. GDI capture/JPEG is still CPU-bound,
/// but stable allocations plus client-driven pacing reduce stutter and old-frame lag.</summary>
public sealed class DesktopCaptureSession : IDisposable
{
    private readonly DesktopEngine _desktop;
    private readonly int _display, _maxWidth;
    private readonly ImageCodecInfo _jpegEncoder;
    private readonly EncoderParameters _parameters;
    private readonly MemoryStream _buffer = new(256 * 1024);
    private Bitmap? _full, _scaled;
    private Graphics? _captureGraphics, _resizeGraphics;
    private int _lastWidth, _lastHeight, _outputWidth, _outputHeight;

    internal DesktopCaptureSession(DesktopEngine desktop, int display, int maxWidth, int quality)
    {
        _desktop = desktop;
        _display = display;
        _maxWidth = maxWidth;
        _jpegEncoder = ImageCodecInfo.GetImageEncoders().First(x => x.MimeType == "image/jpeg");
        _parameters = new EncoderParameters(1);
        _parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)Math.Clamp(quality, 30, 90));
    }

    public byte[] Capture()
    {
        var d = _desktop.Displays().ElementAtOrDefault(_display) ?? throw new ArgumentOutOfRangeException(nameof(_display));
        if (_full is null || _lastWidth != d.Width || _lastHeight != d.Height)
        {
            _resizeGraphics?.Dispose(); _captureGraphics?.Dispose();
            _scaled?.Dispose(); _full?.Dispose();
            _lastWidth = d.Width; _lastHeight = d.Height;
            _outputWidth = Math.Max(1, Math.Min(_maxWidth, d.Width));
            _outputHeight = Math.Max(1, (int)Math.Round(d.Height * (_outputWidth / (double)d.Width)));
            _full = new Bitmap(d.Width, d.Height, PixelFormat.Format24bppRgb);
            _captureGraphics = Graphics.FromImage(_full);
            if (_outputWidth != d.Width || _outputHeight != d.Height)
            {
                _scaled = new Bitmap(_outputWidth, _outputHeight, PixelFormat.Format24bppRgb);
                _resizeGraphics = Graphics.FromImage(_scaled);
                _resizeGraphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
                _resizeGraphics.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighSpeed;
            }
            else { _scaled = null; _resizeGraphics = null; }
        }
        _captureGraphics!.CopyFromScreen(d.X, d.Y, 0, 0, new Size(d.Width, d.Height), CopyPixelOperation.SourceCopy);
        if (_resizeGraphics is not null)
            _resizeGraphics.DrawImage(_full!, 0, 0, _outputWidth, _outputHeight);
        _buffer.Position = 0; _buffer.SetLength(0);
        (_scaled ?? _full)!.Save(_buffer, _jpegEncoder, _parameters);
        return _buffer.ToArray();
    }

    public void Dispose()
    {
        _resizeGraphics?.Dispose(); _captureGraphics?.Dispose();
        _scaled?.Dispose(); _full?.Dispose(); _parameters.Dispose(); _buffer.Dispose();
    }
}

internal static class NativeInput
{
    private const uint InputMouse = 0, InputKeyboard = 1;
    private const uint MouseMove = 0x0001, MouseLeftDown = 0x0002, MouseLeftUp = 0x0004,
        MouseRightDown = 0x0008, MouseRightUp = 0x0010, MouseMiddleDown = 0x0020, MouseMiddleUp = 0x0040,
        MouseWheel = 0x0800;
    private const uint KeyUp = 0x0002, Unicode = 0x0004, Scancode = 0x0008;

    [StructLayout(LayoutKind.Sequential)] private struct INPUT { public uint type; public Payload data; }
    [StructLayout(LayoutKind.Explicit)] private struct Payload
    {
        [FieldOffset(0)] public MOUSEINPUT mouse;
        [FieldOffset(0)] public KEYBDINPUT key;
    }
    [StructLayout(LayoutKind.Sequential)] private struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public UIntPtr extraInfo;
    }
    [StructLayout(LayoutKind.Sequential)] private struct KEYBDINPUT
    {
        public ushort virtualKey, scanCode;
        public uint flags, time;
        public UIntPtr extraInfo;
    }
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, INPUT[] inputs, int size);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr context);

    private static void Send(params INPUT[] inputs) { if (inputs.Length > 0) SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>()); }
    public static void MoveRelative(int dx, int dy) => Send(new INPUT { type = InputMouse, data = new Payload { mouse = new MOUSEINPUT { dx = dx, dy = dy, dwFlags = MouseMove } } });
    public static void Mouse(int button, bool pressed)
    {
        uint flags = button switch
        {
            0 => pressed ? MouseLeftDown : MouseLeftUp,
            1 => pressed ? MouseMiddleDown : MouseMiddleUp,
            2 => pressed ? MouseRightDown : MouseRightUp,
            _ => 0
        };
        if (flags != 0) Send(new INPUT { type = InputMouse, data = new Payload { mouse = new MOUSEINPUT { dwFlags = flags } } });
    }
    public static void Wheel(int delta) => Send(new INPUT { type = InputMouse, data = new Payload { mouse = new MOUSEINPUT { mouseData = unchecked((uint)delta), dwFlags = MouseWheel } } });

    private static readonly Dictionary<string, ushort> Special = new(StringComparer.Ordinal)
    {
        ["Backspace"] = 0x08, ["Tab"] = 0x09, ["Enter"] = 0x0D, ["ShiftLeft"] = 0xA0,
        ["ShiftRight"] = 0xA1, ["ControlLeft"] = 0xA2, ["ControlRight"] = 0xA3,
        ["AltLeft"] = 0xA4, ["AltRight"] = 0xA5, ["Pause"] = 0x13, ["CapsLock"] = 0x14,
        ["Escape"] = 0x1B, ["Space"] = 0x20, ["PageUp"] = 0x21, ["PageDown"] = 0x22,
        ["End"] = 0x23, ["Home"] = 0x24, ["ArrowLeft"] = 0x25, ["ArrowUp"] = 0x26,
        ["ArrowRight"] = 0x27, ["ArrowDown"] = 0x28, ["Insert"] = 0x2D, ["Delete"] = 0x2E,
        ["MetaLeft"] = 0x5B, ["MetaRight"] = 0x5C,
        ["Semicolon"] = 0xBA, ["Equal"] = 0xBB, ["Comma"] = 0xBC, ["Minus"] = 0xBD,
        ["Period"] = 0xBE, ["Slash"] = 0xBF, ["Backquote"] = 0xC0, ["BracketLeft"] = 0xDB,
        ["Backslash"] = 0xDC, ["BracketRight"] = 0xDD, ["Quote"] = 0xDE
    };

    public static void Key(string code, bool pressed)
    {
        ushort vk;
        if (code.Length == 4 && code.StartsWith("Key", StringComparison.Ordinal) && char.IsAsciiLetterUpper(code[3]))
            vk = code[3];
        else if (code.Length == 6 && code.StartsWith("Digit", StringComparison.Ordinal) && char.IsAsciiDigit(code[5]))
            vk = code[5];
        else if (code.Length >= 2 && code[0] == 'F' && int.TryParse(code[1..], out int fn) && fn is >= 1 and <= 12)
            vk = (ushort)(0x6F + fn);
        else if (!Special.TryGetValue(code, out vk)) return;
        Send(new INPUT { type = InputKeyboard, data = new Payload { key = new KEYBDINPUT { virtualKey = vk, flags = pressed ? 0u : KeyUp } } });
    }

    public static void TypeText(string value)
    {
        // KEYEVENTF_UNICODE encodes UTF-16 code units; SendInput supports surrogate pairs.
        foreach (char ch in value)
        {
            Send(
                new INPUT { type = InputKeyboard, data = new Payload { key = new KEYBDINPUT { virtualKey = 0, scanCode = ch, flags = Unicode } } },
                new INPUT { type = InputKeyboard, data = new Payload { key = new KEYBDINPUT { virtualKey = 0, scanCode = ch, flags = Unicode | KeyUp } } }
            );
        }
    }
}
