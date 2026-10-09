using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using WitOS.Dev.Images;
using WitOS.Dev.Kernel;
using WitOS.Dev.NativeAot;

namespace WitOS.Dev.Substrate;

/// <summary>
/// musl's libc-test on WitOS (plan steps S1.3, S7.1): the pinned revision (src/Substrate/libc-test.lock.json) checked
/// out once with Git under .tools/libc-test, the tests tests/User/libc-test.json selects compiled unchanged with musl's
/// options, each a program of the boot package linked statically and dynamically, started in a process of its own by
/// the runner (tests/User/libc_test_runner.c) under the system layer's root task. Nothing of libc-test is patched.
/// </summary>
internal static class LibcTestSuite
{
    #region Constants

    /// <summary>
    /// The lock file.
    /// </summary>
    public const string LOCK = "src/Substrate/libc-test.lock.json";

    /// <summary>
    /// The selection.
    /// </summary>
    public const string SELECTION = "tests/User/libc-test.json";

    private static readonly Regex COMMIT = new("^[0-9a-f]{40}$");

    #endregion

    #region Functions

    /// <summary>
    /// Reads the pin.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <returns>The repository URL and the commit.</returns>
    public static async Task<(string Repository, string Commit)> ReadPinAsync(string root)
    {
        var pin = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, LOCK))).RootElement;
        var repository = pin.GetProperty("repository").GetString() ?? "";
        var commit = pin.GetProperty("commit").GetString() ?? "";
        if (!repository.StartsWith("https://", StringComparison.Ordinal) || !COMMIT.IsMatch(commit))
            throw new InvalidDataException("Unsupported libc-test pin.");
        return (repository, commit);
    }

    /// <summary>
    /// The checkout of the pinned commit.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="commit">Pinned commit.</param>
    /// <returns>Directory path.</returns>
    public static string SourceDirectory(string root, string commit) => Path.Combine(root, ".tools", "libc-test", commit);

    /// <summary>
    /// Creates or reuses the checkout of the pinned commit and verifies its remote, revision and clean state.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <returns>Source tree path.</returns>
    public static async Task<string> PrepareAsync(string root)
    {
        var (repository, commit) = await ReadPinAsync(root);
        var source = SourceDirectory(root, commit);
        if (!Directory.Exists(Path.Combine(source, ".git")))
        {
            if (Directory.Exists(source) && Directory.EnumerateFileSystemEntries(source).Any())
                throw new InvalidOperationException("libc-test source directory exists without Git metadata; refusing to overwrite it.");
            Directory.CreateDirectory(source);
            await RuntimeSourceCheckout.GitAsync(source, ["init"]);
            await RuntimeSourceCheckout.GitAsync(source, ["remote", "add", "origin", repository + ".git"]);
            // repo.or.cz answers unreliably: the fetch of the one commit retries over HTTPS, then over the Git protocol.
            var transports = new[] { repository + ".git", "git://" + repository["https://".Length..] + ".git" };
            Exception? failure = null;
            for (var attempt = 0; attempt < 6; attempt++)
            {
                try
                {
                    await RuntimeSourceCheckout.GitAsync(source, ["fetch", "--depth=1", transports[attempt / 3], commit], 600);
                    failure = null;
                    break;
                }
                catch (Exception exception) when (exception is InvalidOperationException or IOException)
                {
                    failure = exception;
                    await Task.Delay(TimeSpan.FromSeconds(10));
                }
            }
            if (failure is not null)
                throw new InvalidOperationException("libc-test could not be fetched from " + repository + " after six attempts.", failure);
            await RuntimeSourceCheckout.GitAsync(source, ["checkout", "--detach", commit], 600);
        }
        var revision = (await RuntimeSourceCheckout.GitAsync(source, ["rev-parse", "HEAD"])).Trim();
        var remote = (await RuntimeSourceCheckout.GitAsync(source, ["remote", "get-url", "origin"])).Trim();
        if (revision != commit || (remote.TrimEnd('/') != repository && remote.TrimEnd('/') != repository + ".git"))
            throw new InvalidDataException("Existing libc-test checkout differs from the pinned repository/commit; it was not changed.");
        await RuntimeSourceCheckout.RequireCleanAsync(source);
        return source;
    }

    /// <summary>
    /// Requires the checkout that <c>setup</c> makes.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <returns>Source tree path.</returns>
    public static async Task<string> RequireSourcesAsync(string root)
    {
        var (_, commit) = await ReadPinAsync(root);
        var source = SourceDirectory(root, commit);
        if (!Directory.Exists(Path.Combine(source, "src", "common")))
            throw new InvalidOperationException($"Pinned libc-test {commit[..12]} is missing. Run: dotnet run --project tools/WitOS.Dev -- setup");
        return source;
    }

    /// <summary>
    /// Builds the programs of the libc-test scenario (plan step S7.1) for the system layer's root task: every selected
    /// test compiled unchanged with musl's options and linked twice, as a static program and as a dynamic one against
    /// libc.so, as libc-test's Makefile builds it; the shared libraries of libc-test's tests beside them; and the runner
    /// as /bin/init, which starts every run in a process of its own.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="output">Artifact directory of the scenario.</param>
    /// <param name="architecture">Target architecture.</param>
    /// <returns>Package paths and the files to place there.</returns>
    public static async Task<IReadOnlyList<(string Name, string Source)>> BuildProgramsAsync(string root, string output,
        KernelArchitecture architecture)
    {
        var source = await RequireSourcesAsync(root);
        var selection = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, SELECTION))).RootElement;
        string[] Strings(string name) =>
            selection.TryGetProperty(name, out var value) ? value.EnumerateArray().Select(entry => entry.GetString()!).ToArray() : [];
        var support = Strings("support");
        var tests = Strings("tests");
        // libc-test builds the math suite dynamically alone (S7.2), like the tests that open a library; a math test
        // mathExcluded names for this ISA stays out, with its reason in the S7.2 document.
        var isa = architecture.Triple.Split('-')[0];
        var math = Strings("math");
        var mathExcluded = selection.TryGetProperty("mathExcluded", out var excluded)
            ? excluded.EnumerateObject().ToDictionary(entry => entry.Name, entry => entry.Value.EnumerateArray().Select(item => item.GetString()!).ToArray())
            : new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (mathExcluded.Any(entry => !math.Contains(entry.Key) || entry.Value.Length == 0 ||
                entry.Value.Any(name => name is not ("x86_64" or "aarch64"))))
            throw new InvalidDataException("Invalid libc-test math exclusions.");
        var dynamicOnly = Strings("dynamicOnly")
            .Concat(math.Where(test => !mathExcluded.TryGetValue(test, out var isas) || !isas.Contains(isa))).ToArray();
        var libraries = Strings("libraries");
        var exportDynamic = Strings("exportDynamic");
        var linkLibraries = selection.TryGetProperty("linkLibraries", out var links)
            ? links.EnumerateObject().ToDictionary(entry => entry.Name, entry => entry.Value.EnumerateArray().Select(item => item.GetString()!).ToArray())
            : new Dictionary<string, string[]>(StringComparer.Ordinal);
        string[] all = [.. tests, .. dynamicOnly, .. libraries];
        if (tests.Length == 0 || all.Distinct(StringComparer.Ordinal).Count() != all.Length ||
            all.Any(test => test.Contains("..", StringComparison.Ordinal) || test.Contains('\\')) ||
            linkLibraries.Values.SelectMany(list => list).Any(library => !libraries.Contains(library)))
            throw new InvalidDataException("Invalid libc-test selection.");
        var directory = Path.Combine(output, "libc-test");
        Directory.CreateDirectory(directory);
        // libc-test's config.mak.def in clang's terms; warnings are upstream's business.
        string[] options =
        [
            "-std=c99", "-D_POSIX_C_SOURCE=200809L", "-D_FILE_OFFSET_BITS=64", "-fno-builtin", "-frounding-math", "-w",
            "-I", Path.Combine(source, "src", "common")
        ];
        string Object(string name) => Path.Combine(directory, Regex.Replace(name, "[^A-Za-z0-9]", "_"));
        var supportObjects = new List<string>();
        foreach (var file in support)
        {
            var obj = Object(file) + ".o";
            await MuslLibc.CompileAsync(root, architecture, Path.Combine(source, file), obj, options);
            supportObjects.Add(obj);
        }
        // The dynamic runs' interpreter, musl's libc.so (S5.3).
        var package = new List<(string Name, string Source)>
        {
            (MuslLibc.InterpreterPath(architecture).TrimStart('/'), (await MuslLibc.BuildSharedAsync(root, architecture)).Library)
        };
        // The tests' shared libraries, and their objects for the static form of a test that links one.
        var sharedLibraries = new Dictionary<string, string>(StringComparer.Ordinal);
        var libraryObjects = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var library in libraries)
        {
            var pic = Object(library) + ".lo";
            await MuslLibc.CompileAsync(root, architecture, Path.Combine(source, "src", library + ".c"), pic, [.. options, "-fPIC"]);
            var obj = Object(library) + ".o";
            await MuslLibc.CompileAsync(root, architecture, Path.Combine(source, "src", library + ".c"), obj, options);
            var libraryDirectory = Path.Combine(directory, Path.GetDirectoryName(library)!);
            Directory.CreateDirectory(libraryDirectory);
            sharedLibraries[library] = await MuslLibc.LinkSharedLibraryAsync(root, architecture, libraryDirectory,
                Path.GetFileName(library) + ".so", [pic]);
            libraryObjects[library] = obj;
            package.Add(("libc-test/src/" + library + ".so", sharedLibraries[library]));
        }
        var runs = new StringBuilder("static const Run libc_runs[] = {\n");
        var count = 0;
        foreach (var test in tests.Concat(dynamicOnly))
        {
            var obj = Object(test) + ".o";
            await MuslLibc.CompileAsync(root, architecture, Path.Combine(source, "src", test + ".c"), obj, options);
            var linked = linkLibraries.GetValueOrDefault(test, []);
            var testDirectory = Path.Combine(directory, Path.GetDirectoryName(test)!);
            Directory.CreateDirectory(testDirectory);
            var name = Path.GetFileName(test);
            var dynamic = await MuslLibc.LinkDynamicProgramAsync(root, architecture, testDirectory, name, [obj, .. supportObjects],
                linked.Select(library => sharedLibraries[library]), exportDynamic.Contains(test) ? ["--export-dynamic"] : null);
            package.Add(($"libc-test/src/{test}.exe", dynamic));
            runs.Append($"    {{\"{test}\", \"dynamic\", \"/libc-test/src/{test}.exe\"}},\n");
            ++count;
            if (dynamicOnly.Contains(test))
                continue;
            var staticProgram = await MuslLibc.LinkStartedProgramAsync(root, architecture, testDirectory, name + "-static",
                [obj, .. supportObjects, .. linked.Select(library => libraryObjects[library])]);
            package.Add(($"libc-test/src/{test}-static.exe", staticProgram));
            runs.Append($"    {{\"{test}\", \"static\", \"/libc-test/src/{test}-static.exe\"}},\n");
            ++count;
        }
        runs.Append("};\n").Append($"#define LIBC_RUN_COUNT {count}\n");
        await File.WriteAllTextAsync(Path.Combine(directory, "libc_test_runs.h"), runs.ToString(), Encoding.ASCII);
        var runner = Path.Combine(directory, "runner.o");
        await MuslLibc.CompileAsync(root, architecture, Path.Combine(root, "tests", "User", "libc_test_runner.c"), runner,
            ["-std=c11", "-Wall", "-Wextra", "-Werror", "-I", directory]);
        package.Add(("bin/init", await MuslLibc.LinkStartedProgramAsync(root, architecture, directory, "libc_test_runner", [runner])));
        Console.WriteLine($"libc-test: {tests.Length + dynamicOnly.Length} tests, {count} runs for {architecture.Triple}.");
        return package;
    }

    #endregion
}
