using System.Runtime.InteropServices;

namespace AbyssRemote.Host.GameMode;

// Hand-written interop for the parts of DXGI, Direct3D 11 and Media Foundation that Game mode
// uses. The project deliberately has no third-party packages, so instead of a binding library
// each COM method is called through its vtable slot. Every slot number, GUID, struct layout and
// constant below was taken from the Windows SDK 10.0.26100 headers (d3d11.h, dxgi.h,
// dxgi1_2.h, mftransform.h, mfobjects.h, mfidl.h, mfapi.h, codecapi.h, strmif.h, d3d10misc.h).

internal sealed class NativeCallException(string action, int hresult)
    : Exception($"{action} failed (HRESULT 0x{hresult:X8})")
{
    public int HResult32 { get; } = hresult;
}

internal static unsafe class Com
{
    /// <summary>Address of the method in vtable slot <paramref name="index"/> of a COM object.</summary>
    public static void* Slot(IntPtr obj, int index) => (*(void***)obj)[index];

    public static void Check(int hr, string action)
    {
        if (hr < 0) throw new NativeCallException(action, hr);
    }

    public static IntPtr Query(IntPtr obj, in Guid iid, string what)
    {
        Check(Marshal.QueryInterface(obj, in iid, out var result), $"QueryInterface({what})");
        return result;
    }

    public static void Release(ref IntPtr obj)
    {
        if (obj == IntPtr.Zero) return;
        Marshal.Release(obj);
        obj = IntPtr.Zero;
    }
}

internal static class HResults
{
    public const int AccessDenied = unchecked((int)0x80070005);
    public const int DxgiNotFound = unchecked((int)0x887A0002);
    public const int DxgiUnsupported = unchecked((int)0x887A0004);
    public const int DxgiDeviceRemoved = unchecked((int)0x887A0005);
    public const int DxgiNotCurrentlyAvailable = unchecked((int)0x887A0022);
    public const int DxgiAccessLost = unchecked((int)0x887A0026);
    public const int DxgiWaitTimeout = unchecked((int)0x887A0027);
    public const int DxgiSessionDisconnected = unchecked((int)0x887A0028);
    public const int DxgiAccessDenied = unchecked((int)0x887A002B);
    public const int MfNoEventsAvailable = unchecked((int)0xC00D3E80);
    public const int MfShutdown = unchecked((int)0xC00D3E85);
    public const int MfNotAccepting = unchecked((int)0xC00D36B5);
    public const int MfTransformStreamChange = unchecked((int)0xC00D6D61);
    public const int MfTransformNeedMoreInput = unchecked((int)0xC00D6D72);
}

internal static class Iids
{
    public static readonly Guid DxgiFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");
    public static readonly Guid DxgiOutput1 = new("00cddea8-939b-4b83-a340-a685226666cc");
    public static readonly Guid D3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
    public static readonly Guid D3D11VideoDevice = new("10ec4d5b-975a-4689-b9e4-d0aac30fe333");
    public static readonly Guid D3D11VideoContext = new("61f21c45-3c0e-4a74-9cea-67100d9ad5e4");
    public static readonly Guid D3D10Multithread = new("9b7e4e00-342c-4106-a19f-4f2704f689f0");
    public static readonly Guid MfTransform = new("bf94c121-5b05-4e6f-8000-ba598961414d");
    public static readonly Guid MfMediaEventGenerator = new("2cd0bd52-bcd5-4b89-b62c-eadc0c031e7d");
    public static readonly Guid MfShutdown = new("97ec2ea4-0e42-4937-97ac-9d6d328824e1");
    public static readonly Guid CodecApi = new("901db4c7-31ce-41a2-85dc-8fa0bf41b8da");
}

internal static class MfGuids
{
    public static readonly Guid MediaTypeVideo = new("73646976-0000-0010-8000-00aa00389b71");
    public static readonly Guid VideoFormatH264 = new("34363248-0000-0010-8000-00aa00389b71"); // FourCC 'H264'
    public static readonly Guid VideoFormatNv12 = new("3231564e-0000-0010-8000-00aa00389b71"); // FourCC 'NV12'
    public static readonly Guid MajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    public static readonly Guid Subtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    public static readonly Guid FrameSize = new("1652c33d-d6b2-4012-b834-72030849a37d");
    public static readonly Guid FrameRate = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    public static readonly Guid AvgBitrate = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
    public static readonly Guid InterlaceMode = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    public static readonly Guid PixelAspectRatio = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
    public static readonly Guid Mpeg2Profile = new("ad76a80b-2d5c-4e0b-b375-64e520137036");
    public static readonly Guid MpegSequenceHeader = new("3c036de7-3ad0-4c9e-9216-ee6d6ac21cb3");
    public static readonly Guid VideoPrimaries = new("dbfbe4d7-0740-4ee0-8192-850ab0e21935");
    public static readonly Guid TransferFunction = new("5fb0fce9-be5c-4935-a811-ec838f8eed93");
    public static readonly Guid YuvMatrix = new("3e23d450-2c75-4d25-a00e-b91670d12327");
    public static readonly Guid VideoNominalRange = new("c21b8ee5-b956-4071-8daf-325edf5cab11");
    public static readonly Guid TransformAsyncUnlock = new("e5666d6b-3422-4eb6-a421-da7db1f8e207");
    public static readonly Guid D3D11Aware = new("206b4fc8-fcf9-4c51-afe3-9764369e33a0");
    public static readonly Guid LowLatency = new("9c27891a-ed7a-40e1-88e8-b22727a024ee"); // also CODECAPI_AVLowLatencyMode
    public static readonly Guid CategoryVideoEncoder = new("f79eac7d-e545-4387-bdee-d647d7bde42a");
    public static readonly Guid EnumAdapterLuid = new("1d39518c-e220-4da8-a07f-ba172552d6b1");
    public static readonly Guid FriendlyName = new("314ffbae-5b41-4c95-9c19-4e7d586face3");
    public static readonly Guid SampleCleanPoint = new("9cdf01d8-a0f0-43ba-b077-eaa06cbd728a");
}

internal static class CodecApiGuids
{
    public static readonly Guid RateControlMode = new("1c0608e9-370c-4710-8a58-cb6181c42423");
    public static readonly Guid MeanBitRate = new("f7222374-2144-4815-b550-a37f8e12ee52");
    public static readonly Guid BufferSize = new("0db96574-b6a4-4c8b-8106-3773de0310cd");
    public static readonly Guid GopSize = new("95f31b26-95a4-41aa-9303-246a7fc6eef1");
    public static readonly Guid BPictureCount = new("8d390aac-dc5c-4200-b57f-814d04babab2");
    public static readonly Guid ForceKeyFrame = new("398c1b98-8353-475a-9ef2-8f265d260345");
    public static readonly Guid QualityVsSpeed = new("98332df8-03cd-476b-89fa-3f9e442dec9f");
}

[StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left, Top, Right, Bottom; }
[StructLayout(LayoutKind.Sequential)] internal struct Luid { public uint LowPart; public int HighPart; }

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct DxgiAdapterDesc1
{
    public fixed char Description[128];
    public uint VendorId, DeviceId, SubSysId, Revision;
    public nuint DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
    public Luid AdapterLuid;
    public uint Flags;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct DxgiOutputDesc
{
    public fixed char DeviceName[32];
    public Rect DesktopCoordinates;
    public int AttachedToDesktop, Rotation;
    public IntPtr Monitor;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DxgiOutduplFrameInfo
{
    public long LastPresentTime, LastMouseUpdateTime;
    public uint AccumulatedFrames;
    public int RectsCoalesced, ProtectedContentMaskedOut;
    public int PointerX, PointerY, PointerVisible; // DXGI_OUTDUPL_POINTER_POSITION
    public uint TotalMetadataBufferSize, PointerShapeBufferSize;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DxgiOutduplPointerShapeInfo
{
    public uint Type, Width, Height, Pitch;
    public int HotSpotX, HotSpotY;
}

[StructLayout(LayoutKind.Sequential)]
internal struct D3D11Texture2DDesc
{
    public uint Width, Height, MipLevels, ArraySize, Format, SampleCount, SampleQuality, Usage, BindFlags, CpuAccessFlags, MiscFlags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct D3D11VideoProcessorContentDesc
{
    public uint InputFrameFormat, InputFrameRateNumerator, InputFrameRateDenominator, InputWidth, InputHeight;
    public uint OutputFrameRateNumerator, OutputFrameRateDenominator, OutputWidth, OutputHeight, Usage;
}

[StructLayout(LayoutKind.Sequential)] internal struct D3D11VideoProcessorInputViewDesc { public uint FourCC, ViewDimension, MipSlice, ArraySlice; }
[StructLayout(LayoutKind.Sequential)] internal struct D3D11VideoProcessorOutputViewDesc { public uint ViewDimension, MipSlice, FirstArraySlice, ArraySize; }

[StructLayout(LayoutKind.Sequential)]
internal struct D3D11VideoProcessorStream
{
    public int Enable;
    public uint OutputIndex, InputFrameOrField, PastFrames, FutureFrames;
    public IntPtr PastSurfaces, InputSurface, FutureSurfaces, PastSurfacesRight, InputSurfaceRight, FutureSurfacesRight;
}

[StructLayout(LayoutKind.Sequential)] internal struct MftOutputStreamInfo { public uint Flags, Size, Alignment; }
[StructLayout(LayoutKind.Sequential)] internal struct MftOutputDataBuffer { public uint StreamId; public IntPtr Sample; public uint Status; public IntPtr Events; }
[StructLayout(LayoutKind.Sequential)] internal struct MftRegisterTypeInfo { public Guid MajorType, Subtype; }

/// <summary>A VARIANT holding VT_UI4 or VT_BOOL (24 bytes on x64; the value starts at offset 8).</summary>
[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct Variant
{
    [FieldOffset(0)] public ushort Type;
    [FieldOffset(8)] public uint UInt32;
    [FieldOffset(8)] public short Bool;

    public static Variant FromUInt32(uint value) => new() { Type = 19 /* VT_UI4 */, UInt32 = value };
    public static Variant FromBool(bool value) => new() { Type = 11 /* VT_BOOL */, Bool = (short)(value ? -1 : 0) };
}

internal static class Constants
{
    public const uint FormatB8G8R8A8 = 87, FormatNv12 = 103;            // DXGI_FORMAT
    public const uint UsageDefault = 0, UsageStaging = 3;                // D3D11_USAGE
    public const uint BindShaderResource = 0x8, BindRenderTarget = 0x20, BindVideoEncoder = 0x400;
    public const uint CpuAccessRead = 0x20000;
    public const uint CreateBgraSupport = 0x20, CreateVideoSupport = 0x800;
    public const uint VideoUsageOptimalSpeed = 1, FrameFormatProgressive = 0, ViewDimensionTexture2D = 1;
    public const uint PointerMonochrome = 1, PointerColor = 2, PointerMaskedColor = 4;
    public const uint MftMessageCommandFlush = 0, MftMessageSetD3DManager = 2;
    public const uint MftMessageBeginStreaming = 0x10000000, MftMessageEndStreaming = 0x10000001,
                      MftMessageEndOfStream = 0x10000002, MftMessageStartOfStream = 0x10000003;
    public const uint MftEnumHardware = 0x4, MftEnumSortAndFilter = 0x40;
    public const uint MftOutputProvidesSamples = 0x100, MftOutputCanProvideSamples = 0x200;
    public const uint EventTransformNeedInput = 601, EventTransformHaveOutput = 602;
    public const uint InterlaceProgressive = 2, H264ProfileHigh = 100, RateControlCbr = 0;
    public const uint PrimariesBt709 = 2, TransferBt709 = 5, MatrixBt709 = 1, NominalRange16To235 = 2;
    public const uint MfVersion = 0x00020070, MfStartupNoSocket = 0x1;
}

internal static unsafe class Exports
{
    [DllImport("dxgi.dll")] public static extern int CreateDXGIFactory1(in Guid riid, out IntPtr factory);

    [DllImport("d3d11.dll")]
    public static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags,
        uint* featureLevels, uint featureLevelCount, uint sdkVersion, out IntPtr device, out uint featureLevel, out IntPtr context);

    [DllImport("mfplat.dll")] public static extern int MFStartup(uint version, uint flags);
    [DllImport("mfplat.dll")] public static extern int MFCreateMediaType(out IntPtr mediaType);
    [DllImport("mfplat.dll")] public static extern int MFCreateSample(out IntPtr sample);
    [DllImport("mfplat.dll")] public static extern int MFCreateAttributes(out IntPtr attributes, uint initialSize);
    [DllImport("mfplat.dll")] public static extern int MFCreateMemoryBuffer(uint maxLength, out IntPtr buffer);
    [DllImport("mfplat.dll")] public static extern int MFCreateDXGIDeviceManager(out uint resetToken, out IntPtr manager);
    [DllImport("mfplat.dll")] public static extern int MFCreateDXGISurfaceBuffer(in Guid riid, IntPtr surface, uint subresource, int bottomUp, out IntPtr buffer);
    [DllImport("mfplat.dll")]
    public static extern int MFTEnum2(Guid category, uint flags, MftRegisterTypeInfo* input, MftRegisterTypeInfo* output,
        IntPtr attributes, out IntPtr activates, out uint count);
    [DllImport("ole32.dll")] public static extern void CoTaskMemFree(IntPtr memory);
}

// ---- COM methods, grouped by interface. Comments give the vtable slot names.

internal static unsafe class Dxgi
{
    public static int EnumAdapters1(IntPtr factory, uint index, out IntPtr adapter)       // IDXGIFactory1 slot 12
    {
        IntPtr result;
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Com.Slot(factory, 12))(factory, index, &result);
        adapter = result;
        return hr;
    }

    public static int EnumOutputs(IntPtr adapter, uint index, out IntPtr output)          // IDXGIAdapter slot 7
    {
        IntPtr result;
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Com.Slot(adapter, 7))(adapter, index, &result);
        output = result;
        return hr;
    }

    public static DxgiAdapterDesc1 GetDesc1(IntPtr adapter)                                // IDXGIAdapter1 slot 10
    {
        DxgiAdapterDesc1 desc;
        Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, DxgiAdapterDesc1*, int>)Com.Slot(adapter, 10))(adapter, &desc), "IDXGIAdapter1.GetDesc1");
        return desc;
    }

    public static DxgiOutputDesc GetOutputDesc(IntPtr output)                              // IDXGIOutput slot 7
    {
        DxgiOutputDesc desc;
        Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, DxgiOutputDesc*, int>)Com.Slot(output, 7))(output, &desc), "IDXGIOutput.GetDesc");
        return desc;
    }

    public static int DuplicateOutput(IntPtr output1, IntPtr device, out IntPtr duplication) // IDXGIOutput1 slot 22
    {
        IntPtr result;
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)Com.Slot(output1, 22))(output1, device, &result);
        duplication = result;
        return hr;
    }

    public static int AcquireNextFrame(IntPtr duplication, uint timeoutMs, out DxgiOutduplFrameInfo info, out IntPtr resource) // slot 8
    {
        DxgiOutduplFrameInfo frame;
        IntPtr result;
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, DxgiOutduplFrameInfo*, IntPtr*, int>)Com.Slot(duplication, 8))(duplication, timeoutMs, &frame, &result);
        info = frame;
        resource = result;
        return hr;
    }

    public static int GetFramePointerShape(IntPtr duplication, uint bufferSize, void* buffer, out uint required, out DxgiOutduplPointerShapeInfo info) // slot 11
    {
        uint needed;
        DxgiOutduplPointerShapeInfo shape;
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, void*, uint*, DxgiOutduplPointerShapeInfo*, int>)Com.Slot(duplication, 11))(duplication, bufferSize, buffer, &needed, &shape);
        required = needed;
        info = shape;
        return hr;
    }

    public static int ReleaseFrame(IntPtr duplication) =>                                   // IDXGIOutputDuplication slot 14
        ((delegate* unmanaged[Stdcall]<IntPtr, int>)Com.Slot(duplication, 14))(duplication);
}

internal static unsafe class D3D11
{
    public static IntPtr CreateTexture2D(IntPtr device, in D3D11Texture2DDesc desc)        // ID3D11Device slot 5
    {
        IntPtr texture;
        fixed (D3D11Texture2DDesc* p = &desc)
            Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, D3D11Texture2DDesc*, IntPtr, IntPtr*, int>)Com.Slot(device, 5))(device, p, IntPtr.Zero, &texture), "ID3D11Device.CreateTexture2D");
        return texture;
    }

    public static D3D11Texture2DDesc GetTextureDesc(IntPtr texture)                         // ID3D11Texture2D slot 10 (returns void)
    {
        D3D11Texture2DDesc desc;
        ((delegate* unmanaged[Stdcall]<IntPtr, D3D11Texture2DDesc*, void>)Com.Slot(texture, 10))(texture, &desc);
        return desc;
    }

    public static void CopyResource(IntPtr context, IntPtr destination, IntPtr source) =>   // ID3D11DeviceContext slot 47
        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, void>)Com.Slot(context, 47))(context, destination, source);

    public static void Flush(IntPtr context) =>                                            // ID3D11DeviceContext slot 111
        ((delegate* unmanaged[Stdcall]<IntPtr, void>)Com.Slot(context, 111))(context);

    public static void SetMultithreadProtected(IntPtr multithread, bool on) =>             // ID3D10Multithread slot 5
        ((delegate* unmanaged[Stdcall]<IntPtr, int, int>)Com.Slot(multithread, 5))(multithread, on ? 1 : 0);

    public static void Enter(IntPtr multithread) => ((delegate* unmanaged[Stdcall]<IntPtr, void>)Com.Slot(multithread, 3))(multithread);
    public static void Leave(IntPtr multithread) => ((delegate* unmanaged[Stdcall]<IntPtr, void>)Com.Slot(multithread, 4))(multithread);
}

internal static unsafe class D3D11Video
{
    public static IntPtr CreateVideoProcessorEnumerator(IntPtr videoDevice, in D3D11VideoProcessorContentDesc desc) // ID3D11VideoDevice slot 10
    {
        IntPtr result;
        fixed (D3D11VideoProcessorContentDesc* p = &desc)
            Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, D3D11VideoProcessorContentDesc*, IntPtr*, int>)Com.Slot(videoDevice, 10))(videoDevice, p, &result), "CreateVideoProcessorEnumerator");
        return result;
    }

    public static IntPtr CreateVideoProcessor(IntPtr videoDevice, IntPtr enumerator) // slot 4
    {
        IntPtr result;
        Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, IntPtr*, int>)Com.Slot(videoDevice, 4))(videoDevice, enumerator, 0, &result), "CreateVideoProcessor");
        return result;
    }

    public static IntPtr CreateInputView(IntPtr videoDevice, IntPtr texture, IntPtr enumerator) // slot 8
    {
        var desc = new D3D11VideoProcessorInputViewDesc { ViewDimension = Constants.ViewDimensionTexture2D };
        IntPtr result;
        Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, D3D11VideoProcessorInputViewDesc*, IntPtr*, int>)Com.Slot(videoDevice, 8))(videoDevice, texture, enumerator, &desc, &result), "CreateVideoProcessorInputView");
        return result;
    }

    public static IntPtr CreateOutputView(IntPtr videoDevice, IntPtr texture, IntPtr enumerator) // slot 9
    {
        var desc = new D3D11VideoProcessorOutputViewDesc { ViewDimension = Constants.ViewDimensionTexture2D };
        IntPtr result;
        Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, D3D11VideoProcessorOutputViewDesc*, IntPtr*, int>)Com.Slot(videoDevice, 9))(videoDevice, texture, enumerator, &desc, &result), "CreateVideoProcessorOutputView");
        return result;
    }

    public static void SetOutputTargetRect(IntPtr videoContext, IntPtr processor, Rect rect) => // ID3D11VideoContext slot 13
        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int, Rect*, void>)Com.Slot(videoContext, 13))(videoContext, processor, 1, &rect);

    public static void SetOutputColorSpace(IntPtr videoContext, IntPtr processor, uint colorSpace) => // slot 15
        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint*, void>)Com.Slot(videoContext, 15))(videoContext, processor, &colorSpace);

    public static void SetStreamFrameFormat(IntPtr videoContext, IntPtr processor, uint format) => // slot 27
        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint, void>)Com.Slot(videoContext, 27))(videoContext, processor, 0, format);

    public static void SetStreamColorSpace(IntPtr videoContext, IntPtr processor, uint colorSpace) => // slot 28
        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint*, void>)Com.Slot(videoContext, 28))(videoContext, processor, 0, &colorSpace);

    public static void SetStreamSourceRect(IntPtr videoContext, IntPtr processor, Rect rect) => // slot 30
        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, int, Rect*, void>)Com.Slot(videoContext, 30))(videoContext, processor, 0, 1, &rect);

    public static void SetStreamDestRect(IntPtr videoContext, IntPtr processor, Rect rect) => // slot 31
        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, int, Rect*, void>)Com.Slot(videoContext, 31))(videoContext, processor, 0, 1, &rect);

    public static void SetStreamAutoProcessingMode(IntPtr videoContext, IntPtr processor, bool enable) => // slot 37
        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, int, void>)Com.Slot(videoContext, 37))(videoContext, processor, 0, enable ? 1 : 0);

    public static int Blt(IntPtr videoContext, IntPtr processor, IntPtr outputView, IntPtr inputView) // slot 53
    {
        var stream = new D3D11VideoProcessorStream { Enable = 1, InputSurface = inputView };
        return ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, uint, uint, D3D11VideoProcessorStream*, int>)Com.Slot(videoContext, 53))(videoContext, processor, outputView, 0, 1, &stream);
    }
}

internal static unsafe class Mf
{
    // IMFAttributes (also the base of IMFMediaType, IMFSample, IMFActivate, IMFMediaEvent)
    public static int GetUInt32(IntPtr attributes, in Guid key, out uint value)              // slot 7
    {
        uint result;
        int hr;
        fixed (Guid* k = &key) hr = ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, uint*, int>)Com.Slot(attributes, 7))(attributes, k, &result);
        value = result;
        return hr;
    }

    public static string? GetAllocatedString(IntPtr attributes, in Guid key)                 // slot 13
    {
        IntPtr text;
        uint length;
        int hr;
        fixed (Guid* k = &key) hr = ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, uint*, int>)Com.Slot(attributes, 13))(attributes, k, &text, &length);
        if (hr < 0) return null;
        try { return Marshal.PtrToStringUni(text, (int)length); }
        finally { Exports.CoTaskMemFree(text); }
    }

    public static byte[]? GetBlob(IntPtr attributes, in Guid key)                           // slots 14 (GetBlobSize) and 15 (GetBlob)
    {
        uint size;
        fixed (Guid* k = &key)
        {
            if (((delegate* unmanaged[Stdcall]<IntPtr, Guid*, uint*, int>)Com.Slot(attributes, 14))(attributes, k, &size) < 0 || size == 0) return null;
            var blob = new byte[size];
            fixed (byte* b = blob)
            {
                uint written;
                if (((delegate* unmanaged[Stdcall]<IntPtr, Guid*, byte*, uint, uint*, int>)Com.Slot(attributes, 15))(attributes, k, b, size, &written) < 0) return null;
            }
            return blob;
        }
    }

    public static void SetUInt32(IntPtr attributes, in Guid key, uint value)                  // slot 21
    {
        fixed (Guid* k = &key)
            Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, uint, int>)Com.Slot(attributes, 21))(attributes, k, value), "IMFAttributes.SetUINT32");
    }

    public static void SetUInt64(IntPtr attributes, in Guid key, ulong value)                 // slot 22
    {
        fixed (Guid* k = &key)
            Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, ulong, int>)Com.Slot(attributes, 22))(attributes, k, value), "IMFAttributes.SetUINT64");
    }

    public static void SetGuid(IntPtr attributes, in Guid key, in Guid value)                 // slot 24
    {
        fixed (Guid* k = &key)
        fixed (Guid* v = &value)
            Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, Guid*, int>)Com.Slot(attributes, 24))(attributes, k, v), "IMFAttributes.SetGUID");
    }

    public static void SetBlob(IntPtr attributes, in Guid key, ReadOnlySpan<byte> blob)       // slot 26
    {
        fixed (Guid* k = &key)
        fixed (byte* b = blob)
            Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, byte*, uint, int>)Com.Slot(attributes, 26))(attributes, k, b, (uint)blob.Length), "IMFAttributes.SetBlob");
    }

    // IMFActivate
    public static IntPtr ActivateObject(IntPtr activate, in Guid iid)                       // slot 33
    {
        IntPtr result;
        fixed (Guid* i = &iid)
            Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)Com.Slot(activate, 33))(activate, i, &result), "IMFActivate.ActivateObject");
        return result;
    }

    public static int ShutdownObject(IntPtr activate) =>                                    // slot 34
        ((delegate* unmanaged[Stdcall]<IntPtr, int>)Com.Slot(activate, 34))(activate);

    // IMFSample
    public static void SetSampleTime(IntPtr sample, long time) =>                           // slot 36
        Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, long, int>)Com.Slot(sample, 36))(sample, time), "IMFSample.SetSampleTime");

    public static void SetSampleDuration(IntPtr sample, long duration) =>                   // slot 38
        Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, long, int>)Com.Slot(sample, 38))(sample, duration), "IMFSample.SetSampleDuration");

    public static IntPtr ConvertToContiguousBuffer(IntPtr sample)                           // slot 41
    {
        IntPtr buffer;
        Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Com.Slot(sample, 41))(sample, &buffer), "IMFSample.ConvertToContiguousBuffer");
        return buffer;
    }

    public static void AddBuffer(IntPtr sample, IntPtr buffer) =>                           // slot 42
        Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)Com.Slot(sample, 42))(sample, buffer), "IMFSample.AddBuffer");

    // IMFMediaBuffer
    public static byte* Lock(IntPtr buffer, out uint currentLength)                         // slot 3
    {
        byte* data;
        uint max, current;
        Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, byte**, uint*, uint*, int>)Com.Slot(buffer, 3))(buffer, &data, &max, &current), "IMFMediaBuffer.Lock");
        currentLength = current;
        return data;
    }

    public static void Unlock(IntPtr buffer) => ((delegate* unmanaged[Stdcall]<IntPtr, int>)Com.Slot(buffer, 4))(buffer); // slot 4

    public static void SetCurrentLength(IntPtr buffer, uint length) =>                      // slot 6
        Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, int>)Com.Slot(buffer, 6))(buffer, length), "IMFMediaBuffer.SetCurrentLength");

    public static uint GetMaxLength(IntPtr buffer)                                          // slot 7
    {
        uint length;
        Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)Com.Slot(buffer, 7))(buffer, &length), "IMFMediaBuffer.GetMaxLength");
        return length;
    }

    // IMFMediaEventGenerator / IMFMediaEvent
    public static int GetEvent(IntPtr generator, out IntPtr mediaEvent)                     // IMFMediaEventGenerator slot 3 (blocking)
    {
        IntPtr result;
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Com.Slot(generator, 3))(generator, 0, &result);
        mediaEvent = result;
        return hr;
    }

    public static uint GetEventType(IntPtr mediaEvent)                                      // IMFMediaEvent slot 33
    {
        uint type;
        Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)Com.Slot(mediaEvent, 33))(mediaEvent, &type), "IMFMediaEvent.GetType");
        return type;
    }

    // IMFDXGIDeviceManager
    public static void ResetDevice(IntPtr manager, IntPtr device, uint token) =>            // slot 7
        Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, int>)Com.Slot(manager, 7))(manager, device, token), "IMFDXGIDeviceManager.ResetDevice");

    // IMFShutdown
    public static int Shutdown(IntPtr shutdown) => ((delegate* unmanaged[Stdcall]<IntPtr, int>)Com.Slot(shutdown, 3))(shutdown); // slot 3

    // ICodecAPI
    public static int SetCodecValue(IntPtr codecApi, in Guid api, Variant value)            // slot 9
    {
        fixed (Guid* a = &api)
            return ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, Variant*, int>)Com.Slot(codecApi, 9))(codecApi, a, &value);
    }
}

internal static unsafe class Mft
{
    // IMFTransform slots (mftransform.h)
    public static MftOutputStreamInfo GetOutputStreamInfo(IntPtr transform)                 // slot 7
    {
        MftOutputStreamInfo info;
        Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, MftOutputStreamInfo*, int>)Com.Slot(transform, 7))(transform, 0, &info), "IMFTransform.GetOutputStreamInfo");
        return info;
    }

    public static IntPtr GetAttributes(IntPtr transform)                                    // slot 8
    {
        IntPtr attributes;
        Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Com.Slot(transform, 8))(transform, &attributes), "IMFTransform.GetAttributes");
        return attributes;
    }

    public static int GetOutputAvailableType(IntPtr transform, uint index, out IntPtr type)  // slot 14
    {
        IntPtr result;
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr*, int>)Com.Slot(transform, 14))(transform, 0, index, &result);
        type = result;
        return hr;
    }

    public static int SetInputType(IntPtr transform, IntPtr type) =>                        // slot 15
        ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, uint, int>)Com.Slot(transform, 15))(transform, 0, type, 0);

    public static int SetOutputType(IntPtr transform, IntPtr type) =>                       // slot 16
        ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, uint, int>)Com.Slot(transform, 16))(transform, 0, type, 0);

    public static int GetOutputCurrentType(IntPtr transform, out IntPtr type)               // slot 18
    {
        IntPtr result;
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Com.Slot(transform, 18))(transform, 0, &result);
        type = result;
        return hr;
    }

    public static int ProcessMessage(IntPtr transform, uint message, nuint param) =>        // slot 23
        ((delegate* unmanaged[Stdcall]<IntPtr, uint, nuint, int>)Com.Slot(transform, 23))(transform, message, param);

    public static int ProcessInput(IntPtr transform, IntPtr sample) =>                      // slot 24
        ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, uint, int>)Com.Slot(transform, 24))(transform, 0, sample, 0);

    public static int ProcessOutput(IntPtr transform, ref MftOutputDataBuffer output)       // slot 25
    {
        uint status;
        fixed (MftOutputDataBuffer* o = &output)
            return ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, MftOutputDataBuffer*, uint*, int>)Com.Slot(transform, 25))(transform, 0, 1, o, &status);
    }
}
