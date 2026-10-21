using System.Runtime.InteropServices;
using System.Threading.Channels;

namespace AbyssRemote.Host.GameMode;

/// <summary>Captures what the PC is playing with WASAPI loopback (the same mix the speakers get)
/// and turns it into 10 ms chunks of 48 kHz stereo 16-bit PCM. Used by both stream modes.
/// <para>Silence is sent as zero chunks: Windows delivers no loopback data while nothing plays,
/// and a steady stream lets the browser tell a quiet PC from a slow network.</para></summary>
internal sealed unsafe class SystemAudioCapture : IAudioSource
{
    // Bounded: if the network falls behind, the oldest audio is dropped rather than queued.
    private readonly Channel<byte[]> _chunks = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(30)
    {
        FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true,
    });
    private readonly Thread _thread;
    private readonly int _sourceRate, _sourceChannels;
    private readonly bool _sourceIsFloat;
    private IntPtr _enumerator, _device, _client, _capture;
    private volatile bool _stopping;

    public SystemAudioCapture()
    {
        try
        {
            // Make sure this thread is in the COM multithreaded apartment (a no-op if it already is).
            // Not undone: the runtime keeps pool threads in the MTA anyway.
            Wasapi.CoInitializeEx(IntPtr.Zero, 0 /* COINIT_MULTITHREADED */);
            Com.Check(Wasapi.CoCreateInstance(Wasapi.ClsidDeviceEnumerator, IntPtr.Zero, 0x1 /* CLSCTX_INPROC_SERVER */,
                Wasapi.IidDeviceEnumerator, out _enumerator), "CoCreateInstance(MMDeviceEnumerator)");
            int hr = Wasapi.GetDefaultAudioEndpoint(_enumerator, out _device);
            if (hr == unchecked((int)0x80070490)) throw new AudioUnavailableException("This PC has no audio output device.");
            Com.Check(hr, "IMMDeviceEnumerator.GetDefaultAudioEndpoint");
            _client = Wasapi.Activate(_device, Wasapi.IidAudioClient);

            var format = Wasapi.GetMixFormat(_client);
            try
            {
                _sourceChannels = format->Channels;
                _sourceRate = (int)format->SamplesPerSec;
                _sourceIsFloat = format->FormatTag == 3 /* WAVE_FORMAT_IEEE_FLOAT */ ||
                    (format->FormatTag == 0xFFFE /* WAVE_FORMAT_EXTENSIBLE */ && *(Guid*)((byte*)format + 24) == Wasapi.SubtypeFloat);
                if (!_sourceIsFloat && format->BitsPerSample != 16)
                    throw new AudioUnavailableException($"Unsupported audio format ({format->BitsPerSample}-bit integer).");
                // Shared-mode loopback with a 50 ms buffer; this thread drains it every few milliseconds.
                Com.Check(Wasapi.Initialize(_client, 0x00020000 /* AUDCLNT_STREAMFLAGS_LOOPBACK */, 500_000, format), "IAudioClient.Initialize");
            }
            finally { Exports.CoTaskMemFree((IntPtr)format); }
            _capture = Wasapi.GetService(_client, Wasapi.IidAudioCaptureClient);
            Com.Check(Wasapi.Start(_client), "IAudioClient.Start");
        }
        catch
        {
            Release();
            throw;
        }
        _thread = new Thread(CaptureLoop) { IsBackground = true, Name = "Abyss Remote audio capture" };
        _thread.Start();
    }

    public ChannelReader<byte[]> Chunks => _chunks.Reader;

    private void CaptureLoop()
    {
        using var timer = TimerResolution.OneMillisecond();
        var stereo = new List<float>(4096);        // interleaved L/R at 48 kHz, waiting to be chunked
        double sourcePosition = 0;                  // resampler phase, in source frames
        float lastLeft = 0, lastRight = 0;
        double step = _sourceRate / (double)IAudioSource.SampleRate;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        long chunksSent = 0;
        try
        {
            while (!_stopping)
            {
                Thread.Sleep(4);
                bool gotAudio = false;
                while (Wasapi.GetNextPacketSize(_capture) > 0)
                {
                    Com.Check(Wasapi.GetBuffer(_capture, out byte* data, out uint frames, out uint flags), "IAudioCaptureClient.GetBuffer");
                    bool silent = (flags & 0x2) != 0; // AUDCLNT_BUFFERFLAGS_SILENT
                    // Linear resampling to 48 kHz (a no-op step of 1.0 when the PC already runs at 48 kHz).
                    for (int frame = 0; frame < frames; frame++)
                    {
                        var (left, right) = silent ? (0f, 0f) : Downmix(data, frame);
                        while (sourcePosition < 1)
                        {
                            stereo.Add(lastLeft + (left - lastLeft) * (float)sourcePosition);
                            stereo.Add(lastRight + (right - lastRight) * (float)sourcePosition);
                            sourcePosition += step;
                        }
                        sourcePosition -= 1;
                        lastLeft = left;
                        lastRight = right;
                    }
                    Wasapi.ReleaseBuffer(_capture, frames);
                    gotAudio = true;
                }

                // Real-time pacing: never get ahead of the wall clock, and fill gaps (nothing playing) with silence.
                long due = (long)(clock.Elapsed.TotalMilliseconds / 10);
                while (stereo.Count >= IAudioSource.ChunkFrames * 2)
                {
                    Emit(stereo);
                    chunksSent++;
                }
                if (!gotAudio && chunksSent < due - 3)
                {
                    while (chunksSent < due) { _chunks.Writer.TryWrite(new byte[IAudioSource.ChunkBytes]); chunksSent++; }
                }
                else if (chunksSent < due - 50) chunksSent = due - 3; // device clock drift: resynchronize
            }
        }
        catch (NativeCallException)
        {
            // The audio device went away (unplugged, default device changed). The browser reconnects.
        }
        finally { _chunks.Writer.TryComplete(); }
    }

    private void Emit(List<float> stereo)
    {
        var chunk = new byte[IAudioSource.ChunkBytes];
        var samples = MemoryMarshal.Cast<byte, short>(chunk.AsSpan());
        for (int i = 0; i < samples.Length; i++)
            samples[i] = (short)Math.Clamp(stereo[i] * 32767f, short.MinValue, short.MaxValue);
        stereo.RemoveRange(0, samples.Length);
        _chunks.Writer.TryWrite(chunk);
    }

    /// <summary>One frame as stereo. Surround mixes fold the center and rear channels into left/right
    /// (standard order: FL, FR, FC, LFE, BL, BR, SL, SR).</summary>
    private (float Left, float Right) Downmix(byte* data, int frame)
    {
        float Sample(int channel) => _sourceIsFloat
            ? ((float*)data)[frame * _sourceChannels + channel]
            : ((short*)data)[frame * _sourceChannels + channel] / 32768f;
        if (_sourceChannels == 1) { float mono = Sample(0); return (mono, mono); }
        float left = Sample(0), right = Sample(1);
        if (_sourceChannels >= 6)
        {
            float center = Sample(2) * 0.707f;
            left += center + Sample(4) * 0.5f + (_sourceChannels >= 8 ? Sample(6) * 0.5f : 0);
            right += center + Sample(5) * 0.5f + (_sourceChannels >= 8 ? Sample(7) * 0.5f : 0);
        }
        return (Math.Clamp(left, -1f, 1f), Math.Clamp(right, -1f, 1f));
    }

    public void Dispose()
    {
        if (_stopping) return;
        _stopping = true;
        _thread.Join(TimeSpan.FromSeconds(2));
        if (_client != IntPtr.Zero) Wasapi.Stop(_client);
        Release();
    }

    private void Release()
    {
        Com.Release(ref _capture);
        Com.Release(ref _client);
        Com.Release(ref _device);
        Com.Release(ref _enumerator);
    }
}

[StructLayout(LayoutKind.Sequential, Pack = 2)]
internal struct WaveFormatEx
{
    public ushort FormatTag, Channels;
    public uint SamplesPerSec, AvgBytesPerSec;
    public ushort BlockAlign, BitsPerSample, ExtraSize;
}

/// <summary>WASAPI interop. Slots and GUIDs from mmdeviceapi.h / audioclient.h (Windows SDK 10.0.26100).</summary>
internal static unsafe class Wasapi
{
    public static readonly Guid ClsidDeviceEnumerator = new("bcde0395-e52f-467c-8e3d-c4579291692e");
    public static readonly Guid IidDeviceEnumerator = new("a95664d2-9614-4f35-a746-de8db63617e6");
    public static readonly Guid IidAudioClient = new("1cb9ad4c-dbfa-4c32-b178-c2f568a703b2");
    public static readonly Guid IidAudioCaptureClient = new("c8adbd64-e71e-48a0-a4de-185c395cd317");
    public static readonly Guid SubtypeFloat = new("00000003-0000-0010-8000-00aa00389b71"); // KSDATAFORMAT_SUBTYPE_IEEE_FLOAT

    [DllImport("ole32.dll")] public static extern int CoInitializeEx(IntPtr reserved, uint flags);
    [DllImport("ole32.dll")] public static extern int CoCreateInstance(in Guid clsid, IntPtr outer, uint context, in Guid iid, out IntPtr result);

    public static int GetDefaultAudioEndpoint(IntPtr enumerator, out IntPtr device) // IMMDeviceEnumerator slot 4 (eRender, eConsole)
    {
        IntPtr result;
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, int, int, IntPtr*, int>)Com.Slot(enumerator, 4))(enumerator, 0, 0, &result);
        device = result;
        return hr;
    }

    public static IntPtr Activate(IntPtr device, in Guid iid)                         // IMMDevice slot 3
    {
        IntPtr result;
        fixed (Guid* i = &iid)
            Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, uint, IntPtr, IntPtr*, int>)Com.Slot(device, 3))(device, i, 0x17 /* CLSCTX_ALL */, IntPtr.Zero, &result), "IMMDevice.Activate");
        return result;
    }

    public static int Initialize(IntPtr client, uint flags, long bufferDuration, WaveFormatEx* format) => // IAudioClient slot 3
        ((delegate* unmanaged[Stdcall]<IntPtr, int, uint, long, long, WaveFormatEx*, IntPtr, int>)Com.Slot(client, 3))(client, 0 /* shared */, flags, bufferDuration, 0, format, IntPtr.Zero);

    public static WaveFormatEx* GetMixFormat(IntPtr client)                           // IAudioClient slot 8
    {
        WaveFormatEx* format;
        Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, WaveFormatEx**, int>)Com.Slot(client, 8))(client, &format), "IAudioClient.GetMixFormat");
        return format;
    }

    public static int Start(IntPtr client) => ((delegate* unmanaged[Stdcall]<IntPtr, int>)Com.Slot(client, 10))(client); // slot 10
    public static int Stop(IntPtr client) => ((delegate* unmanaged[Stdcall]<IntPtr, int>)Com.Slot(client, 11))(client);   // slot 11

    public static IntPtr GetService(IntPtr client, in Guid iid)                       // IAudioClient slot 14
    {
        IntPtr result;
        fixed (Guid* i = &iid)
            Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)Com.Slot(client, 14))(client, i, &result), "IAudioClient.GetService");
        return result;
    }

    public static int GetBuffer(IntPtr capture, out byte* data, out uint frames, out uint flags) // IAudioCaptureClient slot 3
    {
        byte* d;
        uint f, fl;
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, byte**, uint*, uint*, ulong*, ulong*, int>)Com.Slot(capture, 3))(capture, &d, &f, &fl, null, null);
        data = d;
        frames = f;
        flags = fl;
        return hr;
    }

    public static void ReleaseBuffer(IntPtr capture, uint frames) =>                  // slot 4
        Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, int>)Com.Slot(capture, 4))(capture, frames), "IAudioCaptureClient.ReleaseBuffer");

    public static uint GetNextPacketSize(IntPtr capture)                              // slot 5
    {
        uint frames;
        Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)Com.Slot(capture, 5))(capture, &frames), "IAudioCaptureClient.GetNextPacketSize");
        return frames;
    }
}
