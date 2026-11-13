namespace AbyssRemote.Host.Features;

/// <summary>A short, fixed list of things a paired device may ask the PC to do (for example
/// lock the screen). Never a way to run arbitrary commands.</summary>
public interface IHostActions
{
}

public sealed class WindowsHostActions : IHostActions
{
}

public static class HostActionEndpoints
{
    public static void Map(WebApplication app, ServerContext c)
    {
    }
}
