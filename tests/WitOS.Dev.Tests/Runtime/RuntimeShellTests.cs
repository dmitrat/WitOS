using WitOS.Dev.Commands;
using WitOS.Dev.Kernel;

namespace WitOS.Dev.Tests.Runtime;

/// <summary>
/// The shell command's options (plan steps C1.4 and C2.4): a session on the console, in a window, prepared on one host and
/// booted on another from its image and disk.
/// </summary>
[TestFixture]
public sealed class RuntimeShellTests
{
    #region Functions

    [Test]
    public void ConsoleSessionIsTheDefaultTest()
    {
        var (architecture, options) = CommandShell.Parse([]);
        Assert.That(architecture, Is.EqualTo(KernelArchitecture.X64));
        Assert.That(options.Window, Is.False);
        Assert.That(options.Prepare, Is.Null);
        Assert.That(options.Image, Is.Null);
        Assert.That(options.MemoryMiB, Is.EqualTo(1024));
    }

    [Test]
    public void WindowIsAFlagAmongValuesTest()
    {
        var (architecture, options) = CommandShell.Parse(["--arch", "arm64", "--window", "--disk", "data.img", "--prepare", "session"]);
        Assert.That(architecture, Is.EqualTo(KernelArchitecture.Arm64));
        Assert.That(options.Window, Is.True);
        Assert.That(options.Disk, Is.EqualTo("data.img"));
        Assert.That(options.Prepare, Is.EqualTo("session"));
    }

    [Test]
    public void PreparedImageBootsWithItsDiskAloneTest()
    {
        var (_, options) = CommandShell.Parse(["--image", "WitOS-x64-shell.img", "--disk", "data-x64.img", "--window"]);
        Assert.That(options.Image, Is.EqualTo("WitOS-x64-shell.img"));
        Assert.That(options.Window, Is.True);
        Assert.Throws<ArgumentException>(() => CommandShell.Parse(["--image", "a.img"]));
        Assert.Throws<ArgumentException>(() => CommandShell.Parse(["--image", "a.img", "--disk", "d.img", "--publish", "app"]));
        Assert.Throws<ArgumentException>(() => CommandShell.Parse(["--image", "a.img", "--disk", "d.img", "--prepare", "out"]));
    }

    [Test]
    public void UnknownOrIncompleteOptionsAreRefusedTest()
    {
        Assert.Throws<ArgumentException>(() => CommandShell.Parse(["--disk"]));
        Assert.Throws<ArgumentException>(() => CommandShell.Parse(["--screen", "on"]));
    }

    #endregion
}
