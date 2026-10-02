using System.Security.Cryptography;
using System.Text.Json;
namespace WitOS.Dev;

internal static class RuntimeGpReference
{
    public static async Task RunAsync(string root, string msvc)
    {
        var output = Path.Combine(root, "artifacts/runtime-gp-reference");
        Directory.CreateDirectory(output);
        var vc = Path.GetFullPath(Path.Combine(msvc, "..", "..", ".."));
        var kernel = Toolchain.FindWindowsSdkLibrary("kernel32.lib");
        var sdkVersion = Directory.GetParent(kernel)!.Parent!.Parent!.Name;
        var sdk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Windows Kits", "10");
        var source = Path.Combine(root, "tests/Runtime.NativeAot/gp_reference.cpp");
        var executable = Path.Combine(output, "gp_reference.exe");
        var assembly = Path.Combine(root, "src/Kernel.Arch.X64/gp_reference_fixture.asm");
        var obj = Path.Combine(output, "gp_reference_fixture.obj");
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "ml64.exe"), ["/nologo", "/c", "/Fo" + obj, assembly], root);
        var build = await Processes.RunAsync(Path.Combine(msvc, "cl.exe"),
            ["/nologo","/MD","/std:c++17","/O2","/GS","/W4","/WX","/I"+Path.Combine(vc,"include"),
             "/I"+Path.Combine(sdk,"Include",sdkVersion,"ucrt"),"/I"+Path.Combine(sdk,"Include",sdkVersion,"um"),"/I"+Path.Combine(sdk,"Include",sdkVersion,"shared"),
             "/I"+Path.Combine(root,"src/System.Native"),"/I"+Path.Combine(root,"src/Kernel/include"),"/Fo"+output+"/","/Fe"+executable,source,Path.Combine(root,"src/Kernel.Arch.X64/native_exception_x64.cpp"),obj,"/link","/LIBPATH:"+Path.Combine(vc,"lib/x64"),
             "/LIBPATH:"+Path.Combine(sdk,"Lib",sdkVersion,"ucrt/x64"),"/LIBPATH:"+Path.Combine(sdk,"Lib",sdkVersion,"um/x64"),"kernel32.lib"], root, 60);
        await File.WriteAllTextAsync(Path.Combine(output, "build.log"), build.Output + build.Error);
        if (build.ExitCode != 0 || build.TimedOut)
            throw new InvalidOperationException("Windows exception reference build failed.");
        var run = await Processes.RunAsync(executable, [], output, 30);
        await File.WriteAllTextAsync(Path.Combine(output, "reference.log"), run.Output + run.Error);
        if (run.ExitCode != 0 || run.TimedOut || !run.Output.Contains("PASS: 6 Windows x64 GP translations and bounded decoder", StringComparison.Ordinal))
            throw new InvalidOperationException("Windows exception reference failed.");
        await File.WriteAllTextAsync(Path.Combine(output, "reference.json"), JsonSerializer.Serialize(new { hostOnly = true, decoderSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(root, "src/Kernel.Arch.X64/native_exception_x64.cpp")))).ToLowerInvariant(), assemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly))).ToLowerInvariant(), sourceSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source))).ToLowerInvariant(), executableSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(executable))).ToLowerInvariant() }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        Console.WriteLine("[GP-REFERENCE-PASS] Windows selector, privileged instruction, noncanonical access and SIMD alignment faults (HOSTED only).");
    }
}
