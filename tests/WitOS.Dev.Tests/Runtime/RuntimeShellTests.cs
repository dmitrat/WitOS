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
        var (_, options) = CommandShell.Parse(["--image", "WitOS-x64-shell.img", "--disk", "data-x64.img", "--window", "--accel"]);
        Assert.That(options.Image, Is.EqualTo("WitOS-x64-shell.img"));
        Assert.That(options.Window, Is.True);
        Assert.That(options.Accelerated, Is.True, "--accel is a flag (C2.7)");
        Assert.That(CommandShell.Parse([]).Options.Accelerated, Is.False);
        Assert.Throws<ArgumentException>(() => CommandShell.Parse(["--image", "a.img"]));
        Assert.Throws<ArgumentException>(() => CommandShell.Parse(["--image", "a.img", "--disk", "d.img", "--publish", "app"]));
        Assert.Throws<ArgumentException>(() => CommandShell.Parse(["--image", "a.img", "--disk", "d.img", "--prepare", "out"]));
    }

    // C2.7: the host's accelerator for a guest of its own ISA alone.
    [Test]
    public void HostAcceleratorServesTheHostsIsaTest()
    {
        var host = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture;
        var native = host == System.Runtime.InteropServices.Architecture.Arm64 ? KernelArchitecture.Arm64 : KernelArchitecture.X64;
        var foreign = native == KernelArchitecture.X64 ? KernelArchitecture.Arm64 : KernelArchitecture.X64;
        Assert.Throws<NotSupportedException>(() => BootScenarioRunner.HostAccelerator(foreign), "a guest of another ISA stays on TCG");
        Assert.Throws<NotSupportedException>(() => BootScenarioRunner.HostAccelerator(KernelArchitecture.Riscv64));
        if (OperatingSystem.IsWindows())
            Assert.That(BootScenarioRunner.HostAccelerator(native), Is.EqualTo("whpx"));
        else if (OperatingSystem.IsLinux() && File.Exists("/dev/kvm"))
            Assert.That(BootScenarioRunner.HostAccelerator(native), Is.EqualTo("kvm"));
    }

    [Test]
    public void UnknownOrIncompleteOptionsAreRefusedTest()
    {
        Assert.Throws<ArgumentException>(() => CommandShell.Parse(["--disk"]));
        Assert.Throws<ArgumentException>(() => CommandShell.Parse(["--screen", "on"]));
    }

    [Test]
    public void PcDiskIsMadeAnewTest()
    {
        var (_, options) = CommandShell.Parse(["--pc", "--prepare", "vmware"]);
        Assert.That((options.Pc, options.Prepare, options.Disk, options.Image), Is.EqualTo((true, "vmware", (string?)null, (string?)null)));
        Assert.Throws<ArgumentException>(() => CommandShell.Parse(["--pc", "--disk", "data.img"]), "the one disk is made anew");
        Assert.Throws<ArgumentException>(() => CommandShell.Parse(["--pc", "--image", "WitOS.img", "--disk", "data.img"]));
    }

    #endregion
}
