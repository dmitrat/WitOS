using System.Text.RegularExpressions;
using WitOS.Dev.Runtime;
using WitOS.Dev.Tests.Support;

namespace WitOS.Dev.Tests.Runtime;

/// <summary>
/// The published applications on witos (plan step N4.4): the manifest a publish writes verifies exactly the bytes it
/// names, the suite's checks are read from a guest's log and compared with a record, and CI publishes on Windows and
/// runs the upload on each ISA.
/// </summary>
[TestFixture]
public sealed class RuntimePublishedTests
{
    #region Tests

    // The manifest names every file with its size and hash; a changed byte, a missing file and an extra file are refused.
    [Test]
    public async Task ManifestVerifiesExactBytesTest()
    {
        var directory = Path.Combine(Path.GetTempPath(), "witos-published-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var (name, _, program) in RuntimePublished.APPLICATIONS)
            {
                Directory.CreateDirectory(Path.Combine(directory, name));
                await File.WriteAllBytesAsync(Path.Combine(directory, name, program), [1, 2, 3, (byte)name.Length]);
            }
            var written = await RuntimePublished.WriteManifestAsync(directory, "Test", "10.0.100");
            Assert.That(written.Files.Select(file => file.Path), Is.EqualTo(new[] { "compatibility/DotNetCompatibility.dll", "portable-app/PortableApp.dll" }));
            Assert.That(written.Files[0].Bytes, Is.EqualTo(4));
            Assert.That(written.Files[0].Sha256, Has.Length.EqualTo(64));

            var verified = await RuntimePublished.VerifyAsync(directory);
            Assert.That(verified.Host, Is.EqualTo("Test"));
            Assert.That(verified.Sdk, Is.EqualTo("10.0.100"));
            Assert.That(verified.Files, Is.EqualTo(written.Files));

            var extra = Path.Combine(directory, "compatibility", "extra.txt");
            await File.WriteAllTextAsync(extra, "x");
            Assert.ThrowsAsync<InvalidDataException>(() => RuntimePublished.VerifyAsync(directory));
            File.Delete(extra);

            var program0 = Path.Combine(directory, "compatibility", "DotNetCompatibility.dll");
            await File.WriteAllBytesAsync(program0, [1, 2, 3, 5]);
            Assert.ThrowsAsync<InvalidDataException>(() => RuntimePublished.VerifyAsync(directory));
            await File.WriteAllBytesAsync(program0, [1, 2, 3, 4, 5]);
            Assert.ThrowsAsync<InvalidDataException>(() => RuntimePublished.VerifyAsync(directory));
            File.Delete(program0);
            Assert.ThrowsAsync<InvalidDataException>(() => RuntimePublished.VerifyAsync(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // A check's verdict is its line's; the summary line is no check; and two runs are compared check by check.
    [Test]
    public void ChecksAreReadAndComparedTest()
    {
        var checks = RuntimeCompatibility.ReadChecks(
        [
            "[USER] [N1] Tasks.WhenAll: pass (8 tasks)",
            "[USER] [N1] FileIO.Watcher: fail (threw PlatformNotSupportedException: x); recorded difference: no notifications",
            "[N1] Terminal.ReadLine: not applicable (the input is not a terminal)",
            "[USER] [N1] .NET compatibility on witos (X64): 88 checks, 2 recorded differences, 0 unexpected, 0 not applicable",
            "[USER] [APPS-INIT] /dotnet/dotnet /app/compatibility/DotNetCompatibility.dll exited with 0 after 30 s, 300 MiB free"
        ]);
        Assert.That(checks, Is.EqualTo(new Dictionary<string, string>
        {
            ["Tasks.WhenAll"] = "pass",
            ["FileIO.Watcher"] = "fail",
            ["Terminal.ReadLine"] = "not applicable"
        }));

        Assert.That(RuntimePublished.Compare(checks, checks), Is.Empty);
        var other = new Dictionary<string, string> { ["Tasks.WhenAll"] = "fail", ["Terminal.ReadLine"] = "not applicable", ["Platform.New"] = "pass" };
        Assert.That(RuntimePublished.Compare(checks, other), Is.EqualTo(new[]
        {
            "FileIO.Watcher: fail recorded, not run", "Tasks.WhenAll: pass recorded, fail now", "Platform.New: pass now, not recorded"
        }));
    }

    // CI publishes on Windows, and a job per ISA runs that upload on the install the linux-host leg built, with the
    // record of the suite's run that leg uploads.
    [Test]
    public void CiPublishesOnWindowsAndRunsItOnWitOsTest()
    {
        var workflow = File.ReadAllText(Path.Combine(TestEnvironment.Root, ".github", "workflows", "kernel.yml"));
        var publish = Regex.Match(workflow, @"^  windows-publish:\n(?<body>(?:    .*\n|\n)+)", RegexOptions.Multiline);
        Assert.That(publish.Success, Is.True, "kernel.yml has no windows-publish job");
        Assert.That(publish.Groups["body"].Value, Does.Contain("runs-on: windows-").And.Contain("publish-apps --output artifacts/published-apps")
            .And.Contain("name: witos-published-apps-windows"));

        var run = Regex.Match(workflow, @"^  published-on-windows:\n(?<body>(?:    .*\n|\n)+)", RegexOptions.Multiline);
        Assert.That(run.Success, Is.True, "kernel.yml has no published-on-windows job");
        Assert.That(run.Groups["body"].Value, Does.Contain("needs: [linux-host, windows-publish]").And.Contain("arch: [x64, arm64]")
            .And.Contain("name: witos-published-apps-windows").And.Contain("name: witos-runtime-install-${{ matrix.arch }}")
            .And.Contain("runtime-published --arch ${{ matrix.arch }} --published artifacts/published-apps"));

        var upload = Regex.Match(workflow, @"name: witos-runtime-install-\$\{\{ matrix\.arch \}\}\n(?<body>(?:          .*\n)+)");
        Assert.That(upload.Success, Is.True, "linux-host uploads no install");
        Assert.That(upload.Groups["body"].Value, Does.Contain("artifacts/runtime-witos/compatibility-${{ matrix.arch }}.json"));
    }

    #endregion
}
