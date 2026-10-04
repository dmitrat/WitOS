using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using WitOS.Dev.Host;
using WitOS.Dev.Images;
using WitOS.Dev.Pe;
using WitOS.Dev.Tests.Support;

namespace WitOS.Dev.Tests.Native;

/// <summary>
/// The WitOS UCRT subset against UCRT (hosted): the scenarios through the public functions and the inline functions
/// of UCRT's headers, and the subset's functions next to UCRT's in one process.
/// </summary>
[TestFixture]
public sealed class CrtTests
{
    #region Constants

    private static readonly Regex DIFFERENTIAL_RESULT =
        new(@"^PASS: (?<compared>\d+) compared, (?<skipped>\d+) skipped as invalid, 0 failed\r?$", RegexOptions.Multiline);

    #endregion

    #region Functions

    [Test]
    public async Task WitOsSubsetMatchesUcrtTest()
    {
        var root = TestEnvironment.Root;
        var output = TestEnvironment.Scratch();
        var msvc = await Toolchain.FindMsvcAsync(root);
        var reference = await NativeCrtImage.BuildReferenceAsync(root, output, msvc);
        var witos = await NativeCrtImage.BuildWindowsAsync(root, output, msvc);
        async Task<ProcessResult> Run(string exe)
        {
            var run = await Processes.RunAsync(exe, [], output, 90);
            await File.WriteAllTextAsync(Path.ChangeExtension(exe, ".log"), run.Output + run.Error);
            Assert.That(run.TimedOut || run.ExitCode != 0, Is.False, $"{Path.GetFileName(exe)}: {run.ExitCode} {run.Output}");
            return run;
        }
        var ucrt = await Run(reference);
        var subset = await Run(witos);
        var expected = NativeCrtImage.STANDARD_OUTPUT + "TRACE: " + NativeCrtImage.WINDOWS_TRACE + "\r\n";
        Assert.That(ucrt.Output, Is.EqualTo(expected));
        Assert.That(ucrt.Error, Is.EqualTo(NativeCrtImage.STANDARD_ERROR));
        Assert.That(subset.Output, Is.EqualTo(expected));
        Assert.That(subset.Error, Is.EqualTo(NativeCrtImage.STANDARD_ERROR));
        // Every C runtime function the subset build calls is its own: kernel32 is its only import.
        var imports = NativeImports.Inspect(witos).DirectImports.Select(import => import.Library.ToUpperInvariant()).ToArray();
        Assert.That(imports, Is.EqualTo(new[] { "KERNEL32.DLL" }));
        Assert.That(NativeImports.Inspect(witos).DelayImports, Is.Empty);
    }

    [Test]
    public async Task WitOsSubsetMatchesUcrtDifferentiallyTest()
    {
        var root = TestEnvironment.Root;
        var output = TestEnvironment.Scratch();
        var msvc = await Toolchain.FindMsvcAsync(root);
        var exe = await NativeCrtImage.BuildDifferentialAsync(root, output, msvc);
        var files = Path.Combine(output, "crt-files");
        Directory.CreateDirectory(files);
        // --verbose names each print case on standard error, so a crash shows the case it was in.
        var run = await Processes.RunAsync(exe, [files, "--verbose"], output, 300);
        await File.WriteAllTextAsync(Path.Combine(output, "crt-differential.log"), run.Output + run.Error);
        var lastCases = string.Join("\n", run.Error.Split('\n').TakeLast(3));
        Assert.That(run.TimedOut || run.ExitCode != 0, Is.False,
            $"crt-differential: {run.ExitCode} {run.Output}\nlast cases:\n{lastCases}");
        var result = DIFFERENTIAL_RESULT.Match(run.Output);
        Assert.That(result.Success, Is.True, run.Output);
        var compared = long.Parse(result.Groups["compared"].Value);
        Assert.That(compared, Is.GreaterThan(8_000_000));
        string Hash(string file) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant();
        await File.WriteAllTextAsync(Path.Combine(output, "crt-differential.json"), JsonSerializer.Serialize(new
        {
            hostOnly = true,
            guestExecuted = false,
            compared,
            skippedAsInvalid = long.Parse(result.Groups["skipped"].Value),
            sources = new[] { NativeCrtImage.DIFFERENTIAL, NativeCrtImage.WINDOWS_PLATFORM }.Concat(NativeCrtImage.RUNTIME)
                .ToDictionary(file => file, file => Hash(Path.Combine(root, file)))
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    #endregion
}
