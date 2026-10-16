using WitOS.Dev.Runtime;
using WitOS.Dev.Substrate;
using WitOS.Dev.Tests.Support;

namespace WitOS.Dev.Tests.Substrate;

/// <summary>
/// PowerShell on WitOS (plan step C1.3): the pins name a release's framework-dependent archive by its SHA-256 at the
/// release's URL with PowerShell-Native's commit, and ncurses's tarball for terminfo; the scenario's inputs type Enter
/// as a carriage return after the prompt.
/// </summary>
[TestFixture]
public sealed class PowerShellTests
{
    #region Functions

    [Test]
    public async Task PinNamesAReleaseArchiveTest()
    {
        var pin = await PowerShell.ReadPinAsync(TestEnvironment.Root);
        Assert.That(pin.Version, Does.Match(@"^7\.[0-9]+\.[0-9]+$"));
        Assert.That(pin.Archive, Is.EqualTo($"powershell-{pin.Version}-linux-x64-musl-noopt-fxdependent.tar.gz"), "The IL-only archive serves every ISA");
        Assert.That(pin.Url, Does.StartWith("https://github.com/PowerShell/PowerShell/releases/download/v" + pin.Version + "/"));
        Assert.That(pin.Sha256, Does.Match("^[0-9a-f]{64}$"));
        Assert.That(pin.NativeCommit, Does.Match("^[0-9a-f]{40}$"));
        Assert.That(pin.NativeTag, Does.StartWith("v"));
        Assert.That(pin.Purpose, Does.Contain("libpsl-native"));
    }

    [Test]
    public async Task TerminfoPinNamesAnNcursesReleaseTest()
    {
        var pin = await Terminfo.ReadPinAsync(TestEnvironment.Root);
        Assert.That(pin.Name, Does.Match(@"^ncurses-[0-9]+\.[0-9]+$"));
        Assert.That(pin.Url, Is.EqualTo($"https://ftpmirror.gnu.org/ncurses/{pin.Name}.tar.gz"), "GNU's mirror redirector; the bytes are the hash's");
        Assert.That(pin.Sha256, Does.Match("^[0-9a-f]{64}$"));
        Assert.That(Terminfo.DIRECTORY, Is.EqualTo("usr/share/terminfo"));
        Assert.That(Terminfo.TERM, Is.EqualTo("xterm-256color"));
    }

    [Test]
    public void ScenarioTypesEnterAsCarriageReturnTest()
    {
        var script = RuntimePowerShell.Script();
        Assert.That(script.Count(input => input.After == RuntimePowerShell.PROMPT), Is.EqualTo(5));
        Assert.That(script.Where(input => input.After == RuntimePowerShell.PROMPT).All(input => input.Text.EndsWith('\r')), Is.True,
            "PSReadLine reads the terminal raw, where Enter is CR");
        Assert.That(script[^1].Text, Is.EqualTo("exit\r"));
        Assert.That(RuntimePowerShell.RequiredLines(WitOS.Dev.Kernel.KernelArchitecture.X64), Does.Contain(RuntimePowerShell.PROMPT));
        Assert.That(RuntimePowerShell.Commands().Count(command => command.StartsWith(RuntimePowerShell.PWSH, StringComparison.Ordinal)), Is.EqualTo(3));
    }

    #endregion
}
