using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Http;

namespace AbyssRemote.Host;

public sealed class PairingManager
{
    private readonly object _sync = new();
    private string _pin = "";
    private DateTimeOffset _expires;
    private readonly ConcurrentDictionary<string, (int attempts, DateTimeOffset until)> _attempts = new();

    public string Rotate()
    {
        lock (_sync)
        {
            _pin = RandomNumberGenerator.GetInt32(0, 100_000_000).ToString("D8");
            _expires = DateTimeOffset.UtcNow.AddMinutes(10);
            _attempts.Clear();
            return _pin;
        }
    }

    public bool TryUse(string code, string address)
    {
        lock (_sync)
        {
            var now = DateTimeOffset.UtcNow;
            var current = _attempts.GetOrAdd(address, _ => (0, now.AddMinutes(5)));
            if (current.until < now) current = (0, now.AddMinutes(5));
            if (current.attempts >= 5) return false;
            _attempts[address] = (current.attempts + 1, current.until);
            if (now >= _expires || code.Length != 8 || !code.All(char.IsAsciiDigit) || _pin.Length != 8) return false;
            bool equal = CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.ASCII.GetBytes(code), System.Text.Encoding.ASCII.GetBytes(_pin));
            if (equal) { _pin = ""; _expires = now; }
            return equal;
        }
    }
}

public static class HostCertificates
{
    public static (X509Certificate2 Server, X509Certificate2 Root) GetOrCreate(StateStore state, string localData, bool renewLeaf)
    {
        using var certStore = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        certStore.Open(OpenFlags.ReadWrite);
        var root = certStore.Certificates.Cast<X509Certificate2>()
            .FirstOrDefault(c => c.Thumbprint.Equals(state.RootThumbprint, StringComparison.OrdinalIgnoreCase)
                                 && c.HasPrivateKey && c.NotAfter > DateTime.Now.AddYears(2));
        if (root is null)
        {
            using var rsa = RSA.Create(3072);
            var req = new CertificateRequest("CN=Abyss Remote Personal Root CA", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
            req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
            using var temporary = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddYears(10));
            root = X509CertificateLoader.LoadPkcs12(temporary.Export(X509ContentType.Pfx), (string?)null,
                X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.PersistKeySet);
            certStore.Add(root);
        }
        var server = !renewLeaf ? certStore.Certificates.Cast<X509Certificate2>()
            .FirstOrDefault(c => c.Thumbprint.Equals(state.CertificateThumbprint, StringComparison.OrdinalIgnoreCase)
                && c.HasPrivateKey && c.NotAfter > DateTime.Now.AddDays(14) && c.Issuer == root.Subject) : null;
        if (server is null)
        {
            using var rsa = RSA.Create(3072);
            var req = new CertificateRequest("CN=Abyss Remote Windows Host", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
            req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));
            req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
            var san = new SubjectAlternativeNameBuilder();
            san.AddDnsName("localhost");
            san.AddDnsName(Environment.MachineName);
            san.AddIpAddress(IPAddress.Loopback);
            foreach (var net in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (net.OperationalStatus != OperationalStatus.Up) continue;
                foreach (var addr in net.GetIPProperties().UnicastAddresses)
                    if (addr.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !IPAddress.IsLoopback(addr.Address))
                        san.AddIpAddress(addr.Address);
            }
            req.CertificateExtensions.Add(san.Build());
            using var leafNoKey = req.Create(root, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddDays(365), RandomNumberGenerator.GetBytes(16));
            using var leafWithKey = leafNoKey.CopyWithPrivateKey(rsa);
            server = X509CertificateLoader.LoadPkcs12(leafWithKey.Export(X509ContentType.Pfx), (string?)null,
                X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.PersistKeySet);
            certStore.Add(server);
        }
        state.SetCertificateThumbprints(server.Thumbprint, root.Thumbprint);
        ExportPublic(server, root, localData);
        return (server, root);
    }

    private static void ExportPublic(X509Certificate2 server, X509Certificate2 root, string dir)
    {
        File.WriteAllBytes(Path.Combine(dir, "AbyssRemote-RootCA.cer"), root.Export(X509ContentType.Cert));
        File.WriteAllBytes(Path.Combine(dir, "AbyssRemote-Server.cer"), server.Export(X509ContentType.Cert));
    }
}

public sealed class RequestProtection
{
    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
    public string Csrf(string deviceId)
    {
        using var hmac = new HMACSHA256(_key);
        return Convert.ToHexString(hmac.ComputeHash(System.Text.Encoding.ASCII.GetBytes(deviceId)));
    }
    public bool ValidCsrf(string? supplied, string deviceId)
    {
        if (supplied is null || supplied.Length != 64) return false;
        try { return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(supplied), Convert.FromHexString(Csrf(deviceId))); }
        catch (FormatException) { return false; }
    }

    public static bool SameOrigin(HttpContext ctx)
    {
        string? origin = ctx.Request.Headers.Origin;
        if (string.IsNullOrWhiteSpace(origin)) return false;
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var parsed)) return false;
        // Require exact scheme, host and port. Browsers set Origin for WebSockets and mutating requests.
        return parsed.Scheme == "https" && parsed.Host.Equals(ctx.Request.Host.Host, StringComparison.OrdinalIgnoreCase)
            && parsed.Port == (ctx.Request.Host.Port ?? 443) && parsed.AbsolutePath == "/";
    }
}
