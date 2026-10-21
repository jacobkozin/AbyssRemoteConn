using System.Runtime.InteropServices;

namespace AbyssRemote.Host;

/// <summary>Windows normally wakes sleeping threads in 15.6 ms steps, so a 16.7 ms (60 fps)
/// frame interval turns into 31 ms (32 fps). While a stream is running this requests 1 ms
/// timer resolution; disposing it gives the setting back (the calls are reference counted).</summary>
public sealed class TimerResolution : IDisposable
{
    [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint milliseconds);
    [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint milliseconds);

    private bool _active;

    private TimerResolution() => _active = timeBeginPeriod(1) == 0;

    public static TimerResolution OneMillisecond() => new();

    public void Dispose()
    {
        if (!_active) return;
        _active = false;
        timeEndPeriod(1);
    }
}
