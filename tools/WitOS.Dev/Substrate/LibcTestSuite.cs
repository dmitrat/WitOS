using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using WitOS.Dev.Images;
using WitOS.Dev.Kernel;
using WitOS.Dev.NativeAot;

namespace WitOS.Dev.Substrate;

/// <summary>
/// musl's libc-test on WitOS (plan step S1.3): the pinned revision (src/Substrate/libc-test.lock.json) checked out
/// once with Git under .tools/libc-test, the tests tests/User/libc-test.json selects compiled unchanged with musl's
/// options and their main renamed, and linked with the driver into one static program, the root task of the
/// libc-test scenario. Nothing of libc-test is patched.
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
    /// Builds the selected tests and the driver as the root task's flat image.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="output">Artifact directory of the scenario.</param>
    /// <param name="architecture">Target architecture.</param>
    /// <returns>Path of the flat image.</returns>
    public static async Task<string> BuildRootAsync(string root, string output, KernelArchitecture architecture)
    {
        var source = await RequireSourcesAsync(root);
        var selection = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, SELECTION))).RootElement;
        var support = selection.GetProperty("support").EnumerateArray().Select(entry => entry.GetString()!).ToArray();
        var tests = selection.GetProperty("tests").EnumerateArray().Select(entry => entry.GetString()!).ToArray();
        if (tests.Length == 0 || tests.Distinct(StringComparer.Ordinal).Count() != tests.Length ||
            tests.Any(test => test.Contains("..", StringComparison.Ordinal) || test.Contains('\\')))
            throw new InvalidDataException("Invalid libc-test selection.");
        var directory = Path.Combine(output, "libc-test");
        Directory.CreateDirectory(directory);
        // libc-test's config.mak.def in clang's terms; warnings are upstream's business.
        string[] options =
        [
            "-std=c99", "-D_POSIX_C_SOURCE=200809L", "-D_FILE_OFFSET_BITS=64", "-fno-builtin", "-frounding-math", "-w",
            "-I", Path.Combine(source, "src", "common")
        ];
        var objects = new List<string>();
        foreach (var file in support)
        {
            var obj = Path.Combine(directory, "support_" + Path.GetFileNameWithoutExtension(file) + ".o");
            await MuslLibc.CompileAsync(root, architecture, Path.Combine(source, file), obj, options);
            objects.Add(obj);
        }
        var table = new StringBuilder();
        var entries = new StringBuilder();
        foreach (var test in tests)
        {
            var identifier = "libc_test_" + Regex.Replace(test, "[^A-Za-z0-9]", "_");
            var obj = Path.Combine(directory, identifier + ".o");
            await MuslLibc.CompileAsync(root, architecture, Path.Combine(source, "src", test + ".c"), obj, [.. options, "-Dmain=" + identifier]);
            objects.Add(obj);
            table.Append($"int {identifier}();\n");
            entries.Append($"    {{\"{test}\", (int (*)(int, char **)){identifier}}},\n");
        }
        table.Append("static const Test libc_tests[] = {\n").Append(entries).Append("};\n");
        table.Append($"#define LIBC_TEST_COUNT {tests.Length}\n");
        await File.WriteAllTextAsync(Path.Combine(directory, "libc_test_table.h"), table.ToString(), Encoding.ASCII);
        var driver = Path.Combine(directory, "driver.o");
        await MuslLibc.CompileAsync(root, architecture, Path.Combine(root, "tests", "User", "libc_test_driver.c"), driver,
            ["-std=c11", "-Wall", "-Wextra", "-Werror", "-I", directory]);
        objects.Add(driver);
        var image = await MuslLibc.LinkAsync(root, architecture, output, "RootFixture", objects);
        Console.WriteLine($"libc-test: {tests.Length} tests for {architecture.Triple}.");
        return await FlatImage.FromElfAsync(output, architecture.ElfMachine, image, "RootFixture", "wit_user_root_image", "user_root_image.h");
    }

    #endregion
}
