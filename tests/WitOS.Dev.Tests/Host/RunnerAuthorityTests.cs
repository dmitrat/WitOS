using System.Net;
using System.Security.Cryptography.X509Certificates;
using WitOS.Dev.Host;
using WitOS.Dev.Tests.Support;

namespace WitOS.Dev.Tests.Host;

/// <summary>
/// The runner's certificate authority and its HTTPS server (plan step W3.1): the authority of a boot signs the server's
/// certificate for the address the guest reaches, the PEM a scenario lays in the guest's root store is the authority's
/// certificate, and the network peer answers an HTTPS request with its text over TLS with that certificate.
/// </summary>
[TestFixture]
public sealed class RunnerAuthorityTests
{
    #region Functions

    [Test]
    public async Task AuthoritySignsTheServerCertificateForTheAddressTest()
    {
        var address = IPAddress.Parse(NetworkPeer.ECHO_ADDRESS);
        using var authority = new RunnerAuthority(address);
        using var other = new RunnerAuthority(address);
        Assert.That(authority.Authority.HasPrivateKey && authority.Server.HasPrivateKey, Is.True);
        Assert.That(authority.Authority.Thumbprint, Is.Not.EqualTo(other.Authority.Thumbprint), "each boot has its own authority");
        Assert.That(authority.Server.Issuer, Is.EqualTo(authority.Authority.Subject));
        var names = authority.Server.Extensions.OfType<X509SubjectAlternativeNameExtension>().Single();
        Assert.That(names.EnumerateIPAddresses(), Is.EqualTo(new[] { address }));
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(authority.Authority);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        Assert.That(chain.Build(authority.Server), Is.True, string.Join(", ", chain.ChainStatus.Select(status => status.Status)));
        using var otherChain = new X509Chain();
        otherChain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        otherChain.ChainPolicy.CustomTrustStore.Add(other.Authority);
        otherChain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        Assert.That(otherChain.Build(authority.Server), Is.False, "another boot's authority does not vouch for the server");
        var pem = Path.Combine(TestEnvironment.Scratch(), "witos-runner-ca.pem");
        await authority.WriteAuthorityPemAsync(pem);
        using var read = X509Certificate2.CreateFromPem(await File.ReadAllTextAsync(pem));
        Assert.That(read.Thumbprint, Is.EqualTo(authority.Authority.Thumbprint));
        Assert.That(read.HasPrivateKey, Is.False, "the guest's root store holds the certificate alone");
        Assert.That(RunnerAuthority.PACKAGE_PATH, Does.StartWith("etc/ssl/certs/"));
    }

    [Test]
    public async Task PeerAnswersHttpsWithTheServerCertificateTest()
    {
        using var authority = new RunnerAuthority(IPAddress.Parse(NetworkPeer.ECHO_ADDRESS));
        using var peer = new NetworkPeer(0, authority);
        Assert.That(peer.HttpsPort, Is.Not.Zero);
        Assert.That(peer.NetdevOptions, Does.Contain($",guestfwd=tcp:{NetworkPeer.ECHO_ADDRESS}:{NetworkPeer.HTTPS_PORT}-tcp:127.0.0.1:{peer.HttpsPort}"));
        X509Certificate2? seen = null;
        using var handler = new HttpClientHandler
        {
            UseProxy = false,
            ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
            {
                seen = certificate is null ? null : new X509Certificate2(certificate);
                return certificate is not null && certificate.Thumbprint == authority.Server.Thumbprint;
            }
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        var address = $"https://127.0.0.1:{peer.HttpsPort}/";
        Assert.That(await client.GetStringAsync(address), Is.EqualTo(NetworkPeer.HTTPS_TEXT));
        Assert.That(await client.GetStringAsync(address), Is.EqualTo(NetworkPeer.HTTPS_TEXT));
        Assert.That(peer.HttpsRequests, Is.EqualTo(2));
        Assert.That(seen?.Thumbprint, Is.EqualTo(authority.Server.Thumbprint));
        using var unknown = new NetworkPeer(0);
        Assert.That(unknown.HttpsPort, Is.Zero, "no authority, no HTTPS server");
        Assert.That(unknown.NetdevOptions, Does.Not.Contain("443"));
    }

    #endregion
}
