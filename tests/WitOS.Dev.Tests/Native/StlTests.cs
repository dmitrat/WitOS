using System.Text.Json.Nodes;
using WitOS.Dev.Host;
using WitOS.Dev.Images;
using WitOS.Dev.Pe;
using WitOS.Dev.Tests.Support;

namespace WitOS.Dev.Tests.Native;

/// <summary>
/// The separately compiled sources of the pinned microsoft/STL with the WitOS C++ runtime and UCRT subset against
/// msvcp140 (hosted): the same scenarios through the STL's headers must trace the same tokens.
/// </summary>
[TestFixture]
public sealed class StlTests
{
    #region Functions

    [Test]
    public async Task PinCoversSourcesAndLicenseTest()
    {
        var pin = await StlSources.ReadPinAsync(TestEnvironment.Root);
        var paths = pin.Files.Select(file => file.Path).ToArray();
        Assert.That(paths, Is.Unique);
        Assert.That(NativeStlImage.SOURCES.Append("LICENSE.txt").Append("NOTICE.txt"), Is.SubsetOf(paths));
        Assert.That(paths, Has.Some.EqualTo("stl/inc/yvals_core.h"));
    }

    [TestCase("tag", "vs-2022-17.13")]
    [TestCase("license", "MIT")]
    [TestCase("revision", "1f6e5b16")]
    [TestCase("path", "stl/../stl/inc/vector")]
    [TestCase("path", "stl\\inc\\vector")]
    [TestCase("sha256", "00")]
    public async Task PinRejectsChangedEntriesTest(string field, string value)
    {
        var root = TestEnvironment.Scratch();
        var lockFile = Path.Combine(root, StlSources.LOCK);
        Directory.CreateDirectory(Path.GetDirectoryName(lockFile)!);
        var pin = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(TestEnvironment.Root, StlSources.LOCK)))!;
        if (field is "path" or "sha256")
            pin["files"]![0]![field] = value;
        else
            pin[field] = value;
        await File.WriteAllTextAsync(lockFile, pin.ToJsonString());
        Assert.ThrowsAsync<InvalidDataException>(() => StlSources.ReadPinAsync(root));
    }

    [Test]
    public async Task PinnedStlMatchesMsvcpTest()
    {
        var root = TestEnvironment.Root;
        var output = TestEnvironment.Scratch();
        var msvc = await Toolchain.FindMsvcAsync(root);
        var reference = await NativeStlImage.BuildReferenceAsync(root, output, msvc);
        var witos = await NativeStlImage.BuildWindowsAsync(root, output, msvc);
        async Task<ProcessResult> Run(string exe)
        {
            var run = await Processes.RunAsync(exe, [], output, 90);
            await File.WriteAllTextAsync(Path.ChangeExtension(exe, ".log"), run.Output + run.Error);
            Assert.That(run.TimedOut || run.ExitCode != 0, Is.False, $"{Path.GetFileName(exe)}: {run.ExitCode} {run.Output}");
            return run;
        }
        var expected = "TRACE: " + NativeStlImage.WINDOWS_TRACE + "\r\n";
        Assert.That((await Run(reference)).Output, Is.EqualTo(expected));
        Assert.That((await Run(witos)).Output, Is.EqualTo(expected));
        // No Visual C++ or C runtime library: kernel32 is the only import.
        var imports = NativeImports.Inspect(witos);
        Assert.That(imports.DirectImports.Select(import => import.Library.ToUpperInvariant()), Is.EqualTo(new[] { "KERNEL32.DLL" }));
        Assert.That(imports.DelayImports, Is.Empty);
    }

    #endregion
}
