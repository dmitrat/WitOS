using WitOS.Dev.Substrate;
using WitOS.Dev.Tests.Support;

namespace WitOS.Dev.Tests.Substrate;

/// <summary>
/// ICU's headers in the system layer's sysroot (plan step R1.2b): the pin names an ICU4C release by the SHA-256 of its
/// source tarball, and the sysroot takes the unicode/ headers of the two libraries .NET's globalization shim loads.
/// </summary>
[TestFixture]
public sealed class IcuHeadersTests
{
    #region Functions

    [Test]
    public async Task PinNamesTheSourceTarballOfAReleaseTest()
    {
        var root = TestEnvironment.Root;
        var pin = await IcuHeaders.ReadPinAsync(root);
        Assert.That(pin.Name, Does.Match("^icu4c-[0-9]+\\.[0-9]+$"));
        Assert.That(pin.Url, Does.StartWith("https://github.com/unicode-org/icu/releases/download/release-"));
        Assert.That(pin.Url, Does.EndWith($"/{pin.Name}-sources.tgz"));
        Assert.That(pin.Sha256, Does.Match("^[0-9a-f]{64}$"));
        Assert.That(IcuHeaders.HeaderDirectories(root, pin).Select(directory => Path.GetFileName(Path.GetDirectoryName(directory))),
            Is.EqualTo(new[] { "common", "i18n" }));
    }

    #endregion
}
