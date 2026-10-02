using WitOS.Dev.Host;
using WitOS.Dev.Images;
using WitOS.Dev.Tests.Support;

namespace WitOS.Dev.Tests.Native;

/// <summary>
/// Export parser on guarded inputs and comparisons with LoadLibrary/GetProcAddress (hosted).
/// </summary>
[TestFixture]
public sealed class NativeLibraryTests
{
    #region Functions

    [Test]
    public Task NativeLibraryExportsTest() => RunAsync(TestEnvironment.Root, TestEnvironment.Scratch());

    #endregion

    #region Tools

    private static async Task RunAsync(string root, string output)
    {
        var msvc = await Toolchain.FindMsvcAsync(root);
        var vc = Path.GetFullPath(Path.Combine(msvc, "../../.."));
        var sdk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Windows Kits/10");
        var version = Directory.GetParent(Toolchain.FindWindowsSdkLibrary("kernel32.lib"))!.Parent!.Parent!.Name;
        var fixture = await NativeLibraryImage.BuildAsync(root, output, msvc);
        var exe = Path.Combine(output, "native-library.exe");
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "cl.exe"), ["/nologo","/MD","/TC","/std:c17","/W4","/WX","/O2",
            "/I"+Path.Combine(vc,"include"),"/I"+Path.Combine(sdk,"Include",version,"ucrt"),"/I"+Path.Combine(sdk,"Include",version,"um"),"/I"+Path.Combine(sdk,"Include",version,"shared"),"/I"+Path.Combine(root,"src/Runtime.Native"),"/I"+Path.Combine(root,"src/Kernel/include"),
            "/Fo"+output+"/","/Fe"+exe,Path.Combine(root,"src/Kernel/pe.c"),Path.Combine(root,"src/Kernel/pe_imports.c"),Path.Combine(root,"src/Kernel/pe_exports.c"),Path.Combine(root,"tests/WitOS.Dev.Tests/Native/NativeLibrary.c"),
            "/link","/LIBPATH:"+Path.Combine(vc,"lib/x64"),"/LIBPATH:"+Path.Combine(sdk,"Lib",version,"ucrt/x64"),
            "/LIBPATH:"+Path.Combine(sdk,"Lib",version,"um/x64"),"kernel32.lib"], root);
        var run = await Processes.RunAsync(exe, [fixture], output, 30);
        await File.WriteAllTextAsync(Path.Combine(output, "native-library.log"), run.Output + run.Error);
        if (run.TimedOut || run.ExitCode != 0 || !run.Output.StartsWith("PASS: ", StringComparison.Ordinal))
            throw new InvalidDataException("Native library cases failed: " + run.Output + run.Error);
        Console.Write(run.Output);
    }

    #endregion
}
