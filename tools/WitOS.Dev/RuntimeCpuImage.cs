using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace WitOS.Dev;
internal static class RuntimeCpuImage
{
    public static async Task BuildAsync(string root, string output, string msvc, string archive, string minipal, string memory, string crt, string clockObject, string clockBinding)
    {
        var entry = Path.Combine(output, "runtime_cpu_entry.obj");
        var assembly = Path.Combine(output, "runtime_cpu_test.obj");
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "cl.exe"),
            ["/nologo", "/c", "/TP", "/std:c++17", "/W4", "/WX", "/GS-", "/Zl", "/O1", "/GR-",
                "/I" + Path.Combine(root, "src", "System.Native"), "/I" + Path.Combine(root, "src", "Kernel", "include"),
                "/I" + Path.Combine(root, "tests", "User.X64"), "/Fo" + entry,
                Path.Combine(root, "tests", "User.X64", "runtime_cpu_entry.cpp")], root);
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "ml64.exe"),
            ["/nologo", "/c", "/I" + output, "/Fo" + assembly, Path.Combine(root, "src", "Kernel.Arch.X64", "user_runtime_cpu_fixture.asm")], root);
        var clockTest = Path.Combine(output, "runtime_clock_test.obj");
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "ml64.exe"),
            ["/nologo", "/c", "/Fo" + clockTest, Path.Combine(root, "src", "Kernel.Arch.X64", "user_runtime_clock_fixture.asm")], root);
        string[] shared = ["native_start.obj", "native_error.obj", "dynamic_thread.obj", "dynamic_image.obj", "dynamic_tls.witos.obj", "dynamic_tls_metadata.obj", "pal_pal_error.witos.obj"];
        var path = Path.Combine(output, "RuntimeCpuFixture.pe");
        var mapPath = Path.Combine(output, "RuntimeCpuFixture.map");
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "link.exe"),
            ["/nologo", "/subsystem:native", "/entry:wit_native_start", "/nodefaultlib", "/machine:x64", "/fixed:no", "/dynamicbase",
                "/incremental:no", "/Brepro", "/opt:ref", "/include:_tls_used", "/merge:.CRT=.rdata", "/base:0x180000000",
                "/out:" + path, "/map:" + mapPath, entry, assembly, clockTest, clockObject, clockBinding, archive, minipal, memory, crt,
                .. shared.Select(p => Path.Combine(output, p))], root);
        var bytes = await File.ReadAllBytesAsync(path);
        using var pe = new PEReader(new MemoryStream(bytes, writable: false));
        var h = pe.PEHeaders.PEHeader ?? throw new InvalidDataException("CPU fixture PE header missing.");
        if (h.ImportTableDirectory.Size != 0 || h.DelayImportTableDirectory.Size != 0 || h.LoadConfigTableDirectory.Size != 0 ||
            pe.PEHeaders.CorHeader is not null || h.ThreadLocalStorageTableDirectory.Size != 40 || h.BaseRelocationTableDirectory.Size == 0)
            throw new InvalidDataException("CPU fixture violates the import-free TLS profile.");
        var raw = (byte[])bytes.Clone();
        raw.AsSpan(BitConverter.ToInt32(raw, 0x3c) + 24 + 112 + 9 * 8, 8).Clear();
        await File.WriteAllBytesAsync(Path.Combine(output, "RuntimeCpuRawFixture.pe"), raw);
        var header = new StringBuilder("/* Native CPU runtime fixture; generated. */\n");
        foreach (var (name, data) in new[] { ("wit_runtime_cpu_image", bytes), ("wit_runtime_cpu_raw_image", raw) }) {
            header.AppendLine("static const unsigned char " + name + "[] = {");
            for (var i = 0; i < data.Length; i += 16)
                header.AppendLine("    " + string.Join(", ", data.Skip(i).Take(16).Select(b => $"0x{b:X2}")) + ",");
            header.AppendLine("};");
        }
        var symbol = Regex.Match(await File.ReadAllTextAsync(mapPath), @"\bwit_cpu_avx_probe\s+([0-9a-fA-F]{16})\b");
        if (!symbol.Success) throw new InvalidDataException("AVX probe missing from CPU map.");
        header.AppendLine($"#define WIT_CPU_AVX_RVA 0x{Convert.ToUInt64(symbol.Groups[1].Value, 16) - h.ImageBase:X}U");
        await File.WriteAllTextAsync(Path.Combine(output, "runtime_cpu_image.h"), header.ToString(), Encoding.ASCII);
        static string Hash(string p) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))).ToLowerInvariant();
        await File.WriteAllTextAsync(Path.Combine(root, "artifacts", "runtime-config", "cpu-image-report.json"), JsonSerializer.Serialize(new {
            guestManagedRuntime = false, imageBytes = bytes.Length, imageSha256 = Hash(path), unwindEntries = h.ExceptionTableDirectory.Size / 12,
            inputs = new[] { entry, assembly, clockTest, clockObject, clockBinding, archive, minipal, memory, crt }.Concat(shared.Select(p => Path.Combine(output, p))).Select(p => new { file = p, sha256 = Hash(p) }),
            sources = new[] { "src/Kernel.Arch.X64/user_runtime_clock_fixture.asm", "tests/User.X64/runtime_clock.cpp", "tests/User.X64/runtime_cpu_entry.cpp", "tests/User.X64/runtime_cpu.cpp", "src/Kernel.Arch.X64/user_runtime_cpu_fixture.asm" }
                .Select(p => new { file = p, sha256 = Hash(Path.Combine(root, p)) })
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        Console.WriteLine($"RuntimeCpuFixture: {bytes.Length} bytes, real minipal CPU backend, no OS/CRT imports.");
    }
}
