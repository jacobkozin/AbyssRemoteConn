using System.Globalization;

namespace AbyssRemote.Host.Features;

/// <summary>One installed game, as the browser's game grid shows it.</summary>
public sealed record SteamGame(uint Id, string Name, bool HasArt, DateTimeOffset? LastPlayedUtc, long SizeBytes);

/// <summary>A game's cover picture, ready to send: its bytes and what kind of image they are.</summary>
public sealed record SteamArt(byte[] Data, string ContentType);

/// <summary>Reads the files of a Steam installation to answer two questions: which games are
/// installed, and what is a game's cover picture. It only ever reads, and it never starts
/// anything. Steam changes these files while this runs (downloads, updates), so a file or
/// folder that is missing, locked, half-written or not what it should be is skipped; it is
/// never an error.
/// <code>
/// steamapps\libraryfolders.vdf      every folder that holds games, other drives included
/// steamapps\appmanifest_620.acf     one per game in a library folder: name, state, size
/// appcache\librarycache\620\        the pictures Steam shows in its own library
/// </code></summary>
public sealed class SteamLibrary
{
    /// <summary>Game names are cut to this length. Real ones are far shorter.</summary>
    public const int MaxNameLength = 100;

    /// <summary>The largest cover that is sent. Real ones are around 100 KB.</summary>
    public const long MaxCoverBytes = 8 * 1024 * 1024;

    /// <summary>Real manifests are a few kilobytes. A file far beyond that is not one.</summary>
    private const long MaxManifestBytes = 1024 * 1024;

    /// <summary>The StateFlags bit Steam sets once every file of a game is in place.</summary>
    private const int FullyInstalled = 4;

    // The portrait cover (600 x 900), under both names Steam has given it.
    private static readonly string[] CoverNames = ["library_600x900.jpg", "library_capsule.jpg"];

    private static ReadOnlySpan<byte> JpegStart => [0xFF, 0xD8, 0xFF];
    private static ReadOnlySpan<byte> PngStart => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    // A manifest does not say what kind of thing it describes: Steam keeps that in a binary
    // cache this program does not read. Steam also installs things nobody plays, and those
    // are recognised by their number or their name:
    //  - Steamworks Common Redistributables (228980): the shared installers games need
    //  - SteamVR (250820) and its tools, whose names all start with "SteamVR"
    //  - Proton builds and the Steam Linux Runtime, which let Linux run Windows games
    //  - dedicated servers, named "<game> Dedicated Server"
    // Other software from the Steam store (a wallpaper or drawing program) is still listed:
    // nothing in the manifest tells it apart from a game, and Steam starts it the same way.
    private static readonly string[] ToolNameStarts =
    [
        "Steamworks Common Redistributables", "SteamVR", "Steam Linux Runtime",
        "Proton Experimental", "Proton Hotfix", "Proton EasyAntiCheat Runtime", "Proton BattlEye Runtime",
    ];

    private readonly string _root;

    public SteamLibrary(string steamFolder) => _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(steamFolder));

    /// <summary>A game's number, written with digits only. Signs, spaces, hex and anything
    /// else that is not 0 to 9 are refused, so text from a request can only ever become a number.</summary>
    public static bool TryParseId(string? text, out uint appId) =>
        uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out appId);

    /// <summary>Every folder that holds games: the Steam folder itself, then the others that
    /// libraryfolders.vdf lists (a second drive, for example).</summary>
    public IReadOnlyList<string> Folders()
    {
        var folders = new List<string> { _root };
        var listed = ReadFile(Path.Combine(_root, "steamapps", "libraryfolders.vdf"))?.Child("libraryfolders");
        foreach (var (_, entry) in listed?.Children ?? [])
        {
            string? path = entry.Text("path");
            if (path is null || !Path.IsPathFullyQualified(path)) continue;
            try { path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
            catch (Exception ex) when (Unreadable(ex)) { continue; }
            if (!folders.Contains(path, StringComparer.OrdinalIgnoreCase)) folders.Add(path);
        }
        return folders;
    }

    /// <summary>The fully installed games, most recently played first, then by name.</summary>
    public IReadOnlyList<SteamGame> Games()
    {
        var games = new Dictionary<uint, SteamGame>();
        foreach (string folder in Folders())
        {
            foreach (string file in ManifestFiles(folder))
            {
                // "appmanifest_620.acf": the number in the name is the game's id.
                string number = Path.GetFileNameWithoutExtension(file)["appmanifest_".Length..];
                if (TryParseId(number, out uint appId) && !games.ContainsKey(appId) && ReadManifest(file, appId) is { } game)
                    games.Add(appId, game);
            }
        }
        // A game that was never played has no date (null), which sorts as the oldest: those
        // come last, by name.
        return games.Values
            .OrderByDescending(game => game.LastPlayedUtc)
            .ThenBy(game => game.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(game => game.Id)
            .ToList();
    }

    /// <summary>One installed game, or null when that number is not in <see cref="Games"/>.</summary>
    public SteamGame? Game(uint appId)
    {
        foreach (string folder in Folders())
        {
            if (ReadManifest(Path.Combine(folder, "steamapps", $"appmanifest_{appId}.acf"), appId) is { } game)
                return game;
        }
        return null;
    }

    /// <summary>The cover picture of an installed game. Null when the game is not in
    /// <see cref="Games"/> or Steam has no usable picture for it.</summary>
    public SteamArt? Art(uint appId)
    {
        if (Game(appId) is null || CoverFile(appId) is not { } file) return null;
        try
        {
            byte[] data = File.ReadAllBytes(file.FullName);
            // Judged again on the bytes that are really sent, in case the file changed just now.
            return ImageType(data) is { } type ? new SteamArt(data, type) : null;
        }
        catch (Exception ex) when (Unreadable(ex)) { return null; }
    }

    internal static bool IsGame(uint appId, string name)
    {
        if (appId is 228980 or 250820) return false;
        if (ToolNameStarts.Any(start => name.StartsWith(start, StringComparison.OrdinalIgnoreCase))) return false;
        if (name.EndsWith(" Dedicated Server", StringComparison.OrdinalIgnoreCase)) return false;
        // Numbered Proton builds: "Proton 9.0", "Proton 10.0 (Beta)". A game whose name merely
        // starts with the word Proton is kept.
        return !(name.StartsWith("Proton ", StringComparison.OrdinalIgnoreCase) && name.Length > 7 && char.IsAsciiDigit(name[7]));
    }

    private static string[] ManifestFiles(string folder)
    {
        try { return Directory.GetFiles(Path.Combine(folder, "steamapps"), "appmanifest_*.acf"); }
        catch (Exception ex) when (Unreadable(ex)) { return []; } // for example a drive that is not plugged in
    }

    private SteamGame? ReadManifest(string file, uint appId)
    {
        var state = ReadFile(file)?.Child("AppState");
        if (state is null) return null;
        // The number in the file's name and the number inside must agree. That way the list
        // and a lookup by number always mean the same file.
        if (!TryParseId(state.Text("appid"), out uint inside) || inside != appId) return null;
        if (!int.TryParse(state.Text("StateFlags"), NumberStyles.None, CultureInfo.InvariantCulture, out int flags)
            || (flags & FullyInstalled) == 0) return null;
        string name = CleanName(state.Text("name") ?? "");
        if (name.Length == 0 || !IsGame(appId, name)) return null;

        long.TryParse(state.Text("SizeOnDisk"), NumberStyles.None, CultureInfo.InvariantCulture, out long size);
        return new SteamGame(appId, name, CoverFile(appId) is not null, LastPlayed(state.Text("LastPlayed")), size);
    }

    /// <summary>Steam stores the last play time in seconds since 1970, and 0 for "never".</summary>
    private static DateTimeOffset? LastPlayed(string? unixSeconds) =>
        long.TryParse(unixSeconds, NumberStyles.None, CultureInfo.InvariantCulture, out long seconds)
            && seconds > 0 && seconds <= DateTimeOffset.MaxValue.ToUnixTimeSeconds()
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : null;

    /// <summary>Game names are written by whoever published the game, and they end up in the
    /// browser and on the host console. Control characters (which could rewrite console
    /// output) are removed and very long names are cut, as for device names.</summary>
    private static string CleanName(string name)
    {
        string cleaned = new string(name.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (cleaned.Length <= MaxNameLength) return cleaned;
        // Never cut between the two halves of one character (an emoji, for example).
        return cleaned[..(char.IsHighSurrogate(cleaned[MaxNameLength - 1]) ? MaxNameLength - 1 : MaxNameLength)];
    }

    /// <summary>Finds a game's portrait cover in Steam's picture cache. Steam has kept it in
    /// three places over time, and all three are still in use side by side:
    /// <code>
    /// librarycache\620\library_600x900.jpg
    /// librarycache\620\(a long hash)\library_600x900.jpg
    /// librarycache\620\(a long hash)\library_capsule.jpg
    /// </code>
    /// The path is made of the cache folder, the game's number and names found by listing that
    /// folder. Nothing else from a request is ever part of it, so a request cannot point this
    /// at any other file. When a game has several covers, the newest file wins.</summary>
    private FileInfo? CoverFile(uint appId)
    {
        var folder = new DirectoryInfo(Path.Combine(_root, "appcache", "librarycache", appId.ToString(CultureInfo.InvariantCulture)));
        var files = new List<FileInfo>();
        try
        {
            files.AddRange(folder.EnumerateFiles());
            foreach (var hashed in folder.EnumerateDirectories()) files.AddRange(hashed.EnumerateFiles());
        }
        catch (Exception ex) when (Unreadable(ex))
        {
            // No folder: Steam has no pictures for this game. Whatever was listed is still used.
        }
        return files
            .Where(file => CoverNames.Contains(file.Name, StringComparer.OrdinalIgnoreCase) && file.Length <= MaxCoverBytes)
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ThenBy(file => file.FullName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(file => ImageType(ReadStart(file)) is not null);
    }

    /// <summary>Steam gives every cover the ending ".jpg", but a good number of them are really
    /// PNG files. So the kind of picture is read from the first bytes of the file itself, and a
    /// file that is neither kind is not a cover.</summary>
    private static string? ImageType(ReadOnlySpan<byte> start) =>
        start.StartsWith(JpegStart) ? "image/jpeg" : start.StartsWith(PngStart) ? "image/png" : null;

    private static byte[] ReadStart(FileInfo file)
    {
        try
        {
            using var stream = file.OpenRead();
            var start = new byte[PngStart.Length];
            int read = stream.ReadAtLeast(start, start.Length, throwOnEndOfStream: false);
            return start[..read];
        }
        catch (Exception ex) when (Unreadable(ex)) { return []; }
    }

    private static KeyValues? ReadFile(string path)
    {
        try { return new FileInfo(path).Length <= MaxManifestBytes ? KeyValues.Parse(File.ReadAllText(path)) : null; }
        catch (Exception ex) when (Unreadable(ex)) { return null; }
    }

    /// <summary>What reading a file or folder throws when it is missing, locked, protected or
    /// has a name Windows cannot use.</summary>
    private static bool Unreadable(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException;
}
