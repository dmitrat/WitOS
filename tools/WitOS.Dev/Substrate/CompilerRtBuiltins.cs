using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using WitOS.Dev.Host;
using WitOS.Dev.Kernel;

namespace WitOS.Dev.Substrate;

/// <summary>
/// The compiler's runtime helpers a program needs beside the libc (plan step S1.1): compiler-rt's generic soft-float
/// builtins for IEEE binary128, which clang emits for <c>long double</c> on aarch64, and the complex multiplication
/// builtins musl's complex functions call, which libc.so links whole (S5.3); the pinned LLVM package ships none of
/// them for the Linux triples. The files are pinned by SHA-256 at the toolchain's own tag in
/// src/Substrate/compiler-rt.lock.json, downloaded once into .tools/compiler-rt, verified on every use and compiled
/// unchanged with the pinned clang into builtins.a for each architecture.
/// </summary>
internal static class CompilerRtBuiltins
{
    #region Constants

    /// <summary>
    /// The lock file.
    /// </summary>
    public const string LOCK = "src/Substrate/compiler-rt.lock.json";

    private const string REPOSITORY = "https://github.com/llvm/llvm-project";

    #endregion

    #region Functions

    /// <summary>
    /// Reads and checks the lock file.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <returns>The pin.</returns>
    public static async Task<CompilerRtPin> ReadPinAsync(string root)
    {
        var pin = JsonSerializer.Deserialize<CompilerRtPin>(await File.ReadAllTextAsync(Path.Combine(root, LOCK)),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new InvalidDataException("Missing compiler-rt pin.");
        if (pin.Repository != REPOSITORY || pin.Tag != "llvmorg-" + Toolchain.LLVM_VERSION || pin.Sources.Length == 0 ||
            !pin.Sources.Any(source => source.Path == "compiler-rt/LICENSE.TXT"))
            throw new InvalidDataException("Unsupported compiler-rt pin.");
        foreach (var source in pin.Sources)
        {
            if (!Regex.IsMatch(source.Sha256, "^[0-9a-f]{64}$") || Path.IsPathRooted(source.Path) ||
                source.Path.Contains('\\') || source.Path.Split('/').Any(part => part is "" or "." or ".."))
                throw new InvalidDataException("Invalid compiler-rt file entry: " + source.Path);
        }
        return pin;
    }

    /// <summary>
    /// Downloads the pinned files that are missing and verifies every file.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <returns>The directory holding compiler-rt/.</returns>
    public static async Task<string> PrepareAsync(string root)
    {
        var pin = await ReadPinAsync(root);
        var cache = Path.GetFullPath(Path.Combine(root, ".tools", "compiler-rt", pin.Tag));
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        using var gate = new SemaphoreSlim(8);
        await Task.WhenAll(pin.Sources.Select(async source =>
        {
            var cached = Path.GetFullPath(Path.Combine(cache, source.Path));
            if (!File.Exists(cached))
            {
                await gate.WaitAsync();
                try
                {
                    var bytes = await client.GetByteArrayAsync($"https://raw.githubusercontent.com/llvm/llvm-project/{pin.Tag}/{source.Path}");
                    Verify(bytes, source);
                    Directory.CreateDirectory(Path.GetDirectoryName(cached)!);
                    var temporary = cached + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    await File.WriteAllBytesAsync(temporary, bytes);
                    File.Move(temporary, cached, overwrite: true);
                }
                finally
                {
                    gate.Release();
                }
            }
            Verify(await File.ReadAllBytesAsync(cached), source);
        }));
        return cache;
    }

    /// <summary>
    /// Compiles the pinned builtins for an architecture into builtins.a under the substrate's artifact directory.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="architecture">Target architecture.</param>
    /// <param name="includes">Include directories of the C library, for the standard headers the sources use.</param>
    /// <returns>Path of the archive.</returns>
    public static async Task<string> BuildAsync(string root, KernelArchitecture architecture, IEnumerable<string> includes)
    {
        var pin = await ReadPinAsync(root);
        var cache = await PrepareAsync(root);
        var output = Path.Combine(root, "artifacts", "substrate", architecture.Name, "builtins");
        Directory.CreateDirectory(output);
        var archive = Path.Combine(output, "builtins.a");
        var stamp = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n',
            [pin.Tag, Toolchain.LLVM_VERSION, architecture.Triple, .. architecture.ClangOptions,
                .. pin.Sources.Select(source => source.Sha256 + string.Join(',', source.Architectures ?? []))])))).ToLowerInvariant();
        var stampPath = Path.Combine(output, "stamp.txt");
        if (File.Exists(archive) && File.Exists(stampPath) && await File.ReadAllTextAsync(stampPath) == stamp)
            return archive;
        File.Delete(stampPath);
        var arch = MuslLibc.MuslArchitecture(architecture);
        var objects = new List<string>();
        foreach (var source in pin.Sources.Where(source => source.Path.EndsWith(".c", StringComparison.Ordinal)))
        {
            // An architecture directory holds that architecture's file alone, and a file compiler-rt's CMake lists for some
            // architectures names them (mulxc3.c: x86's 80-bit long double); the rest is generic.
            var parts = source.Path.Split('/');
            if ((parts.Length == 5 && parts[3] != arch) || (source.Architectures is { } only && !only.Contains(arch)))
                continue;
            var obj = Path.Combine(output, string.Join('_', parts[3..]) + ".o");
            await Processes.RequireSuccessAsync(Toolchain.Clang(root),
            [
                $"--target={architecture.Triple}", "-std=c11", "-O2", "-ffreestanding", "-fno-builtin", "-fomit-frame-pointer",
                "-fvisibility=hidden", "-fPIE", "-fno-stack-protector", "-fno-unwind-tables", "-fno-asynchronous-unwind-tables",
                "-nostdlibinc", "-w", .. architecture.ClangOptions, .. includes.SelectMany(include => new[] { "-isystem", include }),
                "-c", Path.Combine(cache, source.Path), "-o", obj
            ], root);
            objects.Add(obj);
        }
        File.Delete(archive);
        await Processes.RequireSuccessAsync(Toolchain.LlvmAr(root), ["rcs", archive, .. objects], root);
        await File.WriteAllTextAsync(stampPath, stamp);
        Console.WriteLine($"compiler-rt builtins {pin.Tag} for {architecture.Triple}: {objects.Count} objects.");
        return archive;
    }

    #endregion

    #region Tools

    private static void Verify(byte[] bytes, CompilerRtSource source)
    {
        if (Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() != source.Sha256)
            throw new InvalidDataException("compiler-rt canonical-byte hash mismatch: " + source.Path);
    }

    #endregion
}

/// <summary>
/// The compiler-rt pin: the repository, the tag and the files.
/// </summary>
/// <param name="Repository">Upstream repository.</param>
/// <param name="Tag">Git tag, the toolchain's.</param>
/// <param name="Purpose">Why the files are pinned.</param>
/// <param name="Sources">Pinned files.</param>
internal sealed record CompilerRtPin(string Repository, string Tag, string Purpose, CompilerRtSource[] Sources);

/// <summary>
/// One pinned file.
/// </summary>
/// <param name="Path">Path within llvm-project.</param>
/// <param name="Sha256">SHA-256 of its canonical bytes.</param>
/// <param name="Architectures">The musl architectures a generic file is compiled for, null for every one.</param>
internal sealed record CompilerRtSource(string Path, string Sha256, string[]? Architectures = null);
