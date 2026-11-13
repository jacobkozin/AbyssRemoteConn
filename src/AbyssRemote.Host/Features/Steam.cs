namespace AbyssRemote.Host.Features;

/// <summary>The Steam games installed on this PC, and a way to start one.</summary>
public interface ISteam
{
}

public sealed class WindowsSteam : ISteam
{
}

public static class SteamEndpoints
{
    public static void Map(WebApplication app, ServerContext c)
    {
    }
}
