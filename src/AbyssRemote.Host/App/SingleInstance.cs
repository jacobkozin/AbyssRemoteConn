using System.Runtime.InteropServices;

namespace AbyssRemote.Host.App;

/// <summary>Lets only one Abyss Remote window run per signed-in user. Starting the program a
/// second time would otherwise fail on the port that is already taken; instead the second copy
/// asks the first one to show its window and exits.
///
/// Two named Windows objects do the work: whoever creates the mutex first is the first copy,
/// and the event is the "please show your window" signal between the two.</summary>
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _showRequest;
    private RegisteredWaitHandle? _listening;

    /// <param name="name">The real program always uses the default; tests pass a unique name
    /// so they can never collide with a running copy.</param>
    public SingleInstance(string name = "AbyssRemote")
    {
        // "Local\" = this Windows session only, so two signed-in users do not block each other.
        _mutex = new Mutex(initiallyOwned: false, $@"Local\{name}.Running", out bool createdNew);
        IsFirst = createdNew;
        _showRequest = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\{name}.ShowWindow");
    }

    /// <summary>True when no other copy was running when this object was created.</summary>
    public bool IsFirst { get; }

    /// <summary>First copy: calls <paramref name="showWindow"/> (on a thread-pool thread)
    /// every time a later copy asks for the window.</summary>
    public void Listen(Action showWindow) =>
        _listening = ThreadPool.RegisterWaitForSingleObject(_showRequest, (_, _) => showWindow(), null, Timeout.Infinite, executeOnlyOnce: false);

    /// <summary>Later copy: asks the first copy to bring its window to the front.</summary>
    public void AskFirstToShow()
    {
        // Windows only lets the program the user just started put a window in front. This
        // passes that right on, so the first copy's window does not merely flash in the taskbar.
        AllowSetForegroundWindow(AnyProcess);
        _showRequest.Set();
    }

    public void Dispose()
    {
        _listening?.Unregister(null);
        _showRequest.Dispose();
        _mutex.Dispose(); // closing the last handle removes the mutex, so the next start is "first" again
    }

    private const int AnyProcess = -1; // ASFW_ANY

    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(int processId);
}
