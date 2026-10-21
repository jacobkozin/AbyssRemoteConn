using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using AbyssRemote.Host;
using Microsoft.AspNetCore.Http;

namespace AbyssRemote.Tests;

[TestClass]
public sealed class RequestProtectionTests
{
    private static HttpContext Request(string host, string? origin)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Scheme = "https";
        ctx.Request.Host = new HostString(host);
        if (origin is not null) ctx.Request.Headers.Origin = origin;
        return ctx;
    }

    [TestMethod]
    public void CsrfTokenIsBoundToOneDeviceAndOneHostRun()
    {
        var protection = new RequestProtection([]);
        string token = protection.Csrf("device-a");

        Assert.IsTrue(protection.ValidCsrf(token, "device-a"));
        Assert.IsTrue(protection.ValidCsrf(token.ToLowerInvariant(), "device-a"));
        Assert.IsFalse(protection.ValidCsrf(token, "device-b"));
        Assert.IsFalse(new RequestProtection([]).ValidCsrf(token, "device-a"), "a restarted host must issue new tokens");
        Assert.IsFalse(protection.ValidCsrf(null, "device-a"));
        Assert.IsFalse(protection.ValidCsrf(token[..63], "device-a"));
        Assert.IsFalse(protection.ValidCsrf(new string('z', 64), "device-a"));
    }

    [TestMethod]
    [DataRow("https://192.168.1.20:8444", true)]
    [DataRow("https://192.168.1.20:8445", false)] // other port = other origin
    [DataRow("http://192.168.1.20:8444", false)]  // plain HTTP
    [DataRow("https://evil.example:8444", false)]
    [DataRow("https://192.168.1.20:8444/path", false)]
    [DataRow("null", false)]                      // sandboxed iframe / file:// pages
    public void OriginMustMatchExactly(string origin, bool allowed) =>
        Assert.AreEqual(allowed, RequestProtection.SameOrigin(Request("192.168.1.20:8444", origin)));

    [TestMethod]
    public void MissingOriginIsRefused() =>
        Assert.IsFalse(RequestProtection.SameOrigin(Request("192.168.1.20:8444", null)));

    [TestMethod]
    public void OnlyKnownHostNamesAreServed()
    {
        var protection = new RequestProtection(["localhost", "127.0.0.1", "192.168.1.20", "GAMING-PC"]);
        Assert.IsTrue(protection.KnownHost(Request("192.168.1.20:8444", null)));
        Assert.IsTrue(protection.KnownHost(Request("gaming-pc:8444", null)));
        Assert.IsFalse(protection.KnownHost(Request("rebind.attacker.example:8444", null)));
    }

    [TestMethod]
    [DataRow("127.0.0.1", true)]
    [DataRow("10.4.5.6", true)]
    [DataRow("172.16.0.9", true)]
    [DataRow("172.31.255.1", true)]
    [DataRow("192.168.0.50", true)]
    [DataRow("169.254.10.10", true)]
    [DataRow("::1", true)]
    [DataRow("fe80::1", true)]
    [DataRow("fd12:3456::1", true)]
    [DataRow("::ffff:192.168.1.7", true)]
    [DataRow("172.32.0.1", false)]
    [DataRow("8.8.8.8", false)]
    [DataRow("100.64.0.1", false)]  // carrier-grade NAT is shared with strangers
    [DataRow("2001:4860:4860::8888", false)]
    [DataRow("::ffff:8.8.8.8", false)]
    public void LanModeOnlyAcceptsLocalNetworkPeers(string address, bool local) =>
        Assert.AreEqual(local, NetworkGuard.IsLocalNetwork(IPAddress.Parse(address)));

    [TestMethod]
    public void CertificateCoverageDetectsAChangedLanAddress()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddIpAddress(IPAddress.Parse("192.168.1.20"));
        request.CertificateExtensions.Add(san.Build());
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1));

        Assert.IsTrue(HostCertificates.Covers(certificate, [IPAddress.Parse("192.168.1.20")]));
        Assert.IsFalse(HostCertificates.Covers(certificate, [IPAddress.Parse("192.168.1.20"), IPAddress.Parse("10.0.0.7")]));
    }
}
