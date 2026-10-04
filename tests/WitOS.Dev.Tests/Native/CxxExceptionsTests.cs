using System.Security.Cryptography;
using System.Text.Json;
using WitOS.Dev.Host;
using WitOS.Dev.Images;
using WitOS.Dev.Tests.Support;

namespace WitOS.Dev.Tests.Native;

/// <summary>
/// C++ exceptions on the WitOS C++ runtime against vcruntime, both on Windows' own dispatcher and unwinder (hosted).
/// </summary>
[TestFixture]
public sealed class CxxExceptionsTests
{
    #region Functions

    [Test]
    public async Task WitOsRuntimeMatchesVcruntimeTest()
    {
        var root = TestEnvironment.Root;
        var output = TestEnvironment.Scratch();
        var msvc = await Toolchain.FindMsvcAsync(root);
        var reference = await NativeCxxExceptionImage.BuildReferenceAsync(root, output, msvc);
        var witos = await NativeCxxExceptionImage.BuildWindowsAsync(root, output, msvc);
        async Task<string> Trace(string exe)
        {
            var run = await Processes.RunAsync(exe, [], output, 90);
            await File.WriteAllTextAsync(Path.ChangeExtension(exe, ".log"), run.Output + run.Error);
            Assert.That(run.TimedOut || run.ExitCode != 0, Is.False, $"{Path.GetFileName(exe)}: {run.ExitCode} {run.Output}");
            return run.Output.Split('\n').Select(line => line.TrimEnd('\r'))
                .Single(line => line.StartsWith("TRACE: ", StringComparison.Ordinal))["TRACE: ".Length..];
        }
        var vcruntime = await Trace(reference);
        Assert.That(vcruntime, Is.EqualTo(NativeCxxExceptionImage.WINDOWS_TRACE));
        var runtime = await Trace(witos);
        Assert.That(runtime, Is.EqualTo(NativeCxxExceptionImage.WINDOWS_TRACE));
        string Hash(string file) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant();
        await File.WriteAllTextAsync(Path.Combine(output, "cxx-exceptions.json"), JsonSerializer.Serialize(new
        {
            hostOnly = true,
            guestExecuted = false,
            vcruntime,
            runtime,
            sources = new[] { "tests/User.X64/cxx_exceptions.cpp", NativeCxxExceptionImage.RUNTIME_SCENARIOS,
                    "tests/WitOS.Dev.Tests/Native/CxxExceptionsHost.cpp", NativeCxxExceptionImage.WINDOWS_PLATFORM,
                    NativeCxxExceptionImage.GUARD }
                .Concat(NativeCxxExceptionImage.RUNTIME).ToDictionary(file => file, file => Hash(Path.Combine(root, file)))
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    #endregion
}
