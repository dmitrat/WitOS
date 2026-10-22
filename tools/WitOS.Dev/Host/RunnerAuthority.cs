using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace WitOS.Dev.Host;

/// <summary>
/// The runner's certificate authority of one boot (plan step W3.1): a self-signed authority and the certificate of the
/// runner's HTTPS server for the address the guest reaches it at, signed by it, both made anew for each boot. A scenario
/// lays the authority's certificate in the guest's root store (<c>/etc/ssl/certs</c>), so that the guest's .NET trusts
/// the server through the store as it trusts a public site, and the runner serves with the server certificate.
/// </summary>
internal sealed class RunnerAuthority : IDisposable
{
    #region Constants

    /// <summary>
    /// The file in the guest's root store that holds the authority's certificate.
    /// </summary>
    public const string PACKAGE_PATH = "etc/ssl/certs/witos-runner-ca.pem";

    private const string AUTHORITY_NAME = "WitOS runner CA";

    #endregion

    #region Constructors

    /// <summary>
    /// Makes the authority and the server certificate for an address.
    /// </summary>
    /// <param name="address">The address the guest reaches the server at, its subject alternative name.</param>
    public RunnerAuthority(IPAddress address)
    {
        using var authorityKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var authorityRequest = new CertificateRequest($"CN={AUTHORITY_NAME}", authorityKey, HashAlgorithmName.SHA256);
        authorityRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(certificateAuthority: true, hasPathLengthConstraint: false, 0, critical: true));
        authorityRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, critical: true));
        authorityRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(authorityRequest.PublicKey, critical: false));
        var notBefore = DateTimeOffset.UtcNow.AddDays(-1);
        var notAfter = DateTimeOffset.UtcNow.AddDays(7);
        Authority = authorityRequest.CreateSelfSigned(notBefore, notAfter);
        using var serverKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var serverRequest = new CertificateRequest($"CN={address}", serverKey, HashAlgorithmName.SHA256);
        serverRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(certificateAuthority: false, hasPathLengthConstraint: false, 0, critical: true));
        serverRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        serverRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], critical: false)); // server authentication
        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(address);
        serverRequest.CertificateExtensions.Add(names.Build());
        serverRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(serverRequest.PublicKey, critical: false));
        var serial = RandomNumberGenerator.GetBytes(16);
        serial[0] &= 0x7F;
        using var signed = serverRequest.Create(Authority, notBefore, notAfter, serial);
        using var withKey = signed.CopyWithPrivateKey(serverKey);
        // Through PKCS#12, so that the key is one the host's TLS can serve with (Windows serves no ephemeral key).
        Server = X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pfx, "witos"), "witos",
            X509KeyStorageFlags.Exportable | (OperatingSystem.IsWindows() ? X509KeyStorageFlags.UserKeySet : X509KeyStorageFlags.DefaultKeySet));
    }

    #endregion

    #region Functions

    /// <summary>
    /// Writes the authority's certificate as PEM, the file of the guest's root store.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <returns>When it is written.</returns>
    public Task WriteAuthorityPemAsync(string path) => File.WriteAllTextAsync(path, Authority.ExportCertificatePem() + "\n");

    #endregion

    #region IDisposable

    /// <inheritdoc />
    public void Dispose()
    {
        Server.Dispose();
        Authority.Dispose();
    }

    #endregion

    #region Properties

    /// <summary>
    /// The authority, with its private key.
    /// </summary>
    public X509Certificate2 Authority { get; }

    /// <summary>
    /// The server's certificate, with its private key.
    /// </summary>
    public X509Certificate2 Server { get; }

    #endregion
}
