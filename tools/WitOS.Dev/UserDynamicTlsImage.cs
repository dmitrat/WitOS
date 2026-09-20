using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WitOS.Dev;

internal static class UserDynamicTlsImage
{
    /// <summary>Builds the real C++ thread_local lifecycle guest fixture.</summary>
    /// <param name="root">Repository root.</param>
    /// <param name="output">Artifact directory.</param>
    /// <param name="msvc">Native compiler directory.</param>
    /// <returns>A task completing after build and artifact validation.</returns>
    /// <exception cref="InvalidDataException">The fixture violates the guest image contract.</exception>
    public static async Task BuildAsync(string root, string output, string msvc)
    {
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
            $"/I{Path.Combine(root, "src", "Runtime.NativeAot")}", $"/I{Path.Combine(root, "src", "System.Native")}",
            $"/I{Path.Combine(root, "src", "Kernel", "include")}", $"/I{Path.Combine(root, "tests", "User.X64")}"
        ];
        string[] sources = ["src/Runtime.NativeAot/tls.witos.cpp", "src/Runtime.NativeAot/native_new.witos.cpp",
            "src/System.Native/thread.c", "src/System.Native/image.c", "src/System.Native/tls_metadata.c", "tests/User.X64/dynamic_tls_entry.c",
            "tests/User.X64/dynamic_tls.cpp", "tests/User.X64/dynamic_tls_access.cpp"];
        var objects = new List<string>();
        foreach (var source in sources)
        {
            var obj = Path.Combine(output, "dynamic_" + Path.GetFileNameWithoutExtension(source) + ".obj");
            await Processes.RequireSuccessAsync(Path.Combine(msvc, "cl.exe"),
                [.. compile.Where(a => a != "/TP" && a != "/std:c++17"),
                    source.EndsWith(".c", StringComparison.Ordinal) ? "/TC" : "/TP",
                    source.EndsWith(".c", StringComparison.Ordinal) ? "/std:c17" : "/std:c++17",
                    $"/Fo{obj}", Path.Combine(root, source)], root);
            objects.Add(obj);
        }
        var path = Path.Combine(output, "DynamicTlsFixture.pe");
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "link.exe"),
            ["/nologo", "/subsystem:native", "/entry:wit_native_start", "/nodefaultlib", "/machine:x64",
                "/fixed:no", "/dynamicbase", "/incremental:no", "/Brepro", "/base:0x180000000", "/include:_tls_used",
                "/merge:.CRT=.rdata", $"/out:{path}", $"/map:{Path.Combine(output, "DynamicTlsFixture.map")}",
                Path.Combine(output, "native_start.obj"), .. objects], root);
        var bytes = await File.ReadAllBytesAsync(path);
        using var stream = new MemoryStream(bytes, writable: false);
        using var pe = new PEReader(stream);
        var h = pe.PEHeaders.PEHeader ?? throw new InvalidDataException("Dynamic TLS image missing.");
        if (h.ImportTableDirectory.Size != 0 || h.DelayImportTableDirectory.Size != 0 ||
            h.LoadConfigTableDirectory.Size != 0 || h.ThreadLocalStorageTableDirectory.Size != 40 ||
            pe.PEHeaders.CorHeader is not null || h.BaseRelocationTableDirectory.Size == 0)
            throw new InvalidDataException("Dynamic TLS fixture has unexpected imports/metadata.");
        var header = new StringBuilder("/* C++ dynamic TLS guest fixture; generated. */\nstatic const unsigned char wit_dynamic_tls_image[] = {\n");
        for (var i = 0; i < bytes.Length; i += 16)
            header.AppendLine("    " + string.Join(", ", bytes.Skip(i).Take(16).Select(b => $"0x{b:X2}")) + ",");
        header.AppendLine("};");
        var map = await File.ReadAllTextAsync(Path.Combine(output, "DynamicTlsFixture.map"));
        var begin = Regex.Match(map, @"\bwit_tls_initializers_begin\s+([0-9A-Fa-f]{16})\b");
        if (!begin.Success) throw new InvalidDataException("Dynamic TLS initializer boundary missing from linker map.");
        var initializerRva = Convert.ToUInt64(begin.Groups[1].Value, 16) - h.ImageBase;
        header.AppendLine($"#define WIT_DYNAMIC_TLS_INITIALIZER_RVA 0x{initializerRva + 8:X}U");
        await File.WriteAllTextAsync(Path.Combine(output, "dynamic_tls_image.h"), header.ToString(), Encoding.ASCII);
        var symbols = await Processes.RunAsync(Path.Combine(msvc, "dumpbin.exe"),
            ["/symbols", Path.Combine(output, "dynamic_dynamic_tls.obj")], root);
        if (symbols.ExitCode != 0 || symbols.TimedOut || !symbols.Output.Contains("__tlregdtor", StringComparison.Ordinal) ||
            !symbols.Output.Contains(".CRT$XDU", StringComparison.Ordinal))
            throw new InvalidDataException("Compiler did not emit dynamic TLS registration/initializers.");
        await File.WriteAllTextAsync(Path.Combine(output, "dynamic-tls-symbols.log"), symbols.Output);
        var report = new
        {
            scope = "Actual MSVC C++ thread_local constructors/destructors in user space. No ThreadStore or managed runtime.",
            guestManagedRuntime = false, imageBytes = bytes.Length,
            imageSha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), compiler = msvc,
            unwindEntries = h.ExceptionTableDirectory.Size / 12,
            sources = sources.Concat(["src/System.Native/tls.h", "src/Kernel.Arch.X64/native_start.asm"])
                .Select(p => new { path = p, sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(root, p)))).ToLowerInvariant() })
        };
        await File.WriteAllTextAsync(Path.Combine(output, "dynamic-tls-build.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        Console.WriteLine($"DynamicTlsFixture: {bytes.Length} bytes, real compiler initializers/destructor registration, no OS/CRT imports.");
    }
}
