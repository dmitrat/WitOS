using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WitOS.Dev;

internal static class UserPalBackgroundImage
{
    /// <summary>Builds a real PAL worker/TLS lifecycle fixture using same-build native objects.</summary>
    /// <param name="root">Repository root.</param>
    /// <param name="output">Artifact directory already populated by the base fixtures.</param>
    /// <param name="msvc">Compiler directory.</param>
    /// <returns>A task completing after image validation and embedding.</returns>
    /// <exception cref="InvalidDataException">The image violates the supported guest profile.</exception>
    public static async Task BuildAsync(string root, string output, string msvc)
    {
        var stage = Path.Combine(output, "pal-source");
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
        string[] sources = ["src/Runtime.NativeAot/pal_threads.witos.cpp", "tests/User.X64/pal_background.cpp",
            "tests/User.X64/pal_background_entry.c"];
        var objects = new List<string>();
        foreach (var name in sources)
        {
            var obj = Path.Combine(output, "background_" + Path.GetFileNameWithoutExtension(name) + ".obj");
            await Processes.RequireSuccessAsync(Path.Combine(msvc, "cl.exe"),
                [.. compile.Where(a => a != "/TP" && a != "/std:c++17"),
                    name.EndsWith(".c", StringComparison.Ordinal) ? "/TC" : "/TP",
                    name.EndsWith(".c", StringComparison.Ordinal) ? "/std:c17" : "/std:c++17",
                    $"/Fo{obj}", Path.Combine(root, name)], root);
            objects.Add(obj);
        }
        string[] shared = ["native_start.obj", "dynamic_tls.witos.obj", "dynamic_native_new.witos.obj",
            "dynamic_thread.obj", "dynamic_library_lifecycle.obj", "dynamic_crt_memory.witos.obj", "dynamic_image.obj", "dynamic_tls_metadata.obj", "pal_pal.witos.obj", "pal_pal_events.witos.obj", "pal_pal_error.witos.obj", "native_error.obj"];
        var path = Path.Combine(output, "PalBackgroundFixture.pe");
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "link.exe"),
            ["/nologo", "/subsystem:native", "/entry:wit_native_start", "/nodefaultlib", "/machine:x64", "/fixed:no",
                "/dynamicbase", "/incremental:no", "/Brepro", "/base:0x180000000", "/include:_tls_used", "/merge:.CRT=.rdata",
                $"/out:{path}", .. shared.Select(p => Path.Combine(output, p)), .. objects], root);
        var bytes = await File.ReadAllBytesAsync(path);
        using var stream = new MemoryStream(bytes, writable: false);
        using var pe = new PEReader(stream);
        var h = pe.PEHeaders.PEHeader ?? throw new InvalidDataException("Background PAL header missing.");
        if (h.ImportTableDirectory.Size != 0 || h.DelayImportTableDirectory.Size != 0 || h.LoadConfigTableDirectory.Size != 0 ||
            h.ThreadLocalStorageTableDirectory.Size != 40 || h.BaseRelocationTableDirectory.Size == 0 || pe.PEHeaders.CorHeader is not null)
            throw new InvalidDataException("Background PAL fixture contains unsupported imports/metadata.");
        var header = new StringBuilder("/* Detached PAL worker fixture; generated. */\nstatic const unsigned char wit_pal_background_image[] = {\n");
        for (var i = 0; i < bytes.Length; i += 16)
            header.AppendLine("    " + string.Join(", ", bytes.Skip(i).Take(16).Select(b => $"0x{b:X2}")) + ",");
        header.AppendLine("};");
        await File.WriteAllTextAsync(Path.Combine(output, "pal_background_image.h"), header.ToString(), Encoding.ASCII);
        await File.WriteAllTextAsync(Path.Combine(output, "pal-background-build.json"), JsonSerializer.Serialize(new
        {
            scope = "Actual PAL worker callbacks with C++ TLS cleanup and detached reclamation; no managed finalizer or collector execution.",
            guestManagedRuntime = false, compiler = msvc, imageBytes = bytes.Length,
            imageSha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            unwindEntries = h.ExceptionTableDirectory.Size / 12,
            localSources = sources.Concat(["src/System.Native/thread.c", "src/System.Native/tls.h", "src/Kernel/include/witos/user_abi.h"])
                .Select(p => new { path = p, sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(root, p)))).ToLowerInvariant() }),
            sharedObjects = shared.Select(p => new { file = p, sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(output, p)))).ToLowerInvariant() })
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        Console.WriteLine($"PalBackgroundFixture: {bytes.Length} bytes, native callback/TLS lifecycle, no OS/CRT imports.");
    }
}
