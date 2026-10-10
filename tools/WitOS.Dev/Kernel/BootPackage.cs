using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WitOS.Dev.Kernel;

/// <summary>
/// Builds the boot package placed beside the kernel on the FAT image: the files the libc programs read and the programs
/// a root task starts.
/// </summary>
internal static class BootPackage
{
    #region Functions

    /// <summary>
    /// Builds the boot package for a scenario.
    /// </summary>
    /// <param name="output">Output directory.</param>
    /// <param name="libcFiles">Whether to include the files the libc programs read.</param>
    /// <param name="programs">Programs a root task starts from the package (S5.2): package paths and files.</param>
    /// <returns>Package bytes.</returns>
    internal static async Task<byte[]> BuildAsync(string output, bool libcFiles = false,
        IReadOnlyList<(string Name, string Source)>? programs = null)
    {
        var files = new List<(string Name, ReadOnlyMemory<byte> Bytes)>();
        var manifest = new List<object>();
        async Task Add(string name, string source)
        {
            var bytes = await File.ReadAllBytesAsync(source);
            files.Add((name, bytes));
            manifest.Add(new { name, source, bytes = bytes.Length, sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() });
        }
        if (libcFiles)
        {
            // The files the first libc program reads through the package (S1.2): a text file and a directory of two.
            foreach (var (name, text) in new[] { ("test/hello.txt", "Hello, package!\nsecond line\n"), ("test/dir/a.txt", "a\n"), ("test/dir/b.txt", "bb\n") })
            {
                var bytes = Encoding.ASCII.GetBytes(text);
                files.Add((name, bytes));
                manifest.Add(new { name, source = (string?)null, bytes = bytes.Length, sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() });
            }
            // Code the libc program maps executable from the package (S5.1): two pages, a function returning 42 for x64
            // (mov eax, 42; ret) in the first and for ARM64 (mov w0, #42; ret) in the second, page-aligned as a file of
            // at least a page is.
            var code = new byte[8192];
            new byte[] { 0xB8, 0x2A, 0x00, 0x00, 0x00, 0xC3 }.CopyTo(code, 0);
            new byte[] { 0x40, 0x05, 0x80, 0x52, 0xC0, 0x03, 0x5F, 0xD6 }.CopyTo(code, 4096);
            files.Add(("test/code.bin", code));
            manifest.Add(new { name = "test/code.bin", source = (string?)null, bytes = code.Length, sha256 = Convert.ToHexString(SHA256.HashData(code)).ToLowerInvariant() });
        }
        foreach (var (name, source) in programs ?? [])
            await Add(name, source);
        var package = AssemblyPackage.Create(files);
        await File.WriteAllBytesAsync(Path.Combine(output, "boot.pak"), package);
        await File.WriteAllTextAsync(Path.Combine(output, "boot-package.json"), JsonSerializer.Serialize(new
        {
            profile = files.Count == 0 ? "empty readonly boot package" : "readonly files of the libc programs and the programs a root task starts",
            fileCount = files.Count,
            packageBytes = package.Length,
            sha256 = Convert.ToHexString(SHA256.HashData(package)).ToLowerInvariant(),
            files = manifest
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Boot package: {files.Count} files, {package.Length} bytes, separate boot-owned readonly/NX allocation.");
        return package;
    }

    #endregion
}
