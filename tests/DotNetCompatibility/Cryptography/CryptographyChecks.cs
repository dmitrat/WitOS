using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace DotNetCompatibility;

/// <summary>
/// Cryptography (plan step W3.1, RFC 0015 section 8): random bytes, the hashes and HMAC against published vectors, AES in
/// CBC against NIST's vector and in GCM with its tag, RSA with PSS and OAEP and its PEM round trip, ECDSA and ECDH on
/// P-256, PBKDF2 against RFC 6070, X.509 certificates made, signed, exported as PEM and PKCS#12 and chained to a custom
/// root, and the platform's root store. On Windows the platform's providers serve; on Linux and WitOS OpenSSL does, on
/// WitOS the one the system layer builds from its pinned release.
/// </summary>
internal static class CryptographyChecks
{
    private const string AREA = "Cryptography";

    /// <summary>
    /// The area's checks.
    /// </summary>
    /// <returns>The checks.</returns>
    public static IEnumerable<Check> All()
    {
        yield return new Check(AREA, "Random", () =>
        {
            var bytes = new byte[1024 * 1024];
            RandomNumberGenerator.Fill(bytes);
            var distinct = bytes.Distinct().Count();
            return CheckResult.Now(distinct == 256 && bytes.Count(b => b == 0) < bytes.Length / 128, $"{distinct} distinct byte values in {bytes.Length} bytes");
        });
        yield return new Check(AREA, "Hashes", () =>
        {
            var sha256 = Convert.ToHexString(SHA256.HashData("abc"u8)).ToLowerInvariant();
            var sha512 = Convert.ToHexString(SHA512.HashData("abc"u8)).ToLowerInvariant();
            var hmac = Convert.ToHexString(HMACSHA256.HashData("key"u8, "The quick brown fox jumps over the lazy dog"u8)).ToLowerInvariant();
            return CheckResult.Now(
                sha256 == "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad" &&
                sha512.StartsWith("ddaf35a193617abacc417349ae20413112e6fa4e89a97ea20a9eeee64b55d39a", StringComparison.Ordinal) &&
                hmac == "f7bc83f430538424b13298e6aa6fb143ef4d59a14946175997479dbc2d1a3cd8",
                $"SHA-256 {sha256[..16]}…, SHA-512 {sha512[..16]}…, HMAC-SHA-256 {hmac[..16]}…");
        });
        yield return new Check(AREA, "Pbkdf2", () =>
        {
            var derived = Convert.ToHexString(Rfc2898DeriveBytes.Pbkdf2("password"u8, "salt"u8, 2, HashAlgorithmName.SHA1, 20)).ToLowerInvariant();
            return CheckResult.Now(derived == "ea6c014dc72d6f8ccd1ed92ace1d41f0d8de8957", $"RFC 6070's second vector: {derived}");
        });
        yield return new Check(AREA, "AesCbc", () =>
        {
            // NIST SP 800-38A, F.2.1 CBC-AES128.Encrypt, the first block.
            using var aes = Aes.Create();
            aes.Key = Convert.FromHexString("2b7e151628aed2a6abf7158809cf4f3c");
            var iv = Convert.FromHexString("000102030405060708090a0b0c0d0e0f");
            var plain = Convert.FromHexString("6bc1bee22e409f96e93d7e117393172a");
            var cipher = Convert.ToHexString(aes.EncryptCbc(plain, iv, PaddingMode.None)).ToLowerInvariant();
            var back = aes.DecryptCbc(Convert.FromHexString(cipher), iv, PaddingMode.None);
            return CheckResult.Now(cipher == "7649abac8119b246cee98e9b12e9197d" && back.AsSpan().SequenceEqual(plain), $"AES-128-CBC {cipher}");
        });
        yield return new Check(AREA, "AesGcm", () =>
        {
            var data = new byte[64 * 1024];
            new Random(11).NextBytes(data);
            var nonce = RandomNumberGenerator.GetBytes(12);
            var cipher = new byte[data.Length];
            var tag = new byte[16];
            var plain = new byte[data.Length];
            using var aes = new AesGcm(RandomNumberGenerator.GetBytes(32), 16);
            aes.Encrypt(nonce, data, cipher, tag, "witos"u8);
            aes.Decrypt(nonce, cipher, tag, plain, "witos"u8);
            var roundTrip = plain.AsSpan().SequenceEqual(data);
            cipher[100] ^= 1;
            bool rejected;
            try
            {
                // A failed decryption may leave anything in its output: the round trip was judged before.
                aes.Decrypt(nonce, cipher, tag, new byte[data.Length], "witos"u8);
                rejected = false;
            }
            catch (CryptographicException)
            {
                rejected = true;
            }
            return CheckResult.Now(roundTrip && rejected, $"{data.Length} bytes through AES-256-GCM back whole: {roundTrip}, a changed byte rejected: {rejected}");
        });
        yield return new Check(AREA, "Rsa", () =>
        {
            var data = Encoding.UTF8.GetBytes("Signed on WitOS");
            using var rsa = RSA.Create(2048);
            var signature = rsa.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
            var verified = rsa.VerifyData(data, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
            var secret = rsa.Decrypt(rsa.Encrypt(data, RSAEncryptionPadding.OaepSHA256), RSAEncryptionPadding.OaepSHA256);
            using var imported = RSA.Create();
            imported.ImportFromPem(rsa.ExportPkcs8PrivateKeyPem());
            var again = imported.VerifyData(data, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
            return CheckResult.Now(verified && again && secret.AsSpan().SequenceEqual(data),
                $"RSA-{rsa.KeySize}: PSS verified {verified}, OAEP round trip {secret.Length} bytes, the PEM key verifies {again}");
        });
        yield return new Check(AREA, "EllipticCurves", () =>
        {
            var data = Encoding.UTF8.GetBytes("Signed on WitOS");
            using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var verified = ecdsa.VerifyData(data, ecdsa.SignData(data, HashAlgorithmName.SHA256), HashAlgorithmName.SHA256);
            using var alice = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            using var bob = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            var shared = alice.DeriveKeyMaterial(bob.PublicKey).AsSpan().SequenceEqual(bob.DeriveKeyMaterial(alice.PublicKey));
            return CheckResult.Now(verified && shared, $"ECDSA P-256 verified {verified}, ECDH shared secrets equal {shared}");
        });
        yield return new Check(AREA, "X509Pem", () =>
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request = new CertificateRequest("CN=WitOS compatibility", key, HashAlgorithmName.SHA256);
            using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            using var loaded = X509Certificate2.CreateFromPem(certificate.ExportCertificatePem(), key.ExportPkcs8PrivateKeyPem());
            using var loadedKey = loaded.GetECDsaPrivateKey();
            var signs = loadedKey is not null && key.VerifyData("x"u8, loadedKey.SignData("x"u8, HashAlgorithmName.SHA256), HashAlgorithmName.SHA256);
            return CheckResult.Now(loaded.Thumbprint == certificate.Thumbprint && loaded.Subject == "CN=WitOS compatibility" && signs,
                $"{loaded.Subject}, thumbprint {loaded.Thumbprint[..12]}…, the PEM key signs {signs}");
        });
        yield return new Check(AREA, "X509Pkcs12", () =>
        {
            using var authority = TestCertificate.Authority("WitOS compatibility CA");
            using var server = TestCertificate.Server(authority);
            using var serverKey = server.GetECDsaPrivateKey();
            var signs = serverKey is not null && serverKey.VerifyData("x"u8, serverKey.SignData("x"u8, HashAlgorithmName.SHA256), HashAlgorithmName.SHA256);
            var names = server.Extensions.OfType<X509SubjectAlternativeNameExtension>().SelectMany(extension => extension.EnumerateDnsNames()).ToArray();
            return CheckResult.Now(server.HasPrivateKey && signs && server.Issuer == authority.Subject && names.Contains(TestCertificate.SERVER_NAME),
                $"{server.Subject} issued by {server.Issuer}, private key {server.HasPrivateKey}, names {string.Join(", ", names)}");
        });
        yield return new Check(AREA, "X509Chain", () =>
        {
            using var authority = TestCertificate.Authority("WitOS compatibility CA");
            using var server = TestCertificate.Server(authority);
            using var trusted = new X509Chain();
            trusted.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            trusted.ChainPolicy.CustomTrustStore.Add(authority);
            trusted.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            var built = trusted.Build(server);
            using var untrusted = new X509Chain();
            untrusted.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            untrusted.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            var refused = !untrusted.Build(server);
            return CheckResult.Now(built && trusted.ChainElements.Count == 2 && refused,
                $"with the authority: built {built} of {trusted.ChainElements.Count} elements; without: {string.Join(", ", untrusted.ChainStatus.Select(status => status.Status))}");
        });
        yield return new Check(AREA, "RootStore", () =>
        {
            using var store = new X509Store(StoreName.Root, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadOnly);
            var count = store.Certificates.Count;
            var authorities = store.Certificates.Count(certificate =>
                certificate.Extensions.OfType<X509BasicConstraintsExtension>().Any(extension => extension.CertificateAuthority));
            return CheckResult.Now(count >= 10 && authorities >= 10, $"{count} certificates in the machine's root store, {authorities} authorities");
        });
    }
}
