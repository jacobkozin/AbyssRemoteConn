using System.Diagnostics;
using System.Text;
using AbyssRemote.Host.Features;

namespace AbyssRemote.Tests;

/// <summary>A pretend Steam installation the tests can fill with games. It is a temporary folder
/// laid out like Steam's own and read by the real library code. Only the two things that would
/// touch the real PC are replaced: where Steam is installed, and starting a program, which is
/// written down here and never done.</summary>
public sealed class FakeSteam : ISteam, IDisposable
{
    public static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0xFF, 0xD9];
    public static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D];

    private readonly string _home;
    private readonly List<ProcessStartInfo> _started = new();
    private readonly WindowsSteam _steam;

    public FakeSteam()
    {
        // Only a name for now. The folder appears when a test first puts something into it, so
        // the many tests that never touch Steam leave nothing behind.
        _home = Path.Combine(Path.GetTempPath(), "abyss-steam-test-" + Guid.NewGuid().ToString("N"));
        _steam = new WindowsSteam(() => IsInstalled ? new SteamInstallation(Folder, Program) : null, Start);
    }

    /// <summary>The pretend Steam folder.</summary>
    public string Folder => Path.Combine(_home, "Steam");
    public string Program => Path.Combine(Folder, "steam.exe");
    public string CoverCache => Path.Combine(Folder, "appcache", "librarycache");

    /// <summary>Set to false to act out a PC without Steam.</summary>
    public bool IsInstalled { get; set; } = true;

    /// <summary>Set to act out a Steam that Windows cannot start.</summary>
    public Exception? StartProblem { get; set; }

    /// <summary>Every program start the code under test asked for, in order. None was carried out.</summary>
    public IReadOnlyList<ProcessStartInfo> Started { get { lock (_started) return _started.ToList(); } }

    public bool Installed => _steam.Installed;
    public IReadOnlyList<SteamGame> Games() => _steam.Games();
    public SteamArt? Art(uint appId) => _steam.Art(appId);
    public SteamGame? Launch(uint appId) => _steam.Launch(appId);
    public void OpenBigPicture() => _steam.OpenBigPicture();
    public void CloseBigPicture() => _steam.CloseBigPicture();

    /// <summary>A path next to the pretend Steam folder, for a second library. Not created.</summary>
    public string Beside(string name) => Path.Combine(_home, name);

    /// <summary>Writes libraryfolders.vdf: the Steam folder first, then these paths as given.</summary>
    public void ListLibraries(params string[] paths)
    {
        var text = new StringBuilder("\"libraryfolders\"\n{\n");
        string[] all = [Folder, .. paths];
        for (int i = 0; i < all.Length; i++)
        {
            text.Append($"\t\"{i}\"\n\t{{\n\t\t\"path\"\t\t\"{Escape(all[i])}\"\n\t\t\"label\"\t\t\"\"\n");
            text.Append("\t\t\"apps\"\n\t\t{\n\t\t\t\"1001\"\t\t\"5000\"\n\t\t}\n\t}\n");
        }
        File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(Folder, "steamapps")).FullName, "libraryfolders.vdf"), text.Append("}\n").ToString());
    }

    /// <summary>Installs a pretend game: writes its manifest into a library folder (the Steam
    /// folder unless another is given).</summary>
    public void AddGame(uint id, string name, string stateFlags = "4", long? lastPlayed = 0, long sizeOnDisk = 1_000_000, string? library = null) =>
        WriteManifest(id, Manifest(id, name, stateFlags, lastPlayed, sizeOnDisk), library);

    /// <summary>Writes any text as the manifest file of a game, for damaged and odd files.</summary>
    public string WriteManifest(uint fileId, string text, string? library = null)
    {
        string folder = Directory.CreateDirectory(Path.Combine(library ?? Folder, "steamapps")).FullName;
        string file = Path.Combine(folder, $"appmanifest_{fileId}.acf");
        File.WriteAllText(file, text);
        return file;
    }

    /// <summary>A manifest in the shape Steam writes: tabs, quoted text and a nested group.</summary>
    public static string Manifest(uint id, string name, string stateFlags = "4", long? lastPlayed = 0, long sizeOnDisk = 1_000_000) =>
        "\"AppState\"\n{\n" +
        $"\t\"appid\"\t\t\"{id}\"\n" +
        "\t\"universe\"\t\t\"1\"\n" +
        $"\t\"name\"\t\t\"{Escape(name)}\"\n" +
        $"\t\"StateFlags\"\t\t\"{stateFlags}\"\n" +
        $"\t\"installdir\"\t\t\"Game {id}\"\n" +
        (lastPlayed is null ? "" : $"\t\"LastPlayed\"\t\t\"{lastPlayed}\"\n") +
        $"\t\"SizeOnDisk\"\t\t\"{sizeOnDisk}\"\n" +
        $"\t\"InstalledDepots\"\n\t{{\n\t\t\"{id + 1}\"\n\t\t{{\n\t\t\t\"manifest\"\t\t\"1234567890\"\n\t\t\t\"size\"\t\t\"{sizeOnDisk}\"\n\t\t}}\n\t}}\n" +
        "}\n";

    /// <summary>Puts a file into the picture cache of a game, at a path below the game's own
    /// cache folder, and returns where it is.</summary>
    public string AddCover(uint id, string relativePath = "library_600x900.jpg", byte[]? data = null)
    {
        string file = Path.Combine(CoverCache, id.ToString(), relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllBytes(file, data ?? Jpeg);
        return file;
    }

    public void Dispose()
    {
        try { Directory.Delete(_home, recursive: true); }
        catch (IOException) { } // never created, or a file is still open
    }

    private void Start(ProcessStartInfo start)
    {
        lock (_started) _started.Add(start);
        if (StartProblem is not null) throw StartProblem;
    }

    private static string Escape(string text) => text.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
