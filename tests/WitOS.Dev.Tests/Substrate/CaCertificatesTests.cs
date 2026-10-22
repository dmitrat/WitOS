using WitOS.Dev.Substrate;
using WitOS.Dev.Tests.Support;

namespace WitOS.Dev.Tests.Substrate;

/// <summary>
/// The root certificates of the .NET install (plan step W3.1): the pin names an extract of Mozilla's CA bundle by its
/// date and the SHA-256 of curl's PEM file, and the install lays it where .NET's root store on Unix reads.
/// </summary>
[TestFixture]
public sealed class CaCertificatesTests
{
    #region Functions

    [Test]
    public void PinNamesAnExtractOfTheBundleTest()
    {
        var pin = CaCertificates.ReadPin(TestEnvironment.Root);
        Assert.That(pin.Name, Does.Match("^cacert-20[0-9]{2}-[0-9]{2}-[0-9]{2}$"));
        Assert.That(pin.Url, Is.EqualTo($"https://curl.se/ca/{pin.Name}.pem"));
        Assert.That(pin.Sha256, Does.Match("^[0-9a-f]{64}$"));
        Assert.That(pin.License, Is.EqualTo(CaCertificates.LICENSE));
        Assert.That(pin.Purpose, Does.Contain("root"));
        Assert.That(CaCertificates.PACKAGE_PATH, Is.EqualTo("etc/ssl/certs/ca-certificates.crt"));
    }

    [Test]
    public async Task PinOfAnotherSourceIsRefusedTest()
    {
        var root = TestEnvironment.Scratch();
        Directory.CreateDirectory(Path.Combine(root, "src", "Substrate"));
        var lock_ = Path.Combine(root, CaCertificates.LOCK);
        var text = await File.ReadAllTextAsync(Path.Combine(TestEnvironment.Root, CaCertificates.LOCK));
        await File.WriteAllTextAsync(lock_, text.Replace("https://curl.se/ca/", "https://example.com/ca/"));
        Assert.That(() => CaCertificates.ReadPin(root), Throws.TypeOf<InvalidDataException>());
        await File.WriteAllTextAsync(lock_, text.Replace("MPL-2.0", "MIT"));
        Assert.That(() => CaCertificates.ReadPin(root), Throws.TypeOf<InvalidDataException>());
        await File.WriteAllTextAsync(lock_, text);
        Assert.That(CaCertificates.ReadPin(root).Name, Does.StartWith("cacert-"));
        Assert.That(() => CaCertificates.Require(root), Throws.TypeOf<InvalidOperationException>().With.Message.Contains("setup"));
    }

    #endregion
}
