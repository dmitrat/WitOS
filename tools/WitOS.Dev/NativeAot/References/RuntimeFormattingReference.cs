using System.Security.Cryptography;
using System.Text.Json;
using WitOS.Dev.Host;
namespace WitOS.Dev.NativeAot.References;

/// <summary>
/// Compares the WitOS secure formatting routines with the Windows CRT (hosted).
/// </summary>
internal static class RuntimeFormattingReference
{
    #region Functions

    /// <summary>
    /// Builds and runs the formatting comparison.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="msvc">Directory of the MSVC x64 host tools.</param>
    public static async Task RunAsync(string root, string msvc)
    {
        var output = Path.Combine(root, "artifacts", "runtime-format");
        Directory.CreateDirectory(output);
        var vc = Path.GetFullPath(Path.Combine(msvc, "..", "..", ".."));
        var kernel = Toolchain.FindWindowsSdkLibrary("kernel32.lib");
        var sdkVersion = Directory.GetParent(kernel)!.Parent!.Parent!.Name;
        var sdk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Windows Kits", "10");
        var assembly = Path.Combine(output, "native_format.obj");
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "ml64.exe"), ["/nologo", "/c", "/Fo" + assembly, Path.Combine(root, "src/Runtime.NativeAot/X64/native_format.asm")], root);
        string[] sources = ["tests/Runtime.NativeAot/format_reference.cpp", "src/Runtime.NativeAot/native_format.witos.cpp", "src/Runtime.NativeAot/format_fixed.witos.cpp"];
        var executable = Path.Combine(output, "format_reference.exe");
        var compile = await Processes.RunAsync(Path.Combine(msvc, "cl.exe"),
            ["/nologo","/MD","/EHsc","/std:c++17","/O2","/W4","/WX","/DWITOS_FORMAT_REFERENCE",
             "/I"+Path.Combine(vc,"include"),"/I"+Path.Combine(sdk,"Include",sdkVersion,"ucrt"),"/I"+Path.Combine(root,"src/Runtime.NativeAot"),
             "/Fo"+output+"/","/Fe"+executable,..sources.Select(p=>Path.Combine(root,p)),assembly,
             "/link","/LIBPATH:"+Path.Combine(vc,"lib/x64"),"/LIBPATH:"+Path.Combine(sdk,"Lib",sdkVersion,"ucrt/x64"),"/LIBPATH:"+Path.Combine(sdk,"Lib",sdkVersion,"um/x64")], root, 120);
        await File.WriteAllTextAsync(Path.Combine(output, "build.log"), compile.Output + compile.Error);
        if (compile.ExitCode != 0 || compile.TimedOut)
            throw new InvalidOperationException("Formatting reference build failed; see runtime-format/build.log.");
        var run = await Processes.RunAsync(executable, [], output, 60);
        await File.WriteAllTextAsync(Path.Combine(output, "reference.log"), run.Output + run.Error);
        if (run.ExitCode != 0 || run.TimedOut || !run.Output.Contains("PASS: 49484 secure formatting differential cases", StringComparison.Ordinal))
            throw new InvalidOperationException("Formatting differs from CRT; see runtime-format/reference.log.");
        await File.WriteAllTextAsync(Path.Combine(output, "reference.json"), JsonSerializer.Serialize(new
        {
            hostOnly = true,
            passedCases = 49484,
            inputs = sources.Concat(["src/Runtime.NativeAot/format_fixed.witos.h", "src/Runtime.NativeAot/X64/native_format.asm"]).Select(p => new { file = p, sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(root, p)))).ToLowerInvariant() }),
            scope = "Hosted C-locale secure formatting comparison, initial round-to-nearest FP environment; guest compiler/GS integration is separate."
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        Console.WriteLine("[FORMAT-REFERENCE-PASS] 49484 secure formatting comparisons against Windows CRT (HOSTED only).");
    }

    #endregion
}
