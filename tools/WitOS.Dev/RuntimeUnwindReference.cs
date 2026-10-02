using System.Security.Cryptography;
using System.Text.Json;
namespace WitOS.Dev;

internal static class RuntimeUnwindReference
{
    private static readonly string[] names = ["src/coreclr/unwinder/amd64/unwinder.cpp", "src/coreclr/unwinder/amd64/unwinder.h", "src/coreclr/unwinder/baseunwinder.h", "src/coreclr/inc/win64unwind.h"];
    public static async Task ValidateImageAsync(string root, string image)
    {
        var output = Path.Combine(root, "artifacts/runtime-unwind");
        var run = await Processes.RunAsync(Path.Combine(output, "unwind_reference.exe"), [image], output, 60);
        await File.WriteAllTextAsync(Path.Combine(output, "image-reference.log"), run.Output + run.Error);
        if (run.ExitCode != 0 || run.TimedOut)
            throw new InvalidDataException("Full image unwind metadata validation failed; see runtime-unwind/image-reference.log.");
        await File.WriteAllTextAsync(Path.Combine(output, "image-reference.json"), JsonSerializer.Serialize(new { hostOnly = true, image, imageSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(image))).ToLowerInvariant() }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        Console.WriteLine("[UNWIND-IMAGE-PASS] Full NativeAOT reference metadata validated (HOSTED only).");
    }
    public static async Task PrepareAsync(string root)
    {
        var pin = RuntimeExperiment.ReadLock(root);
        var output = Path.Combine(root, "artifacts/runtime-unwind");
        Directory.CreateDirectory(output);
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };

        foreach (var name in names)
        {
            var item = pin.Sources.Single(p => p.Path == name);
            var original = await RuntimeExperiment.FetchAsync(client, Path.Combine(root, ".tools/runtime-audit"), "runtime", pin.RuntimeCommit, name, item.Sha256);
            var target = Path.Combine(output, Path.GetFileName(name));
            if (name.EndsWith("unwinder.cpp", StringComparison.Ordinal))
            {
                var body = await File.ReadAllTextAsync(original);
                const string before = "#include \"stdafx.h\"";
                if (body.Split(before).Length != 2)
                    throw new InvalidDataException("Unwinder environment anchor changed.");
                await File.WriteAllTextAsync(target, body.Replace(before, "#include \"unwind_environment.h\""));
            }
            else
                File.Copy(original, target, overwrite: true);
        }
        var checkedBody = await File.ReadAllTextAsync(Path.Combine(output, "unwinder.cpp"));
        foreach (var replacement in new[]{
            ("#include \"unwind_environment.h\"","#include \"unwind_environment.witos.h\""),
            ("return *dac_cast<PTR_ULONG64>((TADDR)addr);","return wit_checked_read64((ULONG64)addr);"),
            ("return *dac_cast<PTR_M128A>((TADDR)addr);","return wit_checked_read128((ULONG64)addr);"),
            ("typedef UCHAR * InstructionBuffer;","typedef WitUnwindInstructionBuffer InstructionBuffer;"),
            ("return (UNWIND_INFO *)taUnwindInfo;","return (UNWIND_INFO *)wit_checked_unwind_info(taUnwindInfo);")})
        {
            if (checkedBody.Split(replacement.Item1).Length != 2)
                throw new InvalidDataException("Checked unwind adaptation anchor changed: " + replacement.Item1);
            checkedBody = checkedBody.Replace(replacement.Item1, replacement.Item2);
        }
        await File.WriteAllTextAsync(Path.Combine(output, "unwinder.checked.cpp"), checkedBody);
    }
    public static async Task RunAsync(string root, string msvc)
    {
        await PrepareAsync(root);
        var pin = RuntimeExperiment.ReadLock(root);
        var output = Path.Combine(root, "artifacts/runtime-unwind");
        var vc = Path.GetFullPath(Path.Combine(msvc, "..", "..", ".."));
        var kernel = Toolchain.FindWindowsSdkLibrary("kernel32.lib");
        var sdkVersion = Directory.GetParent(kernel)!.Parent!.Parent!.Name;
        var sdk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Windows Kits", "10");
        var assembly = Path.Combine(output, "unwind_frames.obj");
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "ml64.exe"), ["/nologo", "/c", "/Fo" + assembly, Path.Combine(root, "tests/Runtime.NativeAot/unwind_frames.asm")], root);
        var executable = Path.Combine(output, "unwind_reference.exe");
        string[] sources = [Path.Combine(root, "tests/Runtime.NativeAot/unwind_checked_reference.cpp"), Path.Combine(root, "tests/Runtime.NativeAot/unwind_reference.cpp"), Path.Combine(root, "tests/Runtime.NativeAot/unwind_compiler_frame.cpp"), Path.Combine(output, "unwinder.cpp"), Path.Combine(root, "tests/Runtime.NativeAot/unwind_validation_reference.cpp"), Path.Combine(root, "src/Runtime.NativeAot/unwind_validation.witos.cpp"), Path.Combine(root, "src/Runtime.NativeAot/unwind_checked.witos.cpp"), Path.Combine(output, "unwinder.checked.cpp")];
        var build = await Processes.RunAsync(Path.Combine(msvc, "cl.exe"),
            ["/nologo","/MD","/EHa","/std:c++17","/O2","/GS","/W4","/WX","/DTARGET_AMD64",
             "/I"+Path.Combine(vc,"include"),"/I"+Path.Combine(sdk,"Include",sdkVersion,"ucrt"),"/I"+Path.Combine(sdk,"Include",sdkVersion,"um"),
             "/I"+Path.Combine(sdk,"Include",sdkVersion,"shared"),"/I"+Path.Combine(root,"src/Kernel/include"),"/I"+Path.Combine(root,"src/Runtime.NativeAot"),"/I"+output,"/I"+Path.Combine(root,"tests/Runtime.NativeAot"),
             "/Fo"+output+"/","/Fe"+executable,..sources,assembly,"/link","/LIBPATH:"+Path.Combine(vc,"lib/x64"),
             "/LIBPATH:"+Path.Combine(sdk,"Lib",sdkVersion,"ucrt/x64"),"/LIBPATH:"+Path.Combine(sdk,"Lib",sdkVersion,"um/x64"),"kernel32.lib"], root, 120);
        await File.WriteAllTextAsync(Path.Combine(output, "build.log"), build.Output + build.Error);
        if (build.ExitCode != 0 || build.TimedOut)
            throw new InvalidOperationException("Unwind reference build failed; see runtime-unwind/build.log.");
        // Compile the same checked implementation without the hosted exception/CRT profile.
        // These objects are evidence of the native boundary, not a guest link or execution.
        var guestObjects = Path.Combine(output, "native-objects");
        Directory.CreateDirectory(guestObjects);
        string[] nativeSources = [Path.Combine(root, "src/Runtime.NativeAot/unwind_checked.witos.cpp"), Path.Combine(output, "unwinder.checked.cpp"), Path.Combine(root, "src/Runtime.NativeAot/unwind_validation.witos.cpp")];
        var nativeBuild = await Processes.RunAsync(Path.Combine(msvc, "cl.exe"),
            ["/nologo","/c","/Zl","/GR-","/EHs-c-","/std:c++17","/O2","/GS","/W4","/WX","/DTARGET_AMD64",
             "/I"+Path.Combine(vc,"include"),"/I"+Path.Combine(sdk,"Include",sdkVersion,"ucrt"),"/I"+Path.Combine(sdk,"Include",sdkVersion,"um"),
             "/I"+Path.Combine(sdk,"Include",sdkVersion,"shared"),"/I"+Path.Combine(root,"src/Kernel/include"),"/I"+Path.Combine(root,"src/Runtime.NativeAot"),"/I"+output,
             "/Fo"+guestObjects+"/",..nativeSources], root, 120);
        await File.WriteAllTextAsync(Path.Combine(output, "native-build.log"), nativeBuild.Output + nativeBuild.Error);
        if (nativeBuild.ExitCode != 0 || nativeBuild.TimedOut)
            throw new InvalidOperationException("Checked unwinder native-profile build failed; see runtime-unwind/native-build.log.");
        var nativeInventory = nativeSources.Select(source =>
        {
            var file = Path.Combine(guestObjects, Path.GetFileNameWithoutExtension(source) + ".obj");
            var symbols = NativeObject.Inspect(file);
            if (symbols.UndefinedExternals.Any(n => n.StartsWith("__imp_", StringComparison.Ordinal) || n.Contains("CxxFrameHandler", StringComparison.Ordinal) || n.Contains("CxxThrowException", StringComparison.Ordinal)))
                throw new InvalidDataException("Checked unwind native profile acquired Windows imports or hosted C++ exception dependencies.");
            return new { file, sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant(), symbols.UndefinedExternals };
        }).ToArray();
        await File.WriteAllTextAsync(Path.Combine(output, "native-objects.json"), JsonSerializer.Serialize(new { guestExecuted = false, profile = "GS enabled, RTTI/C++ EH disabled, no default libraries; failure transport unresolved", objects = nativeInventory }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        var frame = NativeObject.Inspect(Path.Combine(output, "unwind_compiler_frame.obj"));
        if (!frame.UndefinedExternals.Contains("__security_check_cookie") || !frame.UndefinedExternals.Any(n => n.StartsWith("__GSHandlerCheck", StringComparison.Ordinal)))
            throw new InvalidDataException("Compiler reference did not emit GS cookie and unwind-handler dependencies.");
        var run = await Processes.RunAsync(executable, [], output, 60);
        await File.WriteAllTextAsync(Path.Combine(output, "reference.log"), run.Output + run.Error);
        if (run.ExitCode != 0 || run.TimedOut || !run.Output.Contains("PASS: 20 dynamic-source Windows comparisons and 40 transactional entry/metadata rejections", StringComparison.Ordinal) || !run.Output.Contains("PASS: 40 immutable-image cached unwind comparisons and 40 forged-entry rejections", StringComparison.Ordinal) || !run.Output.Contains("PASS: 20 checked unwind differential cases", StringComparison.Ordinal) || !run.Output.Contains("PASS: 13 transactional checked unwind failures", StringComparison.Ordinal) || !run.Output.Contains("PASS: 8 bounded unwind read cases", StringComparison.Ordinal) || !run.Output.Contains("PASS: 20 bounded unwind metadata cases", StringComparison.Ordinal) || !run.Output.Contains("PASS: 20 upstream AMD64 unwind differential cases", StringComparison.Ordinal))
            throw new InvalidOperationException("Upstream unwind differs from Windows; see runtime-unwind/reference.log.");
        await File.WriteAllTextAsync(Path.Combine(output, "reference.json"), JsonSerializer.Serialize(new
        {
            hostOnly = true,
            guestUnwindImplemented = false,
            pin.RuntimeCommit,
            passedCases = 20,
            checkedCases = 20,
            dynamicComparisons = 20,
            dynamicTransactionalRejections = 40,
            cachedComparisons = 40,
            cachedForgedEntryRejections = 40,
            transactionalFailureCases = 13,
            metadataCases = 20,
            boundedReadCases = 8,
            sourcePins = pin.Sources.Where(p => names.Contains(p.Path)),
            adaptation = "Original reference changes only stdafx.h. Separate checked copy replaces stack reads, instruction buffer and metadata lookup, with renamed classes and checked assertions; non-DAC Windows algorithm retained. Hosted access failure uses C++ exceptions; guest delivery remains pending.",
            inputs = names.Select(n => Path.Combine(output, Path.GetFileName(n))).Concat(new[] { Path.Combine(output, "unwinder.checked.cpp") }).Concat(new[] { "src/Kernel/include/witos/unwind_metadata.h", "src/Runtime.NativeAot/unwind_checked.witos.cpp", "src/Runtime.NativeAot/unwind_checked.witos.h", "src/Runtime.NativeAot/unwind_environment.witos.h", "tests/Runtime.NativeAot/unwind_checked_reference.cpp", "tests/Runtime.NativeAot/unwind_environment.h", "tests/Runtime.NativeAot/unwind_reference.cpp", "tests/Runtime.NativeAot/unwind_frames.asm", "tests/Runtime.NativeAot/unwind_compiler_frame.cpp", "tests/Runtime.NativeAot/unwind_validation_reference.cpp", "src/Runtime.NativeAot/unwind_validation.witos.cpp", "src/Runtime.NativeAot/unwind_validation.witos.h" }.Select(p => Path.Combine(root, p)))
                .Select(p => new { file = p, sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))).ToLowerInvariant() })
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        Console.WriteLine("[UNWIND-REFERENCE-PASS] 20 upstream/checked/Windows comparisons, 40 cached comparisons/40 forged-entry rejections and 13 transactional failure cases (HOSTED only).");
    }
}
