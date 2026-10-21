using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace DotNetCompatibility;

/// <summary>
/// Certificates the checks make for themselves (plan step W3.1): a self-signed authority and a server certificate for the
/// loopback it signs, each with its private key persisted the way a TLS server needs it on every platform (exported to
/// PKCS#12 and loaded again, since an ephemeral key serves no Windows TLS session).
/// </summary>
internal static class TestCertificate
{
    /// <summary>
    /// The name of the server certificate's subject, which the client's validation sees.
    /// </summary>
    public const string SERVER_NAME = "localhost";

    /// <summary>
    /// A self-signed certificate authority.
    /// </summary>
    /// <param name="name">The subject's common name.</param>
    /// <returns>The authority with its private key.</returns>
    public static X509Certificate2 Authority(string name)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(certificateAuthority: true, hasPathLengthConstraint: false, 0, critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, critical: true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(7));
        return Persisted(certificate);
    }

    /// <summary>
    /// A server certificate for <see cref="SERVER_NAME"/> and the loopback address, signed by an authority.
    /// </summary>
    /// <param name="authority">The authority, with its private key.</param>
    /// <returns>The certificate with its private key.</returns>
    public static X509Certificate2 Server(X509Certificate2 authority)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={SERVER_NAME}", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(certificateAuthority: false, hasPathLengthConstraint: false, 0, critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], critical: false)); // server authentication
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(SERVER_NAME);
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));
        var serial = RandomNumberGenerator.GetBytes(16);
        serial[0] &= 0x7F;
        // Within the authority's validity, which a signed certificate may not exceed.
        using var signed = request.Create(authority, new DateTimeOffset(authority.NotBefore.ToUniversalTime()), new DateTimeOffset(authority.NotAfter.ToUniversalTime()), serial);
        using var withKey = signed.CopyWithPrivateKey(key);
        return Persisted(withKey);
    }

    /// <summary>
    /// The SHA-256 thumbprint a validation callback compares.
    /// </summary>
    /// <param name="certificate">The certificate.</param>
    /// <returns>The thumbprint, hexadecimal.</returns>
    public static string Thumbprint(X509Certificate? certificate) =>
        certificate is null ? "" : Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData()));

    // The certificate and its key through PKCS#12, so that the key is one the platform's TLS can use.
    private static X509Certificate2 Persisted(X509Certificate2 certificate) =>
        X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx, "witos"), "witos",
            X509KeyStorageFlags.Exportable | (OperatingSystem.IsWindows() ? X509KeyStorageFlags.UserKeySet : X509KeyStorageFlags.DefaultKeySet));
}
