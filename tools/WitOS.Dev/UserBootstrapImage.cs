using System.Reflection.PortableExecutable;
using System.Text;

namespace WitOS.Dev;

internal static class UserBootstrapImage
{
    public static async Task BuildAsync(string root, string output, string msvc)
    {
        var objects = new List<string>();
        foreach (var (source, name) in new[]
        {
            ("src/System.Native/bootstrap.c", "native_bootstrap"),
            ("tests/User.X64/bootstrap.c", "bootstrap_fixture")
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
        var image = Path.Combine(output, "BootstrapFixture.pe");
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "link.exe"),
            ["/nologo", "/subsystem:native", "/entry:wit_native_start", "/nodefaultlib", "/machine:x64",
                "/fixed:no", "/dynamicbase", "/incremental:no", "/Brepro", "/base:0x180000000",
                $"/out:{image}", .. objects], root);
        var bytes = await File.ReadAllBytesAsync(image);
        using var stream = new MemoryStream(bytes, writable: false);
        using var pe = new PEReader(stream);
        var h = pe.PEHeaders.PEHeader ?? throw new InvalidDataException("Native bootstrap PE missing.");
        if (pe.PEHeaders.CoffHeader.Machine != Machine.Amd64 || pe.PEHeaders.CorHeader is not null ||
            h.Subsystem != Subsystem.Native || h.ImportTableDirectory.Size != 0 ||
            h.ThreadLocalStorageTableDirectory.Size != 0 || h.ExceptionTableDirectory.Size < 12 ||
            h.BaseRelocationTableDirectory.Size == 0)
            throw new InvalidDataException("Native bootstrap fixture must include unwind/relocation metadata and no imports.");
        var generated = new StringBuilder("/* Complete native C bootstrap PE; generated. */\nstatic const unsigned char wit_bootstrap_image[] = {\n");
        for (var i = 0; i < bytes.Length; i += 16)
            generated.AppendLine("    " + string.Join(", ", bytes.Skip(i).Take(16).Select(b => $"0x{b:X2}")) + ",");
        generated.AppendLine("};");
        await File.WriteAllTextAsync(Path.Combine(output, "bootstrap_image.h"), generated.ToString(), Encoding.ASCII);
        Console.WriteLine($"BootstrapFixture: {bytes.Length} file bytes, {h.ExceptionTableDirectory.Size / 12} unwind entries; native C, no CRT.");
    }
}
