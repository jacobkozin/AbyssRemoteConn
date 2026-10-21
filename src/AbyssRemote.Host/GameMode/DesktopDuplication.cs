namespace AbyssRemote.Host.GameMode;

/// <summary>GPU screen capture with the DXGI Desktop Duplication API. Windows hands over each
/// new desktop image as a texture that never leaves the graphics card, and only when the
/// screen actually changed. The Direct3D device is created on the GPU that drives the chosen
/// monitor, because duplication (and the hardware encoder that follows) must run there.</summary>
internal sealed class DesktopDuplication : IDisposable
{
    private IntPtr _output1, _duplication;

    public IntPtr Device { get; private set; }
    public IntPtr Context { get; private set; }
    public IntPtr Multithread { get; private set; }
    public Luid AdapterLuid { get; }
    public string AdapterName { get; }
    public string OutputName { get; }
    public int Width { get; private set; }
    public int Height { get; private set; }

    private DesktopDuplication(IntPtr adapter, IntPtr output, DxgiAdapterDesc1 adapterDesc, DxgiOutputDesc outputDesc)
    {
        unsafe
        {
            AdapterName = new string(adapterDesc.Description).TrimEnd('\0');
            OutputName = new string(outputDesc.DeviceName).TrimEnd('\0');
        }
        AdapterLuid = adapterDesc.AdapterLuid;
        Width = outputDesc.DesktopCoordinates.Right - outputDesc.DesktopCoordinates.Left;
        Height = outputDesc.DesktopCoordinates.Bottom - outputDesc.DesktopCoordinates.Top;
        try
        {
            CreateDevice(adapter);
            _output1 = Com.Query(output, Iids.DxgiOutput1, "IDXGIOutput1");
            Duplicate();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>Opens duplication for a monitor by its Windows device name, e.g. \\.\DISPLAY2.</summary>
    public static DesktopDuplication Open(string deviceName)
    {
        Com.Check(Exports.CreateDXGIFactory1(Iids.DxgiFactory1, out var factory), "CreateDXGIFactory1");
        try
        {
            for (uint a = 0; Dxgi.EnumAdapters1(factory, a, out var adapter) >= 0; a++)
            {
                try
                {
                    var adapterDesc = Dxgi.GetDesc1(adapter);
                    for (uint o = 0; Dxgi.EnumOutputs(adapter, o, out var output) >= 0; o++)
                    {
                        try
                        {
                            var outputDesc = Dxgi.GetOutputDesc(output);
                            string name;
                            unsafe { name = new string(outputDesc.DeviceName).TrimEnd('\0'); }
                            if (name.Equals(deviceName, StringComparison.OrdinalIgnoreCase))
                                return new DesktopDuplication(adapter, output, adapterDesc, outputDesc);
                        }
                        finally { Com.Release(ref output); }
                    }
                }
                finally { Com.Release(ref adapter); }
            }
        }
        finally { Com.Release(ref factory); }
        throw new VideoUnsupportedException($"No graphics adapter output was found for {deviceName}.");
    }

    private unsafe void CreateDevice(IntPtr adapter)
    {
        uint* levels = stackalloc uint[] { 0xb100, 0xb000, 0xa100, 0xa000 }; // 11.1, 11.0, 10.1, 10.0
        Com.Check(Exports.D3D11CreateDevice(adapter, 0 /* D3D_DRIVER_TYPE_UNKNOWN: use this adapter */, IntPtr.Zero,
            Constants.CreateBgraSupport | Constants.CreateVideoSupport, levels, 4, 7 /* D3D11_SDK_VERSION */,
            out var device, out _, out var context), "D3D11CreateDevice");
        Device = device;
        Context = context;
        // Media Foundation's encoder uses this device from its own threads.
        Multithread = Com.Query(device, Iids.D3D10Multithread, "ID3D10Multithread");
        D3D11.SetMultithreadProtected(Multithread, true);
    }

    /// <summary>(Re)starts duplication. Needed again after Windows reports "access lost", which
    /// happens on display mode changes, the lock screen and UAC prompts.</summary>
    public void Duplicate()
    {
        Com.Release(ref _duplication);
        int hr = Dxgi.DuplicateOutput(_output1, Device, out _duplication);
        // Seen once on the dev PC right after another capturing process was killed: Windows can
        // briefly refuse with E_INVALIDARG or "not currently available". Retry a few times.
        for (int attempt = 0; attempt < 4 && hr is unchecked((int)0x80070057) or HResults.DxgiNotCurrentlyAvailable; attempt++)
        {
            Thread.Sleep(250);
            hr = Dxgi.DuplicateOutput(_output1, Device, out _duplication);
        }
        if (hr is HResults.AccessDenied or HResults.DxgiSessionDisconnected)
            throw new DesktopUnavailableException("The PC is locked or showing a secure prompt (UAC). Waiting for the desktop…");
        if (hr == HResults.DxgiNotCurrentlyAvailable)
            throw new VideoUnsupportedException("Too many apps are already capturing this monitor (Windows allows only a few at once).");
        if (hr == HResults.DxgiUnsupported)
            throw new VideoUnsupportedException("This monitor cannot be captured with desktop duplication (e.g. some HDR or multi-GPU setups).");
        Com.Check(hr, "IDXGIOutput1.DuplicateOutput");
    }

    public enum Result { Frame, Timeout, AccessLost }

    /// <summary>Waits up to <paramref name="timeoutMs"/> for the next desktop update. On
    /// <see cref="Result.Frame"/> the caller must copy what it needs and call <see cref="ReleaseFrame"/>.</summary>
    public Result Acquire(uint timeoutMs, out DxgiOutduplFrameInfo info, out IntPtr texture)
    {
        texture = IntPtr.Zero;
        if (_duplication == IntPtr.Zero)
        {
            info = default;
            return Result.AccessLost;
        }
        int hr = Dxgi.AcquireNextFrame(_duplication, timeoutMs, out info, out var resource);
        if (hr == HResults.DxgiWaitTimeout) return Result.Timeout;
        if (hr is HResults.DxgiAccessLost or HResults.AccessDenied)
        {
            Com.Release(ref _duplication);
            return Result.AccessLost;
        }
        if (hr == HResults.DxgiDeviceRemoved)
            throw new VideoUnsupportedException("The graphics driver was reset. Reconnect to start again.");
        Com.Check(hr, "IDXGIOutputDuplication.AcquireNextFrame");
        try { texture = Com.Query(resource, Iids.D3D11Texture2D, "ID3D11Texture2D"); }
        finally { Com.Release(ref resource); }
        return Result.Frame;
    }

    public void ReleaseFrame()
    {
        if (_duplication != IntPtr.Zero) Dxgi.ReleaseFrame(_duplication);
    }

    /// <summary>Reads the new mouse pointer shape after a frame reported one.</summary>
    public unsafe (byte[] Pixels, DxgiOutduplPointerShapeInfo Info)? PointerShape(uint bufferSize)
    {
        if (_duplication == IntPtr.Zero || bufferSize == 0) return null;
        var buffer = new byte[bufferSize];
        fixed (byte* b = buffer)
        {
            if (Dxgi.GetFramePointerShape(_duplication, bufferSize, b, out _, out var info) < 0) return null;
            return (buffer, info);
        }
    }

    /// <summary>Updates the size after access was lost (the resolution may have changed).</summary>
    public void RefreshSize(int width, int height)
    {
        Width = width;
        Height = height;
    }

    public void Dispose()
    {
        Com.Release(ref _duplication);
        Com.Release(ref _output1);
        var multithread = Multithread; Com.Release(ref multithread); Multithread = IntPtr.Zero;
        var context = Context; Com.Release(ref context); Context = IntPtr.Zero;
        var device = Device; Com.Release(ref device); Device = IntPtr.Zero;
    }
}

/// <summary>Game mode cannot run for this monitor or PC; the browser falls back to Desktop mode.</summary>
public sealed class VideoUnsupportedException(string message) : Exception(message);
