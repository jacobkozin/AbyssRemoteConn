using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;

namespace AbyssRemote.Host.GameMode;

/// <summary>Game mode for one viewer: desktop duplication -> GPU color conversion -> hardware
/// H.264. One capture thread runs the pipeline. It only starts a new frame when the browser has
/// room for it (credits from <see cref="AllowFrames"/>), the encoder is ready, the frame-rate
/// interval has passed and the screen actually changed, so a slow phone or network never
/// builds a queue of old frames.</summary>
internal sealed class GpuVideoSource : IVideoSource
{
    private readonly Channel<VideoEvent> _events = Channel.CreateUnbounded<VideoEvent>(new UnboundedChannelOptions { SingleReader = true });
    private readonly SemaphoreSlim _credits = new(0, 64);
    private readonly ConcurrentDictionary<long, uint> _captureTimes = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly DisplayInfo _display;
    private readonly VideoSettings _settings;
    private readonly bool _repeatStaticFrames;
    private readonly Thread _thread;
    private DesktopDuplication? _duplication;
    private VideoConverter? _converter;
    private HardwareEncoder? _encoder;
    private volatile bool _stopping, _keyframeRequested = true;
    private bool _configSent;
    private (int X, int Y, bool Visible) _lastCursor = (-1, -1, false);

    /// <param name="repeatStaticFrames">Diagnostics only: keep encoding even when nothing changes.</param>
    public GpuVideoSource(DisplayInfo display, VideoSettings settings, bool repeatStaticFrames = false)
    {
        _display = display;
        _settings = settings;
        _repeatStaticFrames = repeatStaticFrames;
        try
        {
            // Build the whole pipeline up front so an unsupported PC fails before streaming starts.
            _duplication = DesktopDuplication.Open(display.Name);
            var (width, height) = VideoConverter.OutputSize(_duplication.Width, _duplication.Height, settings.MaxHeight);
            _converter = new VideoConverter(_duplication.Device, _duplication.Context, _duplication.Width, _duplication.Height, width, height, settings.Fps);
            _encoder = new HardwareEncoder(_duplication.Device, _duplication.AdapterLuid, width, height, settings.Fps, settings.BitrateKbps, OnEncoded);
        }
        catch
        {
            DisposePipeline();
            throw;
        }
        _thread = new Thread(CaptureLoop) { IsBackground = true, Name = "Abyss Remote game capture" };
        _thread.Start();
    }

    public string EncoderName => _encoder?.Name ?? "";
    public string AdapterName => _duplication?.AdapterName ?? "";
    public (int Width, int Height) OutputSize => (_converter?.OutputWidth ?? 0, _converter?.OutputHeight ?? 0);

    public ChannelReader<VideoEvent> Events => _events.Reader;

    public void AllowFrames(int count)
    {
        try { _credits.Release(count); } catch (SemaphoreFullException) { /* a misbehaving client cannot grow this past the cap */ }
    }

    public void RequestKeyframe() => _keyframeRequested = true;

    private void CaptureLoop()
    {
        var interval = TimeSpan.FromSeconds(1.0 / _settings.Fps);
        long duration100ns = 10_000_000 / _settings.Fps;
        var nextFrameAt = TimeSpan.Zero;
        bool haveImage = false, unavailable = false;
        long frameId = 0;
        using var timer = TimerResolution.OneMillisecond();
        try
        {
            while (!_stopping)
            {
                if (!_credits.Wait(250)) continue;                        // the browser is still busy
                var delay = nextFrameAt - _clock.Elapsed;
                if (delay > TimeSpan.Zero) Thread.Sleep(delay);
                if (!_encoder!.WaitForInputSlot(1000)) { _credits.Release(); continue; }

                // Wait for the screen to change. Pointer-only updates are forwarded without encoding.
                bool encode = false;
                while (!_stopping && !encode)
                {
                    var result = _duplication!.Acquire(_repeatStaticFrames ? 1u : 50u, out var info, out var texture);
                    if (result == DesktopDuplication.Result.Frame)
                    {
                        try
                        {
                            ForwardCursor(info);
                            if (info.LastPresentTime != 0 || !haveImage)
                            {
                                var desc = D3D11.GetTextureDesc(texture);
                                if (desc.Width != _converter!.InputWidth || desc.Height != _converter.InputHeight)
                                {
                                    _events.Writer.TryWrite(new VideoStatus("restart", "The monitor's resolution changed."));
                                    return;
                                }
                                _converter.CopyDesktop(texture);
                                haveImage = encode = true;
                            }
                        }
                        finally
                        {
                            _duplication.ReleaseFrame();
                            Com.Release(ref texture);
                        }
                    }
                    else if (result == DesktopDuplication.Result.Timeout)
                    {
                        encode = haveImage && (_keyframeRequested || _repeatStaticFrames);
                    }
                    else
                    {
                        try
                        {
                            _duplication.Duplicate();
                            if (unavailable) _events.Writer.TryWrite(new VideoStatus("resumed", ""));
                            unavailable = false;
                            _keyframeRequested = true;
                        }
                        catch (DesktopUnavailableException ex)
                        {
                            if (!unavailable) _events.Writer.TryWrite(new VideoStatus("unavailable", ex.Message));
                            unavailable = true;
                            Thread.Sleep(500);
                        }
                    }
                }
                if (_stopping) return;

                // Keep a steady cadence; after an idle period start fresh instead of bursting.
                var now = _clock.Elapsed;
                nextFrameAt = nextFrameAt + interval > now ? nextFrameAt + interval : now;
                IntPtr nv12 = _converter!.Convert();
                bool keyframe = _keyframeRequested;
                _keyframeRequested = false;
                _captureTimes[++frameId] = (uint)_clock.ElapsedMilliseconds;
                _encoder.Encode(nv12, frameId, _clock.Elapsed.Ticks, duration100ns, keyframe);
            }
        }
        catch (Exception ex) when (!_stopping)
        {
            _events.Writer.TryWrite(new VideoStatus("error", $"Game mode stopped: {ex.Message}"));
        }
        finally
        {
            _events.Writer.TryComplete();
        }
    }

    private void ForwardCursor(in DxgiOutduplFrameInfo info)
    {
        if (info.PointerShapeBufferSize > 0 && _duplication!.PointerShape(info.PointerShapeBufferSize) is { } shape)
        {
            try
            {
                int height = shape.Info.Type == Constants.PointerMonochrome ? (int)shape.Info.Height / 2 : (int)shape.Info.Height;
                _events.Writer.TryWrite(new CursorImage(CursorShapes.ToPng(shape.Pixels, shape.Info),
                    shape.Info.HotSpotX, shape.Info.HotSpotY, (int)shape.Info.Width, height));
            }
            catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or System.Runtime.InteropServices.ExternalException)
            {
                // An unusual pointer format: keep showing the previous shape.
            }
        }
        if (info.LastMouseUpdateTime == 0) return;
        var cursor = (info.PointerX, info.PointerY, info.PointerVisible != 0);
        if (cursor == _lastCursor) return;
        _lastCursor = cursor;
        _events.Writer.TryWrite(new CursorPosition(cursor.PointerX, cursor.PointerY, cursor.Item3));
    }

    private void OnEncoded(EncodedOutput output)
    {
        if (!_captureTimes.TryRemove(output.FrameId, out uint captured)) captured = (uint)_clock.ElapsedMilliseconds;
        if (!_configSent)
        {
            // The browser needs the codec string and avcC record (both from the SPS/PPS) before
            // the first frame. Until a keyframe arrives there is nothing it could decode.
            string? codec = output.Keyframe ? H264.CodecString(output.Data) : null;
            byte[]? description = codec is null ? null : H264.DecoderConfig(output.Data);
            if (description is null)
            {
                _keyframeRequested = true;
                AllowFrames(1); // this frame is dropped, so return its credit
                return;
            }
            _events.Writer.TryWrite(new VideoConfig(codec!, description, _converter!.OutputWidth, _converter.OutputHeight,
                _converter.InputWidth, _converter.InputHeight, _encoder?.Name ?? ""));
            _configSent = true;
        }
        _events.Writer.TryWrite(new EncodedVideoFrame(output.FrameId, output.Keyframe, captured, H264.ToAvcc(output.Data), output.EncodeMs));
    }

    public void Dispose()
    {
        if (_stopping) return;
        _stopping = true;
        _credits.Release();
        _thread.Join(TimeSpan.FromSeconds(3));
        DisposePipeline();
    }

    private void DisposePipeline()
    {
        _encoder?.Dispose(); _encoder = null;
        _converter?.Dispose(); _converter = null;
        _duplication?.Dispose(); _duplication = null;
    }
}
