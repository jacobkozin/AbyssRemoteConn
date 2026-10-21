using System.Security.Cryptography;
using System.Text.Json;

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

/// <summary>Paired devices and certificate thumbprints in %LOCALAPPDATA%\AbyssRemote\settings.json.
/// Session secrets are never stored, only their SHA-256 hashes.</summary>
public sealed class StateStore
{
    public static readonly TimeSpan DeviceLifetime = TimeSpan.FromDays(30);

    private readonly object _sync = new();
    private readonly string _filename;
    private readonly TimeProvider _clock;
    private readonly HostSettings _settings;
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public StateStore(string directory, TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
        Directory.CreateDirectory(directory);
        _filename = Path.Combine(directory, "settings.json");
        try
        {
            _settings = File.Exists(_filename)
                ? JsonSerializer.Deserialize<HostSettings>(File.ReadAllText(_filename), JsonOpts) ?? new HostSettings()
                : new HostSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"Cannot read {_filename}. Move that file somewhere safe and restart (paired devices will need to pair again). The file was NOT changed.", ex);
        }
    }

    public string? CertificateThumbprint { get { lock (_sync) return _settings.CertificateThumbprint; } }
    public string? RootThumbprint { get { lock (_sync) return _settings.RootThumbprint; } }

    public void SetCertificateThumbprints(string leaf, string root)
    {
        lock (_sync)
        {
            if (_settings.CertificateThumbprint == leaf && _settings.RootThumbprint == root) return;
            _settings.CertificateThumbprint = leaf;
            _settings.RootThumbprint = root;
            Save();
        }
    }

    /// <summary>Device names are shown in the browser and printed on the host console, so
    /// control characters (which could rewrite console output) are removed.</summary>
    public static string CleanName(string name)
    {
        var cleaned = new string(name.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return cleaned.Length > 60 ? cleaned[..60] : cleaned;
    }

    public (string Id, string Secret) Pair(string name)
    {
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var id = Guid.NewGuid().ToString("N");
        var now = _clock.GetUtcNow();
        lock (_sync)
        {
            _settings.Devices.Add(new DeviceRecord { Id = id, Name = CleanName(name), TokenHash = Hash(secret), CreatedUtc = now, LastSeenUtc = now });
            Save();
        }
        return (id, secret);
    }

    /// <summary>Checks a cookie of the form "{deviceId}.{secret}" in constant time.</summary>
    public DeviceRecord? Authenticate(string? cookie)
    {
        if (string.IsNullOrWhiteSpace(cookie)) return null;
        var parts = cookie.Split('.', 2);
        if (parts.Length != 2 || parts[0].Length != 32 || parts[1].Length != 64) return null;
        lock (_sync)
        {
            var device = _settings.Devices.FirstOrDefault(d => d.Id == parts[0]);
            if (device is null || Expired(device)) return null;
            byte[] expected;
            try { expected = Convert.FromHexString(device.TokenHash); }
            catch (FormatException) { return null; }
            return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(Hash(parts[1])), expected) ? device : null;
        }
    }

    /// <summary>Live connections call this before acting, so revocation and expiry apply mid-session.</summary>
    public bool IsActive(string id)
    {
        lock (_sync) return _settings.Devices.Any(d => d.Id == id && !Expired(d));
    }

    public IReadOnlyList<object> Devices()
    {
        lock (_sync)
            return _settings.Devices.Where(d => !Expired(d))
                .Select(d => (object)new { id = d.Id, name = d.Name, createdUtc = d.CreatedUtc, lastSeenUtc = d.LastSeenUtc, expiresUtc = d.CreatedUtc + DeviceLifetime })
                .ToList();
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

    /// <summary>Drops devices whose 30-day pairing has expired. Returns how many were removed.</summary>
    public int PruneExpired()
    {
        lock (_sync)
        {
            int removed = _settings.Devices.RemoveAll(Expired);
            if (removed > 0) Save();
            return removed;
        }
    }

    public void UpdateSeen(string id)
    {
        lock (_sync)
        {
            var device = _settings.Devices.FirstOrDefault(d => d.Id == id);
            var now = _clock.GetUtcNow();
            if (device is null || now - device.LastSeenUtc < TimeSpan.FromMinutes(5)) return;
            device.LastSeenUtc = now;
            Save();
        }
    }

    private bool Expired(DeviceRecord device) => device.CreatedUtc + DeviceLifetime <= _clock.GetUtcNow();

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
