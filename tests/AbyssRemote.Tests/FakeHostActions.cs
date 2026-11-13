using AbyssRemote.Host.Features;

namespace AbyssRemote.Tests;

/// <summary>Records what the server asked the PC to do instead of doing it.</summary>
public sealed class FakeHostActions : IHostActions
{
    private readonly List<string> _ran = new();

    /// <summary>Every action that reached the PC, in order.</summary>
    public IReadOnlyList<string> Ran { get { lock (_ran) return _ran.ToList(); } }

    /// <summary>Set this to make every action fail the way Windows does when it refuses.</summary>
    public string? Refusal { get; set; }

    /// <summary>Like the real call, Sleep() returns only when the PC is awake. Reset this to
    /// keep the pretend PC asleep, and set it to wake the PC up.</summary>
    public ManualResetEventSlim Awake { get; } = new(initialState: true);

    public void Lock() => Run("lock");

    public void Sleep()
    {
        Run("sleep");
        Awake.Wait();
    }

    private void Run(string action)
    {
        if (Refusal is not null) throw new HostActionException(Refusal);
        lock (_ran) _ran.Add(action);
    }
}
