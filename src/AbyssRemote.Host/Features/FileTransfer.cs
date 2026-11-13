namespace AbyssRemote.Host.Features;

/// <summary>The one folder on the PC that paired devices can send files to and fetch files from.</summary>
public sealed class TransferFolder(string directory)
{
    public string Directory { get; } = directory;

    /// <summary>The production folder: "Abyss Remote" inside the user's Downloads folder.</summary>
    public static TransferFolder InDownloads() =>
        new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Abyss Remote"));
}

public static class FileTransferEndpoints
{
    public static void Map(WebApplication app, ServerContext c)
    {
    }
}
