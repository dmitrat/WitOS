using System.Security.Cryptography;
using System.Text.Json;
using WitOS.Dev.Host;
using WitOS.Dev.Images;
using WitOS.Dev.Tests.Support;

namespace WitOS.Dev.Tests.Native;

/// <summary>
/// Windows reference for the order of PE TLS callbacks and DLL entry calls, built from the guest fixtures (hosted).
/// </summary>
[TestFixture]
[Platform(Include = TestPlatforms.WINDOWS, Reason = TestPlatforms.MSVC)]
public sealed class LibraryTlsCallbacksTests
{
    #region Functions

    [Test]
    public async Task WindowsTlsCallbackOrderTest()
    {
        var root = TestEnvironment.Root;
        var output = TestEnvironment.Scratch();
        var msvc = await Toolchain.FindMsvcAsync(root);
        var vc = Path.GetFullPath(Path.Combine(msvc, "../../.."));
        var sdk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Windows Kits/10");
        var version = Directory.GetParent(Toolchain.FindWindowsSdkLibrary("kernel32.lib"))!.Parent!.Parent!.Name;
        var (sink, library, noEntry) = await NativeTlsCallbackLibraryImage.BuildAsync(root, output, msvc);
        var exe = Path.Combine(output, "dll-tls-callbacks.exe");
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "cl.exe"), ["/nologo", "/MD", "/TC", "/std:c17", "/W4", "/WX",
            "/O2", "/I" + Path.Combine(vc, "include"), "/I" + Path.Combine(sdk, "Include", version, "ucrt"),
            "/I" + Path.Combine(sdk, "Include", version, "um"), "/I" + Path.Combine(sdk, "Include", version, "shared"),
            "/Fo" + output + "/", "/Fe" + exe, Path.Combine(root, "tests/WitOS.Dev.Tests/Native/LibraryTlsCallbacks.c"),
            "/link", "/LIBPATH:" + Path.Combine(vc, "lib/x64"), "/LIBPATH:" + Path.Combine(sdk, "Lib", version, "ucrt/x64"),
            "/LIBPATH:" + Path.Combine(sdk, "Lib", version, "um/x64"), "kernel32.lib"], root);
        async Task<string> Trace(string dll, string name)
        {
            var run = await Processes.RunAsync(exe, [sink, dll], output, 90);
            await File.WriteAllTextAsync(Path.Combine(output, name + ".log"), run.Output + run.Error);
            Assert.That(run.TimedOut || run.ExitCode != 0, Is.False, run.Output + run.Error);
            return run.Output.Split('\n').Select(line => line.TrimEnd('\r'))
                .Single(line => line.StartsWith("TRACE: ", StringComparison.Ordinal))["TRACE: ".Length..];
        }
        var trace = await Trace(library, "dll-tls-callbacks");
        Assert.That(trace, Is.EqualTo(NativeTlsCallbackLibraryImage.WINDOWS_ORDER));
        var noEntryTrace = await Trace(noEntry, "dll-tls-noentry");
        Assert.That(noEntryTrace, Is.EqualTo(NativeTlsCallbackLibraryImage.WINDOWS_NOENTRY_ORDER));
        string Hash(string file) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant();
        await File.WriteAllTextAsync(Path.Combine(output, "dll-tls-callbacks.json"), JsonSerializer.Serialize(new
        {
            hostOnly = true,
            guestExecuted = false,
            trace,
            noEntryTrace,
            sinkSha256 = Hash(sink),
            librarySha256 = Hash(library),
            noEntrySha256 = Hash(noEntry),
            sources = new[] { "tests/User.X64/library_tls_sink.c", "tests/User.X64/library_tls_callbacks.c",
                    "tests/WitOS.Dev.Tests/Native/LibraryTlsCallbacks.c" }
                .ToDictionary(file => file, file => Hash(Path.Combine(root, file)))
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    #endregion
}
