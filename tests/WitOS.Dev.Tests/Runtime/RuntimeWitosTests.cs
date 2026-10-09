using WitOS.Dev.NativeAot;
using WitOS.Dev.Runtime;
using WitOS.Dev.Tests.Support;

namespace WitOS.Dev.Tests.Runtime;

/// <summary>
/// The witos patch set of the Unix-form runtime (plan step R1.1): one patch per pinned upstream file, named by its path,
/// made from the pinned bytes, and measured by area against the FreeBSD and Haiku ports.
/// </summary>
[TestFixture]
public sealed class RuntimeWitosTests
{
    #region Functions

    // The tool's list and the pin name the same files, and every patch is the file's own.
    [Test]
    public void PatchSetMatchesThePinTest()
    {
        var root = TestEnvironment.Root;
        var pin = RuntimeWitos.ReadLock(root);
        Assert.That(pin.Sources.Select(source => source.Path).Order(StringComparer.Ordinal),
            Is.EqualTo(RuntimeWitos.PATCHES.Keys.Order(StringComparer.Ordinal)));
        foreach (var (path, sha256) in pin.Sources)
        {
            var output = RuntimeWitos.PATCHES[path];
            Assert.That(output, Is.EqualTo(path.Replace('/', '.')), $"{path}: a patch is named by its path");
            var patch = UpstreamPatches.Read(root, "runtime", output);
            Assert.That(patch.Source, Is.EqualTo(path));
            Assert.That(patch.Before, Is.EqualTo(sha256), $"{path}: the patch is not made from the pinned bytes");
        }
    }

    // The pin is a commit of a tag, and the budget is the ports' size the plan records.
    [Test]
    public void PinNamesTheReleaseAndTheBudgetTest()
    {
        var pin = RuntimeWitos.ReadLock(TestEnvironment.Root);
        Assert.That(pin.Repository, Is.EqualTo("https://github.com/dotnet/runtime"));
        Assert.That(pin.Tag, Is.EqualTo("v" + pin.Version));
        Assert.That(pin.Commit, Does.Match("^[0-9a-f]{40}$"));
        Assert.That(pin.Budget, Is.EqualTo(new RuntimeBudget(31, 62, 18, 11)));
    }

    [Test]
    public void AreasFollowTheBudgetTest()
    {
        Assert.That(RuntimeWitos.Area("src/coreclr/pal/src/thread/process.cpp"), Is.EqualTo("coreclr"));
        Assert.That(RuntimeWitos.Area("src/native/libs/System.Native/pal_process.c"), Is.EqualTo("nativeLibraries"));
        Assert.That(RuntimeWitos.Area("src/native/corehost/hostmisc/pal.unix.cpp"), Is.EqualTo("hosts"));
        Assert.That(RuntimeWitos.Area("src/libraries/System.Private.CoreLib/src/System/OperatingSystem.cs"), Is.EqualTo("libraries"));
        Assert.That(RuntimeWitos.Area("eng/build.sh"), Is.EqualTo("build"));
    }

    #endregion
}
