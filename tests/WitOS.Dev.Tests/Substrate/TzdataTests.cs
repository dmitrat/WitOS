using WitOS.Dev.Substrate;
using WitOS.Dev.Tests.Support;

namespace WitOS.Dev.Tests.Substrate;

/// <summary>
/// tzdata in the .NET install (plan step N4.1): the pin names an IANA release by the SHA-256 of its tarball at the
/// release's canonical URL, and the install's zoneinfo directory is TimeZoneInfo's default on Unix.
/// </summary>
[TestFixture]
public sealed class TzdataTests
{
    #region Functions

    [Test]
    public async Task PinNamesAReleaseTarballTest()
    {
        var pin = await Tzdata.ReadPinAsync(TestEnvironment.Root);
        Assert.That(pin.Name, Does.Match("^tzdata20[0-9]{2}[a-z]$"));
        Assert.That(pin.Url, Is.EqualTo($"https://data.iana.org/time-zones/releases/{pin.Name}.tar.gz"));
        Assert.That(pin.Sha256, Does.Match("^[0-9a-f]{64}$"));
        Assert.That(pin.Purpose, Does.Contain("zoneinfo"));
        Assert.That(Tzdata.DIRECTORY, Is.EqualTo("usr/share/zoneinfo"));
    }

    #endregion
}
