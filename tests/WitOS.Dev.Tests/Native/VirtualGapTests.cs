using WitOS.Dev.Host;
using WitOS.Dev.Tests.Support;

namespace WitOS.Dev.Tests.Native;

/// <summary>
/// Virtual gap property cases of the common kernel (hosted).
/// </summary>
[TestFixture]
public sealed class VirtualGapTests
{
    #region Functions

    [Test]
    [Category(TestCategories.PE)]
    [Explicit("Needs the runtime-source image")]
    public Task VirtualGapPropertyCasesTest() => RunAsync(TestEnvironment.Root, TestEnvironment.Scratch());

    #endregion

    #region Tools

    private static async Task RunAsync(string root, string output)
    {
        var msvc = await Toolchain.FindMsvcAsync(root);
        var vc = Path.GetFullPath(Path.Combine(msvc, "../../.."));
        var sdk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Windows Kits/10");
        var version = Directory.GetParent(Toolchain.FindWindowsSdkLibrary("kernel32.lib"))!.Parent!.Parent!.Name;
        var exe = Path.Combine(output, "virtual-gap.exe");
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "cl.exe"), ["/nologo","/MD","/TC","/std:c17","/W4","/WX","/O2",
            "/I"+Path.Combine(vc,"include"),"/I"+Path.Combine(sdk,"Include",version,"ucrt"),"/I"+Path.Combine(root,"src/Kernel/include"),
            "/Fo"+output+"/","/Fe"+exe,Path.Combine(root,"src/Kernel/virtual_gap.c"),Path.Combine(root,"tests/WitOS.Dev.Tests/Native/VirtualGap.c"),
            "/link","/LIBPATH:"+Path.Combine(vc,"lib/x64"),"/LIBPATH:"+Path.Combine(sdk,"Lib",version,"ucrt/x64"),
            "/LIBPATH:"+Path.Combine(sdk,"Lib",version,"um/x64")], root);
        var run = await Processes.RunAsync(exe, [], output, 30);
        await File.WriteAllTextAsync(Path.Combine(output, "virtual-gap.log"), run.Output + run.Error);
        if (run.TimedOut || run.ExitCode != 0 || !run.Output.StartsWith("PASS: ", StringComparison.Ordinal))
            throw new InvalidDataException("Virtual gap property cases failed: " + run.Output + run.Error);
        Console.Write(run.Output);
    }

    #endregion
}
