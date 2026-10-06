using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using WitOS.Dev.Host;
using WitOS.Dev.NativeAot;

namespace WitOS.Dev.Images;

/// <summary>
/// The mathematics of the UCRT subset (P6.4.k3a3c): OpenLibm's double functions and fmaf, from the files
/// src/Runtime.Crt/openlibm.lock.json pins by the SHA-256 of their canonical bytes at the revision the NativeAOT
/// overlay's logarithm uses. They are downloaded once into .tools/math-audit/&lt;revision&gt; and verified on every use,
/// copied with the changes of patches/openlibm into a prepared tree under artifacts/openlibm and compiled as C with
/// MSVC under openlibm_names.witos.h.
/// </summary>
internal static class CrtMathSources
{
    #region Constants

    /// <summary>
    /// The lock file.
    /// </summary>
    public const string LOCK = "src/Runtime.Crt/openlibm.lock.json";

    /// <summary>
    /// The header forced ahead of every source, which gives OpenLibm's functions private names.
    /// </summary>
    public const string NAMES = "src/Runtime.Crt/openlibm_names.witos.h";

    private const string REPOSITORY = "https://github.com/JuliaMath/openlibm";

    private const string NATIVE_AOT_LOCK = "src/Runtime.NativeAot/math.lock.json";

    #endregion

    #region Fields

    // The pinned files WitOS changes, each with its patch, named after the output.
    private static readonly Dictionary<string, string> PATCHED = new(StringComparer.Ordinal)
    {
        ["include/openlibm_defs.h"] = "openlibm_defs.h",
        ["src/cdefs-compat.h"] = "cdefs-compat.h",
        ["src/math_private.h"] = "math_private.h",
        ["src/k_exp.c"] = "k_exp.c",
        ["src/e_rem_pio2.c"] = "e_rem_pio2.c"
    };

    // Its static initializers are floating-point expressions, which /fp:strict does not fold into constants.
    private static readonly string[] PRECISE = ["src/s_fma.c"];

    #endregion

    #region Functions

    /// <summary>
    /// Reads and checks the lock file.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <returns>The pin.</returns>
    internal static async Task<NativeMathPin> ReadPinAsync(string root)
    {
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var pin = JsonSerializer.Deserialize<NativeMathPin>(await File.ReadAllTextAsync(Path.Combine(root, LOCK)), json)
            ?? throw new InvalidDataException("Missing OpenLibm pin.");
        var overlay = JsonSerializer.Deserialize<NativeMathPin>(
            await File.ReadAllTextAsync(Path.Combine(root, NATIVE_AOT_LOCK)), json);
        if (pin.Repository != REPOSITORY || !Regex.IsMatch(pin.Revision, "^[0-9a-f]{40}$") ||
            pin.Revision != overlay?.Revision || pin.Sources.Length == 0 || !pin.Sources.Any(s => s.Path == "LICENSE.md"))
            throw new InvalidDataException("Unsupported OpenLibm pin.");
        foreach (var source in pin.Sources)
        {
            if (!Regex.IsMatch(source.Sha256, "^[0-9a-f]{64}$") || Path.IsPathRooted(source.Path) ||
                source.Path.Contains('\\') || source.Path.Split('/').Any(part => part is "" or "." or ".."))
                throw new InvalidDataException("Invalid OpenLibm file entry: " + source.Path);
        }
        return pin;
    }

    /// <summary>
    /// Downloads the pinned files that are missing, verifies every file and writes the prepared tree.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <returns>The prepared directory, which holds include and src.</returns>
    internal static async Task<string> PrepareAsync(string root)
    {
        var pin = await ReadPinAsync(root);
        var cache = Path.GetFullPath(Path.Combine(root, ".tools", "math-audit", pin.Revision));
        var prepared = Path.GetFullPath(Path.Combine(root, "artifacts", "openlibm", pin.Revision));
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
                    var bytes = await client.GetByteArrayAsync(
                        $"https://raw.githubusercontent.com/JuliaMath/openlibm/{pin.Revision}/{source.Path}");
                    Verify(bytes, source);
                    await PublishAsync(cached, bytes);
                }
                finally
                {
                    gate.Release();
                }
            }
            var text = Encoding.UTF8.GetString(Verify(await File.ReadAllBytesAsync(cached), source)).Replace("\r\n", "\n");
            if (PATCHED.TryGetValue(source.Path, out var output))
                text = UpstreamPatches.Apply(root, "openlibm", source.Path, output, text);
            var destination = Path.Combine(prepared, source.Path);
            var content = Encoding.UTF8.GetBytes(text);
            if (!File.Exists(destination) || !(await File.ReadAllBytesAsync(destination)).AsSpan().SequenceEqual(content))
                await PublishAsync(destination, content);
        }));
        return prepared;
    }

    /// <summary>
    /// Compiles the pinned sources into a directory under the output directory.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="output">Output directory.</param>
    /// <param name="msvc">Directory of the MSVC x64 host tools.</param>
    /// <returns>The objects.</returns>
    internal static async Task<List<string>> CompileAsync(string root, string output, string msvc)
    {
        var pin = await ReadPinAsync(root);
        var prepared = await PrepareAsync(root);
        var directory = Path.Combine(output, "openlibm");
        Directory.CreateDirectory(directory);
        // Upstream's options for Clang in MSVC's terms: no contraction or fast math, strict exception behavior, no
        // warnings (upstream's OPENLIBM_SUPPRESS_WARNINGS); no C runtime and no GS cookie, as the subset itself.
        string[] options = ["/nologo", "/c", "/TC", "/O2", "/Zl", "/GS-", "/w", "/DOPENLIBM_STATIC",
            "/DOPENLIBM_USE_HOST_FENV_H", "/D__BSD_VISIBLE", "/FI" + Path.Combine(root, NAMES),
            "/I" + Path.Combine(prepared, "include"), "/I" + Path.Combine(prepared, "src"),
            .. NativeCxxExceptionImage.Includes(msvc), "/Fo" + directory + "/"];
        var sources = pin.Sources.Select(source => source.Path).Where(path => path.EndsWith(".c", StringComparison.Ordinal))
            .ToArray();
        var cl = Path.Combine(msvc, "cl.exe");
        await Processes.RequireSuccessAsync(cl, [.. options, "/fp:strict",
            .. sources.Except(PRECISE).Select(path => Path.Combine(prepared, path))], root);
        await Processes.RequireSuccessAsync(cl, [.. options, "/fp:precise",
            .. PRECISE.Select(path => Path.Combine(prepared, path))], root);
        return [.. sources.Select(path => Path.Combine(directory, Path.GetFileNameWithoutExtension(path) + ".obj"))];
    }

    #endregion

    #region Tools

    private static byte[] Verify(byte[] bytes, NativeMathSource source)
    {
        if (Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() != source.Sha256)
            throw new InvalidDataException("OpenLibm canonical-byte hash mismatch: " + source.Path);
        return bytes;
    }

    // Another build may write the same file at the same time: publish only complete files.
    private static async Task PublishAsync(string destination, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await File.WriteAllBytesAsync(temporary, bytes);
        File.Move(temporary, destination, overwrite: true);
    }

    #endregion
}
