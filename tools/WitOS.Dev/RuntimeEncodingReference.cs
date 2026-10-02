using System.Security.Cryptography;
using System.Text.Json;
namespace WitOS.Dev;

internal static class RuntimeEncodingReference
{
    public static async Task RunAsync(string root, string msvc)
    {
        var output = Path.Combine(root, "artifacts", "runtime-encoding");
        Directory.CreateDirectory(output);
        var vc = Path.GetFullPath(Path.Combine(msvc, "..", "..", ".."));
        var kernel = Toolchain.FindWindowsSdkLibrary("kernel32.lib");
        var sdkVersion = Directory.GetParent(kernel)!.Parent!.Parent!.Name;
        var sdk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Windows Kits", "10");
        string[] sources = ["tests/Runtime.NativeAot/encoding_reference.cpp", "src/Runtime.NativeAot/native_encoding.witos.cpp"];
        var executable = Path.Combine(output, "encoding_reference.exe");
        var compile = await Processes.RunAsync(Path.Combine(msvc, "cl.exe"),
            ["/nologo","/MD","/EHsc","/std:c++17","/O2","/W4","/WX",
             "/I"+Path.Combine(vc,"include"),"/I"+Path.Combine(sdk,"Include",sdkVersion,"ucrt"),
             "/I"+Path.Combine(sdk,"Include",sdkVersion,"um"),"/I"+Path.Combine(sdk,"Include",sdkVersion,"shared"),
             "/I"+Path.Combine(root,"src/Runtime.NativeAot"),"/Fo"+output+"/","/Fe"+executable,
             ..sources.Select(p=>Path.Combine(root,p)),"/link","/manifest:no","/LIBPATH:"+Path.Combine(vc,"lib/x64"),
             "/LIBPATH:"+Path.Combine(sdk,"Lib",sdkVersion,"ucrt/x64"),"/LIBPATH:"+Path.Combine(sdk,"Lib",sdkVersion,"um/x64"),"kernel32.lib"], root, 120);
        await File.WriteAllTextAsync(Path.Combine(output, "build.log"), compile.Output + compile.Error);
        if (compile.ExitCode != 0 || compile.TimedOut)
            throw new InvalidOperationException("Encoding reference build failed; see runtime-encoding/build.log.");
        await Processes.RequireSuccessAsync(Path.Combine(sdk, "bin", sdkVersion, "x64", "mt.exe"),
            ["-nologo", "-manifest", Path.Combine(root, "tests/Runtime.NativeAot/encoding_reference.manifest"), "-outputresource:" + executable + ";#1"], root);
        var run = await Processes.RunAsync(executable, [], output, 60);
        await File.WriteAllTextAsync(Path.Combine(output, "reference.log"), run.Output + run.Error);
        if (run.ExitCode != 0 || run.TimedOut || !run.Output.Contains("PASS: 9456444 UTF conversion differential cases", StringComparison.Ordinal))
            throw new InvalidOperationException("Encoding differs from Windows; see runtime-encoding/reference.log.");
        await File.WriteAllTextAsync(Path.Combine(output, "reference.json"), JsonSerializer.Serialize(new
        {
            hostOnly = true,
            passedCases = 9456444,
            inputs = sources.Concat(["src/Runtime.NativeAot/native_encoding.witos.h", "tests/Runtime.NativeAot/encoding_reference.manifest"]).Select(p => new { file = p, sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(root, p)))).ToLowerInvariant() }),
            scope = "Windows UTF-8 reference: all scalars/surrogates, all two-byte sequences, malformed random sequences, sizing, termination and short buffers. Guest binding validation is separate."
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        Console.WriteLine("[ENCODING-REFERENCE-PASS] 9456444 UTF conversion comparisons against Windows (HOSTED only).");
    }
}
