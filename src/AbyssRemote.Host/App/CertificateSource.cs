using System.Security.Cryptography.X509Certificates;

namespace AbyssRemote.Host.App;

/// <summary>The server certificate, the root that signed it, and anything the user should be
/// told about them (for example that a new root was created).</summary>
public sealed record HostCertificateSet(X509Certificate2 Server, X509Certificate2 Root, string? Notice);

/// <summary>Where the server gets its TLS certificates. The real program uses the Windows
/// certificate store. Tests pass a source that makes throwaway certificates in memory, so they
/// never read or change the store.</summary>
public interface ICertificateSource
{
    /// <param name="state">Remembers which certificates in the store are ours.</param>
    /// <param name="exportDirectory">Folder for the public .cer files a phone can install.</param>
    /// <param name="renewServerCertificate">Issue a fresh server certificate even if the current one is fine.</param>
    /// <param name="lan">True when the certificate must cover this PC's network addresses.</param>
    HostCertificateSet GetOrCreate(StateStore state, string exportDirectory, bool renewServerCertificate, bool lan);
}

public sealed class WindowsCertificateStore : ICertificateSource
{
    public HostCertificateSet GetOrCreate(StateStore state, string exportDirectory, bool renewServerCertificate, bool lan)
    {
        var (server, root, notice) = HostCertificates.GetOrCreate(state, exportDirectory, renewServerCertificate, lan);
        return new HostCertificateSet(server, root, notice);
    }
}
