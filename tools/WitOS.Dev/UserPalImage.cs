using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WitOS.Dev;

internal static class UserPalImage
{
    private static readonly string[] INPUTS =
    [
        "src/coreclr/nativeaot/Runtime/Pal.h", "src/coreclr/nativeaot/Runtime/PalLimitedContext.h",
        "src/coreclr/nativeaot/Runtime/CommonMacros.h", "src/coreclr/nativeaot/Runtime/rhassert.h",
        "src/coreclr/nativeaot/Runtime/inc/CommonTypes.h", "src/coreclr/nativeaot/Runtime/windows/PalInline.h",
        "src/native/minipal/guid.h", "src/native/minipal/utils.h", "src/native/minipal/mutex.h",
        "src/coreclr/gc/env/gcenv.structs.h", "LICENSE.TXT"
    ];

    /// <summary>Builds import-free PAL thread, memory and wait fixtures against pinned upstream declarations.</summary>
    /// <param name="root">Repository root.</param>
    /// <param name="output">Artifact directory.</param>
    /// <param name="msvc">Native compiler directory.</param>
    /// <returns>A task completing after both fixtures are validated and embedded.</returns>
    /// <exception cref="InvalidDataException">The generated image violates the guest contract.</exception>
    public static async Task BuildAsync(string root, string output, string msvc)
    {
        var pin = RuntimeExperiment.ReadLock(root);
        var stage = Path.Combine(output, "pal-source");
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        foreach (var name in INPUTS)
        {
            var item = pin.Sources.Single(s => s.Path == name);
            var cached = await RuntimeExperiment.FetchAsync(client, Path.Combine(root, ".tools", "runtime-audit"),
                "runtime", pin.RuntimeCommit, name, item.Sha256);
            var target = Path.Combine(stage, name);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(cached, target, overwrite: true);
        }
        var vc = Path.GetFullPath(Path.Combine(msvc, "..", "..", ".."));
        var sdkLib = Toolchain.FindWindowsSdkLibrary("kernel32.lib");
        var sdkVersion = Directory.GetParent(sdkLib)!.Parent!.Parent!.Name;
        var sdk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Windows Kits", "10", "Include", sdkVersion);
        string[] compile =
        [
            "/nologo", "/c", "/TP", "/std:c++17", "/W4", "/WX", "/GS-", "/Zl", "/Oi", "/O1", "/GR-",
            "/DHOST_64BIT", "/DHOST_WINDOWS", "/DTARGET_WINDOWS", "/DHOST_AMD64", "/DTARGET_AMD64", "/DTARGET_64BIT",
            "/DNDEBUG", "/DWIN32_LEAN_AND_MEAN", "/DNOMINMAX",
            $"/I{Path.Combine(vc, "include")}", $"/I{Path.Combine(sdk, "ucrt")}",
            $"/I{Path.Combine(sdk, "um")}", $"/I{Path.Combine(sdk, "shared")}",
            $"/I{Path.Combine(stage, "src", "coreclr", "nativeaot", "Runtime")}",
            $"/I{Path.Combine(stage, "src", "coreclr", "nativeaot", "Runtime", "inc")}",
            $"/I{Path.Combine(stage, "src", "coreclr", "nativeaot", "Runtime", "windows")}",
            $"/I{Path.Combine(stage, "src", "coreclr", "gc", "env")}", $"/I{Path.Combine(stage, "src", "native")}",
            $"/I{Path.Combine(root, "src", "Runtime.NativeAot")}", $"/I{Path.Combine(root, "src", "System.Native")}",
            $"/I{Path.Combine(root, "src", "Kernel", "include")}", $"/I{Path.Combine(root, "tests", "User.X64")}"
        ];
        string[] sources = ["src/Runtime.NativeAot/pal.witos.cpp", "tests/User.X64/pal_thread.cpp", "src/System.Native/tls_metadata.c",
            "src/Runtime.NativeAot/pal_memory.witos.cpp", "src/Runtime.NativeAot/pal_events.witos.cpp", "tests/User.X64/pal_services.cpp"];
        var objects = new List<string>();
        foreach (var name in sources)
        {
            var obj = Path.Combine(output, "pal_" + Path.GetFileNameWithoutExtension(name) + ".obj");
            await Processes.RequireSuccessAsync(Path.Combine(msvc, "cl.exe"),
                [.. compile.Where(a => a != "/TP" && a != "/std:c++17"),
                    name.EndsWith(".c", StringComparison.Ordinal) ? "/TC" : "/TP",
                    name.EndsWith(".c", StringComparison.Ordinal) ? "/std:c17" : "/std:c++17",
                    $"/Fo{obj}", Path.Combine(root, name)], root);
            objects.Add(obj);
        }
        var header = new StringBuilder("/* NativeAOT PAL thread/memory/wait fixtures; generated. */\n");
        foreach (var tls in new[] { false, true })
        {
            var path = Path.Combine(output, tls ? "PalThreadFixture.pe" : "PalPlainFixture.pe");
            string[] metadata = tls ? ["/include:_tls_used", objects[2]] : [];
            // A real relocation is rooted by native startup/fixture metadata. Plain
            // fixture uses its fixed supported preferred address if no fixups exist.
            await Processes.RequireSuccessAsync(Path.Combine(msvc, "link.exe"),
                ["/nologo", "/subsystem:native", "/entry:wit_native_start", "/nodefaultlib", "/machine:x64",
                    "/fixed:no", "/dynamicbase", "/incremental:no", "/Brepro", "/base:0x8000100000",
                    $"/out:{path}", Path.Combine(output, "native_start.obj"), objects[0], objects[1], objects[3], objects[4], objects[5], .. metadata], root);
            var bytes = await File.ReadAllBytesAsync(path);
            using var stream = new MemoryStream(bytes, writable: false);
            using var pe = new PEReader(stream);
            var h = pe.PEHeaders.PEHeader ?? throw new InvalidDataException("PAL fixture header missing.");
            if (h.ImportTableDirectory.Size != 0 || h.DelayImportTableDirectory.Size != 0 || h.LoadConfigTableDirectory.Size != 0 ||
                pe.PEHeaders.CorHeader is not null || h.ThreadLocalStorageTableDirectory.Size != (tls ? 40 : 0))
                throw new InvalidDataException("PAL fixture has unsupported imports/metadata.");
            header.AppendLine("static const unsigned char " + (tls ? "wit_pal_tls_image" : "wit_pal_plain_image") + "[] = {");
            for (var i = 0; i < bytes.Length; i += 16)
                header.AppendLine("    " + string.Join(", ", bytes.Skip(i).Take(16).Select(b => $"0x{b:X2}")) + ",");
            header.AppendLine("};");
            Console.WriteLine($"{Path.GetFileName(path)}: {bytes.Length} bytes, unchanged upstream PAL declarations, no OS/CRT imports.");
        }
        await File.WriteAllTextAsync(Path.Combine(output, "pal_image.h"), header.ToString(), Encoding.ASCII);
        await File.WriteAllTextAsync(Path.Combine(output, "pal-build.json"), JsonSerializer.Serialize(new
        {
            pin.RuntimeVersion, pin.RuntimeCommit, guestManagedRuntime = false,
            scope = "Partial NativeAOT PAL: thread discovery, committed memory, events and non-alertable waits; no ThreadStore or GC execution.",
            inputs = pin.Sources.Where(s => INPUTS.Contains(s.Path)),
            localInputs = sources.Concat(["src/Runtime.NativeAot/pal.witos.h", "src/Kernel/include/witos/thread_info.h",
                    "src/Kernel/include/witos/user_abi.h", "src/Kernel.Arch.X64/native_start.asm"])
                .Select(p => new { path = p, sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(root, p)))).ToLowerInvariant() })
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
    }
}
