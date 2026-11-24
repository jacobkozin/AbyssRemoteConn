using System.Text.Json;

namespace AbyssRemote.Host.App;

/// <summary>The settings a person changes in the window. Kept apart from settings.json, which
/// holds the paired devices and belongs to <see cref="StateStore"/>.</summary>
public sealed record Preferences
{
    /// <summary>"Allow devices on my home network". Off: only this PC can connect.</summary>
    public bool Lan { get; init; }

    public int Port { get; init; } = LaunchOptions.DefaultPort;

    /// <summary>"Keep running in the tray when I close the window".</summary>
    public bool CloseToTray { get; init; } = true;

    /// <summary>The one-time explanation of home-network mode has been shown and accepted.</summary>
    public bool LanExplained { get; init; }

    /// <summary>The "still running in the tray" tip has been shown once.</summary>
    public bool TrayTipShown { get; init; }

    /// <summary>"Let added devices connect away from home" (1.2). Only works with a usable
    /// <see cref="AwayHost"/>; see <see cref="AwayAccess"/>.</summary>
    public bool Away { get; init; }

    /// <summary>The public address the Gaming Hub's proxy forwards to this PC, for example
    /// "remote.example.duckdns.org". Kept when away access is turned off.</summary>
    public string AwayHost { get; init; } = "";

    /// <summary>The one-time warning about away access has been shown and accepted.</summary>
    public bool AwayExplained { get; init; }

    public static bool IsValidPort(int port) => port is >= 1024 and <= 65535;
}

/// <summary>Reads and writes %LOCALAPPDATA%\AbyssRemote\preferences.json.</summary>
public sealed class PreferencesFile
{
    public const string FileName = "preferences.json";
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true,
    };

    private readonly string _filename;

    public PreferencesFile(string directory) => _filename = Path.Combine(directory, FileName);

    /// <summary>A missing file means the defaults. So does a damaged one, but then
    /// <paramref name="notice"/> says so; the file is left alone until a setting is changed.</summary>
    public Preferences Load(out string? notice)
    {
        notice = null;
        if (!File.Exists(_filename)) return new Preferences();
        try
        {
            var loaded = JsonSerializer.Deserialize<Preferences>(File.ReadAllText(_filename), JsonOpts) ?? new Preferences();
            if (Preferences.IsValidPort(loaded.Port)) return loaded;
            notice = $"The saved port {loaded.Port} is not between 1024 and 65535, so port {LaunchOptions.DefaultPort} is used.";
            return loaded with { Port = LaunchOptions.DefaultPort };
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            notice = $"The settings file ({FileName}) could not be read, so the default settings are used.";
            return new Preferences();
        }
    }

    /// <summary>Writes a temporary file and renames it over the old one, so a crash or power
    /// cut in the middle never leaves half a file.</summary>
    public void Save(Preferences preferences)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filename)!);
        string temp = _filename + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(preferences, JsonOpts));
            File.Move(temp, _filename, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }
}
