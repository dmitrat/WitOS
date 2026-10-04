using System.Text.RegularExpressions;
using WitOS.Dev.Kernel;
using WitOS.Dev.Quality;
using WitOS.Dev.Tests.Support;

namespace WitOS.Dev.Tests.Repository;

/// <summary>
/// Repository consistency: format manifest coverage, version sources, the ABI reference and tool source paths.
/// </summary>
[TestFixture]
public sealed class ConsistencyTests
{
    #region Functions

    [Test]
    public async Task FormatManifestDescribesTreeTest()
    {
        var root = TestEnvironment.Root;
        var manifest = await SourceFormat.ReadManifestAsync(root);
        var native = SourceFormat.Expand(root, manifest.Native, [".c", ".h", ".cpp"], manifest.Exclude);
        var managed = SourceFormat.Expand(root, manifest.Managed, [".cs"], manifest.Exclude);
        Assert.That(native.All(file => !file.EndsWith(".asm", StringComparison.Ordinal)), Is.True, "Assembly entered the C formatter");
        Assert.That(managed.All(file => file.EndsWith(".cs", StringComparison.Ordinal)), Is.True, "Non-C# file entered the C# formatter");
        Assert.That(native.Concat(managed).All(file => !file.Contains("/obj/") && !file.Contains("/bin/")), Is.True, "Generated output");

        var missing = false;
        try
        {
            SourceFormat.Expand(root, ["src/DoesNotExist"], [".c"], []);
        }
        catch (InvalidDataException)
        {
            missing = true;
        }
        Assert.That(missing, Is.True, "A missing manifest entry was accepted");

        // Every source file in the repository must be under the formatting gate.
        var covered = native.Concat(managed).ToHashSet(StringComparer.Ordinal);
        string[] ignored = [".git", ".tools", "artifacts", "bin", "obj", ".vs", ".idea"];
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (relative.Split('/').Any(segment => ignored.Contains(segment, StringComparer.Ordinal)))
            {
                continue;
            }
            var extension = Path.GetExtension(relative);
            if (extension is ".c" or ".h" or ".cpp" or ".cs")
            {
                Assert.That(covered.Contains(relative) || manifest.Exclude.Any(prefix => relative.StartsWith(prefix, StringComparison.Ordinal)), Is.True, $"Source file is outside the formatting gate: {relative}");
            }
        }
    }

    [Test]
    public void VersionsHaveOneSourceTest()
    {
        var root = TestEnvironment.Root;
        var user = KernelAbi.UserVersion(root);
        var boot = KernelAbi.BootVersion(root);
        Assert.That(KernelAbi.Banner(root) == $"WitOS user ABI v{user}, boot ABI v{boot}", Is.True, "Unexpected banner format");
        var kernel = File.ReadAllText(Path.Combine(root, "src/Kernel/kernel.c"));
        Assert.That(!Regex.IsMatch(kernel, @"WitOS \d+\.\d+\.\d+"), Is.True, "kernel.c prints a hardcoded product version");
        Assert.That(kernel.Contains("wit_console_write_u64(WIT_ABI_VERSION);", StringComparison.Ordinal) &&
            kernel.Contains("wit_console_write_u64(WIT_BOOT_VERSION);", StringComparison.Ordinal), Is.True, "kernel.c banner does not print the header versions");

        // README describes only the current state: every ABI version it names is current.
        var readme = File.ReadAllText(Path.Combine(root, "README.md"));
        foreach (Match match in Regex.Matches(readme, @"\b(user|boot)?\s*ABI v(\d+)", RegexOptions.IgnoreCase))
        {
            var expected = match.Groups[1].Value.Equals("boot", StringComparison.OrdinalIgnoreCase) ? boot : user;
            Assert.That(int.Parse(match.Groups[2].Value) == expected, Is.True, $"README names a stale ABI version: {match.Value}");
        }
        Assert.That(readme.Contains(KernelAbi.Banner(root), StringComparison.Ordinal), Is.True, "README shows a stale kernel banner");

        // PLAN keeps history; its header must name the current versions.
        var header = string.Join("\n", File.ReadLines(Path.Combine(root, "PLAN.md")).Take(5));
        Assert.That(header.Contains($"user ABI v{user}", StringComparison.Ordinal), Is.True, "PLAN header lacks the current user ABI");
        Assert.That(header.Contains($"boot ABI v{boot}", StringComparison.Ordinal), Is.True, "PLAN header lacks the current boot ABI");
    }

    [Test]
    public void AbiReferenceCoversEveryCallTest()
    {
        var root = TestEnvironment.Root;
        var reference = File.ReadAllText(Path.Combine(root, "@Docs/Implementation/ABI-Reference.md"));
        var calls = KernelAbi.Calls(root);
        Assert.That(calls.Count > 0, Is.True, "No WIT_CALL_* definitions were found");
        foreach (var (name, number) in calls)
        {
            Assert.That(Regex.IsMatch(reference, $@"^\| {number} \| `{name}` \|", RegexOptions.Multiline), Is.True, $"ABI reference lacks a row for {name} = {number}");
        }
        var rows = Regex.Matches(reference, @"^\| \d+ \| `(WIT_CALL_[A-Z0-9_]+)` \|", RegexOptions.Multiline);
        Assert.That(rows.Count == calls.Count, Is.True, $"ABI reference has {rows.Count} call rows for {calls.Count} calls");
        var next = calls.Values.Max() + 1;
        Assert.That(reference.Contains($"Следующий свободный номер: **{next}**", StringComparison.Ordinal), Is.True, "ABI reference names a stale next free call number");
        Assert.That(reference.Contains($"**user ABI v{KernelAbi.UserVersion(root)}**", StringComparison.Ordinal) &&
            reference.Contains($"**boot ABI v{KernelAbi.BootVersion(root)}**", StringComparison.Ordinal), Is.True, "ABI reference names stale versions");
    }

    // Every call of the ABI has exactly one entry in the system call table, and the table bound follows the last call.
    [Test]
    public void CallTableCoversEveryCallTest()
    {
        var root = TestEnvironment.Root;
        var table = File.ReadAllText(Path.Combine(root, "src/Kernel/user_calls.c"));
        var calls = KernelAbi.Calls(root);
        foreach (var name in calls.Keys)
        {
            Assert.That(Regex.Matches(table, $@"^\s*\[{name}\] = \w+,", RegexOptions.Multiline).Count, Is.EqualTo(1),
                $"The call table has no single entry for {name}");
        }
        var last = calls.MaxBy(call => call.Value).Key;
        Assert.That(table.Contains($"#define CALL_COUNT ({last} + 1U)", StringComparison.Ordinal), Is.True,
            $"The call table bound does not follow the last call {last}");
    }

    // Repository paths written as literals in the tool and the tests (evidence inputs, overlay sources, native
    // harnesses) must exist; moving a file must not silently break an evidence list or a harness build.
    [Test]
    public void ToolSourcePathsExistTest()
    {
        var root = TestEnvironment.Root;
        var pattern = @"""((?:src/(?:Boot\.Uefi|Kernel[A-Za-z0-9.]*|Runtime\.[A-Za-z]+|System\.Native)|tools|tests|experiments)/" +
            @"[A-Za-z0-9_./-]+\.(?:cs|c|h|cpp|asm|cmake|json|csproj))""";
        var checkedPaths = 0;
        var sources = Directory.EnumerateFiles(Path.Combine(root, "tools"), "*.cs", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(Path.Combine(root, "tests", "WitOS.Dev.Tests"), "*.cs", SearchOption.AllDirectories));
        foreach (var file in sources)
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
                file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            {
                continue;
            }
            foreach (Match match in Regex.Matches(File.ReadAllText(file), pattern))
            {
                ++checkedPaths;
                Assert.That(File.Exists(Path.Combine(root, match.Groups[1].Value)), Is.True, $"{Path.GetFileName(file)} names a missing file: {match.Groups[1].Value}");
            }
        }
        Assert.That(checkedPaths > 0, Is.True, "No repository path literals were found in the tool or the tests");
    }

    #endregion
}
