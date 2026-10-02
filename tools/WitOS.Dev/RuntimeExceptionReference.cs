using System.Security.Cryptography;
using System.Text.Json;
namespace WitOS.Dev;

internal static class RuntimeExceptionReference
{
    public static async Task RunAsync(string root, string msvc)
    {
        var output = Path.Combine(root, "artifacts/runtime-exception-reference");
        Directory.CreateDirectory(output);
        var vc = Path.GetFullPath(Path.Combine(msvc, "..", "..", ".."));
        var kernel = Toolchain.FindWindowsSdkLibrary("kernel32.lib");
        var sdkVersion = Directory.GetParent(kernel)!.Parent!.Parent!.Name;
        var sdk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Windows Kits", "10");
        var source = Path.Combine(root, "tests/Runtime.NativeAot/exception_reference.cpp");
        var executable = Path.Combine(output, "exception_reference.exe");
        var build = await Processes.RunAsync(Path.Combine(msvc, "cl.exe"),
            ["/nologo","/MD","/std:c++17","/O2","/GS","/W4","/WX","/I"+Path.Combine(vc,"include"),
             "/I"+Path.Combine(sdk,"Include",sdkVersion,"ucrt"),"/I"+Path.Combine(sdk,"Include",sdkVersion,"um"),"/I"+Path.Combine(sdk,"Include",sdkVersion,"shared"),
             "/Fo"+Path.Combine(output,"exception_reference.obj"),"/Fe"+executable,source,"/link","/LIBPATH:"+Path.Combine(vc,"lib/x64"),
             "/LIBPATH:"+Path.Combine(sdk,"Lib",sdkVersion,"ucrt/x64"),"/LIBPATH:"+Path.Combine(sdk,"Lib",sdkVersion,"um/x64"),"kernel32.lib"], root, 60);
        await File.WriteAllTextAsync(Path.Combine(output, "build.log"), build.Output + build.Error);
        if (build.ExitCode != 0 || build.TimedOut)
            throw new InvalidOperationException("Windows exception reference build failed.");
        var run = await Processes.RunAsync(executable, [], output, 30);
        await File.WriteAllTextAsync(Path.Combine(output, "reference.log"), run.Output + run.Error);
        if (run.ExitCode != 0 || run.TimedOut || !run.Output.Contains("PASS: Windows VEH ordering", StringComparison.Ordinal) || !run.Output.Contains("PASS: Windows software parameters, nested continuation and noncontinuable search", StringComparison.Ordinal))
            throw new InvalidOperationException("Windows exception reference failed.");
        await File.WriteAllTextAsync(Path.Combine(output, "reference.json"), JsonSerializer.Serialize(new { hostOnly = true, sourceSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source))).ToLowerInvariant(), executableSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(executable))).ToLowerInvariant() }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        Console.WriteLine("[EXCEPTION-REFERENCE-PASS] Windows VEH ordering, payload, continuation and removal (HOSTED only).");
    }
}
