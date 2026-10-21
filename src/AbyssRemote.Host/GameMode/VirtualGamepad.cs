using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace AbyssRemote.Host.GameMode;

/// <summary>One Xbox 360 controller state in XInput terms (XUSB_REPORT).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct XusbReport
{
    public ushort Buttons;
    public byte LeftTrigger, RightTrigger;
    public short ThumbLX, ThumbLY, ThumbRX, ThumbRY;

    // XUSB_GAMEPAD_* button flags (same values as XInput's XINPUT_GAMEPAD_*).
    public const ushort DPadUp = 0x0001, DPadDown = 0x0002, DPadLeft = 0x0004, DPadRight = 0x0008,
        Start = 0x0010, Back = 0x0020, LeftThumb = 0x0040, RightThumb = 0x0080,
        LeftShoulder = 0x0100, RightShoulder = 0x0200, Guide = 0x0400, A = 0x1000, B = 0x2000, X = 0x4000, Y = 0x8000;

    // Browser "standard" gamepad button order -> XUSB flag (6 and 7 are the analog triggers).
    private static readonly ushort[] StandardButtons =
    [
        A, B, X, Y, LeftShoulder, RightShoulder, 0, 0, Back, Start, LeftThumb, RightThumb,
        DPadUp, DPadDown, DPadLeft, DPadRight, Guide,
    ];

    /// <summary>Converts the browser's Gamepad API state (W3C "standard" mapping) into an
    /// XInput report. Every value is clamped; browsers report stick Y with down positive,
    /// XInput with up positive.</summary>
    public static XusbReport FromBrowser(JsonElement command)
    {
        uint pressed = (uint)InputCommand.Int(command, "buttons", 0, (1 << StandardButtons.Length) - 1);
        ushort buttons = 0;
        for (int i = 0; i < StandardButtons.Length; i++)
            if ((pressed & (1u << i)) != 0) buttons |= StandardButtons[i];
        return new XusbReport
        {
            Buttons = buttons,
            LeftTrigger = (byte)Math.Round(InputCommand.Double(command, "lt", 0, 1) * 255),
            RightTrigger = (byte)Math.Round(InputCommand.Double(command, "rt", 0, 1) * 255),
            ThumbLX = Axis(InputCommand.Double(command, "lx", -1, 1)),
            ThumbLY = Axis(-InputCommand.Double(command, "ly", -1, 1)),
            ThumbRX = Axis(InputCommand.Double(command, "rx", -1, 1)),
            ThumbRY = Axis(-InputCommand.Double(command, "ry", -1, 1)),
        };
    }

    private static short Axis(double value) => (short)Math.Clamp(Math.Round(value * 32767), -32768, 32767);
}

/// <summary>The ViGEmBus driver (github.com/nefarius/ViGEmBus), which adds virtual game
/// controllers that games see as real ones. Abyss Remote does not install it. This class talks
/// to the driver with DeviceIoControl using the driver's documented interface
/// (include/ViGEm/km/BusShared.h), so no client library is needed.</summary>
internal sealed class ViGEmBus : IDisposable
{
    // GUID_DEVINTERFACE_BUSENUM_VIGEM
    private static readonly Guid BusInterface = new("96e42b22-f5e9-42f8-b043-ed0f932f014f");

    // CTL_CODE(FILE_DEVICE_BUS_EXTENDER 0x2A, function, METHOD_BUFFERED, FILE_WRITE_DATA)
    // = 0x2A << 16 | 2 << 14 | function << 2, with function = 0x801 + offset.
    public const uint IoctlPlugin = 0x2AA004, IoctlUnplug = 0x2AA008, IoctlCheckVersion = 0x2AA00C,
        IoctlWaitDeviceReady = 0x2AA010, IoctlXusbSubmitReport = 0x2AA808;
    private const uint CommonVersion = 0x0001;
    private const ushort MicrosoftVendor = 0x045E, Xbox360Product = 0x028E;

    private static readonly object Sync = new();
    private static Lazy<(ViGEmBus? Bus, string? Problem)> _shared = new(Open);

    private readonly SafeFileHandle _handle;
    private readonly HashSet<uint> _serials = new();

    private ViGEmBus(SafeFileHandle handle) => _handle = handle;

    /// <summary>The process-wide driver connection, or why it is unavailable.</summary>
    public static (ViGEmBus? Bus, string? Problem) Shared
    {
        get
        {
            var shared = _shared.Value;
            // Retry later if the driver was missing (it may be installed while we run).
            if (shared.Bus is null) lock (Sync) _shared = new Lazy<(ViGEmBus?, string?)>(Open);
            return shared;
        }
    }

    private static (ViGEmBus?, string?) Open()
    {
        string? path = InterfacePath();
        if (path is null) return (null, "Controllers need the free ViGEmBus driver on the PC (see README, \"Controllers\").");
        var handle = CreateFileW(path, 0xC0000000 /* GENERIC_READ | GENERIC_WRITE */, 0x3 /* share read/write */, IntPtr.Zero,
            3 /* OPEN_EXISTING */, 0x80 /* FILE_ATTRIBUTE_NORMAL */, IntPtr.Zero);
        if (handle.IsInvalid) return (null, $"The ViGEmBus driver could not be opened (error {Marshal.GetLastWin32Error()}).");
        var bus = new ViGEmBus(handle);
        Span<uint> version = [8, CommonVersion]; // VIGEM_CHECK_VERSION { Size, Version }
        if (!bus.Control(IoctlCheckVersion, MemoryMarshal.AsBytes(version)))
        {
            bus.Dispose();
            return (null, "The installed ViGEmBus driver version is not supported. Install the latest release.");
        }
        return (bus, null);
    }

    /// <summary>Plugs in a new virtual Xbox 360 controller and returns its serial number.</summary>
    public uint? PlugIn()
    {
        lock (_serials)
        {
            for (uint serial = 1; serial <= 16; serial++)
            {
                if (_serials.Contains(serial)) continue;
                // VIGEM_PLUGIN_TARGET { Size = 16, SerialNo, TargetType = Xbox360Wired (0), VendorId, ProductId }
                Span<byte> plugin = stackalloc byte[16];
                MemoryMarshal.Write(plugin, 16u);
                MemoryMarshal.Write(plugin[4..], serial);
                MemoryMarshal.Write(plugin[8..], 0u);
                MemoryMarshal.Write(plugin[12..], MicrosoftVendor);
                MemoryMarshal.Write(plugin[14..], Xbox360Product);
                if (!Control(IoctlPlugin, plugin)) continue; // serial taken by another app: try the next one
                Span<uint> ready = [8, serial];             // VIGEM_WAIT_DEVICE_READY { Size, SerialNo }
                if (!Control(IoctlWaitDeviceReady, MemoryMarshal.AsBytes(ready))) Thread.Sleep(100); // older drivers lack this call
                _serials.Add(serial);
                return serial;
            }
            return null;
        }
    }

    public bool Submit(uint serial, in XusbReport report)
    {
        // XUSB_SUBMIT_REPORT { Size = 20, SerialNo, XUSB_REPORT (12 bytes) }
        Span<byte> message = stackalloc byte[20];
        MemoryMarshal.Write(message, 20u);
        MemoryMarshal.Write(message[4..], serial);
        MemoryMarshal.Write(message[8..], in report);
        return Control(IoctlXusbSubmitReport, message);
    }

    public void Unplug(uint serial)
    {
        lock (_serials)
        {
            if (!_serials.Remove(serial)) return;
            Span<uint> unplug = [8, serial]; // VIGEM_UNPLUG_TARGET { Size, SerialNo }
            Control(IoctlUnplug, MemoryMarshal.AsBytes(unplug));
        }
    }

    private unsafe bool Control(uint code, ReadOnlySpan<byte> input)
    {
        fixed (byte* p = input)
            return DeviceIoControl(_handle, code, p, (uint)input.Length, null, 0, out _, IntPtr.Zero);
    }

    private static string? InterfacePath()
    {
        var guid = BusInterface;
        if (CM_Get_Device_Interface_List_SizeW(out uint length, ref guid, IntPtr.Zero, 0 /* present only */) != 0 || length <= 1) return null;
        var buffer = new char[length];
        if (CM_Get_Device_Interface_ListW(ref guid, IntPtr.Zero, buffer, length, 0) != 0) return null;
        string first = new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        return first.Length == 0 ? null : first;
    }

    public void Dispose() => _handle.Dispose();

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_Interface_List_SizeW(out uint length, ref Guid interfaceClass, IntPtr deviceId, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_Interface_ListW(ref Guid interfaceClass, IntPtr deviceId, char[] buffer, uint length, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern unsafe bool DeviceIoControl(SafeFileHandle device, uint code, byte* input, uint inputLength,
        byte* output, uint outputLength, out uint returned, IntPtr overlapped);
}

/// <summary>The virtual controllers of one input connection (up to four, like XInput).
/// Disposing unplugs them, so a dropped phone never leaves a controller stuck in a game.</summary>
internal sealed class GamepadSet : IDisposable
{
    public const int MaxPads = 4;
    private readonly Dictionary<int, uint> _pads = new();
    private readonly Action<object> _notify;
    private bool _reported;

    public GamepadSet(Action<object> notify) => _notify = notify;

    public void Handle(JsonElement command)
    {
        int index = InputCommand.Int(command, "index", 0, MaxPads - 1);
        var (bus, problem) = ViGEmBus.Shared;
        if (bus is null)
        {
            if (!_reported) _notify(new { type = "notice", message = problem });
            _reported = true;
            return;
        }
        if (!_pads.TryGetValue(index, out uint serial))
        {
            if (bus.PlugIn() is not { } plugged)
            {
                if (!_reported) _notify(new { type = "notice", message = "No free virtual controller slot on the PC." });
                _reported = true;
                return;
            }
            _pads[index] = serial = plugged;
        }
        bus.Submit(serial, XusbReport.FromBrowser(command));
    }

    /// <summary>Lets go of every button and centers the sticks (when the browser loses focus).</summary>
    public void ReleaseAll()
    {
        if (_pads.Count == 0 || ViGEmBus.Shared.Bus is not { } bus) return;
        foreach (uint serial in _pads.Values) bus.Submit(serial, default);
    }

    public void Dispose()
    {
        if (ViGEmBus.Shared.Bus is { } bus)
            foreach (uint serial in _pads.Values) bus.Unplug(serial);
        _pads.Clear();
    }
}
