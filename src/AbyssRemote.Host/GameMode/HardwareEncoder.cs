using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AbyssRemote.Host.GameMode;

/// <summary>One encoded H.264 access unit in Annex B form (start codes, SPS/PPS before keyframes).</summary>
internal sealed record EncodedOutput(long FrameId, bool Keyframe, byte[] Data, double EncodeMs);

/// <summary>Hardware H.264 encoding through a Media Foundation transform (NVENC on NVIDIA,
/// VCN on AMD, Quick Sync on Intel, whichever the GPU that drives the monitor provides). The
/// encoder reads the NV12 GPU texture directly. Hardware transforms are asynchronous: they
/// raise "need input" and "have output" events, handled here on a dedicated thread.</summary>
internal sealed unsafe class HardwareEncoder : IDisposable
{
    private static readonly Lazy<int> Startup = new(() => Exports.MFStartup(Constants.MfVersion, Constants.MfStartupNoSocket));

    private readonly Action<EncodedOutput> _onOutput;
    private readonly SemaphoreSlim _inputSlots = new(0, int.MaxValue);
    private readonly ConcurrentQueue<(long Id, long Submitted)> _inFlight = new();
    private readonly Thread _eventThread;
    private readonly bool _encoderAllocatesSamples;
    private readonly uint _outputBufferSize;
    private IntPtr _activate, _transform, _events, _codecApi, _manager;
    private volatile bool _stopping;

    public string Name { get; }
    public int Width { get; }
    public int Height { get; }

    public HardwareEncoder(IntPtr device, Luid adapter, int width, int height, int fps, int bitrateKbps, Action<EncodedOutput> onOutput)
    {
        Com.Check(Startup.Value, "MFStartup");
        _onOutput = onOutput;
        Width = width;
        Height = height;
        try
        {
            _activate = FindEncoder(adapter, out string name);
            Name = name;
            _transform = Mf.ActivateObject(_activate, Iids.MfTransform);
            IntPtr attributes = Mft.GetAttributes(_transform);
            try
            {
                Mf.SetUInt32(attributes, MfGuids.TransformAsyncUnlock, 1); // required before using an async MFT
                try { Mf.SetUInt32(attributes, MfGuids.LowLatency, 1); } catch (NativeCallException) { /* optional */ }
            }
            finally { Com.Release(ref attributes); }

            Com.Check(Exports.MFCreateDXGIDeviceManager(out uint resetToken, out _manager), "MFCreateDXGIDeviceManager");
            Mf.ResetDevice(_manager, device, resetToken);
            Com.Check(Mft.ProcessMessage(_transform, Constants.MftMessageSetD3DManager, (nuint)_manager), "IMFTransform: set D3D manager");

            if (Marshal.QueryInterface(_transform, in Iids.CodecApi, out var codecApi) >= 0) _codecApi = codecApi;
            // Low-latency streaming settings. Encoders may ignore ones they do not support.
            SetCodec(MfGuids.LowLatency, Variant.FromBool(true));
            SetCodec(CodecApiGuids.RateControlMode, Variant.FromUInt32(Constants.RateControlCbr));
            SetCodec(CodecApiGuids.MeanBitRate, Variant.FromUInt32((uint)bitrateKbps * 1000));
            SetCodec(CodecApiGuids.BPictureCount, Variant.FromUInt32(0));    // B-frames add a frame of delay
            SetCodec(CodecApiGuids.GopSize, Variant.FromUInt32((uint)fps * 60)); // keyframes mostly on request

            SetMediaType(isOutput: true, width, height, fps, bitrateKbps);
            SetMediaType(isOutput: false, width, height, fps, bitrateKbps);

            var streamInfo = Mft.GetOutputStreamInfo(_transform);
            _encoderAllocatesSamples = (streamInfo.Flags & (Constants.MftOutputProvidesSamples | Constants.MftOutputCanProvideSamples)) != 0;
            _outputBufferSize = Math.Max(streamInfo.Size, (uint)(width * height * 2));

            _events = Com.Query(_transform, Iids.MfMediaEventGenerator, "IMFMediaEventGenerator");
            Com.Check(Mft.ProcessMessage(_transform, Constants.MftMessageBeginStreaming, 0), "IMFTransform: begin streaming");
            Com.Check(Mft.ProcessMessage(_transform, Constants.MftMessageStartOfStream, 0), "IMFTransform: start of stream");
        }
        catch
        {
            ReleaseAll();
            throw;
        }
        _eventThread = new Thread(EventLoop) { IsBackground = true, Name = "Abyss Remote encoder events" };
        _eventThread.Start();
    }

    /// <summary>Waits until the encoder asks for another frame.</summary>
    public bool WaitForInputSlot(int timeoutMs) => !_stopping && _inputSlots.Wait(timeoutMs);

    /// <summary>Queues one NV12 texture for encoding. Call only after <see cref="WaitForInputSlot"/>.</summary>
    public void Encode(IntPtr nv12Texture, long frameId, long time100ns, long duration100ns, bool keyframe)
    {
        if (keyframe) SetCodec(CodecApiGuids.ForceKeyFrame, Variant.FromUInt32(1));
        IntPtr sample = IntPtr.Zero, buffer = IntPtr.Zero;
        try
        {
            Com.Check(Exports.MFCreateSample(out sample), "MFCreateSample");
            Com.Check(Exports.MFCreateDXGISurfaceBuffer(Iids.D3D11Texture2D, nv12Texture, 0, 0, out buffer), "MFCreateDXGISurfaceBuffer");
            Mf.SetCurrentLength(buffer, Mf.GetMaxLength(buffer));
            Mf.AddBuffer(sample, buffer);
            Mf.SetSampleTime(sample, time100ns);
            Mf.SetSampleDuration(sample, duration100ns);
            _inFlight.Enqueue((frameId, Stopwatch.GetTimestamp()));
            Com.Check(Mft.ProcessInput(_transform, sample), "IMFTransform.ProcessInput");
        }
        finally
        {
            Com.Release(ref buffer);
            Com.Release(ref sample);
        }
    }

    private void EventLoop()
    {
        while (!_stopping)
        {
            if (Mf.GetEvent(_events, out var mediaEvent) < 0) return; // MF_E_SHUTDOWN after Dispose
            try
            {
                switch (Mf.GetEventType(mediaEvent))
                {
                    case Constants.EventTransformNeedInput: _inputSlots.Release(); break;
                    case Constants.EventTransformHaveOutput: ReadOutput(); break;
                }
            }
            catch (NativeCallException) when (_stopping) { return; }
            finally { Com.Release(ref mediaEvent); }
        }
    }

    private void ReadOutput()
    {
        while (true)
        {
            var output = new MftOutputDataBuffer();
            IntPtr ownSample = IntPtr.Zero;
            if (!_encoderAllocatesSamples)
            {
                Com.Check(Exports.MFCreateSample(out ownSample), "MFCreateSample");
                Com.Check(Exports.MFCreateMemoryBuffer(_outputBufferSize, out var memory), "MFCreateMemoryBuffer");
                try { Mf.AddBuffer(ownSample, memory); } finally { Com.Release(ref memory); }
                output.Sample = ownSample;
            }
            int hr = Mft.ProcessOutput(_transform, ref output);
            if (output.Events != IntPtr.Zero) Com.Release(ref output.Events);
            if (hr == HResults.MfTransformStreamChange)
            {
                // The encoder refined its output format (often to add SPS/PPS); accept it and retry.
                Com.Release(ref ownSample);
                if (Mft.GetOutputAvailableType(_transform, 0, out var type) >= 0)
                {
                    try { Com.Check(Mft.SetOutputType(_transform, type), "IMFTransform.SetOutputType (stream change)"); }
                    finally { Com.Release(ref type); }
                }
                continue;
            }
            if (hr == HResults.MfTransformNeedMoreInput) { Com.Release(ref ownSample); return; }
            Com.Check(hr, "IMFTransform.ProcessOutput");

            IntPtr sample = output.Sample;
            try
            {
                bool keyframe = Mf.GetUInt32(sample, MfGuids.SampleCleanPoint, out uint cleanPoint) >= 0 && cleanPoint != 0;
                IntPtr contiguous = Mf.ConvertToContiguousBuffer(sample);
                byte[] data;
                try
                {
                    byte* bytes = Mf.Lock(contiguous, out uint length);
                    try { data = new ReadOnlySpan<byte>(bytes, (int)length).ToArray(); }
                    finally { Mf.Unlock(contiguous); }
                }
                finally { Com.Release(ref contiguous); }

                if (keyframe && !H264.ContainsNal(data, H264.NalSps) && SequenceHeader() is { } header)
                    data = [.. header, .. data];
                long id = -1;
                double encodeMs = 0;
                if (_inFlight.TryDequeue(out var pending))
                {
                    id = pending.Id;
                    encodeMs = Stopwatch.GetElapsedTime(pending.Submitted).TotalMilliseconds;
                }
                _onOutput(new EncodedOutput(id, keyframe, data, encodeMs));
            }
            finally
            {
                if (sample != ownSample) Com.Release(ref sample);
                Com.Release(ref ownSample);
            }
            return;
        }
    }

    /// <summary>SPS and PPS from the output type, for encoders that only put them there.</summary>
    private byte[]? SequenceHeader()
    {
        if (Mft.GetOutputCurrentType(_transform, out var type) < 0) return null;
        try { return Mf.GetBlob(type, MfGuids.MpegSequenceHeader); }
        finally { Com.Release(ref type); }
    }

    private void SetCodec(in Guid api, Variant value)
    {
        if (_codecApi != IntPtr.Zero) Mf.SetCodecValue(_codecApi, api, value);
    }

    private void SetMediaType(bool isOutput, int width, int height, int fps, int bitrateKbps)
    {
        Com.Check(Exports.MFCreateMediaType(out var type), "MFCreateMediaType");
        try
        {
            Mf.SetGuid(type, MfGuids.MajorType, MfGuids.MediaTypeVideo);
            Mf.SetGuid(type, MfGuids.Subtype, isOutput ? MfGuids.VideoFormatH264 : MfGuids.VideoFormatNv12);
            Mf.SetUInt64(type, MfGuids.FrameSize, ((ulong)width << 32) | (uint)height);
            Mf.SetUInt64(type, MfGuids.FrameRate, ((ulong)fps << 32) | 1);
            Mf.SetUInt64(type, MfGuids.PixelAspectRatio, (1UL << 32) | 1);
            Mf.SetUInt32(type, MfGuids.InterlaceMode, Constants.InterlaceProgressive);
            if (isOutput)
            {
                Mf.SetUInt32(type, MfGuids.AvgBitrate, (uint)bitrateKbps * 1000);
                Mf.SetUInt32(type, MfGuids.Mpeg2Profile, Constants.H264ProfileHigh);
                // Written into the stream's VUI so browsers convert YUV to RGB correctly.
                Mf.SetUInt32(type, MfGuids.VideoPrimaries, Constants.PrimariesBt709);
                Mf.SetUInt32(type, MfGuids.TransferFunction, Constants.TransferBt709);
                Mf.SetUInt32(type, MfGuids.YuvMatrix, Constants.MatrixBt709);
                Mf.SetUInt32(type, MfGuids.VideoNominalRange, Constants.NominalRange16To235);
                Com.Check(Mft.SetOutputType(_transform, type), "IMFTransform.SetOutputType (H.264)");
            }
            else
            {
                Com.Check(Mft.SetInputType(_transform, type), "IMFTransform.SetInputType (NV12)");
            }
        }
        finally { Com.Release(ref type); }
    }

    /// <summary>True when Windows has at least one hardware H.264 encoder (any GPU).</summary>
    public static bool AnyAvailable()
    {
        if (Startup.Value < 0) return false;
        var input = new MftRegisterTypeInfo { MajorType = MfGuids.MediaTypeVideo, Subtype = MfGuids.VideoFormatNv12 };
        var output = new MftRegisterTypeInfo { MajorType = MfGuids.MediaTypeVideo, Subtype = MfGuids.VideoFormatH264 };
        if (Exports.MFTEnum2(MfGuids.CategoryVideoEncoder, Constants.MftEnumHardware | Constants.MftEnumSortAndFilter,
                &input, &output, IntPtr.Zero, out var array, out uint count) < 0) return false;
        var activates = (IntPtr*)array;
        for (int i = 0; i < count; i++) Marshal.Release(activates[i]);
        Exports.CoTaskMemFree(array);
        return count > 0;
    }

    /// <summary>Finds a hardware H.264 encoder on the given graphics adapter.</summary>
    private static IntPtr FindEncoder(Luid adapter, out string name)
    {
        var input = new MftRegisterTypeInfo { MajorType = MfGuids.MediaTypeVideo, Subtype = MfGuids.VideoFormatNv12 };
        var output = new MftRegisterTypeInfo { MajorType = MfGuids.MediaTypeVideo, Subtype = MfGuids.VideoFormatH264 };
        Com.Check(Exports.MFCreateAttributes(out var attributes, 1), "MFCreateAttributes");
        try
        {
            Mf.SetBlob(attributes, MfGuids.EnumAdapterLuid, MemoryMarshal.AsBytes(new ReadOnlySpan<Luid>(in adapter)));
            Com.Check(Exports.MFTEnum2(MfGuids.CategoryVideoEncoder, Constants.MftEnumHardware | Constants.MftEnumSortAndFilter,
                &input, &output, attributes, out var array, out uint count), "MFTEnum2");
            var activates = (IntPtr*)array;
            try
            {
                if (count == 0)
                    throw new VideoUnsupportedException("The graphics card driving this monitor has no hardware H.264 encoder available to Windows.");
                Marshal.AddRef(activates[0]);
                name = Mf.GetAllocatedString(activates[0], MfGuids.FriendlyName) ?? "Hardware H.264 encoder";
                return activates[0];
            }
            finally
            {
                for (int i = 0; i < count; i++) Marshal.Release(activates[i]);
                Exports.CoTaskMemFree(array);
            }
        }
        finally { Com.Release(ref attributes); }
    }

    public void Dispose()
    {
        if (_stopping) return;
        _stopping = true;
        _inputSlots.Release(); // wake a waiting capture thread
        if (_transform != IntPtr.Zero && Marshal.QueryInterface(_transform, in Iids.MfShutdown, out var shutdown) >= 0)
        {
            Mf.Shutdown(shutdown); // unblocks GetEvent with MF_E_SHUTDOWN
            Com.Release(ref shutdown);
        }
        if (_activate != IntPtr.Zero) Mf.ShutdownObject(_activate);
        _eventThread?.Join(TimeSpan.FromSeconds(2));
        ReleaseAll();
    }

    private void ReleaseAll()
    {
        Com.Release(ref _events);
        Com.Release(ref _codecApi);
        Com.Release(ref _transform);
        Com.Release(ref _manager);
        Com.Release(ref _activate);
    }
}

/// <summary>Small helpers for H.264 Annex B byte streams.</summary>
public static class H264
{
    public const int NalSps = 7, NalPps = 8, NalIdr = 5;

    /// <summary>Offsets of each NAL unit's header byte (the byte right after a start code).</summary>
    public static List<int> NalOffsets(ReadOnlySpan<byte> stream)
    {
        var offsets = new List<int>();
        for (int i = 0; i + 3 < stream.Length; i++)
        {
            if (stream[i] == 0 && stream[i + 1] == 0 && stream[i + 2] == 1)
            {
                offsets.Add(i + 3);
                i += 2;
            }
        }
        return offsets;
    }

    public static bool ContainsNal(ReadOnlySpan<byte> stream, int type)
    {
        foreach (int offset in NalOffsets(stream))
            if ((stream[offset] & 0x1F) == type) return true;
        return false;
    }

    /// <summary>WebCodecs codec string from the SPS, e.g. "avc1.640028" = High profile, level 4.0.</summary>
    public static string? CodecString(ReadOnlySpan<byte> stream)
    {
        foreach (int offset in NalOffsets(stream))
            if ((stream[offset] & 0x1F) == NalSps && offset + 3 < stream.Length)
                return $"avc1.{stream[offset + 1]:x2}{stream[offset + 2]:x2}{stream[offset + 3]:x2}";
        return null;
    }

    /// <summary>Each NAL unit as (start, length), without start codes or trailing zero bytes.</summary>
    public static List<(int Start, int Length)> NalUnits(ReadOnlySpan<byte> stream)
    {
        var offsets = NalOffsets(stream);
        var units = new List<(int, int)>(offsets.Count);
        for (int i = 0; i < offsets.Count; i++)
        {
            int end = i + 1 < offsets.Count ? offsets[i + 1] - 3 : stream.Length;
            while (end > offsets[i] && stream[end - 1] == 0) end--; // zeros belong to the next start code
            units.Add((offsets[i], end - offsets[i]));
        }
        return units;
    }

    /// <summary>Annex B (start codes) to AVCC (a 4-byte big-endian length before each NAL unit),
    /// the layout every WebCodecs implementation accepts together with an avcC description.</summary>
    public static byte[] ToAvcc(ReadOnlySpan<byte> annexB)
    {
        var units = NalUnits(annexB);
        var output = new byte[units.Sum(u => 4 + u.Length)];
        int position = 0;
        foreach (var (start, length) in units)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(output.AsSpan(position), length);
            annexB.Slice(start, length).CopyTo(output.AsSpan(position + 4));
            position += 4 + length;
        }
        return output;
    }

    /// <summary>The avcC decoder configuration record (ISO/IEC 14496-15) built from a keyframe's
    /// SPS and PPS; WebCodecs calls it the "description". Null if the stream has no SPS/PPS.</summary>
    public static byte[]? DecoderConfig(ReadOnlySpan<byte> annexB)
    {
        (int Start, int Length) sps = default, pps = default;
        foreach (var unit in NalUnits(annexB))
        {
            int type = annexB[unit.Start] & 0x1F;
            if (type == NalSps && sps.Length == 0) sps = unit;
            else if (type == NalPps && pps.Length == 0) pps = unit;
        }
        if (sps.Length < 4 || pps.Length == 0) return null;
        var spsBytes = annexB.Slice(sps.Start, sps.Length);
        var record = new List<byte>
        {
            1, spsBytes[1], spsBytes[2], spsBytes[3], // version, profile, compatibility, level
            0xFF,                                     // 4-byte NAL lengths
            0xE1, (byte)(sps.Length >> 8), (byte)sps.Length,
        };
        record.AddRange(spsBytes.ToArray());
        record.AddRange(new byte[] { 1, (byte)(pps.Length >> 8), (byte)pps.Length });
        record.AddRange(annexB.Slice(pps.Start, pps.Length).ToArray());
        if (spsBytes[1] is 100 or 110 or 122 or 144) // High profiles also state 4:2:0, 8-bit, no SPS extensions
            record.AddRange(new byte[] { 0xFD, 0xF8, 0xF8, 0 });
        return record.ToArray();
    }
}
