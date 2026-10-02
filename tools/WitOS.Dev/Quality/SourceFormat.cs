using System.Text.Json;
using WitOS.Dev.Host;

namespace WitOS.Dev.Quality;

// Repository formatting gate. build/format.json lists the files and directories
// that follow the repository style; Q2.3 grows it until it covers the tree.
// C and C++ use the pinned clang-format; C# uses the SDK formatter.
internal static class SourceFormat
{
    #region Constants

    private const int BATCH_SIZE = 48;

    #endregion

    #region Fields

    private static readonly string[] NATIVE_EXTENSIONS = [".c", ".h", ".cpp"];

    private static readonly string[] MANAGED_EXTENSIONS = [".cs"];

    private static readonly JsonSerializerOptions JSON = new(JsonSerializerDefaults.Web);

    #endregion

    #region Functions

    public static async Task RunAsync(string root, bool check)
    {
        var manifest = await ReadManifestAsync(root);
        var native = Expand(root, manifest.Native, NATIVE_EXTENSIONS, manifest.Exclude);
        var managed = Expand(root, manifest.Managed, MANAGED_EXTENSIONS, manifest.Exclude);
        var failed = new List<string>();
        if (native.Count > 0)
        {
            failed.AddRange(await FormatNativeAsync(root, native, check));
        }
        if (managed.Count > 0)
        {
            failed.AddRange(await FormatManagedAsync(root, managed, check));
        }
        Console.WriteLine($"{(check ? "Checked" : "Formatted")} {native.Count} native and {managed.Count} managed files.");
        if (failed.Count > 0)
        {
            var action = check ? "Run: dotnet run --project tools/WitOS.Dev -- format" : "The formatter failed.";
            throw new InvalidOperationException(
                $"{failed.Count} file batch(es) are not formatted. {action}\n" + string.Join("\n", failed));
        }
    }

    internal static async Task<SourceFormatManifest> ReadManifestAsync(string root)
    {
        var path = Path.Combine(root, "build", "format.json");
        var manifest = JsonSerializer.Deserialize<SourceFormatManifest>(await File.ReadAllTextAsync(path), JSON);
        if (manifest is null || manifest.Native is null || manifest.Managed is null || manifest.Exclude is null)
        {
            throw new InvalidDataException("build/format.json must define native, managed and exclude arrays.");
        }
        return manifest;
    }

    // Entries are repository-relative files or directories. A missing entry is an
    // error: the manifest must describe the tree exactly, not optimistically.
    internal static List<string> Expand(string root, IEnumerable<string> entries, string[] extensions, string[] exclude)
    {
        var files = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            var full = Path.GetFullPath(Path.Combine(root, entry));
            if (File.Exists(full))
            {
                if (!extensions.Contains(Path.GetExtension(full), StringComparer.Ordinal))
                {
                    throw new InvalidDataException($"Format manifest entry has an unsupported extension: {entry}");
                }
                files.Add(Relative(root, full));
                continue;
            }
            if (!Directory.Exists(full))
            {
                throw new InvalidDataException($"Format manifest entry does not exist: {entry}");
            }
            foreach (var file in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories))
            {
                var relative = Relative(root, file);
                if (extensions.Contains(Path.GetExtension(file), StringComparer.Ordinal) && !Generated(relative))
                {
                    files.Add(relative);
                }
            }
        }
        files.RemoveWhere(file => exclude.Any(prefix => Covers(prefix, file)));
        return [.. files];
    }

    #endregion

    #region Tools

    private static bool Covers(string prefix, string file)
        => file == prefix || file.StartsWith(prefix.TrimEnd('/') + "/", StringComparison.Ordinal);

    private static bool Generated(string relative)
        => relative.Split('/').Any(segment => segment is "bin" or "obj");

    private static string Relative(string root, string path)
        => Path.GetRelativePath(root, path).Replace('\\', '/');

    private static async Task<List<string>> FormatNativeAsync(string root, List<string> files, bool check)
    {
        var formatter = await Toolchain.PrepareClangFormatAsync(root);
        var failed = new List<string>();
        foreach (var batch in files.Chunk(BATCH_SIZE))
        {
            string[] mode = check ? ["--dry-run", "--Werror"] : ["-i"];
            var result = await Processes.RunAsync(formatter, [.. mode, "--style=file", .. batch], root, 300);
            if (result.TimedOut || result.ExitCode != 0)
            {
                failed.Add(Summarize(batch, result));
            }
        }
        return failed;
    }

    // The SDK formatter can need a second pass, for example when splitting a
    // statement re-indents its nested block. Apply until a check pass is clean.
    private static async Task<List<string>> FormatManagedAsync(string root, List<string> files, bool check)
    {
        if (check)
        {
            return await RunManagedAsync(root, files, check: true);
        }
        for (var pass = 0; pass < 3; ++pass)
        {
            await RunManagedAsync(root, files, check: false);
            if ((await RunManagedAsync(root, files, check: true)).Count == 0)
            {
                return [];
            }
        }
        return await RunManagedAsync(root, files, check: true);
    }

    private static async Task<List<string>> RunManagedAsync(string root, List<string> files, bool check)
    {
        var failed = new List<string>();
        foreach (var batch in files.Chunk(BATCH_SIZE))
        {
            List<string> arguments = ["format", "whitespace", root, "--folder", "--include", .. batch];
            if (check)
            {
                arguments.Add("--verify-no-changes");
            }
            var result = await Processes.RunAsync("dotnet", arguments, root, 600);
            if (result.TimedOut || result.ExitCode != 0)
            {
                failed.Add(Summarize(batch, result));
            }
        }
        return failed;
    }

    private static string Summarize(string[] batch, ProcessResult result)
    {
        var detail = (result.Error + result.Output).Trim();
        if (detail.Length > 4000)
        {
            detail = detail[..4000] + "...";
        }
        var range = $"{batch[0]} .. {batch[^1]} ({batch.Length} files)";
        return $"{range}: exit {result.ExitCode}, timed out {result.TimedOut}\n{detail}";
    }

    #endregion
}
