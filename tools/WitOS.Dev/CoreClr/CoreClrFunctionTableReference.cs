using WitOS.Dev.Host;

namespace WitOS.Dev.CoreClr;

/// <summary>
/// Compares WitOS dynamic function-table registration and target unwind with the Windows implementation (hosted).
/// </summary>
internal static class CoreClrFunctionTableReference
{
    #region Functions

    /// <summary>
    /// Builds and runs the hosted function-table comparison.
    /// </summary>
    /// <param name="root">Repository root.</param>
    internal static async Task RunAsync(string root)
    {
        var output = Path.Combine(root, "artifacts/coreclr-function-tables");
        Directory.CreateDirectory(output);
        var msvc = await Toolchain.FindMsvcAsync(root);
        var vc = Path.GetFullPath(Path.Combine(msvc, "../../.."));
        var sdk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Windows Kits/10");
        var version = Directory.GetParent(Toolchain.FindWindowsSdkLibrary("kernel32.lib"))!.Parent!.Parent!.Name;
        var exe = Path.Combine(output, "function-tables.exe");
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "cl.exe"), ["/nologo","/MD","/EHsc","/std:c++17","/O2","/W4","/WX",
            "/I"+Path.Combine(vc,"include"),"/I"+Path.Combine(sdk,"Include",version,"ucrt"),"/I"+Path.Combine(sdk,"Include",version,"um"),
            "/I"+Path.Combine(sdk,"Include",version,"shared"),"/I"+Path.Combine(root,"src/Runtime.CoreClr"),"/Fo"+output+"/","/Fe"+exe,
            Path.Combine(root,"src/Runtime.CoreClr/function_tables.witos.cpp"),Path.Combine(root,"tests/Runtime.NativeAot/coreclr_function_tables.cpp"),
            "/link","/LIBPATH:"+Path.Combine(vc,"lib/x64"),"/LIBPATH:"+Path.Combine(sdk,"Lib",version,"ucrt/x64"),"/LIBPATH:"+Path.Combine(sdk,"Lib",version,"um/x64"),"kernel32.lib"], root);
        var run = await Processes.RunAsync(exe, [], root, 30);
        await File.WriteAllTextAsync(Path.Combine(output, "reference.log"), run.Output + run.Error);
        if (run.ExitCode != 0 || run.TimedOut || !run.Output.StartsWith("PASS: dynamic registry", StringComparison.Ordinal))
            throw new InvalidDataException("Function-table reference failed: " + run.Output + run.Error);
        Console.Write(run.Output);
        var frame = Path.Combine(output, "target-frame.obj");
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "ml64.exe"),
            ["/nologo", "/c", "/Fo" + frame, Path.Combine(root, "tests/User.X64/coreclr_jit_frame.asm")], root);
        var target = Path.Combine(output, "target-unwind.exe");
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "cl.exe"),
            ["/nologo", "/MD", "/EHsc", "/std:c++17", "/O2", "/W4", "/WX",
             "/I" + Path.Combine(vc, "include"), "/I" + Path.Combine(sdk, "Include", version, "ucrt"),
             "/I" + Path.Combine(sdk, "Include", version, "um"), "/I" + Path.Combine(sdk, "Include", version, "shared"),
             "/Fo" + output + "/", "/Fe" + target, Path.Combine(root, "tests/Runtime.NativeAot/coreclr_target_reference.cpp"), frame,
             "/link", "/LIBPATH:" + Path.Combine(vc, "lib/x64"), "/LIBPATH:" + Path.Combine(sdk, "Lib", version, "ucrt/x64"),
             "/LIBPATH:" + Path.Combine(sdk, "Lib", version, "um/x64"), "kernel32.lib"], root);
        var targetRun = await Processes.RunAsync(target, [], root, 30);
        await File.WriteAllTextAsync(Path.Combine(output, "target-reference.log"), targetRun.Output + targetRun.Error);
        if (targetRun.ExitCode != 0 || targetRun.TimedOut ||
            !targetRun.Output.Contains("PASS: Windows target unwind and CoreCLR collided dispatcher tuple", StringComparison.Ordinal))
            throw new InvalidDataException("Target-unwind reference failed: " + targetRun.ExitCode + " " + targetRun.Output + targetRun.Error);
        Console.Write(targetRun.Output);
    }

    #endregion
}
