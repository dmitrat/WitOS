using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace WitOS.Dev.NativeAot;

/// <summary>
/// Fetches, verifies and prepares the pinned native math sources.
/// </summary>
internal static class NativeMathSources
{
    #region Functions

    /// <summary>
    /// Verifies the pinned math sources and optionally generates the adapted logarithm source.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="generate">Whether to write the adapted source.</param>
    public static async Task PrepareAsync(string root, bool generate)
    {
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        var pin = JsonSerializer.Deserialize<NativeMathPin>(await File.ReadAllTextAsync(Path.Combine(root, "src/Runtime.NativeAot/math.lock.json")), json)
            ?? throw new InvalidDataException("Missing native math pin.");
        if (pin.Repository != "https://github.com/JuliaMath/openlibm" || !Regex.IsMatch(pin.Revision, "^[0-9a-f]{40}$") ||
            pin.Sources.Length != 2 || !pin.Sources.Select(s => s.Path).Order().SequenceEqual(new[] { "LICENSE.md", "src/e_log.c" }))
            throw new InvalidDataException("Unsupported native math source pin.");
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        var paths = new Dictionary<string, string>();
        foreach (var source in pin.Sources)
        {
            if (!Regex.IsMatch(source.Sha256, "^[0-9a-f]{64}$"))
                throw new InvalidDataException("Bad math hash.");
            var path = Path.Combine(root, ".tools", "math-audit", pin.Revision, source.Path);
            byte[] bytes = File.Exists(path) ? await File.ReadAllBytesAsync(path) :
                await client.GetByteArrayAsync($"https://raw.githubusercontent.com/JuliaMath/openlibm/{pin.Revision}/{source.Path}");
            if (Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() != source.Sha256)
                throw new InvalidDataException("Native math canonical-byte hash mismatch: " + source.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (!File.Exists(path))
                await File.WriteAllBytesAsync(path, bytes);
            paths.Add(source.Path, path);
        }
        var output = Path.Combine(root, "artifacts", "runtime-config", "source");
        if (generate)
        {
            Directory.CreateDirectory(output);
            var source = (await File.ReadAllTextAsync(paths["src/e_log.c"])).Replace("\r\n", "\n");
            string Replace(string before, string after)
            {
                if (source.Split(before, StringSplitOptions.None).Length != 2)
                    throw new InvalidDataException("Math adaptation anchor changed.");
                return source.Replace(before, after, StringComparison.Ordinal);
            }
            source = Replace("#include \"cdefs-compat.h\"", "/* Compiler attributes supplied by WitOS. */");
            source = Replace("#include <openlibm_math.h>", "/* Binary64 entry declaration comes from its definition. */");
            source = Replace("#include \"math_private.h\"", "#include \"math_bits.witos.h\"");
            source = Replace("#if (LDBL_MANT_DIG == 53)\nopenlibm_weak_reference(log, logl);\n#endif", "/* No public long-double alias is supplied by this port. */");
            // These two divisions deliberately produce IEEE infinity/NaN and flags.
            source = Replace("OLM_DLLEXPORT double", "#pragma warning(push)\n#pragma warning(disable:4723)\nOLM_DLLEXPORT double");
            source += "\n#pragma warning(pop)\n";
            await File.WriteAllTextAsync(Path.Combine(output, "log.openlibm.c"), source);
            await File.WriteAllTextAsync(Path.Combine(output, "openlibm-LICENSE.md"), await File.ReadAllTextAsync(paths["LICENSE.md"]));
            await File.WriteAllTextAsync(Path.Combine(output, "math-provenance.json"), JsonSerializer.Serialize(new
            {
                pin,
                corrections = new[] { "Replace compiler/header macros with WitOS binary64 word access", "Map __ieee754_log to private wit_ieee754_log", "Omit unsupported long-double weak alias", "Scope MSVC C4723 suppression to intentional IEEE exceptional divisions; preserve strict FP and test MXCSR flags" },
                algorithmBodyChanged = false,
                generatedSha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(output, "log.openlibm.c")))).ToLowerInvariant()
            }, json));
        }
        Console.WriteLine("[MATH-AUDIT-PASS] OpenLibm log source and license verified at " + pin.Revision + ".");
    }

    #endregion
}
