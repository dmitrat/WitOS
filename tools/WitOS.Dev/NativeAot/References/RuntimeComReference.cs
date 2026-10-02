using System.Security.Cryptography;
using System.Text.Json;
using WitOS.Dev.Host;
namespace WitOS.Dev.NativeAot.References;

internal static class RuntimeComReference
{
    #region Functions

    public static async Task RunAsync(string root, string msvc)
    {
        var output = Path.Combine(root, "artifacts", "runtime-com");
        Directory.CreateDirectory(output);
        var vc = Path.GetFullPath(Path.Combine(msvc, "..", "..", ".."));
        var kernel = Toolchain.FindWindowsSdkLibrary("kernel32.lib");
        var sdkVersion = Directory.GetParent(kernel)!.Parent!.Parent!.Name;
        var sdk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Windows Kits", "10");
        string[] sources = ["tests/Runtime.NativeAot/com_reference.cpp"];
        var executable = Path.Combine(output, "com_reference.exe");
        var compile = await Processes.RunAsync(Path.Combine(msvc, "cl.exe"),
            ["/nologo","/MD","/EHsc","/std:c++17","/O2","/W4","/WX",
             "/I"+Path.Combine(vc,"include"),"/I"+Path.Combine(sdk,"Include",sdkVersion,"ucrt"),
             "/I"+Path.Combine(sdk,"Include",sdkVersion,"um"),"/I"+Path.Combine(sdk,"Include",sdkVersion,"shared"),
             "/I"+Path.Combine(root,"src/Runtime.NativeAot"),"/Fo"+output+"/","/Fe"+executable,
             ..sources.Select(p=>Path.Combine(root,p)),"/link","/manifest:no","/LIBPATH:"+Path.Combine(vc,"lib/x64"),
             "/LIBPATH:"+Path.Combine(sdk,"Lib",sdkVersion,"ucrt/x64"),"/LIBPATH:"+Path.Combine(sdk,"Lib",sdkVersion,"um/x64"),"kernel32.lib","ole32.lib"], root, 120);
        await File.WriteAllTextAsync(Path.Combine(output, "build.log"), compile.Output + compile.Error);
        if (compile.ExitCode != 0 || compile.TimedOut)
            throw new InvalidOperationException("COM reference build failed; see runtime-com/build.log.");
        var run = await Processes.RunAsync(executable, [], output, 60);
        await File.WriteAllTextAsync(Path.Combine(output, "reference.log"), run.Output + run.Error);
        if (run.ExitCode != 0 || run.TimedOut || !run.Output.Contains("PASS: Windows explicit and implicit MTA lifecycle reference", StringComparison.Ordinal))
            throw new InvalidOperationException("COM reference failed; see runtime-com/reference.log.");
        await File.WriteAllTextAsync(Path.Combine(output, "reference.json"), JsonSerializer.Serialize(new
        {
            hostOnly = true,
            inputs = sources.Select(p => new { file = p, sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(root, p)))).ToLowerInvariant() }),
            scope = "Native Windows explicit/implicit MTA transitions, balancing, changed mode and query outputs. Guest adapter validation is separate."
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        Console.WriteLine("[COM-REFERENCE-PASS] Windows explicit/implicit MTA lifecycle (HOSTED only).");
    }

    #endregion
}
