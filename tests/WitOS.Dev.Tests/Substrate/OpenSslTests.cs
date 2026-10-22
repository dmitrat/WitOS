using WitOS.Dev.Kernel;
using WitOS.Dev.Substrate;
using WitOS.Dev.Tests.Support;

namespace WitOS.Dev.Tests.Substrate;

/// <summary>
/// OpenSSL in the system layer (plan step W3.1): the pin names a release of the 3.x long-term support series by the
/// SHA-256 of its source tarball at the release's canonical URL, the libraries are the sonames the portable shim opens,
/// each ISA has OpenSSL's own Configure target, and upstream's FindOpenSSL is pointed at the build through CMake variables.
/// </summary>
[TestFixture]
public sealed class OpenSslTests
{
    #region Functions

    [Test]
    public async Task PinNamesAReleaseTarballTest()
    {
        var pin = await OpenSsl.ReadPinAsync(TestEnvironment.Root);
        Assert.That(pin.Name, Does.Match("^openssl-3\\.[0-9]+\\.[0-9]+$"));
        Assert.That(pin.Url, Is.EqualTo($"https://github.com/openssl/openssl/releases/download/{pin.Name}/{pin.Name}.tar.gz"));
        Assert.That(pin.Sha256, Does.Match("^[0-9a-f]{64}$"));
        Assert.That(pin.License, Is.EqualTo(OpenSsl.LICENSE));
        Assert.That(pin.Purpose, Does.Contain("libssl.so.3"));
        Assert.That(OpenSsl.Sonames, Is.EqualTo(new[] { "libcrypto.so.3", "libssl.so.3" }));
        Assert.That(OpenSsl.LIBRARY_DIRECTORY, Is.EqualTo("usr/lib"));
    }

    [Test]
    public async Task PinOfAnotherSeriesOrUrlIsRefusedTest()
    {
        var root = TestEnvironment.Scratch();
        Directory.CreateDirectory(Path.Combine(root, "src", "Substrate"));
        var lock_ = Path.Combine(root, OpenSsl.LOCK);
        var text = await File.ReadAllTextAsync(Path.Combine(TestEnvironment.Root, OpenSsl.LOCK));
        await File.WriteAllTextAsync(lock_, text.Replace("openssl-3.", "openssl-4."));
        Assert.That(async () => await OpenSsl.ReadPinAsync(root), Throws.TypeOf<InvalidDataException>());
        await File.WriteAllTextAsync(lock_, text.Replace("https://github.com/openssl/openssl/", "https://example.com/"));
        Assert.That(async () => await OpenSsl.ReadPinAsync(root), Throws.TypeOf<InvalidDataException>());
        await File.WriteAllTextAsync(lock_, text);
        Assert.That((await OpenSsl.ReadPinAsync(root)).Name, Does.StartWith("openssl-3."));
    }

    [Test]
    public void EachArchitectureHasItsConfigureTargetTest()
    {
        Assert.That(OpenSsl.ConfigureTarget(KernelArchitecture.X64), Is.EqualTo("linux-x86_64"));
        Assert.That(OpenSsl.ConfigureTarget(KernelArchitecture.Arm64), Is.EqualTo("linux-aarch64"));
        Assert.That(OpenSsl.ConfigureTarget(KernelArchitecture.Riscv64), Is.EqualTo("linux64-riscv64"));
    }

    [Test]
    public void CMakeArgumentsPointFindOpenSslAtTheBuildTest()
    {
        var build = new OpenSslBuild("/stage/usr/include", "/stage/usr/lib",
            [("libcrypto.so.3", "/stage/usr/lib/libcrypto.so.3"), ("libssl.so.3", "/stage/usr/lib/libssl.so.3")]);
        Assert.That(OpenSsl.CMakeArguments(build), Is.EqualTo(new[]
        {
            "-cmakeargs", "-DOPENSSL_INCLUDE_DIR=/stage/usr/include",
            "-cmakeargs", "-DOPENSSL_CRYPTO_LIBRARY=/stage/usr/lib/libcrypto.so.3",
            "-cmakeargs", "-DOPENSSL_SSL_LIBRARY=/stage/usr/lib/libssl.so.3"
        }));
    }

    #endregion
}
