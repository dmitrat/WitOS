using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;
using WitOS.Dev.Kernel;

namespace WitOS.Dev.Quality;

// Section fingerprints of the native images the tool builds. Debug records and
// link timestamps are masked, so formatting, comments and file moves must leave
// every fingerprint unchanged, while any code or data change alters one.
internal static class ImageFingerprint
{
    public const string FIXED_BUILD_ID = "fingerprint";
    private static readonly string[] DEFAULT_SCENARIOS = ["boot", "coreclr-memory", "coreclr-storage"];
    private static readonly string[] IMAGE_EXTENSIONS = [".efi", ".pe", ".dll"];
    private static readonly JsonSerializerOptions JSON = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    internal sealed record Section(string Name, int VirtualSize, int RawSize, string Sha256);

    internal sealed record Image(string Key, Section[] Sections);

    internal sealed record Report(string BuildId, string[] Scenarios, Image[] Images);

    // Usage: fingerprint [--output <file>] [--compare <file>] [scenario...]
    public static async Task RunAsync(string root, IReadOnlyList<string> arguments)
    {
        string? output = null;
        string? compare = null;
        var scenarios = new List<string>();
        for (var i = 0; i < arguments.Count; ++i)
        {
            if (arguments[i] == "--output" && i + 1 < arguments.Count)
            {
                output = Path.GetFullPath(arguments[++i]);
            }
            else if (arguments[i] == "--compare" && i + 1 < arguments.Count)
            {
                compare = Path.GetFullPath(arguments[++i]);
            }
            else if (arguments[i].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Unknown or incomplete fingerprint option: {arguments[i]}");
            }
            else
            {
                scenarios.Add(arguments[i]);
            }
        }
        if (scenarios.Count == 0)
        {
            scenarios.AddRange(DEFAULT_SCENARIOS);
        }
        output ??= Path.Combine(root, "artifacts", "fingerprint", "fingerprint.json");

        var images = new List<Image>();
        foreach (var scenario in scenarios)
        {
            var directory = Path.Combine(root, "artifacts", "fingerprint", scenario);
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
            await KernelImageBuilder.BuildAsync(root, scenario, directory, FIXED_BUILD_ID);
            foreach (var file in Directory.EnumerateFiles(directory).Order(StringComparer.Ordinal))
            {
                if (IMAGE_EXTENSIONS.Contains(Path.GetExtension(file).ToLowerInvariant()) && IsNative(file))
                {
                    images.Add(Compute($"{scenario}/{Path.GetFileName(file)}", file));
                }
            }
        }
        // The prepared runtime image is produced by runtime-source; include it
        // when present so overlay formatting is checked by the same comparison.
        var runtime = Path.Combine(root, "artifacts", "runtime-readiness", "guest-driver", "WitOS.NativeAotBoot.pe");
        if (File.Exists(runtime))
        {
            images.Add(Compute("runtime-readiness/WitOS.NativeAotBoot.pe", runtime));
        }

        var report = new Report(FIXED_BUILD_ID, [.. scenarios], [.. images]);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(report, JSON));
        Console.WriteLine($"Fingerprinted {images.Count} images, {images.Sum(i => i.Sections.Length)} sections: {output}");
        if (compare is null)
        {
            return;
        }
        var baseline = JsonSerializer.Deserialize<Report>(await File.ReadAllTextAsync(compare), JSON)
            ?? throw new InvalidDataException("The baseline fingerprint is empty.");
        var differences = Compare(baseline, report);
        if (differences.Count > 0)
        {
            throw new InvalidOperationException(
                "Image fingerprints differ from the baseline:\n" + string.Join("\n", differences));
        }
        Console.WriteLine($"Fingerprints match the baseline: {compare}");
    }

    internal static Image Compute(string key, string path)
    {
        var bytes = File.ReadAllBytes(path);
        var masked = (byte[])bytes.Clone();
        using var reader = new PEReader(new MemoryStream(bytes));
        var headers = reader.PEHeaders;
        var optional = headers.PEHeader ?? throw new InvalidDataException($"{key} has no optional header.");

        // Debug entries carry the link timestamp; their data carries PDB identity
        // and paths, which change with line numbers and output directories.
        var debug = optional.DebugTableDirectory;
        if (debug.Size > 0 && headers.TryGetDirectoryOffset(debug, out var debugOffset))
        {
            foreach (var entry in reader.ReadDebugDirectory())
            {
                Zero(masked, entry.DataPointer, entry.DataSize);
            }
            Zero(masked, debugOffset, debug.Size);
        }
        // The export directory repeats the deterministic (/Brepro) link timestamp.
        var export = optional.ExportTableDirectory;
        if (export.Size >= 40 && headers.TryGetDirectoryOffset(export, out var exportOffset))
        {
            Zero(masked, exportOffset + 4, 4);
        }

        var sections = headers.SectionHeaders
            .Select(section => new Section(section.Name, section.VirtualSize, section.SizeOfRawData,
                Hash(masked, section.PointerToRawData, section.SizeOfRawData)))
            .ToArray();
        return new Image(key, sections);
    }

    internal static List<string> Compare(Report baseline, Report current)
    {
        var differences = new List<string>();
        var before = baseline.Images.ToDictionary(image => image.Key, StringComparer.Ordinal);
        var after = current.Images.ToDictionary(image => image.Key, StringComparer.Ordinal);
        foreach (var key in before.Keys.Except(after.Keys).Order(StringComparer.Ordinal))
        {
            differences.Add($"missing image: {key}");
        }
        foreach (var key in after.Keys.Except(before.Keys).Order(StringComparer.Ordinal))
        {
            differences.Add($"new image: {key}");
        }
        foreach (var key in before.Keys.Intersect(after.Keys).Order(StringComparer.Ordinal))
        {
            var old = before[key].Sections;
            var now = after[key].Sections;
            if (old.Length != now.Length)
            {
                differences.Add($"{key}: section count {old.Length} -> {now.Length}");
                continue;
            }
            for (var i = 0; i < old.Length; ++i)
            {
                if (old[i] != now[i])
                {
                    differences.Add($"{key}: section {old[i].Name} changed (size {old[i].VirtualSize} -> {now[i].VirtualSize})");
                }
            }
        }
        return differences;
    }

    private static bool IsNative(string file)
    {
        using var stream = File.OpenRead(file);
        using var reader = new PEReader(stream);
        return reader.PEHeaders.CorHeader is null;
    }

    private static void Zero(byte[] bytes, int offset, int count)
    {
        Require(bytes, offset, count);
        Array.Clear(bytes, offset, count);
    }

    private static string Hash(byte[] bytes, int offset, int count)
    {
        Require(bytes, offset, count);
        return Convert.ToHexString(SHA256.HashData(bytes.AsSpan(offset, count))).ToLowerInvariant();
    }

    private static void Require(byte[] bytes, int offset, int count)
    {
        if (offset < 0 || count < 0 || offset > bytes.Length - count)
        {
            throw new InvalidDataException("An image range lies outside the file.");
        }
    }
}
