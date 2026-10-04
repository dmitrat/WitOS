using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using WitOS.Dev.Host;
using WitOS.Dev.Pe;
namespace WitOS.Dev.NativeAot.References;

/// <summary>
/// Verifies compiler SEH scope tables and the real filter/finally ABI (hosted).
/// </summary>
internal static class RuntimeSehReference
{
    #region Functions

    /// <summary>
    /// Compiles the SEH frame probe and reads its scope table.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="msvc">Directory of the MSVC x64 host tools.</param>
    /// <param name="output">Output directory.</param>
    /// <param name="aligned">Whether to build the over-aligned frame variant.</param>
    /// <returns>Object and listing paths.</returns>
    public static async Task<string[]> BuildProtectedFrameAsync(string root, string msvc, string output, bool aligned = false)
    {
        Directory.CreateDirectory(output);
        var vc = Path.GetFullPath(Path.Combine(msvc, "..", "..", ".."));
        var sdkVersion = Directory.GetParent(Toolchain.FindWindowsSdkLibrary("kernel32.lib"))!.Parent!.Parent!.Name;
        var sdk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Windows Kits", "10");
        var prefix = aligned ? "seh_gs_aligned" : "seh_gs";
        var frame = Path.Combine(output, prefix + "_frame.obj");
        var listing = Path.Combine(output, prefix + "_frame.asm");
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "cl.exe"), ["/nologo","/c","/std:c++17","/O2","/GS","/EHa","/Zl","/W4","/WX",
            "/I"+Path.Combine(vc,"include"),"/I"+Path.Combine(sdk,"Include",sdkVersion,"ucrt"),"/I"+Path.Combine(sdk,"Include",sdkVersion,"um"),"/I"+Path.Combine(sdk,"Include",sdkVersion,"shared"),
            ..(aligned?new[]{"/DWITOS_SEH_GS_ALIGNED"}:Array.Empty<string>()),"/FAs","/Fa"+listing,"/Fo"+frame,Path.Combine(root,"tests/Runtime.NativeAot/seh_gs_frame.cpp")], root);
        var symbols = NativeObject.Inspect(frame);
        if (!new[] { "__GSHandlerCheck_SEH", "__security_check_cookie", "__security_cookie" }.All(symbols.UndefinedExternals.Contains))
            throw new InvalidDataException("Shared GS/SEH frame lost actual compiler protection dependencies.");
        var assembly = await File.ReadAllTextAsync(listing);
        int Offset(string name)
        { var match = Regex.Match(assembly, @"^" + Regex.Escape(name) + @"\s*=\s*(\d+)", RegexOptions.Multiline); if (!match.Success) throw new InvalidDataException("Missing GS/SEH compiler slot: " + name); return int.Parse(match.Groups[1].Value); }
        var cookieStore = Regex.Match(assembly, @"mov\s+QWORD PTR __\$ArrayPad\$\[(rsp|rbp)\],\s*rax");
        if (!cookieStore.Success || !Regex.IsMatch(assembly, @"buffer\$\[" + cookieStore.Groups[1].Value + @"\]"))
            throw new InvalidDataException("GS/SEH test cookie and buffer no longer share a verified frame base.");
        var delta = Offset("__$ArrayPad$") - Offset("buffer$");
        if (delta < 128 || delta > 4096 || (delta & 7) != 0)
            throw new InvalidDataException("Unexpected GS/SEH compiler cookie placement.");
        var layout = Path.Combine(output, prefix + "_layout.cpp");
        var layoutObject = Path.Combine(output, prefix + "_layout.obj");
        await File.WriteAllTextAsync(layout, "extern \"C\" const unsigned long long wit_" + prefix + "_cookie_delta=" + delta + "ULL;\n");
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "cl.exe"), ["/nologo", "/c", "/O2", "/GS-", "/Zl", "/W4", "/WX", "/Fo" + layoutObject, layout], root);
        return aligned ? [frame, layoutObject] : new[] { frame, layoutObject }.Concat(await BuildProtectedFrameAsync(root, msvc, output, true)).ToArray();
    }

    /// <summary>
    /// Builds and runs the SEH reference.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="msvc">Directory of the MSVC x64 host tools.</param>
    public static async Task RunAsync(string root, string msvc)
    {
        var output = Path.Combine(root, "artifacts/runtime-seh-reference");
        Directory.CreateDirectory(output);
        var vc = Path.GetFullPath(Path.Combine(msvc, "..", "..", ".."));
        var kernel = Toolchain.FindWindowsSdkLibrary("kernel32.lib");
        var sdkVersion = Directory.GetParent(kernel)!.Parent!.Parent!.Name;
        var sdk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Windows Kits", "10");
        var source = Path.Combine(root, "tests/Runtime.NativeAot/seh_reference.cpp");
        var executable = Path.Combine(output, "seh_reference.exe");
        var gsObjects = await BuildProtectedFrameAsync(root, msvc, output);
        var build = await Processes.RunAsync(Path.Combine(msvc, "cl.exe"),
            ["/nologo","/MD","/EHa","/std:c++17","/O2","/GS","/W4","/WX","/I"+Path.Combine(vc,"include"),
             "/I"+Path.Combine(sdk,"Include",sdkVersion,"ucrt"),"/I"+Path.Combine(sdk,"Include",sdkVersion,"um"),"/I"+Path.Combine(sdk,"Include",sdkVersion,"shared"),
             "/I"+Path.Combine(root,"src/Kernel/include"),"/I"+Path.Combine(root,"src/Runtime.NativeAot"),"/FAs","/Fa"+output+"/","/Fo"+output+"/","/Fe"+executable,source,Path.Combine(root,"src/Runtime.NativeAot/seh_validation.witos.cpp"),Path.Combine(root,"src/Runtime.NativeAot/seh_scope.witos.cpp"),Path.Combine(root,"src/Runtime.NativeAot/unwind_validation.witos.cpp"),..gsObjects,"/link","/LIBPATH:"+Path.Combine(vc,"lib/x64"),
             "/LIBPATH:"+Path.Combine(sdk,"Lib",sdkVersion,"ucrt/x64"),"/LIBPATH:"+Path.Combine(sdk,"Lib",sdkVersion,"um/x64"),"kernel32.lib"], root, 60);
        await File.WriteAllTextAsync(Path.Combine(output, "build.log"), build.Output + build.Error);
        if (build.ExitCode != 0 || build.TimedOut)
            throw new InvalidOperationException("Windows exception reference build failed.");
        if (!NativeObject.Inspect(Path.Combine(output, "seh_reference.obj")).UndefinedExternals.Contains("__GSHandlerCheck_SEH"))
            throw new InvalidDataException("Compiler GS/SEH fixture lost its combined handler dependency.");
        var run = await Processes.RunAsync(executable, [], output, 30);
        await File.WriteAllTextAsync(Path.Combine(output, "reference.log"), run.Output + run.Error);
        if (run.ExitCode != 0 || run.TimedOut || !run.Output.Contains("PASS: real compiler C-specific tables", StringComparison.Ordinal) || !run.Output.Contains("PASS: 18 C-specific scope-table boundary cases", StringComparison.Ordinal) || !run.Output.Contains("PASS: actual filter/finally funclet calls on live establisher frame", StringComparison.Ordinal) || !run.Output.Contains("PASS: compiler local return/leave/goto unwind", StringComparison.Ordinal) || !run.Output.Contains("PASS: nested exceptions handled within compiler filter/finally callbacks", StringComparison.Ordinal) || !run.Output.Contains("PASS: escaping filter and collided finally exact-once cleanup", StringComparison.Ordinal) || !run.Output.Contains("PASS: actual compiler GS/SEH protected catch", StringComparison.Ordinal) || !run.Output.Contains("PASS: shared GS/SEH catch, continuation and normal finally", StringComparison.Ordinal) || !run.Output.Contains("PASS: 11 combined GS/SEH payload boundaries and transactional outputs", StringComparison.Ordinal))
            throw new InvalidOperationException("Windows SEH reference failed.");
        foreach (var mode in new[] { 157, 158, 164, 165 })
        {
            var fatal = await Processes.RunAsync(executable, [mode.ToString()], output, 30);
            await File.WriteAllTextAsync(Path.Combine(output, $"gs-corrupt-{mode}.log"), fatal.Output + fatal.Error);
            if (fatal.TimedOut || fatal.ExitCode != unchecked((int)0xC0000409U) || !fatal.Output.Contains("GS-ACTION") ||
               fatal.Output.Contains("GS-FINALLY") || fatal.Output.Contains("GS-CATCH") || fatal.Output.Contains("GS-FILTER") != (mode == 158 || mode == 165))
                throw new InvalidDataException("Windows combined GS/SEH failed cookie-before-handler ordering.");
        }
        await File.WriteAllTextAsync(Path.Combine(output, "reference.json"), JsonSerializer.Serialize(new { hostOnly = true, guestSehImplemented = false, boundaryCases = 18, combinedBoundaryCases = 11, gsObjects = gsObjects.Select(p => new { file = p, sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))).ToLowerInvariant() }), inputs = new[] { source, Path.Combine(root, "tests/Runtime.NativeAot/seh_gs_frame.cpp"), Path.Combine(root, "src/Runtime.NativeAot/seh_scope.witos.cpp"), Path.Combine(root, "src/Runtime.NativeAot/seh_scope.witos.h"), Path.Combine(root, "src/Runtime.NativeAot/seh_validation.witos.cpp"), Path.Combine(root, "src/Runtime.NativeAot/seh_validation.witos.h"), Path.Combine(root, "src/Kernel/include/witos/unwind_metadata.h") }.Select(p => new { file = p, sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))).ToLowerInvariant() }), sourceSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source))).ToLowerInvariant(), executableSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(executable))).ToLowerInvariant() }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        Console.WriteLine("[SEH-REFERENCE-PASS] Actual compiler scope tables, filters/finally/handler order and continuation (HOSTED only).");
    }

    #endregion
}
