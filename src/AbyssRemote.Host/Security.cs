using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Http;

namespace AbyssRemote.Host;

public enum PairingResult { Paired, WrongOrExpired, TooManyAttempts, Locked }

/// <summary>The one-time pairing code shown on the host console. Guessing is limited twice:
/// per client address, and per code (after too many wrong guesses in total the code is
/// destroyed, so even many addresses together get at most 20 guesses out of 100 million).</summary>
public sealed class PairingManager
{
    public const int AttemptsPerAddress = 5;
    public const int MaxFailuresPerCode = 20;
    public static readonly TimeSpan AttemptWindow = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(10);

    private readonly object _sync = new();
    private readonly TimeProvider _clock;
    private readonly Dictionary<string, (int Count, DateTimeOffset WindowEnds)> _attempts = new();
    private string _pin = "";
    private DateTimeOffset _expires;
    private int _failures;

    public PairingManager(TimeProvider? clock = null) => _clock = clock ?? TimeProvider.System;

    /// <summary>Raised once when wrong guesses destroy the current code.</summary>
    public event Action? CodeLocked;

    public string Rotate()
    {
        lock (_sync)
        {
            _pin = RandomNumberGenerator.GetInt32(0, 100_000_000).ToString("D8");
            _expires = _clock.GetUtcNow() + CodeLifetime;
            _failures = 0;
            _attempts.Clear();
            return _pin;
        }
    }

    public PairingResult TryUse(string code, string address)
    {
        PairingResult result;
        bool justLocked = false;
        lock (_sync)
        {
            var now = _clock.GetUtcNow();
            if (_attempts.Count > 1000)
                foreach (var stale in _attempts.Where(a => a.Value.WindowEnds <= now).Select(a => a.Key).ToList())
                    _attempts.Remove(stale);
            if (_attempts.TryGetValue(address, out var entry) && entry.WindowEnds <= now) entry = default;
            if (entry.Count >= AttemptsPerAddress) return PairingResult.TooManyAttempts;
            _attempts[address] = (entry.Count + 1, entry.Count == 0 ? now + AttemptWindow : entry.WindowEnds);

            if (_pin.Length == 0)
                return _failures >= MaxFailuresPerCode ? PairingResult.Locked : PairingResult.WrongOrExpired;
            bool valid = now < _expires && code.Length == 8 && code.All(char.IsAsciiDigit)
                && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(code), Encoding.ASCII.GetBytes(_pin));
            if (valid)
            {
                _pin = "";
                result = PairingResult.Paired;
            }
            else if (++_failures >= MaxFailuresPerCode)
            {
                _pin = "";
                justLocked = true;
                result = PairingResult.Locked;
            }
            else result = PairingResult.WrongOrExpired;
        }
        if (justLocked) CodeLocked?.Invoke();
        return result;
    }
}

/// <summary>Defense in depth for LAN mode: only loopback, private (RFC 1918), link-local and
/// IPv6 unique-local peers may even start a TLS handshake. If someone port-forwards the host
/// by mistake, internet addresses are still refused.</summary>
public static class NetworkGuard
{
    public static bool IsLocalNetwork(IPAddress? address)
    {
        if (address is null) return false;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return true;
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return b[0] == 10
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254);
        }
        return address.AddressFamily == AddressFamily.InterNetworkV6
            && (address.IsIPv6LinkLocal || address.IsIPv6UniqueLocal);
    }

    /// <summary>Kestrel connection middleware; runs before TLS, so refused peers learn nothing.</summary>
    public static Task FilterAsync(ConnectionContext connection, ConnectionDelegate next, Action<string> notice)
    {
        var remote = (connection.RemoteEndPoint as IPEndPoint)?.Address;
        if (IsLocalNetwork(remote)) return next(connection);
        notice($"Refused a connection from {remote}: only devices on your local network may connect.");
        connection.Abort();
        return Task.CompletedTask;
    }
}

public static class HostCertificates
{
    private const string RootSubject = "CN=Abyss Remote Personal Root CA";
    private const string LeafSubject = "CN=Abyss Remote Windows Host";

    /// <summary>IPv4 addresses of this PC's active network adapters (loopback excluded).</summary>
    public static IReadOnlyList<IPAddress> CurrentAddresses() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up)
        .SelectMany(n => n.GetIPProperties().UnicastAddresses)
        .Select(a => a.Address)
        .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
        .Distinct()
        .ToList();

    /// <summary>Loads the private root CA and server certificate from the Windows user store,
    /// creating them on first run. The server certificate is reissued (by the same root, so
    /// phones keep trusting it) when it nears expiry or, in LAN mode, when it does not cover
    /// this PC's current LAN addresses.</summary>
    public static (X509Certificate2 Server, X509Certificate2 Root, string? Notice) GetOrCreate(
        StateStore state, string exportDir, bool forceNewLeaf, bool lan)
    {
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        string? notice = null;

        var root = Find(store, state.RootThumbprint, c => c.HasPrivateKey && c.NotAfter > DateTime.Now.AddYears(2));
        if (root is null)
        {
            root = CreateRoot();
            store.Add(root);
            notice = "Created a new Abyss Remote root certificate. Install AbyssRemote-RootCA.cer on each phone or tablet (see README).";
        }

        var addresses = CurrentAddresses();
        var server = forceNewLeaf ? null : Find(store, state.CertificateThumbprint,
            c => c.HasPrivateKey && c.NotAfter > DateTime.Now.AddDays(14) && c.Issuer == root.Subject);
        if (server is not null && lan && !Covers(server, addresses.Where(NetworkGuard.IsLocalNetwork)))
        {
            server = null;
            notice ??= "This PC's network address changed, so the server certificate was reissued. Devices that trust the Abyss Remote root keep working.";
        }
        if (server is null)
        {
            string? previous = state.CertificateThumbprint;
            server = CreateLeaf(root, addresses);
            store.Add(server);
            RemoveOldLeaf(store, previous, server.Thumbprint, root.Subject);
        }

        state.SetCertificateThumbprints(server.Thumbprint, root.Thumbprint);
        File.WriteAllBytes(Path.Combine(exportDir, "AbyssRemote-RootCA.cer"), root.Export(X509ContentType.Cert));
        File.WriteAllBytes(Path.Combine(exportDir, "AbyssRemote-Server.cer"), server.Export(X509ContentType.Cert));
        return (server, root, notice);
    }

    /// <summary>True when the certificate's subject alternative names include every address.</summary>
    public static bool Covers(X509Certificate2 certificate, IEnumerable<IPAddress> addresses)
    {
        var raw = certificate.Extensions.FirstOrDefault(e => e.Oid?.Value == "2.5.29.17");
        if (raw is null) return false;
        var listed = new X509SubjectAlternativeNameExtension(raw.RawData, raw.Critical).EnumerateIPAddresses().ToHashSet();
        return addresses.All(listed.Contains);
    }

    private static X509Certificate2? Find(X509Store store, string? thumbprint, Func<X509Certificate2, bool> usable) =>
        thumbprint is null ? null : store.Certificates.Cast<X509Certificate2>()
            .FirstOrDefault(c => c.Thumbprint.Equals(thumbprint, StringComparison.OrdinalIgnoreCase) && usable(c));

    private static X509Certificate2 CreateRoot()
    {
        using var rsa = RSA.Create(3072);
        var req = new CertificateRequest(RootSubject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
        using var temporary = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddYears(10));
        // Re-import so the private key is persisted (non-exportable) in the user's key store.
        return X509CertificateLoader.LoadPkcs12(temporary.Export(X509ContentType.Pfx), (string?)null,
            X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.PersistKeySet);
    }

    private static X509Certificate2 CreateLeaf(X509Certificate2 root, IReadOnlyList<IPAddress> addresses)
    {
        using var rsa = RSA.Create(3072);
        var req = new CertificateRequest(LeafSubject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
        req.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(root, true, false));
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddDnsName(Environment.MachineName);
        san.AddIpAddress(IPAddress.Loopback);
        foreach (var address in addresses) san.AddIpAddress(address);
        req.CertificateExtensions.Add(san.Build());
        // iOS rejects TLS server certificates valid for more than 825 days; one year is well inside that.
        using var leafNoKey = req.Create(root, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddDays(365), RandomNumberGenerator.GetBytes(16));
        using var leafWithKey = leafNoKey.CopyWithPrivateKey(rsa);
        return X509CertificateLoader.LoadPkcs12(leafWithKey.Export(X509ContentType.Pfx), (string?)null,
            X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.PersistKeySet);
    }

    // Only ever deletes the exact server certificate this app created before (matched by
    // thumbprint, subject and issuer), never anything else in the user's store.
    private static void RemoveOldLeaf(X509Store store, string? oldThumbprint, string newThumbprint, string rootSubject)
    {
        if (oldThumbprint is null || oldThumbprint.Equals(newThumbprint, StringComparison.OrdinalIgnoreCase)) return;
        foreach (var old in store.Certificates.Find(X509FindType.FindByThumbprint, oldThumbprint, false))
            if (old.Subject == LeafSubject && old.Issuer == rootSubject) store.Remove(old);
    }
}

public sealed class RequestProtection
{
    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
    private readonly HashSet<string> _hosts;

    /// <param name="allowedHosts">Host names and addresses this PC really answers to.</param>
    public RequestProtection(IEnumerable<string> allowedHosts) =>
        _hosts = new HashSet<string>(allowedHosts, StringComparer.OrdinalIgnoreCase);

    /// <summary>CSRF token: HMAC of the device id with a key that changes on every host start.</summary>
    public string Csrf(string deviceId) =>
        Convert.ToHexString(HMACSHA256.HashData(_key, Encoding.ASCII.GetBytes(deviceId)));

    public bool ValidCsrf(string? supplied, string deviceId)
    {
        if (supplied is null || supplied.Length != 64) return false;
        try { return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(supplied), Convert.FromHexString(Csrf(deviceId))); }
        catch (FormatException) { return false; }
    }

    /// <summary>DNS-rebinding defense: a page on evil.example whose DNS later points at this PC
    /// still sends "Host: evil.example", which is not on the list.</summary>
    public bool KnownHost(HttpContext ctx) => _hosts.Contains(ctx.Request.Host.Host);

    public static bool SameOrigin(HttpContext ctx)
    {
        string? origin = ctx.Request.Headers.Origin;
        if (string.IsNullOrWhiteSpace(origin) || !Uri.TryCreate(origin, UriKind.Absolute, out var parsed)) return false;
        // Exact scheme, host and port. Browsers always send Origin on WebSockets and on POST/DELETE.
        return parsed.Scheme == "https" && parsed.Host.Equals(ctx.Request.Host.Host, StringComparison.OrdinalIgnoreCase)
            && parsed.Port == (ctx.Request.Host.Port ?? 443) && parsed.AbsolutePath == "/";
    }
}
