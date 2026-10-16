using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AbyssRemote.Host;

public sealed class DeviceRecord
{
    public required string Id { get; init; }
    public required string Name { get; set; }
    public required string TokenHash { get; init; }
    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastSeenUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class HostSettings
{
    public string? CertificateThumbprint { get; set; }
    public string? RootThumbprint { get; set; }
    public List<DeviceRecord> Devices { get; set; } = new();
}

public sealed class StateStore
{
    private readonly object _sync = new();
    private readonly string _filename;
    private HostSettings _settings;
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public StateStore(string path)
    {
        Directory.CreateDirectory(path);
        _filename = Path.Combine(path, "settings.json");
        try
        {
            _settings = File.Exists(_filename)
                ? JsonSerializer.Deserialize<HostSettings>(File.ReadAllText(_filename), JsonOpts) ?? new HostSettings()
                : new HostSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException("Cannot read settings.json safely. Back it up and repair it before restarting; settings were NOT reset.", ex);
        }
    }

    public string? CertificateThumbprint { get { lock (_sync) return _settings.CertificateThumbprint; } }
    public string? RootThumbprint { get { lock (_sync) return _settings.RootThumbprint; } }

    public void SetCertificateThumbprints(string leaf, string root)
    {
        lock (_sync) { _settings.CertificateThumbprint = leaf; _settings.RootThumbprint = root; Save(); }
    }

    public (string id, string secret) Pair(string name)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var id = Guid.NewGuid().ToString("N");
        lock (_sync)
        {
            _settings.Devices.Add(new DeviceRecord { Id = id, Name = name[..Math.Min(60, name.Length)], TokenHash = Hash(token) });
            Save();
        }
        return (id, token);
    }

    public DeviceRecord? Authenticate(string? combined)
    {
        if (string.IsNullOrWhiteSpace(combined)) return null;
        var split = combined.Split('.', 2);
        if (split.Length != 2 || split[0].Length != 32 || split[1].Length != 64) return null;
        byte[] supplied;
        try { supplied = Convert.FromHexString(Hash(split[1])); }
        catch (FormatException) { return null; }
        lock (_sync)
        {
            var device = _settings.Devices.FirstOrDefault(d => d.Id == split[0]);
            if (device is null || device.CreatedUtc < DateTimeOffset.UtcNow.AddDays(-30)) return null;
            byte[] expected;
            try { expected = Convert.FromHexString(device.TokenHash); }
            catch (FormatException) { return null; }
            return CryptographicOperations.FixedTimeEquals(supplied, expected) ? device : null;
        }
    }

    public bool StillPaired(string id)
    {
        lock (_sync)
            return _settings.Devices.Any(d => d.Id == id && d.CreatedUtc >= DateTimeOffset.UtcNow.AddDays(-30));
    }

    public IReadOnlyList<object> Devices()
    {
        lock (_sync)
            return _settings.Devices.Select(d => (object)new { id = d.Id, name = d.Name, createdUtc = d.CreatedUtc, lastSeenUtc = d.LastSeenUtc }).ToList();
    }

    public int RevokeAll()
    {
        lock (_sync)
        {
            int count = _settings.Devices.Count;
            _settings.Devices.Clear();
            Save();
            return count;
        }
    }

    public bool Revoke(string id)
    {
        lock (_sync)
        {
            int removed = _settings.Devices.RemoveAll(d => d.Id == id);
            if (removed > 0) Save();
            return removed > 0;
        }
    }

    public void UpdateSeen(string id)
    {
        lock (_sync)
        {
            var d = _settings.Devices.FirstOrDefault(d => d.Id == id);
            if (d is null || DateTimeOffset.UtcNow - d.LastSeenUtc < TimeSpan.FromMinutes(5)) return;
            d.LastSeenUtc = DateTimeOffset.UtcNow;
            Save();
        }
    }

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(token)));

    private void Save()
    {
        string temp = _filename + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(_settings, JsonOpts));
            File.Move(temp, _filename, true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }
}
