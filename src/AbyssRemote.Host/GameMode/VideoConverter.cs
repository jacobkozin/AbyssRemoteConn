namespace AbyssRemote.Host.GameMode;

/// <summary>Converts the desktop image (BGRA) into NV12, the YUV layout hardware encoders take,
/// and scales it to the stream size in the same pass. It runs on the GPU's fixed-function video
/// processor, so no pixels are copied to the CPU. Output is BT.709 limited range, which the
/// encoder also writes into the stream so browsers show the right colors.</summary>
internal sealed class VideoConverter : IDisposable
{
    // NV12 textures are handed to the encoder, which reads them asynchronously, so a small
    // ring is used and a texture is only reused after several newer frames.
    public const int RingSize = 4;

    private readonly IntPtr _context;
    private IntPtr _videoDevice, _videoContext, _enumerator, _processor, _input, _inputView;
    private readonly IntPtr[] _outputs = new IntPtr[RingSize];
    private readonly IntPtr[] _outputViews = new IntPtr[RingSize];
    private int _next;

    public int InputWidth { get; }
    public int InputHeight { get; }
    public int OutputWidth { get; }
    public int OutputHeight { get; }

    /// <summary>Our own copy of the latest desktop image, so the duplication frame can be
    /// released right away and a static screen can be re-encoded on request.</summary>
    public IntPtr InputTexture => _input;

    public VideoConverter(IntPtr device, IntPtr context, int inputWidth, int inputHeight, int outputWidth, int outputHeight, int fps)
    {
        _context = context;
        InputWidth = inputWidth;
        InputHeight = inputHeight;
        OutputWidth = outputWidth;
        OutputHeight = outputHeight;
        try
        {
            _videoDevice = Com.Query(device, Iids.D3D11VideoDevice, "ID3D11VideoDevice");
            _videoContext = Com.Query(context, Iids.D3D11VideoContext, "ID3D11VideoContext");
            _enumerator = D3D11Video.CreateVideoProcessorEnumerator(_videoDevice, new D3D11VideoProcessorContentDesc
            {
                InputFrameFormat = Constants.FrameFormatProgressive,
                InputFrameRateNumerator = (uint)fps, InputFrameRateDenominator = 1,
                InputWidth = (uint)inputWidth, InputHeight = (uint)inputHeight,
                OutputFrameRateNumerator = (uint)fps, OutputFrameRateDenominator = 1,
                OutputWidth = (uint)outputWidth, OutputHeight = (uint)outputHeight,
                Usage = Constants.VideoUsageOptimalSpeed,
            });
            _processor = D3D11Video.CreateVideoProcessor(_videoDevice, _enumerator);

            _input = D3D11.CreateTexture2D(device, new D3D11Texture2DDesc
            {
                Width = (uint)inputWidth, Height = (uint)inputHeight, MipLevels = 1, ArraySize = 1,
                Format = Constants.FormatB8G8R8A8, SampleCount = 1, Usage = Constants.UsageDefault,
                BindFlags = Constants.BindRenderTarget | Constants.BindShaderResource,
            });
            _inputView = D3D11Video.CreateInputView(_videoDevice, _input, _enumerator);
            for (int i = 0; i < RingSize; i++)
            {
                _outputs[i] = D3D11.CreateTexture2D(device, new D3D11Texture2DDesc
                {
                    Width = (uint)outputWidth, Height = (uint)outputHeight, MipLevels = 1, ArraySize = 1,
                    Format = Constants.FormatNv12, SampleCount = 1, Usage = Constants.UsageDefault,
                    BindFlags = Constants.BindRenderTarget,
                });
                _outputViews[i] = D3D11Video.CreateOutputView(_videoDevice, _outputs[i], _enumerator);
            }

            var full = new Rect { Right = inputWidth, Bottom = inputHeight };
            var scaled = new Rect { Right = outputWidth, Bottom = outputHeight };
            D3D11Video.SetStreamFrameFormat(_videoContext, _processor, Constants.FrameFormatProgressive);
            D3D11Video.SetStreamSourceRect(_videoContext, _processor, full);
            D3D11Video.SetStreamDestRect(_videoContext, _processor, scaled);
            D3D11Video.SetOutputTargetRect(_videoContext, _processor, scaled);
            D3D11Video.SetStreamAutoProcessingMode(_videoContext, _processor, false);
            // D3D11_VIDEO_PROCESSOR_COLOR_SPACE bit fields: RGB_Range is bit 1 (0 = full 0-255),
            // YCbCr_Matrix is bit 2 (1 = BT.709), Nominal_Range is bits 4-5 (1 = 16-235).
            D3D11Video.SetStreamColorSpace(_videoContext, _processor, 0);
            D3D11Video.SetOutputColorSpace(_videoContext, _processor, (1u << 2) | (1u << 4));
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>Copies a freshly duplicated desktop texture into our input texture.</summary>
    public void CopyDesktop(IntPtr desktopTexture) => D3D11.CopyResource(_context, _input, desktopTexture);

    /// <summary>Converts the input texture into the next NV12 texture of the ring and returns it.</summary>
    public IntPtr Convert()
    {
        int slot = _next;
        _next = (_next + 1) % RingSize;
        Com.Check(D3D11Video.Blt(_videoContext, _processor, _outputViews[slot], _inputView), "VideoProcessorBlt");
        return _outputs[slot];
    }

    public void Dispose()
    {
        for (int i = 0; i < RingSize; i++)
        {
            Com.Release(ref _outputViews[i]);
            Com.Release(ref _outputs[i]);
        }
        Com.Release(ref _inputView);
        Com.Release(ref _input);
        Com.Release(ref _processor);
        Com.Release(ref _enumerator);
        Com.Release(ref _videoContext);
        Com.Release(ref _videoDevice);
    }

    /// <summary>Stream size for a desktop of the given size: at most <paramref name="maxHeight"/>
    /// lines, same aspect ratio, never larger than the desktop, and even (NV12 needs that).</summary>
    public static (int Width, int Height) OutputSize(int desktopWidth, int desktopHeight, int maxHeight)
    {
        int height = Math.Min(desktopHeight, maxHeight) & ~1;
        int width = (int)Math.Round(desktopWidth * (height / (double)desktopHeight) / 2) * 2;
        return (Math.Max(2, width), Math.Max(2, height));
    }
}
