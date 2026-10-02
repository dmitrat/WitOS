using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using WitOS.Dev.Host;
using WitOS.Dev.Pe;
namespace WitOS.Dev.NativeAot.References;

internal static class RuntimeSecurityReference
{
    #region Functions

    public static async Task RunAsync(string root, string msvc)
    {
        var output = Path.Combine(root, "artifacts/runtime-security");
        Directory.CreateDirectory(output);
        var vc = Path.GetFullPath(Path.Combine(msvc, "..", "..", ".."));
        var version = Directory.GetParent(Toolchain.FindWindowsSdkLibrary("kernel32.lib"))!.Parent!.Parent!.Name;
        var sdk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Windows Kits/10");
        var frame = Path.Combine(output, "frame.obj");
        var listing = Path.Combine(output, "frame.asm");
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "cl.exe"), ["/nologo", "/c", "/std:c++17", "/W4", "/WX", "/O2", "/GS", "/Gy", "/Zl", "/FAs", "/Fa" + listing, "/Fo" + frame, Path.Combine(root, "tests/Runtime.NativeAot/security_frame.cpp")], root);
        var assembly = await File.ReadAllTextAsync(listing);
        int Offset(string name)
        { var match = Regex.Match(assembly, @"^" + Regex.Escape(name) + @"\s*=\s*(\d+)", RegexOptions.Multiline); if (!match.Success) throw new InvalidDataException("GS compiler frame layout missing: " + name); return int.Parse(match.Groups[1].Value); }
        var buffer = Offset("buffer$");
        var cookie = Offset("__$ArrayPad$");
        var allocate = Regex.Match(assembly, @"sub\s+rsp,\s*(\d+)");
        if (!allocate.Success || buffer < 32 || cookie - buffer < 128 || cookie + 8 > int.Parse(allocate.Groups[1].Value) || cookie - buffer > 4096 ||
            !new[] { "__security_cookie", "__security_check_cookie", "__GSHandlerCheck" }.All(NativeObject.Inspect(frame).UndefinedExternals.Contains))
            throw new InvalidDataException("GS probe no longer identifies a protected cookie slot safely.");
        string[] includes = ["/I"+Path.Combine(root,"src/System.Native"),"/I"+Path.Combine(root,"src/Kernel/include"),"/I"+Path.Combine(vc,"include"),
            "/I"+Path.Combine(sdk,"Include",version,"ucrt"),"/I"+Path.Combine(sdk,"Include",version,"shared"),"/I"+Path.Combine(sdk,"Include",version,"um")];
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "cl.exe"), ["/nologo", "/c", "/std:c++17", "/W4", "/WX", "/O2", "/GS-", "/Gy", "/Zl", "/DWITOS_GS_COOKIE_OFFSET=" + (cookie - buffer), .. includes, "/Fo" + output + "/", Path.Combine(root, "tests/Runtime.NativeAot/security_reference.cpp"), Path.Combine(root, "src/Runtime.NativeAot/security_cookie.witos.cpp")], root);
        var check = Path.Combine(output, "security_cookie.obj");
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "ml64.exe"), ["/nologo", "/c", "/Fo" + check, Path.Combine(root, "src/Kernel.Arch.X64/security_cookie.asm")], root);
        var executable = Path.Combine(output, "security_reference.exe");
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "link.exe"), ["/nologo","/nodefaultlib","/entry:wit_gs_reference_start","/subsystem:console","/machine:x64","/opt:ref","/incremental:no","/out:"+executable,
            frame,Path.Combine(output,"security_reference.obj"),Path.Combine(output,"security_cookie.witos.obj"),check,
            Path.Combine(sdk,"Lib",version,"um/x64/kernel32.lib"),Path.Combine(sdk,"Lib",version,"um/x64/bcrypt.lib"),Path.Combine(vc,"lib/x64/libcmt.lib")], root);
        var results = new List<object>();
        foreach (var corrupt in new[] { false, true })
        {
            var result = await Processes.RunAsync(executable, [], output, 30, new Dictionary<string, string> { ["WITOS_GS_REFERENCE_CASE"] = corrupt ? "1" : "0" });
            var expected = corrupt ? unchecked((int)0xffff0004U) : 42;
            if (result.TimedOut || result.ExitCode != expected)
                throw new InvalidOperationException("Actual compiler GS frame did not follow its check/failure contract.");
            results.Add(new { corrupt, result.ExitCode, result.TimedOut });
        }
        await File.WriteAllTextAsync(Path.Combine(output, "reference.json"), JsonSerializer.Serialize(new
        {
            hostOnly = true,
            bufferOffset = buffer,
            cookieOffset = cookie,
            results,
            frameSha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(frame))).ToLowerInvariant(),
            sources = new[] { "tests/Runtime.NativeAot/security_frame.cpp", "tests/Runtime.NativeAot/security_reference.cpp", "src/Runtime.NativeAot/security_cookie.witos.cpp", "src/Kernel.Arch.X64/security_cookie.asm", "src/System.Native/native_security.h", "src/System.Native/diagnostics.h" }
                .Select(p => new { file = p, sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(root, p)))).ToLowerInvariant() }),
            scope = "Real MSVC /GS frame and own cookie/check with Windows entropy/exit transport. System seed before protected entry; corrupt slot terminates. Windows GS unwind handler is used only by this hosted image; guest v1 handler evidence remains separate."
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        Console.WriteLine("[GS-REFERENCE-PASS] Actual compiler /GS frame: intact return and corrupted-cookie termination (HOSTED).");
    }

    #endregion
}
