using WitOS.Dev.Host;
using WitOS.Dev.Tests.Support;

namespace WitOS.Dev.Tests.Native;

/// <summary>
/// Native directory enumeration and pattern matching (hosted).
/// </summary>
[TestFixture]
public sealed class NativeDirectoryTests
{
    #region Functions

    [Test]
    public Task NativeDirectoryEnumerationTest() => RunAsync(TestEnvironment.Root, TestEnvironment.Scratch());

    #endregion

    #region Tools

    private static async Task RunAsync(string root, string output)
    {
        var msvc = await Toolchain.FindMsvcAsync(root);
        var vc = Path.GetFullPath(Path.Combine(msvc, "../../.."));
        var sdk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Windows Kits/10");
        var version = Directory.GetParent(Toolchain.FindWindowsSdkLibrary("kernel32.lib"))!.Parent!.Parent!.Name;
        var exe = Path.Combine(output, "native-directory.exe");
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "cl.exe"), ["/nologo","/MD","/TC","/std:c17","/W4","/WX","/O2","/DFILE_VIEW_PAL_TEST",
            "/I"+Path.Combine(vc,"include"),"/I"+Path.Combine(sdk,"Include",version,"ucrt"),"/I"+Path.Combine(sdk,"Include",version,"um"),"/I"+Path.Combine(sdk,"Include",version,"shared"),"/I"+Path.Combine(root,"src/System.Native"),"/I"+Path.Combine(root,"src/Kernel/include"),
            "/Fo"+output+"/","/Fe"+exe,Path.Combine(root,"src/System.Native/path.c"),Path.Combine(root,"src/System.Native/current_directory.c"),Path.Combine(root,"src/System.Native/file.c"),Path.Combine(root,"src/System.Native/directory.c"),Path.Combine(root,"tests/WitOS.Dev.Tests/Native/FileViewFaults.c"),Path.Combine(root,"tests/WitOS.Dev.Tests/Native/NativeDirectory.c"),
            "/link","/LIBPATH:"+Path.Combine(vc,"lib/x64"),"/LIBPATH:"+Path.Combine(sdk,"Lib",version,"ucrt/x64"),
            "/LIBPATH:"+Path.Combine(sdk,"Lib",version,"um/x64"),"kernel32.lib"], root);
        var run = await Processes.RunAsync(exe, [], output, 30);
        await File.WriteAllTextAsync(Path.Combine(output, "native-directory.log"), run.Output + run.Error);
        if (run.TimedOut || run.ExitCode != 0 || !run.Output.StartsWith("PASS: ", StringComparison.Ordinal))
            throw new InvalidDataException("Native directory cases failed: " + run.Output + run.Error);
        Console.Write(run.Output);
    }

    #endregion
}
