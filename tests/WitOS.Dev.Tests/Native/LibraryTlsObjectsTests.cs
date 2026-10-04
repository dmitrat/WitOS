using System.Security.Cryptography;
using System.Text.Json;
using WitOS.Dev.Host;
using WitOS.Dev.Images;
using WitOS.Dev.Tests.Support;

namespace WitOS.Dev.Tests.Native;

/// <summary>
/// Windows reference for C++ thread_local objects in a DLL: the WitOS dynamic TLS support against the MSVC CRT (hosted).
/// </summary>
[TestFixture]
public sealed class LibraryTlsObjectsTests
{
    #region Functions

    [Test]
    public async Task WindowsThreadLocalObjectOrderTest()
    {
        var root = TestEnvironment.Root;
        var output = TestEnvironment.Scratch();
        var msvc = await Toolchain.FindMsvcAsync(root);
        var vc = Path.GetFullPath(Path.Combine(msvc, "../../.."));
        var sdk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Windows Kits/10");
        var version = Directory.GetParent(Toolchain.FindWindowsSdkLibrary("kernel32.lib"))!.Parent!.Parent!.Name;
        string[] includes = ["/I" + Path.Combine(vc, "include"), "/I" + Path.Combine(sdk, "Include", version, "ucrt"),
            "/I" + Path.Combine(sdk, "Include", version, "um"), "/I" + Path.Combine(sdk, "Include", version, "shared")];
        string[] libraries = ["/LIBPATH:" + Path.Combine(vc, "lib/x64"), "/LIBPATH:" + Path.Combine(sdk, "Lib", version, "ucrt/x64"),
            "/LIBPATH:" + Path.Combine(sdk, "Lib", version, "um/x64")];
        var (sink, _, _) = await NativeTlsCallbackLibraryImage.BuildAsync(root, output, msvc);
        var library = await NativeTlsCallbackLibraryImage.BuildObjectsAsync(root, output, msvc);
        // The same objects with the CRT's own dynamic TLS support and DLL entry.
        var crt = Path.Combine(output, "tlsobjects-crt.dll");
        var objects = Path.Combine(root, "tests/User.X64/library_tls_objects.cpp");
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "cl.exe"), ["/nologo", "/LD", "/MD", "/TP", "/std:c++17",
            "/W4", "/WX", "/O2", "/GR-", "/EHs-c-", .. includes, "/Fo" + Path.Combine(output, "tlsobjects-crt.obj"), "/Fe" + crt,
            objects, "/link", .. libraries, Path.Combine(output, "tlssink.lib")], root);
        var exe = Path.Combine(output, "dll-tls-objects.exe");
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "cl.exe"), ["/nologo", "/MD", "/TC", "/std:c17", "/W4", "/WX",
            "/O2", .. includes, "/Fo" + output + "/", "/Fe" + exe, Path.Combine(root, "tests/WitOS.Dev.Tests/Native/LibraryTlsObjects.c"),
            "/link", .. libraries, "kernel32.lib"], root);
        async Task<string> Trace(string dll, string name)
        {
            var run = await Processes.RunAsync(exe, [sink, dll], output, 90);
            await File.WriteAllTextAsync(Path.Combine(output, name + ".log"), run.Output + run.Error);
            Assert.That(run.TimedOut || run.ExitCode != 0, Is.False, run.Output + run.Error);
            return run.Output.Split('\n').Select(line => line.TrimEnd('\r'))
                .Single(line => line.StartsWith("TRACE: ", StringComparison.Ordinal))["TRACE: ".Length..];
        }
        var trace = await Trace(library, "dll-tls-objects");
        Assert.That(trace, Is.EqualTo(NativeTlsCallbackLibraryImage.WINDOWS_OBJECTS_ORDER));
        var crtTrace = await Trace(crt, "dll-tls-objects-crt");
        Assert.That(crtTrace, Is.EqualTo(NativeTlsCallbackLibraryImage.WINDOWS_OBJECTS_ORDER));
        string Hash(string file) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant();
        await File.WriteAllTextAsync(Path.Combine(output, "dll-tls-objects.json"), JsonSerializer.Serialize(new
        {
            hostOnly = true,
            guestExecuted = false,
            trace,
            crtTrace,
            sinkSha256 = Hash(sink),
            librarySha256 = Hash(library),
            sources = new[] { "tests/User.X64/library_tls_sink.c", "tests/User.X64/library_tls_objects.cpp",
                    "src/Runtime.Native/library_dynamic_tls.cpp", "tests/WitOS.Dev.Tests/Native/LibraryTlsObjects.c" }
                .ToDictionary(file => file, file => Hash(Path.Combine(root, file)))
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    [Test]
    public async Task DynamicTlsBoundsTest()
    {
        const int FAST_FAIL = unchecked((int)0xC0000409);
        var root = TestEnvironment.Root;
        var output = TestEnvironment.Scratch();
        var msvc = await Toolchain.FindMsvcAsync(root);
        var vc = Path.GetFullPath(Path.Combine(msvc, "../../.."));
        var sdk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Windows Kits/10");
        var version = Directory.GetParent(Toolchain.FindWindowsSdkLibrary("kernel32.lib"))!.Parent!.Parent!.Name;
        await NativeTlsCallbackLibraryImage.BuildAsync(root, output, msvc);
        var exe = Path.Combine(output, "dll-tls-bounds.exe");
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "cl.exe"), ["/nologo", "/MD", "/TC", "/std:c17", "/W4", "/WX",
            "/O2", "/I" + Path.Combine(vc, "include"), "/I" + Path.Combine(sdk, "Include", version, "ucrt"),
            "/I" + Path.Combine(sdk, "Include", version, "um"), "/I" + Path.Combine(sdk, "Include", version, "shared"),
            "/Fo" + output + "/", "/Fe" + exe, Path.Combine(root, "tests/WitOS.Dev.Tests/Native/LibraryTlsBounds.c"),
            "/link", "/LIBPATH:" + Path.Combine(vc, "lib/x64"), "/LIBPATH:" + Path.Combine(sdk, "Lib", version, "ucrt/x64"),
            "/LIBPATH:" + Path.Combine(sdk, "Lib", version, "um/x64"), "kernel32.lib"], root);
        foreach (var initializers in new[] { true, false })
        {
            foreach (var beyond in new[] { false, true })
            {
                var library = await NativeTlsCallbackLibraryImage.BuildBoundsAsync(root, output, msvc, initializers, beyond);
                var run = await Processes.RunAsync(exe, [library], output, 90);
                var description = Path.GetFileName(library) + ": " + run.ExitCode + " " + run.Output + run.Error;
                Assert.That(run.TimedOut, Is.False, description);
                if (beyond)
                {
                    Assert.That(run.ExitCode, Is.EqualTo(FAST_FAIL), description);
                    Assert.That(run.Output, Does.Not.Contain("LIVE:"), description);
                }
                else
                {
                    Assert.That(run.ExitCode, Is.Zero, description);
                    Assert.That(run.Output.Trim(), Is.EqualTo("LIVE: 32"), description);
                }
            }
        }
    }

    #endregion
}
