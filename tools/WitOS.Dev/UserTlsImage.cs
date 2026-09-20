using System.Reflection.PortableExecutable;
using System.Text;
using System.Security.Cryptography;
using System.Text.Json;

namespace WitOS.Dev;

internal static class UserTlsImage
{
    /// <summary>Builds the import-free MSVC static TLS guest fixture.</summary>
    /// <param name="root">Repository root.</param>
    /// <param name="output">Generated artifact directory.</param>
    /// <param name="msvc">Native compiler tool directory.</param>
    /// <returns>A task completing after image validation and embedding.</returns>
    /// <exception cref="InvalidDataException">The image violates the static TLS profile.</exception>
    public static async Task BuildAsync(string root, string output, string msvc)
    {
        var objects = new List<string>();
        foreach (var (source, name) in new[]
        {
            ("tests/User.X64/compiler_tls.c", "compiler_tls"),
            ("tests/User.X64/compiler_tls_access.c", "compiler_tls_access")
        })
        {
            var obj = Path.Combine(output, name + ".obj");
            await Processes.RequireSuccessAsync(Path.Combine(msvc, "cl.exe"),
                ["/nologo", "/c", "/TC", "/std:c17", "/W4", "/WX", "/GS-", "/Zl", "/Oi", "/O1",
                    $"/I{Path.Combine(root, "src", "Kernel", "include")}",
                    $"/I{Path.Combine(root, "src", "System.Native")}",
                    $"/I{Path.Combine(root, "tests", "User.X64")}", $"/Fo{obj}", Path.Combine(root, source)], root);
            objects.Add(obj);
        }
        var entry = Path.Combine(output, "native_start.obj");
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "ml64.exe"),
            ["/nologo", "/c", $"/I{output}", $"/Fo{entry}", Path.Combine(root, "src", "Kernel.Arch.X64", "native_start.asm")], root);
        objects.Add(entry);
        var image = Path.Combine(output, "TlsFixture.pe");
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "link.exe"),
            ["/nologo", "/subsystem:native", "/entry:wit_native_start", "/nodefaultlib", "/machine:x64",
                "/fixed:no", "/dynamicbase", "/incremental:no", "/Brepro", "/base:0x180000000", "/include:_tls_used",
                $"/out:{image}", .. objects], root);
        var bytes = await File.ReadAllBytesAsync(image);
        using var stream = new MemoryStream(bytes, writable: false);
        using var pe = new PEReader(stream);
        var h = pe.PEHeaders.PEHeader ?? throw new InvalidDataException("Compiler TLS PE missing.");
        if (pe.PEHeaders.CoffHeader.Machine != Machine.Amd64 || pe.PEHeaders.CorHeader is not null ||
            h.Subsystem != Subsystem.Native || h.ImportTableDirectory.Size != 0 ||
            h.DelayImportTableDirectory.Size != 0 || h.LoadConfigTableDirectory.Size != 0 ||
            h.ThreadLocalStorageTableDirectory.Size != 40 || h.ExceptionTableDirectory.Size < 12 ||
            h.BaseRelocationTableDirectory.Size == 0)
            throw new InvalidDataException("Compiler TLS fixture must include TLS/unwind/relocation metadata and no imports.");
        var generated = new StringBuilder("/* Static compiler TLS PE; generated. */\nstatic const unsigned char wit_tls_image[] = {\n");
        for (var i = 0; i < bytes.Length; i += 16)
            generated.AppendLine("    " + string.Join(", ", bytes.Skip(i).Take(16).Select(b => $"0x{b:X2}")) + ",");
        generated.AppendLine("};");
        await File.WriteAllTextAsync(Path.Combine(output, "tls_image.h"), generated.ToString(), Encoding.ASCII);
        var disassembly = await Processes.RunAsync(Path.Combine(msvc, "dumpbin.exe"),
            ["/disasm", Path.Combine(output, "compiler_tls_access.obj")], root);
        if (disassembly.ExitCode != 0 || disassembly.TimedOut ||
            !disassembly.Output.Contains("gs:[58h]", StringComparison.OrdinalIgnoreCase) ||
            !disassembly.Output.Contains("_tls_index", StringComparison.Ordinal))
            throw new InvalidDataException("MSVC fixture did not emit the expected x64 TLS addressing.");
        await File.WriteAllTextAsync(Path.Combine(output, "compiler-tls-disassembly.log"), disassembly.Output);
        var report = new
        {
            scope = "Single static MSVC TLS image. No dynamic TLS callbacks, ThreadStore attachment or guest managed execution.",
            guestManagedRuntime = false, compiler = msvc,
            imageBytes = bytes.Length, imageSha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            tlsDirectoryBytes = h.ThreadLocalStorageTableDirectory.Size, unwindEntries = h.ExceptionTableDirectory.Size / 12,
            sources = new[] { "tests/User.X64/compiler_tls.c", "tests/User.X64/compiler_tls_access.c",
                "src/Kernel.Arch.X64/native_start.asm", "src/Kernel/include/witos/user_abi.h" }
                .Select(p => new { path = p, sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(root, p)))).ToLowerInvariant() })
        };
        await File.WriteAllTextAsync(Path.Combine(output, "compiler-tls-build.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        Console.WriteLine($"TlsFixture: {bytes.Length} file bytes, {h.ExceptionTableDirectory.Size / 12} unwind entries; native C, no CRT.");
    }
}
